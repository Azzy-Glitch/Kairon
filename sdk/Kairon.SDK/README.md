# Kairon.SDK

Telemetry and observability SDK for .NET applications monitored by Kairon. Two lines of
integration in a host ASP.NET app capture request/response telemetry and exceptions
automatically; nothing else about how the application runs changes.

## Install

```
dotnet add package Kairon.SDK
```

## Simple mode vs advanced mode

**Simple mode - a pairing code only.** The operator generates a pairing code in Kairon and picks
the environment (Development/Staging/Production) and service there. The application supplies just
that code; the SDK redeems it once, stores the credential together with those choices, and uses
them as the default `Environment` and application/service name. Leaving the code in configuration
is safe: a restart (or several processes started with the same code) recognises the stored
credential issued for that code (only a SHA-256 hash of the code is stored) and reuses it instead
of redeeming the spent code again.

**Advanced mode - explicit configuration.** Set `Endpoint`/`ProjectId`/`ApiKey` (or
`KAIRON_ENDPOINT`/`KAIRON_PROJECT_ID`/`KAIRON_API_KEY`) for CI/CD or containers, and/or set
`ApplicationName`, `ServiceName` and `Environment`. Precedence matches the Python SDK:
explicit options, then `KAIRON_ENVIRONMENT` / `KAIRON_APPLICATION_NAME`, then the operator's
pairing defaults, and only then the host's ambient `ASPNETCORE_ENVIRONMENT`/`DOTNET_ENVIRONMENT`
or entry-assembly name. (The ambient ASP.NET environment no longer overrides the environment an
operator chose for the pairing code, which would otherwise mismatch its remediation target.)

Pairing errors say what happened: HTTP 429 means pairing is rate limited and the code was **not**
consumed (retry shortly with the same code); HTTP 400 means the code is invalid, expired, already
used, revoked, or was generated for a different SDK type (generate a new one).

## Usage

```csharp
builder.Services.AddKairon(); // Reuses the protected connection after pairing.

// ...

app.UseKairon();
```

`AddKairon` registers the telemetry queue, background sender, and process metrics collector.
`UseKairon` adds the middleware that captures every request: status code, duration, and
exceptions.

The normal background sender posts idempotent normalized events to
`/api/v1/telemetry/events`, batching up to 25 queued events per request (within the backend's
200-event / 1 MiB bound) and backing off on HTTP 429 for the server's `Retry-After` (capped at
30 seconds) before resending the same event IDs. When an enrolled Kairon Agent is running on the
same Windows machine, the SDK automatically obtains a short-lived proof for the exact batch body;
a proof is only attached to a batch with one project, service and environment. The Agent
confirms it with its separate credential, and only the backend may assign `MachineId`. If the
backend rejects a proof, the same batch is resent once without it, so telemetry is never lost to
a proof failure - only a 401 without a proof is reported as "project authentication rejected". An
unbound app can still send telemetry, but it cannot authorize Windows remediation. Developers
do not configure a machine ID or call a remediation API. The older direct
`KaironTelemetryClient.SendAsync`/`SendMetricAsync` methods remain available and use the same
backend-authoritative proof mechanism on the legacy ingestion routes.

On a first run, explicitly opt into asynchronous pairing before building the application:

```csharp
var pairingCode = Environment.GetEnvironmentVariable("KAIRON_PAIRING_CODE");
if (!string.IsNullOrWhiteSpace(pairingCode))
{
    await builder.Services.AddKaironAsync(pairingCode);
}
else
{
    builder.Services.AddKairon();
}
```

`AddKaironAsync` redeems, confirms and stores the pairing result. `AddKairon` performs no
network pairing; when endpoint/project/key are omitted it loads that same stored connection as
one unit. Existing complete explicit `Endpoint` + `ProjectId` + `ApiKey` configuration remains
supported and authoritative. A partial project/key configuration is rejected rather than mixed
with stored values.

`UseKairon()` remains explicit because its position in the ASP.NET Core pipeline is an application
decision: place it before the endpoints you want to observe. Queue delivery and process metrics
are hosted services, so the ASP.NET host starts and stops them automatically. Application and
service names default to the entry assembly; use the optional configuration callback only when you
want custom labels, remote first-contact HTTPS, sampling or other advanced options.

### Options

The most commonly set fields on `KaironOptions`:

