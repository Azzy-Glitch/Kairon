using Kairon.Backend.Configuration;
using Kairon.Backend.Controllers;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Services;
using Kairon.Backend.Services.Audit;
using Kairon.Backend.Services.Remediation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// Baseline coverage for the existing SdkPairingService/SdkPairingController - previously
/// untested (confirmed via a full-repo search before this task: zero pairing-service/controller
/// tests existed). Also covers the new GetStatusAsync/GetStatus status endpoint added to support
/// the frontend re-pairing UX, which reuses this exact same pairing session/redemption mechanism -
/// a re-pair is just another pairing session for an already-connected project, not a second
/// credential-issuance path.
/// </summary>
public sealed class SdkPairingTests : IDisposable
{
    private readonly TestHarness _h = new();

    private SdkPairingService Service() => new(_h.Db,
        new ProjectCredentialService(_h.Db, TestHarness.Opt(new PlatformSecurityOptions()), TimeProvider.System),
        TimeProvider.System, new ConfigurationBuilder().Build(),
        new PlatformAuditService(_h.Db, TimeProvider.System, NullLogger<PlatformAuditService>.Instance));

    [Theory]
    [InlineData("dotnet")]
    [InlineData(".net")]
    [InlineData("python")]
    public async Task CreatePairingSucceedsAndNeverPersistsTheRawCode(string sdkType)
    {
        _h.EnsureProject();
        var result = await Service().CreateAsync(_h.ProjectId, sdkType, default);

        Assert.NotNull(result);
        Assert.StartsWith("pair_", result!.Code);
        Assert.True(result.Code.Length >= 20);

        var session = Assert.Single(_h.Db.SdkPairingSessions);
        Assert.Equal(_h.ProjectId, session.ProjectId);
        Assert.NotEqual(result.Code, session.CodeHash); // only the hash is ever stored
        Assert.Null(session.RedeemedAt);
        Assert.Null(session.RevokedAt);
    }

    [Fact]
    public async Task CreatePairingRejectsUnknownProjectOrSdkType()
    {
        _h.EnsureProject();
        var service = Service();

        Assert.Null(await service.CreateAsync(Guid.NewGuid(), "dotnet", default));
        Assert.Null(await service.CreateAsync(_h.ProjectId, "java", default));
        Assert.Empty(_h.Db.SdkPairingSessions);
    }

    [Fact]
    public async Task RedeemSucceedsIssuesAFreshCredentialAndMarksTheSessionRedeemed()
    {
        _h.EnsureProject();
        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;

        var paired = await service.RedeemAsync(created.Code, "python", "1.0.0", default);

        Assert.NotNull(paired);
        Assert.Equal(_h.ProjectId, paired!.ProjectId);
        Assert.NotEmpty(paired.ApiKey);

        var credential = Assert.Single(_h.Db.ProjectApiCredentials);
        Assert.Null(credential.RevokedAt);

        var session = _h.Db.SdkPairingSessions.Single(s => s.Id == created.PairingId);
        Assert.NotNull(session.RedeemedAt);
    }

    [Fact]
    public async Task RedeemFailsForWrongSdkType()
    {
        _h.EnsureProject();
        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;

        Assert.Null(await service.RedeemAsync(created.Code, "dotnet", "1.0.0", default));
        Assert.Empty(_h.Db.ProjectApiCredentials);
        Assert.Null(_h.Db.SdkPairingSessions.Single(s => s.Id == created.PairingId).RedeemedAt);
    }

    [Fact]
    public async Task RedeemFailsForAnExpiredSession()
    {
        _h.EnsureProject();
        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;
        _h.Db.SdkPairingSessions.Single(s => s.Id == created.PairingId).ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        _h.Db.SaveChanges();

        Assert.Null(await service.RedeemAsync(created.Code, "python", "1.0.0", default));
        Assert.Empty(_h.Db.ProjectApiCredentials);
    }

    [Fact]
    public async Task RedeemFailsForARevokedSession()
    {
        _h.EnsureProject();
        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;
        Assert.True(await service.RevokePairingAsync(created.PairingId, default));

        Assert.Null(await service.RedeemAsync(created.Code, "python", "1.0.0", default));
        Assert.Empty(_h.Db.ProjectApiCredentials);
    }

    [Fact]
    public async Task PairingCodesAreSingleUse()
    {
        _h.EnsureProject();
        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;

        var first = await service.RedeemAsync(created.Code, "python", "1.0.0", default);
        var second = await service.RedeemAsync(created.Code, "python", "1.0.0", default);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Single(_h.Db.ProjectApiCredentials); // the second attempt never issued another credential
    }

    [Fact]
    public async Task RevokingAPendingSessionSucceedsAndBlocksLaterRedemption()
    {
        _h.EnsureProject();
        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;

        Assert.True(await service.RevokePairingAsync(created.PairingId, default));
        Assert.Null(await service.RedeemAsync(created.Code, "python", "1.0.0", default));
    }

    [Fact]
    public async Task DoubleRevokeIsNotAllowed()
    {
        _h.EnsureProject();
        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;

        Assert.True(await service.RevokePairingAsync(created.PairingId, default));
        Assert.False(await service.RevokePairingAsync(created.PairingId, default));
    }

    [Fact]
    public async Task RevokingAnAlreadyRedeemedSessionFails()
    {
        _h.EnsureProject();
        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;
        await service.RedeemAsync(created.Code, "python", "1.0.0", default);

        Assert.False(await service.RevokePairingAsync(created.PairingId, default));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("redeemed")]
    [InlineData("confirmed")]
    [InlineData("cancelled")]
    [InlineData("expired")]
    public async Task GetStatusReflectsEveryTerminalAndNonTerminalState(string scenario)
    {
        _h.EnsureProject();
        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;

        var expected = scenario switch
        {
            "redeemed" => "Redeemed",
            "confirmed" => "Confirmed",
            "cancelled" => "Cancelled",
            "expired" => "Expired",
            _ => "Pending"
        };
        PairedSdk? paired = null;
        switch (scenario)
        {
            case "redeemed": await service.RedeemAsync(created.Code, "python", "1.0.0", default); break;
            case "confirmed":
                paired = await service.RedeemAsync(created.Code, "python", "1.0.0", default);
                await service.ConfirmAsync(created.PairingId, paired!.ApiKey, default);
                break;
            case "cancelled": await service.RevokePairingAsync(created.PairingId, default); break;
            case "expired":
                _h.Db.SdkPairingSessions.Single(s => s.Id == created.PairingId).ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
                _h.Db.SaveChanges();
                break;
        }

        var status = await service.GetStatusAsync(created.PairingId, default);

        Assert.NotNull(status);
        Assert.Equal(expected, status!.Status);
        Assert.Equal(created.PairingId, status.PairingId);
        Assert.Null(status.CompletedAt); // none of these scenarios ever run CompleteRepairAsync
    }

