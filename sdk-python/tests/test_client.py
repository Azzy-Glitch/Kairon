"""
Tests for kairon.client.Kairon - mirrors the conventions in
tests/Kairon.SDK.Tests/TelemetryClientTests.cs and TelemetryQueueAndMiddlewareTests.cs: one test
per failure mode, each proving the SDK never raises into the caller. Uses a real local HTTP
server for the "genuine" success/error-status/malformed-body cases (no mocking needed there),
and unittest.mock only for failure modes a local server can't easily simulate deterministically
(connection refused, DNS failure, arbitrary transport exceptions).
"""

from __future__ import annotations

import json
import socket
import threading
import time
from http.server import BaseHTTPRequestHandler, HTTPServer
from unittest.mock import patch

import pytest

from kairon.client import Kairon


# --- a tiny local HTTP server, so success/error/malformed-body tests hit a real socket -------


class _RecordingHandler(BaseHTTPRequestHandler):
    received: list = []
    status_to_return = 200
    body_to_return = b'{"success": true, "message": "ok", "telemetryId": "abc"}'

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        raw = self.rfile.read(length)
        _RecordingHandler.received.append((self.path, self.headers, json.loads(raw)))
        self.send_response(_RecordingHandler.status_to_return)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(_RecordingHandler.body_to_return)

    def log_message(self, *args):
        pass  # keep test output quiet


