using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// SdkPairingController.Pair/Confirm are unattended, no-operator-key endpoints - the pairing code
/// (or, for confirm, the freshly issued API key) is the only proof of intent, and neither carried
/// any rate limit before this. These tests drive the REAL ASP.NET Core request pipeline (a real
/// Kestrel-backed TestServer via WebApplicationFactory&lt;Program&gt;, not a direct call into the
/// controller action) because the rate limiter only ever engages through
/// app.UseRateLimiter() + the [EnableRateLimiting] attribute's routing metadata - a unit-level call,
/// as the rest of this test project makes, cannot exercise it at all. This mirrors why
/// CaddyGatewayIntegrationTests runs a real Caddy process rather than asserting on config strings.
/// </summary>
public sealed class PairingRateLimitTests : IDisposable
{
    // A FRESH factory per test method (xUnit constructs a new test class instance per [Fact]),
    // never IClassFixture's one-shared-instance-per-class: these tests deliberately exhaust the
    // pairing policy's window, and a shared backend/rate-limiter would let one test's exhaustion
    // bleed into the next test's "legitimate call still succeeds" assertion.
    private readonly PairingRateLimitFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task RepeatedPairAttemptsAreEventuallyRateLimited()
    {
        using var client = _fixture.CreateClient();

        HttpResponseMessage? last = null;
        var sawThrottled = false;
        for (var i = 0; i < 40; i++)
        {
            last = await client.PostAsJsonAsync("api/v1/sdk/pair",
                new { code = $"pair_wrong_code_{i:D3}_aaaaaaaaaaaaaaaa", sdkType = "python", version = "1.1.0" });
            if (last.StatusCode == HttpStatusCode.TooManyRequests) { sawThrottled = true; break; }
        }

        Assert.True(sawThrottled, "40 rapid pairing attempts never received a 429 - the pairing policy is not applied.");
    }

