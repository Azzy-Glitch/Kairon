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
import os
import queue
import random
import socket
import threading
import time
import traceback
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from typing import Optional
from uuid import UUID
from urllib.parse import urlsplit

from . import _credential_store

_logger = logging.getLogger("kairon")
_logger.addHandler(logging.NullHandler())

DEFAULT_IGNORED_PATH_PREFIXES = ("/health", "/healthz", "/metrics", "/favicon.ico")

# Matches the .NET SDK's KaironOptions.Endpoint default exactly - the same address the installed
# desktop backend actually binds to (desktop/Kairon.Desktop/MainForm.cs).
DEFAULT_ENDPOINT = "http://localhost:8000"

# Module-level default instance, set by the most recent start() call. Lets
# `app.add_middleware(KaironMiddleware)` work with no explicit instance, mirroring how the
# .NET SDK's app.UseKairon() resolves everything from DI without the caller passing an instance.
_default_instance: Optional["Kairon"] = None


def get_default_instance() -> Optional["Kairon"]:
    return _default_instance


def _utcnow_iso() -> str:
    # .isoformat() on a UTC-aware datetime renders the offset as "+00:00", not "Z". The backend's
    # DateTime fields (not DateTimeOffset) round-trip a "Z"-suffixed UTC instant as-is - the same
    # format the .NET SDK's DateTime.UtcNow serializes to - but System.Text.Json's default
    # DateTime converter treats an explicit "+00:00" offset as needing conversion to the server's
    # local time zone. On a server not itself running in UTC, "+00:00" silently lands as a
    # wrong-by-the-server's-UTC-offset timestamp instead of the instant actually reported.
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def format_exception(exc: BaseException) -> Optional[str]:
    try:
        return "".join(traceback.format_exception(type(exc), exc, exc.__traceback__))
    except Exception:
        return None


def pair(endpoint: str, code: str, version: str = "1.0.1", timeout_seconds: float = 10.0) -> Optional[dict]:
    """Redeems a one-time pairing code (minted by an operator in the Kairon UI) for a
    persistent project API key - the Python counterpart to Kairon.SDK's KaironPairingClient
    (docs/DESKTOP_SHELL.md). Returns a dict with "apiKey"/"projectId"/"endpoint"/"pairingId" on
    success, or None on any failure (rejected code, malformed response, unreachable backend) -
    contained here rather than raised, matching every other network path in this client.
    """
    try:
        parsed = urlsplit(endpoint)
        if parsed.scheme not in ("http", "https") or not parsed.netloc or parsed.username or parsed.password:
            return None
        url = endpoint.rstrip("/") + "/api/v1/sdk/pair"
        body = json.dumps({"code": code, "sdkType": "python", "version": version}).encode("utf-8")
        request = urllib.request.Request(url, data=body, method="POST", headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=max(1.0, timeout_seconds)) as response:
            if not 200 <= response.status < 300:
                return None
            raw = response.read(65537)
            if len(raw) > 65536:
                return None
            result = json.loads(raw)
            if not isinstance(result, dict) or not isinstance(result.get("apiKey"), str) or not result["apiKey"]:
                return None
            if not isinstance(result.get("projectId"), str) or UUID(result["projectId"]).int == 0:
                return None
            if not isinstance(result.get("pairingId"), str) or UUID(result["pairingId"]).int == 0:
                return None
            address = urlsplit(result.get("endpoint", ""))
            if address.scheme not in ("http", "https") or not address.netloc or address.username or address.password:
                return None
            return result
    except Exception:
        return None