    [Fact]
    public async Task GetStatusReflectsCompletedAndExposesCompletedAtOnceARepairActuallyCompletes()
    {
        // The authoritative signal SdkPage.jsx's refresh-recovery logic depends on (see its own
        // remarks): CompletedAt/Status="Completed" must be visible from a PLAIN status poll, with
        // no further mutating call, so a client that lost the CompleteRepairAsync HTTP response can
        // still learn - without retrying it - that the operation genuinely already happened.
        _h.EnsureProject();
        var service = Service();
        var oldCredential = _h.SeedCredential(_h.ProjectId, "old-hash");
        var created = (await service.CreateAsync(_h.ProjectId, "python", default, replacesCredentialId: oldCredential.Id))!;
        var paired = (await service.RedeemAsync(created.Code, "python", "1.0.0", default))!;
        await service.ConfirmAsync(created.PairingId, paired.ApiKey, default);

        var beforeCompletion = await service.GetStatusAsync(created.PairingId, default);
        Assert.Equal("Confirmed", beforeCompletion!.Status);
        Assert.Null(beforeCompletion.CompletedAt);

        var completion = await service.CompleteRepairAsync(created.PairingId, oldCredential.Id, default);
        Assert.Equal(CompleteRepairOutcome.Success, completion.Outcome);

        // Reads back through a brand new status call - exactly what a recovering client does after
        // a refresh - not merely inspecting the CompleteRepairAsync return value itself.
        var afterCompletion = await service.GetStatusAsync(created.PairingId, default);
        Assert.Equal("Completed", afterCompletion!.Status);
        Assert.NotNull(afterCompletion.CompletedAt);
        Assert.NotNull(afterCompletion.ConfirmedAt);
    }

