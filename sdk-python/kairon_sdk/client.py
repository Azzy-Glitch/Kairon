"""Bounded, asynchronous and fail-open KAIRON telemetry transport."""

from __future__ import annotations

import platform
import queue
import threading
import time
import traceback
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any, Callable

import httpx


@dataclass(frozen=True)
class KaironOptions:
    endpoint: str = "http://127.0.0.1:8000"
    project_id: str = ""
    api_key: str | None = None
    application: str = "python-application"
    service: str | None = None
    environment: str = "Development"
    installation_id: str = field(default_factory=lambda: str(uuid.uuid4()))
    queue_capacity: int = 1000
    batch_size: int = 25
    timeout_seconds: float = 5.0
    flush_interval_seconds: float = 1.0


Sender = Callable[[dict[str, Any], dict[str, str], float], None]


class KaironClient:
    """Queues telemetry without blocking or raising into the monitored application."""

    def __init__(self, options: KaironOptions, sender: Sender | None = None):
        self.options = options
        self._queue: queue.Queue[dict[str, Any]] = queue.Queue(maxsize=max(1, options.queue_capacity))
        self._sender = sender or self._http_send
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self.dropped_events = 0
        self.delivery_failures = 0

    @staticmethod
    def pair(endpoint: str, code: str, version: str = "1.0.0") -> dict[str, Any] | None:
        """Redeems a one-time Python pairing code; failures remain contained."""
        try:
            response = httpx.post(endpoint.rstrip("/") + "/api/v1/sdk/pair",
                                  json={"code": code, "sdkType": "python", "version": version}, timeout=10.0)
            response.raise_for_status()
            return response.json()
        except Exception:
            return None

    @property
    def pending_events(self) -> int:
        return self._queue.qsize()

    def start(self) -> None:
        if self._thread and self._thread.is_alive():
            return
        self._stop.clear()
        self._thread = threading.Thread(target=self._run, name="kairon-telemetry", daemon=True)
        self._thread.start()

    def close(self, timeout: float = 2.0) -> None:
        self._stop.set()
        if self._thread:
            self._thread.join(max(0.0, timeout))

    def capture(self, event_type: str, *, severity: str = "Information", message: str | None = None,
                exception: BaseException | None = None, http_context: dict[str, Any] | None = None,
                metadata: dict[str, Any] | None = None, correlation_id: str | None = None) -> bool:
        try:
            event = {
                "eventId": str(uuid.uuid4()),
                "timestamp": datetime.now(timezone.utc).isoformat(),
                "eventType": event_type,
                "severity": severity,
                "source": "python-sdk",
                "projectId": self.options.project_id,
                "application": self.options.application,
                "service": self.options.service or self.options.application,
                "environment": self.options.environment,
                "runtime": f"Python {platform.python_version()}",
                "installationId": self.options.installation_id,
                "correlationId": correlation_id,
                "message": message,
                "exceptionType": type(exception).__name__ if exception else None,
                "stackTrace": "".join(traceback.format_exception(exception)) if exception else None,
                "httpContext": http_context,
                "metadata": metadata or {},
            }
            self._queue.put_nowait(event)
            return True
        except queue.Full:
            self.dropped_events += 1
            return False
        except Exception:  # collection must always fail open
            self.dropped_events += 1
            return False

    def flush(self) -> None:
        batch: list[dict[str, Any]] = []
        while len(batch) < max(1, self.options.batch_size):
            try:
                batch.append(self._queue.get_nowait())
            except queue.Empty:
                break
        if not batch:
            return
        try:
            headers = {"X-KAIRON-API-Key": self.options.api_key} if self.options.api_key else {}
            self._sender({"events": batch}, headers, max(0.1, self.options.timeout_seconds))
        except Exception:
            self.delivery_failures += len(batch)
        finally:
            for _ in batch:
                self._queue.task_done()

    def _run(self) -> None:
        while not self._stop.wait(max(0.05, self.options.flush_interval_seconds)):
            self.flush()
        self.flush()

    def _http_send(self, payload: dict[str, Any], headers: dict[str, str], timeout: float) -> None:
        with httpx.Client(timeout=timeout) as client:
            response = client.post(
                self.options.endpoint.rstrip("/") + "/api/v1/telemetry/events",
                json=payload,
                headers=headers,
            )
            response.raise_for_status()
