# Kairon Python SDK

The Python counterpart to `sdk/Kairon.SDK/` (the .NET client SDK) — a telemetry collector, not
a Kairon platform dependency. One-call framework integrations send idempotent batches to
`/api/v1/telemetry/events`. The lower-level direct client retains the existing
`/api/telemetry/incidents` and `/api/telemetry/metrics` routes for compatibility.
The backend also accepts an optional request-correlation ID.

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

In KAIRON open **Connect an App**, choose your Python framework, pick the project and
environment (and optionally the service name), and generate a pairing code. A `pair_...` value
is a single-use, 10-minute credential-exchange code for that project. It is not a project ID or
telemetry API key.

For FastAPI/Starlette, the normal integration is one call. It redeems the code, stores the
resulting connection, registers request telemetry, starts with the application, and performs a
bounded drain during shutdown:

### Simple mode

```python
from fastapi import FastAPI
from kairon import Kairon

app = FastAPI()
Kairon.attach(app, pairing_code="YOUR_PAIRING_CODE")
```

That is the whole integration. The SDK resolves and stores the endpoint, project and credential,
adopts the environment and service name chosen when the code was generated, proves the machine
through the local KAIRON Agent (when installed), and starts sending request, error and CPU/latency
telemetry. No project ID, API key, machine ID, endpoint, header, batching or storage path is needed.

Leaving the same code in your source is safe: after the first redemption the SDK recognises the
code (by a fingerprint stored with the credential) and reuses the stored connection on restart,
and several workers started together share the one redemption instead of failing. `Kairon.attach(app)`
without a code also works once paired. The credential file is per application (working directory
plus service/application name) and is encrypted with DPAPI on Windows (owner-only elsewhere).

### Advanced mode

```python
Kairon.attach(app, pairing_code="YOUR_PAIRING_CODE", environment="Development", service="Orders API")
```

Explicit `environment`/`service`/`application` (or `KAIRON_ENVIRONMENT`) override the pairing
defaults; without either, the framework name (FastAPI title, Flask app name, Django settings
package) is the last fallback. `machine_id` is accepted for compatibility but ignored: machine
identity only ever comes from the local Agent's proof. The low-level `Kairon` constructor and
`KaironMiddleware` remain available for workers and applications that need manual control.

**Pairing against a remote or cloud KAIRON backend?** Set `KAIRON_ENDPOINT` (or pass
`endpoint=`) to that backend's real HTTPS address *first* - the pairing call itself has
to reach the right backend to redeem the code. `Kairon.attach(app, pairing_code=...)` with no
endpoint tries `http://localhost:8000`, this machine, not a remote one. "Nothing else
to configure" only holds when KAIRON is on this same machine.

Lower-level, if you need the resulting credential without starting a collector (a
one-time setup script, for example): `from kairon import pair; pair(endpoint, code)`
returns `apiKey`, `projectId`, `endpoint`, or None on failure. Securely persist those
values yourself without printing the response. An operator may also issue a project
credential directly; pairing is not required at runtime and does not create a project.

The runtime sends the project UUID in JSON and the project key in `X-Kairon-API-Key`.
It does not use a Groq key, operator key or pairing code for telemetry. Revoked keys and
inactive/unregistered projects are rejected. Backend custom telemetry header settings
must remain compatible with this header. A scoped MachineId additionally requires the
operator's exact project/machine/service/environment and credential association.

## Supported web integrations

FastAPI and Starlette use `Kairon.attach(app, pairing_code=...)` on the first run and
`Kairon.attach(app)` thereafter. Flask uses the same call; install the matching optional
extra with `python -m pip install "./sdk-python[flask]"`. The core collector and generic
ASGI/WSGI wrappers need no web-framework dependency.

For Django 4.2+, install `./sdk-python[django]`, add
`"kairon.django.KaironMiddleware"` near the start of `MIDDLEWARE`, and set
`KAIRON = {"pairing_code": os.getenv("KAIRON_PAIRING_CODE")}` in settings for the first
run. Remove the environment variable after pairing. This middleware works with Django's
ASGI and WSGI handlers; no FastAPI lifecycle hook is involved.

For another ASGI 3 app, wrap its callable with `Kairon.wrap_asgi(app, pairing_code=...)`.
For a WSGI app use `Kairon.wrap_wsgi(app, pairing_code=...)`. On later runs omit the code.
Both wrappers expose `.kairon` and `.close()`. ASGI lifespan and FastAPI/Starlette manage
shutdown automatically. WSGI and Flask lack a portable application-shutdown hook: call
the wrapper's `close()` during your server shutdown for a bounded drain, or rely on its
best-effort process-exit fallback. Do not claim untested framework-specific semantics
merely because its server speaks ASGI or WSGI.

## FastAPI usage

The normal local first run needs only the pairing code. On every later run, leave the pairing
code unset; the SDK reuses the protected stored endpoint, project ID and API key. For remote/cloud
first contact, also set `KAIRON_ENDPOINT` to the reachable HTTPS backend; it is bootstrap
information and the endpoint returned by pairing is what gets stored.

