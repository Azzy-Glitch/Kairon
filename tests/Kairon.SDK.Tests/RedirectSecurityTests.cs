using System.Net;
using System.Net.Sockets;
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
        private readonly HttpListener _listener = new();
        private readonly int _port;
        private volatile bool _running;

        public int OriginHitCount;
        public int TargetHitCount;
        public bool TargetSawApiKeyHeader;

        public LoopbackRedirectServer()
        {
            _port = GetFreeLoopbackPort();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        }

        public string OriginUrl => $"http://127.0.0.1:{_port}/origin";
        private string TargetUrl => $"http://127.0.0.1:{_port}/target";

        public void Start()
        {
            _listener.Start();
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

        private static int GetFreeLoopbackPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch { /* best-effort teardown */ }
            try { _listener.Close(); } catch { /* best-effort teardown */ }
        }
    }
}