| Property | Default | Purpose |
|---|---|---|
| `Endpoint` | `http://localhost:8000` | Base URL of the Kairon backend. |
| `ApiKey` | `null` | Project credential, if the backend requires one. |
| `ProjectId` | — | The Kairon project this application reports to. |
| `CredentialPath` | `%LOCALAPPDATA%/Kairon/sdk/credential-dotnet.json` | Optional override for the protected pairing record. A `credential.json` written there by an earlier .NET SDK is adopted automatically; a foreign-format file (e.g. the Python SDK's) is ignored. |
| `ApplicationName` / `ServiceName` | pairing service, else entry assembly name | Identifies this app/service in Kairon. |
| `Environment` | pairing environment, else `Production` | e.g. `"Production"`, `"Staging"`. |
| `EnableTelemetry` / `EnableMetrics` | `true` | Toggle request telemetry and process metrics independently. |
| `SuccessSampleRate` | `1.0` | Fraction of successful requests reported; errors are always reported. |
| `IgnoredPathPrefixes` | `/health`, `/healthz`, `/metrics`, `/favicon.ico` | Paths never instrumented. |

See `KaironOptions.cs` for the complete list, including queue capacity, timeouts, and body
capture limits.

### Reporting application-known signals

Two signals only the host application can know are reported through `IKaironMetrics`, resolved
from DI:

```csharp
public class OrderWorker(IKaironMetrics metrics)
{
    public void OnRetry() => metrics.RecordRetries(1);
    public void OnQueueChanged(int depth) => metrics.ReportQueueDepth(depth);
}
```

## Standalone use (workers, console apps, anything that isn't ASP.NET Core)

`KaironClient` is the non-DI entry point - no `IServiceCollection` required:

```csharp
using Kairon.SDK;

// Redeems a one-time pairing code (from the Kairon UI), persists the resulting project
// credential, and starts the background process-metrics collector.
var kairon = new KaironClient(pairingCode: "YOUR_PAIRING_CODE");
kairon.Start();
```

A later run of the same application with the same `configPath` (default:
`%LOCALAPPDATA%/Kairon/sdk/credential-dotnet.json`) reuses the stored credential automatically - no
pairing code needed again. To connect against a remote or cloud Kairon backend instead of this
machine's own, set `KAIRON_ENDPOINT` (or pass `endpoint:`) to that backend's HTTPS address
*before* pairing: the pairing call itself needs to reach the right backend, since a bare
`new KaironClient(pairingCode: "...")` otherwise tries `http://localhost:8000`. Explicit
`projectId`/`apiKey`/`endpoint` arguments (or `KAIRON_PROJECT_ID`/`KAIRON_API_KEY`/`KAIRON_ENDPOINT`)
remain fully supported in place of a pairing code, for CI/CD or containers.

`KaironClient.Start()` only switches on automatic process-metrics collection (CPU/memory every five
seconds by default) - it has no HTTP request to instrument on its own. Report anything else the
host code itself observes directly.

Behaviour shared with the Python SDK:

- **CPU.** CPU is this process's CPU as a share of the CPUs it may use (`cpu.scope = process`),
  never machine-wide CPU.
- **Process identity.** Metric events carry this process's id, working directory and executable,
  so KAIRON can offer *Restart the application* after approval. The restart is performed by the
  KAIRON UserAgent, never the SDK.
- **Credentials.** Each application keeps its own encrypted credential file, keyed by working
  directory plus service/application name, so pairing one .NET app never replaces another's. An
  existing shared `credential-dotnet.json` is adopted once and left in place. `CredentialPath`
  still overrides this.
- **Error text.** Exception messages, stack traces and endpoints are masked for bearer tokens,
  key/secret/password values and KAIRON keys/pairing codes before they are queued.
- **Delivery.** `DeliveryAttempts` (default 3) applies to transient failures; a rate limit honours
  `Retry-After`. `ShutdownTimeoutSeconds` (default 5) bounds the shutdown drain.

Report anything else the host code itself observes directly:

```csharp
try { ProcessOrder(order); }
catch (Exception ex)
{
    kairon.CaptureException(ex, endpoint: "/jobs/order-processing", method: "JOB", statusCode: 500);
}

kairon.RecordMetric(queueDepth: queue.Count, component: "order-worker");
```

Both are non-blocking, bounded, and never throw - the same fail-open contract every other
telemetry path in this SDK follows.

## Redirect and transport safety

Pairing, confirmation and telemetry never follow an HTTP redirect: every `HttpClient` this SDK
constructs internally uses a handler with automatic redirect-following disabled, so a compromised
or misconfigured backend cannot redirect one of those requests - and the credentials on it - onto
a different origin merely by answering with a 3xx. Plain HTTP is accepted only to a loopback
address (`localhost`/`127.0.0.0/8`/`::1`); anywhere else requires HTTPS, checked at every point an
endpoint can enter the SDK (explicit configuration, a pairing response's own returned endpoint, the
actual send itself) rather than once at startup.

## Failure behavior

The SDK never turns a Kairon outage into an application outage. Every send is bounded, timed
out, and cancellation-aware; a failure returns a result rather than throwing. The outbound queue
is bounded (`QueueCapacity`, default 1000) and drops the oldest item under pressure rather than
growing or blocking the host application.

## Delivery diagnostics and shutdown

Resolve `IKaironTelemetryQueue` to inspect lifetime `DeliveredCount`, `FailedCount`,
and `DroppedCount`. `PendingCount` excludes an in-flight send. `FlushAsync(token)` waits
for queued and in-flight items, bounded by five seconds or the supplied cancellation,
whichever is earlier. It returns false after any lifetime failure/drop or on timeout.
Stop request producers before calling it for a final result. The hosted sender closes
its queue and attempts a five-second drain during shutdown, then cancels outstanding I/O.

Delivery remains best effort, without durable storage. Normalized batches are resent only in
bounded ways (one transient retry, one unscoped resend after a rejected proof, one resend after a
429 back-off) with the same event IDs, so the backend counts a repeat as a duplicate; a 429
back-off is skipped during shutdown so the drain stays bounded. An ambiguous timeout is never
retried against the legacy endpoints, which have no idempotency key.
Monitor the counters; queue emptiness is not proof of collector acceptance. Custom
`IKaironTelemetryQueue` implementations must implement the added diagnostic members.

## Requirements

Targets `net10.0` and references `Microsoft.AspNetCore.App` — for use in an ASP.NET Core host.
