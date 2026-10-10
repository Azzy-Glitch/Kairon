"""Automatic signals - no application code: retries of failed outgoing HTTP calls, requests in
progress as queue depth, and per-app settings fetched from KAIRON (set in the desktop)."""

from __future__ import annotations

import json
import threading
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import pytest

import kairon.client as client_module
from kairon import Kairon
from kairon import _outgoing


class _Flaky(BaseHTTPRequestHandler):
    """A dependency that fails the first `failures` calls to /pay, then succeeds."""

    failures = 0

    def do_GET(self):
        if self.path.startswith("/pay") and type(self).failures > 0:
            type(self).failures -= 1
            status = 503
        else:
            status = 200
        self.send_response(status)
        self.send_header("Content-Length", "2")
        self.end_headers()
        self.wfile.write(b"ok")

    def log_message(self, *_):
        pass


@pytest.fixture
def dependency():
    server = ThreadingHTTPServer(("127.0.0.1", 0), _Flaky)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    yield f"http://127.0.0.1:{server.server_port}"
    server.shutdown()


@pytest.fixture
def collector(tmp_path):
    instance = Kairon(
        endpoint="http://127.0.0.1:8000", project_id="11111111-1111-1111-1111-111111111111",
        api_key="krn_test_key_123456", config_path=str(tmp_path / "credential.json"),
        normalized_telemetry=True, enable_metrics=False,
    )
    assert _outgoing.install()
    _outgoing.calls.enabled = True
    _outgoing.calls.window_seconds = 10.0
    _outgoing.calls.drain()
    yield instance
    _outgoing.calls.enabled = True


def _call_with_retries(url, attempts=5):
    """An ordinary hand-written retry loop, as any application might have."""
    for _ in range(attempts):
        try:
            with urllib.request.urlopen(url, timeout=5) as response:
                return response.status
        except urllib.error.HTTPError:
            continue
    return None


def test_a_retry_loop_against_a_failing_dependency_is_counted_automatically(collector, dependency):
    _Flaky.failures = 2

    assert _call_with_retries(dependency + "/pay?order=1") == 200

    retries, _ = collector._drain_app_metrics()
    assert retries == 2            # two repeats of a call that had just failed
    assert collector._drain_app_metrics()[0] == 0   # tracked: later samples say 0, not nothing


def test_successful_calls_and_different_endpoints_are_not_retries(collector, dependency):
    _Flaky.failures = 0
    for _ in range(3):
        assert _call_with_retries(dependency + "/pay") == 200
    _Flaky.failures = 1
    assert _call_with_retries(dependency + "/pay") == 200   # one failure, then one retry
    urllib.request.urlopen(dependency + "/other", timeout=5).read()

    assert collector._drain_app_metrics()[0] == 1


def test_kairons_own_network_calls_are_never_counted(collector, dependency):
    _Flaky.failures = 3
    for _ in range(3):
        try:
            client_module._open(urllib.request.Request(dependency + "/pay"), timeout=5)
        except urllib.error.HTTPError:
            pass

    assert collector._drain_app_metrics()[0] == 0


def test_settings_from_the_desktop_switch_signals_off_and_tune_the_window(collector, monkeypatch):
    seen = {}

    class _Response:
        status = 200

        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def read(self, limit):
            return json.dumps({"autoQueueDepth": False, "autoRetries": False, "retryWindowSeconds": 30}).encode()

    def opened(request, timeout):
        seen["url"] = request.full_url
        seen["key"] = request.get_header("X-kairon-api-key")
        seen["body"] = json.loads(request.data)
        return _Response()

    monkeypatch.setattr(client_module, "_open", opened)
    collector._refresh_settings(force=True)

    assert seen["url"].endswith("/api/v1/sdk/settings")
    assert seen["key"] == "krn_test_key_123456"
    assert seen["body"] == {"projectId": "11111111-1111-1111-1111-111111111111"}
    assert collector.auto_queue_depth is False and collector.auto_retries is False
    assert _outgoing.calls.window_seconds == 30.0
    assert collector._drain_app_metrics() == (None, None)


def test_an_unreachable_kairon_leaves_everything_on(collector, monkeypatch):
    def unreachable(request, timeout):
        raise urllib.error.URLError("down")

    monkeypatch.setattr(client_module, "_open", unreachable)
    collector._refresh_settings(force=True)

    assert collector.auto_queue_depth is True and collector.auto_retries is True


def test_requests_in_progress_are_tracked_by_the_framework_middleware(collector):
    from starlette.applications import Starlette
    from starlette.responses import PlainTextResponse
    from starlette.routing import Route
    from starlette.testclient import TestClient

    observed = {}

    async def checkout(request):
        observed["inflight"] = collector._inflight
        return PlainTextResponse("ok")

    from kairon.middleware import KaironMiddleware

    app = Starlette(routes=[Route("/checkout", checkout)])
    app.add_middleware(KaironMiddleware, kairon=collector)
    with TestClient(app) as client:
        assert client.get("/checkout").status_code == 200

    assert observed["inflight"] == 1
    assert collector._inflight == 0
    assert collector._drain_app_metrics()[1] == 1
