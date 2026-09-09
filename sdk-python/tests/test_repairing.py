"""
Tests for explicit SDK re-pairing: `Kairon(pairing_code="...")` must always (re)pair immediately,
even when a stored (or explicit) credential already resolves a project - overriding it with the
freshly redeemed credential - while a 401 from telemetry must never trigger pairing on its own and
must never delete/modify the stored credential file. Mirrors the required-semantics acceptance
scenario: an existing app's credential is revoked, telemetry starts failing with 401, and only an
explicit pairing_code (never automatic behavior) recovers it.
"""

from __future__ import annotations

import json
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

import pytest

from kairon import Kairon


class _RepairHandler(BaseHTTPRequestHandler):
    """Serves both the pairing-redemption endpoint and a telemetry endpoint from one process, so
    a single fixture can exercise "revoked credential -> 401 -> explicit re-pair -> 2xx" without
    juggling two servers. Telemetry authorization is modeled by a single accepted api_key that
    changes once a pairing redemption happens - exactly like the real backend rejecting an old,
    revoked ProjectApiCredential and accepting only the freshly issued one.
    """

    accepted_api_key = "krn_old_key"
    pairing_calls = 0
    telemetry_calls = 0
    next_pairing_body = b'{"apiKey": "krn_new_key", "projectId": "77777777-7777-7777-7777-777777777777", "endpoint": "http://127.0.0.1:8000"}'
    next_pairing_status = 200

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        raw = self.rfile.read(length)
        if self.path == "/api/v1/sdk/pair":
            _RepairHandler.pairing_calls += 1
            self.send_response(_RepairHandler.next_pairing_status)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(_RepairHandler.next_pairing_body)
            if _RepairHandler.next_pairing_status == 200:
                _RepairHandler.accepted_api_key = json.loads(_RepairHandler.next_pairing_body)["apiKey"]
            return

        _RepairHandler.telemetry_calls += 1
        supplied = self.headers.get("X-Kairon-API-Key")
        json.loads(raw)  # a well-formed payload is expected either way
        if supplied != _RepairHandler.accepted_api_key:
            self.send_response(401)
            self.end_headers()
            return
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(b'{"success": true, "message": "ok", "telemetryId": "abc"}')

    def log_message(self, *args):
        pass


