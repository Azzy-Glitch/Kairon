"""
Tests for kairon.pair() (docs/DESKTOP_SHELL.md) - mirrors
tests/Kairon.SDK.Tests/PairingClientTests.cs: every failure path returns None rather than
raising, same invariant as the rest of this client.
"""

from __future__ import annotations

import json
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer
from unittest.mock import patch

import pytest

from kairon import __version__
from kairon.client import pair


class _PairingHandler(BaseHTTPRequestHandler):
    status_to_return = 200
    body_to_return = b'{"apiKey": "krn_abc123", "projectId": "11111111-1111-1111-1111-111111111111", "endpoint": "http://127.0.0.1:8000"}'
    received: list = []

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        raw = self.rfile.read(length)
        _PairingHandler.received.append(json.loads(raw))
        self.send_response(_PairingHandler.status_to_return)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(_PairingHandler.body_to_return)

    def log_message(self, *args):
        pass


@pytest.fixture()
def pairing_server():
    _PairingHandler.received = []
    _PairingHandler.status_to_return = 200
    _PairingHandler.body_to_return = b'{"apiKey": "krn_abc123", "projectId": "11111111-1111-1111-1111-111111111111", "endpoint": "http://127.0.0.1:8000"}'

    server = HTTPServer(("127.0.0.1", 0), _PairingHandler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield f"http://127.0.0.1:{server.server_port}"
    finally:
        server.shutdown()
        thread.join(timeout=2)


def test_successful_pairing_returns_the_issued_credential(pairing_server):
    result = pair(pairing_server, "pair_validcode")

    assert result is not None
    assert result["apiKey"] == "krn_abc123"
    assert result["projectId"] == "11111111-1111-1111-1111-111111111111"

    request_body = _PairingHandler.received[0]
    assert request_body["code"] == "pair_validcode"
    assert request_body["sdkType"] == "python"
    assert request_body["version"] == __version__


def test_rejected_pairing_code_returns_none_not_an_exception(pairing_server):
    _PairingHandler.status_to_return = 400
    _PairingHandler.body_to_return = b'{"error": "Pairing code is invalid, expired, revoked, or already used."}'

    assert pair(pairing_server, "pair_expired") is None


def test_malformed_response_body_returns_none_not_an_exception(pairing_server):
    _PairingHandler.body_to_return = b"not json"

    assert pair(pairing_server, "pair_x") is None


def test_unreachable_backend_returns_none_not_an_exception():
    assert pair("http://127.0.0.1:1", "pair_x", timeout_seconds=1.0) is None


def test_timeout_returns_none_not_an_exception():
    import socket

    with patch("kairon.client.urllib.request.urlopen", side_effect=socket.timeout("timed out")):
        assert pair("http://localhost:8000", "pair_x") is None


@pytest.mark.parametrize("body", [b"{}", b"[]", b"null", b'{"apiKey":"x","projectId":"not-a-uuid","endpoint":"http://localhost"}'])
def test_valid_json_without_usable_credentials_is_rejected(pairing_server, body):
    _PairingHandler.body_to_return = body
    assert pair(pairing_server, "pair_x") is None


def test_invalid_endpoint_is_contained_before_request_creation():
    assert pair("http://[broken", "pair_x") is None
