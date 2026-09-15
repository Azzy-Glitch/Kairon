using System.Net;

namespace Kairon.UserAgent;

/// <summary>
/// KAIRON.UserAgent's copy of sdk/Kairon.SDK/KaironEndpointSecurity.cs (also duplicated in
/// agent/Kairon.Agent/AgentEndpointSecurity.cs) - same policy, kept in sync deliberately rather
/// than referenced, because this is a plain Worker Service and taking a ProjectReference on
/// Kairon.SDK would pull in its ASP.NET Core FrameworkReference for no reason (mirrors the
/// existing DeriveMachineId duplication between this project and Kairon.Agent).
///
/// Plain, unencrypted HTTP is trusted ONLY for a genuine loopback destination (this same machine) -
/// anywhere else, the scoped UserAgent key or session process telemetry could cross a real
/// network in clear text. HTTPS is accepted for any destination, local or remote. Embedded
/// userinfo (https://user:pass@host) is never accepted either.
/// </summary>
public static class AgentEndpointSecurity
{
    public static bool IsAllowed(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return false;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;

        if (uri.Scheme == Uri.UriSchemeHttps) return true;

        var host = uri.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    public static bool IsAllowed(Uri? endpoint) => endpoint is not null && IsAllowed(endpoint.ToString());

    public static void EnsureAllowed(string? endpoint)
    {
        if (!IsAllowed(endpoint))
            throw new InvalidOperationException(
                "Kairon UserAgent endpoint rejected: plain HTTP is only allowed to localhost/127.0.0.0/8/::1. " +
                "Use HTTPS for any non-local KAIRON backend.");
    }

    /// <summary>Disables automatic redirect-following - a compromised/malicious backend must never
    /// be able to redirect a heartbeat request (carrying the scoped UserAgent key) onto a
    /// different origin and have this service transparently follow it there.</summary>
    public static HttpClientHandler CreateNonRedirectingHandler() => new() { AllowAutoRedirect = false };
}
