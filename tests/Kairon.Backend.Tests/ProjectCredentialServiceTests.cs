using Kairon.Backend.Configuration;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Services;
using Kairon.Backend.Services.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// P0.2: a credential's security-state mutation and its mandatory audit record must commit
/// atomically - both are added to the same DbContext's change tracker (ProjectCredentialService.
/// CreateAsync/RevokeAsync) immediately before the ONE SaveChangesAsync call that persists them, so
/// a failure anywhere in that single commit can never leave a mutation un-audited, or an audit
/// record for a mutation that never actually landed. Mirrors the existing transactional pattern
/// SdkPairingService.CompleteRepairAsync already established for exactly this reason.
/// </summary>
public sealed class ProjectCredentialServiceTests : IDisposable
{
    private readonly TestHarness _h = new();

    private ProjectCredentialService Service() => new(_h.Db, TestHarness.Opt(new PlatformSecurityOptions()),
        TimeProvider.System, new PlatformAuditService(_h.Db, TimeProvider.System, NullLogger<PlatformAuditService>.Instance));

    // --- Successful creation: mutation + audit both commit -----------------------------------

    [Fact]
    public async Task SuccessfulCreationPersistsTheCredentialAndItsAuditRecordTogether()
    {
        _h.EnsureProject();

        var created = await Service().CreateAsync(_h.ProjectId, "my-credential", default, actor: "alice@kairon");

        Assert.NotNull(created);
        var persisted = Assert.Single(_h.Db.ProjectApiCredentials);
        Assert.Equal(created!.Id, persisted.Id);

        var audit = Assert.Single(_h.Db.PlatformAuditEvents.Where(e => e.Action == "credential.created"));
        Assert.Equal("alice@kairon", audit.Actor);
        Assert.Equal(created.Id.ToString(), audit.TargetId);
        Assert.Contains(created.Name, audit.DataJson);
        // The raw key itself must never appear in the audit trail.
        Assert.DoesNotContain(created.ApiKey, audit.DataJson ?? string.Empty);
    }

    [Fact]
    public async Task CreationAuditFailurePreventsTheCredentialFromPersisting()
    {
        // A genuine, non-mocked database-level failure - a required NOT NULL column left null on
        // an otherwise-unrelated tracked entity - poisons the SAME SaveChangesAsync call CreateAsync
        // is about to issue. This proves the atomicity guarantee itself (EF Core's SaveChangesAsync
        // either persists everything it was asked to or nothing at all), not merely that a mocked
        // method was called.
        _h.EnsureProject();
        _h.Db.Machines.Add(new Machine { HostName = null!, OperatingSystem = "Windows", AgentCredentialHash = "h", LastSeenAt = DateTime.UtcNow });

        await Assert.ThrowsAsync<DbUpdateException>(() => Service().CreateAsync(_h.ProjectId, "my-credential", default));

        // Nothing from this call landed: not the credential, not its audit record - a fresh,
        // untracked read confirms the actual committed database state, not merely this context's
        // (already-rolled-back) in-memory view of it.
        await using var fresh = _h.CreateAdditionalDbContext();
        Assert.Empty(await fresh.ProjectApiCredentials.AsNoTracking().ToListAsync());
        Assert.Empty(await fresh.PlatformAuditEvents.AsNoTracking().Where(e => e.Action == "credential.created").ToListAsync());
    }

    [Fact]
    public async Task CreationForANonExistentProjectNeverRecordsAnAuditEvent()
    {
        // The project-existence check happens before anything is added to the tracker - nothing to
        // audit for a request that never had a real target in the first place.
        var created = await Service().CreateAsync(Guid.NewGuid(), "my-credential", default);

        Assert.Null(created);
        Assert.Empty(_h.Db.PlatformAuditEvents);
    }

    // --- Successful revocation: mutation + audit both commit ---------------------------------

    [Fact]
    public async Task SuccessfulRevocationPersistsTheMutationAndItsAuditRecordTogether()
    {
        var credential = _h.SeedCredential(_h.ProjectId);

        var outcome = await Service().RevokeAsync(_h.ProjectId, credential.Id, default, actor: "bob@kairon");

        Assert.Equal(RevokeCredentialOutcome.Revoked, outcome);
        Assert.NotNull(_h.Db.ProjectApiCredentials.Single(c => c.Id == credential.Id).RevokedAt);

        var audit = Assert.Single(_h.Db.PlatformAuditEvents.Where(e => e.Action == "credential.revoked"));
        Assert.Equal("bob@kairon", audit.Actor);
        Assert.Equal(credential.Id.ToString(), audit.TargetId);
    }