def _resolve_configuration(
    endpoint: Optional[str],
    project_id: Optional[str],
    api_key: Optional[str],
    pairing_code: Optional[str],
    config_path: Optional[str],
) -> tuple:
    """Implements the required configuration precedence:

        1. explicit pairing_code - always wins, redeeming (or re-pairing) immediately, before
           anything else below is even consulted.
        2. a previously stored credential, if one exists - preferred as a whole over ordinary
           configuration. A stored credential represents a real, completed pairing event; treating
           it as the strongest available signal of intended identity (once no pairing_code is
           given) means a stray or inherited KAIRON_PROJECT_ID/KAIRON_API_KEY - or even an explicit
           project_id/api_key left over in code - can never silently override, or be silently
           mixed field-by-field with, an application's own already-paired identity. endpoint/
           project_id/api_key are always taken from the SAME source together: there is no
           per-field merge between "stored" and "explicit/env" anywhere in this function, which is
           what makes a mixed configuration (one project's id with another's key) structurally
           impossible rather than merely unlikely.
        3. ordinary explicit arguments, then KAIRON_ENDPOINT/KAIRON_PROJECT_ID/KAIRON_API_KEY
           environment variables - consulted only when neither of the above applies (first-time
           onboarding, or a fresh config_path with nothing stored yet).

    Raises ValueError/RuntimeError (no new exception hierarchy) rather than ever continuing with
    an incomplete credential.

    Pairing is always explicit, never automatic: nothing in this SDK ever supplies pairing_code
    on the caller's behalf (not on HTTP 401, not on startup with a still-valid credential) - it is
    consulted here only because the caller passed it in this exact call.
    """
    path = Path(config_path) if config_path else None

    if pairing_code:
        resolved_endpoint = endpoint or os.environ.get("KAIRON_ENDPOINT") or DEFAULT_ENDPOINT
        paired = pair(resolved_endpoint, pairing_code)
        if paired is None:
            raise RuntimeError(
                "Kairon pairing failed: the pairing code is invalid, expired, already used, or "
                "Kairon is unavailable. Generate a new pairing code from Kairon and try again."
            )
        # Persisted before being used - if this raises, __init__ never completes, so pairing is
        # never reported as successful without a durable credential. The freshly redeemed values
        # win outright, replacing whatever explicit args/env vars/stored file resolved above -
        # an explicit pairing_code is a direct instruction to (re)pair now, not a fallback.
        _credential_store.save_stored_config(paired["endpoint"], paired["projectId"], paired["apiKey"], path)
        _confirm_pairing(paired["endpoint"], paired["pairingId"], paired["apiKey"])
        return paired["endpoint"], paired["projectId"], paired["apiKey"]

    stored = _credential_store.load_stored_config(path)
    if stored and stored.get("projectId"):
        resolved_endpoint = endpoint or os.environ.get("KAIRON_ENDPOINT") or stored.get("endpoint") or DEFAULT_ENDPOINT
        return resolved_endpoint, stored["projectId"], stored.get("apiKey")

    # First-time onboarding: no pairing code, nothing stored yet - project_id has always been
    # required; api_key has always been optional (some deployments run with no authentication at
    # all), which is the existing, still-supported "unauthenticated" configuration.
    endpoint = endpoint or os.environ.get("KAIRON_ENDPOINT")
    project_id = project_id or os.environ.get("KAIRON_PROJECT_ID")
    api_key = api_key or os.environ.get("KAIRON_API_KEY")

    if not project_id:
        raise ValueError(
            "Kairon needs a project_id. Provide it directly, set KAIRON_PROJECT_ID, pass "
            "pairing_code from a Kairon-generated pairing code, or pair once so the stored "
            "configuration can be reused."
        )

    return endpoint or DEFAULT_ENDPOINT, project_id, api_key


def _confirm_pairing(endpoint: str, pairing_id: str, api_key: str, timeout_seconds: float = 10.0) -> None:
    """Proves to the backend that this SDK actually received and is about to persist the newly
    issued credential - the only trustworthy signal of that, distinct from the backend merely
    having ISSUED the credential (a redeem response can be lost in transit, or this process could
    crash between receiving it and writing it to disk). Best-effort and silent: a confirmation
    failure must never block onboarding - the credential is already saved and fully usable either
    way. It only means an operator-triggered re-pair completion (which revokes the credential being
    replaced) will keep waiting until a retry, or the next successful telemetry send, lets this
    catch up - never that the old credential gets revoked before this one is confirmed working.
    """
    try:
        url = endpoint.rstrip("/") + f"/api/v1/sdk/pair/{pairing_id}/confirm"
        body = json.dumps({"apiKey": api_key}).encode("utf-8")
        request = urllib.request.Request(url, data=body, method="POST", headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=max(1.0, timeout_seconds)):
            pass
    except Exception:
        pass


