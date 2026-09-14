using Kairon.SDK;
using Xunit;

namespace Kairon.SDK.Tests;

/// <summary>
/// Transport-security contract (Kairon.SDK.KaironEndpointSecurity): plain HTTP is trusted only for
/// a genuine loopback destination; HTTPS is trusted for any destination, local or remote. Mirrors
/// sdk-python/tests/test_endpoint_security.py - both SDKs must enforce the identical policy.
/// </summary>
public class EndpointSecurityTests
{
    [Theory]
    [InlineData("http://localhost")]
    [InlineData("http://localhost:8000")]
    [InlineData("http://LOCALHOST")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.0.0.1:8000")]
    [InlineData("http://127.1.2.3")]
    [InlineData("http://127.255.255.255")]
    [InlineData("http://[::1]")]
    [InlineData("http://[::1]:8000")]
    public void LoopbackHttpIsAllowed(string endpoint) =>
        Assert.True(KaironEndpointSecurity.IsAllowed(endpoint));

    [Theory]
    [InlineData("http://192.168.1.20")]
    [InlineData("http://10.0.0.20")]
    [InlineData("http://172.16.1.20")]
    [InlineData("http://8.8.8.8")]
    [InlineData("http://example.com")]
    [InlineData("http://example.com:8000")]
    // Not proper URI parsing pitfalls: a naive prefix/substring check could be fooled by these.
    [InlineData("http://localhost.evil.com")]
    [InlineData("http://evil.com/localhost")]
    [InlineData("http://127.0.0.1.evil.com")]
    public void RemotePlaintextHttpIsRejected(string endpoint) =>
        Assert.False(KaironEndpointSecurity.IsAllowed(endpoint));

    [Theory]
    [InlineData("https://localhost")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://[::1]")]
    [InlineData("https://192.168.1.20")]
    [InlineData("https://10.0.0.20")]
    [InlineData("https://172.16.1.20")]
    [InlineData("https://8.8.8.8")]
    [InlineData("https://example.com")]
    [InlineData("https://api.example.com:8443")]
    public void HttpsIsAllowedForAnyHostLocalOrRemote(string endpoint) =>
        Assert.True(KaironEndpointSecurity.IsAllowed(endpoint));

    [Theory]
    [InlineData("https://user:password@example.com")]
    [InlineData("http://user:password@127.0.0.1")]
    [InlineData("https://user@example.com")]
    public void EmbeddedUserinfoIsRejectedRegardlessOfSchemeOrHost(string endpoint) =>
        Assert.False(KaironEndpointSecurity.IsAllowed(endpoint));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("ftp://127.0.0.1")]
    [InlineData("javascript://127.0.0.1")]
    public void MalformedOrNonHttpSchemesAreRejected(string? endpoint) =>
        Assert.False(KaironEndpointSecurity.IsAllowed(endpoint));

    [Fact]
    public void EnsureAllowedThrowsForAnInsecureEndpoint()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => KaironEndpointSecurity.EnsureAllowed("http://8.8.8.8"));
        Assert.DoesNotContain("8.8.8.8", ex.Message); // the message must not need to echo the rejected value to be clear
    }

    [Fact]
    public void EnsureAllowedDoesNotThrowForAnAllowedEndpoint()
    {
        KaironEndpointSecurity.EnsureAllowed("https://example.com");
        KaironEndpointSecurity.EnsureAllowed("http://127.0.0.1:8000");
    }
}
