"""Minimal onboarding: pairing defaults, per-application credentials, restart safety, and
delivery hardening that keeps one bad value or an expired Agent proof from losing telemetry."""

from __future__ import annotations

import io
import json
import urllib.error
import uuid

import pytest

import kairon.client as client_module
from kairon import Kairon, _credential_store, _machine_proof

PROJECT = "11111111-1111-1111-1111-111111111111"


def _paired(environment=None, service=None):
    result = {"apiKey": "krn_paired", "projectId": PROJECT, "endpoint": "http://127.0.0.1:8000",
              "pairingId": str(uuid.uuid4())}
    if environment:
        result["environment"] = environment
    if service:
        result["service"] = service
    return result


@pytest.fixture
def pairing(monkeypatch):
    calls = []

    def fake_pair(endpoint, code, *args, **kwargs):
        calls.append(code)
        return fake_pair.result

    fake_pair.result = _paired("Development", "Orders API")
    monkeypatch.setattr(client_module, "pair", fake_pair)
    monkeypatch.setattr(client_module, "_confirm_pairing", lambda *a, **k: True)
    return fake_pair, calls


def test_pairing_code_alone_resolves_environment_and_service(pairing, tmp_path):
    collector = Kairon(pairing_code="pair_abc", config_path=str(tmp_path / "c.json"), enable_metrics=False)
    assert (collector.environment, collector.service, collector.project_id) == ("Development", "Orders API", PROJECT)


def test_explicit_values_still_win_over_pairing_defaults(pairing, tmp_path):
    collector = Kairon(pairing_code="pair_abc", config_path=str(tmp_path / "c.json"), enable_metrics=False,
                       environment="Staging", service="Billing")
    assert (collector.environment, collector.service) == ("Staging", "Billing")


def test_pairing_defaults_beat_inferred_framework_names(pairing, tmp_path):
    collector = Kairon(pairing_code="pair_abc", config_path=str(tmp_path / "c.json"), enable_metrics=False,
                       default_application="FastAPI")
    assert collector.service == "Orders API"


def test_restart_without_a_code_keeps_the_paired_defaults(pairing, tmp_path):
    path = str(tmp_path / "c.json")
    Kairon(pairing_code="pair_abc", config_path=path, enable_metrics=False)
    restarted = Kairon(config_path=path, enable_metrics=False)
    assert (restarted.environment, restarted.service) == ("Development", "Orders API")


def test_a_worker_that_loses_the_redeem_race_adopts_the_winners_credential(pairing, tmp_path, monkeypatch):
    fake_pair, calls = pairing
    path = tmp_path / "c.json"
    _credential_store.save_stored_config("http://127.0.0.1:8000", PROJECT, "krn_winner", path,
                                          pairing_code_hash=_credential_store.pairing_code_hash("pair_raced"))
    collector = Kairon(pairing_code="pair_raced", config_path=str(path), enable_metrics=False)
    assert collector.api_key == "krn_winner"
    assert calls == []  # matched the stored code fingerprint; never re-redeemed


def test_rate_limited_pairing_says_so(monkeypatch, tmp_path):
    def rate_limited(request, timeout):
        raise urllib.error.HTTPError(request.full_url, 429, "Too Many Requests", {}, io.BytesIO())

    monkeypatch.setattr(client_module, "_open", rate_limited)
    with pytest.raises(RuntimeError, match="rate limiting"):
        Kairon(pairing_code="pair_" + "x" * 20, config_path=str(tmp_path / "c.json"))
    assert not (tmp_path / "c.json").exists()


def test_each_application_gets_its_own_credential_file():
    a = _credential_store.credential_file_name(r"C:\apps\orders|Orders API")
    b = _credential_store.credential_file_name(r"C:\apps\billing|Billing")
    assert a != b
    assert a.startswith("credential-python-") and b.startswith("credential-python-")
    assert a == _credential_store.credential_file_name(r"C:\apps\orders|Orders API")
    assert _credential_store.credential_file_name(None) == "credential-python.json"