    [Fact]
    public async Task GetStatusReturnsNullForAnUnknownSession()
    {
        Assert.Null(await Service().GetStatusAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task GetStatusNeverExposesTheCodeOrItsHash()
    {
        _h.EnsureProject();
        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;
        var session = _h.Db.SdkPairingSessions.Single(s => s.Id == created.PairingId);

        var status = await service.GetStatusAsync(created.PairingId, default);
        var serialized = SreJson.Serialize(status);

        Assert.DoesNotContain(created.Code, serialized);
        Assert.DoesNotContain(session.CodeHash, serialized);
    }

    // --- Controller: authorization shape ---

    [Fact]
    public void CreateRevokeAndStatusRequireOperatorButRedeemDoesNot()
    {
        var type = typeof(SdkPairingController);
        Assert.NotEmpty(type.GetMethod(nameof(SdkPairingController.Create))!.GetCustomAttributes(typeof(RequiresOperatorAttribute), false));
        Assert.NotEmpty(type.GetMethod(nameof(SdkPairingController.RevokePairing))!.GetCustomAttributes(typeof(RequiresOperatorAttribute), false));
        Assert.NotEmpty(type.GetMethod(nameof(SdkPairingController.GetStatus))!.GetCustomAttributes(typeof(RequiresOperatorAttribute), false));
        // The SDK redeems unattended, with no operator present - the single-use code is its own proof of intent.
        Assert.Empty(type.GetMethod(nameof(SdkPairingController.Pair))!.GetCustomAttributes(typeof(RequiresOperatorAttribute), false));
    }

    [Fact]
    public async Task ControllerGetStatusReturns404ForAnUnknownSessionWithoutLeakingInternals()
    {
        var controller = new SdkPairingController(Service())
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        Assert.IsType<NotFoundResult>(await controller.GetStatus(Guid.NewGuid(), default));
    }

    // --- Phase 11: pairing cannot issue a usable-looking credential for an inactive project ---

    [Fact]
    public async Task CreatePairingRejectsAnInactiveProject()
    {
        var project = new Project { Name = "inactive", IsActive = false };
        _h.Db.Projects.Add(project);
        _h.Db.SaveChanges();

        Assert.Null(await Service().CreateAsync(project.Id, "python", default));
        Assert.Empty(_h.Db.SdkPairingSessions);
    }

    [Fact]
    public async Task RedeemRejectsIfTheProjectBecameInactiveAfterTheCodeWasMinted()
    {
        _h.EnsureProject();
        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;

        _h.Db.Projects.Single(p => p.Id == _h.ProjectId).IsActive = false;
        _h.Db.SaveChanges();

        Assert.Null(await service.RedeemAsync(created.Code, "python", "1.0.0", default));
        Assert.Empty(_h.Db.ProjectApiCredentials);
    }

    // --- Phase 12: an already-expired session cannot become Cancelled -----------------------

    [Fact]
    public async Task RevokingAnAlreadyExpiredSessionFailsAndPreservesTheExpiredStatus()
    {
        _h.EnsureProject();
        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;
        _h.Db.SdkPairingSessions.Single(s => s.Id == created.PairingId).ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        _h.Db.SaveChanges();

        Assert.False(await service.RevokePairingAsync(created.PairingId, default));
        var status = await service.GetStatusAsync(created.PairingId, default);
        Assert.Equal("Expired", status!.Status);
    }

    // --- Phase 3/4: ConfirmAsync - proof of receipt/persistence, distinct from Redeemed ------

    [Fact]
    public async Task ConfirmRequiresTheExactIssuedCredentialAndIsIdempotent()
    {
        _h.EnsureProject();
        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;
        var paired = (await service.RedeemAsync(created.Code, "python", "1.0.0", default))!;

        Assert.False(await service.ConfirmAsync(created.PairingId, "wrong-key", default));
        Assert.Null(_h.Db.SdkPairingSessions.Single(s => s.Id == created.PairingId).ConfirmedAt);

        Assert.True(await service.ConfirmAsync(created.PairingId, paired.ApiKey, default));
        Assert.True(await service.ConfirmAsync(created.PairingId, paired.ApiKey, default)); // idempotent replay
        Assert.NotNull(_h.Db.SdkPairingSessions.Single(s => s.Id == created.PairingId).ConfirmedAt);
    }

    [Fact]
    public async Task ConfirmFailsForAnUnredeemedSession()
    {
        _h.EnsureProject();
        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;

        Assert.False(await service.ConfirmAsync(created.PairingId, "any-key", default));
    }

    [Fact]
    public async Task ConfirmFailsOnceTheIssuedCredentialHasBeenRevoked()
    {
        _h.EnsureProject();
        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;
        var paired = (await service.RedeemAsync(created.Code, "python", "1.0.0", default))!;
        var issuedCredentialId = _h.Db.SdkPairingSessions.Single(s => s.Id == created.PairingId).IssuedCredentialId!.Value;
        var credentials = new ProjectCredentialService(_h.Db, TestHarness.Opt(new PlatformSecurityOptions()), TimeProvider.System);
        await credentials.RevokeAsync(_h.ProjectId, issuedCredentialId, default);

        Assert.False(await service.ConfirmAsync(created.PairingId, paired.ApiKey, default));
    }

    [Fact]
    public async Task ARepeatedConfirmationWithAnUnrelatedKeyNeverSucceedsEvenAfterTheSessionWasAlreadyConfirmed()
    {
        // Regression: confirmation must revalidate the exact key on EVERY call, not just the
        // first - otherwise, once ConfirmedAt is set, literally any non-empty string would be
        // accepted as a "replay", including a completely unrelated caller's key.
        _h.EnsureProject();
        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;
        var paired = (await service.RedeemAsync(created.Code, "python", "1.0.0", default))!;
        Assert.True(await service.ConfirmAsync(created.PairingId, paired.ApiKey, default));

        Assert.False(await service.ConfirmAsync(created.PairingId, "some-unrelated-key", default));
        Assert.False(await service.ConfirmAsync(created.PairingId, "", default));

        // The genuine replay (the real key, again) must still succeed.
        Assert.True(await service.ConfirmAsync(created.PairingId, paired.ApiKey, default));
    }

    [Fact]
    public async Task ConfirmationFailsIfTheIssuedCredentialIsRevokedAfterItWasAlreadyConfirmedOnce()
    {
        _h.EnsureProject();
        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;
        var paired = (await service.RedeemAsync(created.Code, "python", "1.0.0", default))!;
        Assert.True(await service.ConfirmAsync(created.PairingId, paired.ApiKey, default));

        var issuedCredentialId = _h.Db.SdkPairingSessions.Single(s => s.Id == created.PairingId).IssuedCredentialId!.Value;
        var credentials = new ProjectCredentialService(_h.Db, TestHarness.Opt(new PlatformSecurityOptions()), TimeProvider.System);
        await credentials.RevokeAsync(_h.ProjectId, issuedCredentialId, default);

        Assert.False(await service.ConfirmAsync(created.PairingId, paired.ApiKey, default));
    }

    [Fact]
    public async Task ConfirmRejectsAKeyThatBelongsToAnotherProjectsCredentialEvenIfHashedFormatMatches()
    {
        _h.EnsureProject();
        var otherProject = new Project { Name = "other-project-for-confirm" };
        _h.Db.Projects.Add(otherProject);
        _h.Db.SaveChanges();

        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;
        await service.RedeemAsync(created.Code, "python", "1.0.0", default);

        var otherCreated = (await service.CreateAsync(otherProject.Id, "python", default))!;
        var otherPaired = (await service.RedeemAsync(otherCreated.Code, "python", "1.0.0", default))!;

        // otherPaired.ApiKey is a real, currently-active credential - just not this session's.
        Assert.False(await service.ConfirmAsync(created.PairingId, otherPaired.ApiKey, default));
    }

    // --- Phase 5: CompleteRepairAsync - atomic rebind + revoke, gated on Confirmed -----------

    [Fact]
    public async Task CompleteRepairRefusesUntilTheNewCredentialIsConfirmed()
    {
        _h.EnsureProject();
        var service = Service();
        var oldCredential = _h.SeedCredential(_h.ProjectId, "old-hash");
        var machine = _h.SeedMachine();
        _h.SeedRemediationTarget(machine.Id, oldCredential.Id, machine.HostName);
        var created = (await service.CreateAsync(_h.ProjectId, "python", default, replacesCredentialId: oldCredential.Id))!;
        await service.RedeemAsync(created.Code, "python", "1.0.0", default);

        var result = await service.CompleteRepairAsync(created.PairingId, oldCredential.Id, default);

        Assert.Equal(CompleteRepairOutcome.NotConfirmed, result.Outcome);
        Assert.Null(_h.Db.ProjectApiCredentials.Single(c => c.Id == oldCredential.Id).RevokedAt);
    }

    [Fact]
    public async Task CompleteRepairRebindsEnabledTargetsAndRevokesTheOldCredentialOnceConfirmed()
    {
        _h.EnsureProject();
        var service = Service();
        var oldCredential = _h.SeedCredential(_h.ProjectId, "old-hash");
        var machine = _h.SeedMachine();
        var target = _h.SeedRemediationTarget(machine.Id, oldCredential.Id, machine.HostName);

        var created = (await service.CreateAsync(_h.ProjectId, "python", default, replacesCredentialId: oldCredential.Id))!;
        var paired = (await service.RedeemAsync(created.Code, "python", "1.0.0", default))!;
        Assert.True(await service.ConfirmAsync(created.PairingId, paired.ApiKey, default));

        var result = await service.CompleteRepairAsync(created.PairingId, oldCredential.Id, default);

        Assert.Equal(CompleteRepairOutcome.Success, result.Outcome);
        Assert.Equal(1, result.RebindCount);
        var newCredentialId = _h.Db.SdkPairingSessions.Single(s => s.Id == created.PairingId).IssuedCredentialId!.Value;
        Assert.Equal(newCredentialId, result.NewCredentialId);
        Assert.Equal(newCredentialId, _h.Db.RemediationTargets.Single(t => t.Id == target.Id).TelemetryCredentialId);
        Assert.NotNull(_h.Db.ProjectApiCredentials.Single(c => c.Id == oldCredential.Id).RevokedAt);
    }

    [Fact]
    public async Task CompleteRepairOnlyRebindsEnabledTargetsNeverDisabledOnes()
    {
        _h.EnsureProject();
        var service = Service();
        var oldCredential = _h.SeedCredential(_h.ProjectId, "old-hash");
        var machine = _h.SeedMachine();
        var enabledTarget = _h.SeedRemediationTarget(machine.Id, oldCredential.Id, machine.HostName, service: "SvcA");
        var disabledTarget = _h.SeedRemediationTarget(machine.Id, oldCredential.Id, machine.HostName, service: "SvcB", enabled: false);

        var created = (await service.CreateAsync(_h.ProjectId, "python", default, replacesCredentialId: oldCredential.Id))!;
        var paired = (await service.RedeemAsync(created.Code, "python", "1.0.0", default))!;
        await service.ConfirmAsync(created.PairingId, paired.ApiKey, default);

        var result = await service.CompleteRepairAsync(created.PairingId, oldCredential.Id, default);

        Assert.Equal(CompleteRepairOutcome.Success, result.Outcome);
        Assert.Equal(1, result.RebindCount);
        Assert.NotEqual(oldCredential.Id, _h.Db.RemediationTargets.Single(t => t.Id == enabledTarget.Id).TelemetryCredentialId);
        Assert.Equal(oldCredential.Id, _h.Db.RemediationTargets.Single(t => t.Id == disabledTarget.Id).TelemetryCredentialId);
    }

    [Fact]
    public async Task CompleteRepairOnlyTouchesEnabledSameProjectTargetsBoundToTheExactOldCredential()
    {
        _h.EnsureProject();
        var otherProject = new Project { Name = "other-project-for-rebind-scope" };
        _h.Db.Projects.Add(otherProject);
        _h.Db.SaveChanges();

        var oldCredential = _h.SeedCredential(_h.ProjectId, "old-hash");
        var otherCredential = _h.SeedCredential(_h.ProjectId, "other-hash");
        var machine = _h.SeedMachine();

        var targetA = _h.SeedRemediationTarget(machine.Id, oldCredential.Id, machine.HostName, service: "SvcA");
        var targetB = _h.SeedRemediationTarget(machine.Id, oldCredential.Id, machine.HostName, service: "SvcB");
        var targetC = _h.SeedRemediationTarget(machine.Id, otherCredential.Id, machine.HostName, service: "SvcC");
        var disabledTarget = _h.SeedRemediationTarget(machine.Id, oldCredential.Id, machine.HostName, service: "SvcDisabled", enabled: false);
        // A data anomaly a correct implementation must still defend against: another project's own
        // row somehow carrying this project's old credential id. ProjectId scoping - not "does any
        // row reference this credential id" - must be what decides eligibility.
        var otherProjectTarget = _h.SeedRemediationTarget(machine.Id, oldCredential.Id, machine.HostName,
            service: "SvcOtherProject", projectId: otherProject.Id);

        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default, replacesCredentialId: oldCredential.Id))!;
        var paired = (await service.RedeemAsync(created.Code, "python", "1.0.0", default))!;
        await service.ConfirmAsync(created.PairingId, paired.ApiKey, default);

        var result = await service.CompleteRepairAsync(created.PairingId, oldCredential.Id, default);
        var newCredentialId = _h.Db.SdkPairingSessions.Single(s => s.Id == created.PairingId).IssuedCredentialId!.Value;

        Assert.Equal(CompleteRepairOutcome.Success, result.Outcome);
        Assert.Equal(2, result.RebindCount);
        Assert.Equal(newCredentialId, _h.Db.RemediationTargets.Single(t => t.Id == targetA.Id).TelemetryCredentialId);
        Assert.Equal(newCredentialId, _h.Db.RemediationTargets.Single(t => t.Id == targetB.Id).TelemetryCredentialId);
        Assert.Equal(otherCredential.Id, _h.Db.RemediationTargets.Single(t => t.Id == targetC.Id).TelemetryCredentialId);
        Assert.Equal(oldCredential.Id, _h.Db.RemediationTargets.Single(t => t.Id == disabledTarget.Id).TelemetryCredentialId);
        Assert.Equal(oldCredential.Id, _h.Db.RemediationTargets.Single(t => t.Id == otherProjectTarget.Id).TelemetryCredentialId);
        Assert.NotNull(_h.Db.ProjectApiCredentials.Single(c => c.Id == oldCredential.Id).RevokedAt);
    }

