using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// Runs a REAL Caddy process against the REAL deploy/Caddyfile.cloud.example, with a stub backend
/// behind it that reports exactly which headers arrived. Skipped, with a reason, when no Caddy
/// binary is available - a string assertion over the config file would prove nothing about what
/// Caddy actually does with it, which is the whole question here.
///
/// Point the tests at a specific binary with KAIRON_TEST_CADDY; otherwise "caddy" on PATH is used.
/// </summary>
public sealed class CaddyFactAttribute : FactAttribute
{
    public CaddyFactAttribute()
    {
        if (CaddyGateway.FindBinary() is null)
            Skip = "No Caddy binary found (set KAIRON_TEST_CADDY or put caddy on PATH).";
    }
}

public sealed class CaddyGatewayIntegrationTests : IClassFixture<CaddyGateway>
{
    private const string OperatorHeader = "X-Kairon-Operator-Key";

    private readonly CaddyGateway _gateway;

    public CaddyGatewayIntegrationTests(CaddyGateway gateway) => _gateway = gateway;

    // --- The SDK/Agent surface: its own credentials, never the operator's ---------------------

    [CaddyFact]
    public async Task AnSdkIngestCallNeedsNoHumanLoginAndIsNeverGivenTheOperatorKey()
    {
        var (response, upstream) = await _gateway.SendAsync(HttpMethod.Post, "/api/telemetry/metrics",
            configure: request => request.Headers.Add("X-Kairon-API-Key", "krn_project_key"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(upstream);
        Assert.Equal("krn_project_key", upstream!.Header("X-Kairon-API-Key"));
        // Being an SDK must never be a route to an operator action.
        Assert.Null(upstream.Header(OperatorHeader));
    }

    [CaddyTheory]
    [InlineData("/api/telemetry/incidents")]
    [InlineData("/api/telemetry/metrics")]
    [InlineData("/api/telemetry/events")]
    [InlineData("/api/v1/telemetry/events")]
    [InlineData("/api/v1/sdk/pair")]
    [InlineData("/api/v1/sdk/pair/3fa85f64-5717-4562-b3fc-2c963f66afa6/confirm")]
    [InlineData("/api/agent/register")]
    [InlineData("/api/agent/machines/3fa85f64-5717-4562-b3fc-2c963f66afa6/heartbeat")]
    [InlineData("/api/agent/machines/3fa85f64-5717-4562-b3fc-2c963f66afa6/user-session/heartbeat")]
    public async Task EveryPublicSdkOrAgentRouteReachesTheBackendWithoutAnOperatorKey(string path)
    {
        var (response, upstream) = await _gateway.SendAsync(HttpMethod.Post, path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(upstream);
        Assert.Null(upstream!.Header(OperatorHeader));
    }

    [CaddyTheory]
    [InlineData("/api/health")]
    [InlineData("/api/health/ready")]
    public async Task HealthChecksStayReachableAndUnprivileged(string path)
    {
        var (response, upstream) = await _gateway.SendAsync(HttpMethod.Get, path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(upstream!.Header(OperatorHeader));
    }

    // --- Stripping: a client may never supply the operator key -------------------------------

    [CaddyFact]
    public async Task AForgedOperatorKeyIsStrippedFromAPublicRoute()
    {
        // Without this the whole boundary is decorative: anyone could send the header and be
        // treated as the operator by the backend.
        var (response, upstream) = await _gateway.SendAsync(HttpMethod.Post, "/api/telemetry/metrics",
            configure: request => request.Headers.Add(OperatorHeader, "forged-by-the-caller"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(upstream!.Header(OperatorHeader));
    }

    [CaddyFact]
    public async Task AForgedOperatorKeyDoesNotSubstituteForTheHumanLogin()
    {
        var (response, upstream) = await _gateway.SendAsync(HttpMethod.Get, "/api/incidents",
            configure: request => request.Headers.Add(OperatorHeader, "forged-by-the-caller"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(upstream);
    }

    [CaddyFact]
    public async Task AnAuthenticatedRequestCarriesOnlyTheKeyCaddyInjectedNotTheOneTheClientSent()
    {
        var (response, upstream) = await _gateway.SendAsync(HttpMethod.Get, "/api/incidents",
            authenticate: true,
            configure: request => request.Headers.Add(OperatorHeader, "forged-by-the-caller"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(CaddyGateway.OperatorKey, upstream!.Header(OperatorHeader));
    }

    [CaddyTheory]
    [InlineData("/api/telemetry/metrics", true)]
    [InlineData("/api/health", false)]
    public async Task TheBrowsersAuthorizationHeaderIsNeverForwardedUpstream(string path, bool post)
    {
        var (_, upstream) = await _gateway.SendAsync(post ? HttpMethod.Post : HttpMethod.Get, path,
            authenticate: true);

        Assert.NotNull(upstream);
        Assert.Null(upstream!.Header("Authorization"));
    }

    [CaddyFact]
    public async Task TheDashboardPasswordNeverReachesTheBackendOnAnAuthenticatedOperatorCall()
    {
        var (response, upstream) = await _gateway.SendAsync(HttpMethod.Get, "/api/incidents", authenticate: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(upstream!.Header("Authorization"));
    }

    // --- The human boundary ------------------------------------------------------------------

    [CaddyTheory]
    [InlineData("/")]
    [InlineData("/incidents")]
    [InlineData("/api/incidents")]
    [InlineData("/api/v1/projects")]
    [InlineData("/api/agent/machines")]
    [InlineData("/api/v1/platform/sdk-installations")]
    public async Task EveryDashboardAndOperatorRouteRefusesAnUnauthenticatedCallerAtTheEdge(string path)
    {
        var (response, upstream) = await _gateway.SendAsync(HttpMethod.Get, path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // Refused at the edge - the request never reaches the application at all.
        Assert.Null(upstream);
    }

    [CaddyFact]
    public async Task AWrongPasswordIsRefusedAndNeverReachesTheBackend()
    {
        var (response, upstream) = await _gateway.SendAsync(HttpMethod.Get, "/api/incidents",
            configure: request => request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{CaddyGateway.DashboardUser}:wrong-password"))));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(upstream);
    }

    [CaddyFact]
    public async Task TheOperatorReadOnASharedPathIsStillGatedEvenThoughThePostIsPublic()
    {
        // GET /api/telemetry/incidents lists incidents; POST to the same path ingests one. A
        // path-only rule would have handed the read to anything holding a project API key.
        var (response, upstream) = await _gateway.SendAsync(HttpMethod.Get, "/api/telemetry/incidents",
            configure: request => request.Headers.Add("X-Kairon-API-Key", "krn_project_key"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(upstream);
    }

    [CaddyFact]
    public async Task AnAuthenticatedHumanReachesTheDashboardShell()
    {
        var (response, upstream) = await _gateway.SendAsync(HttpMethod.Get, "/", authenticate: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(CaddyGateway.OperatorKey, upstream!.Header(OperatorHeader));
    }
}

public sealed class CaddyTheoryAttribute : TheoryAttribute
{
    public CaddyTheoryAttribute()
    {
        if (CaddyGateway.FindBinary() is null)
            Skip = "No Caddy binary found (set KAIRON_TEST_CADDY or put caddy on PATH).";
    }
}

/// <summary>A real Caddy process in front of a stub backend that records what actually arrived.</summary>
public sealed class CaddyGateway : IDisposable
{
    public const string OperatorKey = "test-operator-key-do-not-reuse";
    public const string DashboardUser = "dashboard-operator";
    public const string DashboardPassword = "dashboard-password-do-not-reuse";

    private readonly Process? _caddy;
    private readonly StubBackend? _backend;
    private readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly int _gatewayPort;
    // Captured, not discarded: Caddy failing to reach a genuinely ready state (see
    // WaitUntilAnswering) is otherwise reported as nineteen confusing, identical-looking
    // "Expected OK, Actual NotFound" test failures with no indication of WHY - this is what makes
    // that diagnosable instead.
    private readonly StringBuilder _caddyLog = new();

    public CaddyGateway()
    {
        var binary = FindBinary();
        if (binary is null) return;

        _backend = new StubBackend();
        _backend.Start();
        _gatewayPort = FreePort();

        var environment = new Dictionary<string, string>
        {
            // An http:// site address keeps Caddy off its automatic-HTTPS path, so the test needs
            // no certificate while still exercising the real routing, matching and header rules.
            ["KAIRON_PUBLIC_DOMAIN"] = $"http://localhost:{_gatewayPort}",
            ["KAIRON_BACKEND_UPSTREAM"] = $"127.0.0.1:{_backend.Port}",
            ["KAIRON_OPERATOR_KEY"] = OperatorKey,
            ["KAIRON_DASHBOARD_USER"] = DashboardUser,
            ["KAIRON_DASHBOARD_PASSWORD_HASH"] = HashPassword(binary, DashboardPassword)
        };

        _caddy = StartCaddy(binary, environment);
        WaitUntilAnswering();
    }

    public static string? FindBinary()
    {
        var configured = Environment.GetEnvironmentVariable("KAIRON_TEST_CADDY");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var name = OperatingSystem.IsWindows() ? "caddy.exe" : "caddy";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not this test's problem.
            }
        }

        return null;
    }

    /// <summary>The repository's real Caddyfile - never a copy written by the test, which could
    /// drift from what actually ships.</summary>
    public static string CaddyfilePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "deploy", "Caddyfile.cloud.example");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate deploy/Caddyfile.cloud.example");
    }

    public async Task<(HttpResponseMessage Response, RecordedRequest? Upstream)> SendAsync(
        HttpMethod method, string path, bool authenticate = false,
        Action<HttpRequestMessage>? configure = null)
    {
        Assert.NotNull(_backend);
        _backend!.Requests.Clear();

        using var request = new HttpRequestMessage(method, $"http://localhost:{_gatewayPort}{path}");
        if (method == HttpMethod.Post) request.Content = new StringContent("{}");
        if (authenticate)
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{DashboardUser}:{DashboardPassword}")));
        configure?.Invoke(request);

        var response = await _client.SendAsync(request);
        await response.Content.ReadAsStringAsync();

        return (response, _backend.Requests.FirstOrDefault());
    }

    private static string HashPassword(string binary, string plaintext)
    {
        using var process = Process.Start(new ProcessStartInfo(binary)
        {
            ArgumentList = { "hash-password", "--plaintext", plaintext },
            RedirectStandardOutput = true,
            UseShellExecute = false
        })!;
        var hash = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit(30_000);
        return hash;
    }

    private Process StartCaddy(string binary, Dictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo(binary)
        {
            ArgumentList = { "run", "--config", CaddyfilePath(), "--adapter", "caddyfile" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var (key, value) in environment) startInfo.Environment[key] = value;

        var process = Process.Start(startInfo)!;
        // Captured under a lock (both streams deliver on their own threads) rather than discarded,
        // so a Caddy that never reaches a ready state - a bad Caddyfile substitution, a real config
        // error - can actually be diagnosed instead of just producing nineteen bare "Actual:
        // NotFound" failures.
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_caddyLog) _caddyLog.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_caddyLog) _caddyLog.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>
    /// Requires an actual 200 from /api/health - not merely "the socket accepted a connection and
    /// returned SOME response" - before considering Caddy ready. A caddy process can start
    /// accepting TCP connections before its Caddyfile has finished loading into active routes; in
    /// that narrow window (observed to be wide enough to matter under a loaded, slower CI runner)
    /// it answers with its own built-in 404 for "no site configured yet", which is a real HTTP
    /// response and would satisfy a check that only asked "did this throw" - letting every
    /// subsequent test in the class run against a gateway that never actually finished configuring,
    /// each failing identically and unhelpfully as "Expected OK, Actual NotFound" with no signal of
    /// why. Failing loudly here instead, with Caddy's own captured log attached, turns that into a
    /// single, actionable diagnosis at setup time.
    /// </summary>
    private void WaitUntilAnswering()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        HttpStatusCode? lastStatus = null;
        while (DateTime.UtcNow < deadline)
        {
            if (_caddy!.HasExited)
                throw new InvalidOperationException(
                    $"Caddy exited during startup (code {_caddy.ExitCode}) before it ever answered /api/health.\n--- Caddy output ---\n{_caddyLog}");

            try
            {
                using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var response = probe.GetAsync($"http://localhost:{_gatewayPort}/api/health").GetAwaiter().GetResult();
                lastStatus = response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    _backend!.Requests.Clear();
                    return;
                }
            }
            catch (Exception)
            {
                // Not yet listening at all - keep retrying until the deadline.
            }

            Thread.Sleep(250);
        }

        throw new InvalidOperationException(
            $"Caddy never answered /api/health with a success status within 30 seconds (last observed: " +
            $"{(lastStatus.HasValue ? ((int)lastStatus.Value).ToString() : "no response")}).\n--- Caddy output ---\n{_caddyLog}");
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        try
        {
            if (_caddy is { HasExited: false })
            {
                _caddy.Kill(entireProcessTree: true);
                _caddy.WaitForExit(10_000);
            }
        }
        catch (Exception)
        {
            // A already-gone process is not a test failure.
        }

        _caddy?.Dispose();
        _backend?.Dispose();
        _client.Dispose();
    }

    public sealed record RecordedRequest(string Method, string Path, Dictionary<string, string> Headers)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }

    private sealed class StubBackend : IDisposable
    {
        private HttpListener _listener = new();
        private volatile bool _running;

        public readonly ConcurrentBag<RecordedRequest> Requests = new();
        public int Port { get; private set; }

        public void Start()
        {
            // Same claim-and-retry approach as Kairon.SDK.Tests' loopback server: HttpListener
            // registers through http.sys, a different namespace from a raw TCP bind, so probing a
            // port first does not prove this one can be claimed.
            HttpListenerException? last = null;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                Port = Random.Shared.Next(20000, 60000);
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                try
                {
                    _listener.Start();
                    last = null;
                    break;
                }
                catch (HttpListenerException exception)
                {
                    last = exception;
                }
            }

            if (last is not null) throw last;

            _running = true;
            _ = Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (_running)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                var headers = context.Request.Headers.AllKeys
                    .Where(key => key is not null)
                    .ToDictionary(key => key!, key => context.Request.Headers[key] ?? "",
                        StringComparer.OrdinalIgnoreCase);
                Requests.Add(new RecordedRequest(context.Request.HttpMethod,
                    context.Request.Url?.AbsolutePath ?? "", headers));

                var body = "{\"ok\":true}"u8.ToArray();
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = body.Length;
                // HttpListener on Linux is a fully-managed implementation (Windows backs it with
                // http.sys instead), and its HTTP/1.1 keep-alive framing does not line up with what
                // Caddy's Go-based reverse-proxy client expects - confirmed via a real Linux CI run,
                // where Caddy logged a stream of "Unsolicited response received on idle HTTP channel"
                // for every proxied request, silently dropping each one instead of returning it to the
                // client, so every probe here saw only Caddy's own built-in 404. Closing the connection
                // after every response, rather than leaving it pooled for reuse, sidesteps the mismatch
                // entirely. Windows was never affected, which is why this stayed invisible until CI.
                context.Response.KeepAlive = false;
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            _running = false;
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (Exception)
            {
                // Already torn down.
            }
        }
    }
}
