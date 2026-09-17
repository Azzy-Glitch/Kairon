using System.Net;
using System.Reflection;
using System.Text;
using Xunit;

namespace Kairon.SDK.Tests;

/// <summary>
/// RB-002: a compromised or malicious KAIRON backend must never be able to redirect this SDK's
/// requests onto a different origin and have credentials/telemetry follow transparently. A custom
/// <see cref="HttpMessageHandler"/> stub (used everywhere else in this test project) cannot prove
/// this - AllowAutoRedirect is a property of the REAL handler
/// (<see cref="HttpClientHandler"/>/SocketsHttpHandler), which a stub subclass bypasses entirely by
/// overriding SendAsync itself. These tests use a real loopback HTTP listener so the actual
/// redirect-following machinery is exercised.
/// </summary>
public sealed class RedirectSecurityTests
{
    [Fact]
    public async Task NonRedirectingHandlerDoesNotFollowARedirectResponse()
    {
        using var server = new LoopbackRedirectServer();
        server.Start();

        using var client = new HttpClient(KaironEndpointSecurity.CreateNonRedirectingHandler());
        var response = await client.GetAsync(server.OriginUrl);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(1, server.OriginHitCount);
        Assert.Equal(0, server.TargetHitCount);
    }

    [Fact]
    public async Task DefaultHandlerWouldHaveFollowedTheSameRedirect()
    {
        // Proves the previous test is not vacuous: a default handler (this SDK's old behavior)
        // really does follow the exact same server response.
        using var server = new LoopbackRedirectServer();
        server.Start();

        using var client = new HttpClient(new HttpClientHandler());
        var response = await client.GetAsync(server.OriginUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, server.TargetHitCount);
    }

    [Fact]
    public async Task ACredentialHeaderIsNeverForwardedToARedirectTarget()
    {
        using var server = new LoopbackRedirectServer();
        server.Start();

        using var client = new HttpClient(KaironEndpointSecurity.CreateNonRedirectingHandler());
        using var request = new HttpRequestMessage(HttpMethod.Get, server.OriginUrl);
        request.Headers.Add("X-Kairon-API-Key", "super-secret-key");

        await client.SendAsync(request);

        Assert.Equal(0, server.TargetHitCount);
        Assert.False(server.TargetSawApiKeyHeader);
    }

    private sealed class LoopbackRedirectServer : IDisposable
    {
        private HttpListener _listener = new();
        private string _baseUrl = "";
        private volatile bool _running;

        public int OriginHitCount;
        public int TargetHitCount;
        public bool TargetSawApiKeyHeader;

        public string OriginUrl => _baseUrl + "/origin";
        private string TargetUrl => _baseUrl + "/target";

        public void Start()
        {
            (_listener, _baseUrl) = LoopbackListener.Claim();
            _running = true;
            _ = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch
                {
                    return;
                }

                if (ctx.Request.Url!.AbsolutePath == "/origin")
                {
                    Interlocked.Increment(ref OriginHitCount);
                    ctx.Response.StatusCode = (int)HttpStatusCode.Redirect;
                    ctx.Response.RedirectLocation = TargetUrl;
                    ctx.Response.Close();
                }
                else
                {
                    Interlocked.Increment(ref TargetHitCount);
                    if (ctx.Request.Headers["X-Kairon-API-Key"] is not null)
                        TargetSawApiKeyHeader = true;
                    ctx.Response.StatusCode = (int)HttpStatusCode.OK;
                    ctx.Response.Close();
                }
            }
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch { /* best-effort teardown */ }
            try { _listener.Close(); } catch { /* best-effort teardown */ }
        }
    }
}

