# Kairon Python SDK

The Python counterpart to `sdk/Kairon.SDK/` (the .NET client SDK) — a telemetry collector, not
a Kairon platform dependency. It talks to the same `/api/telemetry/incidents` and
`/api/telemetry/metrics` endpoints the .NET SDK already uses, with the same PascalCase field
names, so no backend changes were needed to add it.

## Install and authentication

From the KAIRON repository root, in your application's virtual environment:

```powershell
python -m pip install "./sdk-python[fastapi]"
```

This installs the core package plus Starlette, enough for `from kairon import Kairon`
and `from kairon.middleware import KaironMiddleware`. FastAPI and an ASGI server belong
to your host application; install `fastapi` and `uvicorn` there if absent. Python 3.9+
is supported. The core remains standard-library-only. Public package registry
availability is not assumed; deployment can use a release wheel instead of source.

Create/select a project in KAIRON's Pairing page and choose Python. A `pair_...` value
is a single-use, 10-minute credential-exchange code for that existing project. It is
not a project ID or telemetry API key. `from kairon import pair; pair(endpoint, code)`
returns `apiKey`, `projectId`, `endpoint`, or None on failure. Securely persist those
values yourself without printing the response. An operator may also issue a project
credential directly; pairing is not required at runtime and does not create a project.

The runtime sends the project UUID in JSON and the project key in `X-Kairon-API-Key`.
It does not use a Groq key, operator key or pairing code for telemetry. Revoked keys and
inactive/unregistered projects are rejected. Backend custom telemetry header settings
must remain compatible with this header. A scoped MachineId additionally requires the
operator's exact project/machine/service/environment and credential association.

## Usage

Supply these variables from your deployment configuration or secret store; do not
commit their values. The example reads them explicitly (the constructor does not
automatically load environment variables or a `.env` file):

```powershell
$env:KAIRON_ENDPOINT="http://127.0.0.1:8000"
$env:KAIRON_PROJECT_ID="<real-project-UUID>"
# Supply KAIRON_API_KEY securely from your deployment's secret store.
$env:KAIRON_ENVIRONMENT="Development"
```

```python
import os
from kairon import Kairon
from kairon.middleware import KaironMiddleware

collector = Kairon(
    endpoint=os.environ["KAIRON_ENDPOINT"],
    project_id=os.environ["KAIRON_PROJECT_ID"],
    api_key=os.environ["KAIRON_API_KEY"],
    environment=os.environ["KAIRON_ENVIRONMENT"],
    application="OrdersApp", service="OrdersService",
)
# Start in the FastAPI lifespan; stop in its finally block.
# See the executable integration example for the complete lifecycle.
app.add_middleware(KaironMiddleware, kairon=collector)
```

Use a base backend URL, without `/api`. Loopback works only on the same host;
containers and remote machines need an explicitly reachable backend. The default
desktop backend is loopback-only. Remote transport should use HTTPS.

The middleware must be installed and `collector.start()` called once per process.
It streams responses unchanged, measures through completion, preserves application
exceptions, and records request path, method, status, elapsed milliseconds, UTC
timestamp, application, service and environment. Handled HTTP errors retain their
actual status; unhandled errors before headers have status 500. A late streaming
error retains the already-sent HTTP status and includes exception details.

Automatic metrics every five seconds sample real process CPU/memory and aggregate
middleware request/error counts and mean duration. Omitting service uses the application
identity consistently for both requests and metrics. The SDK is collection-only;
telemetry does not grant remediation authorization.

## Real integration example

```powershell
python sdk-python/examples/kairon_python_sdk_integration_test.py
```

This replaces the old unauthenticated simulation worker. It runs a real local FastAPI
application and sends `GET /`, `GET /test/slow`, `GET /test/error`, `GET /` through real
HTTP. The slow route actually awaits three seconds; the error route actually raises.
No request telemetry or metrics are hand-posted or fabricated.

The four variables above are sufficient for SDK ingestion. For independent read-back
verification, the script additionally accepts `KAIRON_OPERATOR_KEY` from a secure
operator-controlled environment: GET telemetry APIs require operator authorization.
It is never passed to the collector. Without it, backend read-back is explicitly
NOT VERIFIED, even if delivery counters indicate acceptance. Never extract the desktop
host's private per-launch operator key; use an authorized backend configuration or UI.

Inspect `last_delivery_error` for a safe failure category/HTTP status (including 401/403),
and the counters below for delivery outcomes. It never includes URLs, payloads, keys
or raw exception text. HTTP 2xx is delivery acceptance, not independent database proof;
malformed informational response bodies are tolerated for compatibility with .NET.

## Design

Mirrors the .NET SDK's resilience contract exactly:

- One bounded, drop-oldest in-memory queue (`queue_capacity`, default 1000) — never blocks the
  calling request, never grows unbounded.
- One background sender thread plus a lightweight process-metrics sampler, one telemetry item
  per HTTP POST (no batching), short independent per-request timeout (`timeout_seconds`, default
  5s). Automatic metrics can be tuned with `metrics_interval_seconds` or disabled with
  `enable_metrics=False`.
- Fail-open everywhere: every network call is wrapped, nothing here ever raises into your
  application code. The one exception path that *does* re-raise is the host application's own
  exception inside `KaironMiddleware` — telemetry capture never swallows your own errors.
- No AI credential, no database setting, no remediation option — this is a collector and
  nothing more, same boundary `KaironOptions` documents on the .NET side.

## Delivery diagnostics and shutdown

`delivered_count`, `failed_count`, and `dropped_count` expose lifetime outcomes for
background sends. `pending_count` counts only waiting items, not in-flight HTTP requests.
After stopping request producers, `flush(timeout_seconds=5)` waits for both queued and
in-flight sends. `stop(timeout_seconds=5)` closes the queue, stops metric production, and
attempts the same bounded drain. Work still queued after the deadline is counted as dropped and removed; an in-flight call completes under its transport timeout. Both return `False` on timeout or any lifetime delivery
failure/drop; an empty queue alone is not a delivery confirmation. A stopped instance is
closed; create a new instance to restart collection.

Delivery remains best effort: there is no disk spool or automatic retry. An ambiguous
transport failure may have reached the collector; blindly retrying the legacy endpoints
could duplicate incidents. Observe the counters and handle a failed drain operationally.

## Tests

```bash
pip install -e .[test]
pytest
```

Convention: one test per failure mode proving it never throws (connection refused, DNS
failure, timeout, malformed response, 500, arbitrary transport exception), mirroring
`tests/Kairon.SDK.Tests/TelemetryClientTests.cs`.
