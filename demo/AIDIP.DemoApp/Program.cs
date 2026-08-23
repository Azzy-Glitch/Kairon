using AIDIP.SDK;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAIDIP(options =>
{
    options.Endpoint = builder.Configuration["AIDIP:Endpoint"] ?? "http://localhost:8000";
    options.ApiKey = builder.Configuration["AIDIP:ApiKey"];
    options.ProjectId = Guid.TryParse(builder.Configuration["AIDIP:ProjectId"], out var id) ? id : Guid.Empty;
});

var app = builder.Build();

// AIDIP instruments every request from here down — this is the only wiring
// a customer app needs, per the build plan's "small, documented setup" goal.
app.UseAIDIP();

app.MapGet("/", () => Results.Ok(new
{
    status = "AIDIP demo app running",
    routes = new[] { "/api/products", "/api/failure", "/api/slow" },
    note = "Stop the AIDIP backend (port 8000) and call these again — this app keeps working; only telemetry delivery fails, silently."
}));

// UC-01: successful request — SDK captures status + duration, no incident.
app.MapGet("/api/products", () => Results.Ok(new[]
{
    new { id = 1, name = "Widget" },
    new { id = 2, name = "Gadget" }
}));

// UC-02: real exception — SDK middleware captures it as an incident before
// it propagates, then the AIDIP backend can run it through AI diagnosis.
app.MapGet("/api/failure", () =>
{
    throw new InvalidOperationException("Customer record was null while processing order #4471");
});

// UC-03: slow endpoint — SDK captures the duration as a performance signal.
app.MapGet("/api/slow", async () =>
{
    await Task.Delay(2500);
    return Results.Ok(new { status = "completed", delayMs = 2500 });
});

app.Run();
