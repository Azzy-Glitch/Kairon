# KAIRON .NET SDK

Optional deep instrumentation for ASP.NET Core applications. Basic monitoring remains available
through the KAIRON Agent without this package.

```csharp
builder.Services.AddAIDIP(options =>
{
    options.Endpoint = "http://127.0.0.1:8000";
    options.ProjectId = Guid.Parse("YOUR_PROJECT_ID");
    options.ApiKey = "YOUR_KAIRON_TELEMETRY_KEY";
    options.ApplicationName = "orders-api";
    options.Environment = "Development";
});

app.UseAIDIP();
```

Delivery is asynchronous, bounded, timeout-limited, and fail-open. The SDK never accesses the
KAIRON database and contains no AI provider or remediation logic.