    [Fact]
    public async Task LegitimatePairingStillSucceedsWithinTheNormalLimit()
    {
        using var client = _fixture.CreateClient();
        var code = await _fixture.MintPairingCodeAsync(client);

        var response = await client.PostAsJsonAsync("api/v1/sdk/pair",
            new { code, sdkType = "python", version = "1.1.0" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RepeatedConfirmAttemptsAreEventuallyRateLimited()
    {
        using var client = _fixture.CreateClient();
        var pairingId = Guid.NewGuid();

        HttpResponseMessage? last = null;
        var sawThrottled = false;
        for (var i = 0; i < 40; i++)
        {
            last = await client.PostAsJsonAsync($"api/v1/sdk/pair/{pairingId}/confirm",
                new { apiKey = $"krn_wrong_guess_{i:D3}" });
            if (last.StatusCode == HttpStatusCode.TooManyRequests) { sawThrottled = true; break; }
        }

        Assert.True(sawThrottled, "40 rapid confirmation attempts never received a 429 - the pairing policy is not applied to Confirm.");
    }

    [Fact]
    public async Task LegitimateConfirmationStillSucceedsWithinTheNormalLimit()
    {
        using var client = _fixture.CreateClient();
        var code = await _fixture.MintPairingCodeAsync(client);

        var pairResponse = await client.PostAsJsonAsync("api/v1/sdk/pair", new { code, sdkType = "python", version = "1.1.0" });
        Assert.Equal(HttpStatusCode.OK, pairResponse.StatusCode);
        var paired = await pairResponse.Content.ReadFromJsonAsync<JsonElement>();
        var pairingId = paired.GetProperty("pairingId").GetString();
        var apiKey = paired.GetProperty("apiKey").GetString();

        var confirmResponse = await client.PostAsJsonAsync($"api/v1/sdk/pair/{pairingId}/confirm", new { apiKey });

        Assert.Equal(HttpStatusCode.NoContent, confirmResponse.StatusCode);
    }

    [Fact]
    public async Task ThrottlingPairingNeverThrottlesUnrelatedTelemetryIngestion()
    {
        using var client = _fixture.CreateClient();

        // Exhaust the pairing policy's window.
        HttpResponseMessage? pairResponse = null;
        for (var i = 0; i < 40 && (pairResponse is null || pairResponse.StatusCode != HttpStatusCode.TooManyRequests); i++)
            pairResponse = await client.PostAsJsonAsync("api/v1/sdk/pair",
                new { code = $"pair_wrong_code_{i:D3}_bbbbbbbbbbbbbbbb", sdkType = "python", version = "1.1.0" });
        Assert.Equal(HttpStatusCode.TooManyRequests, pairResponse!.StatusCode);

        // Telemetry ingestion (a different policy, "telemetry") must be entirely unaffected - a
        // shared/global rate limiter would have been "accidental global rate limiting of unrelated
        // endpoints", which this asserts against directly. 401 (unauthenticated) is expected and
        // fine here; 429 is the one outcome that would prove the policies are not actually isolated.
        var telemetryResponse = await client.PostAsJsonAsync("api/telemetry/incidents", new
        {
            projectId = Guid.NewGuid(), service = "x", applicationName = "x", environment = "Development",
            endpoint = "/x", method = "GET", statusCode = 200, duration = 1
        });

        Assert.NotEqual(HttpStatusCode.TooManyRequests, telemetryResponse.StatusCode);
    }

    [Fact]
    public async Task ARateLimitedResponseContainsNoSecretsOrInternalDetails()
    {
        using var client = _fixture.CreateClient();
        const string secretLookingCode = "pair_this_is_a_secret_probe_value_zzzzzzzz";

        HttpResponseMessage? last = null;
        for (var i = 0; i < 40 && (last is null || last.StatusCode != HttpStatusCode.TooManyRequests); i++)
            last = await client.PostAsJsonAsync("api/v1/sdk/pair",
                new { code = i == 0 ? secretLookingCode : $"pair_wrong_code_{i:D3}_cccccccccccccccc", sdkType = "python", version = "1.1.0" });

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        var body = await last.Content.ReadAsStringAsync();

        Assert.DoesNotContain(secretLookingCode, body);
        Assert.DoesNotContain(_fixture.OperatorKey, body);
        // No stack trace / exception type leakage either.
        Assert.DoesNotContain("Exception", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.", body, StringComparison.Ordinal);
    }
}

/// <summary>
/// A real WebApplicationFactory&lt;Program&gt; over the actual backend pipeline - isolated to its
/// own throwaway SQLite database and config directory (Persistence:DatabasePath), NEVER the shared
/// %LOCALAPPDATA%\Kairon layout an installed Kairon uses, so this can never touch a real
/// installation's data.
/// </summary>
public sealed class PairingRateLimitFixture : WebApplicationFactory<Program>
{
    public string OperatorKey { get; } = "rate-limit-test-operator-key-" + Guid.NewGuid().ToString("N");

    private readonly string _dataDir;
    private readonly string _webRoot;

    public PairingRateLimitFixture()
    {
        _dataDir = Path.Combine(
            Path.GetTempPath(),
            "kairon-ratelimit-tests-" + Guid.NewGuid().ToString("N"));
        _webRoot = Path.Combine(_dataDir, "wwwroot");
        Directory.CreateDirectory(_webRoot);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(WebHostDefaults.StaticWebAssetsKey, string.Empty);
        builder.UseWebRoot(_webRoot);
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:DatabasePath"] = Path.Combine(_dataDir, "kairon.db"),
                ["SreSecurity:RequireOperatorKey"] = "true",
                ["SreSecurity:OperatorKey"] = OperatorKey,
                // No AI provider is configured or needed for pairing/rate-limit behavior.
                ["AiOrchestration:Enabled"] = "false"
            });
        });
    }

    /// <summary>Real operator-authenticated project creation + pairing-code mint, exactly what the
    /// Kairon UI does - so "legitimate pairing still works" tests exercise the genuine flow, not a
    /// shortcut into the database.</summary>
    public async Task<string> MintPairingCodeAsync(HttpClient client)
    {
        client.DefaultRequestHeaders.Remove("X-Kairon-Operator-Key");
        client.DefaultRequestHeaders.Add("X-Kairon-Operator-Key", OperatorKey);

        var projectResponse = await client.PostAsJsonAsync("api/v1/projects", new { name = "rate-limit-test-" + Guid.NewGuid() });
        projectResponse.EnsureSuccessStatusCode();
        var project = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = project.GetProperty("id").GetString();

        var pairingResponse = await client.PostAsJsonAsync($"api/v1/projects/{projectId}/pairing", new { sdkType = "python" });
        pairingResponse.EnsureSuccessStatusCode();
        var pairing = await pairingResponse.Content.ReadFromJsonAsync<JsonElement>();

        client.DefaultRequestHeaders.Remove("X-Kairon-Operator-Key");
        return pairing.GetProperty("code").GetString()!;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
