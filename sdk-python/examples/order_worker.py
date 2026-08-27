"""
Example FastAPI companion worker for the Kairon demo scenario.

This represents a Python-language piece of the same "OrderProcessingService" the .NET demo app
(demo/Kairon.DemoApp) already simulates a retry storm in. Tagging both with the same
`service` name is what makes them fold into the SAME incident under Kairon's existing
Service-scoped correlation key - it proves telemetry from two different languages can feed one
incident, not two separate ones.

Run:
    pip install -e ..[fastapi]
    uvicorn order_worker:app --port 8090

Then point it at a running Kairon backend (default http://localhost:8000) and call
POST /api/inventory/reserve a few times to generate exception telemetry, or GET / to see it
report a normal request.
"""

from __future__ import annotations

import os
import random

from fastapi import FastAPI

from kairon import Kairon
from kairon.middleware import KaironMiddleware

kairon = Kairon(
    endpoint=os.environ.get("KAIRON_ENDPOINT", "http://localhost:8000"),
    project_id=os.environ.get("KAIRON_PROJECT_ID", "550e8400-e29b-41d4-a716-446655440000"),
    service="OrderProcessingService",
    application="kairon-python-worker",
    environment="Demo",
)
kairon.start()

app = FastAPI(title="Kairon Python demo worker")
app.add_middleware(KaironMiddleware)


@app.get("/")
def root():
    return {
        "status": "Kairon Python worker running",
        "service": kairon.service,
        "routes": ["/api/inventory/check", "/api/inventory/reserve"],
        "note": "Stop the Kairon backend and call these again - this app keeps working; only telemetry delivery fails, silently.",
    }


@app.get("/api/inventory/check")
def check_inventory():
    return {"available": True, "sku": "widget-001", "quantity": random.randint(1, 50)}


@app.post("/api/inventory/reserve")
def reserve_inventory():
    """Deliberately unreliable, mirroring the .NET demo app's own /api/failure endpoint - the
    SDK middleware captures this as telemetry before it propagates, same as the .NET SDK does."""
    if random.random() < 0.5:
        raise RuntimeError("Inventory reservation failed: downstream warehouse service timeout")

    return {"reserved": True, "sku": "widget-001"}
