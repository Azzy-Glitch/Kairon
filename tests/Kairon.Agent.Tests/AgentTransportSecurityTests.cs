using System.Net;
using Kairon.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kairon.Agent.Tests;

/// <summary>RB-004: the Agent must reject a remote plaintext endpoint (and one embedding
/// credentials) exactly like the SDKs do, at the actual transport boundary - not merely in
/// Program.cs's DI wiring, which a directly-constructed HttpClient could bypass.</summary>
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
    public async Task RegistrationOverARemoteHttpBaseAddressIsRefusedAndNeverSent()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var handler = new CountingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://remote-host:8000/") };
        var service = new TestMachineRegistrationService(http, Options.Create(new AgentOptions
        {
            EnableMachineRegistration = true,
            AgentKey = "unit-test-agent-key",
            UserAgentKey = "unit-test-user-agent-key-that-is-long-enough",
            HeartbeatIntervalSeconds = 20,
            TimeoutSeconds = 1
        }), stop);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(stop.Token));

        // The rejection is treated as an ordinary registration failure (the retry loop keeps
        // running, never crashes), but the handler - the actual network transport - is never
        // invoked at all: the request is refused before anything is sent.
        Assert.True(service.Delays.Count > 0);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RegistrationOverAnAllowedHttpsBaseAddressIsSent()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var handler = new CountingHandler(completesOnHeartbeat: stop);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://backend.example/") };
        var service = new TestMachineRegistrationService(http, Options.Create(new AgentOptions
        {
            EnableMachineRegistration = true,
            AgentKey = "unit-test-agent-key",
            UserAgentKey = "unit-test-user-agent-key-that-is-long-enough",
            HeartbeatIntervalSeconds = 20,
            TimeoutSeconds = 1
        }), stop);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(stop.Token));

        Assert.True(handler.Calls > 0);
    }

    [Fact]
    public async Task EventSendOverARemoteHttpBaseAddressIsRefusedAndNeverSent()
    {
        var handler = new CountingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://remote-host:8000/") };
        var client = new AgentEventClient(http, Options.Create(new AgentOptions { ApiKey = "test-key" }),
            NullLogger<AgentEventClient>.Instance);

        var result = await client.SendAsync(new AgentEventPayload { EventType = "Test", Message = "m", Source = "s" });

        Assert.False(result);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task EventSendOverAnAllowedEndpointIsSent()
    {
        var handler = new CountingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://backend.example/") };
        var client = new AgentEventClient(http, Options.Create(new AgentOptions { ApiKey = "test-key" }),
            NullLogger<AgentEventClient>.Instance);

        var result = await client.SendAsync(new AgentEventPayload { EventType = "Test", Message = "m", Source = "s" });

        Assert.True(result);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class TestMachineRegistrationService(
        HttpClient http, IOptions<AgentOptions> options, CancellationTokenSource stop)
        : MachineRegistrationService(http, options, NullLogger<MachineRegistrationService>.Instance)
    {
        public List<TimeSpan> Delays { get; } = [];
        private int _delayCount;

        public Task RunAsync(CancellationToken cancellationToken) => base.ExecuteAsync(cancellationToken);

        protected override Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            // Bound the loop deterministically rather than relying purely on the 5s wall-clock
            // timeout above - a rejected endpoint retries forever otherwise.
            if (++_delayCount >= 3) stop.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class CountingHandler(CancellationTokenSource? completesOnHeartbeat = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.RequestUri!.AbsolutePath.Contains("heartbeat", StringComparison.Ordinal))
                completesOnHeartbeat?.Cancel();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
