namespace Kairon.Backend.Models.Platform;

/// <summary>
/// User-supplied AI provider configuration (frontend AI Configuration panel), so a user never has
/// to edit ai-service/.env or backend/appsettings.json by hand. Single-tenant: this is one desktop
/// product, not a multi-project setting, so there is always at most one row - the service that
/// owns this table always upserts the same well-known Id rather than tracking many.
///
/// <see cref="EncryptedApiKey"/> is protected with ASP.NET Core's Data Protection API (Windows
/// DPAPI-backed key ring under the product's own data directory) - reversible, unlike the SDK
/// credential store's one-way hash, because the backend has to resend this key to the AI service
/// on every provider call. It is never returned by any endpoint and never logged.
/// </summary>
public sealed class AiProviderConfig
{
    public static readonly Guid SingletonId = Guid.Parse("00000000-0000-0000-0000-0000000a1c06");

    public Guid Id { get; set; } = SingletonId;
    public string Provider { get; set; } = string.Empty;

    /// <summary>Empty means "Auto / Recommended" - the AI service resolves its own verified
    /// current default for the provider rather than the frontend guessing one.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Empty means the provider's default public endpoint. Set only when the provider is
    /// fronted by a dedicated/regional URL instead - for example an Alibaba Model Studio Token
    /// Plan workspace, which rejects calls made to the shared DashScope endpoint even with a
    /// valid key.</summary>
    public string Endpoint { get; set; } = string.Empty;

    public string EncryptedApiKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
