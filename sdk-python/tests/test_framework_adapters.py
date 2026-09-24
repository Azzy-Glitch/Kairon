"""Contract tests for the non-Starlette framework adapters."""

from __future__ import annotations

import asyncio
import threading
import time
from wsgiref.util import setup_testing_defaults

import pytest

from kairon import Kairon


def _options(tmp_path):
    return dict(
        endpoint="http://127.0.0.1:1", project_id="11111111-1111-1111-1111-111111111111",
        api_key="krn_test", config_path=str(tmp_path / "credential.json"),
        enable_metrics=False,
    )


def test_raw_wsgi_success_exception_and_bounded_close(tmp_path, monkeypatch):
    sent = []
    monkeypatch.setattr(Kairon, "_send_normalized", lambda self, items: sent.extend(items) or True)

    def app(environ, start_response):
        if environ["PATH_INFO"] == "/fail":
            raise ValueError("failure")
        start_response("200 OK", [("Content-Type", "text/plain")])
        return [b"ok"]

    wrapped = Kairon.wrap_wsgi(app, **_options(tmp_path))
    environ = {"REQUEST_METHOD": "GET", "PATH_INFO": "/orders",
               "HTTP_X_REQUEST_ID": "22222222-2222-2222-2222-222222222222"}
    setup_testing_defaults(environ)
    assert b"".join(wrapped(environ, lambda *_: None)) == b"ok"
    environ["PATH_INFO"] = "/fail"
    with pytest.raises(ValueError):
        list(wrapped(environ, lambda *_: None))
    assert wrapped.close()
    assert len(sent) == 2
    assert sent[0][1]["StatusCode"] == 200
    assert sent[0][1]["RequestId"] == "22222222-2222-2222-2222-222222222222"
    assert sent[1][1]["ExceptionType"] == "ValueError"
    assert not any(key in sent[0][1] for key in ("Headers", "Cookies", "RequestBody"))


def test_raw_wsgi_streaming_exception_preserves_started_status(tmp_path, monkeypatch):
    sent = []
    monkeypatch.setattr(Kairon, "_send_normalized", lambda self, items: sent.extend(payload for _, payload in items) or True)

    def app(environ, start_response):
        start_response("200 OK", [])
        def stream():
            yield b"first"
            raise ValueError("late")
        return stream()

    wrapped = Kairon.wrap_wsgi(app, **_options(tmp_path))
    with pytest.raises(ValueError):
        list(wrapped({"REQUEST_METHOD": "GET", "PATH_INFO": "/stream"}, lambda *_: None))
    assert wrapped.close()
    assert len(sent) == 1
    assert sent[0]["StatusCode"] == 200
    assert sent[0]["ExceptionType"] == "ValueError"


def test_flask_attach_uses_wsgi_and_rejects_duplicates(tmp_path, monkeypatch):
    flask = pytest.importorskip("flask")
    sent = []
    monkeypatch.setattr(Kairon, "_send_normalized", lambda self, items: sent.extend(payload for _, payload in items) or True)
    app = flask.Flask(__name__)
    collector = Kairon.attach(app, **_options(tmp_path))

    @app.get("/orders")
    def orders():
        return {"ok": True}

    @app.get("/error")
    def error():
        raise RuntimeError("failed")

    with pytest.raises(RuntimeError, match="already attached"):
        Kairon.attach(app, **_options(tmp_path))
    with app.test_client() as web:
        success = web.get("/orders")
        failure = web.get("/error")
        assert success.status_code == 200 and success.data
        assert failure.status_code == 500 and failure.data
        success.close()
        failure.close()
    assert app.wsgi_app.close()
    assert [item["StatusCode"] for item in sent] == [200, 500]
    assert collector.last_shutdown_drained is True


def test_raw_asgi_lifespan_http_and_shutdown(tmp_path, monkeypatch):
    starlette = pytest.importorskip("starlette.testclient")
    sent = []
    monkeypatch.setattr(Kairon, "_send_normalized", lambda self, items: sent.extend(payload for _, payload in items) or True)

    async def app(scope, receive, send):
        if scope["type"] == "lifespan":
            while True:
                message = await receive()
                if message["type"] == "lifespan.startup":
                    await send({"type": "lifespan.startup.complete"})
                elif message["type"] == "lifespan.shutdown":
                    await send({"type": "lifespan.shutdown.complete"})
                    return
        elif scope["type"] == "http":
            await receive()
            await send({"type": "http.response.start", "status": 201, "headers": []})
            await send({"type": "http.response.body", "body": b"ok"})

    wrapped = Kairon.wrap_asgi(app, **_options(tmp_path))
    with starlette.TestClient(wrapped) as web:
        assert web.get("/orders").status_code == 201
    assert wrapped.kairon.last_shutdown_drained is True
    assert len(sent) == 1 and sent[0]["StatusCode"] == 201


def test_raw_asgi_without_lifespan_still_starts_on_http(tmp_path, monkeypatch):
    sent = []
    monkeypatch.setattr(Kairon, "_send_normalized", lambda self, items: sent.extend(items) or True)

    async def app(scope, receive, send):
        if scope["type"] == "lifespan":
            raise RuntimeError("lifespan unsupported")
        await send({"type": "http.response.start", "status": 200, "headers": []})
        await send({"type": "http.response.body", "body": b"ok"})

    wrapped = Kairon.wrap_asgi(app, **_options(tmp_path))

    async def exercise():
        async def receive():
            return {"type": "http.request", "body": b"", "more_body": False}
        async def send(message):
            pass
        with pytest.raises(RuntimeError, match="lifespan unsupported"):
            await wrapped({"type": "lifespan"}, receive, send)
        assert not wrapped.kairon._closed
        await wrapped({"type": "http", "path": "/orders", "method": "GET"}, receive, send)

    asyncio.run(exercise())
    assert wrapped.close()
    assert len(sent) == 1 and sent[0][1]["Endpoint"] == "/orders"


