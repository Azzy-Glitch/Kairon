"""Automatic retry counting for the application's outgoing HTTP calls - no application code.

A retry is a repeat of the same outgoing call (method, host, port and path) within a short window
after that call failed (a connection error, a timeout, HTTP 429 or a 5xx). That is what a retry loop
looks like from outside, whichever library or hand-written loop performs it.

Calls are observed at ``http.client`` - which ``urllib`` and ``requests``/``urllib3`` go through -
and at ``httpx``'s transports when ``httpx`` is installed. KAIRON's own network calls are never
counted. Observation can never raise into, delay or alter the application's call.
"""

from __future__ import annotations

import threading
import time
from collections import OrderedDict
from typing import Optional, Tuple
from urllib.parse import urlsplit

_MAX_TRACKED_CALLS = 256

# Set while the SDK itself is on the network (kairon.client._open), so its own delivery, pairing,
# settings and machine-proof calls - and their retries - never count as the application's.
_internal = threading.local()


class _OutgoingCalls:
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._failures: "OrderedDict[tuple, float]" = OrderedDict()
        self._retries = 0
        self.window_seconds = 10.0
        self.enabled = True
        self.installed = False

    def observe(self, host: Optional[str], port: Optional[int], method: str, url: str, failed: bool) -> None:
        if not self.enabled or getattr(_internal, "active", False) or not host:
            return
        key = (host.lower(), port, (method or "").upper(), urlsplit(url or "").path or "/")
        now = time.monotonic()
        with self._lock:
            last_failure = self._failures.get(key)
            if last_failure is not None and now - last_failure <= self.window_seconds:
                self._retries += 1
            if failed:
                self._failures[key] = now
                self._failures.move_to_end(key)
                while len(self._failures) > _MAX_TRACKED_CALLS:
                    self._failures.popitem(last=False)
            else:
                self._failures.pop(key, None)

    def drain(self) -> int:
        with self._lock:
            retries, self._retries = self._retries, 0
            return retries


calls = _OutgoingCalls()
_install_lock = threading.Lock()


def _failed_status(status) -> bool:
    try:
        status = int(status)
    except (TypeError, ValueError):
        return False
    return status == 429 or status >= 500


def _safe_observe(*args) -> None:
    try:
        calls.observe(*args)
    except Exception:
        pass  # Observation must never affect the application's call.


def install() -> bool:
    """Patches the HTTP client layers once per process. Idempotent; False if nothing could be patched."""
    with _install_lock:
        if calls.installed:
            return True
        patched = _install_http_client()
        patched = _install_httpx() or patched
        calls.installed = patched
        return patched


def _install_http_client() -> bool:
    try:
        import http.client as http_client
    except Exception:
        return False
    connection = http_client.HTTPConnection
    if getattr(connection, "_kairon_observed", False):
        return True
    original_putrequest = connection.putrequest
    original_endheaders = connection.endheaders
    original_getresponse = connection.getresponse

    def putrequest(self, method, url, *args, **kwargs):
        try:
            self._kairon_call = (method, url)
        except Exception:
            pass
        return original_putrequest(self, method, url, *args, **kwargs)

    def _call(self) -> Tuple[str, str]:
        return getattr(self, "_kairon_call", ("", ""))

    def endheaders(self, *args, **kwargs):
        try:
            return original_endheaders(self, *args, **kwargs)
        except OSError:
            method, url = _call(self)
            _safe_observe(getattr(self, "host", None), getattr(self, "port", None), method, url, True)
            raise

    def getresponse(self, *args, **kwargs):
        method, url = _call(self)
        try:
            response = original_getresponse(self, *args, **kwargs)
        except (OSError, http_client.HTTPException):
            _safe_observe(getattr(self, "host", None), getattr(self, "port", None), method, url, True)
            raise
        _safe_observe(getattr(self, "host", None), getattr(self, "port", None), method, url,
                      _failed_status(getattr(response, "status", 0)))
        return response

    connection.putrequest = putrequest
    connection.endheaders = endheaders
    connection.getresponse = getresponse
    connection._kairon_observed = True
    return True


def _install_httpx() -> bool:
    try:
        import httpx
    except Exception:
        return False

    def _key(request):
        url = request.url
        return url.host, url.port, request.method, str(url.raw_path.decode("ascii", "ignore") if hasattr(url, "raw_path") else url.path)

    transport = getattr(httpx, "HTTPTransport", None)
    if transport is not None and not getattr(transport, "_kairon_observed", False):
        original = transport.handle_request

        def handle_request(self, request):
            host, port, method, path = _key(request)
            try:
                response = original(self, request)
            except Exception:
                _safe_observe(host, port, method, path, True)
                raise
            _safe_observe(host, port, method, path, _failed_status(response.status_code))
            return response

        transport.handle_request = handle_request
        transport._kairon_observed = True

    async_transport = getattr(httpx, "AsyncHTTPTransport", None)
    if async_transport is not None and not getattr(async_transport, "_kairon_observed", False):
        original_async = async_transport.handle_async_request

        async def handle_async_request(self, request):
            host, port, method, path = _key(request)
            try:
                response = await original_async(self, request)
            except Exception:
                _safe_observe(host, port, method, path, True)
                raise
            _safe_observe(host, port, method, path, _failed_status(response.status_code))
            return response

        async_transport.handle_async_request = handle_async_request
        async_transport._kairon_observed = True
    return True
