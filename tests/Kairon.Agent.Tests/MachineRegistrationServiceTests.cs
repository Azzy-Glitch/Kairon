using System.Net;
using Kairon.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kairon.Agent.Tests;

public sealed class MachineRegistrationServiceTests
{
    [Fact]
    public async Task FailedInitialRegistrationRetriesWithCappedBackoffAndThenHeartbeats()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var handler = new ScriptedHandler(stop, failuresBeforeSuccess: 6);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000/") };
        var service = new TestMachineRegistrationService(http, Options.Create(new AgentOptions
        {
            EnableMachineRegistration = true,
            AgentKey = "unit-test-agent-key",
            UserAgentKey = "unit-test-user-agent-key-that-is-long-enough",
            HeartbeatIntervalSeconds = 20,
            TimeoutSeconds = 1
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(stop.Token));

        Assert.Equal(7, handler.RegistrationCalls);
        Assert.Equal(1, handler.HeartbeatCalls);
        Assert.Equal(new[]
        {
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30)
        }, service.Delays);
    }

    private sealed class TestMachineRegistrationService(
        HttpClient http, IOptions<AgentOptions> options)
        : MachineRegistrationService(http, options, NullLogger<MachineRegistrationService>.Instance)
    {
        public List<TimeSpan> Delays { get; } = [];

        public Task RunAsync(CancellationToken cancellationToken) => base.ExecuteAsync(cancellationToken);

        protected override Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedHandler(CancellationTokenSource stop, int failuresBeforeSuccess) : HttpMessageHandler
    {
        public int RegistrationCalls { get; private set; }
        public int HeartbeatCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/register", StringComparison.Ordinal))
            {
                RegistrationCalls++;
                var status = RegistrationCalls <= failuresBeforeSuccess
                    ? HttpStatusCode.ServiceUnavailable
                    : HttpStatusCode.OK;
                return Task.FromResult(new HttpResponseMessage(status));
            }

            HeartbeatCalls++;
            stop.Cancel();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
