# KAIRON Python SDK

Optional deep instrumentation for Python and FastAPI applications. The KAIRON Agent continues to
provide basic process, CPU, memory, and available log monitoring without this SDK.

```python
from fastapi import FastAPI
from kairon_sdk import KaironClient, KaironMiddleware, KaironOptions

client = KaironClient(KaironOptions(
    endpoint="http://127.0.0.1:8000",
    project_id="YOUR_PROJECT_ID",
    api_key="YOUR_KAIRON_TELEMETRY_KEY",
    application="orders-api",
    environment="Development",
))

app = FastAPI()
app.add_middleware(KaironMiddleware, client=client)

@app.on_event("startup")
def start_kairon():
    client.start()

@app.on_event("shutdown")
def stop_kairon():
    client.close()
```

Collection is non-blocking and bounded. Transport errors are contained and never fail the host
application. The SDK knows nothing about KAIRON persistence, AI providers, or remediation.
