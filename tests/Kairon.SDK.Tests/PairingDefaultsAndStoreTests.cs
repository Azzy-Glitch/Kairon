using System.Net;
using System.Text;
using System.Text.Json;
using Kairon.SDK;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kairon.SDK.Tests;

/// <summary>
/// Simple-mode onboarding: the operator picks the environment and service when generating the
/// pairing code, so a pairing code alone is enough. Those defaults travel with the protected stored
/// credential (non-secret metadata), every entry point applies them, and anything the application
/// sets explicitly still wins. Also covers the store's own file-format tolerance and migration.
/// </summary>
public sealed class PairingDefaultsAndStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "kairon-sdk-defaults-tests-" + Guid.NewGuid());
    private string ConfigPath => Path.Combine(_tempDir, "credential-dotnet.json");

    // Ambient environment variables outrank pairing defaults by design; assertions about the
    // fallback itself are only meaningful when the test process does not set them.
    private static bool NoAmbientEnvironment => Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") is null;
    private static bool NoAmbientApplication => Environment.GetEnvironmentVariable("Kairon_APPLICATION_NAME") is null;

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task APairingCodeAloneAppliesTheOperatorsEnvironmentAndServiceToTelemetry()
    {
        using var server = new PairingAndTelemetryServer(environment: "Staging", service: "checkout-api");

        await using (var client = new KaironClient(pairingCode: "pair_simple", endpoint: server.Url, configPath: ConfigPath))
        {
            client.Start();
            client.CaptureException(new InvalidOperationException("boom"), endpoint: "/pay", statusCode: 500);
            var incident = await server.WaitForEventAsync(TimeSpan.FromSeconds(10));
            if (NoAmbientEnvironment) Assert.Equal("Staging", incident.GetProperty("environment").GetString());
            if (NoAmbientApplication)
            {
                Assert.Equal("checkout-api", incident.GetProperty("service").GetString());
                Assert.Equal("checkout-api", incident.GetProperty("application").GetString());
            }
        }

        var stored = KaironCredentialStore.Load(ConfigPath);
        Assert.NotNull(stored);
        Assert.Equal("Staging", stored!.Value.Environment);
        Assert.Equal("checkout-api", stored.Value.Service);
        Assert.Equal(KaironCredentialStore.HashPairingCode("pair_simple"), stored.Value.PairingCodeSha256);
        Assert.Null(stored.Value.PendingConfirmationPairingId); // confirmation re-save kept the metadata
    }

    [Fact]
    public async Task ExplicitIdentityStillWinsOverPairingDefaults()
    {
        using var server = new PairingAndTelemetryServer(environment: "Staging", service: "checkout-api");

        await using var client = new KaironClient(pairingCode: "pair_explicit", endpoint: server.Url,
            configPath: ConfigPath, applicationName: "orders", environment: "Development");
        client.Start();
        client.CaptureException(new InvalidOperationException("boom"));
        var incident = await server.WaitForEventAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Development", incident.GetProperty("environment").GetString());
        Assert.Equal("orders", incident.GetProperty("service").GetString());
        Assert.Equal("orders", incident.GetProperty("application").GetString());
    }

    [Fact]
    public void ExplicitServiceNameWinsForServiceWhilePairedServiceStillNamesTheApplication()
    {
        var options = new KaironOptions { ServiceName = "explicit-svc", PairedService = "paired-svc", PairedEnvironment = "Staging" };

        Assert.Equal("explicit-svc", KaironIdentity.ResolveService(options));
        if (NoAmbientApplication) Assert.Equal("paired-svc", KaironIdentity.ResolveApplication(options));
        options.Environment = "Production";
        Assert.Equal("Production", KaironIdentity.ResolveEnvironment(options));
    }

    [Fact]
    public async Task LaterRunsAndBothAspNetCoreEntryPointsApplyTheStoredDefaults()
    {
        KaironCredentialStore.Save(ConfigPath, new StoredKaironCredential("http://127.0.0.1:8000", Guid.NewGuid(),
            "krn_stored", null, "Staging", "checkout-api", KaironCredentialStore.HashPairingCode("pair_old")));

        // Standalone client, no pairing code.
        var standalone = await KaironConfigurationResolver.ResolveAsync(null, null, null, null, ConfigPath, default);
        Assert.Equal("Staging", standalone.PairedEnvironment);
        Assert.Equal("checkout-api", standalone.PairedService);

        // Synchronous AddKairon reusing the stored credential.
        var services = new ServiceCollection();
        services.AddKairon(options => options.CredentialPath = ConfigPath);
        using (var provider = services.BuildServiceProvider())
        {
            var resolved = provider.GetRequiredService<IOptions<KaironOptions>>().Value;
            Assert.Equal("Staging", resolved.PairedEnvironment);
            Assert.Equal("checkout-api", resolved.PairedService);
            if (NoAmbientEnvironment) Assert.Equal("Staging", KaironIdentity.ResolveEnvironment(resolved));
        }

        // AddKaironAsync with the same code still in configuration: reused, defaults kept, and an
        // explicit environment from the application still wins.
        var asyncServices = new ServiceCollection();
        await asyncServices.AddKaironAsync("pair_old", options =>
        {
            options.CredentialPath = ConfigPath;
            options.Environment = "Production";
        });
        using var asyncProvider = asyncServices.BuildServiceProvider();
        var asyncResolved = asyncProvider.GetRequiredService<IOptions<KaironOptions>>().Value;
        Assert.Equal("checkout-api", asyncResolved.PairedService);
        Assert.Equal("Production", KaironIdentity.ResolveEnvironment(asyncResolved));
    }

    [Fact]
    public async Task ACredentialFileWithoutPairingDefaultsStillLoadsWithNoDefaults()
    {
        var projectId = Guid.NewGuid();
        KaironCredentialStore.Save(ConfigPath, "http://127.0.0.1:8000", projectId, "krn_old_format");

        var resolved = await KaironConfigurationResolver.ResolveAsync(null, null, null, null, ConfigPath, default);

        Assert.Equal(projectId, resolved.ProjectId);
        Assert.Null(resolved.PairedEnvironment);
        Assert.Null(resolved.PairedService);
        Assert.Null(KaironCredentialStore.Load(ConfigPath)!.Value.PairingCodeSha256);
    }

    [Fact]
    public async Task MalformedPairingDefaultsFromTheWireAreDroppedNotTrusted()
    {
        using var server = new PairingAndTelemetryServer(environment: "Stag\u0000ing", service: new string('s', 500));

        var resolved = await KaironConfigurationResolver.ResolveAsync("pair_bad_defaults", server.Url, null, null, ConfigPath, default);

        Assert.Null(resolved.PairedEnvironment);
        Assert.Null(resolved.PairedService);
    }

    [Fact]
    public async Task AForeignFormatFileAtTheCredentialPathNeverBlocksPairingOrExplicitConfiguration()
    {
        // e.g. sdk-python's differently-encrypted file found at the old shared path.
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(ConfigPath, """{"endpoint":"http://127.0.0.1:8000","api_key":"gAAAAABfernet..."}""");

        var explicitConfig = await KaironConfigurationResolver.ResolveAsync(null, "http://127.0.0.1:8000",
            Guid.NewGuid(), "krn_explicit", ConfigPath, default);
        Assert.Equal("krn_explicit", explicitConfig.ApiKey);

        using var server = new PairingAndTelemetryServer(environment: null, service: null);
        var paired = await KaironConfigurationResolver.ResolveAsync("pair_over_foreign", server.Url, null, null, ConfigPath, default);
        Assert.Equal(server.ProjectId, paired.ProjectId);
        Assert.NotNull(KaironCredentialStore.Load(ConfigPath)); // replaced with this SDK's own format
    }

    [Fact]
    public void DefaultPathIsTheDotnetSpecificFileAndTheLegacySharedNameIsSeparate()
    {
        Assert.Equal("credential-dotnet.json", Path.GetFileName(KaironCredentialStore.DefaultPath()));
        Assert.Equal("credential.json", Path.GetFileName(KaironCredentialStore.LegacyDefaultPath()));
        Assert.Equal(Path.GetDirectoryName(KaironCredentialStore.DefaultPath()),
            Path.GetDirectoryName(KaironCredentialStore.LegacyDefaultPath()));
    }

    [Fact]
    public void ALegacyFileInThisSdksFormatIsMigratedAndLeftInPlace()
    {
        var legacy = Path.Combine(_tempDir, "credential.json");
        var projectId = Guid.NewGuid();
        KaironCredentialStore.Save(legacy, new StoredKaironCredential("http://127.0.0.1:8000", projectId, "krn_legacy",
            null, "Staging", "svc"));

        var loaded = KaironCredentialStore.LoadOrMigrate(ConfigPath, legacy);

        Assert.Equal(projectId, loaded!.Value.ProjectId);
        Assert.Equal("Staging", loaded.Value.Environment);
        Assert.Equal(projectId, KaironCredentialStore.Load(ConfigPath)!.Value.ProjectId);
        Assert.True(File.Exists(legacy)); // another app on an older SDK may still read it
    }

    [Fact]
    public void AForeignLegacyFileIsIgnoredAndNothingIsMigrated()
    {
        Directory.CreateDirectory(_tempDir);
        var legacy = Path.Combine(_tempDir, "credential.json");
        File.WriteAllText(legacy, "python-sdk-encrypted-bytes");

        Assert.Null(KaironCredentialStore.LoadOrMigrate(ConfigPath, legacy));
        Assert.False(File.Exists(ConfigPath));
        Assert.Equal("python-sdk-encrypted-bytes", File.ReadAllText(legacy));
    }

    [Fact]
    public void TheStoredFileHoldsOnlyAHashOfThePairingCode()
    {
        KaironCredentialStore.Save(ConfigPath, new StoredKaironCredential("http://127.0.0.1:8000", Guid.NewGuid(),
            "krn_k", null, PairingCodeSha256: KaironCredentialStore.HashPairingCode("pair_secret_code")));

        Assert.DoesNotContain("pair_secret_code", File.ReadAllText(ConfigPath));
        Assert.True(KaironCredentialStore.IssuedFor(KaironCredentialStore.Load(ConfigPath)!.Value,
            KaironCredentialStore.HashPairingCode(" pair_secret_code ")));
        Assert.False(KaironCredentialStore.IssuedFor(KaironCredentialStore.Load(ConfigPath)!.Value,
            KaironCredentialStore.HashPairingCode("pair_other")));
    }

    /// <summary>Real loopback backend for pair + confirm + normalized events.</summary>
    private sealed class PairingAndTelemetryServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource<JsonElement> _event = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string? _environment;
        private readonly string? _service;

        public string Url { get; }
        public Guid ProjectId { get; } = Guid.NewGuid();

        public PairingAndTelemetryServer(string? environment, string? service)
        {
            _environment = environment;
            _service = service;
            (_listener, Url) = LoopbackListener.Claim();
            _ = AcceptLoop(_cts.Token);
        }

        public async Task<JsonElement> WaitForEventAsync(TimeSpan timeout) => await _event.Task.WaitAsync(timeout);

        private async Task AcceptLoop(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().WaitAsync(token);
                    using var reader = new StreamReader(context.Request.InputStream);
                    var raw = await reader.ReadToEndAsync(token);
                    var path = context.Request.Url!.AbsolutePath;
                    string body;
                    var status = HttpStatusCode.OK;
                    if (path == "/api/v1/sdk/pair")
                        body = JsonSerializer.Serialize(new
                        {
                            apiKey = "krn_paired", projectId = ProjectId, pairingId = Guid.NewGuid(),
                            endpoint = Url, environment = _environment, service = _service
                        });
                    else if (path.EndsWith("/confirm"))
                    {
                        status = HttpStatusCode.NoContent;
                        body = "";
                    }
                    else
                    {
                        using var document = JsonDocument.Parse(raw);
                        var events = document.RootElement.GetProperty("events");
                        foreach (var item in events.EnumerateArray())
                            if (item.GetProperty("eventType").GetString() == "http") _event.TrySetResult(item.Clone());
                        var count = events.GetArrayLength();
                        body = $"{{\"accepted\":{count},\"duplicates\":0,\"rejected\":0}}";
                    }
                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.StatusCode = (int)status;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    if (bytes.Length > 0) await context.Response.OutputStream.WriteAsync(bytes, token);
                    context.Response.Close();
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (HttpListenerException) { }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
        }
    }
}
