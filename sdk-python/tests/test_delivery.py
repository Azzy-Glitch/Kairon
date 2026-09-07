import threading
from unittest.mock import patch

from kairon.client import Kairon


def client():
    return Kairon('http://localhost:8000', 'project', enable_metrics=False)


def test_flush_includes_inflight_and_stop_drains():
    sdk = client()
    entered, release = threading.Event(), threading.Event()
    def send(*args):
        entered.set()
        release.wait(2)
        return True
    with patch.object(sdk, '_send', side_effect=send):
        sdk._enqueue_telemetry({'Endpoint': '/one'})
        sdk.start()
        assert entered.wait(1)
        assert sdk.pending_count == 0
        assert not sdk.flush(0.01)
        release.set()
        assert sdk.stop(2)
    assert sdk.delivered_count == 1
    assert sdk.failed_count == 0


def test_failed_delivery_is_not_successful_flush():
    sdk = client()
    with patch.object(sdk, '_send', return_value=False):
        sdk._enqueue_telemetry({})
        sdk.start()
        assert not sdk.stop(2)
    assert sdk.failed_count == 1
    assert sdk.delivered_count == 0


def test_overflow_and_late_writes_remain_visible():
    sdk = Kairon('http://localhost', 'project', queue_capacity=1, enable_metrics=False)
    sdk._enqueue_telemetry({'Endpoint': '/old'})
    sdk._enqueue_telemetry({'Endpoint': '/new'})
    sent = []
    with patch.object(sdk, '_send', side_effect=lambda p, x: sent.append(x) or True):
        sdk.start()
        assert not sdk.stop(2)
    assert sent[0]['Endpoint'] == '/new'
    assert sdk.dropped_count == 1
    assert not sdk._enqueue_telemetry({})
    assert sdk.dropped_count == 2


def test_stop_is_bounded_without_sender():
    sdk = client()
    sdk._enqueue_telemetry({})
    assert not sdk.stop(0)
    assert sdk.pending_count == 0
    assert sdk.dropped_count == 1
