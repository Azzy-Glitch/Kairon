# Kairon Python SDK

The Python counterpart to `sdk/Kairon.SDK/` (the .NET client SDK) — a telemetry collector, not
a Kairon platform dependency. It talks to the same `/api/telemetry/incidents` and
`/api/telemetry/metrics` endpoints the .NET SDK already uses, with the same PascalCase field
names, so no backend changes were needed to add it.

## Install

```bash
pip install -e .              # core client only, stdlib-only, zero dependencies
pip install -e .[fastapi]     # + KaironMiddleware for FastAPI/Starlette apps
pip install -e .[test]        # + pytest and everything the test suite needs
```

## Usage

```python
from kairon import Kairon

kairon = Kairon(
    endpoint="https://your-kairon-server",
    project_id="my-project",
    service="OrderProcessingService",
)
kairon.start()
```

For FastAPI:

```python
from kairon.middleware import KaironMiddleware

app.add_middleware(KaironMiddleware)
```

`kairon.middleware` is a separate import specifically so the core `kairon` package never
requires starlette/fastapi — only apps that use the middleware need that extra.

## Design

Mirrors the .NET SDK's resilience contract exactly:

- One bounded, drop-oldest in-memory queue (`queue_capacity`, default 1000) — never blocks the
  calling request, never grows unbounded.
- One background sender thread, one telemetry item per HTTP POST (no batching), short
  independent per-request timeout (`timeout_seconds`, default 5s).
- Fail-open everywhere: every network call is wrapped, nothing here ever raises into your
  application code. The one exception path that *does* re-raise is the host application's own
  exception inside `KaironMiddleware` — telemetry capture never swallows your own errors.
- No AI credential, no database setting, no remediation option — this is a collector and
  nothing more, same boundary `KaironOptions` documents on the .NET side.

## Tests

```bash
pip install -e .[test]
pytest
```

Convention: one test per failure mode proving it never throws (connection refused, DNS
failure, timeout, malformed response, 500, arbitrary transport exception), mirroring
`tests/Kairon.SDK.Tests/TelemetryClientTests.cs`.
