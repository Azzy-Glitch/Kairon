using System.Data;
using System.Security.Cryptography;
using System.Text;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace Kairon.Backend.Services;

public sealed record CreatedPairing(Guid PairingId, string Code, DateTime ExpiresAt, string SdkType);

public sealed record PairedSdk(string ApiKey, Guid ProjectId, string Endpoint);

/// <summary>Safe, secret-free status projection of an SdkPairingSession - never includes the
/// pairing code or its hash. Status is computed, not stored, matching the entity's own
/// three-nullable-datetime convention (no persisted state enum).</summary>
public sealed record PairingStatus(
    Guid PairingId, Guid ProjectId, string SdkType, DateTime CreatedAt, DateTime ExpiresAt,
    DateTime? RedeemedAt, DateTime? RevokedAt, string Status);

/// <summary>
/// Adapted from Azzy's productization branch: a temporary, hashed pairing code (10-minute expiry)
/// redeemed exactly once for a real ProjectApiCredential (minted via IProjectCredentialService,
/// not duplicated here). The original version anchored pairing to a MonitoredApplication - a
/// deeper Project/Application/Environment hierarchy this codebase doesn't model; this version
/// anchors directly to Project, which is the only concept this codebase actually needs for
/// pairing to mean something.
/// </summary>
public interface ISdkPairingService
{
    Task<CreatedPairing?> CreateAsync(Guid projectId, string sdkType, CancellationToken cancellationToken);
    Task<PairedSdk?> RedeemAsync(string code, string sdkType, string version, CancellationToken cancellationToken);
    Task<bool> RevokePairingAsync(Guid pairingId, CancellationToken cancellationToken);

    /// <summary>Lets an operator UI poll whether a pairing session (re-pairing included - a
    /// re-pair is just another pairing session for the same, already-connected, project) has been
    /// redeemed yet, without ever exposing the code/hash. Returns null if the session doesn't exist.</summary>
    Task<PairingStatus?> GetStatusAsync(Guid pairingId, CancellationToken cancellationToken);
}

public sealed class SdkPairingService : ISdkPairingService
{
    private readonly AppDbContext _db;
    private readonly IProjectCredentialService _credentials;
    private readonly TimeProvider _time;
    private readonly IConfiguration _configuration;

    public SdkPairingService(AppDbContext db, IProjectCredentialService credentials, TimeProvider time,
        IConfiguration configuration)
    {
        _db = db;
        _credentials = credentials;
        _time = time;
        _configuration = configuration;
    }

    public async Task<CreatedPairing?> CreateAsync(Guid projectId, string sdkType, CancellationToken cancellationToken)
    {
        if (!await _db.Projects.AsNoTracking().AnyAsync(x => x.Id == projectId, cancellationToken)) return null;
        var normalized = NormalizeSdk(sdkType);
        if (normalized is null) return null;

        var code = "pair_" + Token(24);
        var now = _time.GetUtcNow().UtcDateTime;
        var session = new SdkPairingSession
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            SdkType = normalized,
            CodeHash = Hash(code),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(10)
        };
        _db.SdkPairingSessions.Add(session);
        await _db.SaveChangesAsync(cancellationToken);
        return new CreatedPairing(session.Id, code, session.ExpiresAt, normalized);
    }

    public async Task<PairedSdk?> RedeemAsync(string code, string sdkType, string version,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || !code.StartsWith("pair_", StringComparison.Ordinal) || code.Length < 20)
            return null;

        var normalizedSdk = NormalizeSdk(sdkType);
        if (normalizedSdk is null) return null;

        var now = _time.GetUtcNow().UtcDateTime;
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var hash = Hash(code);
        var session = await _db.SdkPairingSessions.SingleOrDefaultAsync(x => x.CodeHash == hash, cancellationToken);
        if (session is null || session.RedeemedAt is not null || session.RevokedAt is not null || session.ExpiresAt <= now)
            return null;
        if (!string.Equals(session.SdkType, normalizedSdk, StringComparison.Ordinal)) return null;

        var created = await _credentials.CreateAsync(session.ProjectId, $"{normalizedSdk}-sdk", cancellationToken);
        if (created is null) return null;

        session.RedeemedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var endpoint = _configuration["Product:BackendUrl"] ?? "http://127.0.0.1:8000";
        return new PairedSdk(created.ApiKey, session.ProjectId, endpoint.TrimEnd('/'));
    }

    public async Task<bool> RevokePairingAsync(Guid pairingId, CancellationToken cancellationToken)
    {
        var session = await _db.SdkPairingSessions.SingleOrDefaultAsync(x => x.Id == pairingId, cancellationToken);
        if (session is null || session.RedeemedAt is not null || session.RevokedAt is not null) return false;
        session.RevokedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<PairingStatus?> GetStatusAsync(Guid pairingId, CancellationToken cancellationToken)
    {
        var session = await _db.SdkPairingSessions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == pairingId, cancellationToken);
        if (session is null) return null;

        var now = _time.GetUtcNow().UtcDateTime;
        var status = session.RedeemedAt is not null ? "Redeemed"
            : session.RevokedAt is not null ? "Cancelled"
            : session.ExpiresAt <= now ? "Expired"
            : "Pending";

        return new PairingStatus(session.Id, session.ProjectId, session.SdkType, session.CreatedAt,
            session.ExpiresAt, session.RedeemedAt, session.RevokedAt, status);
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
}
