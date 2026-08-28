using System.Net.Http.Json;
using System.Reflection;

namespace AIDIP.SDK;

public sealed record AIDIPPairingResult(bool Success, string? Error, Guid ProjectId = default,
    Guid ApplicationId = default, string? Application = null, string? Service = null,
    string? Environment = null, string? InstallationId = null, string? Credential = null,
    string? Endpoint = null);

/// <summary>Exchanges a temporary, single-use pairing code for scoped SDK configuration.</summary>
public static class AIDIPPairingClient
{
    public static async Task<AIDIPPairingResult> PairAsync(string backendEndpoint, string pairingCode,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(backendEndpoint.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(10) };
            var version = typeof(AIDIPPairingClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "1.0.0";
            var response = await client.PostAsJsonAsync("api/v1/sdk/pair",
                new { code = pairingCode, sdkType = "dotnet", version }, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new AIDIPPairingResult(false, "Pairing code was rejected.");
            var paired = await response.Content.ReadFromJsonAsync<PairingWireResponse>(cancellationToken);
            return paired is null ? new AIDIPPairingResult(false, "Pairing response was invalid.")
                : new AIDIPPairingResult(true, null, paired.ProjectId, paired.ApplicationId, paired.Application,
                    paired.Service, paired.Environment, paired.InstallationId, paired.Credential, paired.Endpoint);
        }
        catch
        {
            return new AIDIPPairingResult(false, "KAIRON is unavailable.");
        }
    }

    private sealed class PairingWireResponse
    {
        public Guid ProjectId { get; set; }
        public Guid ApplicationId { get; set; }
        public string Application { get; set; } = string.Empty;
        public string Service { get; set; } = string.Empty;
        public string Environment { get; set; } = string.Empty;
        public string InstallationId { get; set; } = string.Empty;
        public string Credential { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
    }
}
