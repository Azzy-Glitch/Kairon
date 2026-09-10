using System.Data;
using System.Security.Cryptography;
using System.Text;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace Kairon.Backend.Services;

public sealed record CreatedPairing(Guid PairingId, string Code, DateTime ExpiresAt, string SdkType);

/// <summary>PairingId lets the SDK call ConfirmAsync once it has durably persisted ApiKey - the
/// only trustworthy proof that this response was actually received, distinct from the backend
/// merely having issued the credential.</summary>
public sealed record PairedSdk(string ApiKey, Guid ProjectId, string Endpoint, Guid PairingId);

/// <summary>Safe, secret-free status projection of an SdkPairingSession - never includes the
/// pairing code, its hash, or the issued credential's id/secret. Status is computed, not stored,
/// matching the entity's own nullable-datetime convention (no persisted state enum). ConfirmedAt
/// is exposed separately from Status: Status stays "Redeemed" the instant the backend issues the
/// credential (unchanged, existing meaning - a plain first-time pairing has nothing further to
/// wait for), while ConfirmedAt only becomes non-null once the SDK itself has proven it received
/// and is using that exact credential - the signal a re-pair completion must wait for before it
/// is safe to revoke the credential being replaced.</summary>
public sealed record PairingStatus(
    Guid PairingId, Guid ProjectId, string SdkType, DateTime CreatedAt, DateTime ExpiresAt,
    DateTime? RedeemedAt, DateTime? ConfirmedAt, DateTime? RevokedAt, string Status);

public enum CompleteRepairOutcome { Success, SessionNotFound, NotConfirmed, OldCredentialNotFound, OldCredentialWrongProject }

/// <summary>NewCredentialId/ProjectId are populated on Success (and are needed by the controller
/// for a secret-free audit record); RebindCount is the number of enabled RemediationTargets whose
/// TelemetryCredentialId was moved from the old credential onto the new one.</summary>
public sealed record CompleteRepairResult(
    CompleteRepairOutcome Outcome, int RebindCount = 0, Guid? NewCredentialId = null, Guid? ProjectId = null);

