using System.Data;
using System.Security.Cryptography;
using System.Text;
using AIDIP.Backend.DTOs;
using AIDIP.Backend.Infrastructure;
using AIDIP.Backend.Models;
using Microsoft.EntityFrameworkCore;

namespace AIDIP.Backend.Services;

public sealed record CreatedPairing(Guid PairingId, string Code, DateTime ExpiresAt, Guid ApplicationId, string SdkType);
public sealed record PairedSdk(Guid InstallationRecordId, string InstallationId, string Credential,
    Guid ProjectId, Guid ApplicationId, string Application, string Service, string Environment, string Endpoint);

public interface ISdkPairingService
{
    Task<CreatedPairing?> CreateAsync(Guid applicationId, string sdkType, CancellationToken cancellationToken);
    Task<PairedSdk?> RedeemAsync(string code, string sdkType, string version, CancellationToken cancellationToken);
    Task<bool> RevokePairingAsync(Guid pairingId, CancellationToken cancellationToken);
    Task<bool> AuthorizeTelemetryAsync(NormalizedTelemetryBatchDto batch, string? key, CancellationToken cancellationToken);
    Task<bool> AuthorizeLegacyAsync(Guid projectId, string service, string? key, CancellationToken cancellationToken);
    Task<bool> RevokeAsync(Guid installationId, CancellationToken cancellationToken);
}

