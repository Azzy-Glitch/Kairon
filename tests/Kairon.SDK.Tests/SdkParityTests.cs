using System.Net;
using System.Text.Json;
using Kairon.SDK;
using Kairon.SDK.Models;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kairon.SDK.Tests;

/// <summary>
/// The .NET SDK behaves like the Python SDK where it matters to a developer: one credential file
/// per application, credentials masked in error text, process CPU labelled as such, the process
/// identified for KAIRON's application restart, and the same delivery and shutdown defaults.
/// </summary>
public sealed class SdkParityTests
{
    [Fact]
    public void EachApplicationGetsItsOwnCredentialFile()
    {
        var orders = KaironCredentialStore.DefaultPath(KaironCredentialStore.ApplicationKey("Orders"));
        var billing = KaironCredentialStore.DefaultPath(KaironCredentialStore.ApplicationKey("Billing"));

        Assert.NotEqual(orders, billing);
        Assert.Equal(orders, KaironCredentialStore.DefaultPath(KaironCredentialStore.ApplicationKey("Orders")));
        Assert.Matches(@"credential-dotnet-[0-9a-f]{16}\.json$", orders);
        Assert.EndsWith("credential-dotnet.json", KaironCredentialStore.DefaultPath());
        Assert.StartsWith(Environment.CurrentDirectory + "|", KaironCredentialStore.ApplicationKey("Orders"));
    }

    [Fact]
    public void AnApplicationAdoptsThePreviousSharedFileOnceAndNeverDeletesIt()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kairon-sdk-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var shared = Path.Combine(folder, "credential-dotnet.json");
            var mine = Path.Combine(folder, "credential-dotnet-0123456789abcdef.json");
            var stored = new StoredKaironCredential("http://localhost:8000", Guid.NewGuid(), "krn_shared_credential_123", null);
            KaironCredentialStore.Save(shared, stored);

            var adopted = KaironCredentialStore.LoadOrMigrate(mine, Path.Combine(folder, "missing.json"), shared);

            Assert.Equal(stored.ProjectId, adopted!.Value.ProjectId);
            Assert.True(File.Exists(mine));
            Assert.True(File.Exists(shared));
            Assert.Equal(stored.ProjectId, KaironCredentialStore.Load(mine)!.Value.ProjectId);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig", "eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("connect failed: api_key=krn_live_secret_value_123", "krn_live_secret_value_123")]
    [InlineData("password=hunter2 rejected", "hunter2")]
    [InlineData("pairing code pair_abcdefgh12345678 expired", "pair_abcdefgh12345678")]
    public void ExceptionTextIsMaskedLikeThePythonSdk(string text, string secret)
    {
        var scrubbed = KaironScrubber.Scrub(text)!;
        Assert.DoesNotContain(secret, scrubbed);
        Assert.Contains("[redacted]", scrubbed);
        Assert.Equal("Order 42 timed out after 3000 ms", KaironScrubber.Scrub("Order 42 timed out after 3000 ms"));
    }

    [Fact]
    public void MetricEventsIdentifyTheProcessAndLabelCpuAsProcessCpu()
    {
        var options = new KaironOptions { ProjectId = Guid.NewGuid() };
        var metric = NormalizedTelemetryEvent.From(new MetricPayload { CpuPercent = 12.5, Timestamp = DateTime.UtcNow }, options);
        var request = NormalizedTelemetryEvent.From(new TelemetryPayload { Endpoint = "/orders", Timestamp = DateTime.UtcNow }, options);

        Assert.Equal(Environment.ProcessId, metric.ProcessId);
        Assert.Equal("process", metric.Metadata!["cpu.scope"]);
        Assert.Equal(Environment.CurrentDirectory, metric.Metadata["process.cwd"]);
        Assert.Null(request.ProcessId);
        Assert.Null(request.Metadata);
    }

    [Fact]
    public void DefaultsMatchThePythonSdk()
    {
        var options = new KaironOptions();
        Assert.Equal(5, options.MetricsIntervalSeconds);
        Assert.Equal(3, options.DeliveryAttempts);
        Assert.Equal(5, options.ShutdownTimeoutSeconds);
    }

    [Theory]
    [InlineData(3, 3, true)]
    [InlineData(1, 1, false)]
    public async Task TransientFailuresAreRetriedUpToTheConfiguredAttempts(int attempts, int expectedCalls, bool delivered)
    {
        var calls = 0;
        var handler = new Respond(request =>
        {
            calls++;
            return calls < 3
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"accepted\":1,\"duplicates\":0,\"rejected\":0}") };
        });
        var options = Options.Create(new KaironOptions
        {
            Endpoint = "http://localhost:8000", ProjectId = Guid.NewGuid(), ApiKey = "krn_sdk_credential", DeliveryAttempts = attempts
        });
        var client = new KaironTelemetryClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000/") }, options, agentProofPort: 0);

        var result = await client.SendNormalizedAsync(new MetricPayload { CpuPercent = 1, Timestamp = DateTime.UtcNow });

        Assert.Equal(expectedCalls, calls);
        Assert.Equal(delivered, result!.Success);
    }

    private sealed class Respond(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