def test_legacy_shared_credential_is_migrated_once(tmp_path, monkeypatch):
    legacy = tmp_path / "legacy" / "credential.json"
    per_app = tmp_path / "per-app" / "credential-python-x.json"
    monkeypatch.setattr(_credential_store, "legacy_config_path", lambda: legacy)
    monkeypatch.setattr(_credential_store, "default_config_path", lambda *_a, **_k: per_app)
    _credential_store.save_stored_config("http://127.0.0.1:8000", PROJECT, "krn_legacy", legacy)

    collector = Kairon(enable_metrics=False)

    assert collector.api_key == "krn_legacy"
    assert _credential_store.load_stored_config(per_app)["apiKey"] == "krn_legacy"


def test_machine_id_is_ignored_and_no_longer_disables_normalized_delivery(tmp_path):
    collector = Kairon(endpoint="http://127.0.0.1:8000", project_id=PROJECT, api_key="krn_x",
                       config_path=str(tmp_path / "c.json"), normalized_telemetry=True, machine_id="web-01",
                       enable_metrics=False)
    assert collector.normalized_telemetry is True
    assert collector.machine_id is None


def test_out_of_range_values_are_clamped_instead_of_failing_the_batch(tmp_path):
    collector = Kairon(endpoint="http://127.0.0.1:8000", project_id=PROJECT, api_key="krn_x",
                       config_path=str(tmp_path / "c.json"), normalized_telemetry=True, enable_metrics=False)
    metric = collector._normalized_event("api/telemetry/metrics", {"_EventId": str(uuid.uuid4()), "CpuPercent": 150, "MemoryPercent": -3})
    http = collector._normalized_event("api/telemetry/incidents", {"_EventId": str(uuid.uuid4()), "StatusCode": 999, "Duration": 12.7})
    assert metric["ResourceMetrics"]["CpuPercent"] == 100.0 and metric["ResourceMetrics"]["MemoryPercent"] == 0.0
    assert http["HttpContext"]["StatusCode"] == 599 and http["HttpContext"]["DurationMs"] == 12


def test_a_refused_agent_proof_is_resent_once_without_the_proof(tmp_path, monkeypatch):
    sent = []

    class _Ok:
        status = 200
        def __enter__(self): return self
        def __exit__(self, *_): return None
        def read(self, _limit): return json.dumps({"accepted": 1, "duplicates": 0, "rejected": 0}).encode()

    def opened(request, timeout):
        sent.append(request.get_header("X-kairon-machine-proof"))
        if request.get_header("X-kairon-machine-proof"):
            raise urllib.error.HTTPError(request.full_url, 401, "Unauthorized", {}, io.BytesIO())
        return _Ok()

    monkeypatch.setattr(client_module, "_open", opened)
    monkeypatch.setattr(_machine_proof, "acquire", lambda *a, **k: str(uuid.uuid4()))
    collector = Kairon(endpoint="http://127.0.0.1:8000", project_id=PROJECT, api_key="krn_x",
                       config_path=str(tmp_path / "c.json"), normalized_telemetry=True, enable_metrics=False)
    collector.record_http_request("GET", "/orders", 200, 4)
    collector.start()
    assert collector.stop(3)
    assert collector.delivered_count == 1
    assert sent[0] is not None and sent[-1] is None and len(sent) == 2


def test_fastapi_without_lifespan_still_delivers(tmp_path, monkeypatch, pairing):
    from fastapi import FastAPI
    from starlette.testclient import TestClient

    delivered = []
    monkeypatch.setattr(Kairon, "_send_normalized", lambda self, items: delivered.extend(items) or True)
    app = FastAPI()

    @app.get("/")
    def home():
        return {"ok": True}

    collector = Kairon.attach(app, pairing_code="pair_abc", config_path=str(tmp_path / "c.json"), enable_metrics=False)
    TestClient(app).get("/")  # not used as a context manager: the lifespan never runs
    assert collector.stop(3)
    assert len(delivered) == 1


def test_flask_unhandled_exception_details_are_reported(tmp_path, monkeypatch, pairing):
    from flask import Flask

    sent = []
    monkeypatch.setattr(Kairon, "_send_normalized", lambda self, items: sent.extend(p for _, p in items) or True)
    app = Flask("orders")

    @app.get("/error")
    def error():
        raise RuntimeError("intentional failure")

    collector = Kairon.attach(app, pairing_code="pair_abc", config_path=str(tmp_path / "c.json"), enable_metrics=False)
    response = app.test_client().get("/error")
    assert response.status_code == 500
    response.close()  # a WSGI server closes the response; that is when the adapter reports
    assert collector.stop(3)
    assert sent and sent[0]["ExceptionType"] == "RuntimeError"