@pytest.fixture()
def repair_server():
    _RepairHandler.accepted_api_key = "krn_old_key"
    _RepairHandler.pairing_calls = 0
    _RepairHandler.telemetry_calls = 0
    _RepairHandler.next_pairing_status = 200
    server = HTTPServer(("127.0.0.1", 0), _RepairHandler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    address = f"http://127.0.0.1:{server.server_port}"
    # The redeemed "endpoint" must point back at THIS server (not a fixed placeholder), so a
    # subsequent telemetry send in the same test actually reaches the fake backend that issued
    # the credential, rather than whatever - if anything - is listening on a hardcoded port.
    _RepairHandler.next_pairing_body = json.dumps({
        "apiKey": "krn_new_key",
        "projectId": "77777777-7777-7777-7777-777777777777",
        "endpoint": address,
    }).encode("utf-8")
    try:
        yield address
    finally:
        server.shutdown()
        thread.join(timeout=2)


@pytest.fixture()
def config_path(tmp_path):
    return str(tmp_path / "credential.json")


@pytest.fixture(autouse=True)
def _clear_kairon_env(monkeypatch):
    for name in ("KAIRON_ENDPOINT", "KAIRON_PROJECT_ID", "KAIRON_API_KEY", "KAIRON_ENVIRONMENT"):
        monkeypatch.delenv(name, raising=False)


def _seed_stored_credential(config_path, endpoint, project_id, api_key):
    from kairon import _credential_store

    _credential_store.save_stored_config(endpoint, project_id, api_key, Path(config_path))


# --- Rule 1: explicit pairing code always wins ---------------------------------------------


def test_explicit_pairing_code_overrides_an_existing_stored_credential(repair_server, config_path):
    _seed_stored_credential(config_path, repair_server, "11111111-1111-1111-1111-111111111111", "krn_old_key")

    kairon = Kairon(pairing_code="pair_repair", endpoint=repair_server, config_path=config_path)
    try:
        assert kairon.project_id == "77777777-7777-7777-7777-777777777777"
        assert kairon.api_key == "krn_new_key"
        assert _RepairHandler.pairing_calls == 1

        # A second construction with no pairing_code must now reuse the FRESH credential, not the
        # stale one it replaced - proving the stored file was actually overwritten, not just the
        # in-memory instance.
        again = Kairon(endpoint="http://127.0.0.1:1", config_path=config_path)
        try:
            assert again.project_id == "77777777-7777-7777-7777-777777777777"
            assert again.api_key == "krn_new_key"
        finally:
            again.stop(timeout_seconds=1)
    finally:
        kairon.stop(timeout_seconds=1)


def test_explicit_pairing_code_overrides_explicit_project_id_and_api_key_arguments_too(repair_server, config_path):
    kairon = Kairon(
        pairing_code="pair_repair",
        endpoint=repair_server,
        project_id="11111111-1111-1111-1111-111111111111",
        api_key="krn_explicit_ignored",
        config_path=config_path,
    )
    try:
        assert kairon.project_id == "77777777-7777-7777-7777-777777777777"
        assert kairon.api_key == "krn_new_key"
    finally:
        kairon.stop(timeout_seconds=1)


# --- Rule 2/3: no automatic re-pairing, credential.json is never deleted on 401 ------------


def test_401_does_not_delete_or_modify_the_stored_credential_file(repair_server, config_path):
    _seed_stored_credential(config_path, repair_server, "11111111-1111-1111-1111-111111111111", "krn_stale_key")
    before = Path(config_path).read_bytes()

    kairon = Kairon(endpoint=repair_server, config_path=config_path)
    try:
        assert kairon.api_key == "krn_stale_key"  # not the server's currently-accepted key
        ok = kairon._send("api/telemetry/incidents", {"foo": "bar"})

        assert ok is False
        assert kairon.last_delivery_error == "HTTP 401 (project authentication rejected)"
        assert "krn_stale_key" not in kairon.last_delivery_error
        # Fail-open with respect to local state: the file is untouched, and pairing was never hit.
        assert Path(config_path).read_bytes() == before
        assert _RepairHandler.pairing_calls == 0
    finally:
        kairon.stop(timeout_seconds=1)


def test_no_pairing_code_and_a_401_never_silently_initiates_pairing(repair_server, config_path):
    _seed_stored_credential(config_path, repair_server, "11111111-1111-1111-1111-111111111111", "krn_stale_key")

    kairon = Kairon(endpoint=repair_server, config_path=config_path)
    try:
        for _ in range(3):
            assert kairon._send("api/telemetry/incidents", {}) is False
        assert _RepairHandler.pairing_calls == 0
        assert kairon.project_id == "11111111-1111-1111-1111-111111111111"  # unchanged
    finally:
        kairon.stop(timeout_seconds=1)


# --- Full acceptance scenario: revoked credential -> 401 -> explicit re-pair -> 2xx --------


def test_revoked_credential_then_explicit_pairing_recovers_telemetry(repair_server, config_path):
    _seed_stored_credential(config_path, repair_server, "11111111-1111-1111-1111-111111111111", "krn_old_key")
    _RepairHandler.accepted_api_key = "krn_new_key"  # simulate: backend already revoked the old credential

    stale = Kairon(endpoint=repair_server, config_path=config_path)
    try:
        assert stale._send("api/telemetry/incidents", {}) is False
        assert stale.last_delivery_error == "HTTP 401 (project authentication rejected)"
    finally:
        stale.stop(timeout_seconds=1)
    assert _RepairHandler.pairing_calls == 0  # no automatic re-pair happened

    repaired = Kairon(pairing_code="pair_recover", endpoint=repair_server, config_path=config_path)
    try:
        assert repaired.api_key == "krn_new_key"
        assert repaired._send("api/telemetry/incidents", {}) is True
        assert repaired.last_delivery_error is None
    finally:
        repaired.stop(timeout_seconds=1)


# --- Single-use / expired / cancelled codes fail cleanly (no retry loop) -------------------


def test_a_pairing_code_cannot_be_redeemed_twice(repair_server, config_path):
    first = Kairon(pairing_code="pair_onceonly", endpoint=repair_server, config_path=config_path)
    first.stop(timeout_seconds=1)
    assert _RepairHandler.pairing_calls == 1

    _RepairHandler.next_pairing_status = 400
    _RepairHandler.next_pairing_body = b'{"error": "Pairing code is invalid, expired, revoked, or already used."}'

    with pytest.raises(RuntimeError, match="pairing failed"):
        Kairon(pairing_code="pair_onceonly", endpoint=repair_server, config_path=config_path)
    assert _RepairHandler.pairing_calls == 2  # attempted once more, not retried in a loop


def test_expired_pairing_code_fails_cleanly(repair_server, config_path):
    _RepairHandler.next_pairing_status = 400
    _RepairHandler.next_pairing_body = b'{"error": "Pairing code is invalid, expired, revoked, or already used."}'

    with pytest.raises(RuntimeError, match="pairing failed"):
        Kairon(pairing_code="pair_expired", endpoint=repair_server, config_path=config_path)
    assert not Path(config_path).exists()


def test_cancelled_pairing_code_fails_cleanly(repair_server, config_path):
    _RepairHandler.next_pairing_status = 400
    _RepairHandler.next_pairing_body = b'{"error": "Pairing code is invalid, expired, revoked, or already used."}'

    with pytest.raises(RuntimeError, match="pairing failed"):
        Kairon(pairing_code="pair_cancelled", endpoint=repair_server, config_path=config_path)
    assert not Path(config_path).exists()
