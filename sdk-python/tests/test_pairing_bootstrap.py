"""
Tests for the pairing-code-only onboarding path: `Kairon(pairing_code="...")` should redeem the
code through the real pair() contract, persist the resulting credential via
kairon._credential_store, and configure the client from it - while explicit configuration,
environment variables, and a previously stored credential all continue to work exactly as
before. Mirrors tests/Kairon.SDK.Tests/KaironTests.cs; reuses the same local-HTTPServer fixture
convention as test_pairing.py.
"""

from __future__ import annotations

import json
import os
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

import pytest

from kairon import Kairon


class _PairingHandler(BaseHTTPRequestHandler):
    status_to_return = 200
    body_to_return = b'{"apiKey": "krn_real_key", "projectId": "66666666-6666-6666-6666-666666666666", "pairingId": "88888888-8888-8888-8888-888888888888", "endpoint": "http://127.0.0.1:8000"}'

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        self.rfile.read(length)
        self.send_response(_PairingHandler.status_to_return)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(_PairingHandler.body_to_return)

    def log_message(self, *args):
        pass


@pytest.fixture()
def pairing_server():
    _PairingHandler.status_to_return = 200
    _PairingHandler.body_to_return = b'{"apiKey": "krn_real_key", "projectId": "66666666-6666-6666-6666-666666666666", "pairingId": "88888888-8888-8888-8888-888888888888", "endpoint": "http://127.0.0.1:8000"}'
    server = HTTPServer(("127.0.0.1", 0), _PairingHandler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield f"http://127.0.0.1:{server.server_port}"
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


def test_pairing_code_alone_redeems_persists_and_configures_the_client(pairing_server, config_path):
    kairon = Kairon(pairing_code="pair_realcode", endpoint=pairing_server, config_path=config_path)
    try:
        assert kairon.project_id == "66666666-6666-6666-6666-666666666666"
        assert kairon.endpoint == "http://127.0.0.1:8000"
        assert kairon.api_key == "krn_real_key"
        assert Path(config_path).exists(), "the redeemed credential must be persisted for future runs"
        # The persisted file must never contain the pairing code itself.
        assert b"pair_realcode" not in Path(config_path).read_bytes()
    finally:
        kairon.stop(timeout_seconds=1)


def test_second_run_reuses_the_stored_credential_without_redeeming_again(pairing_server, config_path):
    first = Kairon(pairing_code="pair_onceonly", endpoint=pairing_server, config_path=config_path)
    try:
        assert first.project_id == "66666666-6666-6666-6666-666666666666"
    finally:
        first.stop(timeout_seconds=1)

    # A real pairing code is single-use; the fake server would happily "redeem" the same code
    # again, so prove the second construction needs no network access at all by pointing it at an
    # endpoint nothing is listening on.
    second = Kairon(endpoint="http://127.0.0.1:1", config_path=config_path)
    try:
        assert second.project_id == "66666666-6666-6666-6666-666666666666"
        assert second.api_key == "krn_real_key"
    finally:
        second.stop(timeout_seconds=1)


def test_invalid_or_expired_pairing_code_fails_clearly_without_persisting_anything(pairing_server, config_path):
    _PairingHandler.status_to_return = 400
    _PairingHandler.body_to_return = b'{"error": "Pairing code is invalid, expired, revoked, or already used."}'

    with pytest.raises(RuntimeError, match="pairing failed"):
        Kairon(pairing_code="pair_bad", endpoint=pairing_server, config_path=config_path)

    assert not Path(config_path).exists()


def test_unavailable_backend_fails_clearly_without_persisting_anything(config_path):
    with pytest.raises(RuntimeError, match="pairing failed"):
        Kairon(pairing_code="pair_x", endpoint="http://127.0.0.1:1", config_path=config_path)

    assert not Path(config_path).exists()


def test_explicit_configuration_is_used_without_any_pairing_code(config_path):
    kairon = Kairon(endpoint="http://127.0.0.1:9999", project_id="proj-explicit", api_key="krn_explicit",
                     config_path=config_path)
    try:
        assert kairon.project_id == "proj-explicit"
        assert kairon.endpoint == "http://127.0.0.1:9999"
        assert not Path(config_path).exists(), "explicit configuration should not trigger pairing persistence"
    finally:
        kairon.stop(timeout_seconds=1)


def test_environment_variables_are_used_when_no_explicit_value_is_given(monkeypatch, config_path):
    monkeypatch.setenv("KAIRON_ENDPOINT", "http://127.0.0.1:8123")
    monkeypatch.setenv("KAIRON_PROJECT_ID", "proj-from-env")
    monkeypatch.setenv("KAIRON_API_KEY", "krn_from_env")

    kairon = Kairon(config_path=config_path)
    try:
        assert kairon.project_id == "proj-from-env"
        assert kairon.endpoint == "http://127.0.0.1:8123"
    finally:
        kairon.stop(timeout_seconds=1)


def test_missing_everything_fails_clearly_instead_of_starting_half_configured(config_path):
    with pytest.raises(ValueError, match="needs a project_id"):
        Kairon(config_path=config_path)


def test_stored_credential_is_encrypted_or_permission_restricted_at_rest(pairing_server, config_path):
    kairon = Kairon(pairing_code="pair_secureme", endpoint=pairing_server, config_path=config_path)
    try:
        raw = Path(config_path).read_bytes()
        # Windows: DPAPI-protected bytes, not plain JSON. POSIX: plain JSON is acceptable only
        # because the file is owner-only (0600) - either way, the API key must not be trivially
        # readable by another local account.
        if os.name == "nt":
            assert b"krn_real_key" not in raw
        else:
            assert (Path(config_path).stat().st_mode & 0o777) == 0o600
    finally:
        kairon.stop(timeout_seconds=1)