    [Fact]
    public async Task CreatingAReRepairSessionRejectsAnOldCredentialFromAnotherProject()
    {
        _h.EnsureProject();
        var otherProject = new Project { Name = "other-project" };
        _h.Db.Projects.Add(otherProject);
        _h.Db.SaveChanges();
        var otherCredential = _h.SeedCredential(otherProject.Id, "other-hash");

        Assert.Null(await Service().CreateAsync(_h.ProjectId, "python", default, replacesCredentialId: otherCredential.Id));
        Assert.Empty(_h.Db.SdkPairingSessions);
    }

    [Fact]
    public async Task CreatingAReRepairSessionRejectsAnAlreadyRevokedOldCredential()
    {
        _h.EnsureProject();
        var revoked = _h.SeedCredential(_h.ProjectId, "revoked-hash");
        revoked.RevokedAt = DateTime.UtcNow;
        _h.Db.SaveChanges();

        Assert.Null(await Service().CreateAsync(_h.ProjectId, "python", default, replacesCredentialId: revoked.Id));
        Assert.Empty(_h.Db.SdkPairingSessions);
    }

    [Fact]
    public async Task CompleteRepairRejectsAWrongButRealSameProjectCredentialTheSessionWasNeverBoundTo()
    {
        _h.EnsureProject();
        var boundCredential = _h.SeedCredential(_h.ProjectId, "bound-hash");
        var unrelatedCredential = _h.SeedCredential(_h.ProjectId, "unrelated-hash");

        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default, replacesCredentialId: boundCredential.Id))!;
        var paired = (await service.RedeemAsync(created.Code, "python", "1.0.0", default))!;
        await service.ConfirmAsync(created.PairingId, paired.ApiKey, default);

        // A real, active, same-project credential - just not the one THIS session was bound to.
        var result = await service.CompleteRepairAsync(created.PairingId, unrelatedCredential.Id, default);

        Assert.Equal(CompleteRepairOutcome.OldCredentialMismatch, result.Outcome);
        Assert.Null(_h.Db.ProjectApiCredentials.Single(c => c.Id == unrelatedCredential.Id).RevokedAt);
        Assert.Null(_h.Db.ProjectApiCredentials.Single(c => c.Id == boundCredential.Id).RevokedAt);
    }

    [Fact]
    public async Task ReplayingCompletionAgainstADifferentCredentialNeverSucceeds()
    {
        _h.EnsureProject();
        var oldCredential = _h.SeedCredential(_h.ProjectId, "old-hash");
        var unrelatedCredential = _h.SeedCredential(_h.ProjectId, "unrelated-hash");

        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default, replacesCredentialId: oldCredential.Id))!;
        var paired = (await service.RedeemAsync(created.Code, "python", "1.0.0", default))!;
        await service.ConfirmAsync(created.PairingId, paired.ApiKey, default);
        Assert.Equal(CompleteRepairOutcome.Success, (await service.CompleteRepairAsync(created.PairingId, oldCredential.Id, default)).Outcome);

        // The session already completed successfully - a replay naming a DIFFERENT credential must
        // never be treated as "already done" just because this session is already complete.
        var replay = await service.CompleteRepairAsync(created.PairingId, unrelatedCredential.Id, default);

        Assert.Equal(CompleteRepairOutcome.OldCredentialMismatch, replay.Outcome);
        Assert.Null(_h.Db.ProjectApiCredentials.Single(c => c.Id == unrelatedCredential.Id).RevokedAt);
    }

    /// <summary>A data-integrity defense-in-depth check: even if a credential's own ProjectId were
    /// somehow mutated after it was bound as a session's ReplacesCredentialId (never possible
    /// through this codebase's normal write paths, which never reassign a credential's project),
    /// CompleteRepairAsync must still refuse rather than trust the binding blindly.</summary>
    [Fact]
    public async Task CompleteRepairDefendsAgainstTheBoundCredentialNoLongerBelongingToTheSessionsProject()
    {
        _h.EnsureProject();
        var otherProject = new Project { Name = "other-project" };
        _h.Db.Projects.Add(otherProject);
        _h.Db.SaveChanges();
        var credential = _h.SeedCredential(_h.ProjectId, "old-hash");

        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default, replacesCredentialId: credential.Id))!;
        var paired = (await service.RedeemAsync(created.Code, "python", "1.0.0", default))!;
        await service.ConfirmAsync(created.PairingId, paired.ApiKey, default);

        credential.ProjectId = otherProject.Id; // simulated data anomaly
        _h.Db.SaveChanges();

        var result = await service.CompleteRepairAsync(created.PairingId, credential.Id, default);

        Assert.Equal(CompleteRepairOutcome.OldCredentialWrongProject, result.Outcome);
        Assert.Null(_h.Db.ProjectApiCredentials.Single(c => c.Id == credential.Id).RevokedAt);
    }

    [Fact]
    public async Task CompleteRepairIsIdempotent()
    {
        _h.EnsureProject();
        var service = Service();
        var oldCredential = _h.SeedCredential(_h.ProjectId, "old-hash");
        var machine = _h.SeedMachine();
        _h.SeedRemediationTarget(machine.Id, oldCredential.Id, machine.HostName);

        var created = (await service.CreateAsync(_h.ProjectId, "python", default, replacesCredentialId: oldCredential.Id))!;
        var paired = (await service.RedeemAsync(created.Code, "python", "1.0.0", default))!;
        await service.ConfirmAsync(created.PairingId, paired.ApiKey, default);

        var first = await service.CompleteRepairAsync(created.PairingId, oldCredential.Id, default);
        var second = await service.CompleteRepairAsync(created.PairingId, oldCredential.Id, default);

        Assert.Equal(CompleteRepairOutcome.Success, first.Outcome);
        Assert.Equal(1, first.RebindCount);
        Assert.Equal(CompleteRepairOutcome.Success, second.Outcome);
        Assert.Equal(0, second.RebindCount); // nothing left to rebind/revoke a second time
    }

    /// <summary>The core P1 race this task fixes: Redeemed/Confirmed only prove the NEW credential
    /// was valid at THAT time - it can still be independently revoked (a second, unrelated re-pair;
    /// a direct operator action) any time between confirmation and completion. Completion must
    /// re-check the new credential itself, inside its own transaction, immediately before acting -
    /// not trust that a Confirmed session's credential is still good.</summary>
    [Fact]
    public async Task CompleteRepairFailsSafelyIfTheNewCredentialWasRevokedAfterConfirmationButBeforeCompletion()
    {
        _h.EnsureProject();
        var oldCredential = _h.SeedCredential(_h.ProjectId, "old-hash");
        var machine = _h.SeedMachine();
        var target = _h.SeedRemediationTarget(machine.Id, oldCredential.Id, machine.HostName);

        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default, replacesCredentialId: oldCredential.Id))!;
        var paired = (await service.RedeemAsync(created.Code, "python", "1.0.0", default))!;
        Assert.True(await service.ConfirmAsync(created.PairingId, paired.ApiKey, default));

        var newCredentialId = _h.Db.SdkPairingSessions.Single(s => s.Id == created.PairingId).IssuedCredentialId!.Value;
        var credentials = new ProjectCredentialService(_h.Db, TestHarness.Opt(new PlatformSecurityOptions()), TimeProvider.System);
        await credentials.RevokeAsync(_h.ProjectId, newCredentialId, default); // independently revoked before completion

        var result = await service.CompleteRepairAsync(created.PairingId, oldCredential.Id, default);

        Assert.Equal(CompleteRepairOutcome.NewCredentialRevoked, result.Outcome);
        // Nothing committed: the old (still genuinely working) credential stays active, the target
        // stays bound to it, and the newly-revoked credential was never bound to anything.
        Assert.Null(_h.Db.ProjectApiCredentials.Single(c => c.Id == oldCredential.Id).RevokedAt);
        Assert.Equal(oldCredential.Id, _h.Db.RemediationTargets.Single(t => t.Id == target.Id).TelemetryCredentialId);
        Assert.Null(_h.Db.SdkPairingSessions.Single(s => s.Id == created.PairingId).CompletedAt);

        // The session is not permanently stuck either - completing is never possible for THIS now-
        // dead credential, but the old credential remaining active means telemetry/remediation keep
        // working, and an operator can start a fresh re-pair for the same old credential.
        Assert.False(await service.ConfirmAsync(created.PairingId, paired.ApiKey, default)); // the confirmed key is dead too
    }

    /// <summary>The core Blocker-1 race: TWO different, independently CONFIRMED re-pair sessions
    /// both bound (at creation) to replace the exact same old credential - a real scenario an
    /// operator could trigger by starting a second re-pair before noticing the first one is still
    /// in flight. Genuinely concurrent (Task.WhenAll, neither call awaited before the other starts)
    /// - not sequential calls dressed up as a race. Both DbContexts pre-track the SAME old-credential
    /// row before either call runs, with the SAME original RowVersion, so the race is deterministic
    /// rather than dependent on real thread-scheduling luck: only the actual database-level
    /// concurrency check at commit time - not the earlier in-memory RevokedAt read, which the race
    /// can outrun - decides the single winner.</summary>
    [Fact]
    public async Task TwoConfirmedSessionsRacingToReplaceTheSameOldCredentialResolveToExactlyOneWinner()
    {
        _h.EnsureProject();
        var oldCredential = _h.SeedCredential(_h.ProjectId, "old-hash");
        var machine = _h.SeedMachine();
        var target = _h.SeedRemediationTarget(machine.Id, oldCredential.Id, machine.HostName);

        var setupService = Service();
        var createdA = (await setupService.CreateAsync(_h.ProjectId, "python", default, replacesCredentialId: oldCredential.Id))!;
        var pairedA = (await setupService.RedeemAsync(createdA.Code, "python", "1.0.0", default))!;
        Assert.True(await setupService.ConfirmAsync(createdA.PairingId, pairedA.ApiKey, default));

        var createdB = (await setupService.CreateAsync(_h.ProjectId, "dotnet", default, replacesCredentialId: oldCredential.Id))!;
        var pairedB = (await setupService.RedeemAsync(createdB.Code, "dotnet", "1.0.0", default))!;
        Assert.True(await setupService.ConfirmAsync(createdB.PairingId, pairedB.ApiKey, default));

        using var dbA = _h.CreateAdditionalDbContext();
        using var dbB = _h.CreateAdditionalDbContext();
        // Pre-track the SAME old-credential row via BOTH contexts, with the SAME original
        // RowVersion, before either commits - reproduces "both requests read before either writes"
        // deterministically rather than hoping real thread timing lines up.
        await dbA.ProjectApiCredentials.SingleAsync(c => c.Id == oldCredential.Id);
        await dbB.ProjectApiCredentials.SingleAsync(c => c.Id == oldCredential.Id);

        var serviceA = new SdkPairingService(dbA,
            new ProjectCredentialService(dbA, TestHarness.Opt(new PlatformSecurityOptions()), TimeProvider.System),
            TimeProvider.System, new ConfigurationBuilder().Build(),
            new PlatformAuditService(dbA, TimeProvider.System, NullLogger<PlatformAuditService>.Instance));
        var serviceB = new SdkPairingService(dbB,
            new ProjectCredentialService(dbB, TestHarness.Opt(new PlatformSecurityOptions()), TimeProvider.System),
            TimeProvider.System, new ConfigurationBuilder().Build(),
            new PlatformAuditService(dbB, TimeProvider.System, NullLogger<PlatformAuditService>.Instance));

        // Real concurrency (neither task is awaited before the other starts).
        var taskA = serviceA.CompleteRepairAsync(createdA.PairingId, oldCredential.Id, default);
        var taskB = serviceB.CompleteRepairAsync(createdB.PairingId, oldCredential.Id, default);
        var results = await Task.WhenAll(taskA, taskB);

        // Exactly one winner - never both, never neither.
        Assert.Single(results, r => r.Outcome == CompleteRepairOutcome.Success);
        Assert.Single(results, r => r.Outcome == CompleteRepairOutcome.ConcurrentReplacementConflict);

        // _h.Db already holds tracked (now-stale) instances of these rows from the setup above -
        // every verification query below reads AsNoTracking() so it reflects what dbA/dbB actually
        // committed, not _h.Db's own cached pre-race snapshot.
        var winner = results.Single(r => r.Outcome == CompleteRepairOutcome.Success);
        var winningNewCredentialId = winner.NewCredentialId!.Value;
        var issuedCredentialIdA = await _h.Db.SdkPairingSessions.AsNoTracking()
            .Where(s => s.Id == createdA.PairingId).Select(s => s.IssuedCredentialId).SingleAsync();
        var winningSessionId = winningNewCredentialId == issuedCredentialIdA ? createdA.PairingId : createdB.PairingId;
        var losingSessionId = winningSessionId == createdA.PairingId ? createdB.PairingId : createdA.PairingId;

        // Old credential is revoked exactly once, by the winner.
        Assert.NotNull((await _h.Db.ProjectApiCredentials.AsNoTracking().SingleAsync(c => c.Id == oldCredential.Id)).RevokedAt);

        // The remediation target is bound to the WINNER's newly issued credential - never left
        // half-migrated, and never bound to the loser's (unused, still-active) credential.
        Assert.Equal(winningNewCredentialId, (await _h.Db.RemediationTargets.AsNoTracking().SingleAsync(t => t.Id == target.Id)).TelemetryCredentialId);

        // The winning session is marked completed; the losing session is NOT - its own attempt
        // never actually committed, so it must not be reported (now, or on any future poll) as
        // having successfully completed.
        Assert.NotNull((await _h.Db.SdkPairingSessions.AsNoTracking().SingleAsync(s => s.Id == winningSessionId)).CompletedAt);
        Assert.Null((await _h.Db.SdkPairingSessions.AsNoTracking().SingleAsync(s => s.Id == losingSessionId)).CompletedAt);

        // Exactly one completion audit event was ever recorded - the loser's attempt (audit record
        // included) rolled back together with everything else in its failed SaveChangesAsync call.
        Assert.Single(await _h.Db.PlatformAuditEvents.AsNoTracking().Where(e => e.Action == "sdk.repair-completed").ToListAsync());

        // The loser's OWN new credential (still confirmed, still valid) was never touched - it is
        // simply unused, not revoked, not bound to anything. Only the shared OLD credential and the
        // shared target were ever contended.
        var losingNewCredentialId = (await _h.Db.SdkPairingSessions.AsNoTracking().SingleAsync(s => s.Id == losingSessionId)).IssuedCredentialId!.Value;
        Assert.Null((await _h.Db.ProjectApiCredentials.AsNoTracking().SingleAsync(c => c.Id == losingNewCredentialId)).RevokedAt);
    }

    /// <summary>Race A: a plain operator-driven manual revoke (ProjectCredentialService.RevokeAsync)
    /// of the OLD credential, genuinely concurrent with a repair completion that is in the middle of
    /// revoking that exact same credential as part of its own atomic completion. Both contexts
    /// pre-track the same old-credential row (same original RowVersion) before either commits, so
    /// the race is deterministic: only the actual database-level concurrency check - not the earlier
    /// in-memory RevokedAt reads either side performed - decides which one lands. Regardless of
    /// which one wins, the loser must cleanly detect the conflict (never crash, never silently
    /// re-apply its own stale values) and the credential must never end up un-revoked again.</summary>
    [Fact]
    public async Task ConcurrentManualRevokeOfTheOldCredentialWhileCompletionIsInProgressNeverSilentlyRestoresStaleState()
    {
        _h.EnsureProject();
        var oldCredential = _h.SeedCredential(_h.ProjectId, "old-hash");
        var machine = _h.SeedMachine();
        var target = _h.SeedRemediationTarget(machine.Id, oldCredential.Id, machine.HostName);

        var setupService = Service();
        var created = (await setupService.CreateAsync(_h.ProjectId, "python", default, replacesCredentialId: oldCredential.Id))!;
        var paired = (await setupService.RedeemAsync(created.Code, "python", "1.0.0", default))!;
        Assert.True(await setupService.ConfirmAsync(created.PairingId, paired.ApiKey, default));

        using var dbA = _h.CreateAdditionalDbContext();
        using var dbB = _h.CreateAdditionalDbContext();
        await dbA.ProjectApiCredentials.SingleAsync(c => c.Id == oldCredential.Id);
        await dbB.ProjectApiCredentials.SingleAsync(c => c.Id == oldCredential.Id);

        var serviceA = new SdkPairingService(dbA,
            new ProjectCredentialService(dbA, TestHarness.Opt(new PlatformSecurityOptions()), TimeProvider.System),
            TimeProvider.System, new ConfigurationBuilder().Build(),
            new PlatformAuditService(dbA, TimeProvider.System, NullLogger<PlatformAuditService>.Instance));
        var credentialsB = new ProjectCredentialService(dbB, TestHarness.Opt(new PlatformSecurityOptions()), TimeProvider.System);

        // Real concurrency: both tasks are created (and therefore started) before either is awaited.
        var taskA = serviceA.CompleteRepairAsync(created.PairingId, oldCredential.Id, default);
        var taskB = credentialsB.RevokeAsync(_h.ProjectId, oldCredential.Id, default);
        await Task.WhenAll(taskA, taskB);
        var completeResult = await taskA;
        var revokeOutcome = await taskB;

        var completionWon = completeResult.Outcome == CompleteRepairOutcome.Success;
        var revokeWon = revokeOutcome == RevokeCredentialOutcome.Revoked;
        Assert.True(completionWon ^ revokeWon,
            $"Exactly one of the two racing operations should have committed. complete={completeResult.Outcome} revoke={revokeOutcome}");

        // Regardless of who won, the old credential ends up revoked exactly once - never silently
        // restored to active by whichever side's transaction rolled back.
        Assert.NotNull((await _h.Db.ProjectApiCredentials.AsNoTracking().SingleAsync(c => c.Id == oldCredential.Id)).RevokedAt);

        if (completionWon)
        {
            Assert.Equal(RevokeCredentialOutcome.ConcurrentConflict, revokeOutcome);
            Assert.NotEqual(oldCredential.Id, (await _h.Db.RemediationTargets.AsNoTracking().SingleAsync(t => t.Id == target.Id)).TelemetryCredentialId);
            Assert.NotNull((await _h.Db.SdkPairingSessions.AsNoTracking().SingleAsync(s => s.Id == created.PairingId)).CompletedAt);
        }
        else
        {
            Assert.Equal(CompleteRepairOutcome.ConcurrentReplacementConflict, completeResult.Outcome);
            // The completion's own attempt rolled back entirely - never left half-migrated.
            Assert.Equal(oldCredential.Id, (await _h.Db.RemediationTargets.AsNoTracking().SingleAsync(t => t.Id == target.Id)).TelemetryCredentialId);
            Assert.Null((await _h.Db.SdkPairingSessions.AsNoTracking().SingleAsync(s => s.Id == created.PairingId)).CompletedAt);
        }
    }

    /// <summary>Race B: a plain operator-driven manual revoke of the NEW credential (the one this
    /// session's completion is about to bind targets onto), genuinely concurrent with that same
    /// completion. Before this fix, CompleteRepairAsync only ever READ newCredential - never wrote
    /// to it - so it never participated in any concurrency check, and a revoke landing in the gap
    /// between that read and the commit would go completely undetected: the completion would still
    /// "succeed", silently binding remediation targets onto a credential that was actually already
    /// dead. Both contexts pre-track the same new-credential row before either commits, so the race
    /// is deterministic.</summary>
    [Fact]
    public async Task ConcurrentManualRevokeOfTheNewCredentialWhileCompletionIsInProgressIsDetectedAsAConflict()
    {
        _h.EnsureProject();
        var oldCredential = _h.SeedCredential(_h.ProjectId, "old-hash");
        var machine = _h.SeedMachine();
        var target = _h.SeedRemediationTarget(machine.Id, oldCredential.Id, machine.HostName);

        var setupService = Service();
        var created = (await setupService.CreateAsync(_h.ProjectId, "python", default, replacesCredentialId: oldCredential.Id))!;
        var paired = (await setupService.RedeemAsync(created.Code, "python", "1.0.0", default))!;
        Assert.True(await setupService.ConfirmAsync(created.PairingId, paired.ApiKey, default));
        var newCredentialId = (await _h.Db.SdkPairingSessions.AsNoTracking().SingleAsync(s => s.Id == created.PairingId)).IssuedCredentialId!.Value;

        using var dbA = _h.CreateAdditionalDbContext();
        using var dbB = _h.CreateAdditionalDbContext();
        await dbA.ProjectApiCredentials.SingleAsync(c => c.Id == newCredentialId);
        await dbB.ProjectApiCredentials.SingleAsync(c => c.Id == newCredentialId);

        var serviceA = new SdkPairingService(dbA,
            new ProjectCredentialService(dbA, TestHarness.Opt(new PlatformSecurityOptions()), TimeProvider.System),
            TimeProvider.System, new ConfigurationBuilder().Build(),
            new PlatformAuditService(dbA, TimeProvider.System, NullLogger<PlatformAuditService>.Instance));
        var credentialsB = new ProjectCredentialService(dbB, TestHarness.Opt(new PlatformSecurityOptions()), TimeProvider.System);

        var taskA = serviceA.CompleteRepairAsync(created.PairingId, oldCredential.Id, default);
        var taskB = credentialsB.RevokeAsync(_h.ProjectId, newCredentialId, default);
        await Task.WhenAll(taskA, taskB);
        var completeResult = await taskA;
        var revokeOutcome = await taskB;

        var completionWon = completeResult.Outcome == CompleteRepairOutcome.Success;
        var revokeWon = revokeOutcome == RevokeCredentialOutcome.Revoked;
        Assert.True(completionWon ^ revokeWon,
            $"Exactly one of the two racing operations should have committed. complete={completeResult.Outcome} revoke={revokeOutcome}");

        if (completionWon)
        {
            // The manual revoke lost - its own commit was invalidated by completion's rotation of
            // newCredential.RowVersion, so it never actually landed. The new credential is genuinely
            // still active, and completion's own rebind/revoke of the OLD credential fully took effect.
            Assert.Equal(RevokeCredentialOutcome.ConcurrentConflict, revokeOutcome);
            Assert.Null((await _h.Db.ProjectApiCredentials.AsNoTracking().SingleAsync(c => c.Id == newCredentialId)).RevokedAt);
            Assert.Equal(newCredentialId, (await _h.Db.RemediationTargets.AsNoTracking().SingleAsync(t => t.Id == target.Id)).TelemetryCredentialId);
            Assert.NotNull((await _h.Db.ProjectApiCredentials.AsNoTracking().SingleAsync(c => c.Id == oldCredential.Id)).RevokedAt);
        }
        else
        {
            // The manual revoke of the NEW credential won: completion must NEVER silently bind/use
            // a credential that was revoked out from under it. The old (still genuinely working)
            // credential is left completely untouched, the target stays bound to it, and the
            // session is not falsely marked complete.
            Assert.Equal(CompleteRepairOutcome.ConcurrentReplacementConflict, completeResult.Outcome);
            Assert.NotNull((await _h.Db.ProjectApiCredentials.AsNoTracking().SingleAsync(c => c.Id == newCredentialId)).RevokedAt);
            Assert.Null((await _h.Db.ProjectApiCredentials.AsNoTracking().SingleAsync(c => c.Id == oldCredential.Id)).RevokedAt);
            Assert.Equal(oldCredential.Id, (await _h.Db.RemediationTargets.AsNoTracking().SingleAsync(t => t.Id == target.Id)).TelemetryCredentialId);
            Assert.Null((await _h.Db.SdkPairingSessions.AsNoTracking().SingleAsync(s => s.Id == created.PairingId)).CompletedAt);
        }
    }

    // --- Controller: Confirm/CompleteRepair authorization + outcome mapping -----------------

    [Fact]
    public void ConfirmDoesNotRequireOperatorButCompleteRepairDoes()
    {
        var type = typeof(SdkPairingController);
        Assert.Empty(type.GetMethod(nameof(SdkPairingController.Confirm))!.GetCustomAttributes(typeof(RequiresOperatorAttribute), false));
        Assert.NotEmpty(type.GetMethod(nameof(SdkPairingController.CompleteRepair))!.GetCustomAttributes(typeof(RequiresOperatorAttribute), false));
    }

    [Fact]
    public async Task ControllerConfirmReturnsBadRequestOnFailureAndNoContentOnSuccess()
    {
        _h.EnsureProject();
        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;
        var paired = (await service.RedeemAsync(created.Code, "python", "1.0.0", default))!;
        var controller = new SdkPairingController(service)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        Assert.IsType<BadRequestObjectResult>(await controller.Confirm(created.PairingId, new ConfirmPairingRequest { ApiKey = "wrong" }, default));
        Assert.IsType<NoContentResult>(await controller.Confirm(created.PairingId, new ConfirmPairingRequest { ApiKey = paired.ApiKey }, default));
    }

    [Fact]
    public async Task ControllerCompleteRepairMapsOutcomesToTheRightStatusCodesAndAuditsWithoutTheSecret()
    {
        _h.EnsureProject();
        var oldCredential = _h.SeedCredential(_h.ProjectId, "old-hash");
        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default, replacesCredentialId: oldCredential.Id))!;
        var paired = (await service.RedeemAsync(created.Code, "python", "1.0.0", default))!;
        var controller = new SdkPairingController(service)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        Assert.IsType<ConflictObjectResult>(
            await controller.CompleteRepair(created.PairingId, new CompleteRepairRequest { OldCredentialId = oldCredential.Id }, default));

        await controller.Confirm(created.PairingId, new ConfirmPairingRequest { ApiKey = paired.ApiKey }, default);
        Assert.IsType<OkObjectResult>(
            await controller.CompleteRepair(created.PairingId, new CompleteRepairRequest { OldCredentialId = oldCredential.Id }, default));

        var audit = _h.Db.PlatformAuditEvents.Single(e => e.Action == "sdk.repair-completed");
        Assert.DoesNotContain(paired.ApiKey, audit.DataJson);
    }

    [Fact]
    public async Task ANonSuccessfulCompleteRepairAttemptNeverRecordsACompletionAuditEvent()
    {
        // Regression guard for the audit/transaction-consistency fix: the audit call for
        // "sdk.repair-completed" lives on the SAME code path as the state mutation it describes
        // (inside CompleteRepairAsync, before its own SaveChangesAsync/CommitAsync) - not in the
        // controller, as a separate save after the fact. Proving a failed attempt records nothing
        // is the other half of proving they can never drift apart: either both land, or neither does.
        _h.EnsureProject();
        var oldCredential = _h.SeedCredential(_h.ProjectId, "old-hash");
        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default, replacesCredentialId: oldCredential.Id))!;
        await service.RedeemAsync(created.Code, "python", "1.0.0", default); // never confirmed

        var result = await service.CompleteRepairAsync(created.PairingId, oldCredential.Id, default);

        Assert.Equal(CompleteRepairOutcome.NotConfirmed, result.Outcome);
        Assert.DoesNotContain(_h.Db.PlatformAuditEvents, e => e.Action == "sdk.repair-completed");
    }

    public void Dispose() => _h.Dispose();
}
