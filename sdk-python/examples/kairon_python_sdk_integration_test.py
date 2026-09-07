"""Real FastAPI -> SDK -> KAIRON integration. No fabricated telemetry or backend.

Set KAIRON_ENDPOINT, KAIRON_PROJECT_ID, KAIRON_API_KEY, KAIRON_ENVIRONMENT.
Optional KAIRON_OPERATOR_KEY enables read-back verification through the backend API.
Run with Python from an environment containing kairon-sdk, FastAPI and uvicorn.
"""
import asyncio
import json
import os
import socket
import threading
import time
from contextlib import asynccontextmanager
from datetime import datetime, timezone
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import Request, urlopen
from uuid import UUID

import uvicorn
from fastapi import FastAPI
from kairon import Kairon
from kairon.middleware import KaironMiddleware


def run():
    required = ("KAIRON_ENDPOINT", "KAIRON_PROJECT_ID", "KAIRON_API_KEY", "KAIRON_ENVIRONMENT")
    if any(not os.environ.get(name) for name in required):
        raise RuntimeError("Set KAIRON_ENDPOINT, KAIRON_PROJECT_ID, KAIRON_API_KEY and KAIRON_ENVIRONMENT")
    UUID(os.environ["KAIRON_PROJECT_ID"])
    collector = Kairon(endpoint=os.environ["KAIRON_ENDPOINT"], project_id=os.environ["KAIRON_PROJECT_ID"],
        api_key=os.environ["KAIRON_API_KEY"], environment=os.environ["KAIRON_ENVIRONMENT"],
        application="PythonSDKIntegrationTest", service="PythonSDKTestService")
    started = datetime.now(timezone.utc)
    @asynccontextmanager
    async def lifespan(app):
        collector.start()
        try:
            yield
        finally:
            collector.stop(5)
    app = FastAPI(lifespan=lifespan)
    app.add_middleware(KaironMiddleware, kairon=collector)
    @app.get("/")
    async def root():
        return {"status": "ok"}
    @app.get("/test/slow")
    async def slow():
        await asyncio.sleep(3)
        return {"status": "completed"}
    @app.get("/test/error")
    async def error():
        raise RuntimeError("Intentional integration endpoint failure")
    sock = socket.socket()
    sock.bind(("127.0.0.1", 0))
    port = sock.getsockname()[1]
    server = uvicorn.Server(uvicorn.Config(app, log_level="critical", access_log=False))
    thread = threading.Thread(target=lambda: server.run(sockets=[sock]), daemon=True)
    thread.start()
    result = {"requests": [], "backendReadback": "NOT VERIFIED"}
    try:
        deadline = time.monotonic() + 15
        while not server.started and thread.is_alive() and time.monotonic() < deadline:
            time.sleep(.05)
        if not server.started:
            raise RuntimeError("FastAPI server did not start")
        for path, expected in [("/", 200), ("/test/slow", 200), ("/test/error", 500), ("/", 200)]:
            before = time.monotonic()
            try:
                with urlopen(f"http://127.0.0.1:{port}{path}", timeout=10) as response:
                    status = response.status
                    response.read()
            except HTTPError as response:
                status = response.code
                response.close()
            assert status == expected, "Application status mismatch"
            result["requests"].append({"path": path, "status": status, "elapsedMs": round((time.monotonic()-before)*1000)})
        # Automatic sampler emits actual process metrics and measured request aggregates.
        time.sleep(5.5)
        drained = collector.stop(5)
        result.update(delivered=collector.delivered_count, failed=collector.failed_count,
                      dropped=collector.dropped_count, pending=collector.pending_count, drained=drained,
                      lastDeliveryError=collector.last_delivery_error)
        operator = os.environ.get("KAIRON_OPERATOR_KEY")
        if operator:
            def read(kind):
                query = urlencode({"projectId": collector.project_id, "service": collector.service, "limit": 100})
                request = Request(collector.endpoint + "/api/telemetry/" + kind + "?" + query,
                                  headers={"X-Kairon-Operator-Key": operator})
                with urlopen(request, timeout=10) as response:
                    return [row for row in json.load(response) if datetime.fromisoformat(row["timestamp"].replace("Z", "+00:00")) >= started]
            events, metrics = read("incidents"), read("metrics")
            assert len(events) == 4, "Expected four ingested requests through the real API"
            for row in events:
                assert row["projectId"] == collector.project_id and row["service"] == collector.service
                assert row["application"] == collector.application and row["environment"] == collector.environment
                assert row["method"] == "GET"
            slow_row = next(row for row in events if row["endpoint"] == "/test/slow")
            error_row = next(row for row in events if row["endpoint"] == "/test/error")
            assert 2900 <= slow_row["durationMs"] <= 6000, "Slow duration was not measured correctly"
            assert error_row["statusCode"] == 500 and error_row["errorType"] == "RuntimeError"
            assert error_row["errorMessage"] == "Intentional integration endpoint failure" and error_row["stackTrace"]
            assert all(row["statusCode"] == 200 for row in events if row["endpoint"] != "/test/error")
            assert sum(row["requestCount"] for row in metrics) == 4
            assert sum(row["errorCount"] for row in metrics) == 1
            assert any(row["cpuPercent"] is not None for row in metrics)
            result.update(backendReadback="PASS", persistedRequests=len(events), persistedMetrics=len(metrics),
                          measuredSlowDurationMs=slow_row["durationMs"], exceptionCaptured=True)
        return result
    finally:
        server.should_exit = True
        thread.join(timeout=10)
        sock.close()


if __name__ == "__main__":
    try:
        result = run()
        print(json.dumps(result, indent=2))
        if not result["drained"] or result["failed"] or result["dropped"]:
            raise SystemExit(1)
    except Exception as exc:
        # Do not echo HTTP request objects, credentials or backend error response bodies.
        print(json.dumps({"result": "FAIL", "errorType": type(exc).__name__}))
        raise SystemExit(1)
