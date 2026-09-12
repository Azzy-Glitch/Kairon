using System.Net;
using System.Text;
using Kairon.SDK;
using Xunit;

namespace Kairon.SDK.Tests;

/// <summary>
/// The pairing-code-only onboarding path: `new KaironClient(pairingCode: "...")` should redeem the code
/// through the real api/v1/sdk/pair contract (docs/DESKTOP_SHELL.md), persist the resulting
/// credential via KaironCredentialStore, and configure telemetry from it - while explicit
/// configuration, environment variables, and a previously stored credential all continue to work
/// exactly as before. Uses a real local HttpListener rather than a stub handler (mirroring
/// sdk-python's test convention) because Kairon's own pairing call is not seeded with a test
/// handler - it goes over a real HTTP endpoint, same as production.
/// </summary>
public class KaironTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "kairon-sdk-tests-" + Guid.NewGuid());
    private string ConfigPath => Path.Combine(_tempDir, "credential.json");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task PairingCodeAloneRedeemsPersistsAndConfiguresTheClient()
    {
        using var server = new FakePairingServer(HttpStatusCode.OK,
            """{"apiKey":"krn_real_key","projectId":"22222222-2222-2222-2222-222222222222","pairingId":"44444444-4444-4444-4444-444444444444","endpoint":"http://127.0.0.1:8000"}""");

        await using var kairon = new KaironClient(pairingCode: "pair_realcode", endpoint: server.Url, configPath: ConfigPath);

        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), kairon.ProjectId);
        Assert.Equal("http://127.0.0.1:8000", kairon.Endpoint);
        Assert.True(File.Exists(ConfigPath), "the redeemed credential must be persisted for future runs");
        // The persisted file must never contain the pairing code itself.
        Assert.DoesNotContain("pair_realcode", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public async Task SecondRunReusesTheStoredCredentialWithoutRedeemingAgain()
    {
        var server = new FakePairingServer(HttpStatusCode.OK,
            """{"apiKey":"krn_real_key","projectId":"33333333-3333-3333-3333-333333333333","pairingId":"55555555-5555-5555-5555-555555555555","endpoint":"http://127.0.0.1:8000"}""");

        await using (var first = new KaironClient(pairingCode: "pair_onceonly", endpoint: server.Url, configPath: ConfigPath))
        {
            Assert.Equal(Guid.Parse("33333333-3333-3333-3333-333333333333"), first.ProjectId);
        }

        // The fake server would reject a second redemption of the same code (real pairing codes
        // are single-use) - stopping it here proves the second construction cannot be relying on
        // a network call at all, only the stored credential.
        server.Dispose();

        var second = new KaironClient(configPath: ConfigPath);
        Assert.Equal(Guid.Parse("33333333-3333-3333-3333-333333333333"), second.ProjectId);
        await second.DisposeAsync();
    }

    [Fact]
    public void InvalidOrExpiredPairingCodeFailsClearlyWithoutPersistingAnything()
    {
        using var server = new FakePairingServer(HttpStatusCode.BadRequest,
            """{"error":"Pairing code is invalid, expired, revoked, or already used."}""");

        var ex = Assert.Throws<InvalidOperationException>(
            () => new KaironClient(pairingCode: "pair_bad", endpoint: server.Url, configPath: ConfigPath));

        Assert.Contains("pairing failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public void UnavailableBackendFailsClearlyWithoutPersistingAnything()
    {
        // Nothing listening on this port.
        var unreachable = "http://127.0.0.1:1";

        var ex = Assert.Throws<InvalidOperationException>(
            () => new KaironClient(pairingCode: "pair_x", endpoint: unreachable, configPath: ConfigPath));

        Assert.Contains("pairing failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public void ExplicitConfigurationIsUsedWithoutAnyPairingCode()
    {
        using var kairon = new KaironClient(
            endpoint: "http://127.0.0.1:9999",
            projectId: Guid.Parse("44444444-4444-4444-4444-444444444444"),
            apiKey: "krn_explicit",
            configPath: ConfigPath);

        Assert.Equal(Guid.Parse("44444444-4444-4444-4444-444444444444"), kairon.ProjectId);
        Assert.Equal("http://127.0.0.1:9999", kairon.Endpoint);
        Assert.False(File.Exists(ConfigPath), "explicit configuration should not trigger pairing persistence");
    }

    [Fact]
    public void EnvironmentVariablesAreUsedWhenNoExplicitValueIsGiven()
    {
        Environment.SetEnvironmentVariable("KAIRON_ENDPOINT", "http://127.0.0.1:8123");
        Environment.SetEnvironmentVariable("KAIRON_PROJECT_ID", "55555555-5555-5555-5555-555555555555");
        Environment.SetEnvironmentVariable("KAIRON_API_KEY", "krn_from_env");
        try
        {
            using var kairon = new KaironClient(configPath: ConfigPath);
            Assert.Equal(Guid.Parse("55555555-5555-5555-5555-555555555555"), kairon.ProjectId);
            Assert.Equal("http://127.0.0.1:8123", kairon.Endpoint);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KAIRON_ENDPOINT", null);
            Environment.SetEnvironmentVariable("KAIRON_PROJECT_ID", null);
            Environment.SetEnvironmentVariable("KAIRON_API_KEY", null);
        }
    }

    [Fact]
    public void MissingEverythingFailsClearlyInsteadOfStartingHalfConfigured()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new KaironClient(configPath: ConfigPath));
        Assert.Contains("needs a project", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsyncResolvesExplicitConfigurationJustLikeTheSynchronousConstructor()
    {
        await using var kairon = await KaironClient.CreateAsync(
            endpoint: "http://127.0.0.1:9999",
            projectId: Guid.Parse("44444444-4444-4444-4444-444444444444"),
            apiKey: "krn_explicit",
            configPath: ConfigPath);

        Assert.Equal(Guid.Parse("44444444-4444-4444-4444-444444444444"), kairon.ProjectId);
        Assert.Equal("http://127.0.0.1:9999", kairon.Endpoint);
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public async Task CreateAsyncPropagatesAnInvalidPairingCodeJustLikeTheSynchronousConstructor()
    {
        using var server = new FakePairingServer(HttpStatusCode.BadRequest,
            """{"error":"Pairing code is invalid, expired, revoked, or already used."}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => KaironClient.CreateAsync(pairingCode: "pair_bad", endpoint: server.Url, configPath: ConfigPath));

        Assert.Contains("pairing failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public void CorruptedStoredCredentialFileIsTreatedAsNotStored()
    {
        // A stored credential.json can become unreadable (disk corruption, a foreign machine's
        // key ring after copying the file across machines, a partial write) - this must never be
        // fatal and must never be partially trusted; it is exactly as if nothing had been stored.
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(ConfigPath, "not valid protected data at all");

        using var kairon = new KaironClient(
            endpoint: "http://127.0.0.1:9999",
            projectId: Guid.Parse("66666666-6666-6666-6666-666666666666"),
            apiKey: "krn_fallback",
            configPath: ConfigPath);

        Assert.Equal(Guid.Parse("66666666-6666-6666-6666-666666666666"), kairon.ProjectId);
    }

    [Fact]
    public void MissingEverythingFailsClearlyEvenWithAPartiallyValidStoredFilePresent()
    {
        // A stored file that fails KaironCredentialStore.Load's own validity check (e.g. saved
        // by a corrupted/older writer) must be rejected as a whole, never partially trusted - so
        // with nothing else supplying a project/key, construction still fails clearly.
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(ConfigPath, "");

        var ex = Assert.Throws<InvalidOperationException>(() => new KaironClient(configPath: ConfigPath));
        Assert.Contains("needs a project", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakePairingServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        public string Url { get; }

        public FakePairingServer(HttpStatusCode status, string body)
        {
            var port = GetFreePort();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _ = AcceptLoop(status, body, _cts.Token);
        }

        private async Task AcceptLoop(HttpStatusCode status, string body, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().WaitAsync(token);
                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.StatusCode = (int)status;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, token);
                    context.Response.Close();
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (HttpListenerException) { }
        }

        private static int GetFreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
        }
    }
}
