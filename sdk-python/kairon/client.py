"""
Kairon telemetry client - the collector-only Python counterpart to the .NET Kairon.SDK
(sdk/Kairon.SDK/). Deliberately dependency-light: the core client uses only the standard
library, so a Python application never has to install or depend on anything Kairon-specific
beyond this package (fastapi/starlette are only required if you use kairon.middleware).

The contract this class exists to keep, mirroring sdk/Kairon.SDK/KaironTelemetryQueue.cs and
KaironTelemetryClient.cs exactly: never block the caller, never raise into host code, and a
bounded queue drops the OLDEST item on overflow rather than blocking or growing unbounded.
There is deliberately no AI credential, no database setting and no remediation option here -
this is a telemetry collector and nothing more, same boundary KaironOptions documents on the
.NET side.
"""

from __future__ import annotations

import json
import logging
import queue
import random
import socket
import threading
import traceback
import urllib.error
import urllib.request
from datetime import datetime, timezone
from typing import Optional

_logger = logging.getLogger("kairon")
_logger.addHandler(logging.NullHandler())

DEFAULT_IGNORED_PATH_PREFIXES = ("/health", "/healthz", "/metrics", "/favicon.ico")

# Module-level default instance, set by the most recent start() call. Lets
# `app.add_middleware(KaironMiddleware)` work with no explicit instance, mirroring how the
# .NET SDK's app.UseKairon() resolves everything from DI without the caller passing an instance.
_default_instance: Optional["Kairon"] = None


def get_default_instance() -> Optional["Kairon"]:
    return _default_instance


def _utcnow_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def format_exception(exc: BaseException) -> Optional[str]:
    try:
        return "".join(traceback.format_exception(type(exc), exc, exc.__traceback__))
    except Exception:
        return None