def test_django_native_middleware_sync_request(tmp_path, monkeypatch):
    pytest.importorskip("django")
    from django.conf import settings
    from django.http import HttpResponse
    from django.test import override_settings
    from django.test import RequestFactory
    from kairon.django import KaironMiddleware

    if not settings.configured:
        settings.configure(SECRET_KEY="test-only", ALLOWED_HOSTS=["testserver"])
    sent = []
    monkeypatch.setattr(Kairon, "_send_normalized", lambda self, items: sent.extend(payload for _, payload in items) or True)
    with override_settings(KAIRON=_options(tmp_path)):
        middleware = KaironMiddleware(lambda request: HttpResponse("ok", status=202))
        response = middleware(RequestFactory().get("/orders?token=not-captured"))
        assert response.status_code == 202
        assert middleware.close()
    assert len(sent) == 1
    assert sent[0]["Endpoint"] == "/orders"
    assert sent[0]["StatusCode"] == 202


def test_django_native_middleware_async_request(tmp_path, monkeypatch):
    pytest.importorskip("django")
    from django.conf import settings
    from django.http import HttpResponse
    from django.test import RequestFactory, override_settings
    from kairon.django import KaironMiddleware

    if not settings.configured:
        settings.configure(SECRET_KEY="test-only", ALLOWED_HOSTS=["testserver"])
    sent = []
    monkeypatch.setattr(Kairon, "_send_normalized", lambda self, items: sent.extend(payload for _, payload in items) or True)

    async def get_response(request):
        return HttpResponse("ok", status=203)

    with override_settings(KAIRON=_options(tmp_path)):
        middleware = KaironMiddleware(get_response)
        response = asyncio.run(middleware(RequestFactory().get("/async")))
        assert response.status_code == 203
        assert middleware.close()
    assert len(sent) == 1
    assert sent[0]["Endpoint"] == "/async"


def test_django_streaming_response_records_after_body_and_late_error(tmp_path, monkeypatch):
    pytest.importorskip("django")
    from django.conf import settings
    from django.http import StreamingHttpResponse
    from django.test import RequestFactory, override_settings
    from kairon.django import KaironMiddleware

    if not settings.configured:
        settings.configure(SECRET_KEY="test-only", ALLOWED_HOSTS=["testserver"])
    sent = []
    monkeypatch.setattr(Kairon, "_send_normalized", lambda self, items: sent.extend(payload for _, payload in items) or True)

    def streaming(_request):
        def chunks():
            yield b"first"
            time.sleep(0.05)
            raise ValueError("late stream error")
        return StreamingHttpResponse(chunks(), status=200)

    with override_settings(KAIRON=_options(tmp_path)):
        middleware = KaironMiddleware(streaming)
        response = middleware(RequestFactory().get("/stream"))
        assert middleware.kairon.pending_count == 0
        with pytest.raises(ValueError):
            b"".join(response.streaming_content)
        assert middleware.close()
    assert len(sent) == 1
    assert sent[0]["Duration"] >= 45
    assert sent[0]["StatusCode"] == 200
    assert sent[0]["ExceptionType"] == "ValueError"


def test_starlette_attach_uses_existing_lifespan(tmp_path, monkeypatch):
    from starlette.applications import Starlette
    from starlette.responses import PlainTextResponse
    from starlette.routing import Route
    from starlette.testclient import TestClient

    sent = []
    monkeypatch.setattr(Kairon, "_send_normalized", lambda self, items: sent.extend(payload for _, payload in items) or True)
    app = Starlette(routes=[Route("/orders", lambda request: PlainTextResponse("ok"))])
    collector = Kairon.attach(app, **_options(tmp_path))
    with TestClient(app) as web:
        assert web.get("/orders").status_code == 200
    assert collector.last_shutdown_drained is True
    assert len(sent) == 1 and sent[0]["Endpoint"] == "/orders"


def test_wrong_protocol_and_unsupported_attach_fail_actionably(tmp_path):
    async def asgi(scope, receive, send):
        pass
    def wsgi(environ, start_response):
        return []
    with pytest.raises(TypeError, match="WSGI"):
        Kairon.wrap_wsgi(asgi, **_options(tmp_path))
    with pytest.raises(TypeError, match="ASGI"):
        Kairon.wrap_asgi(wsgi, **_options(tmp_path))
    with pytest.raises(TypeError, match="wrap_asgi"):
        Kairon.attach(object(), **_options(tmp_path))


def test_shared_recorder_scrubs_common_credentials_before_enqueue(tmp_path):
    collector = Kairon(**_options(tmp_path))
    collector.record_http_request(
        "GET", "/orders/krn_abcdefghijklmnop", 500, 4,
        ValueError("pairing_code=pair_abcdefghijklmnop"),
    )
    _, payload = collector._queue.get_nowait()
    assert "krn_abcdefghijklmnop" not in str(payload)
    assert "pair_abcdefghijklmnop" not in str(payload)
    assert "[redacted]" in str(payload)


def test_concurrent_first_requests_cannot_start_two_sender_threads(tmp_path):
    collector = Kairon(**_options(tmp_path))
    entered = threading.Event()
    release = threading.Event()
    count = []

    def sender():
        count.append(1)
        entered.set()
        release.wait(2)

    collector._run = sender
    callers = [threading.Thread(target=collector.start) for _ in range(16)]
    for caller in callers:
        caller.start()
    for caller in callers:
        caller.join(timeout=2)
    assert entered.wait(1)
    release.set()
    collector.stop(1)
    assert len(count) == 1
