using System.Net;
using System.Text;
using System.Text.Json;
using Kairon.SDK;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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
    public async Task AddKaironMiddlewareUsesTheSameNormalizedSenderWithoutExtraUserConfiguration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKairon(options =>
        {
            options.Endpoint = _server.Url;
            options.ProjectId = Guid.Parse("33333333-3333-3333-3333-333333333333");
            options.ApiKey = "krn_test_key";
            options.ApplicationName = "aspnet-app";
            options.ServiceName = "aspnet-service";
            options.Environment = "Development";
            options.EnableMetrics = false;
        });
        await using var provider = services.BuildServiceProvider();
        var sender = Assert.Single(provider.GetServices<IHostedService>().OfType<KaironTelemetrySender>());
        await sender.StartAsync(default);
        try
        {
            var middleware = new KaironMiddleware(context =>
            {
                context.Response.StatusCode = 503;
                return Task.CompletedTask;
            }, provider.GetRequiredService<IKaironTelemetryQueue>(),
                provider.GetRequiredService<IKaironMetrics>(),
                provider.GetRequiredService<IOptions<KaironOptions>>());
            var context = new DefaultHttpContext();
            context.Request.Path = "/orders";
            context.Request.Method = "GET";
            context.Response.Body = new MemoryStream();
            await middleware.InvokeAsync(context);

            var item = await _server.WaitForIncidentAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("dotnet-sdk", item.GetProperty("source").GetString());
            Assert.Equal("33333333-3333-3333-3333-333333333333", item.GetProperty("projectId").GetString());
            Assert.Equal("aspnet-service", item.GetProperty("service").GetString());
            Assert.Equal(503, item.GetProperty("httpContext").GetProperty("statusCode").GetInt32());
            Assert.False(item.TryGetProperty("machineId", out _));
        }
        finally { await sender.StopAsync(CancellationToken.None); }
    }

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

        // The normal queue/sender path uses the backend's normalized, idempotent event contract.
        Assert.Equal("http", incident.GetProperty("eventType").GetString());
        Assert.Equal("dotnet-sdk", incident.GetProperty("source").GetString());
        Assert.NotEqual(Guid.Empty, incident.GetProperty("eventId").GetGuid());
        Assert.Equal("11111111-1111-1111-1111-111111111111", incident.GetProperty("projectId").GetString());
        Assert.Equal("worker-app", incident.GetProperty("application").GetString());
        Assert.Equal("worker-service", incident.GetProperty("service").GetString());
        Assert.Equal("Staging", incident.GetProperty("environment").GetString());
        var http = incident.GetProperty("httpContext");
        Assert.Equal("/jobs/nightly-sync", http.GetProperty("endpoint").GetString());
        Assert.Equal("JOB", http.GetProperty("method").GetString());
        Assert.Equal(500, http.GetProperty("statusCode").GetInt32());
        Assert.Equal(250, http.GetProperty("durationMs").GetInt64());
        Assert.Equal("something broke", incident.GetProperty("message").GetString());
        Assert.Contains("InvalidOperationException", incident.GetProperty("exceptionType").GetString());
        Assert.False(incident.TryGetProperty("machineId", out _));

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

        Assert.Equal("metric", metric.GetProperty("eventType").GetString());
        Assert.Equal("22222222-2222-2222-2222-222222222222", metric.GetProperty("projectId").GetString());
        var values = metric.GetProperty("resourceMetrics");
        Assert.Equal(72.5, values.GetProperty("cpuPercent").GetDouble());
        Assert.Equal(41.2, values.GetProperty("memoryPercent").GetDouble());
        Assert.Equal(12, values.GetProperty("requestCount").GetInt64());
        Assert.Equal(1, values.GetProperty("errorCount").GetInt64());
        Assert.Equal(3, values.GetProperty("retryCount").GetInt64());
        Assert.Equal(7, values.GetProperty("queueDepth").GetInt64());
        Assert.Equal("worker-app", metric.GetProperty("application").GetString());
        Assert.Equal("worker-service", metric.GetProperty("service").GetString());
        Assert.False(metric.TryGetProperty("machineId", out _));
    }

    /// <summary>Captures normalized event POSTs - a REAL loopback listener, not a stub, proving
    /// delivery through
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
                if (path.Equals("/api/v1/telemetry/events", StringComparison.Ordinal))
                {
                    using var document = JsonDocument.Parse(raw);
                    var events = document.RootElement.GetProperty("events");
                    Assert.Single(events.EnumerateArray());
                    var item = events[0].Clone();
                    if (item.GetProperty("eventType").GetString() == "metric") _metric.TrySetResult(item);
                    else _incident.TrySetResult(item);
                }

                var body = "{\"accepted\":1,\"duplicates\":0,\"rejected\":0}"u8.ToArray();
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
