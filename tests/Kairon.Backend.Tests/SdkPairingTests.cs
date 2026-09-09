using Kairon.Backend.Configuration;
using Kairon.Backend.Controllers;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Services;
using Kairon.Backend.Services.Audit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

    public void Dispose() => _h.Dispose();
}
