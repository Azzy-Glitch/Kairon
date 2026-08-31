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
    options.Endpoint = "http://localhost:8000";
    options.ProjectId = Guid.Parse("...");
    options.ServiceName = "OrderProcessingService";
});

// ...

app.UseKairon();
```

`AddKairon` registers the telemetry queue, background sender, and process metrics collector.
`UseKairon` adds the middleware that captures every request: status code, duration, and
exceptions.

### Options

The most commonly set fields on `KaironOptions`:

| Property | Default | Purpose |
|---|---|---|
| `Endpoint` | `http://localhost:8000` | Base URL of the Kairon backend. |
| `ApiKey` | `null` | Project credential, if the backend requires one. |
| `ProjectId` | — | The Kairon project this application reports to. |
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

## Failure behavior

The SDK never turns a Kairon outage into an application outage. Every send is bounded, timed
out, and cancellation-aware; a failure returns a result rather than throwing. The outbound queue
is bounded (`QueueCapacity`, default 1000) and drops the oldest item under pressure rather than
growing or blocking the host application.

## Requirements

Targets `net10.0` and references `Microsoft.AspNetCore.App` — for use in an ASP.NET Core host.
