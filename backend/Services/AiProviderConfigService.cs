using System.Security.Cryptography;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Platform;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Kairon.Backend.Services;

/// <summary>Safe to return from any endpoint - the API key is never included, only whether one is
/// set (frontend AI Configuration panel section 12: never return a stored key to the browser).</summary>
public sealed record AiProviderConfigSummary(string Provider, string Model, bool HasApiKey, DateTime? UpdatedAt)
{
    public static readonly AiProviderConfigSummary NotConfigured = new(string.Empty, string.Empty, false, null);
}

/// <summary>
/// Persists the user-supplied AI provider/model/key from the frontend AI Configuration panel, so
/// the user never edits ai-service/.env or backend/appsettings.json by hand (frontend PRD: AI
/// provider configuration). The API key is encrypted at rest via ASP.NET Core's Data Protection
/// API - reversible, because the backend has to resend it to the AI service on every provider
/// call, unlike the SDK credential store's one-way hash.
/// </summary>
public interface IAiProviderConfigService
{
    Task<AiProviderConfigSummary> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Upserts the singleton row. A null/blank <paramref name="apiKey"/> keeps whatever
    /// key is already stored (so changing just the model never requires resending a known-good
    /// key); a null/blank <paramref name="model"/> means "Auto / Recommended".</summary>
    Task<AiProviderConfigSummary> SaveAsync(
        string provider, string? apiKey, string? model, CancellationToken cancellationToken = default);

    /// <summary>True only when a row exists with a real (decryptable, non-blank) key - the signal
    /// AiMicroservice uses to decide whether a saved UI configuration should override
    /// appsettings.json's static AiService:MockMode (frontend PRD section 13: UI config wins over
    /// the existing static default; untouched deployments with no saved row are unaffected).</summary>
    Task<bool> HasValidConfigurationAsync(CancellationToken cancellationToken = default);

    /// <summary>The decrypted key, for internal use only (forwarding to the AI service's
    /// /configure endpoint) - never returned by any controller action.</summary>
    Task<string?> GetDecryptedApiKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>The full current selection (provider/model), for internal use when only the model
    /// is changing and the existing provider/key still apply.</summary>
    Task<(string Provider, string Model)?> GetSelectionAsync(CancellationToken cancellationToken = default);
}

public sealed class AiProviderConfigService : IAiProviderConfigService
{
    private const string ProtectorPurpose = "Kairon.AiProviderConfig.ApiKey.v1";

    private readonly AppDbContext _db;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;

    public AiProviderConfigService(AppDbContext db, IDataProtectionProvider dataProtection, TimeProvider time)
    {
        _db = db;
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
        _time = time;
    }

    public async Task<AiProviderConfigSummary> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await FindAsync(cancellationToken);
        return ToSummary(entity);
    }

    public async Task<AiProviderConfigSummary> SaveAsync(
        string provider, string? apiKey, string? model, CancellationToken cancellationToken = default)
    {
        var normalizedProvider = (provider ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalizedProvider))
            throw new ArgumentException("A provider is required.", nameof(provider));

        var entity = await FindAsync(cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;

        if (entity is null)
        {
            entity = new AiProviderConfig { Id = AiProviderConfig.SingletonId, CreatedAt = now };
            _db.AiProviderConfigs.Add(entity);
        }

        entity.Provider = normalizedProvider;
        entity.Model = (model ?? string.Empty).Trim();
        entity.UpdatedAt = now;

        var trimmedKey = (apiKey ?? string.Empty).Trim();
        if (trimmedKey.Length > 0)
            entity.EncryptedApiKey = _protector.Protect(trimmedKey);

        await _db.SaveChangesAsync(cancellationToken);
        return ToSummary(entity);
    }

    public async Task<bool> HasValidConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var entity = await FindAsync(cancellationToken);
        return entity is not null && entity.EncryptedApiKey.Length > 0;
    }

    public async Task<string?> GetDecryptedApiKeyAsync(CancellationToken cancellationToken = default)
    {
        var entity = await FindAsync(cancellationToken);
        if (entity is null || entity.EncryptedApiKey.Length == 0) return null;

        try
        {
            return _protector.Unprotect(entity.EncryptedApiKey);
        }
        catch (CryptographicException)
        {
            // The key ring changed (e.g. a machine migration) - treat as "no key configured"
            // rather than throwing out of a request path that only asked "is AI ready".
            return null;
        }
    }

    public async Task<(string Provider, string Model)?> GetSelectionAsync(CancellationToken cancellationToken = default)
    {
        var entity = await FindAsync(cancellationToken);
        return entity is null ? null : (entity.Provider, entity.Model);
    }

    private Task<AiProviderConfig?> FindAsync(CancellationToken cancellationToken) =>
        _db.AiProviderConfigs.SingleOrDefaultAsync(x => x.Id == AiProviderConfig.SingletonId, cancellationToken);

    private static AiProviderConfigSummary ToSummary(AiProviderConfig? entity) => entity is null
        ? AiProviderConfigSummary.NotConfigured
        : new AiProviderConfigSummary(entity.Provider, entity.Model, entity.EncryptedApiKey.Length > 0, entity.UpdatedAt);
}