class Kairon:
    def __init__(
        self,
        endpoint: str,
        project_id: str,
        service: Optional[str] = None,
        application: Optional[str] = None,
        environment: str = "Production",
        api_key: Optional[str] = None,
        enabled: bool = True,
        timeout_seconds: float = 5.0,
        queue_capacity: int = 1000,
        success_sample_rate: float = 1.0,
        ignored_path_prefixes: tuple = DEFAULT_IGNORED_PATH_PREFIXES,
    ) -> None:
        self.endpoint = endpoint.rstrip("/")
        self.project_id = project_id
        self.service = service
        self.application = application or service or "python-app"
        self.environment = environment
        self.api_key = api_key
        self.enabled = enabled
        # Kept short on purpose: a slow collector must not hold the caller's thread, and a
        # dropped telemetry item is always cheaper than a delayed one.
        self.timeout_seconds = max(1.0, timeout_seconds)
        # Errors are always sent; only successful requests are sampled.
        self.success_sample_rate = min(1.0, max(0.0, success_sample_rate))
        self.ignored_path_prefixes = tuple(ignored_path_prefixes)

        self._queue: "queue.Queue[tuple]" = queue.Queue(maxsize=max(1, queue_capacity))
        self._queue_lock = threading.Lock()
        self._dropped_count = 0
        self._thread: Optional[threading.Thread] = None
        self._stop_event = threading.Event()
        self._sampler = random.Random()

    # --- lifecycle -------------------------------------------------------------------

    def start(self) -> "Kairon":
        """Starts the background sender thread and registers this as the process-wide default
        instance for KaironMiddleware() called with no explicit instance."""
        if self._thread is None:
            self._stop_event.clear()
            self._thread = threading.Thread(target=self._run, name="kairon-sender", daemon=True)
            self._thread.start()

        global _default_instance
        _default_instance = self
        return self

    def stop(self) -> None:
        """Signals the sender thread to stop. Fail-open: does not flush the queue, matching the
        .NET SDK's own lack of a graceful-drain-on-shutdown guarantee."""
        self._stop_event.set()

    @property
    def dropped_count(self) -> int:
        return self._dropped_count

    @property
    def pending_count(self) -> int:
        return self._queue.qsize()

    # --- public recording API ---------------------------------------------------------

    def capture_exception(
        self,
        exc: BaseException,
        endpoint: str = "",
        method: str = "",
        status_code: int = 500,
        duration_ms: int = 0,
    ) -> None:
        """Manually records an exception as an incident. Framework middleware (see
        kairon.middleware) calls the same underlying enqueue automatically for HTTP apps; this is
        for background workers, scheduled jobs, or anywhere there is no request to instrument."""
        if not self.enabled:
            return
        self._enqueue_telemetry(
            {
                "ApplicationName": self.application,
                "Environment": self.environment,
                "Service": self.service,
                "Endpoint": endpoint,
                "Method": method,
                "StatusCode": status_code,
                "Duration": duration_ms,
                "Error": str(exc),
                "ExceptionType": type(exc).__name__,
                "StackTrace": format_exception(exc),
                "Timestamp": _utcnow_iso(),
            }
        )

    def record_metric(
        self,
        cpu_percent: Optional[float] = None,
        memory_percent: Optional[float] = None,
        response_time_ms: Optional[float] = None,
        request_count: int = 0,
        error_count: int = 0,
        retry_count: Optional[int] = None,
        queue_depth: Optional[int] = None,
        component: Optional[str] = None,
    ) -> None:
        if not self.enabled:
            return
        self._enqueue_metric(
            {
                "Timestamp": _utcnow_iso(),
                "CpuPercent": cpu_percent,
                "MemoryPercent": memory_percent,
                "ResponseTimeMs": response_time_ms,
                "RequestCount": request_count,
                "ErrorCount": error_count,
                "RetryCount": retry_count,
                "QueueDepth": queue_depth,
                "Environment": self.environment,
                "Application": self.application,
                "Service": self.service,
                "Component": component,
            }
        )

    def should_sample(self) -> bool:
        if self.success_sample_rate >= 1.0:
            return True
        if self.success_sample_rate <= 0:
            return False
        return self._sampler.random() < self.success_sample_rate

    def is_ignored(self, path: str) -> bool:
        return any(path.startswith(prefix) for prefix in self.ignored_path_prefixes)

    # --- internal enqueue (also used by kairon.middleware) -----------------------------

    def _enqueue_telemetry(self, payload: dict) -> bool:
        payload["ProjectId"] = self.project_id
        return self._enqueue(("api/telemetry/incidents", payload))

    def _enqueue_metric(self, payload: dict) -> bool:
        payload["ProjectId"] = self.project_id
        return self._enqueue(("api/telemetry/metrics", payload))

    def _enqueue(self, item: tuple) -> bool:
        try:
            with self._queue_lock:
                if self._queue.full():
                    try:
                        self._queue.get_nowait()
                        self._dropped_count += 1
                    except queue.Empty:
                        pass
                self._queue.put_nowait(item)
            return True
        except Exception:
            # Enqueueing must never raise into the caller - a full/broken queue is a dropped
            # telemetry item, not an application error.
            return False

    # --- background send loop -----------------------------------------------------------

    def _run(self) -> None:
        while not self._stop_event.is_set():
            try:
                path, payload = self._queue.get(timeout=0.5)
            except queue.Empty:
                continue

            try:
                self._send(path, payload)
            except Exception:
                # A single bad send must never kill the sender thread - the next item still
                # needs a chance to go out.
                _logger.debug("kairon: failed to send telemetry", exc_info=True)

    def _send(self, path: str, payload: dict) -> None:
        if not self.enabled:
            return

        url = f"{self.endpoint}/{path}"
        body = json.dumps(payload, default=str).encode("utf-8")

        request = urllib.request.Request(
            url, data=body, method="POST", headers={"Content-Type": "application/json"}
        )
        if self.api_key:
            request.add_header("X-Kairon-API-Key", self.api_key)

        try:
            urllib.request.urlopen(request, timeout=self.timeout_seconds)
        except (urllib.error.URLError, socket.timeout):
            # Backend unreachable, DNS failure, connection refused, or timed out - reported
            # nowhere but a debug log, exactly like the .NET client's caught-and-swallowed paths.
            pass
        except Exception:
            # Any other unexpected transport fault is still contained here.
            pass
