using System.Data;
using System.Security.Cryptography;
using System.Text;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Services.Audit;
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
/// and CompletedAt are exposed separately from Status, not just folded into it, because a caller
/// recovering an in-flight re-pair (e.g. the operator UI after a refresh/lost response - see
/// SdkPage.jsx's recovery effect) needs to tell "the backend actually finished this" apart from
/// every other state without string-matching Status: CompletedAt is the ONE authoritative signal
/// that CompleteRepairAsync's own mutation (old-credential revocation + target rebinds) already
/// happened for THIS session, and is never set any other way. Status itself now distinguishes
/// Completed and Confirmed as their own values (rather than folding both into "Redeemed", the
/// original, coarser behavior) so a plain status string is self-describing on its own for any
/// other caller of this endpoint.</summary>
public sealed record PairingStatus(
    Guid PairingId, Guid ProjectId, string SdkType, DateTime CreatedAt, DateTime ExpiresAt,
    DateTime? RedeemedAt, DateTime? ConfirmedAt, DateTime? CompletedAt, DateTime? RevokedAt, string Status);

public enum CompleteRepairOutcome
{
    Success,
    SessionNotFound,
    NotConfirmed,
    OldCredentialNotFound,
    OldCredentialWrongProject,

    /// <summary>The caller-supplied old-credential id does not match the exact credential this
    /// session was bound, at creation, to replace (SdkPairingSession.ReplacesCredentialId) - or
    /// this session was never bound to replace anything (a first-time pairing session has no
    /// binding at all, and can never be completed as a re-pair).</summary>
    OldCredentialMismatch,

    /// <summary>The old credential is already revoked, but not because THIS session's own
    /// completion revoked it (that case is reported as Success, idempotently, via CompletedAt) -
    /// something else revoked it first, so the expected rebind cannot be verified to have actually
    /// happened under this session.</summary>
    OldCredentialAlreadyRevoked,

    /// <summary>The newly issued credential - confirmed as received/usable at confirmation time -
    /// has since been independently revoked (e.g. a second, unrelated re-pair; a direct operator
    /// revocation). Completing now would rebind targets onto a credential that no longer works and
    /// revoke the one they were still relying on - refused instead.</summary>
    NewCredentialRevoked,

    /// <summary>A genuinely concurrent change to either credential this completion depends on,
    /// detected via ProjectApiCredential.RowVersion's optimistic-concurrency check rather than
    /// merely the earlier in-memory RevokedAt reads (which a genuine race can outrun) - covers two
    /// distinct races: (1) another CONFIRMED session, also bound to replace this exact old
    /// credential, committed its own replacement between this call's own read of the old credential
    /// and this call's commit; (2) the new credential this call already validated as active was
    /// itself independently revoked (a second, unrelated re-pair; a direct operator revocation)
    /// between that validation and this call's commit. Either way, nothing from this call was
    /// persisted - the old credential, remediation targets, and this session's own CompletedAt are
    /// exactly as whatever concurrent writer won left them.</summary>
    ConcurrentReplacementConflict
}

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
///
/// Every mutating method records its own audit event (IPlatformAuditService.Record only adds to
/// this same AppDbContext's change tracker; it does not save independently) immediately before its
/// own SaveChangesAsync/CommitAsync, so the audit row and the business-state mutation it describes
/// either both land or neither does - a separate, later SaveChangesAsync call (e.g. from a
/// controller, after this method's own transaction already committed) could otherwise lose the
/// audit record for an operation that had already taken effect.
/// </summary>
public interface ISdkPairingService
{
    Task<CreatedPairing?> CreateAsync(Guid projectId, string sdkType, CancellationToken cancellationToken,
        Guid? replacesCredentialId = null, string actor = "local-operator");

    Task<PairedSdk?> RedeemAsync(string code, string sdkType, string version, CancellationToken cancellationToken);

    Task<bool> RevokePairingAsync(Guid pairingId, CancellationToken cancellationToken, string actor = "local-operator");

    /// <summary>Lets an operator UI poll whether a pairing session (re-pairing included - a
    /// re-pair is just another pairing session for the same, already-connected, project) has been
    /// redeemed yet, without ever exposing the code/hash. Returns null if the session doesn't exist.</summary>
    Task<PairingStatus?> GetStatusAsync(Guid pairingId, CancellationToken cancellationToken);

