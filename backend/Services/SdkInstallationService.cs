using System.Security.Cryptography;
using System.Text;
using Kairon.Backend.DTOs;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace Kairon.Backend.Services;

public sealed record CreatedSdkInstallation(Guid Id, string InstallationId, string Credential, Guid ProjectId,
    Guid ApplicationId, DateTime CreatedAt);

public sealed record SdkInstallationStatusDto(Guid Id, Guid ProjectId, Guid ApplicationId, string SdkType,
    string Version, string InstallationId, DateTime CreatedAt, DateTime? LastSeenAt, bool Connected, DateTime? RevokedAt);

/// <summary>
/// Installation-scoped SDK credentials ("ksi_..."), a stronger, optional tier above the existing
/// project-level pairing (SdkPairingService/ProjectApiCredential, "krn_...") - compromising one
/// installation's key does not expose every other installation on the same project. Adapted from
/// Azzy's productization branch, but kept as a genuinely separate, additive service rather than
/// replacing SdkPairingService: main's version rewrote pairing to redeem directly into an
/// SdkInstallation, which would have changed the /api/v1/sdk/pair response shape both existing
/// SDKs already depend on (Kairon.SDK's KaironPairingClient, sdk-python's pair()). Issuance here
/// instead mirrors ProjectCredentialService's existing operator-mints-once pattern - no pairing-
/// code redemption ceremony needed for this tier.
/// </summary>
public interface ISdkInstallationService
{
    Task<CreatedSdkInstallation?> IssueAsync(Guid applicationId, string sdkType, string version,
        CancellationToken cancellationToken);
    Task<bool> RevokeAsync(Guid installationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SdkInstallationStatusDto>> ListAsync(Guid? applicationId, CancellationToken cancellationToken);

    /// <summary>Authorizes a normalized telemetry batch against a single installation credential -
    /// the batch must reference exactly one project and one installation id (a real caller sends
    /// one installation's own events per batch), matching the service the installation was issued
    /// for.</summary>
    Task<bool> AuthorizeBatchAsync(NormalizedTelemetryBatchDto batch, string? suppliedKey,
        CancellationToken cancellationToken);
}

public sealed class SdkInstallationService : ISdkInstallationService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _time;

    public SdkInstallationService(AppDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public async Task<CreatedSdkInstallation?> IssueAsync(Guid applicationId, string sdkType, string version,
        CancellationToken cancellationToken)
    {
        var app = await _db.MonitoredApplications.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == applicationId, cancellationToken);
        if (app is null) return null;
        var normalized = NormalizeSdk(sdkType);
        if (normalized is null) return null;

        var now = _time.GetUtcNow().UtcDateTime;
        var installationId = Guid.NewGuid().ToString("N");
        var credential = "ksi_" + Token(32);
        var boundedVersion = Bound(version, 50);

        var source = new TelemetrySourceRegistration
        {
            Id = Guid.NewGuid(), ProjectId = app.ProjectId, ApplicationId = app.Id,
            SourceType = normalized + "-sdk", InstallationId = installationId, Version = boundedVersion,
            RegisteredAt = now, LastSeenAt = now, IsActive = true
        };
        var installation = new SdkInstallation
        {
            Id = Guid.NewGuid(), ProjectId = app.ProjectId, ApplicationId = app.Id, SourceId = source.Id,
            SdkType = normalized, Version = boundedVersion, InstallationId = installationId,
            KeyPrefix = credential[..12], KeyHash = Hash(credential), CreatedAt = now
        };
        _db.TelemetrySources.Add(source);
        _db.SdkInstallations.Add(installation);
        await _db.SaveChangesAsync(cancellationToken);

        return new CreatedSdkInstallation(installation.Id, installationId, credential, app.ProjectId, app.Id, now);
    }

    public async Task<bool> RevokeAsync(Guid installationId, CancellationToken cancellationToken)
    {
        var installation = await _db.SdkInstallations.SingleOrDefaultAsync(x => x.Id == installationId, cancellationToken);
        if (installation is null || installation.RevokedAt is not null) return false;
        installation.RevokedAt = _time.GetUtcNow().UtcDateTime;
        var source = await _db.TelemetrySources.FindAsync([installation.SourceId], cancellationToken);
        if (source is not null) source.IsActive = false;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<SdkInstallationStatusDto>> ListAsync(Guid? applicationId,
        CancellationToken cancellationToken)
    {
        var query = _db.SdkInstallations.AsNoTracking().AsQueryable();
        if (applicationId.HasValue) query = query.Where(x => x.ApplicationId == applicationId.Value);
        return await query.OrderByDescending(x => x.CreatedAt)
            .Select(x => new SdkInstallationStatusDto(x.Id, x.ProjectId, x.ApplicationId, x.SdkType, x.Version,
                x.InstallationId, x.CreatedAt, x.LastSeenAt, x.RevokedAt == null, x.RevokedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> AuthorizeBatchAsync(NormalizedTelemetryBatchDto batch, string? suppliedKey,
        CancellationToken cancellationToken)
    {
        if (batch.Events.Count == 0 || string.IsNullOrWhiteSpace(suppliedKey) || suppliedKey.Length < 16) return false;

        var installationIds = batch.Events.Select(x => x.InstallationId).Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal).ToList();
        var projectIds = batch.Events.Select(x => x.ProjectId).Distinct().ToList();
        if (installationIds.Count != 1 || projectIds.Count != 1) return false;

        var prefix = suppliedKey[..Math.Min(12, suppliedKey.Length)];
        var installation = await _db.SdkInstallations.SingleOrDefaultAsync(x => x.ProjectId == projectIds[0]
            && x.InstallationId == installationIds[0] && x.KeyPrefix == prefix && x.RevokedAt == null,
            cancellationToken);
        if (installation is null || !FixedEquals(installation.KeyHash, Hash(suppliedKey))) return false;

        var expectedService = await _db.MonitoredApplications.AsNoTracking()
            .Where(x => x.Id == installation.ApplicationId).Select(x => x.Service).SingleAsync(cancellationToken);
        if (batch.Events.Any(x => !string.Equals(
                string.IsNullOrWhiteSpace(x.Service) ? x.Application : x.Service,
                expectedService, StringComparison.OrdinalIgnoreCase)))
            return false;

        var now = _time.GetUtcNow().UtcDateTime;
        installation.LastSeenAt = now;
        var source = await _db.TelemetrySources.FindAsync([installation.SourceId], cancellationToken);
        if (source is not null) { source.LastSeenAt = now; source.IsActive = true; }
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string? NormalizeSdk(string value) => value.Trim().ToLowerInvariant() switch
    {
        "dotnet" or ".net" => "dotnet",
        "python" => "python",
        _ => null
    };

    private static string Token(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(
        Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    private static string Bound(string value, int length) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim()[..Math.Min(value.Trim().Length, length)];
}
