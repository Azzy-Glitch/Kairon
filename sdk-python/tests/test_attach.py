from __future__ import annotations

import json
import threading
import time
from contextlib import asynccontextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import pytest
from fastapi import FastAPI
from starlette.testclient import TestClient

import kairon.client as client_module
from kairon import Kairon, get_default_instance


class _KaironHandler(BaseHTTPRequestHandler):
    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0"))
        raw = self.rfile.read(length)
        self.server.requests.append((self.path, {key.lower(): value for key, value in self.headers.items()}, raw))

        if self.path == "/api/v1/sdk/pair":
            body = json.dumps(
                {
                    "apiKey": "krn_attach_test_key",
                    "projectId": "11111111-1111-1111-1111-111111111111",
                    "pairingId": "22222222-2222-2222-2222-222222222222",
                    "endpoint": self.server.url,
                }
            ).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return

        if self.path.endswith("/confirm"):
            self.send_response(204)
            self.end_headers()
            return

        if self.path == "/api/v1/telemetry/events":
            count = len(json.loads(raw)["events"])
            body = json.dumps({"accepted": count, "duplicates": 0, "rejected": 0}).encode()
        else:
            body = b'{"success":true}'
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *_args):
        pass


@pytest.fixture
def kairon_server():
    server = ThreadingHTTPServer(("127.0.0.1", 0), _KaironHandler)
    server.url = f"http://127.0.0.1:{server.server_port}"
    server.requests = []
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield server
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=2)


def _wait_for(predicate, timeout=3.0):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(0.02)
    assert predicate()


def test_attach_pairs_locally_registers_middleware_and_manages_lifecycle(
    monkeypatch, tmp_path, kairon_server
):
    config_path = tmp_path / "credential.json"
    monkeypatch.setattr(client_module, "DEFAULT_ENDPOINT", kairon_server.url)
    app = FastAPI(title="OrdersApp")

    collector = Kairon.attach(
        app,
        pairing_code="pair_attach_first_run",
        config_path=str(config_path),
        enable_metrics=False,
    )

    @app.get("/orders")
    def orders():
        return {"ok": True}

    assert collector._thread is None
    with TestClient(app) as web:
        assert get_default_instance() is collector
        assert web.get("/orders").json() == {"ok": True}
        _wait_for(lambda: collector.delivered_count == 1)

    paths = [request[0] for request in kairon_server.requests]
    assert paths.count("/api/v1/sdk/pair") == 1
    assert paths.count("/api/v1/sdk/pair/22222222-2222-2222-2222-222222222222/confirm") == 1
    assert paths.count("/api/v1/telemetry/events") == 1
    telemetry = next(request for request in kairon_server.requests if request[0] == "/api/v1/telemetry/events")
    payload = json.loads(telemetry[2])["events"][0]
    assert payload["Application"] == "OrdersApp"
    assert payload["Service"] == "OrdersApp"
    assert payload["HttpContext"]["Endpoint"] == "/orders"
    assert telemetry[1]["x-kairon-api-key"] == "krn_attach_test_key"
    assert config_path.exists()
    assert b"pair_attach_first_run" not in config_path.read_bytes()
    assert get_default_instance() is None
    assert collector._closed is True
    assert collector.last_shutdown_drained is True


def test_attach_reuses_stored_credential_without_pairing_again(tmp_path, kairon_server):
    config_path = tmp_path / "credential.json"
    first = Kairon.attach(
        FastAPI(),
        pairing_code="pair_attach_once",
        endpoint=kairon_server.url,
        config_path=str(config_path),
        enable_metrics=False,
    )
    # No lifespan needs to run for the credential to be durably paired and confirmed.
    assert first.project_id == "11111111-1111-1111-1111-111111111111"
    initial_pair_requests = sum(path == "/api/v1/sdk/pair" for path, _, _ in kairon_server.requests)

    app = FastAPI(title="StoredOrdersApp")
    collector = Kairon.attach(app, config_path=str(config_path), enable_metrics=False)

    @app.get("/stored")
    def stored():
        return {"stored": True}

    with TestClient(app) as web:
        assert web.get("/stored").status_code == 200
        _wait_for(lambda: collector.delivered_count == 1)

    assert sum(path == "/api/v1/sdk/pair" for path, _, _ in kairon_server.requests) == initial_pair_requests


def test_attach_preserves_an_existing_application_lifespan(tmp_path):
    events = []

    @asynccontextmanager
    async def existing_lifespan(_app):
        events.append("started")
        yield {"existing": "state"}
        events.append("stopped")

    app = FastAPI(lifespan=existing_lifespan)
    collector = Kairon.attach(
        app,
        endpoint="http://127.0.0.1:1",
        project_id="11111111-1111-1111-1111-111111111111",
        api_key="krn_attach_explicit",
        config_path=str(tmp_path / "credential.json"),
        enable_metrics=False,
    )

    with TestClient(app):
        assert events == ["started"]
        assert get_default_instance() is collector

    assert events == ["started", "stopped"]
    assert collector._closed is True


def test_attach_rejects_missing_configuration_and_duplicate_registration(tmp_path):
    with pytest.raises(ValueError, match="pairing_code"):
        Kairon.attach(FastAPI(), config_path=str(tmp_path / "missing.json"))

    app = FastAPI()
    Kairon.attach(
        app,
        endpoint="http://127.0.0.1:8000",
        project_id="11111111-1111-1111-1111-111111111111",
        api_key="krn_attach_explicit",
        config_path=str(tmp_path / "explicit.json"),
        enable_metrics=False,
    )
    with pytest.raises(RuntimeError, match="already attached"):
        Kairon.attach(app, config_path=str(tmp_path / "explicit.json"))
