"""
Tests for kairon.middleware.KaironMiddleware - mirrors the MiddlewareTests class in
tests/Kairon.SDK.Tests/TelemetryQueueAndMiddlewareTests.cs: successful requests are
instrumented, exceptions are captured but always re-raised (never swallowed), ignored paths
are skipped, errors bypass sampling, successes respect it, and a full queue never breaks the
request/response cycle.
"""

from __future__ import annotations

import pytest
from fastapi import FastAPI
from starlette.testclient import TestClient

from kairon.client import Kairon
from kairon.middleware import KaironMiddleware


def _build_app(kairon: Kairon) -> TestClient:
    app = FastAPI()
    app.add_middleware(KaironMiddleware, kairon=kairon)

    @app.get("/api/orders")
    def orders():
        return {"ok": True}

    @app.get("/api/boom")
    def boom():
        raise ValueError("order processing failed")

    @app.get("/health")
    def health():
        return {"ok": True}

    return TestClient(app, raise_server_exceptions=True)


def _kairon(**overrides) -> Kairon:
    defaults = dict(
        endpoint="http://127.0.0.1:1",  # never actually sent in these tests - queue only
        project_id="proj-1",
        service="OrderProcessingService",
    )
    defaults.update(overrides)
    return Kairon(**defaults)


def test_successful_request_is_instrumented():
    kairon = _kairon()
    client = _build_app(kairon)

    response = client.get("/api/orders")

    assert response.status_code == 200
    assert kairon.pending_count == 1
    _, payload = kairon._queue.get_nowait()
    assert payload["Endpoint"] == "/api/orders"
    assert payload["Method"] == "GET"
    assert payload["StatusCode"] == 200
    assert payload["Service"] == "OrderProcessingService"
    assert kairon._drain_request_metrics()[0] == 1


def test_exception_is_captured_and_still_reraised():
    kairon = _kairon()
    client = _build_app(kairon)

    with pytest.raises(ValueError, match="order processing failed"):
        client.get("/api/boom")

    # The middleware must have recorded it despite re-raising.
    assert kairon.pending_count == 1
    _, payload = kairon._queue.get_nowait()
    assert payload["ExceptionType"] == "ValueError"
    assert payload["Error"] == "order processing failed"
    assert payload["StatusCode"] == 500


def test_ignored_paths_are_never_instrumented():
    kairon = _kairon()
    client = _build_app(kairon)

    response = client.get("/health")

    assert response.status_code == 200
    assert kairon.pending_count == 0


def test_errors_are_always_reported_even_at_zero_sampling():
    kairon = _kairon(success_sample_rate=0.0)
    client = _build_app(kairon)

    with pytest.raises(ValueError):
        client.get("/api/boom")

    assert kairon.pending_count == 1


def test_successes_are_dropped_at_zero_sampling():
    kairon = _kairon(success_sample_rate=0.0)
    client = _build_app(kairon)

    response = client.get("/api/orders")

    assert response.status_code == 200
    assert kairon.pending_count == 0


def test_successes_are_always_reported_at_full_sampling():
    kairon = _kairon(success_sample_rate=1.0)
    client = _build_app(kairon)

    for _ in range(5):
        client.get("/api/orders")

    assert kairon.pending_count == 5


def test_a_full_queue_never_breaks_the_response():
    kairon = _kairon(queue_capacity=1)
    client = _build_app(kairon)

    for _ in range(10):
        response = client.get("/api/orders")
        assert response.status_code == 200  # every request still succeeds

    assert kairon.pending_count == 1  # bounded, oldest dropped
    assert kairon.dropped_count == 9


def test_disabled_kairon_skips_instrumentation_entirely():
    kairon = _kairon(enabled=False)
    client = _build_app(kairon)

    response = client.get("/api/orders")

    assert response.status_code == 200
    assert kairon.pending_count == 0


def test_no_default_instance_does_not_break_requests():
    # KaironMiddleware() with no explicit instance and no start()-registered default must still
    # let requests through untouched.
    app = FastAPI()
    app.add_middleware(KaironMiddleware)

    @app.get("/api/orders")
    def orders():
        return {"ok": True}

    client = TestClient(app)
    response = client.get("/api/orders")

    assert response.status_code == 200


def test_response_body_is_left_intact():
    kairon = _kairon()
    client = _build_app(kairon)

    response = client.get("/api/orders")

    assert response.json() == {"ok": True}


def test_streaming_duration_includes_body_and_preserves_contents():
    import asyncio
    from starlette.responses import StreamingResponse
    collector = _kairon()
    app = FastAPI()
    app.add_middleware(KaironMiddleware, kairon=collector)
    @app.get("/stream")
    async def stream():
        async def chunks():
            yield b"one"
            await asyncio.sleep(.1)
            yield b"two"
        return StreamingResponse(chunks())
    with TestClient(app) as client:
        assert client.get("/stream").content == b"onetwo"
    payload = collector._queue.get_nowait()[1]
    assert payload["Duration"] >= 90
    assert payload["StatusCode"] == 200


def test_late_stream_error_keeps_actual_status_and_records_exception():
    from starlette.responses import StreamingResponse
    collector = _kairon()
    app = FastAPI()
    app.add_middleware(KaironMiddleware, kairon=collector)
    @app.get("/stream")
    async def stream():
        async def chunks():
            yield b"one"
            raise ValueError("late stream failure")
        return StreamingResponse(chunks())
    with TestClient(app) as client:
        with pytest.raises(Exception):
            client.get("/stream")
    payload = collector._queue.get_nowait()[1]
    assert payload["StatusCode"] == 200  # Headers already sent; do not invent a 500.
    assert payload["ExceptionType"] and "late stream failure" in payload["StackTrace"]
