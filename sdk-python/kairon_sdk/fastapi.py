"""Optional FastAPI integration without coupling the core SDK to FastAPI."""

from __future__ import annotations

import time

from .client import KaironClient

try:
    from starlette.middleware.base import BaseHTTPMiddleware
except ImportError:  # pragma: no cover - only reached when the optional extra is absent
    BaseHTTPMiddleware = object  # type: ignore[assignment,misc]


class KaironMiddleware(BaseHTTPMiddleware):  # type: ignore[misc]
    def __init__(self, app, client: KaironClient):
        super().__init__(app)
        self.client = client

    async def dispatch(self, request, call_next):
        started = time.perf_counter()
        try:
            response = await call_next(request)
            self.client.capture(
                "http",
                severity="Error" if response.status_code >= 500 else "Information",
                http_context={
                    "endpoint": request.url.path,
                    "method": request.method,
                    "statusCode": response.status_code,
                    "durationMs": int((time.perf_counter() - started) * 1000),
                },
            )
            return response
        except Exception as exc:
            self.client.capture(
                "exception",
                severity="Error",
                message=str(exc),
                exception=exc,
                http_context={
                    "endpoint": request.url.path,
                    "method": request.method,
                    "statusCode": 500,
                    "durationMs": int((time.perf_counter() - started) * 1000),
                },
            )
            raise
