using System.Net;
using System.Text;
using Kairon.SDK;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kairon.SDK.Tests;

public sealed class DependencyInjectionOnboardingTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "kairon-di-tests-" + Guid.NewGuid());
    private string ConfigPath => Path.Combine(_tempDir, "credential.json");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task AddKaironAsyncPairsPersistsAndDoesNotRetainThePairingCodeInOptions()
    {
        using var server = new PairingServer();
        var services = new ServiceCollection();

        await services.AddKaironAsync("pair_di_first_run", options =>
        {
            options.Endpoint = server.Url;
            options.CredentialPath = ConfigPath;
            options.ApplicationName = "OrdersApp";
            options.ServiceName = "OrdersService";
        });

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IOptions<KaironOptions>>().Value;

        Assert.Equal(server.ProjectId, resolved.ProjectId);
        Assert.Equal(server.Url.TrimEnd('/'), resolved.Endpoint.TrimEnd('/'));
        Assert.Equal("OrdersApp", resolved.ApplicationName);
        Assert.Equal(1, server.PairRequests);
        Assert.Equal(1, server.ConfirmationRequests);
        Assert.True(File.Exists(ConfigPath));
        Assert.DoesNotContain("pair_di_first_run", File.ReadAllText(ConfigPath));
        Assert.DoesNotContain(typeof(KaironOptions).GetProperties(), p =>
            p.Name.Contains("Pairing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AddKaironReusesTheStoredCredentialWithoutPairingOrNetworkAccess()
    {
        var server = new PairingServer();
        var firstServices = new ServiceCollection();
        await firstServices.AddKaironAsync("pair_once", options =>
        {
            options.Endpoint = server.Url;
            options.CredentialPath = ConfigPath;
        });
        server.Dispose();

        var laterServices = new ServiceCollection();
        laterServices.AddKairon(options =>
        {
            options.CredentialPath = ConfigPath;
            options.ApplicationName = "OrdersApp";
            options.ServiceName = "OrdersService";
            options.Environment = "Development";
        });

        using var provider = laterServices.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IOptions<KaironOptions>>().Value;

        Assert.Equal(server.ProjectId, resolved.ProjectId);
        Assert.Equal(server.Url.TrimEnd('/'), resolved.Endpoint.TrimEnd('/'));
        Assert.Equal("OrdersService", resolved.ServiceName);
    }

    [Fact]
    public async Task ExistingExplicitAddKaironConfigurationRemainsAuthoritative()
    {
        using var server = new PairingServer();
        var bootstrap = new ServiceCollection();
        await bootstrap.AddKaironAsync("pair_stored", options =>
        {
            options.Endpoint = server.Url;
            options.CredentialPath = ConfigPath;
        });

        var explicitProject = Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddKairon(options =>
        {
            options.Endpoint = "http://127.0.0.1:9999";
            options.ProjectId = explicitProject;
            options.ApiKey = "krn_explicit";
            options.CredentialPath = ConfigPath;
        });

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IOptions<KaironOptions>>().Value;
        Assert.Equal(explicitProject, resolved.ProjectId);
        Assert.Equal("krn_explicit", resolved.ApiKey);
        Assert.Equal("http://127.0.0.1:9999", resolved.Endpoint);
    }

    [Fact]
    public void AddKaironRejectsPartialIdentityRatherThanMixingSources()
    {
        var services = new ServiceCollection();
        services.AddKairon(options =>
        {
            options.ProjectId = Guid.NewGuid();
            options.CredentialPath = ConfigPath;
        });
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IOptions<KaironOptions>>().Value);

        Assert.Contains("both ProjectId and ApiKey", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddKaironWithoutConfigurationOrStoredCredentialFailsActionably()
    {
        var services = new ServiceCollection();
        services.AddKairon(options =>
        {
            options.CredentialPath = ConfigPath;
        });
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IOptions<KaironOptions>>().Value);

        Assert.Contains("pair once", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddKaironFailsClosedForACorruptStoredCredentialEvenWithAnEndpointConfigured()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(ConfigPath, "not a protected Kairon credential");

        var services = new ServiceCollection();
        services.AddKairon(options =>
        {
            options.Endpoint = "http://127.0.0.1:9999";
            options.CredentialPath = ConfigPath;
        });
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IOptions<KaironOptions>>().Value);
        Assert.Contains("project and API key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SynchronousAddKaironDoesNotBypassPendingPairingConfirmation()
    {
        KaironCredentialStore.Save(
            ConfigPath,
            "http://127.0.0.1:8000",
            Guid.NewGuid(),
            "krn_pending",
            Guid.NewGuid());

        var services = new ServiceCollection();
        services.AddKairon(options =>
        {
            options.CredentialPath = ConfigPath;
        });
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IOptions<KaironOptions>>().Value);

        Assert.Contains("AddKaironAsync", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsyncStoredCredentialRegistrationRecoversPendingConfirmationWithoutPairingAgain()
    {
        using var server = new PairingServer();
        KaironCredentialStore.Save(
            ConfigPath,
            server.Url,
            server.ProjectId,
            "krn_pending",
            server.PairingId);

        var services = new ServiceCollection();
        await services.AddKaironAsync(options =>
        {
            options.CredentialPath = ConfigPath;
            options.ApplicationName = "RecoveredApp";
        });

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IOptions<KaironOptions>>().Value;
        var stored = KaironCredentialStore.Load(ConfigPath);

        Assert.Equal(0, server.PairRequests);
        Assert.Equal(1, server.ConfirmationRequests);
        Assert.Equal(server.ProjectId, resolved.ProjectId);
        Assert.Equal(server.Url.TrimEnd('/'), resolved.Endpoint.TrimEnd('/'));
        Assert.Equal("krn_pending", resolved.ApiKey);
        Assert.NotNull(stored);
        Assert.Null(stored!.Value.PendingConfirmationPairingId);
    }

    [Fact]
    public async Task UseKaironInstrumentsRequestsWithAStoredCredential()
    {
        KaironCredentialStore.Save(ConfigPath, "http://127.0.0.1:8000", Guid.NewGuid(), "krn_stored");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKairon(options =>
        {
            options.CredentialPath = ConfigPath;
            options.ApplicationName = "StoredCredentialApp";
            options.EnableMetrics = false;
        });

        using var provider = services.BuildServiceProvider();
        var application = new ApplicationBuilder(provider);
        application.UseKairon();
        application.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });
        var pipeline = application.Build();

        await pipeline(new DefaultHttpContext { RequestServices = provider });

        var queue = provider.GetRequiredService<IKaironTelemetryQueue>();
        Assert.Equal(1, queue.PendingCount);
    }

    private sealed class PairingServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private int _pairRequests;
        private int _confirmationRequests;

        public string Url { get; }
        public Guid ProjectId { get; } = Guid.NewGuid();
        public Guid PairingId { get; } = Guid.NewGuid();
        public int PairRequests => Volatile.Read(ref _pairRequests);
        public int ConfirmationRequests => Volatile.Read(ref _confirmationRequests);

        public PairingServer()
        {
            (_listener, Url) = LoopbackListener.Claim();
            _ = AcceptLoop(_cts.Token);
        }

        private async Task AcceptLoop(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().WaitAsync(token);
                    string body;
                    if (context.Request.Url?.AbsolutePath.EndsWith("/confirm", StringComparison.Ordinal) == true)
                    {
                        Interlocked.Increment(ref _confirmationRequests);
                        body = "{}";
                    }
                    else
                    {
                        Interlocked.Increment(ref _pairRequests);
                        body = $$"""{"apiKey":"krn_di_key","projectId":"{{ProjectId}}","pairingId":"{{PairingId}}","endpoint":"{{Url.TrimEnd('/')}}"}""";
                    }

                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.StatusCode = StatusCodes.Status200OK;
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

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
        }
    }
}
