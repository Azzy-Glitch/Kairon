using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kairon.SDK.Tests;

/// <summary>
/// Automatic signals with no application code (parity with the Python SDK): requests in progress as
/// queue depth, retries of failed outgoing HTTP calls, and per-app settings fetched from KAIRON.
/// </summary>
public sealed class AutoSignalsTests
{
    [Fact]
    public void QueueDepthIsThePeakOfRequestsInProgress()
    {
        var metrics = new KaironMetrics(new KaironAutoSignals());

        metrics.BeginRequest(); metrics.BeginRequest(); metrics.BeginRequest();
        metrics.EndRequest(); metrics.EndRequest();

        Assert.Equal(3, metrics.Drain().QueueDepth);   // the peak since the last sample
        Assert.Equal(1, metrics.Drain().QueueDepth);   // still in progress
        metrics.EndRequest(); metrics.EndRequest();     // never below zero
        Assert.Equal(1, metrics.Drain().QueueDepth);
        Assert.Equal(0, metrics.Drain().QueueDepth);
    }

    [Fact]
    public void AnApplicationReportedValueWinsAndSwitchedOffSignalsAreAbsent()
    {
        var signals = new KaironAutoSignals();
        var metrics = new KaironMetrics(signals);
        metrics.BeginRequest();
        metrics.ReportQueueDepth(40);
        Assert.Equal(40, metrics.Drain().QueueDepth);

        var off = new KaironMetrics(new KaironAutoSignals { AutoQueueDepth = false, AutoRetries = false });
        off.BeginRequest();
        var snapshot = off.Drain();
        Assert.Null(snapshot.QueueDepth);
        Assert.False(snapshot.RetriesTracked);
        Assert.Null(new KaironMetrics().Drain().QueueDepth); // no automatic signals configured at all
    }

    [Fact]
    public void ARepeatOfARecentlyFailedCallIsARetry()
    {
        var signals = new KaironAutoSignals();
        var metrics = new KaironMetrics(signals) { OutgoingCallsObserved = true };
        var observer = new KaironOutgoingCallObserver(metrics, signals, "http://localhost:8000");
        var pay = new Uri("https://payments.example/charge?order=1");

        observer.Observe(pay, "POST", 503, faulted: false);
        observer.Observe(pay, "POST", null, faulted: true);   // retry 1 (fails again)
        observer.Observe(pay, "POST", 200, faulted: false);   // retry 2 (succeeds)
        observer.Observe(pay, "POST", 200, faulted: false);   // ordinary call
        observer.Observe(new Uri("https://payments.example/refund"), "POST", 200, faulted: false);
        observer.Observe(new Uri("http://localhost:8000/api/v1/telemetry/events"), "POST", 503, faulted: false);
        observer.Observe(new Uri("http://localhost:8000/api/v1/telemetry/events"), "POST", 200, faulted: false); // KAIRON's own

        var snapshot = metrics.Drain();
        Assert.Equal(2, snapshot.Retries);
        Assert.True(snapshot.RetriesTracked);
        Assert.Equal(0, metrics.Drain().Retries);
    }

    [Fact]
    public async Task ARealHttpClientRetryLoopIsCountedThroughDiagnostics()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var failures = 2;
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                using var socket = await listener.AcceptTcpClientAsync();
                var stream = socket.GetStream();
                var buffer = new byte[4096];
                _ = await stream.ReadAsync(buffer);
                var status = failures-- > 0 ? "503 Service Unavailable" : "200 OK";
                var reply = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                await stream.WriteAsync(reply);
            }
        });

        var signals = new KaironAutoSignals();
        var metrics = new KaironMetrics(signals);
        using var observer = new KaironOutgoingCallObserver(metrics, signals, "http://localhost:8000");
        observer.Start();

        using var http = new HttpClient();
        HttpStatusCode last = 0;
        for (var attempt = 0; attempt < 5 && last != HttpStatusCode.OK; attempt++)
            last = (await http.GetAsync($"http://127.0.0.1:{port}/pay")).StatusCode;
        await server;

        Assert.Equal(HttpStatusCode.OK, last);
        Assert.Equal(2, metrics.Drain().Retries);
    }

    [Fact]
    public async Task SettingsFromTheDesktopAreFetchedWithTheAppsOwnKey()
    {
        HttpRequestMessage? seen = null;
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            seen = request;
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"autoQueueDepth\":false,\"autoRetries\":false,\"retryWindowSeconds\":30}",
                    Encoding.UTF8, "application/json")
            };
        });
        var projectId = Guid.NewGuid();
        var client = new KaironTelemetryClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000/") },
            Options.Create(new KaironOptions { ProjectId = projectId, ApiKey = "krn_private-sdk-key", TimeoutSeconds = 5 }));
        var signals = new KaironAutoSignals();

        Assert.True(await client.RefreshAutoSignalsAsync(signals));

        Assert.Equal("/api/v1/sdk/settings", seen!.RequestUri!.AbsolutePath);
        Assert.Equal("krn_private-sdk-key", seen.Headers.GetValues("X-Kairon-API-Key").Single());
        Assert.Contains(projectId.ToString(), body);
        Assert.False(signals.AutoQueueDepth);
        Assert.False(signals.AutoRetries);
        Assert.Equal(TimeSpan.FromSeconds(30), signals.RetryWindow);
    }

    [Fact]
    public async Task AnUnreachableKaironLeavesEverythingOn()
    {
        var client = new KaironTelemetryClient(
            new HttpClient(new StubHandler(_ => throw new HttpRequestException("down"))) { BaseAddress = new Uri("http://localhost:8000/") },
            Options.Create(new KaironOptions { ProjectId = Guid.NewGuid(), ApiKey = "krn_private-sdk-key", TimeoutSeconds = 5 }));
        var signals = new KaironAutoSignals();

        Assert.False(await client.RefreshAutoSignalsAsync(signals));
        Assert.True(signals.AutoQueueDepth);
        Assert.True(signals.AutoRetries);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }
}
