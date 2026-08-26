using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Kairon.SDK.Models;

namespace Kairon.SDK;

/// <summary>
/// Instruments every request. The rules it lives by (PRD section 4.1 and 17): never block the
/// pipeline, never change the response, never throw, and never let an Kairon outage become an
/// application outage. Telemetry is handed to a bounded queue and forgotten about.
/// </summary>
public class KaironMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IKaironTelemetryQueue _queue;
    private readonly IKaironMetrics _metrics;
    private readonly KaironOptions _options;

    // Deterministic-enough sampling without a shared Random: one instance per middleware, and
    // sampling only ever affects successful requests.
    private readonly Random _sampler = new();

    public KaironMiddleware(
        RequestDelegate next,
        IKaironTelemetryQueue queue,
        IKaironMetrics metrics,
        IOptions<KaironOptions> options)
    {
        _next = next;
        _queue = queue;
        _metrics = metrics;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.EnableTelemetry || IsIgnored(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        Exception? exception = null;

        string? requestBody = null;
        if (_options.CaptureRequestBody)
            requestBody = await TryReadRequestBodyAsync(context);

        var originalBodyStream = context.Response.Body;
        MemoryStream? responseBuffer = null;

        if (_options.CaptureResponseBody)
        {
            responseBuffer = new MemoryStream();
            context.Response.Body = responseBuffer;
        }

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

            string? responseBody = null;

            if (responseBuffer is not null)
            {
                try
                {
                    responseBuffer.Position = 0;
                    responseBody = Bound(await new StreamReader(responseBuffer).ReadToEndAsync());
                    responseBuffer.Position = 0;
                    await responseBuffer.CopyToAsync(originalBodyStream);
                }
                catch
                {
                    // Capturing a body is a convenience; failing to capture it must not affect
                    // the response the caller receives.
                }
                finally
                {
                    context.Response.Body = originalBodyStream;
                    await responseBuffer.DisposeAsync();
                }
            }

            try
            {
                var statusCode = exception != null
                    ? StatusCodes.Status500InternalServerError
                    : context.Response.StatusCode;

                var isError = exception != null || statusCode >= 500;

                _metrics.RecordRequest(stopwatch.ElapsedMilliseconds, isError);

                // Errors are always reported; only successes are sampled. Losing an error to
                // sampling would be the one loss that actually matters.
                if (isError || ShouldSample())
                {
                    _queue.TryEnqueue(new TelemetryPayload
                    {
                        ProjectId = _options.ProjectId,
                        ApplicationName = KaironIdentity.ResolveApplication(_options),
                        Service = KaironIdentity.ResolveService(_options),
                        Environment = KaironIdentity.ResolveEnvironment(_options),
                        Method = context.Request.Method,
                        Endpoint = context.Request.Path,
                        StatusCode = statusCode,
                        Duration = stopwatch.ElapsedMilliseconds,
                        Error = exception?.Message,
                        ExceptionType = exception?.GetType().FullName,
                        StackTrace = exception?.StackTrace,
                        RequestBody = requestBody,
                        ResponseBody = responseBody,
                        Timestamp = DateTime.UtcNow
                    });
                }
            }
            catch
            {
                // swallow - telemetry must never affect the host
            }
        }
    }

    private bool ShouldSample()
    {
        if (_options.SuccessSampleRate >= 1.0) return true;
        if (_options.SuccessSampleRate <= 0) return false;
        return _sampler.NextDouble() < _options.SuccessSampleRate;
    }

    private bool IsIgnored(PathString path)
    {
        if (!path.HasValue) return false;

        foreach (var prefix in _options.IgnoredPathPrefixes)
        {
            if (path.Value!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private async Task<string?> TryReadRequestBodyAsync(HttpContext context)
    {
        try
        {
            context.Request.EnableBuffering();

            using var reader = new StreamReader(
                context.Request.Body, Encoding.UTF8, leaveOpen: true);

            var body = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;

            return Bound(body);
        }
        catch
        {
            return null;
        }
    }

    private string? Bound(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value.Length <= _options.MaxBodyCharacters
            ? value
            : value[.._options.MaxBodyCharacters] + "...";
    }
}
