"""Local API boundary for the packaged KAIRON AI service.

The AI process is a backend implementation detail, not a public API.  This middleware enforces a
per-launch shared secret, a bounded request body, and a fixed-window request budget before FastAPI
parses or executes a request.  The health endpoint remains credential-free so the desktop process
supervisor and container health checks can determine whether the child is alive.
"""

from __future__ import annotations

import secrets
import threading
import time
from collections import defaultdict, deque

from starlette.datastructures import Headers
from starlette.responses import JSONResponse
from starlette.types import ASGIApp, Message, Receive, Scope, Send


class FixedWindowRequestLimiter:
    """Small process-local limiter suitable for KAIRON's single packaged AI worker."""

    def __init__(self, permit_limit: int, window_seconds: float = 60.0) -> None:
        self.permit_limit = max(1, permit_limit)
        self.window_seconds = max(1.0, window_seconds)
        self._requests: dict[str, deque[float]] = defaultdict(deque)
        self._lock = threading.Lock()

    def allow(self, client: str, now: float | None = None) -> bool:
        timestamp = time.monotonic() if now is None else now
        cutoff = timestamp - self.window_seconds

        with self._lock:
            requests = self._requests[client]
            while requests and requests[0] <= cutoff:
                requests.popleft()

            if len(requests) >= self.permit_limit:
                return False

            requests.append(timestamp)
            return True


class AiApiSecurityMiddleware:
    header_name = "x-kairon-ai-key"

    def __init__(
        self,
        app: ASGIApp,
        *,
        api_key: str,
        max_request_bytes: int,
        requests_per_minute: int,
        requests_per_hour: int,
    ) -> None:
        self.app = app
        self.api_key = api_key
        self.max_request_bytes = max(1024, max_request_bytes)
        self.minute_limiter = FixedWindowRequestLimiter(requests_per_minute)
        self.hour_limiter = FixedWindowRequestLimiter(requests_per_hour, window_seconds=3600)

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] != "http" or scope.get("path") == "/health":
            await self.app(scope, receive, send)
            return

        headers = Headers(scope=scope)
        if not self.api_key:
            await self._error(503, "AI API authentication is not configured.", "AUTH_MISCONFIGURED",
                              scope, receive, send)
            return

        provided = headers.get(self.header_name, "")
        if not provided or not secrets.compare_digest(provided, self.api_key):
            await self._error(401, "A valid AI service key is required.", "AI_KEY_REQUIRED",
                              scope, receive, send)
            return

        client = scope.get("client")
        client_id = str(client[0]) if client else "local"
        if not self.minute_limiter.allow(client_id) or not self.hour_limiter.allow(client_id):
            response = JSONResponse(
                {"error": "AI request rate limit exceeded.", "code": "RATE_LIMITED"},
                status_code=429,
                headers={"Retry-After": "60"},
            )
            await response(scope, receive, send)
            return

        content_length = headers.get("content-length")
        if content_length:
            try:
                if int(content_length) > self.max_request_bytes:
                    await self._too_large(scope, receive, send)
                    return
            except ValueError:
                await self._error(400, "Invalid Content-Length header.", "INVALID_CONTENT_LENGTH",
                                  scope, receive, send)
                return

        # Buffer only mutating request bodies, with a hard cap that also covers chunked requests.
        if scope.get("method") in {"POST", "PUT", "PATCH"}:
            messages: list[Message] = []
            size = 0
            more = True
            while more:
                message = await receive()
                messages.append(message)
                if message["type"] == "http.request":
                    size += len(message.get("body", b""))
                    more = message.get("more_body", False)
                    if size > self.max_request_bytes:
                        await self._too_large(scope, receive, send)
                        return
                else:
                    more = False

            async def replay() -> Message:
                if messages:
                    return messages.pop(0)
                return {"type": "http.disconnect"}

            await self.app(scope, replay, send)
            return

        await self.app(scope, receive, send)

    @staticmethod
    async def _error(
        status: int,
        message: str,
        code: str,
        scope: Scope,
        receive: Receive,
        send: Send,
    ) -> None:
        await JSONResponse({"error": message, "code": code}, status_code=status)(scope, receive, send)

    async def _too_large(self, scope: Scope, receive: Receive, send: Send) -> None:
        await self._error(
            413,
            f"AI request exceeds the {self.max_request_bytes}-byte limit.",
            "REQUEST_TOO_LARGE",
            scope,
            receive,
            send,
        )