    /// <summary>Called by the SDK itself, unattended, right after it durably persists the newly
    /// redeemed credential - authenticates by proving possession of the EXACT api key this session
    /// issued (never merely "any valid credential for the project", which the old one still is
    /// until revoked). Revalidates that exact credential on EVERY call, including a replay of an
    /// already-confirmed session: a repeated confirmation request must never be able to claim
    /// success without presenting the correct key again, even if some earlier call already
    /// succeeded.</summary>
    Task<bool> ConfirmAsync(Guid pairingId, string? apiKey, CancellationToken cancellationToken);

    /// <summary>Operator-driven re-pair completion. Refuses unless the session is Confirmed (see
    /// class remarks) and the supplied oldCredentialId matches exactly what this session was bound,
    /// at creation, to replace. Re-validates the NEW credential is still active immediately before
    /// acting on it, then atomically rebinds every ENABLED RemediationTarget currently bound to
    /// oldCredentialId - scoped to this session's own project, so it can never touch another
    /// project's targets - onto the newly issued credential, then revokes oldCredentialId. Safe to
    /// call more than once: re-pairing "itself" (oldCredentialId already equal to the issued
    /// credential) and completing an already-completed repair both succeed as no-ops rather than
    /// erroring or double-revoking.</summary>
    Task<CompleteRepairResult> CompleteRepairAsync(Guid pairingId, Guid oldCredentialId, CancellationToken cancellationToken,
        string actor = "local-operator");
}

public sealed class SdkPairingService : ISdkPairingService
{
    private readonly AppDbContext _db;
    private readonly IProjectCredentialService _credentials;
    private readonly TimeProvider _time;
    private readonly IConfiguration _configuration;
    private readonly IPlatformAuditService _audit;

    public SdkPairingService(AppDbContext db, IProjectCredentialService credentials, TimeProvider time,
        IConfiguration configuration, IPlatformAuditService audit)
    {
        _db = db;
        _credentials = credentials;
        _time = time;
        _configuration = configuration;
        _audit = audit;
    }

