using System.Net;
using Kairon.UserAgent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kairon.UserAgent.Tests;

/// <summary>RB-004: KAIRON.UserAgent must reject a remote plaintext endpoint (and one embedding
/// credentials) exactly like the SDKs and Kairon.Agent do, at the actual transport boundary.</summary>
public sealed class AgentTransportSecurityTests
{
    [Theory]
    [InlineData("https://backend.example")]
    [InlineData("https://backend.example:8443")]
    [InlineData("http://localhost:8000")]
    [InlineData("http://127.0.0.1:8000")]
    [InlineData("http://[::1]:8000")]
    public void AllowedEndpointsAreAccepted(string endpoint) =>
        Assert.True(AgentEndpointSecurity.IsAllowed(endpoint));

    [Theory]
    [InlineData("http://remote-host:8000")]
    [InlineData("http://example.com")]
    [InlineData("https://user:pass@backend.example")]
    [InlineData("http://user:pass@127.0.0.1")]
    [InlineData("not-a-url")]
    [InlineData("ftp://backend.example")]
    [InlineData(null)]
    [InlineData("")]
    public void DisallowedEndpointsAreRejected(string? endpoint) =>
        Assert.False(AgentEndpointSecurity.IsAllowed(endpoint));

    [Fact]
    public void EnsureAllowedThrowsForARemoteHttpEndpoint() =>
        Assert.Throws<InvalidOperationException>(() => AgentEndpointSecurity.EnsureAllowed("http://remote-host"));

    [Fact]
    public void EnsureAllowedDoesNotThrowForAnAllowedEndpoint() =>
        AgentEndpointSecurity.EnsureAllowed("https://backend.example");

    [Fact]
    public async Task HeartbeatOverARemoteHttpBaseAddressIsRefusedAndNeverSent()
    {
        // PollAndSendAsync runs once, immediately, before this BackgroundService ever waits on
        // its PeriodicTimer - so a short cancellation window after starting it is enough to
        // observe exactly one attempted cycle without needing to wait out a real heartbeat
        // interval.
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var handler = new CountingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://remote-host:8000/") };
        var service = new SessionProcessCollector(http, Options.Create(new UserAgentOptions
        {
            AgentKey = "unit-test-agent-key",
            HeartbeatIntervalSeconds = 3
        }), NullLogger<SessionProcessCollector>.Instance);

        await service.StartAsync(stop.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, handler.Calls);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
