using System.Security.Cryptography;
using System.Text;
using Kairon.Backend.Configuration;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Services.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Services;

public sealed record CreatedProjectCredential(Guid Id, string Name, string KeyPrefix, string ApiKey, DateTime CreatedAt);

/// <summary>NotFound: no such credential for this project. Revoked: it is now revoked - whether by
/// this call or, idempotently, because it was already revoked (by anyone, including a concurrent
/// SdkPairingService.CompleteRepairAsync completion) by the time this call reached it - the caller
/// only ever cares about the credential's end state, so this is reported as success rather than a
/// spurious conflict. ConcurrentConflict: this call's own attempt to set RevokedAt raced a
/// DIFFERENT concurrent write that changed RowVersion after this call already read the row but
/// before it committed (a second concurrent revoke that landed in between; a repair completion
/// that revoked it as its own old credential in between) - genuinely undetermined from here
/// whether the row ended up revoked or not, so this is reported distinctly rather than silently
/// coerced into either Revoked or NotFound.</summary>
public enum RevokeCredentialOutcome { NotFound, Revoked, ConcurrentConflict }

/// <summary>Ported near-verbatim from Azzy's productization branch - the persistent, non-expiring
/// credential a pairing redemption ultimately issues (see SdkPairingService).</summary>
public interface IProjectCredentialService
{
    /// <summary>Mutation and its mandatory "credential.created" audit record commit atomically -
    /// both are added to the same DbContext's change tracker and persisted by the one
    /// SaveChangesAsync call inside this method, so a failure partway through can never leave a
    /// newly created credential un-audited (or an audit record for a credential that was not
    /// actually created).</summary>
    Task<CreatedProjectCredential?> CreateAsync(Guid projectId, string name, CancellationToken cancellationToken,
        string actor = "local-operator");

    /// <summary>Mutation and its mandatory "credential.revoked" audit record commit atomically -
    /// see CreateAsync's remarks. Only the outcome that actually revokes something on THIS call
    /// records the audit event; NotFound and the idempotent already-revoked case have nothing new
    /// to audit, and ConcurrentConflict means none of this call's own changes landed at all.</summary>
    Task<RevokeCredentialOutcome> RevokeAsync(Guid projectId, Guid credentialId, CancellationToken cancellationToken,
        string actor = "local-operator");

    Task<bool> AuthorizeAsync(Guid projectId, string? suppliedKey, CancellationToken cancellationToken);

    /// <summary>Batch variant for the normalized telemetry endpoint (PlatformTelemetryController),
    /// which can reference more than one project per request - authorizes only when every distinct
    /// project id in the batch accepts the same supplied key.</summary>
    Task<bool> AuthorizeAsync(IEnumerable<Guid> projectIds, string? suppliedKey, CancellationToken cancellationToken);
}

public sealed class ProjectCredentialService : IProjectCredentialService
{
    private readonly AppDbContext _db;
    private readonly PlatformSecurityOptions _options;
    private readonly TimeProvider _time;
    private readonly IPlatformAuditService _audit;

    public ProjectCredentialService(AppDbContext db, IOptions<PlatformSecurityOptions> options, TimeProvider time,
        IPlatformAuditService audit)
    {
        _db = db;
        _options = options.Value;
        _time = time;
        _audit = audit;
    }

