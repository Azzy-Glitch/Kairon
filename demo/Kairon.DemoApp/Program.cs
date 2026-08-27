using Kairon.DemoApp;
using Kairon.SDK;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// A real, rotating log file for the KAIRON Agent to tail (docs/OBSERVABILITY_MIGRATION.md).
// Same sink packages and dated-daily-file rolling scheme as backend/Program.cs uses, so the
// Agent's tailer only has to understand one log format across the whole solution.
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/kairon-demo-.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

builder.Services.AddKairon(options =>
{
    options.Endpoint = builder.Configuration["Kairon:Endpoint"] ?? "http://localhost:8000";
    options.ApiKey = builder.Configuration["Kairon:ApiKey"];
    options.ProjectId = Guid.TryParse(builder.Configuration["Kairon:ProjectId"], out var id) ? id : Guid.Empty;
    options.ApplicationName = "Kairon.DemoApp";
    options.ServiceName = "OrderProcessingService";
    options.Environment = "Demo";
    options.MetricsIntervalSeconds = builder.Configuration.GetValue("Kairon:MetricsIntervalSeconds", 5);
});

builder.Services.AddSingleton<DemoScenario>();
builder.Services.AddHttpClient();
builder.Services.AddHostedService<OrderTrafficGenerator>();

var app = builder.Build();

// Kairon instruments every request from here down - this is the only wiring
// a customer app needs, per the build plan's "small, documented setup" goal.
app.UseKairon();

app.MapGet("/", () => Results.Ok(new
{
    status = "Kairon demo app running",
    routes = new[]
    {
        "/api/products",
        "/api/failure",
        "/api/slow",
        "/api/orders/process",
        "/kairon-control/state"
    },
    note = "Stop the Kairon backend (port 8000) and call these again - this app keeps working; only telemetry delivery fails, silently."
}));

// UC-01: successful request - SDK captures status + duration, no incident.
app.MapGet("/api/products", () => Results.Ok(new[]
{
    new { id = 1, name = "Widget" },
    new { id = 2, name = "Gadget" }
}));

// UC-02: real exception - SDK middleware captures it as an incident before
// it propagates, then the Kairon backend can run it through AI diagnosis.
app.MapGet("/api/failure", () =>
{
    throw new InvalidOperationException("Customer record was null while processing order #4471");
});

// UC-03: slow endpoint - SDK captures the duration as a performance signal.
app.MapGet("/api/slow", async () =>
{
    await Task.Delay(2500);
    return Results.Ok(new { status = "completed", delayMs = 2500 });
});

// --- The controlled scenario (PRD section 20) ---

// The order-processing endpoint the whole demo revolves around. Healthy by default; once the
// retry loop is enabled it fails slowly and retries, which is what produces the correlated
// CPU / latency / error-rate / retry / queue signals.
app.MapPost("/api/orders/process", async (DemoScenario scenario, ILogger<Program> logger) =>
{
    var outcome = scenario.ProcessOrder();

    // The latency is simulated by actually waiting, so the duration the SDK records is real
    // rather than a number the demo asserts about itself.
    await Task.Delay(outcome.LatencyMs);

    if (outcome.Success)
        return Results.Ok(new { status = "processed", latencyMs = outcome.LatencyMs });

    // A real multi-line log entry (message + a simulated stack trace), so the KAIRON Agent's log
    // tailer has genuine repeated, multi-line content to group and deduplicate rather than a
    // single one-off line - this fires on every failed order while the retry loop is active,
    // which is exactly the volume the Agent's dedup/rate-limit exists to handle.
    logger.LogError(
        "Order processing failed: {Message}\n   at Kairon.DemoApp.DemoScenario.ProcessOrder() in DemoScenario.cs\n   at Kairon.DemoApp.Program.<>c.<<Main>$>b__0_6(DemoScenario scenario) in Program.cs:line 68",
        outcome.Message);

    return Results.Json(
        new { status = "failed", retries = outcome.Retries, message = outcome.Message },
        statusCode: StatusCodes.Status503ServiceUnavailable);
});

// --- Control API ---
//
// The closed set of commands the Kairon backend's registered remediation tools may issue. There is
// no endpoint here that takes a command string, a script, or a path: a tool can only ask for one
// of these named, fixed effects (PRD section 12).

app.MapGet("/kairon-control/state", (DemoScenario scenario) => Results.Ok(scenario.GetState()));

app.MapPost("/kairon-control/{command}", (string command, DemoScenario scenario) =>
{
    var result = scenario.Execute(command);
    return result.Success
        ? Results.Ok(result)
        : Results.BadRequest(result);
});

app.Run();

public partial class Program { }
