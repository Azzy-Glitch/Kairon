using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Kairon.SDK.Tests;

/// <summary>
/// KaironClient.CaptureException/RecordMetric - the .NET counterpart to sdk-python's
/// Kairon.capture_exception/record_metric.
///
/// Before these existed, KaironClient - documented as "for applications that are not themselves an
/// ASP.NET Core host, a worker, console app, or any process that wants Kairon telemetry without
/// wiring IServiceCollection" - had no public way to report an incident or a metric at all: Start()
/// only switches on the background PROCESS-metrics collector (CPU/memory on an interval), which
/// cannot know about a caught exception or a business-level failure the host code itself observed.
/// A worker or console app - exactly KaironClient's documented audience - had strictly less
/// capability than the ASP.NET Core path (KaironMiddleware auto-reports every request) and strictly
/// less than the Python SDK's equivalent class, which has always exposed both methods.
/// </summary>
public sealed class KaironClientDirectTelemetryTests : IDisposable
{
    private readonly CapturingServer _server = new();

    public KaironClientDirectTelemetryTests() => _server.Start();

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task CaptureExceptionDeliversAFullIncidentThroughTheRealQueueAndSender()
    {
        await using var client = new KaironClient(
            endpoint: _server.Url, projectId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            apiKey: "krn_test_key", applicationName: "worker-app", serviceName: "worker-service",
            environment: "Staging");
        client.Start();

        client.CaptureException(new InvalidOperationException("something broke"),
            endpoint: "/jobs/nightly-sync", method: "JOB", statusCode: 500, durationMs: 250);

        var incident = await _server.WaitForIncidentAsync(TimeSpan.FromSeconds(10));

        // JsonContent.Create(payload, payload.GetType()) (KaironTelemetryClient.PostAsync) uses no
        // explicit options, so System.Net.Http.Json's own default (JsonSerializerDefaults.Web)
        // applies - camelCase on the wire, exactly what the backend's own case-insensitive MVC
        // binding expects (see the SDK-backend contract audit).
        Assert.Equal("11111111-1111-1111-1111-111111111111", incident.GetProperty("projectId").GetString());
        Assert.Equal("worker-app", incident.GetProperty("applicationName").GetString());
        Assert.Equal("worker-service", incident.GetProperty("service").GetString());
        Assert.Equal("Staging", incident.GetProperty("environment").GetString());
        Assert.Equal("/jobs/nightly-sync", incident.GetProperty("endpoint").GetString());
        Assert.Equal("JOB", incident.GetProperty("method").GetString());
        Assert.Equal(500, incident.GetProperty("statusCode").GetInt32());
        Assert.Equal(250, incident.GetProperty("duration").GetInt64());
        Assert.Equal("something broke", incident.GetProperty("error").GetString());
        Assert.Contains("InvalidOperationException", incident.GetProperty("exceptionType").GetString());

        Assert.Equal("krn_test_key", _server.LastApiKeyHeader);
    }

    [Fact]
    public async Task RecordMetricDeliversAFullSampleThroughTheRealQueueAndSender()
    {
        await using var client = new KaironClient(
            endpoint: _server.Url, projectId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            apiKey: "krn_test_key", applicationName: "worker-app", serviceName: "worker-service",
            environment: "Staging");
        client.Start();

        client.RecordMetric(cpuPercent: 72.5, memoryPercent: 41.2, responseTimeMs: 88,
            requestCount: 12, errorCount: 1, retryCount: 3, queueDepth: 7, component: "ingest-worker");

        var metric = await _server.WaitForMetricAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("22222222-2222-2222-2222-222222222222", metric.GetProperty("projectId").GetString());
        Assert.Equal(72.5, metric.GetProperty("cpuPercent").GetDouble());
        Assert.Equal(41.2, metric.GetProperty("memoryPercent").GetDouble());
        Assert.Equal(12, metric.GetProperty("requestCount").GetInt64());
        Assert.Equal(1, metric.GetProperty("errorCount").GetInt64());
        Assert.Equal(3, metric.GetProperty("retryCount").GetInt64());
        Assert.Equal(7, metric.GetProperty("queueDepth").GetInt64());
        Assert.Equal("ingest-worker", metric.GetProperty("component").GetString());
        Assert.Equal("worker-app", metric.GetProperty("application").GetString());
        Assert.Equal("worker-service", metric.GetProperty("service").GetString());
    }

    /// <summary>Captures whatever POST body/headers actually arrive at api/telemetry/incidents or
    /// api/telemetry/metrics - a REAL loopback listener, not a stub, so this proves delivery through
    /// the SDK's real queue -> sender -> HttpClient -> network path, not merely that a method was
    /// called.</summary>
    private sealed class CapturingServer : IDisposable
    {
        private HttpListener _listener = new();
        private volatile bool _running;
        private readonly TaskCompletionSource<JsonElement> _incident = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<JsonElement> _metric = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Url { get; private set; } = "";
        public string? LastApiKeyHeader { get; private set; }

        public void Start()
        {
            (_listener, Url) = LoopbackListener.Claim();
            _running = true;
            _ = Task.Run(AcceptLoopAsync);
        }

        public Task<JsonElement> WaitForIncidentAsync(TimeSpan timeout) => WaitAsync(_incident.Task, timeout);
        public Task<JsonElement> WaitForMetricAsync(TimeSpan timeout) => WaitAsync(_metric.Task, timeout);

        private static async Task<JsonElement> WaitAsync(Task<JsonElement> task, TimeSpan timeout)
        {
            var completed = await Task.WhenAny(task, Task.Delay(timeout));
            if (completed != task) throw new TimeoutException("No request arrived within the allotted time.");
            return await task;
        }

        private async Task AcceptLoopAsync()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }

                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var raw = await reader.ReadToEndAsync();
                LastApiKeyHeader = ctx.Request.Headers["X-Kairon-API-Key"];

                var path = ctx.Request.Url!.AbsolutePath;
                if (path.Contains("incidents"))
                    _incident.TrySetResult(JsonDocument.Parse(raw).RootElement.Clone());
                else if (path.Contains("metrics"))
                    _metric.TrySetResult(JsonDocument.Parse(raw).RootElement.Clone());

                var body = "{\"success\":true}"u8.ToArray();
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = body.Length;
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.Close();
            }
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }
}