    public async Task<CreatedProjectCredential?> CreateAsync(Guid projectId, string name, CancellationToken cancellationToken,
        string actor = "local-operator")
    {
        if (!await _db.Projects.AnyAsync(x => x.Id == projectId, cancellationToken)) return null;
        var key = "krn_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = _time.GetUtcNow().UtcDateTime;
        var credential = new ProjectApiCredential
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = string.IsNullOrWhiteSpace(name) ? "Telemetry" : name.Trim(),
            KeyPrefix = key[..12],
            KeyHash = Hash(key),
            CreatedAt = now
        };
        _db.ProjectApiCredentials.Add(credential);
        // Added to the SAME change tracker as the credential above, immediately before the ONE
        // SaveChangesAsync that commits both - a mandatory audit trail for a security-relevant
        // mutation must never depend on a second, separate save succeeding afterward (that gap is
        // exactly what let a credential exist with no audit record if that second call ever failed).
        _audit.Record("credential.created", actor, "credential", credential.Id.ToString(), projectId,
            data: new { credential.Name, credential.KeyPrefix });
        await _db.SaveChangesAsync(cancellationToken);
        return new CreatedProjectCredential(credential.Id, credential.Name, credential.KeyPrefix, key, now);
    }

    public async Task<RevokeCredentialOutcome> RevokeAsync(Guid projectId, Guid credentialId, CancellationToken cancellationToken,
        string actor = "local-operator")
    {
        var credential = await _db.ProjectApiCredentials.SingleOrDefaultAsync(
            x => x.Id == credentialId && x.ProjectId == projectId, cancellationToken);
        if (credential is null) return RevokeCredentialOutcome.NotFound;
        // Idempotent: already revoked, by anyone - including a repair completion that revoked this
        // exact credential as its own old side concurrently with this call's own read. The desired
        // end state (revoked) already holds, so report success rather than a spurious conflict - and
        // nothing new happened on THIS call to audit.
        if (credential.RevokedAt is not null) return RevokeCredentialOutcome.Revoked;

        credential.RevokedAt = _time.GetUtcNow().UtcDateTime;
        // Every write that changes this credential's validity must rotate its concurrency token,
        // not just the ones inside SdkPairingService.CompleteRepairAsync's own transaction - EF's
        // optimistic-concurrency check only detects a conflict when the stored RowVersion no longer
        // matches what a concurrent reader originally saw. Leaving it untouched here would let a
        // completion that read this credential BEFORE this revoke silently overwrite/outrun it,
        // since the row's RowVersion would still equal whatever that earlier read captured.
        credential.RowVersion = Guid.NewGuid();
        // Added to the SAME change tracker, immediately before the ONE SaveChangesAsync below - if
        // that call throws (including the DbUpdateConcurrencyException caught right after), NOTHING
        // added here is persisted either, so this audit record can never survive a mutation that
        // itself did not land.
        _audit.Record("credential.revoked", actor, "credential", credentialId.ToString(), projectId);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Something else (another concurrent revoke; a repair completion revoking this same
            // credential as its old side) changed this row's RowVersion between our read above and
            // this commit - none of THIS call's changes landed, audit record included. Never
            // silently retry with our own now-stale in-memory values, and never claim NotFound (the
            // row plainly does exist) or Revoked (we cannot prove it ended up that way) - report the
            // conflict itself.
            return RevokeCredentialOutcome.ConcurrentConflict;
        }
        return RevokeCredentialOutcome.Revoked;
    }

    public async Task<bool> AuthorizeAsync(Guid projectId, string? suppliedKey, CancellationToken cancellationToken)
    {
        // Discovered processes are not connected projects. Requiring an active project here keeps
        // stale SDK processes (especially after Delete all data) from recreating orphan telemetry
        // and autonomous incidents under arbitrary project ids.
        if (!await _db.Projects.AsNoTracking()
                .AnyAsync(x => x.Id == projectId && x.IsActive, cancellationToken))
            return false;

        if (!_options.RequireTelemetryKey) return true;
        if (string.IsNullOrWhiteSpace(suppliedKey) || suppliedKey.Length < 16) return false;
        var prefix = suppliedKey[..Math.Min(12, suppliedKey.Length)];
        var candidates = await _db.ProjectApiCredentials.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.KeyPrefix == prefix && x.RevokedAt == null)
            .Select(x => x.KeyHash)
            .ToListAsync(cancellationToken);
        var suppliedHash = Hash(suppliedKey);
        return candidates.Any(candidate => CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(suppliedHash)));
    }

    public async Task<bool> AuthorizeAsync(IEnumerable<Guid> projectIds, string? suppliedKey,
        CancellationToken cancellationToken)
    {
        var ids = projectIds.Distinct().ToList();
        if (ids.Count == 0) return false;
        foreach (var projectId in ids)
        {
            if (!await AuthorizeAsync(projectId, suppliedKey, cancellationToken)) return false;
        }
        return true;
    }

    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
