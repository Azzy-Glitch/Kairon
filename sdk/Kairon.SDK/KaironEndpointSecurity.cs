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

    /// <summary>Same policy as <see cref="IsAllowed(string?)"/>, applied to an already-parsed
    /// <see cref="Uri"/> - the form an <c>HttpClient.BaseAddress</c> is stored as. This is what lets
    /// the actual transport boundary (<see cref="KaironTelemetryClient"/>) validate the EFFECTIVE
    /// destination it is about to send to, regardless of how that HttpClient was constructed or
    /// configured - a caller that builds its own HttpClient and passes it directly, bypassing
    /// AddKairon/KaironClient entirely, must not be able to point this SDK at an insecure endpoint
    /// merely by never going through either of those.</summary>
    public static bool IsAllowed(Uri? endpoint) => endpoint is not null && IsAllowed(endpoint.ToString());

    /// <summary>A message handler with automatic redirect-following disabled. Every HttpClient this
    /// SDK constructs internally (pairing, confirmation, telemetry) uses one of these rather than a
    /// default handler: a compromised or malicious KAIRON backend must never be able to redirect a
    /// pairing code, project API key header, or telemetry payload onto a different - possibly
    /// attacker-controlled or plaintext - origin by returning a 3xx response. A redirect is instead
    /// surfaced as an ordinary non-success status, exactly like any other rejected request.</summary>
    public static HttpClientHandler CreateNonRedirectingHandler() => new() { AllowAutoRedirect = false };

    /// <summary>
    /// Forcibly turns off redirect-following on an already-constructed handler, walking a
    /// <see cref="DelegatingHandler"/> chain down to whatever concrete handler actually executes
    /// the request. Used wherever this SDK's public surface accepts an OPTIONAL caller-supplied
    /// <see cref="HttpMessageHandler"/> for test stubbing (<see cref="KaironPairingClient.PairAsync"/>,
    /// <see cref="KaironPairingClient.ConfirmAsync"/>): the default (no handler supplied) already
    /// gets a fresh <see cref="CreateNonRedirectingHandler"/>, but a caller who supplies their own -
    /// a real <see cref="HttpClientHandler"/>/<see cref="SocketsHttpHandler"/> at its default
    /// settings, however many <see cref="DelegatingHandler"/>s deep - must not be able to silently
    /// reintroduce redirect-following merely by not having thought to set
    /// <c>AllowAutoRedirect = false</c> themselves. A pairing code and a confirmation's freshly
    /// issued API key both travel in the POST body, which a 307/308 redirect replays verbatim to
    /// wherever the response's Location points.
    ///
    /// This mutates the supplied instance's <c>AllowAutoRedirect</c> property in place - it never
    /// replaces or wraps the handler - so every other customization the caller made (a proxy,
    /// client certificates, cookies, a DelegatingHandler's own logic) is left untouched. A handler
    /// type this SDK does not recognize (neither <see cref="HttpClientHandler"/> nor
    /// <see cref="SocketsHttpHandler"/> at the end of the chain - for example, a fully custom
    /// <see cref="HttpMessageHandler"/> subclass implementing its own transport) cannot be mutated
    /// this way; test doubles that just return a canned <see cref="HttpResponseMessage"/> directly
    /// (this SDK's own test suite's pattern) fall in that category too, but are not a redirect risk
    /// in the first place - they never call a base implementation that follows redirects at all.
    /// </summary>
    public static void DisableAutoRedirect(HttpMessageHandler? handler)
    {
        var current = handler;
        while (current is DelegatingHandler delegating && delegating.InnerHandler is not null)
            current = delegating.InnerHandler;

        switch (current)
        {
            case HttpClientHandler httpClientHandler:
                httpClientHandler.AllowAutoRedirect = false;
                break;
            case SocketsHttpHandler socketsHttpHandler:
                socketsHttpHandler.AllowAutoRedirect = false;
                break;
        }
    }
}