class Kairon:
    def __init__(
        self,
        endpoint: Optional[str] = None,
        project_id: Optional[str] = None,
        pairing_code: Optional[str] = None,
        service: Optional[str] = None,
        application: Optional[str] = None,
        environment: Optional[str] = None,
        api_key: Optional[str] = None,
        enabled: bool = True,
        timeout_seconds: float = 5.0,
        queue_capacity: int = 1000,
        success_sample_rate: float = 1.0,
        ignored_path_prefixes: tuple = DEFAULT_IGNORED_PATH_PREFIXES,
        enable_metrics: bool = True,
        metrics_interval_seconds: float = 5.0,
        machine_id: Optional[str] = None,
        config_path: Optional[str] = None,
    ) -> None:
        endpoint, project_id, api_key = _resolve_configuration(
            endpoint, project_id, api_key, pairing_code, config_path
        )
        self.endpoint = endpoint.rstrip("/")
        self.project_id = project_id
        self.machine_id = machine_id
        self.application = application or service or "python-app"
        self.service = service or self.application
        self.environment = environment or os.environ.get("KAIRON_ENVIRONMENT") or "Production"
        self.api_key = api_key
        self.enabled = enabled
        # Kept short on purpose: a slow collector must not hold the caller's thread, and a
        # dropped telemetry item is always cheaper than a delayed one.
        self.timeout_seconds = max(1.0, timeout_seconds)
        # Errors are always sent; only successful requests are sampled.
        self.success_sample_rate = min(1.0, max(0.0, success_sample_rate))
        self.ignored_path_prefixes = tuple(ignored_path_prefixes)
        self.enable_metrics = enable_metrics
        self.metrics_interval_seconds = max(1.0, float(metrics_interval_seconds))

        self._queue: "queue.Queue[tuple]" = queue.Queue(maxsize=max(1, queue_capacity))
        self._queue_lock = threading.Lock()
        self._dropped_count = 0
        self._delivered_count = 0
        self._failed_count = 0
        self.last_delivery_error: Optional[str] = None
        self._outstanding = 0
        self._delivery = threading.Condition()
        self._closed = False
        self._sender_stop = threading.Event()
        self._thread: Optional[threading.Thread] = None
        self._metrics_thread: Optional[threading.Thread] = None
        self._stop_event = threading.Event()
        self._sampler = random.Random()
        self._metrics_lock = threading.Lock()
        self._metric_requests = 0
        self._metric_errors = 0
        self._metric_duration_ms = 0

    # --- lifecycle -------------------------------------------------------------------

    def start(self) -> "Kairon":
        """Starts the background sender thread and registers this as the process-wide default
        instance for KaironMiddleware() called with no explicit instance."""
        if self._closed:
            return self  # A stopped collector is closed; create a new instance.
        if self._thread is None:
            self._stop_event.clear()
            self._thread = threading.Thread(target=self._run, name="kairon-sender", daemon=True)
            self._thread.start()
        if self.enabled and self.enable_metrics and self._metrics_thread is None:
            self._metrics_thread = threading.Thread(
                target=self._run_metrics, name="kairon-metrics", daemon=True
            )
            self._metrics_thread.start()

        global _default_instance
        _default_instance = self
        return self

    def flush(self, timeout_seconds: float = 5.0) -> bool:
        """Wait for queued AND in-flight sends. False on timeout or any lifetime loss.
        Call after producers stop for a final delivery result. Does not retry requests.
        """
        deadline = time.monotonic() + max(0.0, timeout_seconds)
        with self._delivery:
            while self._outstanding:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return False
                self._delivery.wait(remaining)
            return self._failed_count == 0 and self._dropped_count == 0

    def stop(self, timeout_seconds: float = 5.0) -> bool:
        """Stop producers and attempt a bounded drain; never guarantee delivery on timeout."""
        with self._queue_lock:
            self._closed = True
        self._stop_event.set()
        drained = self.flush(timeout_seconds)
        self._sender_stop.set()
        # Account for queued work that can no longer be delivered after the deadline.
        # A request already in flight finishes independently under its transport timeout.
        with self._queue_lock:
            while True:
                try:
                    self._queue.get_nowait()
                except queue.Empty:
                    break
                self._dropped_count += 1
                self._finish()
        global _default_instance
        if _default_instance is self:
            _default_instance = None
        return drained

    @property
    def delivered_count(self) -> int:
        return self._delivered_count

    @property
    def failed_count(self) -> int:
        return self._failed_count

    def _finish(self, delivered=None):
        with self._delivery:
            if delivered is True:
                self._delivered_count += 1
            elif delivered is False:
                self._failed_count += 1
            self._outstanding -= 1
            self._delivery.notify_all()

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

    def _record_request(self, duration_ms: int, is_error: bool) -> None:
        """Accumulates middleware request measurements for the automatic metrics sample."""
        if not self.enabled or not self.enable_metrics:
            return
        with self._metrics_lock:
            self._metric_requests += 1
            self._metric_duration_ms += max(0, int(duration_ms))
            if is_error:
                self._metric_errors += 1

    def _drain_request_metrics(self) -> tuple[int, int, Optional[float]]:
        with self._metrics_lock:
            requests = self._metric_requests
            errors = self._metric_errors
            duration = self._metric_duration_ms
            self._metric_requests = 0
            self._metric_errors = 0
            self._metric_duration_ms = 0
        latency = round(duration / requests, 1) if requests else None
        return requests, errors, latency

    # --- internal enqueue (also used by kairon.middleware) -----------------------------

    def _enqueue_telemetry(self, payload: dict) -> bool:
        payload["ProjectId"] = self.project_id
        payload["MachineId"] = self.machine_id
        return self._enqueue(("api/telemetry/incidents", payload))

    def _enqueue_metric(self, payload: dict) -> bool:
        payload["ProjectId"] = self.project_id
        payload["MachineId"] = self.machine_id
        return self._enqueue(("api/telemetry/metrics", payload))

    def _enqueue(self, item: tuple) -> bool:
        try:
            with self._queue_lock:
                if self._closed:
                    self._dropped_count += 1
                    return False
                if self._queue.full():
                    try:
                        self._queue.get_nowait()
                        self._dropped_count += 1
                        self._finish()
                    except queue.Empty:
                        pass
                with self._delivery:
                    self._outstanding += 1
                try:
                    self._queue.put_nowait(item)
                except Exception:
                    self._dropped_count += 1
                    self._finish()
                    return False
            return True
        except Exception:
            # Even an unexpected queue fault remains visible without affecting the host.
            self._dropped_count += 1
            return False

    # --- background send loop -----------------------------------------------------------

    def _run(self) -> None:
        while not self._sender_stop.is_set():
            try:
                path, payload = self._queue.get(timeout=0.5)
            except queue.Empty:
                continue

            if self._sender_stop.is_set():
                self._dropped_count += 1
                self._finish()
                continue
            delivered = False
            try:
                delivered = self._send(path, payload)
            except Exception:
                pass
            finally:
                self._finish(delivered is True)

    def _run_metrics(self) -> None:
        last_wall = time.perf_counter()
        last_cpu = time.process_time()

        while not self._stop_event.wait(self.metrics_interval_seconds):
            try:
                now_wall = time.perf_counter()
                now_cpu = time.process_time()
                elapsed = max(now_wall - last_wall, 0.001)
                cpu_percent = min(
                    100.0,
                    max(0.0, ((now_cpu - last_cpu) / elapsed) * 100 / (os.cpu_count() or 1)),
                )
                last_wall = now_wall
                last_cpu = now_cpu

                requests, errors, latency = self._drain_request_metrics()
                self.record_metric(
                    cpu_percent=round(cpu_percent, 1),
                    memory_percent=_process_memory_percent(),
                    response_time_ms=latency,
                    request_count=requests,
                    error_count=errors,
                    component=self.service,
                )
            except Exception:
                # Automatic metrics are best-effort and must never affect the host application.
                _logger.debug("kairon: failed to collect process metrics", exc_info=True)

    def _send(self, path: str, payload: dict) -> bool:
        if not self.enabled:
            return False
        try:
            request = urllib.request.Request(
                f"{self.endpoint}/{path}", data=json.dumps(payload, default=str).encode("utf-8"),
                method="POST", headers={"Content-Type": "application/json"})
            if self.api_key:
                request.add_header("X-Kairon-API-Key", self.api_key)
            with urllib.request.urlopen(request, timeout=self.timeout_seconds) as response:
                if not 200 <= response.status < 300:
                    return self._delivery_failure("HTTP " + str(response.status))
                try:
                    body = json.loads(response.read(65536))
                    if isinstance(body, dict) and body.get("success") is False:
                        return self._delivery_failure("Collector rejected telemetry")
                    self.last_delivery_error = None
                    return True
                except (ValueError, UnicodeError):
                    self.last_delivery_error = None
                    return True  # HTTP acceptance; informational body is not database proof.
        except urllib.error.HTTPError as exc:
            status = exc.code
            exc.close()
            return self._delivery_failure("HTTP " + str(status) + (" (project authentication rejected)" if status in (401, 403) else ""))
        except Exception:
            return self._delivery_failure("Transport or serialization failure")

    def _delivery_failure(self, message: str) -> bool:
        # Only fixed categories/status codes; never URL, payload, credential or exception text.
        self.last_delivery_error = message
        return False


