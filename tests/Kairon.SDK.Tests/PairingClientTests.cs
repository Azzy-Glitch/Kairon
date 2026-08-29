using System.Net;
using Kairon.SDK;
using Xunit;

namespace Kairon.SDK.Tests;

/// <summary>
/// KaironPairingClient.PairAsync (docs/DESKTOP_SHELL.md): a temporary pairing code exchanged for
/// a persistent API key. Same "never throw into host code" invariant as the telemetry client -
/// every failure path here still ends in a returned result.
/// </summary>
public class PairingClientTests
{
    [Fact]
    public async Task SuccessfulPairingReturnsTheIssuedCredential()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"apiKey":"krn_abc123","projectId":"11111111-1111-1111-1111-111111111111","endpoint":"http://127.0.0.1:8000"}""");

        var result = await KaironPairingClient.PairAsync("http://localhost:8000", "pair_validcode", handler: handler);

        Assert.True(result.Success);
        Assert.Equal("krn_abc123", result.ApiKey);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), result.ProjectId);
        Assert.Equal("http://127.0.0.1:8000", result.Endpoint);
    }

    [Fact]
    public async Task RejectedPairingCodeReturnsFailureNotAnException()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest, """{"error":"Pairing code is invalid, expired, revoked, or already used."}""");

        var result = await KaironPairingClient.PairAsync("http://localhost:8000", "pair_expired", handler: handler);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Null(result.ApiKey);
    }

    [Fact]
    public async Task MalformedResponseBodyReturnsFailureNotAnException()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "not json");

        var result = await KaironPairingClient.PairAsync("http://localhost:8000", "pair_x", handler: handler);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task UnreachableBackendReturnsFailureNotAnException()
    {
        var handler = new ThrowingHandler();

        var result = await KaironPairingClient.PairAsync("http://localhost:8000", "pair_x", handler: handler);

        Assert.False(result.Success);
        Assert.Equal("Kairon is unavailable.", result.Error);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused.");
    }
}
