using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kairon.SDK;
using Kairon.SDK.Models;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kairon.SDK.Tests;

public sealed class AgentMachineProofTests
{
    [Fact]
    public async Task NormalizedSendUsesTheAgentProofForTheExactEventBody()
    {
        var proofId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var handler = new NormalizedHandler(proofId);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000/") };
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var agent = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var connection = await listener.AcceptTcpClientAsync();
                var stream = connection.GetStream();
                var prefix = new byte[4];
                await stream.ReadExactlyAsync(prefix);
                var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
                Assert.InRange(length, 1, 1024);
                var frame = new byte[length];
                await stream.ReadExactlyAsync(frame);
                Assert.DoesNotContain("krn_", Encoding.UTF8.GetString(frame));
                using var document = JsonDocument.Parse(frame);
                Assert.Equal("http://localhost:8000", document.RootElement.GetProperty("endpoint").GetString());
                if (i == 1) Assert.Equal(proofId.ToString(), document.RootElement.GetProperty("proofId").GetString());
                var reply = JsonSerializer.SerializeToUtf8Bytes(new { confirmed = true });
                BinaryPrimitives.WriteInt32BigEndian(prefix, reply.Length);
                await stream.WriteAsync(prefix);
                await stream.WriteAsync(reply);
            }
        });
        var options = Options.Create(new KaironOptions
        {
            ProjectId = projectId, ApiKey = "krn_private-sdk-key", TimeoutSeconds = 5
        });
        var client = new KaironTelemetryClient(http, options, port);
        var result = await client.SendNormalizedAsync(new MetricPayload
        {
            ProjectId = Guid.NewGuid(), Application = "Orders", Service = "Orders",
            Environment = "Production", CpuPercent = 95, MachineId = Guid.NewGuid()
        });
        await agent.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result!.Success);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(projectId, handler.TelemetryProjectId);
        Assert.True(handler.ProofBoundToBody);
    }

    [Fact]
    public async Task BodyBoundChallengeKeepsSdkKeyOffTheLocalAgentWire()
    {
        var proofId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var body = Encoding.UTF8.GetBytes("{\"events\":[{\"status\":500}]}");
        var handler = new ChallengeHandler(proofId, projectId, body);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000/") };
        var options = new KaironOptions { ProjectId = projectId, ApiKey = "krn_private-sdk-key" };
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var localRequests = new List<JsonDocument>();

        var agent = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var prefix = new byte[4];
                await stream.ReadExactlyAsync(prefix);
                var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
                Assert.InRange(length, 1, 1024);
                var frame = new byte[length];
                await stream.ReadExactlyAsync(frame);
                Assert.DoesNotContain("krn_", Encoding.UTF8.GetString(frame));
                localRequests.Add(JsonDocument.Parse(frame));
                var reply = JsonSerializer.SerializeToUtf8Bytes(new { confirmed = true });
                BinaryPrimitives.WriteInt32BigEndian(prefix, reply.Length);
                await stream.WriteAsync(prefix);
                await stream.WriteAsync(reply);
            }
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var actual = await AgentMachineProof.TryAcquireAsync(http, options, "Orders", "Production",
            body, deadline.Token, port);
        await agent.WaitAsync(deadline.Token);

        Assert.Equal(proofId, actual);
        Assert.Equal(1, handler.Calls);
        Assert.True(localRequests[0].RootElement.GetProperty("ping").GetBoolean());
        Assert.Equal(proofId.ToString(), localRequests[1].RootElement.GetProperty("proofId").GetString());
        Assert.Equal("http://localhost:8000", localRequests[1].RootElement.GetProperty("endpoint").GetString());
        foreach (var request in localRequests) request.Dispose();
    }

    private sealed class ChallengeHandler(Guid proofId, Guid projectId, byte[] body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal("/api/v1/telemetry/machine-proofs", request.RequestUri?.AbsolutePath);
            Assert.Equal("krn_private-sdk-key", request.Headers.GetValues("X-Kairon-API-Key").Single());
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync(ct));
            Assert.Equal(projectId.ToString(), json.RootElement.GetProperty("projectId").GetString());
            Assert.Equal("Orders", json.RootElement.GetProperty("service").GetString());
            Assert.Equal("Production", json.RootElement.GetProperty("environment").GetString());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(body)),
                json.RootElement.GetProperty("bodySha256").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { proofId })
            };
        }
    }

    private sealed class NormalizedHandler(Guid proofId) : HttpMessageHandler
    {
        private string? _digest;
        public int Calls { get; private set; }
        public Guid TelemetryProjectId { get; private set; }
        public bool ProofBoundToBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal("krn_private-sdk-key", request.Headers.GetValues("X-Kairon-API-Key").Single());
            if (request.RequestUri!.AbsolutePath == "/api/v1/telemetry/machine-proofs")
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync(ct));
                _digest = json.RootElement.GetProperty("bodySha256").GetString();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { proofId }) };
            }

            Assert.Equal("/api/v1/telemetry/events", request.RequestUri!.AbsolutePath);
            Assert.Equal(proofId.ToString(), request.Headers.GetValues("X-Kairon-Machine-Proof").Single());
            var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
            ProofBoundToBody = _digest == Convert.ToHexString(SHA256.HashData(bytes));
            using var jsonBody = JsonDocument.Parse(bytes);
            var item = jsonBody.RootElement.GetProperty("events")[0];
            TelemetryProjectId = item.GetProperty("projectId").GetGuid();
            Assert.False(item.TryGetProperty("machineId", out _));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"accepted\":1,\"duplicates\":0,\"rejected\":0}")
            };
        }
    }
}