public sealed class SdkPairingService : ISdkPairingService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _time;
    private readonly IConfiguration _configuration;

    public SdkPairingService(AppDbContext db, TimeProvider time, IConfiguration configuration)
    {
        _db = db;
        _time = time;
        _configuration = configuration;
    }

    public async Task<CreatedPairing?> CreateAsync(Guid applicationId, string sdkType,
        CancellationToken cancellationToken)
    {
        var app = await _db.MonitoredApplications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == applicationId,
            cancellationToken);
        if (app is null) return null;
        var normalized = NormalizeSdk(sdkType);
        if (normalized is null) return null;
        var code = "pair_" + Token(24);
        var now = _time.GetUtcNow().UtcDateTime;
        var session = new SdkPairingSession { Id = Guid.NewGuid(), ProjectId = app.ProjectId,
            ApplicationId = app.Id, SdkType = normalized, CodeHash = Hash(code), CreatedAt = now,
            ExpiresAt = now.AddMinutes(10) };
        _db.SdkPairingSessions.Add(session);
        await _db.SaveChangesAsync(cancellationToken);
        return new CreatedPairing(session.Id, code, session.ExpiresAt, app.Id, normalized);
    }

    public async Task<PairedSdk?> RedeemAsync(string code, string sdkType, string version,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || !code.StartsWith("pair_", StringComparison.Ordinal) || code.Length < 20)
            return null;
        var now = _time.GetUtcNow().UtcDateTime;
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var hash = Hash(code);
        var session = await _db.SdkPairingSessions.SingleOrDefaultAsync(x => x.CodeHash == hash, cancellationToken);
        if (session is null || session.RedeemedAt is not null || session.RevokedAt is not null || session.ExpiresAt <= now)
            return null;
        if (!string.Equals(session.SdkType, NormalizeSdk(sdkType), StringComparison.Ordinal)) return null;
        var app = await _db.MonitoredApplications.SingleAsync(x => x.Id == session.ApplicationId, cancellationToken);
        var installationId = Guid.NewGuid().ToString("N");
        var credential = "ksi_" + Token(32);
        var source = new TelemetrySourceRegistration { Id = Guid.NewGuid(), ProjectId = session.ProjectId,
            ApplicationId = app.Id, SourceType = session.SdkType + "-sdk", InstallationId = installationId,
            Version = Bound(version, 50), RegisteredAt = now, LastSeenAt = now, IsActive = true };
        var installation = new SdkInstallation { Id = Guid.NewGuid(), ProjectId = session.ProjectId,
            ApplicationId = app.Id, SourceId = source.Id, SdkType = session.SdkType, Version = Bound(version, 50),
            InstallationId = installationId, KeyPrefix = credential[..12], KeyHash = Hash(credential), CreatedAt = now };
        session.RedeemedAt = now;
        _db.TelemetrySources.Add(source);
        _db.SdkInstallations.Add(installation);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var endpoint = _configuration["Product:BackendUrl"] ?? "http://127.0.0.1:8000";
        return new PairedSdk(installation.Id, installationId, credential, session.ProjectId, app.Id,
            app.Name, app.Service, "Development", endpoint.TrimEnd('/'));
    }

    public async Task<bool> RevokePairingAsync(Guid pairingId, CancellationToken cancellationToken)
    {
        var session = await _db.SdkPairingSessions.SingleOrDefaultAsync(x => x.Id == pairingId,
            cancellationToken);
        if (session is null || session.RedeemedAt is not null || session.RevokedAt is not null) return false;
        session.RevokedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> AuthorizeTelemetryAsync(NormalizedTelemetryBatchDto batch, string? key,
        CancellationToken cancellationToken)
    {
        if (batch.Events.Count == 0 || string.IsNullOrWhiteSpace(key) || key.Length < 16) return false;
        var installations = batch.Events.Select(x => x.InstallationId).Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal).ToList();
        var projects = batch.Events.Select(x => x.ProjectId).Distinct().ToList();
        if (installations.Count != 1 || projects.Count != 1) return false;
        var prefix = key[..Math.Min(12, key.Length)];
        var installation = await _db.SdkInstallations.SingleOrDefaultAsync(x => x.ProjectId == projects[0]
            && x.InstallationId == installations[0] && x.KeyPrefix == prefix && x.RevokedAt == null, cancellationToken);
        if (installation is null || !FixedEquals(installation.KeyHash, Hash(key))) return false;
        var application = await _db.MonitoredApplications.AsNoTracking().SingleAsync(x => x.Id == installation.ApplicationId,
            cancellationToken);
        if (batch.Events.Any(x => !string.Equals(x.Service, application.Service, StringComparison.OrdinalIgnoreCase)))
            return false;
        installation.LastSeenAt = _time.GetUtcNow().UtcDateTime;
        var source = await _db.TelemetrySources.FindAsync([installation.SourceId], cancellationToken);
        if (source is not null) { source.LastSeenAt = installation.LastSeenAt.Value; source.IsActive = true; }
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> AuthorizeLegacyAsync(Guid projectId, string service, string? key,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length < 16) return false;
        var prefix = key[..Math.Min(12, key.Length)];
        var installation = await _db.SdkInstallations.SingleOrDefaultAsync(x => x.ProjectId == projectId
            && x.KeyPrefix == prefix && x.RevokedAt == null, cancellationToken);
        if (installation is null || !FixedEquals(installation.KeyHash, Hash(key))) return false;
        var expected = await _db.MonitoredApplications.AsNoTracking().Where(x => x.Id == installation.ApplicationId)
            .Select(x => x.Service).SingleAsync(cancellationToken);
        if (!string.Equals(expected, service, StringComparison.OrdinalIgnoreCase)) return false;
        installation.LastSeenAt = _time.GetUtcNow().UtcDateTime;
        var source = await _db.TelemetrySources.FindAsync([installation.SourceId], cancellationToken);
        if (source is not null) { source.LastSeenAt = installation.LastSeenAt.Value; source.IsActive = true; }
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RevokeAsync(Guid installationId, CancellationToken cancellationToken)
    {
        var installation = await _db.SdkInstallations.SingleOrDefaultAsync(x => x.Id == installationId,
            cancellationToken);
        if (installation is null) return false;
        installation.RevokedAt = _time.GetUtcNow().UtcDateTime;
        var source = await _db.TelemetrySources.FindAsync([installation.SourceId], cancellationToken);
        if (source is not null) source.IsActive = false;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string? NormalizeSdk(string value) => value.Trim().ToLowerInvariant() switch
    { "dotnet" or ".net" => "dotnet", "python" => "python", _ => null };
    private static string Token(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool FixedEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(
        Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    private static string Bound(string? value, int length) => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim()[..Math.Min(value.Trim().Length, length)];
}