/// <summary>
/// Adapted from Azzy's productization branch: a temporary, hashed pairing code (10-minute expiry)
/// redeemed exactly once for a real ProjectApiCredential (minted via IProjectCredentialService,
/// not duplicated here). The original version anchored pairing to a MonitoredApplication - a
/// deeper Project/Application/Environment hierarchy this codebase doesn't model; this version
/// anchors directly to Project, which is the only concept this codebase actually needs for
/// pairing to mean something.
///
/// Re-pairing safety (audit-driven): a browser polling GetStatusAsync can observe that a pairing
/// session was Redeemed, but "Redeemed" only proves the backend's HTTP response carried a fresh
/// credential - never that the SDK actually received that response and durably persisted it
/// (the network can fail after issuance; the SDK's own atomic file write can fail). Treating
/// Redeemed as sufficient to revoke the credential being replaced could therefore strand an
/// application with no usable credential at all. ConfirmAsync closes this gap: the SDK calls it,
/// unattended, immediately after persisting the new credential, authenticating with the exact key
/// it just saved - proof of possession, not merely a claim. Only once ConfirmedAt is set does
/// CompleteRepairAsync (operator-driven) become willing to revoke the old credential, and it does
/// so atomically together with rebinding any RemediationTarget that referenced it, so the system
/// is never left believing a target is healthy while its credential is actually revoked.
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

    /// <summary>Called by the SDK itself, unattended, right after it durably persists the newly
    /// redeemed credential - authenticates by proving possession of the EXACT api key this session
    /// issued (never merely "any valid credential for the project", which the old one still is
    /// until revoked). Idempotent: confirming an already-confirmed session succeeds trivially.</summary>
    Task<bool> ConfirmAsync(Guid pairingId, string? apiKey, CancellationToken cancellationToken);

    /// <summary>Operator-driven re-pair completion. Refuses unless the session is Confirmed (see
    /// class remarks). Atomically rebinds every ENABLED RemediationTarget currently bound to
    /// oldCredentialId - scoped to this session's own project, so it can never touch another
    /// project's targets - onto the newly issued credential, then revokes oldCredentialId. Safe to
    /// call more than once: re-pairing "itself" (oldCredentialId already equal to the issued
    /// credential) and completing an already-completed repair (oldCredentialId already revoked)
    /// both succeed as no-ops rather than erroring or double-revoking.</summary>
    Task<CompleteRepairResult> CompleteRepairAsync(Guid pairingId, Guid oldCredentialId, CancellationToken cancellationToken);
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
        // Matches ProjectCredentialService.AuthorizeAsync's own IsActive requirement: an inactive
        // project can never authenticate telemetry, so pairing must not issue a usable-looking
        // credential for one - that credential would be valid-shaped but permanently unusable.
        if (!await _db.Projects.AsNoTracking().AnyAsync(x => x.Id == projectId && x.IsActive, cancellationToken)) return null;
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

        // Re-checked at redemption, not just at creation: a project can be deactivated in the
        // window between an operator minting a code and an SDK redeeming it.
        if (!await _db.Projects.AnyAsync(p => p.Id == session.ProjectId && p.IsActive, cancellationToken)) return null;

        var created = await _credentials.CreateAsync(session.ProjectId, $"{normalizedSdk}-sdk", cancellationToken);
        if (created is null) return null;

        session.RedeemedAt = now;
        session.IssuedCredentialId = created.Id;
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var endpoint = _configuration["Product:BackendUrl"] ?? "http://127.0.0.1:8000";
        return new PairedSdk(created.ApiKey, session.ProjectId, endpoint.TrimEnd('/'), session.Id);
    }

    public async Task<bool> RevokePairingAsync(Guid pairingId, CancellationToken cancellationToken)
    {
        var session = await _db.SdkPairingSessions.SingleOrDefaultAsync(x => x.Id == pairingId, cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;
        // An already-expired session cannot be cancelled - doing so would overwrite the more
        // informative "Expired" status with "Cancelled" (RevokedAt is checked first in
        // GetStatusAsync), losing exactly when/why the session actually became unusable.
        if (session is null || session.RedeemedAt is not null || session.RevokedAt is not null || session.ExpiresAt <= now)
            return false;
        session.RevokedAt = now;
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
            session.ExpiresAt, session.RedeemedAt, session.ConfirmedAt, session.RevokedAt, status);
    }

    public async Task<bool> ConfirmAsync(Guid pairingId, string? apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return false;

        var session = await _db.SdkPairingSessions.SingleOrDefaultAsync(x => x.Id == pairingId, cancellationToken);
        if (session is null || session.RedeemedAt is null || session.IssuedCredentialId is null) return false;
        if (session.ConfirmedAt is not null) return true; // idempotent replay - already confirmed

        // Deliberately NOT ProjectCredentialService.AuthorizeAsync: that call is gated by
        // PlatformSecurity:RequireTelemetryKey and would trivially "pass" with any/no key at all
        // when that flag is off. Confirmation must always cryptographically prove possession of
        // this session's specific issued credential, regardless of the telemetry-auth toggle.
        var credential = await _db.ProjectApiCredentials.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == session.IssuedCredentialId.Value && c.ProjectId == session.ProjectId, cancellationToken);
        if (credential is null || credential.RevokedAt is not null) return false;

        var suppliedHash = Hash(apiKey);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(suppliedHash), Encoding.ASCII.GetBytes(credential.KeyHash)))
            return false;

        session.ConfirmedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<CompleteRepairResult> CompleteRepairAsync(Guid pairingId, Guid oldCredentialId, CancellationToken cancellationToken)
    {
        var session = await _db.SdkPairingSessions.SingleOrDefaultAsync(x => x.Id == pairingId, cancellationToken);
        if (session is null || session.IssuedCredentialId is null)
            return new CompleteRepairResult(CompleteRepairOutcome.SessionNotFound);
        if (session.ConfirmedAt is null)
            return new CompleteRepairResult(CompleteRepairOutcome.NotConfirmed);

        var oldCredential = await _db.ProjectApiCredentials.SingleOrDefaultAsync(c => c.Id == oldCredentialId, cancellationToken);
        if (oldCredential is null)
            return new CompleteRepairResult(CompleteRepairOutcome.OldCredentialNotFound);
        if (oldCredential.ProjectId != session.ProjectId)
            return new CompleteRepairResult(CompleteRepairOutcome.OldCredentialWrongProject);

        var newCredentialId = session.IssuedCredentialId.Value;
        if (oldCredentialId == newCredentialId || oldCredential.RevokedAt is not null)
        {
            // Re-pairing "itself", or a retry after a previous completion already ran - a safe,
            // idempotent no-op rather than an error or a second revoke/rebind pass.
            return new CompleteRepairResult(CompleteRepairOutcome.Success, 0, newCredentialId, session.ProjectId);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        // Only ENABLED targets referencing the exact old credential, in this exact project - never
        // another project's rows (TelemetryCredentialId alone is not project-scoped by itself, so
        // the ProjectId filter here is load-bearing, not redundant) and never a disabled target an
        // operator deliberately parked on a different credential.
        var affected = await _db.RemediationTargets
            .Where(t => t.ProjectId == session.ProjectId && t.TelemetryCredentialId == oldCredentialId && t.Enabled)
            .ToListAsync(cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var target in affected)
        {
            target.TelemetryCredentialId = newCredentialId;
            target.UpdatedAt = now;
        }

        oldCredential.RevokedAt = now;

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CompleteRepairResult(CompleteRepairOutcome.Success, affected.Count, newCredentialId, session.ProjectId);
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