/// <summary>
/// Finding 2 (previous release audit): <see cref="KaironPairingClient.PairAsync"/> and
/// <see cref="KaironPairingClient.ConfirmAsync"/> accept an OPTIONAL caller-supplied
/// <see cref="HttpMessageHandler"/> ("exposed only so tests can stub the HTTP response"). Before
/// the fix, a caller who supplied their own real handler - rather than omitting it and getting this
/// SDK's own <see cref="KaironEndpointSecurity.CreateNonRedirectingHandler"/> - got exactly that
/// handler's own redirect behavior, unmodified. A pairing code (PairAsync's POST body) and a freshly
/// issued API key (ConfirmAsync's POST body) both survive a 307/308 redirect verbatim, so a
/// malicious or compromised backend could harvest either merely by answering with one - PROVIDED
/// the caller's handler actually follows redirects, which is the default for a bare
/// <see cref="HttpClientHandler"/>.
///
/// These tests build that exact bare, default-settings handler, hand it to PairAsync/ConfirmAsync,
/// and point it at a real loopback server that redirects the POST to a second "attacker" listener -
/// proving the attacker listener is never contacted, for every redirect status a POST body survives
/// (307, 308) and the ones it does not (301, 302, 303 - covered for completeness: even a downgraded,
/// bodyless GET replay of the ORIGINAL destination is still one this SDK must not make).
/// </summary>
public sealed class PairingRedirectSecurityTests
{
    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]   // 301
    [InlineData(HttpStatusCode.Found)]               // 302
    [InlineData(HttpStatusCode.SeeOther)]             // 303
    [InlineData(HttpStatusCode.TemporaryRedirect)]    // 307 - preserves method + body
    [InlineData(HttpStatusCode.PermanentRedirect)]    // 308 - preserves method + body
    public async Task PairAsyncNeverForwardsThePairingCodeToARedirectTargetEvenWithACallerSuppliedHandler(
        HttpStatusCode redirectStatus)
    {
        using var attacker = new CapturingPostServer();
        attacker.Start();
        using var origin = new PostRedirectServer(redirectStatus, () => attacker.BaseUrl + "/harvest");
        origin.Start();

        // The bare, default-settings handler PairAsync's own doc comment assumes tests will supply
        // "to stub the HTTP response" - AllowAutoRedirect is true here, exactly like `new
        // HttpClientHandler()` with no further configuration. This is the realistic misuse case:
        // nobody has to have set anything maliciously for the old code to be exploitable.
        var handler = new HttpClientHandler();

        var result = await KaironPairingClient.PairAsync(origin.BaseUrl, "pair_secret_code_do_not_leak",
            handler: handler);

        Assert.False(result.Success);
        Assert.Equal(0, attacker.HitCount);
        Assert.DoesNotContain("pair_secret_code_do_not_leak", attacker.LastBody ?? "");
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task ConfirmAsyncNeverForwardsTheApiKeyToARedirectTargetEvenWithACallerSuppliedHandler(
        HttpStatusCode redirectStatus)
    {
        using var attacker = new CapturingPostServer();
        attacker.Start();
        using var origin = new PostRedirectServer(redirectStatus, () => attacker.BaseUrl + "/harvest");
        origin.Start();

        var handler = new HttpClientHandler();

        var confirmed = await KaironPairingClient.ConfirmAsync(origin.BaseUrl, Guid.NewGuid(),
            "krn_secret_api_key_do_not_leak", handler: handler, attempts: 1);

        Assert.False(confirmed);
        Assert.Equal(0, attacker.HitCount);
        Assert.DoesNotContain("krn_secret_api_key_do_not_leak", attacker.LastBody ?? "");
    }

    [Fact]
    public async Task ASameOriginRedirectGetsNoMoreTrustThanACrossOriginOne()
    {
        // Same-origin is exactly where a "it's the same host, surely that's fine" exemption would
        // look safe. It is not: the path is still attacker-chosen, and the body would still be
        // replayed - so this is refused identically to the cross-origin case above.
        using var origin = new PostRedirectServer(HttpStatusCode.TemporaryRedirect,
            () => "/different-path-same-host");
        origin.Start();

        var result = await KaironPairingClient.PairAsync(origin.BaseUrl, "pair_secret_code",
            handler: new HttpClientHandler());

        Assert.False(result.Success);
        Assert.Equal(1, origin.OriginHitCount); // only the first request landed; no second hop
    }

    [Fact]
    public async Task ADelegatingHandlerWrappingARedirectFollowingHandlerIsAlsoNeutralized()
    {
        // Confirms KaironEndpointSecurity.DisableAutoRedirect walks the DelegatingHandler chain
        // rather than only recognizing a bare HttpClientHandler passed directly.
        using var attacker = new CapturingPostServer();
        attacker.Start();
        using var origin = new PostRedirectServer(HttpStatusCode.TemporaryRedirect,
            () => attacker.BaseUrl + "/harvest");
        origin.Start();

        var wrapped = new PassThroughDelegatingHandler(new HttpClientHandler());

        var result = await KaironPairingClient.PairAsync(origin.BaseUrl, "pair_secret_code",
            handler: wrapped);

        Assert.False(result.Success);
        Assert.Equal(0, attacker.HitCount);
    }

    [Fact]
    public async Task OmittingTheHandlerStillWorksExactlyAsBefore()
    {
        // The overwhelmingly common case (production code never passes `handler:` at all) must be
        // completely unaffected by DisableAutoRedirect ever being called - it must never be called
        // when handler is null in the first place (that path already gets a fresh
        // CreateNonRedirectingHandler()), which this guards against regressing.
        using var attacker = new CapturingPostServer();
        attacker.Start();
        using var origin = new PostRedirectServer(HttpStatusCode.TemporaryRedirect,
            () => attacker.BaseUrl + "/harvest");
        origin.Start();

        var result = await KaironPairingClient.PairAsync(origin.BaseUrl, "pair_secret_code");

        Assert.False(result.Success);
        Assert.Equal(0, attacker.HitCount);
    }

    /// <summary>A pass-through DelegatingHandler with no logic of its own - stands in for any
    /// wrapping a real consumer might add (logging, telemetry, auth) around their actual transport
    /// handler.</summary>
    private sealed class PassThroughDelegatingHandler : DelegatingHandler
    {
        public PassThroughDelegatingHandler(HttpMessageHandler inner) : base(inner) { }
    }

    /// <summary>Answers every POST with a redirect to a caller-supplied location, and separately
    /// records whether more than one request ever arrived (proving no second hop happened here).</summary>
    private sealed class PostRedirectServer : IDisposable
    {
        private readonly HttpStatusCode _status;
        private readonly Func<string> _location;
        private HttpListener _listener = new();
        private volatile bool _running;

        public int OriginHitCount;
        public string BaseUrl { get; private set; } = "";

        public PostRedirectServer(HttpStatusCode status, Func<string> location)
        {
            _status = status;
            _location = location;
        }

        public void Start()
        {
            (_listener, BaseUrl) = LoopbackListener.Claim();
            _running = true;
            _ = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }

                Interlocked.Increment(ref OriginHitCount);
                ctx.Response.StatusCode = (int)_status;
                ctx.Response.RedirectLocation = _location();
                ctx.Response.Close();
            }
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }

    /// <summary>Stands in for the attacker's server: records every request it receives and the raw
    /// body of the last one. The entire point of every test above is that <see cref="HitCount"/>
    /// stays zero.</summary>
    private sealed class CapturingPostServer : IDisposable
    {
        private HttpListener _listener = new();
        private volatile bool _running;

        public int HitCount;
        public string? LastBody;
        public string BaseUrl { get; private set; } = "";

        public void Start()
        {
            (_listener, BaseUrl) = LoopbackListener.Claim();
            _running = true;
            _ = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }

                Interlocked.Increment(ref HitCount);
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                LastBody = await reader.ReadToEndAsync();
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
            }
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }
}

