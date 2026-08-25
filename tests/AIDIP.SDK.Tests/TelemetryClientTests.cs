using System.Net;
using AIDIP.SDK;
using AIDIP.SDK.Models;
using Microsoft.Extensions.Options;
using Xunit;

namespace AIDIP.SDK.Tests;

/// <summary>
/// The SDK's core promise (PRD section 17): "The SDK must not turn an AIDIP outage into an
/// application outage." Every test here is a way the collector could fail, and every one of them
/// must end in a returned result rather than a thrown exception.
/// </summary>
public class TelemetryClientTests
{
    private static AIDIPTelemetryClient CreateClient(
        HttpMessageHandler handler,
        Action<AIDIPOptions>? configure = null)
    {
        var options = new AIDIPOptions
        {
            Endpoint = "http://localhost:8000",
            ProjectId = Guid.NewGuid(),
            TimeoutSeconds = 2
        };

        configure?.Invoke(options);

        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(10)
        };

        return new AIDIPTelemetryClient(http, Options.Create(options));
    }

    private static TelemetryPayload Payload() => new()
    {
        ApplicationName = "TestApp",
        Service = "TestService",
        Endpoint = "/api/orders",
        Method = "POST",
        StatusCode = 500,
        Duration = 1200,
        Error = "boom"
    };

    [Fact]
    public async Task SuccessfulSendReturnsTheServerResponse()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"success":true,"message":"Telemetry recorded.","telemetryId":"abc"}""");

        var response = await CreateClient(handler).SendAsync(Payload());

        Assert.NotNull(response);
        Assert.True(response!.Success);
        Assert.Equal("abc", response.TelemetryId);
    }

    [Fact]
    public async Task ProjectIdIsAlwaysStampedFromOptions()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"success":true}""");
        var projectId = Guid.NewGuid();

        var payload = Payload();
        payload.ProjectId = Guid.Empty;

        await CreateClient(handler, o => o.ProjectId = projectId).SendAsync(payload);

        Assert.Equal(projectId, payload.ProjectId);
    }

    [Fact]
    public async Task ApiKeyIsSentAsAHeaderWhenConfigured()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"success":true}""");

        await CreateClient(handler, o => o.ApiKey = "platform-key").SendAsync(Payload());

        Assert.True(handler.LastRequest!.Headers.Contains("X-AIDIP-API-Key"));
    }

    [Fact]
    public async Task NoApiKeyHeaderWhenNoneIsConfigured()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"success":true}""");

        await CreateClient(handler, o => o.ApiKey = null).SendAsync(Payload());

        Assert.False(handler.LastRequest!.Headers.Contains("X-AIDIP-API-Key"));
    }

    [Fact]
    public async Task ServerErrorIsReportedNotThrown()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, "boom");

        var response = await CreateClient(handler).SendAsync(Payload());

        Assert.NotNull(response);
        Assert.False(response!.Success);
        Assert.Contains("500", response.Message);
    }

    [Fact]
    public async Task BackendUnreachableIsReportedNotThrown()
    {
        // The headline case: AIDIP is down and the host application must not notice.
        var handler = new ThrowingHandler(new HttpRequestException("Connection refused"));

        var response = await CreateClient(handler).SendAsync(Payload());

        Assert.NotNull(response);
        Assert.False(response!.Success);
        Assert.Contains("Unable to send telemetry", response.Message);
    }

    [Fact]
    public async Task TimeoutIsReportedNotThrown()
    {
        var handler = new SlowHandler(TimeSpan.FromSeconds(5));

        var response = await CreateClient(handler, o => o.TimeoutSeconds = 1).SendAsync(Payload());

        Assert.NotNull(response);
        Assert.False(response!.Success);
        Assert.Contains("timed out", response.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TimeoutIsBoundedEvenWithNoCallerCancellation()
    {
        // A caller passing CancellationToken.None must still not be held indefinitely.
        var handler = new SlowHandler(TimeSpan.FromSeconds(10));
        var started = DateTime.UtcNow;

        await CreateClient(handler, o => o.TimeoutSeconds = 1).SendAsync(Payload(), CancellationToken.None);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CallerCancellationIsReportedNotThrown()
    {
        var handler = new SlowHandler(TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var response = await CreateClient(handler).SendAsync(Payload(), cts.Token);

        Assert.NotNull(response);
        Assert.False(response!.Success);
        Assert.Contains("cancelled", response.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnUnparseableSuccessBodyIsStillTreatedAsDelivered()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "not json at all");

        var response = await CreateClient(handler).SendAsync(Payload());

        Assert.NotNull(response);
        Assert.True(response!.Success);
    }

    [Fact]
    public async Task DisablingTelemetrySkipsTheRequestEntirely()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"success":true}""");

        var response = await CreateClient(handler, o => o.EnableTelemetry = false).SendAsync(Payload());

        Assert.Null(response);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task MetricsAreSentToTheMetricsEndpoint()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"success":true}""");

        await CreateClient(handler).SendMetricAsync(new MetricPayload { CpuPercent = 94 });

        Assert.Contains("api/telemetry/metrics", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task MetricFailuresAreAlsoNonThrowing()
    {
        var handler = new ThrowingHandler(new HttpRequestException("down"));

        var response = await CreateClient(handler).SendMetricAsync(new MetricPayload());

        Assert.NotNull(response);
        Assert.False(response!.Success);
    }

    /// <summary>
    /// Belt and braces: a handler that throws something unusual must still not escape the client.
    /// </summary>
    [Fact]
    public async Task AnUnexpectedTransportFaultIsContained()
    {
        var handler = new ThrowingHandler(new InvalidOperationException("something very odd"));

        var response = await CreateClient(handler).SendAsync(Payload());

        Assert.NotNull(response);
        Assert.False(response!.Success);
    }

    // --- Test doubles ---

    private class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public HttpRequestMessage? LastRequest { get; private set; }

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw _exception;
    }

    private class SlowHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;

        public SlowHandler(TimeSpan delay) => _delay = delay;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(_delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"success":true}""")
            };
        }
    }
}
