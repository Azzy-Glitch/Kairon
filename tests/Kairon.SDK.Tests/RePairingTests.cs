using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using Kairon.SDK;
using Kairon.SDK.Models;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kairon.SDK.Tests;

/// <summary>
/// Explicit SDK re-pairing: `new KaironClient(pairingCode: "...")` must always (re)pair
/// immediately, even when a stored (or explicit) credential already resolves a project -
/// overriding it with the freshly redeemed credential - while an HTTP 401 from telemetry must
/// never trigger pairing on its own and must never delete/modify the stored credential file.
/// Mirrors sdk-python/tests/test_repairing.py's required-semantics acceptance scenario: an
/// existing app's credential is revoked, telemetry starts failing with 401, and only an explicit
/// pairingCode (never automatic behavior) recovers it.
///
/// KaironCredentialStore is internal, so - unlike the Python SDK's test suite, which can reach
/// its equivalent module directly - a "pre-existing stored credential" is always bootstrapped here
/// via one real pairing redemption against the fake server, exactly like
/// KaironTests.SecondRunReusesTheStoredCredentialWithoutRedeemingAgain already does.
/// </summary>
public sealed class RePairingTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "kairon-sdk-repair-tests-" + Guid.NewGuid());
    private string ConfigPath => Path.Combine(_tempDir, "credential.json");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private void SeedStoredCredential(FakeRepairServer server, string projectId, string apiKey)
    {
        server.NextPairingBody = JsonSerializer.Serialize(new { apiKey, projectId, pairingId = "99999999-9999-9999-9999-999999999999", endpoint = server.Url });
        using (new KaironClient(pairingCode: "pair_seed", endpoint: server.Url, configPath: ConfigPath)) { }
    }

    private static async Task<TelemetryResponse> SendWithKeyAsync(FakeRepairServer server, string apiKey)
    {
        var options = Options.Create(new KaironOptions { Endpoint = server.Url, ProjectId = Guid.NewGuid(), ApiKey = apiKey });
        using var http = new HttpClient { BaseAddress = new Uri(server.Url.TrimEnd('/') + "/") };
        return (await new KaironTelemetryClient(http, options)
            .SendAsync(new TelemetryPayload { Endpoint = "/x", Method = "GET", StatusCode = 200 }))!;
    }

    [Fact]
    public async Task ExplicitPairingCodeOverridesAnExistingStoredCredential()
    {
        using var server = new FakeRepairServer();
        SeedStoredCredential(server, "11111111-1111-1111-1111-111111111111", "krn_old_key");
        server.PairingCalls = 0; // only count what this test itself triggers

        server.NextPairingBody = JsonSerializer.Serialize(new
        {
            apiKey = "krn_new_key", projectId = "77777777-7777-7777-7777-777777777777", pairingId = "99999999-9999-9999-9999-999999999999", endpoint = server.Url
        });
        await using var repaired = new KaironClient(pairingCode: "pair_repair", endpoint: server.Url, configPath: ConfigPath);

        Assert.Equal(Guid.Parse("77777777-7777-7777-7777-777777777777"), repaired.ProjectId);
        Assert.Equal(1, server.PairingCalls);

        // A second construction with no pairingCode must now reuse the FRESH credential, not the
        // stale one it replaced - proving the stored file was actually overwritten.
        await using var again = new KaironClient(endpoint: "http://127.0.0.1:1", configPath: ConfigPath);
        Assert.Equal(Guid.Parse("77777777-7777-7777-7777-777777777777"), again.ProjectId);
    }

    [Fact]
    public async Task ExplicitPairingCodeOverridesExplicitProjectIdAndApiKeyArgumentsToo()
    {
        using var server = new FakeRepairServer();
        server.NextPairingBody = JsonSerializer.Serialize(new
        {
            apiKey = "krn_new_key", projectId = "77777777-7777-7777-7777-777777777777", pairingId = "99999999-9999-9999-9999-999999999999", endpoint = server.Url
        });

        await using var repaired = new KaironClient(
            pairingCode: "pair_repair", endpoint: server.Url,
            projectId: Guid.Parse("11111111-1111-1111-1111-111111111111"), apiKey: "krn_explicit_ignored",
            configPath: ConfigPath);

        Assert.Equal(Guid.Parse("77777777-7777-7777-7777-777777777777"), repaired.ProjectId);
    }

    [Fact]
    public async Task PairingConfirmsWithTheBackendAfterRedemption()
    {
        using var server = new FakeRepairServer();
        // The redeemed "endpoint" must point back at THIS server, not the default fixture body's
        // fixed placeholder - the confirm call is sent to whatever endpoint redemption returned.
        server.NextPairingBody = JsonSerializer.Serialize(new
        {
            apiKey = "krn_old_key", projectId = "11111111-1111-1111-1111-111111111111", pairingId = "99999999-9999-9999-9999-999999999999", endpoint = server.Url
        });

        await using var kairon = new KaironClient(pairingCode: "pair_confirmme", endpoint: server.Url, configPath: ConfigPath);

        Assert.Equal(1, server.ConfirmCalls);
        Assert.Equal("krn_old_key", server.LastConfirmApiKey);
    }

    [Fact]
    public void PersistenceFailurePreventsConfirmationAndPropagatesTheError()
    {
        using var server = new FakeRepairServer();
        server.NextPairingBody = JsonSerializer.Serialize(new
        {
            apiKey = "krn_new_key", projectId = "77777777-7777-7777-7777-777777777777", pairingId = "99999999-9999-9999-9999-999999999999", endpoint = server.Url
        });

        // Force KaironCredentialStore.Save to fail: making _tempDir itself an ordinary FILE means
        // creating "<_tempDir>/credential.json"'s parent directory (_tempDir) is impossible.
        File.WriteAllText(_tempDir, "occupies the path Save would need as a directory");
        try
        {
            var badConfigPath = Path.Combine(_tempDir, "credential.json");

            Assert.ThrowsAny<IOException>(
                () => new KaironClient(pairingCode: "pair_diskfull", endpoint: server.Url, configPath: badConfigPath));

            // Confirmation is proof the SDK durably persisted the credential - it must never be
            // sent when persistence itself failed, even though the backend already issued a real
            // credential.
            Assert.Equal(0, server.ConfirmCalls);
        }
        finally
        {
            if (File.Exists(_tempDir)) File.Delete(_tempDir);
        }
    }

    [Fact]
    public async Task ConfirmationNetworkFailureDoesNotBlockOnboardingOrChangeTheCredential()
    {
        using var server = new FakeRepairServer();
        // The backend-reported "endpoint" (where confirm is POSTed) can differ from - and be less
        // reachable than - the endpoint used to redeem the code. Confirmation is best-effort: its
        // own request failing must never fail pairing, revert the just-persisted credential, or
        // delete it.
        server.NextPairingBody = JsonSerializer.Serialize(new
        {
            apiKey = "krn_new_key", projectId = "77777777-7777-7777-7777-777777777777", pairingId = "99999999-9999-9999-9999-999999999999", endpoint = "http://127.0.0.1:1"
        });

        await using var kairon = new KaironClient(pairingCode: "pair_lossy_confirm", endpoint: server.Url, configPath: ConfigPath);

        Assert.Equal(Guid.Parse("77777777-7777-7777-7777-777777777777"), kairon.ProjectId);
        Assert.True(File.Exists(ConfigPath));
        Assert.Equal(0, server.ConfirmCalls); // confirm's own POST never reached a real server
    }

    // --- Required precedence: pairingCode > stored credential > ordinary configuration --------

    [Fact]
    public async Task StoredCredentialOverridesEnvironmentConfiguration()
    {
        using var server = new FakeRepairServer();
        SeedStoredCredential(server, "11111111-1111-1111-1111-111111111111", "krn_stored_key");

        Environment.SetEnvironmentVariable("KAIRON_PROJECT_ID", "22222222-2222-2222-2222-222222222222");
        Environment.SetEnvironmentVariable("KAIRON_API_KEY", "krn_env_key");
        try
        {
            await using var kairon = new KaironClient(endpoint: server.Url, configPath: ConfigPath);
            Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), kairon.ProjectId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KAIRON_PROJECT_ID", null);
            Environment.SetEnvironmentVariable("KAIRON_API_KEY", null);
        }
    }

    [Fact]
    public async Task StoredCredentialOverridesExplicitConfigurationAndIsNeverMixed()
    {
        using var server = new FakeRepairServer();
        SeedStoredCredential(server, "11111111-1111-1111-1111-111111111111", "krn_stored_key");

        // An explicit projectId for a DIFFERENT project, alongside a stored credential for this
        // one - the required precedence is that storage wins wholesale, so the result must be
        // exactly the stored identity, never a hybrid of the two sources.
        await using var kairon = new KaironClient(
            endpoint: server.Url,
            projectId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            apiKey: "krn_explicit_key",
            configPath: ConfigPath);

        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), kairon.ProjectId);
    }

    [Fact]
    public async Task A401DoesNotDeleteOrModifyTheStoredCredentialFile()
    {
        using var server = new FakeRepairServer();
        SeedStoredCredential(server, "11111111-1111-1111-1111-111111111111", "krn_stale_key");
        server.PairingCalls = 0;
        server.AcceptedApiKey = "krn_current_key"; // the stored key no longer matches what the backend accepts
        var before = File.ReadAllBytes(ConfigPath);

        var result = await SendWithKeyAsync(server, "krn_stale_key");

        Assert.False(result.Success);
        Assert.Contains("401", result.Message);
        Assert.Contains("project authentication rejected", result.Message);
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        Assert.Equal(0, server.PairingCalls);
    }

    [Fact]
    public async Task RevokedCredentialThenExplicitPairingRecoversTelemetry()
    {
        using var server = new FakeRepairServer();
        SeedStoredCredential(server, "11111111-1111-1111-1111-111111111111", "krn_old_key");
        server.PairingCalls = 0;
        server.AcceptedApiKey = "krn_new_key"; // simulate: backend already revoked the old credential

        var staleResult = await SendWithKeyAsync(server, "krn_old_key");
        Assert.False(staleResult.Success);
        Assert.Contains("401", staleResult.Message);
        Assert.Equal(0, server.PairingCalls); // no automatic re-pair happened

        server.NextPairingBody = JsonSerializer.Serialize(new
        {
            apiKey = "krn_new_key", projectId = "11111111-1111-1111-1111-111111111111", pairingId = "99999999-9999-9999-9999-999999999999", endpoint = server.Url
        });
        await using var repaired = new KaironClient(pairingCode: "pair_recover", endpoint: server.Url, configPath: ConfigPath);

        var freshResult = await SendWithKeyAsync(server, "krn_new_key");
        Assert.True(freshResult.Success);
    }

    [Fact]
    public void APairingCodeCannotBeRedeemedTwice()
    {
        using var server = new FakeRepairServer();
        server.NextPairingBody = JsonSerializer.Serialize(new
        {
            apiKey = "krn_new_key", projectId = "77777777-7777-7777-7777-777777777777", pairingId = "99999999-9999-9999-9999-999999999999", endpoint = server.Url
        });

        using (new KaironClient(pairingCode: "pair_onceonly", endpoint: server.Url, configPath: ConfigPath)) { }
        Assert.Equal(1, server.PairingCalls);

        server.NextPairingStatus = HttpStatusCode.BadRequest;
        server.NextPairingBody = """{"error":"Pairing code is invalid, expired, revoked, or already used."}""";

        var ex = Assert.Throws<InvalidOperationException>(
            () => new KaironClient(pairingCode: "pair_onceonly", endpoint: server.Url, configPath: ConfigPath));

        Assert.Contains("pairing failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, server.PairingCalls); // attempted once more, not retried in a loop
    }

    [Fact]
    public async Task ALostConfirmationIsRetriedOnALaterRunAndThePendingMarkerIsThenCleared()
    {
        // Simulates the confirmation response getting lost the first time (a transient network
        // failure, or the process exiting right after redemption) - recovery must not re-redeem a
        // code or generate one; it must retry confirming with the SAME already-issued credential,
        // using only the non-secret pairing id already recorded alongside it.
        using var server = new FakeRepairServer { ConfirmShouldFail = true };
        server.NextPairingBody = JsonSerializer.Serialize(new
        {
            apiKey = "krn_new_key", projectId = "77777777-7777-7777-7777-777777777777", pairingId = "99999999-9999-9999-9999-999999999999", endpoint = server.Url
        });

        await using (var first = new KaironClient(pairingCode: "pair_lostconfirm", endpoint: server.Url, configPath: ConfigPath))
        {
            Assert.Equal(Guid.Parse("77777777-7777-7777-7777-777777777777"), first.ProjectId);
        }
        var callsAfterFirstRun = server.ConfirmCalls;
        Assert.True(callsAfterFirstRun > 0); // it did try, more than once (bounded retry), just never succeeded

        // A later run - crucially, with NO pairingCode at all - recovers the confirmation using
        // only the stored credential and its recorded pending pairing id.
        server.ConfirmShouldFail = false;
        await using (var second = new KaironClient(endpoint: "http://127.0.0.1:1", configPath: ConfigPath))
        {
            Assert.Equal(Guid.Parse("77777777-7777-7777-7777-777777777777"), second.ProjectId);
        }
        Assert.True(server.ConfirmCalls > callsAfterFirstRun); // it actually retried and succeeded

        // A THIRD run must not keep retrying a confirmation that already succeeded.
        var callsAfterRecovery = server.ConfirmCalls;
        await using (var third = new KaironClient(endpoint: "http://127.0.0.1:1", configPath: ConfigPath)) { }
        Assert.Equal(callsAfterRecovery, server.ConfirmCalls);
    }

    [Fact]
    public async Task APermanentlyLostConfirmationNeverBlocksOnboardingOrDeletesTheCredential()
    {
        using var server = new FakeRepairServer { ConfirmShouldFail = true };
        server.NextPairingBody = JsonSerializer.Serialize(new
        {
            apiKey = "krn_new_key", projectId = "77777777-7777-7777-7777-777777777777", pairingId = "99999999-9999-9999-9999-999999999999", endpoint = server.Url
        });

        await using var kairon = new KaironClient(pairingCode: "pair_neverconfirmed", endpoint: server.Url, configPath: ConfigPath);

        Assert.Equal(Guid.Parse("77777777-7777-7777-7777-777777777777"), kairon.ProjectId);
        var result = await SendWithKeyAsync(server, "krn_new_key");
        Assert.True(result.Success); // the credential remains fully usable despite confirmation never succeeding
    }

    [Fact]
    public async Task ConcurrentCredentialWritesNeverCorruptTheStoredFile()
    {
        // Multiple overlapping saves to the SAME configPath must never leave it half-written or
        // unreadable - each writer's own uniquely named temp file means no two writers can clobber
        // each other's in-progress write, and the final File.Move is what atomically decides which
        // one's contents actually land.
        var writers = Enumerable.Range(1, 12).Select(n => Task.Run(async () =>
        {
            using var server = new FakeRepairServer();
            server.NextPairingBody = JsonSerializer.Serialize(new
            {
                apiKey = $"krn_key_{n}", projectId = $"{n:00000000}-0000-0000-0000-000000000000", pairingId = "99999999-9999-9999-9999-999999999999", endpoint = server.Url
            });
            await using var kairon = new KaironClient(pairingCode: $"pair_concurrent_{n}", endpoint: server.Url, configPath: ConfigPath);
        }));

        await Task.WhenAll(writers);

        // Whichever write landed last, the file itself is fully intact and loadable.
        await using var final = new KaironClient(endpoint: "http://127.0.0.1:1", configPath: ConfigPath);
        Assert.EndsWith("-0000-0000-0000-000000000000", final.ProjectId.ToString());
    }

    [Fact]
    public void CreateAsyncDoesNotDeadlockUnderACapturedSingleThreadedSynchronizationContext()
    {
        // Reproduces the classic ASP.NET-classic/WinForms/WPF deadlock shape: a single-threaded
        // synchronization context whose one thread is busy synchronously waiting - anything that
        // needs to resume back on that exact thread (as `.GetAwaiter().GetResult()` on a
        // continuation captured with the default awaiter config would) hangs forever. CreateAsync
        // must complete cleanly here precisely because it never blocks synchronously on its own
        // async work internally.
        using var server = new FakeRepairServer();
        server.NextPairingBody = JsonSerializer.Serialize(new
        {
            apiKey = "krn_new_key", projectId = "77777777-7777-7777-7777-777777777777", pairingId = "99999999-9999-9999-9999-999999999999", endpoint = server.Url
        });

        // The pump - and the blocking .GetAwaiter().GetResult() call it drives - run on their OWN
        // dedicated background thread (mimicking a real UI thread), never on this xUnit test
        // thread: if CreateAsync ever regresses back into deadlocking under a captured context, it
        // is that background thread that hangs forever, not this test - Join's own timeout below
        // still lets the test fail fast and cleanly either way, rather than hanging the whole run.
        KaironClient? client = null;
        Exception? backgroundError = null;
        var pumpThread = new Thread(() =>
        {
            var singleThreaded = new SingleThreadedSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(singleThreaded);
            singleThreaded.Post(_ =>
            {
                try
                {
                    // Blocks THIS single thread synchronously waiting for CreateAsync - exactly
                    // the deadlock-prone pattern this test exists to prove is now safe.
                    client = KaironClient.CreateAsync(pairingCode: "pair_syncctx", endpoint: server.Url, configPath: ConfigPath)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    backgroundError = ex;
                }
                finally
                {
                    singleThreaded.Complete();
                }
            }, null);
            singleThreaded.Pump();
        }) { IsBackground = true };

        pumpThread.Start();
        var completed = pumpThread.Join(TimeSpan.FromSeconds(10));

        Assert.True(completed, "CreateAsync deadlocked under a captured synchronization context.");
        Assert.Null(backgroundError);
        Assert.NotNull(client);
        Assert.Equal(Guid.Parse("77777777-7777-7777-7777-777777777777"), client!.ProjectId);
        client.Dispose();
    }

    /// <summary>Minimal single-threaded synchronization context test double: a message-loop-style
    /// context (like WinForms/WPF/ASP.NET-classic) where every posted callback runs on ONE thread -
    /// the well-established shape used to reproduce (or prove the absence of) the classic
    /// sync-over-async deadlock in a unit test.</summary>
    private sealed class SingleThreadedSynchronizationContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback, object?)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public void Complete() => _queue.CompleteAdding();

        public void Pump()
        {
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                SetSynchronizationContext(this);
                callback(state);
            }
        }
    }

    [Fact]
    public void AnExpiredOrCancelledPairingCodeFailsCleanlyWithoutPersistingAnything()
    {
        using var server = new FakeRepairServer();
        server.NextPairingStatus = HttpStatusCode.BadRequest;
        server.NextPairingBody = """{"error":"Pairing code is invalid, expired, revoked, or already used."}""";

        var ex = Assert.Throws<InvalidOperationException>(
            () => new KaironClient(pairingCode: "pair_expired", endpoint: server.Url, configPath: ConfigPath));

        Assert.Contains("pairing failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(ConfigPath));
    }

    private sealed class FakeRepairServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        public string Url { get; }
        public string AcceptedApiKey = "krn_old_key";
        public int PairingCalls;
        public int ConfirmCalls;
        public bool ConfirmShouldFail;
        public string? LastConfirmApiKey;
        public HttpStatusCode NextPairingStatus = HttpStatusCode.OK;
        public string NextPairingBody = """{"apiKey":"krn_old_key","projectId":"11111111-1111-1111-1111-111111111111","pairingId":"99999999-9999-9999-9999-999999999999","endpoint":"http://127.0.0.1:8000"}""";

        public FakeRepairServer()
        {
            var port = GetFreePort();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _ = AcceptLoop(_cts.Token);
        }

        private async Task AcceptLoop(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().WaitAsync(token);
                    await HandleAsync(context, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (HttpListenerException) { }
        }

        private async Task HandleAsync(HttpListenerContext context, CancellationToken token)
        {
            using var reader = new StreamReader(context.Request.InputStream);
            var raw = await reader.ReadToEndAsync(token);

            if (context.Request.Url!.AbsolutePath == "/api/v1/sdk/pair")
            {
                Interlocked.Increment(ref PairingCalls);
                if (NextPairingStatus == HttpStatusCode.OK)
                {
                    using var doc = JsonDocument.Parse(NextPairingBody);
                    AcceptedApiKey = doc.RootElement.GetProperty("apiKey").GetString()!;
                }
                await RespondAsync(context, NextPairingStatus, NextPairingBody, token);
                return;
            }

            if (context.Request.Url.AbsolutePath.StartsWith("/api/v1/sdk/pair/") && context.Request.Url.AbsolutePath.EndsWith("/confirm"))
            {
                Interlocked.Increment(ref ConfirmCalls);
                using var doc = JsonDocument.Parse(raw);
                LastConfirmApiKey = doc.RootElement.TryGetProperty("apiKey", out var apiKeyProp) ? apiKeyProp.GetString() : null;
                if (ConfirmShouldFail)
                {
                    await RespondAsync(context, HttpStatusCode.InternalServerError, "", token);
                    return;
                }
                await RespondAsync(context, HttpStatusCode.NoContent, "", token);
                return;
            }

            var supplied = context.Request.Headers["X-Kairon-API-Key"];
            if (supplied != AcceptedApiKey)
            {
                await RespondAsync(context, HttpStatusCode.Unauthorized, "", token);
                return;
            }
            await RespondAsync(context, HttpStatusCode.OK, """{"success":true,"message":"ok","telemetryId":"abc"}""", token);
        }

        private static async Task RespondAsync(HttpListenerContext context, HttpStatusCode status, string body, CancellationToken token)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            if (bytes.Length > 0) await context.Response.OutputStream.WriteAsync(bytes, token);
            context.Response.Close();
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
