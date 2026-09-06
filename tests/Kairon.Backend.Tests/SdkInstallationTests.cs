using Kairon.Backend.DTOs;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Services;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// Installation-scoped SDK credentials (docs/DESKTOP_SHELL.md) - a stronger, optional tier above
/// project-level pairing. Issued directly against a known MonitoredApplication, authorizes a
/// normalized telemetry batch, and is revocable independently of any other installation.
/// </summary>
public sealed class SdkInstallationTests : IDisposable
{
    private readonly TestHarness _h = new();
    private SdkInstallationService Service => new(_h.Db, TimeProvider.System);

    private MonitoredApplication SeedApplication(Guid projectId)
    {
        _h.Db.Projects.Add(new Project { Id = projectId, Name = "Development", Slug = "demo" });
        var app = new MonitoredApplication
        {
            Id = Guid.NewGuid(), ProjectId = projectId, Name = "Kairon.DemoApp", Service = "OrderProcessingService"
        };
        _h.Db.MonitoredApplications.Add(app);
        _h.Db.SaveChanges();
        return app;
    }

    private static NormalizedTelemetryBatchDto BatchFor(Guid projectId, string installationId, string service) => new()
    {
        Events =
        [
            new NormalizedTelemetryEventDto
            {
                EventId = Guid.NewGuid(), ProjectId = projectId, EventType = "exception", Source = "dotnet-sdk",
                Application = "Kairon.DemoApp", Service = service, InstallationId = installationId
            }
        ]
    };

    [Fact]
    public async Task IssueCreatesARevocableCredentialAndATelemetrySource()
    {
        var app = SeedApplication(Guid.NewGuid());

        var created = await Service.IssueAsync(app.Id, "dotnet", "1.2.3", default);

        Assert.NotNull(created);
        Assert.StartsWith("ksi_", created!.Credential);
        Assert.Single(_h.Db.SdkInstallations, x => x.Id == created.Id);
        Assert.Single(_h.Db.TelemetrySources, x => x.ApplicationId == app.Id);
    }

    [Fact]
    public async Task IssueForUnknownApplicationReturnsNull()
    {
        var created = await Service.IssueAsync(Guid.NewGuid(), "dotnet", "1.0.0", default);
        Assert.Null(created);
    }

    [Fact]
    public async Task ValidCredentialAuthorizesAMatchingBatch()
    {
        var app = SeedApplication(Guid.NewGuid());
        var created = (await Service.IssueAsync(app.Id, "dotnet", "1.0.0", default))!;

        var authorized = await Service.AuthorizeBatchAsync(
            BatchFor(app.ProjectId, created.InstallationId, app.Service), created.Credential, default);

        Assert.True(authorized);
        var installation = Assert.Single(_h.Db.SdkInstallations, x => x.Id == created.Id);
        Assert.NotNull(installation.LastSeenAt);
    }

    [Fact]
    public async Task WrongCredentialIsRejected()
    {
        var app = SeedApplication(Guid.NewGuid());
        var created = (await Service.IssueAsync(app.Id, "dotnet", "1.0.0", default))!;

        var authorized = await Service.AuthorizeBatchAsync(
            BatchFor(app.ProjectId, created.InstallationId, app.Service), "ksi_" + new string('x', 40), default);

        Assert.False(authorized);
    }

    [Fact]
    public async Task RevokedCredentialIsRejected()
    {
        var app = SeedApplication(Guid.NewGuid());
        var created = (await Service.IssueAsync(app.Id, "dotnet", "1.0.0", default))!;

        Assert.True(await Service.RevokeAsync(created.Id, default));

        var authorized = await Service.AuthorizeBatchAsync(
            BatchFor(app.ProjectId, created.InstallationId, app.Service), created.Credential, default);

        Assert.False(authorized);
        var source = Assert.Single(_h.Db.TelemetrySources, x => x.ApplicationId == app.Id);
        Assert.False(source.IsActive);
    }

    [Fact]
    public async Task DoubleRevokeIsNotAllowed()
    {
        var app = SeedApplication(Guid.NewGuid());
        var created = (await Service.IssueAsync(app.Id, "dotnet", "1.0.0", default))!;

        Assert.True(await Service.RevokeAsync(created.Id, default));
        Assert.False(await Service.RevokeAsync(created.Id, default));
    }

    [Fact]
    public async Task BatchForADifferentServiceThanTheInstallationIsRejected()
    {
        var app = SeedApplication(Guid.NewGuid());
        var created = (await Service.IssueAsync(app.Id, "dotnet", "1.0.0", default))!;

        var authorized = await Service.AuthorizeBatchAsync(
            BatchFor(app.ProjectId, created.InstallationId, "SomeOtherService"), created.Credential, default);

        Assert.False(authorized);
    }

    public void Dispose() => _h.Dispose();
}
