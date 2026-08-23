using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using AIDIP.SDK.Models;

namespace AIDIP.SDK;

public class AIDIPMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AIDIPTelemetryClient _telemetry;
    private readonly AIDIPOptions _options;

    public AIDIPMiddleware(
        RequestDelegate next,
        AIDIPTelemetryClient telemetry,
        IOptions<AIDIPOptions> options)
    {
        _next = next;
        _telemetry = telemetry;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        Exception? exception = null;

        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            exception = ex;
            throw;
        }
        finally
        {
            stopwatch.Stop();

            if (_options.EnableTelemetry)
            {
                var payload = new TelemetryPayload
                {
                    ProjectId = _options.ProjectId,
                    ApplicationName = Environment.GetEnvironmentVariable("AIDIP_APPLICATION_NAME") ?? "UnknownApplication",
                    Environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
                    Method = context.Request.Method,
                    Endpoint = context.Request.Path,
                    StatusCode = exception != null
                        ? StatusCodes.Status500InternalServerError
                        : context.Response.StatusCode,
                    Duration = stopwatch.ElapsedMilliseconds,
                    Error = exception?.Message,
                    ExceptionType = exception?.GetType().FullName,
                    StackTrace = exception?.StackTrace,
                    Timestamp = DateTime.UtcNow
                };

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _telemetry.SendAsync(payload);
                    }
                    catch
                    {
                        // swallow – telemetry must never affect the host
                    }
                });
            }
        }
    }
}