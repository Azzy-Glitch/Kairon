"""Actual normalized wire contract, bounded batching, and retry safety."""

from __future__ import annotations

import io
import json
import urllib.error

import pytest

import kairon.client as client_module
from kairon.client import RedirectNotAllowedError
from kairon import Kairon


class _Response:
    status = 200

    def __init__(self, body):
        self.body = body

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return None

    def read(self, limit):
        return self.body


def _collector(tmp_path):
    return Kairon(
        endpoint="http://127.0.0.1:8000",
        project_id="11111111-1111-1111-1111-111111111111",
        api_key="krn_test", config_path=str(tmp_path / "credential.json"),
        normalized_telemetry=True, enable_metrics=False,
    )


def test_two_observations_share_one_authenticated_batch(tmp_path, monkeypatch):
    calls = []

    def opened(request, timeout):
        calls.append(request)
        events = json.loads(request.data)["events"]
        return _Response(json.dumps({"accepted": len(events), "duplicates": 0, "rejected": 0}).encode())

    monkeypatch.setattr(client_module, "_open", opened)
    collector = _collector(tmp_path)
    collector.record_http_request("GET", "/orders", 200, 4)
    collector.record_http_request("POST", "/orders", 500, 6, ValueError("failed"))
    collector.start()
    assert collector.stop(3)
    assert collector.delivered_count == 2
    assert len(calls) == 1
    request = calls[0]
    assert request.full_url.endswith("/api/v1/telemetry/events")
    assert request.get_header("X-kairon-api-key") == "krn_test"
    events = json.loads(request.data)["events"]
    assert len({event["EventId"] for event in events}) == 2
    assert events[0]["HttpContext"]["Endpoint"] == "/orders"
    assert events[1]["ExceptionType"] == "ValueError"


def test_lost_response_retries_same_ids_and_accepts_duplicates(tmp_path, monkeypatch):
    calls = []

    def opened(request, timeout):
        calls.append(json.loads(request.data))
        if len(calls) == 1:
            raise urllib.error.URLError("connection reset")
        return _Response(b'{"accepted":0,"duplicates":1,"rejected":0}')

    monkeypatch.setattr(client_module, "_open", opened)
    collector = _collector(tmp_path)
    collector.record_http_request("GET", "/orders", 200, 4)
    collector.start()
    assert collector.stop(3)
    assert len(calls) == 2
    assert calls[0]["events"][0]["EventId"] == calls[1]["events"][0]["EventId"]


def test_authentication_failure_is_not_retried(tmp_path, monkeypatch):
    calls = []

    def opened(request, timeout):
        calls.append(request)
        raise urllib.error.HTTPError(request.full_url, 401, "Unauthorized", {}, io.BytesIO())

    monkeypatch.setattr(client_module, "_open", opened)
    collector = _collector(tmp_path)
    collector.record_http_request("GET", "/orders", 200, 4)
    collector.start()
    assert not collector.stop(3)
    assert len(calls) == 1
    assert collector.last_delivery_error == "HTTP 401"
    assert collector.failed_count == 1


def test_metric_uses_backend_resource_metrics_schema(tmp_path):
    collector = _collector(tmp_path)
    collector.record_metric(cpu_percent=47.5, response_time_ms=12.0, request_count=3)
    path, payload = collector._queue.get_nowait()
    event = collector._normalized_event(path, payload)
    assert event["EventType"] == "metric"
    assert event["ProjectId"] == collector.project_id
    assert event["ResourceMetrics"]["CpuPercent"] == 47.5
    assert event["ResourceMetrics"]["RequestCount"] == 3
    assert "HttpContext" not in event


def test_request_correlation_accepts_only_uuid_not_arbitrary_header_text(tmp_path):
    collector = _collector(tmp_path)
    request_id = "22222222-2222-2222-2222-222222222222"
    collector.record_http_request("GET", "/orders", 200, 4, request_id=request_id)
    path, payload = collector._queue.get_nowait()
    assert collector._normalized_event(path, payload)["RequestId"] == request_id
    collector.record_http_request("GET", "/orders", 200, 4, request_id="secret=do-not-emit")
    path, payload = collector._queue.get_nowait()
    assert collector._normalized_event(path, payload)["RequestId"] is None


def test_normalized_sender_refuses_redirect_without_retry(tmp_path, monkeypatch):
    calls = []

    def opened(request, timeout):
        calls.append(request)
        raise RedirectNotAllowedError(307, "https://untrusted.example/harvest")

    monkeypatch.setattr(client_module, "_open", opened)
    collector = _collector(tmp_path)
    collector.record_http_request("GET", "/orders", 200, 4)
    collector.start()
    assert not collector.stop(3)
    assert len(calls) == 1
    assert collector.last_delivery_error == "Endpoint rejected: HTTP 307 redirect not followed"
    assert "untrusted.example" not in collector.last_delivery_error


def test_normalized_sender_rejects_remote_plain_http_before_network(tmp_path, monkeypatch):
    collector = _collector(tmp_path)
    collector.endpoint = "http://remote.example"
    monkeypatch.setattr(client_module, "_open", lambda *_: pytest.fail("network should not be used"))
    collector.record_http_request("GET", "/orders", 200, 4)
    collector.start()
    assert not collector.stop(3)
    assert collector.last_delivery_error == "Endpoint rejected: insecure transport"


def test_partial_batch_result_accounts_for_each_event_idempotently(tmp_path, monkeypatch):
    calls = []

    def opened(request, timeout):
        events = json.loads(request.data)["events"]
        calls.append([event["EventId"] for event in events])
        if len(events) == 2:
            return _Response(b'{"accepted":1,"duplicates":0,"rejected":1}')
        if events[0]["HttpContext"]["Endpoint"] == "/accepted":
            return _Response(b'{"accepted":0,"duplicates":1,"rejected":0}')
        return _Response(b'{"accepted":0,"duplicates":0,"rejected":1}')

    monkeypatch.setattr(client_module, "_open", opened)
    collector = _collector(tmp_path)
    collector.record_http_request("GET", "/accepted", 200, 1)
    collector.record_http_request("GET", "/rejected", 200, 1)
    collector.start()
    assert not collector.stop(3)
    assert collector.delivered_count == 1
    assert collector.failed_count == 1
    assert len(calls) == 3
    assert calls[0] == [calls[1][0], calls[2][0]]
    assert collector.last_delivery_error == "Collector rejected telemetry batch"
