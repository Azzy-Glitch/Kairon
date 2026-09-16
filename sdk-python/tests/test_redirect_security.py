"""Redirect handling for every network call this SDK makes.

urllib's default opener REPLAYS a request - headers included - at whatever address a 3xx response's
Location header names. For this SDK that is a credential-exfiltration primitive: a compromised or
misconfigured backend could collect X-Kairon-API-Key, or a pairing code, simply by answering
"302 Location: http://attacker/". kairon.client therefore sends through a private, non-redirecting
opener.

These tests drive REAL local HTTP servers - a redirector and a sink standing in for the attacker -
and assert the sink is never contacted at all, for every redirect status urllib would otherwise
follow, in both same-origin and cross-origin form, and for a malformed or absent Location.
"""

from __future__ import annotations

import json
import shutil
import ssl
import subprocess
import tempfile
import threading
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import pytest

from kairon.client import (
    Kairon,
    RedirectNotAllowedError,
    _confirm_pairing,
    _open,
    _opener,
    _RefuseRedirects,
    pair,
)

REDIRECT_STATUSES = [301, 302, 303, 307, 308]


class _Sink:
    """Stands in for the redirect destination. Records every request that reaches it - the whole
    point is that this list stays empty."""

    def __init__(self) -> None:
        self.requests: list[tuple[str, dict, bytes]] = []
        self._lock = threading.Lock()

    def record(self, path: str, headers: dict, body: bytes) -> None:
        with self._lock:
            self.requests.append((path, headers, body))


def _handler_factory(state: dict, sink: _Sink | None):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, *args):  # silence the default stderr access log
            pass

        def _read_body(self) -> bytes:
            length = int(self.headers.get("Content-Length") or 0)
            return self.rfile.read(length) if length else b""

        def do_POST(self):
            body = self._read_body()
            if sink is not None:
                sink.record(self.path, dict(self.headers), body)
                payload = json.dumps({"success": True}).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)
                return

            self.send_response(state["status"])
            if state["location"] is not None:
                self.send_header("Location", state["location"])
            self.send_header("Content-Length", "0")
            self.end_headers()

        do_GET = do_POST

    return Handler


def _serve(state: dict, sink: _Sink | None = None, tls_context: ssl.SSLContext | None = None):
    server = ThreadingHTTPServer(("127.0.0.1", 0), _handler_factory(state, sink))
    if tls_context is not None:
        server.socket = tls_context.wrap_socket(server.socket, server_side=True)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    scheme = "https" if tls_context is not None else "http"
    return server, f"{scheme}://127.0.0.1:{server.server_address[1]}"


@pytest.fixture
def sink():
    state: dict = {}
    s = _Sink()
    server, url = _serve(state, sink=s)
    try:
        yield s, url
    finally:
        server.shutdown()
        server.server_close()


@pytest.fixture
def redirector():
    state = {"status": 302, "location": None}
    server, url = _serve(state)
    try:
        yield state, url
    finally:
        server.shutdown()
        server.server_close()


# --- The transport itself -------------------------------------------------------------------


@pytest.mark.parametrize("status", REDIRECT_STATUSES)
def test_every_redirect_status_is_refused_not_followed(status, redirector, sink):
    state, origin = redirector
    recorded, sink_url = sink
    state["status"] = status
    state["location"] = sink_url + "/harvest"

    request = urllib.request.Request(origin + "/api/v1/sdk/pair", data=b"{}", method="POST")
    request.add_header("X-Kairon-API-Key", "krn_secret_value")

    with pytest.raises(RedirectNotAllowedError) as raised:
        _open(request, timeout=10)

    assert raised.value.status == status
    assert recorded.requests == [], "the redirect destination was contacted"


def test_a_same_origin_redirect_gets_no_more_trust_than_a_cross_origin_one(redirector):
    # Same-origin is where a "just follow it, it's the same host" exemption would look safe. It
    # isn't: the path can still be attacker-chosen, and the credential would still be replayed.
    state, origin = redirector
    state["status"] = 307
    state["location"] = origin + "/somewhere-else"

    with pytest.raises(RedirectNotAllowedError):
        _open(urllib.request.Request(origin + "/api/v1/sdk/pair", data=b"{}", method="POST"), timeout=10)


def test_a_plaintext_downgrade_target_is_refused(redirector, sink):
    state, origin = redirector
    recorded, sink_url = sink
    state["status"] = 302
    state["location"] = sink_url.replace("http://", "http://")  # explicit: a plaintext destination

    with pytest.raises(RedirectNotAllowedError):
        _open(urllib.request.Request(origin + "/api/v1/sdk/pair", data=b"{}", method="POST"), timeout=10)

    assert recorded.requests == []


def test_an_https_target_is_refused_too(redirector):
    # Refusal is not a scheme filter. Even an "upgrade" to HTTPS is a request this SDK did not
    # decide to make, at an address it never validated.
    state, origin = redirector
    state["status"] = 301
    state["location"] = "https://example.invalid/harvest"

    with pytest.raises(RedirectNotAllowedError) as raised:
        _open(urllib.request.Request(origin + "/api/v1/sdk/pair", data=b"{}", method="POST"), timeout=10)

    assert raised.value.location == "https://example.invalid/harvest"