/// <summary>
/// Finding 1 (previous release audit): <see cref="KaironTelemetryClient"/>'s constructor took a
/// caller-supplied <see cref="HttpClient"/> directly and was PUBLIC - an already-constructed
/// HttpClient exposes no way to inspect or change the handler chain baked into it, so a caller
/// handing in a plain <c>new HttpClient()</c> (auto-redirect on by default) could make this class
/// forward <c>X-Kairon-API-Key</c> to wherever a malicious backend's 3xx response pointed, with no
/// way for KaironTelemetryClient to detect or prevent it after the fact. The constructor is now
/// internal; this is a compiler-enforced guarantee, not a runtime one, so what is worth regression-
/// testing is that the guarantee stays in place - this fails loudly if a future change makes the
/// constructor public again (accidentally or "for convenience"), rather than silently reopening the
/// hole.
/// </summary>
public sealed class TelemetryClientConstructorAccessibilityTests
{
    [Fact]
    public void TheHttpClientAcceptingConstructorHasNoPublicOverload()
    {
        var constructors = typeof(KaironTelemetryClient)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        Assert.Empty(constructors);
    }

    [Fact]
    public void TheOnlyConstructorIsInternalNotPrivateNorProtected()
    {
        // Internal (not private) is what lets Kairon.SDK's own DI wiring and KaironClient - and,
        // via InternalsVisibleTo, this test assembly - still construct it directly.
        var constructor = Assert.Single(
            typeof(KaironTelemetryClient).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance));

        Assert.True(constructor.IsAssembly);
        Assert.False(constructor.IsPublic);
        Assert.False(constructor.IsPrivate);
        Assert.False(constructor.IsFamily);
    }
}
