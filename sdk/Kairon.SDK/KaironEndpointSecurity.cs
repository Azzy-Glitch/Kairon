using System.Net;

namespace Kairon.SDK;

/// <summary>
/// Enforces the SDK's transport-security contract for every endpoint it is ever asked to use:
/// plain, unencrypted HTTP is trusted ONLY for a genuine loopback destination (this same machine) -
/// anywhere else, a pairing code, a project API key, or telemetry could cross a real network in
/// clear text. HTTPS is accepted for any destination, local or remote. Embedded userinfo
/// (https://user:pass@host) is never accepted either - that is a credential leak vector of its own
/// (logged URLs, proxies, browser history) and this SDK never needs it.
///
/// Applied everywhere an endpoint can enter the SDK - explicit configuration (KaironClient,
/// AddKairon), a stored connection, and critically a pairing response's own returned endpoint - so
/// a compromised or malicious backend/pairing response can never redirect this SDK onto a remote
/// plaintext address merely by returning one.
/// </summary>
public static class KaironEndpointSecurity
{
    /// <summary>True only for an absolute http(s) URL with no embedded credentials, where plain
    /// HTTP is additionally restricted to a loopback host.</summary>
    public static bool IsAllowed(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return false;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        // Embedded userinfo (user:pass@host) is never allowed, on either scheme.
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;

        if (uri.Scheme == Uri.UriSchemeHttps) return true;

        // Plain HTTP is trusted only for this exact machine. Uri.Host already strips the []
        // bracket notation from an IPv6 literal (e.g. "[::1]" -> "::1"), so IPAddress.TryParse
        // handles both IPv4 and IPv6 loopback forms uniformly, including the whole 127.0.0.0/8
        // range via IPAddress.IsLoopback - not merely a literal "127.0.0.1" string match.
        var host = uri.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    /// <summary>Throws a clear, secret-free InvalidOperationException when <paramref
    /// name="endpoint"/> fails <see cref="IsAllowed"/> - the fail-closed counterpart used at every
    /// point that is about to actually commit to using an endpoint (never merely log a warning and
    /// continue).</summary>
    public static void EnsureAllowed(string? endpoint)
    {
        if (!IsAllowed(endpoint))
            throw new InvalidOperationException(
                "Kairon endpoint rejected: plain HTTP is only allowed to localhost/127.0.0.0/8/::1. " +
                "Use HTTPS for any non-local KAIRON backend.");
    }
}
