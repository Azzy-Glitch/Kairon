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
        TimeProvider.System, new ConfigurationBuilder().Build());

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
            "cancelled" => "Cancelled",
            "expired" => "Expired",
            _ => "Pending"
        };
        switch (scenario)
        {
            case "redeemed": await service.RedeemAsync(created.Code, "python", "1.0.0", default); break;
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
        var controller = new SdkPairingController(Service(), new PlatformAuditService(_h.Db, TimeProvider.System, NullLogger<PlatformAuditService>.Instance), _h.Db)
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

    // --- Phase 5: CompleteRepairAsync - atomic rebind + revoke, gated on Confirmed -----------

    [Fact]
    public async Task CompleteRepairRefusesUntilTheNewCredentialIsConfirmed()
    {
        _h.EnsureProject();
        var service = Service();
        var oldCredential = _h.SeedCredential(_h.ProjectId, "old-hash");
        var machine = _h.SeedMachine();
        _h.SeedRemediationTarget(machine.Id, oldCredential.Id, machine.HostName);
        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;
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

        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;
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

        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;
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
        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;
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
    public async Task CompleteRepairRejectsACredentialBelongingToAnotherProjectAndLeavesItUntouched()
    {
        _h.EnsureProject();
        var otherProject = new Project { Name = "other-project" };
        _h.Db.Projects.Add(otherProject);
        _h.Db.SaveChanges();
        var otherCredential = _h.SeedCredential(otherProject.Id, "other-hash");

        var service = Service();
        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;
        var paired = (await service.RedeemAsync(created.Code, "python", "1.0.0", default))!;
        await service.ConfirmAsync(created.PairingId, paired.ApiKey, default);

        var result = await service.CompleteRepairAsync(created.PairingId, otherCredential.Id, default);

        Assert.Equal(CompleteRepairOutcome.OldCredentialWrongProject, result.Outcome);
        Assert.Null(_h.Db.ProjectApiCredentials.Single(c => c.Id == otherCredential.Id).RevokedAt);
    }

    [Fact]
    public async Task CompleteRepairIsIdempotent()
    {
        _h.EnsureProject();
        var service = Service();
        var oldCredential = _h.SeedCredential(_h.ProjectId, "old-hash");
        var machine = _h.SeedMachine();
        _h.SeedRemediationTarget(machine.Id, oldCredential.Id, machine.HostName);

        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;
        var paired = (await service.RedeemAsync(created.Code, "python", "1.0.0", default))!;
        await service.ConfirmAsync(created.PairingId, paired.ApiKey, default);

        var first = await service.CompleteRepairAsync(created.PairingId, oldCredential.Id, default);
        var second = await service.CompleteRepairAsync(created.PairingId, oldCredential.Id, default);

        Assert.Equal(CompleteRepairOutcome.Success, first.Outcome);
        Assert.Equal(1, first.RebindCount);
        Assert.Equal(CompleteRepairOutcome.Success, second.Outcome);
        Assert.Equal(0, second.RebindCount); // nothing left to rebind/revoke a second time
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
        var controller = new SdkPairingController(service, new PlatformAuditService(_h.Db, TimeProvider.System, NullLogger<PlatformAuditService>.Instance), _h.Db)
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
        var created = (await service.CreateAsync(_h.ProjectId, "python", default))!;
        var paired = (await service.RedeemAsync(created.Code, "python", "1.0.0", default))!;
        var controller = new SdkPairingController(service, new PlatformAuditService(_h.Db, TimeProvider.System, NullLogger<PlatformAuditService>.Instance), _h.Db)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        Assert.IsType<ConflictObjectResult>(
            await controller.CompleteRepair(created.PairingId, new CompleteRepairRequest { OldCredentialId = oldCredential.Id }, default));

        await controller.Confirm(created.PairingId, new ConfirmPairingRequest { ApiKey = paired.ApiKey }, default);
        Assert.IsType<OkObjectResult>(
            await controller.CompleteRepair(created.PairingId, new CompleteRepairRequest { OldCredentialId = oldCredential.Id }, default));

        var audit = _h.Db.PlatformAuditEvents.Single(e => e.Action == "sdk.repair-completed");
        Assert.DoesNotContain(paired.ApiKey, audit.DataJson);
    }

    public void Dispose() => _h.Dispose();
}
