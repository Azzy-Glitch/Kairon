// A deliberately tiny local dependency that an application can call. POST /wedge puts it into a
// degraded state (slow 503 responses) that only a process restart clears, so a KAIRON
// RestartService remediation has a real, observable effect. Loopback only; no persistent state.
var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "KaironScmTest");
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("KAIRON_SCM_TEST_URL") ?? "http://127.0.0.1:18080");

var app = builder.Build();
var wedged = 0;

app.MapGet("/health", () => Results.Ok(new { status = Volatile.Read(ref wedged) == 1 ? "wedged" : "ok" }));
app.MapGet("/work", async () =>
{
    if (Volatile.Read(ref wedged) == 0) return Results.Ok(new { status = "ok" });
    await Task.Delay(1500);
    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});
app.MapPost("/wedge", () =>
{
    Interlocked.Exchange(ref wedged, 1);
    return Results.Ok(new { status = "wedged" });
});

app.Run();
