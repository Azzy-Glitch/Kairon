from __future__ import annotations

import time

from kairon_sdk import KaironClient, KaironOptions


def options(**changes):
    values = dict(project_id="550e8400-e29b-41d4-a716-446655440000", application="orders")
    values.update(changes)
    return KaironOptions(**values)


def test_capture_is_bounded_and_non_blocking():
    client = KaironClient(options(queue_capacity=1), sender=lambda *_: None)
    assert client.capture("log", message="one") is True
    assert client.capture("log", message="two") is False
    assert client.pending_events == 1
    assert client.dropped_events == 1


def test_flush_uses_normalized_contract_and_authentication_header():
    sent = []
    client = KaironClient(options(api_key="secret-key"), sender=lambda body, headers, timeout: sent.append((body, headers)))
    client.capture("exception", exception=ValueError("bad"), message="bad")
    client.flush()

    event = sent[0][0]["events"][0]
    assert event["projectId"] == client.options.project_id
    assert event["source"] == "python-sdk"
    assert event["exceptionType"] == "ValueError"
    assert sent[0][1] == {"X-KAIRON-API-Key": "secret-key"}


def test_transport_failure_is_contained():
    def fail(*_):
        raise OSError("backend unavailable")

    client = KaironClient(options(), sender=fail)
    client.capture("log", message="safe")
    client.flush()

    assert client.delivery_failures == 1
    assert client.pending_events == 0


def test_background_sender_drains_queue():
    sent = []
    client = KaironClient(options(flush_interval_seconds=0.05), sender=lambda body, *_: sent.append(body))
    client.start()
    client.capture("log", message="async")
    time.sleep(0.12)
    client.close()
    assert len(sent) == 1


def test_start_and_close_are_idempotent():
    client = KaironClient(options(), sender=lambda *_: None)
    client.start()
    client.start()
    client.close()
    client.close()
