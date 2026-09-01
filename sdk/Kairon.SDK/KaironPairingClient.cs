using System.Net.Http.Json;
using System.Reflection;

namespace Kairon.SDK;

/// <summary>Result of redeeming a pairing code for an API key (docs/DESKTOP_SHELL.md). On
/// success, <see cref="ApiKey"/> and <see cref="ProjectId"/> are ready to drop straight into
/// <see cref="KaironOptions.ApiKey"/>/<see cref="KaironOptions.ProjectId"/>.</summary>
public sealed record KaironPairingResult(bool Success, string? Error, Guid ProjectId = default,
    string? ApiKey = null, string? Endpoint = null);

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
    /// live server; production callers always omit it and get a real connection.
    /// </summary>
    public static async Task<KaironPairingResult> PairAsync(string backendEndpoint, string pairingCode,
        CancellationToken cancellationToken = default, HttpMessageHandler? handler = null)
    {
        try
        {
            using var client = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true)
            {
                BaseAddress = new Uri(backendEndpoint.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(10)
            };
            var version = typeof(KaironPairingClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "1.0.1";

            var response = await client.PostAsJsonAsync("api/v1/sdk/pair",
                new { code = pairingCode, sdkType = "dotnet", version }, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return new KaironPairingResult(false, "Pairing code was rejected.");

            var paired = await response.Content.ReadFromJsonAsync<PairingWireResponse>(cancellationToken);
            return paired is null
                ? new KaironPairingResult(false, "Pairing response was invalid.")
                : new KaironPairingResult(true, null, paired.ProjectId, paired.ApiKey, paired.Endpoint);
        }
        catch
        {
            return new KaironPairingResult(false, "Kairon is unavailable.");
        }
    }

    private sealed class PairingWireResponse
    {
        public string ApiKey { get; set; } = string.Empty;
        public Guid ProjectId { get; set; }
        public string Endpoint { get; set; } = string.Empty;
    }
}
