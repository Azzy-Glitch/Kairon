using System.Security.Cryptography;
using System.Text;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Services.Audit;
using Microsoft.EntityFrameworkCore;

namespace Kairon.Backend.Services;

/// <summary>Automatic signals an SDK collects for one paired app. Operators set them in the desktop;
/// the SDK reads them with its own key, so the application never needs code or configuration.</summary>
public sealed record SdkAutoSignalSettings(bool AutoQueueDepth, bool AutoRetries, int RetryWindowSeconds);

public enum SdkSettingsUpdateOutcome { Updated, NotFound, Invalid }

public interface ISdkSettingsService
{
    /// <summary>The settings for the active credential matching this SDK key, or null.</summary>
    Task<SdkAutoSignalSettings?> GetForKeyAsync(Guid projectId, string? suppliedKey, CancellationToken cancellationToken);
    Task<SdkAutoSignalSettings?> GetAsync(Guid projectId, Guid credentialId, CancellationToken cancellationToken);
    Task<SdkSettingsUpdateOutcome> UpdateAsync(Guid projectId, Guid credentialId, SdkAutoSignalSettings settings,
        string actor, CancellationToken cancellationToken);
}

public sealed class SdkSettingsService(AppDbContext db, IPlatformAuditService audit) : ISdkSettingsService
{
    public async Task<SdkAutoSignalSettings?> GetForKeyAsync(Guid projectId, string? suppliedKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(suppliedKey) || suppliedKey.Length < 16) return null;
        var prefix = suppliedKey[..Math.Min(12, suppliedKey.Length)];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(suppliedKey)));
        var candidates = await db.ProjectApiCredentials.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.KeyPrefix == prefix && x.RevokedAt == null
                        && db.Projects.Any(p => p.Id == projectId && p.IsActive))
            .ToListAsync(cancellationToken);
        var match = candidates.FirstOrDefault(c => CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(c.KeyHash), Encoding.ASCII.GetBytes(hash)));
        return match is null ? null : From(match);
    }

    public async Task<SdkAutoSignalSettings?> GetAsync(Guid projectId, Guid credentialId, CancellationToken cancellationToken)
    {
        var credential = await db.ProjectApiCredentials.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == credentialId && x.ProjectId == projectId, cancellationToken);
        return credential is null ? null : From(credential);
    }

    public async Task<SdkSettingsUpdateOutcome> UpdateAsync(Guid projectId, Guid credentialId, SdkAutoSignalSettings settings,
        string actor, CancellationToken cancellationToken)
    {
        if (settings.RetryWindowSeconds is < SdkAutoSignalDefaults.MinRetryWindowSeconds or > SdkAutoSignalDefaults.MaxRetryWindowSeconds)
            return SdkSettingsUpdateOutcome.Invalid;
        var credential = await db.ProjectApiCredentials
            .FirstOrDefaultAsync(x => x.Id == credentialId && x.ProjectId == projectId && x.RevokedAt == null, cancellationToken);
        if (credential is null) return SdkSettingsUpdateOutcome.NotFound;

        credential.AutoQueueDepth = settings.AutoQueueDepth;
        credential.AutoRetries = settings.AutoRetries;
        credential.RetryWindowSeconds = settings.RetryWindowSeconds;
        credential.RowVersion = Guid.NewGuid();
        audit.Record("credential.sdk-settings.updated", actor, "credential", credentialId.ToString(), projectId, data: settings);
        await db.SaveChangesAsync(cancellationToken);
        return SdkSettingsUpdateOutcome.Updated;
    }

    private static SdkAutoSignalSettings From(ProjectApiCredential c) => new(c.AutoQueueDepth, c.AutoRetries, c.RetryWindowSeconds);
}