    [Fact]
    public async Task RevocationAuditFailureLeavesTheCredentialActiveNotPartiallyRevoked()
    {
        var credential = _h.SeedCredential(_h.ProjectId);
        // Same genuine-failure technique as the creation test above - a real NOT NULL violation on
        // an unrelated tracked entity poisons RevokeAsync's own SaveChangesAsync call.
        _h.Db.Machines.Add(new Machine { HostName = null!, OperatingSystem = "Windows", AgentCredentialHash = "h", LastSeenAt = DateTime.UtcNow });

        await Assert.ThrowsAsync<DbUpdateException>(() => Service().RevokeAsync(_h.ProjectId, credential.Id, default));

        await using var fresh = _h.CreateAdditionalDbContext();
        var reread = await fresh.ProjectApiCredentials.AsNoTracking().SingleAsync(c => c.Id == credential.Id);
        Assert.Null(reread.RevokedAt); // still active - never partially revoked
        Assert.Empty(await fresh.PlatformAuditEvents.AsNoTracking().Where(e => e.Action == "credential.revoked").ToListAsync());
    }

    [Fact]
    public async Task RevokingAnUnknownCredentialNeverRecordsAnAuditEvent()
    {
        var outcome = await Service().RevokeAsync(_h.ProjectId, Guid.NewGuid(), default);

        Assert.Equal(RevokeCredentialOutcome.NotFound, outcome);
        Assert.Empty(_h.Db.PlatformAuditEvents);
    }

    [Fact]
    public async Task RevokingAnAlreadyRevokedCredentialIsIdempotentAndRecordsNoDuplicateAudit()
    {
        var credential = _h.SeedCredential(_h.ProjectId);
        Assert.Equal(RevokeCredentialOutcome.Revoked, await Service().RevokeAsync(_h.ProjectId, credential.Id, default));

        // A second call observes the credential already revoked - nothing new happened on this
        // call, so nothing new should be audited (avoiding a misleading duplicate "revoked" trail).
        var second = await Service().RevokeAsync(_h.ProjectId, credential.Id, default);

        Assert.Equal(RevokeCredentialOutcome.Revoked, second);
        Assert.Single(_h.Db.PlatformAuditEvents.Where(e => e.Action == "credential.revoked"));
    }

    // --- Concurrency: a losing revoke must never produce a misleading success audit -----------

    [Fact]
    public async Task ConcurrentRevokeConflictNeverRecordsAMisleadingSuccessAudit()
    {
        var credential = _h.SeedCredential(_h.ProjectId);

        using var dbA = _h.CreateAdditionalDbContext();
        using var dbB = _h.CreateAdditionalDbContext();
        await dbA.ProjectApiCredentials.SingleAsync(c => c.Id == credential.Id);
        await dbB.ProjectApiCredentials.SingleAsync(c => c.Id == credential.Id);

        var serviceA = new ProjectCredentialService(dbA, TestHarness.Opt(new PlatformSecurityOptions()), TimeProvider.System,
            new PlatformAuditService(dbA, TimeProvider.System, NullLogger<PlatformAuditService>.Instance));
        var serviceB = new ProjectCredentialService(dbB, TestHarness.Opt(new PlatformSecurityOptions()), TimeProvider.System,
            new PlatformAuditService(dbB, TimeProvider.System, NullLogger<PlatformAuditService>.Instance));

        // Real concurrency: both tasks are created (and therefore started) before either is awaited.
        var taskA = serviceA.RevokeAsync(_h.ProjectId, credential.Id, default, actor: "operator-a");
        var taskB = serviceB.RevokeAsync(_h.ProjectId, credential.Id, default, actor: "operator-b");
        var results = await Task.WhenAll(taskA, taskB);

        Assert.Contains(results, r => r == RevokeCredentialOutcome.Revoked);
        Assert.Contains(results, r => r == RevokeCredentialOutcome.ConcurrentConflict);

        // Exactly one "credential.revoked" audit event ever exists - the loser's own attempt (audit
        // record included) rolled back together with everything else in its failed SaveChangesAsync.
        await using var fresh = _h.CreateAdditionalDbContext();
        Assert.Single(await fresh.PlatformAuditEvents.AsNoTracking().Where(e => e.Action == "credential.revoked").ToListAsync());
    }

    public void Dispose() => _h.Dispose();
}
