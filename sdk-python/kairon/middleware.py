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
    from starlette.middleware.base import BaseHTTPMiddleware
    from starlette.requests import Request
except ImportError as exc:  # pragma: no cover - exercised only when starlette is absent
    raise ImportError(
        "KaironMiddleware requires starlette/fastapi. Install with `pip install kairon-sdk[fastapi]`."
    ) from exc

from .client import Kairon, format_exception, get_default_instance, _utcnow_iso


class KaironMiddleware(BaseHTTPMiddleware):
    """
    Usage:
        kairon = Kairon(endpoint=..., project_id=..., service="OrderProcessingService")
        kairon.start()
        app.add_middleware(KaironMiddleware)

    Or, to avoid relying on the process-wide default instance:
        app.add_middleware(KaironMiddleware, kairon=kairon)
    """

    def __init__(self, app, kairon: Optional[Kairon] = None):
        super().__init__(app)
        self._kairon = kairon

    async def dispatch(self, request: Request, call_next):
        kairon = self._kairon or get_default_instance()

        if kairon is None or not kairon.enabled or kairon.is_ignored(request.url.path):
            return await call_next(request)

        start = time.monotonic()
        exception: Optional[BaseException] = None
        response = None

        try:
            response = await call_next(request)
        except Exception as exc:
            exception = exc
            raise
        finally:
            self._report(kairon, request, response, exception, start)

        return response

    def _report(self, kairon: Kairon, request: Request, response, exception, start: float) -> None:
        try:
            duration_ms = int((time.monotonic() - start) * 1000)
            status_code = 500 if exception is not None else response.status_code
            is_error = exception is not None or status_code >= 500

            # Errors are always reported; only successes are sampled - losing an error to
            # sampling would be the one loss that actually matters.
            if is_error or kairon.should_sample():
                kairon._enqueue_telemetry(
                    {
                        "ApplicationName": kairon.application,
                        "Environment": kairon.environment,
                        "Service": kairon.service,
                        "Endpoint": request.url.path,
                        "Method": request.method,
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