```python
import os
from fastapi import FastAPI
from kairon import Kairon

app = FastAPI(title="OrdersApp")
Kairon.attach(app, pairing_code=os.environ.get("KAIRON_PAIRING_CODE"))
```

After the code has been redeemed you may remove `KAIRON_PAIRING_CODE` (keeping it is harmless).
The service name chosen at pairing wins over the FastAPI title; pass `application=` or
`service=` to override both.

Explicit `endpoint`/`project_id`/`api_key` arguments and the matching `KAIRON_*` environment
variables remain supported for managed deployments with no stored credential. The SDK does not
load `.env` files itself. Once a paired credential exists, its endpoint, project ID and API key
win together; ambient values are never mixed into that stored identity.

Use a base backend URL, without `/api`. Loopback works only on the same host;
containers and remote machines need an explicitly reachable backend. The default
desktop backend is loopback-only. Remote transport should use HTTPS.

`Kairon.attach()` installs the existing middleware and wraps—not replaces—an existing application
lifespan. It streams responses unchanged, measures through completion, preserves application
exceptions, and records request path, method, status, elapsed milliseconds, UTC
timestamp, application, service and environment. Handled HTTP errors retain their
actual status; unhandled errors before headers have status 500. A late streaming
error retains the already-sent HTTP status and includes exception details.

For the advanced/manual path, construct `Kairon(...)`, call `start()`/`stop()`, and register
`KaironMiddleware` exactly as before. Do not combine manual lifecycle management with
`Kairon.attach()` for the same collector.

Automatic metrics every five seconds sample real **process** CPU/memory and aggregate
middleware request/error counts and mean duration. Omitting service uses the application
identity consistently for both requests and metrics. The SDK is collection-only;
telemetry does not grant remediation authorization.

CPU is this process's CPU time as a share of the CPUs it may run on (the same scale as the .NET
SDK). It is never machine-wide CPU, and each metric event says so (`cpu.scope = process`).
Metric events also carry this process's id, working directory and interpreter path. KAIRON uses
these to offer *Restart the application* after an operator's approval. The backend trusts the
folder only for the process the local Agent itself identified, and a restart is always performed
by the KAIRON UserAgent, never by the SDK.

Delivery retries transient failures (`delivery_attempts`, default 3) and honours `Retry-After`
on a rate limit. A batch over the backend's 1 MiB limit is split rather than dropped.

### Reporting outside a web request

For a worker, scheduled job, or anywhere there is no HTTP request to instrument,
`capture_exception`/`record_metric` report directly - non-blocking, bounded, and fail-open
like every other telemetry path here:

```python
try:
    process_order(order)
except Exception as exc:
    kairon.capture_exception(exc, endpoint="/jobs/order-processing", method="JOB", status_code=500)

kairon.record_metric(queue_depth=queue.qsize(), component="order-worker")
```

### Redirect and transport safety

Pairing, confirmation and telemetry never follow an HTTP redirect: every network call goes
through a private urllib opener with redirect-following disabled, so a compromised or
misconfigured backend cannot redirect one of those requests - and the credentials on it -
onto a different origin merely by answering with a 3xx. Plain HTTP is accepted only to a
loopback address (`localhost`/`127.0.0.0/8`/`::1`); anywhere else requires HTTPS, checked at
every point an endpoint can enter the SDK (explicit configuration, a pairing response's own
returned endpoint, the actual send itself) rather than once at startup.

## Real integration example

```powershell
python sdk-python/examples/kairon_python_sdk_integration_test.py
```

This replaces the old unauthenticated simulation worker. It runs a real local FastAPI
application and sends `GET /`, `GET /test/slow`, `GET /test/error`, `GET /` through real
HTTP. The slow route actually awaits three seconds; the error route actually raises.
No request telemetry or metrics are hand-posted or fabricated.

The integration example accepts first-run `KAIRON_PAIRING_CODE` or an existing stored credential.
For independent read-back verification, the script additionally accepts `KAIRON_OPERATOR_KEY` from a secure
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
- One background sender thread plus a lightweight process-metrics sampler. One-call web adapters
  batch up to 25 observations per idempotent POST (configurable through the advanced constructor),
  with a short independent request timeout (`timeout_seconds`, default 5s). Automatic metrics can be tuned with `metrics_interval_seconds` or disabled with
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

Delivery remains best effort: there is no disk spool. The one-call framework adapters use stable
event IDs and bounded retries for transport errors, HTTP 429 and selected 5xx responses; the
backend deduplicates retries. Authentication failures (401/403), redirects and invalid batches
are not retried. The older direct-client legacy routes do not retry, because an ambiguous
response could otherwise duplicate incidents. Observe the counters and handle a failed drain
operationally.

## Tests

```bash
pip install -e .[test]
pytest
```

Convention: one test per failure mode proving it never throws (connection refused, DNS
failure, timeout, malformed response, 500, arbitrary transport exception), mirroring
`tests/Kairon.SDK.Tests/TelemetryClientTests.cs`.
