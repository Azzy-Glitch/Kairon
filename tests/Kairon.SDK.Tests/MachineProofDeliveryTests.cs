using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kairon.SDK;
using Kairon.SDK.Models;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kairon.SDK.Tests;

/// <summary>
/// How machine proof interacts with delivery: the Agent is told the exact backend base the SDK
/// posts to (path base included), a rejected proof never costs the telemetry itself, and a proof
/// is only ever attached to a batch the backend can scope to one machine target.
/// </summary>
public sealed class MachineProofDeliveryTests
{
    [Fact]
    public void ProductionAgentPortIsUnchangedAndThisAssemblyNeverTargetsIt()
    {
        Assert.Equal(47891, AgentMachineProof.ProductionPort);
        Assert.NotEqual(AgentMachineProof.ProductionPort, AgentMachineProof.DefaultPort);
    }

    [Theory]
    [InlineData("http://localhost:8000/", "http://localhost:8000")]
    [InlineData("http://localhost:8000/kairon/", "http://localhost:8000/kairon")]
    [InlineData("https://kairon.example.com/tenant/api/", "https://kairon.example.com/tenant/api")]
    [InlineData("https://kairon.example.com:8443/", "https://kairon.example.com:8443")]
    public void ProofEndpointKeepsThePathBaseTheSdkPostsTo(string baseAddress, string expected) =>
        Assert.Equal(expected, AgentMachineProof.ProofEndpoint(new Uri(baseAddress)));

    [Fact]
    public async Task TheAgentIsToldTheFullEndpointIncludingPathBase()
    {
        using var agent = new FakeAgent();
        var handler = new ScriptedHandler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("/machine-proofs")
            ? Json(new { proofId = Guid.NewGuid() })
            : Accepted(request));
        var client = Client(handler, "http://localhost:8000/kairon/", agent.Port);

        var result = await client.SendNormalizedAsync(Metric("Orders"));

