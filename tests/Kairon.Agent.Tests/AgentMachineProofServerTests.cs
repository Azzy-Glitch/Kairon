using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Kairon.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kairon.Agent.Tests;

public sealed class AgentMachineProofServerTests
{
    [Fact]
    public async Task LocalExchangeOnlyCarriesOpaqueProofAndAgentKeyGoesToConfiguredBackend()
    {
        var reserve = new TcpListener(IPAddress.Loopback, 0);
        reserve.Start();
        var port = ((IPEndPoint)reserve.LocalEndpoint).Port;
        reserve.Stop();

        const string agentKey = "unit-test-agent-proof-key";
        var options = Options.Create(new AgentOptions
        {
            Endpoint = "http://localhost:8000", AgentKey = agentKey,
            EnableMachineRegistration = true, TimeoutSeconds = 1
        });
        var handler = new CaptureHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000/") };
        var registration = new MachineRegistrationService(http, options,
            NullLogger<MachineRegistrationService>.Instance);
        var server = new AgentMachineProofServer(http, options, registration,
            NullLogger<AgentMachineProofServer>.Instance, port);
        await server.StartAsync(default);
        try
        {
            // Localhost/127.0.0.1 are aliases for the same configured loopback backend.
            Assert.True(await ExchangeAsync(port, "http://127.0.0.1:8000", null));
            Assert.False(await ExchangeAsync(port, "https://attacker.example", null));
            Assert.Equal(0, handler.Calls);

            var id = Guid.NewGuid();
            Assert.True(await ExchangeAsync(port, "http://127.0.0.1:8000", id));
            Assert.Equal(1, handler.Calls);
            Assert.Equal($"/api/agent/machines/{registration.MachineId}/telemetry-proofs/{id}/confirm",
                handler.LastPath);
            Assert.Equal(agentKey, handler.LastAgentKey);
        }
        finally { await server.StopAsync(default); server.Dispose(); }
    }

    private static async Task<bool> ExchangeAsync(int port, string endpoint, Guid? id)
    {
        using var client = new TcpClient();
        for (var i = 0; ; i++)
        {
            try { await client.ConnectAsync(IPAddress.Loopback, port); break; }
            catch (SocketException) when (i < 10) { await Task.Delay(50); }
        }
        var stream = client.GetStream();
        // No SDK credential is ever present in the local protocol, even for confirmation.
        var frame = JsonSerializer.SerializeToUtf8Bytes(new
        {
            endpoint, proofId = id?.ToString(), ping = !id.HasValue
        });
        Assert.DoesNotContain("krn_", System.Text.Encoding.UTF8.GetString(frame));
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, frame.Length);
        await stream.WriteAsync(prefix);
        await stream.WriteAsync(frame);
        await stream.ReadExactlyAsync(prefix);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        Assert.InRange(length, 1, 1024);
        var reply = new byte[length];
        await stream.ReadExactlyAsync(reply);
        return JsonDocument.Parse(reply).RootElement.GetProperty("confirmed").GetBoolean();
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastPath { get; private set; }
        public string? LastAgentKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastPath = request.RequestUri?.AbsolutePath;
            LastAgentKey = request.Headers.GetValues("X-Kairon-Agent-Key").Single();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
