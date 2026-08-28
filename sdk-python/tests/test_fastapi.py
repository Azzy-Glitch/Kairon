from fastapi import FastAPI
from fastapi.testclient import TestClient

from kairon_sdk import KaironClient, KaironMiddleware, KaironOptions


def build():
    delivered = []
    telemetry = KaironClient(KaironOptions(project_id="550e8400-e29b-41d4-a716-446655440000"),
                             sender=lambda body, *_: delivered.extend(body["events"]))
    app = FastAPI()
    app.add_middleware(KaironMiddleware, client=telemetry)

    @app.get("/ok")
    def ok():
        return {"ok": True}

    @app.get("/fail")
    def fail():
        raise RuntimeError("application failure")

    return TestClient(app, raise_server_exceptions=False), telemetry, delivered


def test_successful_request_is_captured_without_changing_response():
    app, telemetry, delivered = build()
    response = app.get("/ok")
    telemetry.flush()
    assert response.status_code == 200
    assert delivered[0]["eventType"] == "http"
    assert delivered[0]["httpContext"]["endpoint"] == "/ok"


def test_application_exception_remains_an_application_failure():
    app, telemetry, delivered = build()
    response = app.get("/fail")
    telemetry.flush()
    assert response.status_code == 500
    assert delivered[0]["eventType"] == "exception"
