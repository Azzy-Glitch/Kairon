"""Small framework adapters around the single Kairon collector and HTTP recorder.

The raw ASGI/WSGI adapters intentionally depend on no web framework. WSGI has no portable
startup/shutdown protocol: delivery starts on first request, and callers can explicitly close
the wrapper (an atexit fallback also makes a bounded best-effort drain).
"""

from __future__ import annotations

import atexit
import asyncio
import inspect
import time


def _collector(cls, kwargs):
    return cls(**{key: value for key, value in kwargs.items() if key != "shutdown_timeout_seconds"})


def _finish(collector, timeout):
    if collector.last_shutdown_drained is None:
        collector.last_shutdown_drained = collector.stop(timeout)


def attach_application(cls, app, **kwargs):
    try:
        from starlette.applications import Starlette
    except ImportError:
        Starlette = ()
    if isinstance(app, Starlette):
        return cls._attach_starlette(app, **kwargs)

    try:
        from flask import Flask
    except ImportError:
        Flask = ()
    if isinstance(app, Flask):
        if "kairon" in app.extensions:
            raise RuntimeError("Kairon is already attached to this application.")
        if app._got_first_request:
            raise RuntimeError("Call Kairon.attach(app) before the application starts.")
        if kwargs.get("application") is None:
            kwargs["application"] = app.name
        collector = _collector(cls, kwargs)
        wrapped = WSGIAdapter(app.wsgi_app, collector, kwargs["shutdown_timeout_seconds"])
        app.wsgi_app = wrapped
        app.extensions["kairon"] = collector
        return collector

    raise TypeError(
        "Kairon.attach supports FastAPI/Starlette and Flask applications. "
        "For Django use kairon.django.KaironMiddleware; for another ASGI/WSGI "
        "application use Kairon.wrap_asgi(app) or Kairon.wrap_wsgi(app)."
    )


class ASGIAdapter:
    """ASGI 3 adapter that forwards all messages unchanged, including lifespan."""

    def __init__(self, app, collector, shutdown_timeout_seconds=5.0):
        if getattr(app, "_kairon_adapter", False):
            raise RuntimeError("Kairon is already attached to this application.")
        self.app = app
        self.kairon = collector
        self.shutdown_timeout_seconds = shutdown_timeout_seconds
        self._kairon_adapter = True
        atexit.register(_finish, collector, shutdown_timeout_seconds)

    async def __call__(self, scope, receive, send):
        kind = scope.get("type")
        if kind == "lifespan":
            started = False

            async def observed_lifespan_send(message):
                nonlocal started
                if message.get("type") == "lifespan.startup.complete":
                    self.kairon.start()
                    started = True
                await send(message)

            try:
                await self.app(scope, receive, observed_lifespan_send)
            finally:
                if started:
                    try:
                        self.kairon.last_shutdown_drained = await asyncio.to_thread(
                            self.kairon.stop, self.shutdown_timeout_seconds
                        )
                    except Exception:
                        self.kairon.last_shutdown_drained = False
            return
        if kind != "http":
            return await self.app(scope, receive, send)
        self.kairon.start()  # ASGI servers without lifespan still deliver telemetry.
        path = scope.get("path", "")
        if self.kairon.is_ignored(path) or not self.kairon.enabled:
            return await self.app(scope, receive, send)
        start = time.monotonic()
        status = 500
        exception = None

        async def observed_send(message):
            nonlocal status
            if message.get("type") == "http.response.start":
                status = message.get("status", 500)
            await send(message)

        try:
            await self.app(scope, receive, observed_send)
        except Exception as exc:
            exception = exc
            raise
        finally:
            self.kairon.record_http_request(
                scope.get("method", ""), path, status,
                int((time.monotonic() - start) * 1000), exception,
                next((value.decode("ascii", "ignore") for name, value in scope.get("headers", [])
                      if name.lower() == b"x-request-id"), None),
            )

    def close(self):
        _finish(self.kairon, self.shutdown_timeout_seconds)
        return self.kairon.last_shutdown_drained


class _ObservedIterable:
    def __init__(self, iterable, report):
        self.iterable = iterable
        self.report = report
        self.completed = False

    def __iter__(self):
        try:
            yield from self.iterable
        except BaseException as exc:
            self._complete(exc)
            raise
        finally:
            self.close()

    def _complete(self, exception=None):
        if not self.completed:
            self.completed = True
            self.report(exception)

    def close(self):
        try:
            close = getattr(self.iterable, "close", None)
            if close:
                close()
        finally:
            self._complete()


class WSGIAdapter:
    """WSGI adapter; response status and total streaming time are recorded on close."""

    def __init__(self, app, collector, shutdown_timeout_seconds=5.0):
        if getattr(app, "_kairon_adapter", False):
            raise RuntimeError("Kairon is already attached to this application.")
        self.app = app
        self.kairon = collector
        self.shutdown_timeout_seconds = shutdown_timeout_seconds
        self._kairon_adapter = True
        atexit.register(_finish, collector, shutdown_timeout_seconds)

    def __call__(self, environ, start_response):
        self.kairon.start()
        path = environ.get("PATH_INFO", "")
        if self.kairon.is_ignored(path) or not self.kairon.enabled:
            return self.app(environ, start_response)
        start = time.monotonic()
        status = 500

        def observed_start_response(status_line, headers, exc_info=None):
            nonlocal status
            try:
                status = int(status_line.split(" ", 1)[0])
            except (ValueError, TypeError):
                status = 500
            return start_response(status_line, headers, exc_info)

        def report(exception=None):
            self.kairon.record_http_request(
                environ.get("REQUEST_METHOD", ""), path, status,
                int((time.monotonic() - start) * 1000), exception,
                environ.get("HTTP_X_REQUEST_ID"),
            )

        try:
            iterable = self.app(environ, observed_start_response)
        except BaseException as exc:
            report(exc)
            raise
        return _ObservedIterable(iterable, report)

    def close(self):
        _finish(self.kairon, self.shutdown_timeout_seconds)
        return self.kairon.last_shutdown_drained


def wrap_asgi(cls, app, **kwargs):
    if not callable(app) or not (
        inspect.iscoroutinefunction(app) or
        inspect.iscoroutinefunction(getattr(app, "__call__", None))
    ):
        raise TypeError("Kairon.wrap_asgi requires an ASGI 3 async callable.")
    return ASGIAdapter(app, _collector(cls, kwargs), kwargs["shutdown_timeout_seconds"])


def wrap_wsgi(cls, app, **kwargs):
    if not callable(app) or inspect.iscoroutinefunction(app) or inspect.iscoroutinefunction(getattr(app, "__call__", None)):
        raise TypeError("Kairon.wrap_wsgi requires a synchronous WSGI callable.")
    return WSGIAdapter(app, _collector(cls, kwargs), kwargs["shutdown_timeout_seconds"])
