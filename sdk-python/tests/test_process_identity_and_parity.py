"""Process identity for KAIRON's application restart, CPU scope labelling, and delivery parity with
the .NET SDK (Retry-After honoured, oversized batches split rather than dropped)."""

from __future__ import annotations

import io
import json
import os
import sys
import urllib.error

import kairon.client as client_module
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


def _accept_all(calls):
    def opened(request, timeout):
        events = json.loads(request.data)["events"]
        calls.append(events)
        return _Response(json.dumps({"accepted": len(events), "duplicates": 0, "rejected": 0}).encode())
    return opened


def test_metric_events_identify_the_process_and_label_cpu_as_process_cpu(tmp_path, monkeypatch):
    calls = []
    monkeypatch.setattr(client_module, "_open", _accept_all(calls))
    collector = _collector(tmp_path)
    collector.record_metric(cpu_percent=42.0, request_count=3)
    collector.record_http_request("GET", "/orders", 200, 4)
    collector.start()
    assert collector.stop(3)

    events = [event for batch in calls for event in batch]
    metric = next(e for e in events if e["EventType"] == "metric")
    request = next(e for e in events if e["EventType"] == "http")
    assert metric["ProcessId"] == os.getpid()
    assert metric["Metadata"]["cpu.scope"] == "process"
    assert metric["Metadata"]["process.cwd"] == os.getcwd()
    assert metric["Metadata"]["process.executable"] == sys.executable
    assert "ProcessId" not in request and "Metadata" not in request


def test_cpu_is_scaled_by_the_cpus_this_process_may_use():
    count = client_module._usable_cpu_count()
    assert 1 <= count <= (os.cpu_count() or count)


def test_retry_after_is_parsed_and_capped():
    assert client_module._retry_after_seconds("2") == 2.0
    assert client_module._retry_after_seconds("3600") == 30.0
    assert client_module._retry_after_seconds(None) is None
    assert client_module._retry_after_seconds("Wed, 21 Oct 2026 07:28:00 GMT") is None
    assert client_module._retry_after_seconds("-1") is None


def test_a_rate_limited_batch_waits_as_asked_then_is_delivered(tmp_path, monkeypatch):
    attempts = []
    slept = []

    def opened(request, timeout):
        attempts.append(1)
        if len(attempts) == 1:
            raise urllib.error.HTTPError(request.full_url, 429, "Too Many Requests", {"Retry-After": "2"}, io.BytesIO())
        events = json.loads(request.data)["events"]
        return _Response(json.dumps({"accepted": len(events), "duplicates": 0, "rejected": 0}).encode())

    monkeypatch.setattr(client_module, "_open", opened)
    monkeypatch.setattr(client_module.time, "sleep", lambda seconds: slept.append(seconds))
    collector = _collector(tmp_path)
    collector.record_http_request("GET", "/orders", 200, 4)
    collector.start()
    assert collector.stop(3)

    assert len(attempts) == 2
    assert 2.0 in slept
    assert collector.delivered_count == 1


def test_an_oversized_batch_is_split_instead_of_dropped(tmp_path, monkeypatch):
    calls = []
    monkeypatch.setattr(client_module, "_open", _accept_all(calls))
    collector = _collector(tmp_path)
    big = "x" * 3000
    items = [("/api/telemetry/incidents", {
        "_EventId": f"00000000-0000-0000-0000-{i:012d}", "Endpoint": "/orders", "Method": "GET",
        "StatusCode": 500, "Duration": 1, "Error": big, "StackTrace": big * 2,
    }) for i in range(160)]

    result = collector._send_normalized_batch(items)

    assert result is True
    assert len(calls) >= 2
    assert sum(len(batch) for batch in calls) == len(items)
    assert all(len(json.dumps({"events": batch})) <= 1_048_576 for batch in calls)


def test_with_automatic_signals_switched_off_only_app_reported_values_are_sent(tmp_path):
    collector = _collector(tmp_path)
    collector.auto_queue_depth = False
    collector.auto_retries = False

    # Nothing reported: absent, not zero.
    assert collector._drain_app_metrics() == (None, None)

    collector.record_retries(3)
    collector.record_retries(2)
    collector.record_retries(0)        # ignored
    collector.record_retries("bad")    # ignored
    collector.report_queue_depth(17)
    assert collector._drain_app_metrics() == (5, 17)

    # Retries are a counter (reset per sample, then a meaningful 0); queue depth is a gauge (kept).
    assert collector._drain_app_metrics() == (0, 17)
    collector.report_queue_depth(-4)
    assert collector._drain_app_metrics() == (0, 0)


def test_queue_depth_is_the_peak_of_requests_in_progress_without_any_app_code(tmp_path):
    collector = _collector(tmp_path)
    collector.auto_retries = False

    for _ in range(3):
        collector._begin_request()
    collector._end_request()
    collector._end_request()                  # one still in progress
    assert collector._drain_app_metrics()[1] == 3   # the peak since the last sample
    assert collector._drain_app_metrics()[1] == 1   # then what is still in progress
    collector._end_request()
    collector._end_request()                  # never below zero
    assert collector._drain_app_metrics()[1] == 1   # it was still in progress during this sample
    assert collector._drain_app_metrics()[1] == 0


def test_the_metrics_loop_sends_one_sample_carrying_cpu_retries_and_queue_depth(tmp_path, monkeypatch):
    collector = _collector(tmp_path)
    sent = []
    monkeypatch.setattr(collector, "record_metric", lambda **kwargs: (sent.append(kwargs), collector._stop_event.set()))
    collector.metrics_interval_seconds = 0.01
    collector.record_retries(4)
    collector.report_queue_depth(9)

    collector._run_metrics()

    sample = sent[0]
    assert sample["retry_count"] == 4 and sample["queue_depth"] == 9
    assert sample["cpu_percent"] is not None
