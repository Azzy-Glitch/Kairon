"""
ASGI middleware for FastAPI/Starlette apps - the Python counterpart to
sdk/Kairon.SDK/KaironMiddleware.cs. Same rules the .NET version lives by: never block the
pipeline, never change the response, capture the host's exception but always re-raise it (a
Kairon outage must never look like it swallowed the application's own error), and treat a
Kairon outage as invisible to the host application.

Imported separately from kairon/__init__.py on purpose, so importing the base `kairon` package
never requires starlette/fastapi to be installed - only apps that actually use this middleware
need that dependency (install with `pip install kairon-sdk[fastapi]`).
"""

from __future__ import annotations

import time
from typing import Optional

try:
    from starlette.types import ASGIApp, Scope, Receive, Send
except ImportError as exc:  # pragma: no cover - exercised only when starlette is absent
    raise ImportError(
        "KaironMiddleware requires starlette/fastapi. Install with `pip install kairon-sdk[fastapi]`."
    ) from exc

from .client import Kairon, format_exception, get_default_instance, _utcnow_iso


class KaironMiddleware:
    """
    Usage:
        kairon = Kairon(endpoint=..., project_id=..., service="OrderProcessingService")
        kairon.start()
        app.add_middleware(KaironMiddleware)

    Or, to avoid relying on the process-wide default instance:
        app.add_middleware(KaironMiddleware, kairon=kairon)
    """

    def __init__(self, app: ASGIApp, kairon: Optional[Kairon] = None):
        self.app = app
        self._kairon = kairon

    async def __call__(self, scope: Scope, receive: Receive, send: Send):
        if scope["type"] != "http":
            return await self.app(scope, receive, send)
        try:
            kairon = self._kairon or get_default_instance()
            observe = kairon is not None and kairon.enabled and not kairon.is_ignored(scope.get("path", ""))
        except Exception:
            observe = False
        if not observe:
            return await self.app(scope, receive, send)

        start = time.monotonic()
        status_code = 500
        exception = None

        async def observed_send(message):
            nonlocal status_code
            if message["type"] == "http.response.start":
                status_code = message["status"]
            await send(message)  # Stream unchanged; do not buffer or consume the response.

        try:
            await self.app(scope, receive, observed_send)
        except Exception as exc:
            exception = exc
            raise  # The application's error handler/server still owns exception behavior.
        finally:
            self._report(kairon, scope, status_code, exception, start)

    def _report(self, kairon: Kairon, scope: Scope, status_code: int, exception, start: float) -> None:
        try:
            duration_ms = int((time.monotonic() - start) * 1000)
            is_error = exception is not None or status_code >= 500

            # Match the .NET SDK: every non-ignored request contributes to the periodic Metrics
            # sample, independently of success-event sampling below.
            kairon._record_request(duration_ms, is_error)

            # Errors are always reported; only successes are sampled - losing an error to
            # sampling would be the one loss that actually matters.
            if is_error or kairon.should_sample():
                kairon._enqueue_telemetry(
                    {
                        "ApplicationName": kairon.application,
                        "Environment": kairon.environment,
                        "Service": kairon.service,
                        "Endpoint": scope.get("path", ""),
                        "Method": scope.get("method", ""),
                        "StatusCode": status_code,
                        "Duration": duration_ms,
                        "Error": str(exception) if exception else None,
                        "ExceptionType": type(exception).__name__ if exception else None,
                        "StackTrace": format_exception(exception) if exception else None,
                        "Timestamp": _utcnow_iso(),
                    }
                )
        except Exception:
            # Telemetry reporting must never affect the host, even here.
            pass
