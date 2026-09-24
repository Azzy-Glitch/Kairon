"""Django-native middleware for both Django's WSGI and ASGI handlers.

Add ``kairon.django.KaironMiddleware`` near the start of ``MIDDLEWARE`` and set
``KAIRON = {"pairing_code": os.getenv("KAIRON_PAIRING_CODE")}`` on first pairing.
On later runs the code can be omitted; the protected stored connection is reused.
"""

from __future__ import annotations

import atexit
import os
import time

try:
    from django.conf import settings
    from django.utils.deprecation import MiddlewareMixin
except ImportError as exc:  # pragma: no cover - exercised without the optional extra
    raise ImportError("Django integration requires `pip install kairon-sdk[django]`.") from exc

from .adapters import _finish
from .client import Kairon


class KaironMiddleware(MiddlewareMixin):
    """Use Django's supported middleware hooks rather than an ASGI-only wrapper."""

    def __init__(self, get_response):
        super().__init__(get_response)
        configuration = dict(getattr(settings, "KAIRON", {}) or {})
        timeout = configuration.pop("shutdown_timeout_seconds", 5.0)
        configuration.setdefault("normalized_telemetry", True)
        if not configuration.get("application") and not configuration.get("service"):
            settings_module = os.environ.get("DJANGO_SETTINGS_MODULE", "")
            configuration["application"] = settings_module.rsplit(".", 1)[0] or "django-app"
        self.kairon = Kairon(**configuration)
        self.kairon.start()
        self.shutdown_timeout_seconds = timeout
        atexit.register(_finish, self.kairon, timeout)

    def process_request(self, request):
        request._kairon_started = time.monotonic()
        request._kairon_exception = None

    def process_exception(self, request, exception):
        request._kairon_exception = exception
        return None

    def process_response(self, request, response):
        start = getattr(request, "_kairon_started", None)
        if start is None:
            return response

        reported = False

        def report(exception=None):
            nonlocal reported
            if reported:
                return
            reported = True
            self.kairon.record_http_request(
                request.method, request.path_info, response.status_code,
                int((time.monotonic() - start) * 1000),
                exception or getattr(request, "_kairon_exception", None),
                request.headers.get("X-Request-ID"),
            )

        if getattr(response, "streaming", False):
            content = response.streaming_content
            if response.is_async:
                async def observed_async():
                    try:
                        async for chunk in content:
                            yield chunk
                    except BaseException as exc:
                        report(exc)
                        raise
                    finally:
                        report()
                response.streaming_content = observed_async()
            else:
                def observed_sync():
                    try:
                        yield from content
                    except BaseException as exc:
                        report(exc)
                        raise
                    finally:
                        report()
                response.streaming_content = observed_sync()
        else:
            report()
        return response

    def close(self):
        _finish(self.kairon, self.shutdown_timeout_seconds)
        return self.kairon.last_shutdown_drained