    public async Task<CreatedPairing?> CreateAsync(Guid projectId, string sdkType, CancellationToken cancellationToken,
        Guid? replacesCredentialId = null, string actor = "local-operator")
    {
        // Matches ProjectCredentialService.AuthorizeAsync's own IsActive requirement: an inactive
        // project can never authenticate telemetry, so pairing must not issue a usable-looking
        // credential for one - that credential would be valid-shaped but permanently unusable.
        if (!await _db.Projects.AsNoTracking().AnyAsync(x => x.Id == projectId && x.IsActive, cancellationToken)) return null;
        var normalized = NormalizeSdk(sdkType);
        if (normalized is null) return null;

        if (replacesCredentialId is { } oldId)
        {
            // A re-pair session must be bound, at creation, to the exact active credential it
            // intends to replace - CompleteRepairAsync later refuses any other credential (see its
            // own remarks). Rejecting an invalid binding here rather than silently ignoring it
            // means a session can never exist "for" a credential it could never actually complete
            // against.
            var replaces = await _db.ProjectApiCredentials.AsNoTracking()
                .SingleOrDefaultAsync(c => c.Id == oldId, cancellationToken);
            if (replaces is null || replaces.ProjectId != projectId || replaces.RevokedAt is not null) return null;
        }

        var code = "pair_" + Token(24);
        var now = _time.GetUtcNow().UtcDateTime;
        var session = new SdkPairingSession
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            SdkType = normalized,
            CodeHash = Hash(code),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(10),
            ReplacesCredentialId = replacesCredentialId
        };
        _db.SdkPairingSessions.Add(session);
        _audit.Record("sdk.pairing-created", actor, "project", projectId.ToString(), projectId,
            data: new { PairingId = session.Id, SdkType = normalized, session.ExpiresAt, ReplacesCredentialId = replacesCredentialId });
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
        _audit.Record("sdk.paired", "sdk:" + sdkType, "project", session.ProjectId.ToString(), session.ProjectId,
            data: new { SdkType = sdkType, Version = version });
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var endpoint = _configuration["Product:BackendUrl"] ?? "http://127.0.0.1:8000";
        return new PairedSdk(created.ApiKey, session.ProjectId, endpoint.TrimEnd('/'), session.Id);
    }

    public async Task<bool> RevokePairingAsync(Guid pairingId, CancellationToken cancellationToken, string actor = "local-operator")
    {
        var session = await _db.SdkPairingSessions.SingleOrDefaultAsync(x => x.Id == pairingId, cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;
        // An already-expired session cannot be cancelled - doing so would overwrite the more
        // informative "Expired" status with "Cancelled" (RevokedAt is checked first in
        // GetStatusAsync), losing exactly when/why the session actually became unusable.
        if (session is null || session.RedeemedAt is not null || session.RevokedAt is not null || session.ExpiresAt <= now)
            return false;
        session.RevokedAt = now;
        _audit.Record("sdk.pairing-revoked", actor, "sdk-pairing", pairingId.ToString(), session.ProjectId);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<PairingStatus?> GetStatusAsync(Guid pairingId, CancellationToken cancellationToken)
    {
        var session = await _db.SdkPairingSessions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == pairingId, cancellationToken);
        if (session is null) return null;

        var now = _time.GetUtcNow().UtcDateTime;
        // Ordered most-specific-first: a completed/confirmed session is always also redeemed, so
        // those checks must come before the plain "Redeemed" fallback below or they could never be
        // reached.
        var status = session.CompletedAt is not null ? "Completed"
            : session.ConfirmedAt is not null ? "Confirmed"
            : session.RedeemedAt is not null ? "Redeemed"
            : session.RevokedAt is not null ? "Cancelled"
            : session.ExpiresAt <= now ? "Expired"
            : "Pending";

        return new PairingStatus(session.Id, session.ProjectId, session.SdkType, session.CreatedAt,
            session.ExpiresAt, session.RedeemedAt, session.ConfirmedAt, session.CompletedAt, session.RevokedAt, status);
    }

    public async Task<bool> ConfirmAsync(Guid pairingId, string? apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return false;

        var session = await _db.SdkPairingSessions.SingleOrDefaultAsync(x => x.Id == pairingId, cancellationToken);
        if (session is null || session.RedeemedAt is null || session.IssuedCredentialId is null) return false;

        // Deliberately NOT ProjectCredentialService.AuthorizeAsync: that call is gated by
        // PlatformSecurity:RequireTelemetryKey and would trivially "pass" with any/no key at all
        // when that flag is off. Confirmation must always cryptographically prove possession of
        // this session's specific issued credential, regardless of the telemetry-auth toggle.
        //
        // This check runs BEFORE the already-confirmed idempotency shortcut below, on every call,
        // including a replay - a repeated confirmation request must never be able to claim success
        // (even for an already-confirmed session) without presenting the correct key again. It also
        // means a session whose credential was revoked after it was first confirmed can no longer
        // be "re-confirmed" by replaying the old key.
        var credential = await _db.ProjectApiCredentials.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == session.IssuedCredentialId.Value && c.ProjectId == session.ProjectId, cancellationToken);
        if (credential is null || credential.RevokedAt is not null) return false;

        var suppliedHash = Hash(apiKey);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(suppliedHash), Encoding.ASCII.GetBytes(credential.KeyHash)))
            return false;

        if (session.ConfirmedAt is not null) return true; // idempotent replay - just reproven above

        session.ConfirmedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<CompleteRepairResult> CompleteRepairAsync(Guid pairingId, Guid oldCredentialId, CancellationToken cancellationToken,
        string actor = "local-operator")
    {
        var session = await _db.SdkPairingSessions.SingleOrDefaultAsync(x => x.Id == pairingId, cancellationToken);
        if (session is null || session.IssuedCredentialId is null)
            return new CompleteRepairResult(CompleteRepairOutcome.SessionNotFound);
        if (session.ConfirmedAt is null)
            return new CompleteRepairResult(CompleteRepairOutcome.NotConfirmed);

        // The session must have been explicitly bound, at creation, to the exact credential it is
        // completing against - never any other active same-project credential the caller happens
        // to supply (see SdkPairingSession.ReplacesCredentialId's remarks). A first-time pairing
        // session (no binding at all) can never be completed as a re-pair. Checked BEFORE the
        // idempotency shortcut below: a replay against a different (wrong) credential must never
        // succeed just because this session already completed against its real one.
        if (session.ReplacesCredentialId is null || session.ReplacesCredentialId != oldCredentialId)
            return new CompleteRepairResult(CompleteRepairOutcome.OldCredentialMismatch);

        // Idempotent replay: THIS session's own completion already ran (and the caller just
        // proved, above, that they're asking about the same credential relationship). Deliberately
        // distinct from "the old credential happens to be revoked" (checked further down) - that
        // alone doesn't prove this session is what did it.
        if (session.CompletedAt is not null)
            return new CompleteRepairResult(CompleteRepairOutcome.Success, 0, session.IssuedCredentialId, session.ProjectId);

        var oldCredential = await _db.ProjectApiCredentials.SingleOrDefaultAsync(c => c.Id == oldCredentialId, cancellationToken);
        if (oldCredential is null)
            return new CompleteRepairResult(CompleteRepairOutcome.OldCredentialNotFound);
        if (oldCredential.ProjectId != session.ProjectId)
            return new CompleteRepairResult(CompleteRepairOutcome.OldCredentialWrongProject);

        var newCredentialId = session.IssuedCredentialId.Value;
        if (oldCredentialId == newCredentialId)
        {
            // Re-pairing "itself" - a safe no-op; still recorded as completed so a replay is
            // consistent and idempotent.
            session.CompletedAt = _time.GetUtcNow().UtcDateTime;
            _audit.Record("sdk.repair-completed", actor, "sdk-pairing", pairingId.ToString(), session.ProjectId,
                data: new { OldCredentialId = oldCredentialId, NewCredentialId = newCredentialId, RebindCount = 0 });
            await _db.SaveChangesAsync(cancellationToken);
            return new CompleteRepairResult(CompleteRepairOutcome.Success, 0, newCredentialId, session.ProjectId);
        }

        if (oldCredential.RevokedAt is not null)
        {
            // Not this session's own prior completion (that would have short-circuited via
            // CompletedAt above) - something else revoked it first. Reporting success here would
            // misrepresent whether the expected rebind actually happened under this session.
            return new CompleteRepairResult(CompleteRepairOutcome.OldCredentialAlreadyRevoked);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        // Re-load and revalidate the NEW credential itself, immediately before acting on it.
        // Confirmed only proves it was valid at confirmation time - it could have been
        // independently revoked any time between then and now (a second, unrelated re-pair; a
        // direct operator revocation). Rebinding targets onto an already-revoked credential, or
        // revoking the old one in exchange for a new one that no longer works, would silently
        // strand telemetry.
        var newCredential = await _db.ProjectApiCredentials.SingleOrDefaultAsync(c => c.Id == newCredentialId, cancellationToken);
        if (newCredential is null || newCredential.ProjectId != session.ProjectId || newCredential.RevokedAt is not null)
            return new CompleteRepairResult(CompleteRepairOutcome.NewCredentialRevoked);

        // This check alone only proves newCredential was still valid at the instant of this read -
        // it is never otherwise written by this method, so without this line EF has nothing to
        // include newCredential in this SaveChangesAsync's change set at all, and therefore no
        // concurrency check ever runs against it: a concurrent revoke of THIS credential (another
        // unrelated re-pair; a direct operator revoke) landing after this read but before the
        // commit below would go completely undetected, and the transaction would still complete
        // "successfully" onto a credential that is actually already dead. Rotating RowVersion here
        // - even though nothing else about newCredential changes - forces EF to issue a real
        // UPDATE ... WHERE RowVersion = <value read above> for it, so a concurrent revoke (which
        // itself now always rotates RowVersion - see ProjectCredentialService.RevokeAsync) makes
        // that WHERE clause match zero rows and throws DbUpdateConcurrencyException below, exactly
        // like the equivalent oldCredential protection already does.
        newCredential.RowVersion = Guid.NewGuid();

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
            target.RowVersion = Guid.NewGuid();
        }

        oldCredential.RevokedAt = now;
        oldCredential.RowVersion = Guid.NewGuid();
        session.CompletedAt = now;
        _audit.Record("sdk.repair-completed", actor, "sdk-pairing", pairingId.ToString(), session.ProjectId,
            data: new { OldCredentialId = oldCredentialId, NewCredentialId = newCredentialId, RebindCount = affected.Count });

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Either race this method guards against: another confirmed session also bound to
            // replace this exact old credential won the race and already committed its own
            // replacement, OR the new credential validated above was itself concurrently revoked,
            // between that validation and this commit. SaveChangesAsync failing here means NONE of
            // this call's changes landed - the target rebinds, the old credential's revocation, this
            // session's CompletedAt, and the audit record all rolled back together - so returning
            // anything but a clear conflict would misreport what actually happened.
            return new CompleteRepairResult(CompleteRepairOutcome.ConcurrentReplacementConflict);
        }

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