        Assert.True(result!.Success);
        Assert.Equal("/kairon/api/v1/telemetry/machine-proofs", handler.Paths[0]);
        Assert.All(agent.Endpoints, endpoint => Assert.Equal("http://localhost:8000/kairon", endpoint));
        Assert.Equal(2, agent.Frames); // ping + proof confirmation
    }

    [Fact]
    public async Task ARejectedProofIsRetriedOnceWithoutTheProofAndTheTelemetryIsDelivered()
    {
        using var agent = new FakeAgent();
        var handler = new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/machine-proofs"))
                return Json(new { proofId = Guid.NewGuid() });
            // The backend's answer to an invalid/expired/consumed proof, with a VALID API key.
            return request.Headers.Contains("X-Kairon-Machine-Proof")
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))
                : Accepted(request);
        });
        var client = Client(handler, "http://localhost:8000/", agent.Port);

        var result = await client.SendNormalizedAsync(Metric("Orders"));

        Assert.True(result!.Success);
        Assert.Equal(new[] { true, false }, handler.EventPostsWithProof);
        Assert.Equal(handler.EventBodies[0], handler.EventBodies[1]); // same EventId, same bytes
        Assert.DoesNotContain("authentication", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnlyA401WithoutAProofIsReportedAsAuthenticationFailure()
    {
        using var agent = new FakeAgent();
        var handler = new ScriptedHandler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("/machine-proofs")
            ? Json(new { proofId = Guid.NewGuid() })
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var client = Client(handler, "http://localhost:8000/", agent.Port);

        var result = await client.SendNormalizedAsync(Metric("Orders"));

        Assert.False(result!.Success);
        Assert.Contains("401", result.Message);
        Assert.Contains("project authentication rejected", result.Message);
        Assert.Equal(new[] { true, false }, handler.EventPostsWithProof); // never more than one fallback
    }

    [Fact]
    public async Task LegacyDirectSendAlsoFallsBackWhenItsProofIsRejected()
    {
        using var agent = new FakeAgent();
        var handler = new ScriptedHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/machine-proofs"))
                return Json(new { proofId = Guid.NewGuid() });
            return request.Headers.Contains("X-Kairon-Machine-Proof")
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))
                : Json(new { success = true });
        });
        var client = Client(handler, "http://localhost:8000/", agent.Port);

        var result = await client.SendAsync(new TelemetryPayload
        {
            ApplicationName = "Orders", Service = "Orders", Environment = "Production", StatusCode = 500
        });

        Assert.True(result!.Success);
        Assert.Equal(new[] { true, false }, handler.LegacyPostsWithProof);
    }

    [Fact]
    public async Task AMixedScopeBatchNeverAsksForOrCarriesAProof()
    {
        using var agent = new FakeAgent();
        var handler = new ScriptedHandler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("/machine-proofs")
            ? Json(new { proofId = Guid.NewGuid() })
            : Accepted(request));
        var client = Client(handler, "http://localhost:8000/", agent.Port);
        var options = client.Options;

        var result = await client.SendNormalizedBatchAsync(new[]
        {
            NormalizedTelemetryEvent.From(Metric("Orders"), options),
            NormalizedTelemetryEvent.From(Metric("Billing"), options)
        }, CancellationToken.None);

        Assert.Equal(2, result.Delivered);
        Assert.Equal(new[] { false }, handler.EventPostsWithProof);
        Assert.DoesNotContain(handler.Paths, path => path.EndsWith("/machine-proofs"));
        Assert.Equal(0, agent.Frames);
    }

    [Fact]
    public async Task AHomogeneousBatchCarriesOneProofForTheWholeBody()
    {
        using var agent = new FakeAgent();
        var handler = new ScriptedHandler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("/machine-proofs")
            ? Json(new { proofId = Guid.NewGuid() })
            : Accepted(request));
        var client = Client(handler, "http://localhost:8000/", agent.Port);
        var options = client.Options;

        var result = await client.SendNormalizedBatchAsync(new[]
        {
            NormalizedTelemetryEvent.From(Metric("Orders"), options),
            NormalizedTelemetryEvent.From(new TelemetryPayload
            {
                ApplicationName = "Orders", Service = "Orders", Environment = "production", StatusCode = 500
            }, options)
        }, CancellationToken.None);

        Assert.Equal(2, result.Delivered);
        Assert.Equal(new[] { true }, handler.EventPostsWithProof);
    }

    [Fact]
    public void BatchScopeMirrorsTheBackendHomogeneityRule()
    {
        var project = Guid.NewGuid();
        NormalizedTelemetryEvent Event(string service, string environment, string application = "App", Guid? id = null) =>
            new() { ProjectId = id ?? project, Service = service, Environment = environment, Application = application };

        Assert.True(NormalizedBatchScope.IsHomogeneous(new[] { Event("A", "Production"), Event("A", "PRODUCTION") }));
        Assert.True(NormalizedBatchScope.IsHomogeneous(new[] { Event("", "Production", "A"), Event("A", "Production") }));
        Assert.True(NormalizedBatchScope.IsHomogeneous(new[] { Event("A", ""), Event("A", "Development") }));
        Assert.False(NormalizedBatchScope.IsHomogeneous(new[] { Event("A", "Production"), Event("a", "Production") }));
        Assert.False(NormalizedBatchScope.IsHomogeneous(new[] { Event("A", "Production"), Event("A", "Staging") }));
        Assert.False(NormalizedBatchScope.IsHomogeneous(new[] { Event("A", "Production"), Event("A", "Production", id: Guid.NewGuid()) }));
    }

    private static MetricPayload Metric(string service) => new()
    {
        Application = service, Service = service, Environment = "Production", CpuPercent = 50
    };

    private static KaironTelemetryClient Client(HttpMessageHandler handler, string baseAddress, int agentPort) =>
        new(new HttpClient(handler) { BaseAddress = new Uri(baseAddress) },
            Options.Create(new KaironOptions
            {
                ProjectId = Guid.NewGuid(), ApiKey = "krn_valid_key", TimeoutSeconds = 10
            }), agentPort);

    private static Task<HttpResponseMessage> Json(object body) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });

    private static async Task<HttpResponseMessage> Accepted(HttpRequestMessage request)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
        var count = body.RootElement.GetProperty("events").GetArrayLength();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"accepted\":{count},\"duplicates\":0,\"rejected\":0}}")
        };
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        private int _calls;
        public List<string> Paths { get; } = new();
        public List<bool> EventPostsWithProof { get; } = new();
        public List<bool> LegacyPostsWithProof { get; } = new();
        public List<byte[]> EventBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("krn_valid_key", request.Headers.GetValues("X-Kairon-API-Key").Single());
            var path = request.RequestUri!.AbsolutePath;
            lock (Paths) Paths.Add(path);
            var withProof = request.Headers.Contains("X-Kairon-Machine-Proof");
            if (path.EndsWith("/api/v1/telemetry/events"))
            {
                lock (Paths) { EventPostsWithProof.Add(withProof); }
                EventBodies.Add(await request.Content!.ReadAsByteArrayAsync(ct));
            }
            else if (path.EndsWith("/api/telemetry/incidents"))
                lock (Paths) LegacyPostsWithProof.Add(withProof);
            return await respond(request, Interlocked.Increment(ref _calls));
        }
    }
}