@pytest.fixture()
def local_server():
    _RecordingHandler.received = []
    _RecordingHandler.status_to_return = 200
    _RecordingHandler.body_to_return = b'{"success": true, "message": "ok", "telemetryId": "abc"}'

    server = HTTPServer(("127.0.0.1", 0), _RecordingHandler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield f"http://127.0.0.1:{server.server_port}"
    finally:
        server.shutdown()
        thread.join(timeout=2)


def _client(endpoint: str, **overrides) -> Kairon:
    defaults = dict(endpoint=endpoint, project_id="proj-1", service="OrderProcessingService", timeout_seconds=2)
    defaults.update(overrides)
    return Kairon(**defaults)


# --- real-server tests -------------------------------------------------------------------------


def test_successful_send_reaches_the_server_with_project_id_stamped(local_server):
    client = _client(local_server)
    client._send("api/telemetry/incidents", {"ProjectId": "should-be-overwritten", "Endpoint": "/x"})

    assert len(_RecordingHandler.received) == 1
    path, headers, body = _RecordingHandler.received[0]
    assert path == "/api/telemetry/incidents"
    assert body["Endpoint"] == "/x"


def test_api_key_header_present_when_configured(local_server):
    client = _client(local_server, api_key="secret-key")
    client._send("api/telemetry/incidents", {"Endpoint": "/x"})

    _, headers, _ = _RecordingHandler.received[0]
    assert headers.get("X-Kairon-Api-Key") == "secret-key"


def test_api_key_header_absent_when_not_configured(local_server):
    client = _client(local_server, api_key=None)
    client._send("api/telemetry/incidents", {"Endpoint": "/x"})

    _, headers, _ = _RecordingHandler.received[0]
    assert headers.get("X-Kairon-Api-Key") is None


def test_server_error_status_does_not_raise(local_server):
    _RecordingHandler.status_to_return = 500
    client = _client(local_server)

    client._send("api/telemetry/incidents", {"Endpoint": "/x"})  # must not raise


def test_malformed_response_body_still_counts_as_delivered_not_thrown(local_server):
    _RecordingHandler.status_to_return = 200
    _RecordingHandler.body_to_return = b"not json at all"
    client = _client(local_server)

    client._send("api/telemetry/incidents", {"Endpoint": "/x"})  # must not raise


def test_metrics_posted_to_the_metrics_endpoint(local_server):
    client = _client(local_server)
    client._send("api/telemetry/metrics", {"CpuPercent": 42.0})

    path, _, body = _RecordingHandler.received[0]
    assert path == "/api/telemetry/metrics"
    assert body["CpuPercent"] == 42.0


# --- failure-mode tests: every one of these must not raise -------------------------------------


def test_backend_unreachable_is_reported_not_thrown():
    # Port 1 is a real closed port on loopback in virtually every sandboxed environment -
    # connection refused, fast and deterministic.
    client = _client("http://127.0.0.1:1", timeout_seconds=1)
    client._send("api/telemetry/incidents", {"Endpoint": "/x"})  # must not raise


def test_dns_failure_is_reported_not_thrown():
    client = _client("http://this-host-does-not-exist.invalid", timeout_seconds=1)
    client._send("api/telemetry/incidents", {"Endpoint": "/x"})  # must not raise


def test_timeout_is_reported_not_thrown():
    with patch("kairon.client.urllib.request.urlopen", side_effect=socket.timeout("timed out")):
        client = _client("http://127.0.0.1:9", timeout_seconds=1)
        client._send("api/telemetry/incidents", {"Endpoint": "/x"})  # must not raise


def test_unexpected_transport_fault_is_contained():
    with patch("kairon.client.urllib.request.urlopen", side_effect=RuntimeError("boom")):
        client = _client("http://127.0.0.1:9", timeout_seconds=1)
        client._send("api/telemetry/incidents", {"Endpoint": "/x"})  # must not raise


def test_disabled_client_skips_the_send_entirely():
    with patch("kairon.client.urllib.request.urlopen") as mock_urlopen:
        client = _client("http://127.0.0.1:9", enabled=False)
        client._send("api/telemetry/incidents", {"Endpoint": "/x"})

    mock_urlopen.assert_not_called()


# --- queue behavior ------------------------------------------------------------------------


def test_queue_drops_oldest_item_when_full():
    client = _client("http://127.0.0.1:1", queue_capacity=2)

    client._enqueue_telemetry({"Endpoint": "/1"})
    client._enqueue_telemetry({"Endpoint": "/2"})
    client._enqueue_telemetry({"Endpoint": "/3"})  # queue is full, should drop "/1"

    assert client.pending_count == 2
    assert client.dropped_count == 1

    remaining = []
    while not client._queue.empty():
        _, payload = client._queue.get_nowait()
        remaining.append(payload["Endpoint"])

    assert remaining == ["/2", "/3"]


def test_zero_capacity_is_coerced_to_at_least_one():
    client = _client("http://127.0.0.1:1", queue_capacity=0)
    assert client._enqueue_telemetry({"Endpoint": "/x"})


def test_project_id_is_always_stamped_on_enqueue():
    client = _client("http://127.0.0.1:1", project_id="the-real-project-id")
    client._enqueue_telemetry({"ProjectId": "wrong-id", "Endpoint": "/x"})

    _, payload = client._queue.get_nowait()
    assert payload["ProjectId"] == "the-real-project-id"


# --- capture_exception / record_metric shape --------------------------------------------------


def test_capture_exception_populates_expected_fields():
    client = _client("http://127.0.0.1:1")

    try:
        raise ValueError("bad order id")
    except ValueError as exc:
        client.capture_exception(exc, endpoint="/api/orders/process", method="POST", status_code=500, duration_ms=42)

    _, payload = client._queue.get_nowait()
    assert payload["Error"] == "bad order id"
    assert payload["ExceptionType"] == "ValueError"
    assert payload["StackTrace"] is not None
    assert payload["Endpoint"] == "/api/orders/process"
    assert payload["Method"] == "POST"
    assert payload["StatusCode"] == 500
    assert payload["Duration"] == 42
    assert payload["Service"] == "OrderProcessingService"


def test_record_metric_populates_expected_fields():
    client = _client("http://127.0.0.1:1")
    client.record_metric(cpu_percent=55.5, request_count=10, error_count=1, retry_count=3)

    _, payload = client._queue.get_nowait()
    assert payload["CpuPercent"] == 55.5
    assert payload["RequestCount"] == 10
    assert payload["ErrorCount"] == 1
    assert payload["RetryCount"] == 3
    assert payload["Service"] == "OrderProcessingService"


def test_request_metrics_are_aggregated_and_drained():
    client = _client("http://127.0.0.1:1")
    client._record_request(10, False)
    client._record_request(30, True)

    assert client._drain_request_metrics() == (2, 1, 20.0)
    assert client._drain_request_metrics() == (0, 0, None)


def test_start_automatically_posts_process_and_request_metrics(local_server):
    client = _client(local_server, metrics_interval_seconds=1)
    client.start()
    try:
        client._record_request(24, False)

        deadline = time.monotonic() + 3
        while time.monotonic() < deadline and not any(
            path == "/api/telemetry/metrics" for path, _, _ in _RecordingHandler.received
        ):
            time.sleep(0.05)

        metrics = [
            body for path, _, body in _RecordingHandler.received
            if path == "/api/telemetry/metrics"
        ]
        assert metrics
        assert metrics[0]["RequestCount"] == 1
        assert metrics[0]["ErrorCount"] == 0
        assert metrics[0]["ResponseTimeMs"] == 24.0
        assert metrics[0]["CpuPercent"] is not None
    finally:
        client.stop()


def test_timestamp_is_z_suffixed_not_numeric_offset():
    """The backend's Timestamp fields are DateTime, not DateTimeOffset. System.Text.Json's default
    DateTime converter round-trips a "Z"-suffixed UTC instant as-is (the same format .NET's own
    DateTime.UtcNow serializes to) but silently converts an explicit "+00:00" offset to the
    server's local time zone - a real bug this test caught: Python's datetime.isoformat() emits
    "+00:00", not "Z", so every Python-SDK timestamp landed hours off on a non-UTC server."""
    client = _client("http://127.0.0.1:1")

    try:
        raise ValueError("bad order id")
    except ValueError as exc:
        client.capture_exception(exc)

    _, payload = client._queue.get_nowait()
    assert payload["Timestamp"].endswith("Z")
    assert "+00:00" not in payload["Timestamp"]


def test_disabled_client_does_not_enqueue_on_capture_exception():
    client = _client("http://127.0.0.1:1", enabled=False)

    try:
        raise ValueError("x")
    except ValueError as exc:
        client.capture_exception(exc)

    assert client.pending_count == 0


# --- background sender thread ------------------------------------------------------------------


def test_start_drains_the_queue_via_the_background_thread(local_server):
    client = _client(local_server)
    client.start()
    try:
        client._enqueue_telemetry({"Endpoint": "/background-drain-test"})

        deadline = time.monotonic() + 3
        while time.monotonic() < deadline and not _RecordingHandler.received:
            time.sleep(0.05)

        assert any(body.get("Endpoint") == "/background-drain-test" for _, _, body in _RecordingHandler.received)
    finally:
        client.stop()


def test_a_single_bad_send_does_not_kill_the_sender_thread(local_server):
    client = _client(local_server)
    client.start()
    try:
        # First item targets a real send that will fail (server briefly returns 500), second
        # item must still be processed afterwards - proves one bad item doesn't stop the loop.
        _RecordingHandler.status_to_return = 500
        client._enqueue_telemetry({"Endpoint": "/will-error"})
        time.sleep(0.3)

        _RecordingHandler.status_to_return = 200
        client._enqueue_telemetry({"Endpoint": "/should-still-arrive"})

        deadline = time.monotonic() + 3
        while time.monotonic() < deadline and len(_RecordingHandler.received) < 2:
            time.sleep(0.05)

        assert any(body.get("Endpoint") == "/should-still-arrive" for _, _, body in _RecordingHandler.received)
    finally:
        client.stop()


def test_omitted_service_matches_application_in_both_payloads():
    client = Kairon("http://localhost", "project", application="worker", enable_metrics=False)
    client.capture_exception(ValueError("failure"))
    client.record_metric()
    assert client._queue.get_nowait()[1]["Service"] == "worker"
    assert client._queue.get_nowait()[1]["Service"] == "worker"


def test_authentication_failure_diagnostic_excludes_credentials(local_server):
    _RecordingHandler.status_to_return = 401
    client = _client(local_server, api_key="private-secret")
    assert not client._send("api/telemetry/incidents", {})
    assert client.last_delivery_error == "HTTP 401 (project authentication rejected)"
    assert "private-secret" not in client.last_delivery_error
