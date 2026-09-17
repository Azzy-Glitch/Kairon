using System.Net.Http.Json;
using System.Reflection;

namespace Kairon.SDK;

/// <summary>Result of redeeming a pairing code for an API key (docs/DESKTOP_SHELL.md). On
/// success, <see cref="ApiKey"/> and <see cref="ProjectId"/> are ready to drop straight into
/// <see cref="KaironOptions.ApiKey"/>/<see cref="KaironOptions.ProjectId"/>. <see cref="PairingId"/>
/// lets the caller confirm receipt/persistence afterward via <see cref="KaironPairingClient.ConfirmAsync"/>.</summary>
public sealed record KaironPairingResult(bool Success, string? Error, Guid ProjectId = default,
    string? ApiKey = null, string? Endpoint = null, Guid PairingId = default);

/// <summary>
/// Exchanges a temporary, single-use pairing code (minted by an operator in the Kairon UI) for a
/// persistent project API key. Adapted from Azzy's productization branch's AIDIPPairingClient,
/// renamed and matched to this backend's simpler Project-scoped pairing response
/// (backend/Services/SdkPairingService.cs) - no separate application/installation identity here.
/// </summary>
public static class KaironPairingClient
{
    /// <summary>
    /// <paramref name="handler"/> is exposed only so tests can stub the HTTP response without a
    /// live server; production callers always omit it and get a real connection. Supplying one
    /// does not reopen the redirect-safety hole it might otherwise look like it does: whatever is
    /// passed has its own redirect-following forcibly disabled first (see
    /// <see cref="KaironEndpointSecurity.DisableAutoRedirect"/>) - a pairing code is a secret
    /// worth protecting in transit regardless of who constructed the transport.
    /// </summary>
    public static async Task<KaironPairingResult> PairAsync(string backendEndpoint, string pairingCode,
        CancellationToken cancellationToken = default, HttpMessageHandler? handler = null)
    {
        // Checked before this SDK ever sends a pairing code anywhere - a caller-supplied endpoint
        // is exactly as untrusted as one returned in a response (see the check on paired.Endpoint
        // below), and the pairing code itself is a secret worth protecting in transit.
        if (!KaironEndpointSecurity.IsAllowed(backendEndpoint))
            return new KaironPairingResult(false, "Refusing to pair over an insecure endpoint: plain HTTP is only allowed to localhost/127.0.0.0/8/::1. Use HTTPS for a non-local KAIRON backend.");

        if (handler is not null) KaironEndpointSecurity.DisableAutoRedirect(handler);

        try
        {
            using var client = new HttpClient(handler ?? KaironEndpointSecurity.CreateNonRedirectingHandler(), disposeHandler: true)
            {
                BaseAddress = new Uri(backendEndpoint.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(10)
            };
            var version = typeof(KaironPairingClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "1.1.0";

            var response = await client.PostAsJsonAsync("api/v1/sdk/pair",
                new { code = pairingCode, sdkType = "dotnet", version }, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return new KaironPairingResult(false, "Pairing code was rejected.");

            var paired = await response.Content.ReadFromJsonAsync<PairingWireResponse>(cancellationToken).ConfigureAwait(false);
            // The returned endpoint is exactly as untrusted as any other network input - a
            // compromised or misconfigured backend must never be able to redirect this SDK onto a
            // remote plaintext address merely by including one in a pairing response.
            return paired is null || paired.ProjectId == Guid.Empty || string.IsNullOrWhiteSpace(paired.ApiKey) ||
                paired.PairingId == Guid.Empty || !KaironEndpointSecurity.IsAllowed(paired.Endpoint)
                ? new KaironPairingResult(false, "Pairing response was invalid.")
                : new KaironPairingResult(true, null, paired.ProjectId, paired.ApiKey, paired.Endpoint, paired.PairingId);
        }
        catch
        {
            return new KaironPairingResult(false, "Kairon is unavailable.");
        }
    }

    /// <summary>Called right after the caller durably persists the newly redeemed credential -
    /// proves to the backend that this SDK actually received and is using it, the only
    /// trustworthy signal distinct from the backend merely having issued it (a redeem response can
    /// be lost in transit, or the process can crash before the credential is saved to disk).
    /// Never throws - the credential is already valid and usable regardless of whether this call
    /// succeeds - so this returns a bool rather than propagating an exception. A failure here only
    /// means an operator-triggered re-pair completion (which revokes the credential being
    /// replaced) keeps waiting until a retry - either one of THIS call's own bounded attempts, or a
    /// later run recovering a still-pending confirmation from the stored credential file (see
    /// KaironClient.ResolveAsync) - lets this catch up.
    ///
    /// Retries a small, fixed number of times with a short linear backoff before giving up for
    /// THIS call - bounded, never an unbounded loop - so a single transient network blip does not
    /// need a full process restart to recover from.</summary>
    public static async Task<bool> ConfirmAsync(string backendEndpoint, Guid pairingId, string apiKey,
        CancellationToken cancellationToken = default, HttpMessageHandler? handler = null, int attempts = 2)
    {
        // Never validated only once at the top of the flow - a stored/recovered endpoint reaches
        // this call independently of PairAsync (e.g. retrying a pending confirmation on a later
        // run), so it is re-checked here too rather than trusted because it was checked somewhere
        // earlier.
        if (!KaironEndpointSecurity.IsAllowed(backendEndpoint)) return false;

        // Same reasoning as PairAsync: the freshly issued API key travels in this call's POST
        // body, and a caller-supplied handler must not be able to let a redirect replay it.
        if (handler is not null) KaironEndpointSecurity.DisableAutoRedirect(handler);

        using var client = new HttpClient(handler ?? KaironEndpointSecurity.CreateNonRedirectingHandler(), disposeHandler: true)
        {
            BaseAddress = new Uri(backendEndpoint.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(10)
        };
        for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            try
            {
                var response = await client.PostAsJsonAsync($"api/v1/sdk/pair/{pairingId}/confirm", new { apiKey }, cancellationToken)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return true;
            }
            catch
            {
                // Falls through to the backoff/retry below - never propagated.
            }
            if (attempt + 1 < attempts)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return false; }
            }
        }
        return false;
    }

    private sealed class PairingWireResponse
    {
        public string ApiKey { get; set; } = string.Empty;
        public Guid ProjectId { get; set; }
        public string Endpoint { get; set; } = string.Empty;
        public Guid PairingId { get; set; }
    }
}
