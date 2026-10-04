using System.Net;
using System.Text.Json;
using Kairon.SDK;
using Kairon.SDK.Models;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kairon.SDK.Tests;

public sealed class NormalizedTelemetryTests
{
    [Fact]
    public async Task IncidentAndMetricUseTheSharedBackendContractWithoutClaimingMachineId()
    {
        var projectId = Guid.NewGuid();
        var bodies = new List<JsonElement>();
        var handler = new StubHandler(async (request, _) =>
        {
            Assert.Equal("/api/v1/telemetry/events", request.RequestUri!.AbsolutePath);
            Assert.Equal("krn_sdk_credential", request.Headers.GetValues("X-Kairon-API-Key").Single());
            Assert.False(request.Headers.Contains("X-Kairon-Machine-Proof"));
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync());
            bodies.Add(document.RootElement.Clone());
            return Accepted();
        });
        var client = Client(handler, projectId);
        var incident = await client.SendNormalizedAsync(new TelemetryPayload
        {
            ApplicationName = "Orders", Service = "OrderService", Environment = "Development",
            Method = "GET", Endpoint = "/orders", StatusCode = 500, Duration = 77,
            Error = "boom", MachineId = Guid.NewGuid()
        });
        var metric = await client.SendNormalizedAsync(new MetricPayload
        {
            Application = "Orders", Service = "OrderService", Environment = "Development",
            CpuPercent = 91, RequestCount = 10, ErrorCount = 3, MachineId = Guid.NewGuid()
        });

        Assert.True(incident!.Success);
        Assert.True(metric!.Success);
        Assert.Equal(2, bodies.Count);
        var http = Assert.Single(bodies[0].GetProperty("events").EnumerateArray());
        Assert.Equal("http", http.GetProperty("eventType").GetString());
        Assert.Equal("dotnet-sdk", http.GetProperty("source").GetString());
        Assert.Equal(projectId, http.GetProperty("projectId").GetGuid());
        Assert.Equal("OrderService", http.GetProperty("service").GetString());
        Assert.Equal(500, http.GetProperty("httpContext").GetProperty("statusCode").GetInt32());
        Assert.False(http.TryGetProperty("machineId", out _));
        var resource = Assert.Single(bodies[1].GetProperty("events").EnumerateArray());
        Assert.Equal("metric", resource.GetProperty("eventType").GetString());
        Assert.Equal(91, resource.GetProperty("resourceMetrics").GetProperty("cpuPercent").GetDouble());
        Assert.False(resource.TryGetProperty("machineId", out _));
        Assert.NotEqual(http.GetProperty("eventId").GetGuid(), resource.GetProperty("eventId").GetGuid());
    }

    [Fact]
    public async Task LostResponseRetriesTheSameEventIdAndAcceptsBackendDuplicate()
    {
        var bytes = new List<byte[]>();
        var handler = new StubHandler(async (request, call) =>
        {
            bytes.Add(await request.Content!.ReadAsByteArrayAsync());
            if (call == 1) throw new HttpRequestException("response lost after commit");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"accepted\":0,\"duplicates\":1,\"rejected\":0}")
            };
        });
        var result = await Client(handler, Guid.NewGuid()).SendNormalizedAsync(new TelemetryPayload
        {
            ApplicationName = "Orders", Service = "OrderService", StatusCode = 500
        });
        Assert.True(result!.Success);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(bytes[0], bytes[1]);
        using var document = JsonDocument.Parse(bytes[0]);
        Assert.NotEqual(Guid.Empty, document.RootElement.GetProperty("events")[0]
            .GetProperty("eventId").GetGuid());
    }

    [Fact]
    public async Task AuthenticationFailureNeverRetriesOrInitiatesPairing()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("/api/v1/telemetry/events", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });
        var result = await Client(handler, Guid.NewGuid()).SendNormalizedAsync(new MetricPayload
        {
            Service = "Orders", Application = "Orders"
        });
        Assert.False(result!.Success);
        Assert.Contains("401", result.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DirectLegacyMethodRemainsAvailableAndCannotOverrideBackendMachineScope()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("/api/telemetry/incidents", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true}")
            });
        });
        var result = await Client(handler, Guid.NewGuid()).SendAsync(new TelemetryPayload
        {
            ApplicationName = "Orders", Service = "Orders", StatusCode = 500
        });
        Assert.True(result!.Success);
    }

    private static KaironTelemetryClient Client(HttpMessageHandler handler, Guid projectId)
    {
        var options = Options.Create(new KaironOptions
        {
            Endpoint = "http://localhost:8000", ProjectId = projectId,
            ApiKey = "krn_sdk_credential", TimeoutSeconds = 3
        });
        return new KaironTelemetryClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8000/")
        }, options, agentProofPort: 0);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            respond(request, ++Calls);
    }

    private static HttpResponseMessage Accepted() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"accepted\":1,\"duplicates\":0,\"rejected\":0}")
    };
}