def _process_memory_percent() -> Optional[float]:
    """Returns this process's resident-memory share using only the Python standard library."""
    try:
        if os.name == "nt":
            import ctypes

            class MemoryStatusEx(ctypes.Structure):
                _fields_ = [
                    ("length", ctypes.c_ulong),
                    ("memory_load", ctypes.c_ulong),
                    ("total_physical", ctypes.c_ulonglong),
                    ("available_physical", ctypes.c_ulonglong),
                    ("total_page_file", ctypes.c_ulonglong),
                    ("available_page_file", ctypes.c_ulonglong),
                    ("total_virtual", ctypes.c_ulonglong),
                    ("available_virtual", ctypes.c_ulonglong),
                    ("available_extended_virtual", ctypes.c_ulonglong),
                ]

            class ProcessMemoryCounters(ctypes.Structure):
                _fields_ = [
                    ("cb", ctypes.c_ulong),
                    ("page_fault_count", ctypes.c_ulong),
                    ("peak_working_set_size", ctypes.c_size_t),
                    ("working_set_size", ctypes.c_size_t),
                    ("quota_peak_paged_pool_usage", ctypes.c_size_t),
                    ("quota_paged_pool_usage", ctypes.c_size_t),
                    ("quota_peak_non_paged_pool_usage", ctypes.c_size_t),
                    ("quota_non_paged_pool_usage", ctypes.c_size_t),
                    ("pagefile_usage", ctypes.c_size_t),
                    ("peak_pagefile_usage", ctypes.c_size_t),
                ]

            kernel32 = ctypes.windll.kernel32
            psapi = ctypes.windll.psapi
            kernel32.GetCurrentProcess.restype = ctypes.c_void_p
            kernel32.GlobalMemoryStatusEx.argtypes = [ctypes.POINTER(MemoryStatusEx)]
            kernel32.GlobalMemoryStatusEx.restype = ctypes.c_int
            psapi.GetProcessMemoryInfo.argtypes = [
                ctypes.c_void_p,
                ctypes.POINTER(ProcessMemoryCounters),
                ctypes.c_ulong,
            ]
            psapi.GetProcessMemoryInfo.restype = ctypes.c_int

            system = MemoryStatusEx()
            system.length = ctypes.sizeof(system)
            process = ProcessMemoryCounters()
            process.cb = ctypes.sizeof(process)
            if not kernel32.GlobalMemoryStatusEx(ctypes.byref(system)):
                return None
            if not psapi.GetProcessMemoryInfo(
                kernel32.GetCurrentProcess(), ctypes.byref(process), process.cb
            ):
                return None
            return round((process.working_set_size / system.total_physical) * 100, 1)

        if os.path.exists("/proc/self/statm"):
            with open("/proc/self/statm", encoding="ascii") as statm:
                resident_pages = int(statm.read().split()[1])
            total_pages = int(os.sysconf("SC_PHYS_PAGES"))
            return round((resident_pages / total_pages) * 100, 1) if total_pages > 0 else None
    except (AttributeError, IndexError, OSError, TypeError, ValueError):
        pass
    return None
