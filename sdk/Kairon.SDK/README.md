# Kairon.SDK

Telemetry and observability SDK for .NET applications monitored by Kairon. Two lines of
integration in a host ASP.NET app capture request/response telemetry and exceptions
automatically; nothing else about how the application runs changes.

## Install

```
dotnet add package Kairon.SDK
```

## Usage

```csharp
builder.Services.AddKairon(options =>
{
    // Endpoint, ProjectId and ApiKey are loaded from the protected credential created by pairing.
    options.ServiceName = "OrderProcessingService";
});

// ...

app.UseKairon();
```

`AddKairon` registers the telemetry queue, background sender, and process metrics collector.
`UseKairon` adds the middleware that captures every request: status code, duration, and
exceptions.

On a first run, explicitly opt into asynchronous pairing before building the application:

```csharp
var pairingCode = Environment.GetEnvironmentVariable("KAIRON_PAIRING_CODE");
if (!string.IsNullOrWhiteSpace(pairingCode))
{
    await builder.Services.AddKaironAsync(pairingCode, options =>
    {
        options.ApplicationName = "OrdersApp";
        options.ServiceName = "OrderProcessingService";
        // For remote/cloud first contact only:
        // options.Endpoint = "https://your-kairon-server.example.com";
    });
}
else
{
    builder.Services.AddKairon(options =>
    {
        options.ApplicationName = "OrdersApp";
        options.ServiceName = "OrderProcessingService";
    });
}
```

`AddKaironAsync` redeems, confirms and stores the pairing result. `AddKairon` performs no
network pairing; when endpoint/project/key are omitted it loads that same stored connection as
one unit. Existing complete explicit `Endpoint` + `ProjectId` + `ApiKey` configuration remains
supported and authoritative. A partial project/key configuration is rejected rather than mixed
with stored values.

### Options

The most commonly set fields on `KaironOptions`:

| Property | Default | Purpose |
|---|---|---|
| `Endpoint` | `http://localhost:8000` | Base URL of the Kairon backend. |
| `ApiKey` | `null` | Project credential, if the backend requires one. |
| `ProjectId` | — | The Kairon project this application reports to. |
| `CredentialPath` | `%LOCALAPPDATA%/Kairon/sdk/credential.json` | Optional override for the protected pairing record. |
| `ApplicationName` / `ServiceName` | entry assembly name | Identifies this app/service in Kairon. |
| `Environment` | `null` | e.g. `"Production"`, `"Staging"`. |
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
`%LOCALAPPDATA%/Kairon/sdk/credential.json`) reuses the stored credential automatically - no
pairing code needed again. To connect against a remote or cloud Kairon backend instead of this
machine's own, set `KAIRON_ENDPOINT` (or pass `endpoint:`) to that backend's HTTPS address
*before* pairing: the pairing call itself needs to reach the right backend, since a bare
`new KaironClient(pairingCode: "...")` otherwise tries `http://localhost:8000`. Explicit
`projectId`/`apiKey`/`endpoint` arguments (or `KAIRON_PROJECT_ID`/`KAIRON_API_KEY`/`KAIRON_ENDPOINT`)
remain fully supported in place of a pairing code, for CI/CD or containers.

`KaironClient.Start()` only switches on automatic process-metrics collection (CPU/memory on an
interval) - it has no HTTP request to instrument on its own. Report anything else the host code
itself observes directly:

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

Delivery remains best effort, without durable storage or automatic retries: an ambiguous
timeout cannot safely be retried against the legacy endpoints without risking duplicates.
Monitor the counters; queue emptiness is not proof of collector acceptance. Custom
`IKaironTelemetryQueue` implementations must implement the added diagnostic members.

## Requirements

Targets `net10.0` and references `Microsoft.AspNetCore.App` — for use in an ASP.NET Core host.
