using System.Security.Cryptography;
using System.Text;
using Kairon.Backend.Configuration;
using Kairon.Backend.Controllers;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Services;
using Kairon.Backend.Services.Audit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// Automatic SDK signals are configured per paired app in the desktop and read by the SDK with its
/// own key - so the application needs no code or configuration for them.
/// </summary>
public sealed class SdkSettingsTests : IDisposable
{
    private const string SdkKey = "krn_settings_test_key_1234567890";
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private ProjectApiCredential SeedPairedApp(string key = SdkKey)
    {
        var credential = _h.SeedCredential(_h.ProjectId, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
        credential.KeyPrefix = key[..12];
        _h.Db.SaveChanges();
        return credential;
    }

    private SdkSettingsService Service() =>
        new(_h.Db, new PlatformAuditService(_h.Db, TimeProvider.System, NullLogger<PlatformAuditService>.Instance));

    private SdkSettingsController Controller(string? key = null)
    {
        var controller = new SdkSettingsController(Service(), Options.Create(new PlatformSecurityOptions()))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        if (key is not null) controller.Request.Headers["X-Kairon-API-Key"] = key;
        return controller;
    }

    [Fact]
    public async Task AFreshlyPairedAppHasEverythingOnByDefault()
    {
        SeedPairedApp();

        var result = Assert.IsType<OkObjectResult>(await Controller(SdkKey).ForSdk(new SdkSettingsRequest { ProjectId = _h.ProjectId }, default));

        Assert.Equal(new SdkAutoSignalSettings(true, true, 10), result.Value);
    }

    [Fact]
    public async Task TheSdkSeesWhatTheOperatorSetForThatAppOnly()
    {
        var app = SeedPairedApp();
        SeedPairedApp("krn_other_app_key_0987654321");

        Assert.Equal(SdkSettingsUpdateOutcome.Updated,
            await Service().UpdateAsync(_h.ProjectId, app.Id, new SdkAutoSignalSettings(false, true, 30), "operator-test", default));

        var mine = Assert.IsType<OkObjectResult>(await Controller(SdkKey).ForSdk(new SdkSettingsRequest { ProjectId = _h.ProjectId }, default));
        var other = Assert.IsType<OkObjectResult>(await Controller("krn_other_app_key_0987654321").ForSdk(new SdkSettingsRequest { ProjectId = _h.ProjectId }, default));
        Assert.Equal(new SdkAutoSignalSettings(false, true, 30), mine.Value);
        Assert.Equal(new SdkAutoSignalSettings(true, true, 10), other.Value);
        Assert.Contains(_h.Db.PlatformAuditEvents, e => e.Action == "credential.sdk-settings.updated");
    }

    [Fact]
    public async Task AWrongRevokedOrMissingKeyGetsNothing()
    {
        var app = SeedPairedApp();

        Assert.IsType<UnauthorizedObjectResult>(await Controller().ForSdk(new SdkSettingsRequest { ProjectId = _h.ProjectId }, default));
        Assert.IsType<UnauthorizedObjectResult>(await Controller("krn_settings_wrong_key_000000").ForSdk(new SdkSettingsRequest { ProjectId = _h.ProjectId }, default));
        Assert.IsType<UnauthorizedObjectResult>(await Controller(SdkKey).ForSdk(new SdkSettingsRequest { ProjectId = Guid.NewGuid() }, default));

        app.RevokedAt = DateTime.UtcNow;
        _h.Db.SaveChanges();
        Assert.IsType<UnauthorizedObjectResult>(await Controller(SdkKey).ForSdk(new SdkSettingsRequest { ProjectId = _h.ProjectId }, default));
        Assert.Equal(SdkSettingsUpdateOutcome.NotFound,
            await Service().UpdateAsync(_h.ProjectId, app.Id, new SdkAutoSignalSettings(true, true, 10), "operator-test", default));
    }

    [Fact]
    public async Task TheRetryWindowIsBounded()
    {
        var app = SeedPairedApp();

        Assert.Equal(SdkSettingsUpdateOutcome.Invalid,
            await Service().UpdateAsync(_h.ProjectId, app.Id, new SdkAutoSignalSettings(true, true, 0), "operator-test", default));
        Assert.Equal(SdkSettingsUpdateOutcome.Invalid,
            await Service().UpdateAsync(_h.ProjectId, app.Id, new SdkAutoSignalSettings(true, true, 301), "operator-test", default));
        Assert.Equal(10, (await _h.Db.ProjectApiCredentials.AsNoTracking().SingleAsync()).RetryWindowSeconds);
    }

    [Fact]
    public void OnlyTheOperatorCanChangeSettings()
    {
        foreach (var name in new[] { nameof(SdkSettingsController.Get), nameof(SdkSettingsController.Update) })
            Assert.NotNull(typeof(SdkSettingsController).GetMethod(name)!
                .GetCustomAttributes(typeof(Kairon.Backend.Infrastructure.RequiresOperatorAttribute), inherit: true).SingleOrDefault());
        Assert.Empty(typeof(SdkSettingsController).GetMethod(nameof(SdkSettingsController.ForSdk))!
            .GetCustomAttributes(typeof(Kairon.Backend.Infrastructure.RequiresOperatorAttribute), inherit: true));
    }
}