@pytest.mark.parametrize("location", [
    None,                         # 302 with no Location header at all
    "",                           # present but empty
    "://::",                      # unparsable
    "http://[oops",               # malformed authority
    "ht tp://example.invalid/",   # illegal scheme characters
    "\\\\attacker\\share",        # a UNC path, not a URL
])
def test_a_malformed_or_absent_location_never_becomes_a_request(location, redirector, sink):
    state, origin = redirector
    recorded, _ = sink
    state["status"] = 302
    state["location"] = location

    # Either outcome is correct and neither follows anything: urllib raises HTTPError when it
    # cannot find a usable Location, and our handler raises when it can.
    with pytest.raises((RedirectNotAllowedError, urllib.error.HTTPError, ValueError)):
        _open(urllib.request.Request(origin + "/api/v1/sdk/pair", data=b"{}", method="POST"), timeout=10)

    assert recorded.requests == []


def test_a_normal_response_still_works_through_the_private_opener(sink):
    # The opener must only remove redirect following - ordinary requests are unaffected.
    _, sink_url = sink
    with _open(urllib.request.Request(sink_url + "/ok", data=b"{}", method="POST"), timeout=10) as response:
        assert response.status == 200


def test_the_private_opener_is_never_installed_globally():
    # Host applications share this process. This SDK's transport policy is its own business.
    assert urllib.request._opener is not _opener


# --- The three public call paths --------------------------------------------------------------


@pytest.mark.parametrize("status", REDIRECT_STATUSES)
def test_pairing_never_hands_the_pairing_code_to_a_redirect_destination(status, redirector, sink):
    state, origin = redirector
    recorded, sink_url = sink
    state["status"] = status
    state["location"] = sink_url + "/api/v1/sdk/pair"

    assert pair(origin, "pair_secret_code") is None
    assert recorded.requests == []


@pytest.mark.parametrize("status", REDIRECT_STATUSES)
def test_confirmation_never_hands_the_api_key_to_a_redirect_destination(status, redirector, sink):
    state, origin = redirector
    recorded, sink_url = sink
    state["status"] = status
    state["location"] = sink_url + "/confirm"

    assert _confirm_pairing(origin, "3fa85f64-5717-4562-b3fc-2c963f66afa6", "krn_secret_value",
                            attempts=1) is False
    assert recorded.requests == []


@pytest.mark.parametrize("status", REDIRECT_STATUSES)
def test_telemetry_never_hands_the_api_key_to_a_redirect_destination(status, redirector, sink):
    state, origin = redirector
    recorded, sink_url = sink
    state["status"] = status
    state["location"] = sink_url + "/api/telemetry/incidents"

    client = Kairon(project_id="3fa85f64-5717-4562-b3fc-2c963f66afa6", endpoint=origin,
                    api_key="krn_secret_value", timeout_seconds=10)
    try:
        assert client._send("api/telemetry/incidents", {"Endpoint": "/x"}) is False
        assert recorded.requests == [], "the API key was replayed at the redirect destination"
        # The failure is reported as a fixed category, with no attacker-controlled text in it.
        assert "redirect not followed" in (client.last_delivery_error or "")
        assert sink_url not in (client.last_delivery_error or "")
        assert "krn_secret_value" not in (client.last_delivery_error or "")
    finally:
        client.stop()


# --- HTTPS origin: a real TLS listener, when one can be provisioned here ------------------------


def _self_signed_context():
    """A throwaway self-signed cert for 127.0.0.1, built with whatever openssl is on PATH.
    Returns (server_context, client_context) or None when openssl is unavailable."""
    openssl = shutil.which("openssl")
    if not openssl:
        return None

    directory = Path(tempfile.mkdtemp(prefix="kairon-redirect-tls-"))
    cert, key = directory / "cert.pem", directory / "key.pem"
    completed = subprocess.run(
        [openssl, "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
         "-subj", "/CN=127.0.0.1", "-addext", "subjectAltName=IP:127.0.0.1",
         "-keyout", str(key), "-out", str(cert)],
        capture_output=True)
    if completed.returncode != 0 or not cert.exists():
        return None

    server_context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    server_context.load_cert_chain(certfile=str(cert), keyfile=str(key))
    client_context = ssl.create_default_context(cafile=str(cert))
    return server_context, client_context


def test_an_https_origin_cannot_downgrade_this_sdk_to_plaintext_by_redirect():
    provisioned = _self_signed_context()
    if provisioned is None:
        pytest.skip("no openssl on PATH to provision a throwaway TLS listener")
    server_context, client_context = provisioned

    sink_state: dict = {}
    recorded = _Sink()
    sink_server, sink_url = _serve(sink_state, sink=recorded)
    state = {"status": 302, "location": sink_url + "/harvest"}
    tls_server, tls_url = _serve(state, tls_context=server_context)

    try:
        request = urllib.request.Request(tls_url + "/api/v1/sdk/pair", data=b"{}", method="POST")
        request.add_header("X-Kairon-API-Key", "krn_secret_value")

        # The SDK's own refusing handler, plus a TLS handler trusting the throwaway CA only for
        # this call - so the test proves redirect refusal rather than incidentally passing because
        # certificate verification failed first.
        opener = urllib.request.build_opener(
            _RefuseRedirects(), urllib.request.HTTPSHandler(context=client_context))

        with pytest.raises(RedirectNotAllowedError):
            opener.open(request, timeout=10)

        assert recorded.requests == [], "an HTTPS origin walked this SDK down to plaintext"
    finally:
        for server in (tls_server, sink_server):
            server.shutdown()
            server.server_close()


def test_the_client_opener_actually_carries_the_refusing_handler():
    installed = [h for h in _opener.handlers if isinstance(h, urllib.request.HTTPRedirectHandler)]

    assert installed, "no redirect handler at all - urllib would fall back to its default"
    assert all(isinstance(h, _RefuseRedirects) for h in installed), \
        "a following redirect handler is present in the SDK's opener"
