using AIDIP.Backend.DTOs;
using AIDIP.Backend.Models;
using AIDIP.Backend.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AIDIP.Backend.Tests;

public sealed class SdkPairingTests : IDisposable
{
    private readonly TestHarness _h = new();
    private readonly MutableTimeProvider _time = new(DateTimeOffset.Parse("2026-08-28T00:00:00Z"));
    private readonly Guid _applicationId = Guid.NewGuid();

    public SdkPairingTests()
    {
        _h.Db.Projects.Add(new KaironProject { Id = _h.ProjectId, Name = "Orders", Slug = "orders",
            CreatedAt = _time.GetUtcNow().UtcDateTime });
        _h.Db.MonitoredApplications.Add(new MonitoredApplication { Id = _applicationId,
            ProjectId = _h.ProjectId, Name = "Orders API", Service = "orders-api", Runtime = ".NET",
            CreatedAt = _time.GetUtcNow().UtcDateTime });
        _h.Db.SaveChanges();
    }

    [Fact]
    public async Task PairingCodeIsTemporaryHashedScopedAndSingleUse()
    {
        var service = Service();
        var pairing = await service.CreateAsync(_applicationId, "dotnet", default);
        Assert.NotNull(pairing);
        Assert.DoesNotContain(pairing.Code, _h.Db.SdkPairingSessions.Single().CodeHash);

        Assert.Null(await service.RedeemAsync(pairing.Code, "python", "1.0", default));
        var paired = await service.RedeemAsync(pairing.Code, "dotnet", "1.0", default);

        Assert.NotNull(paired);
        Assert.Equal(_applicationId, paired.ApplicationId);
        Assert.DoesNotContain(paired.Credential, _h.Db.SdkInstallations.Single().KeyHash);
        Assert.Null(await service.RedeemAsync(pairing.Code, "dotnet", "1.0", default));
    }

    [Fact]
    public async Task ExpiredPairingCannotBeRedeemed()
    {
        var service = Service();
        var pairing = await service.CreateAsync(_applicationId, "python", default);
        _time.Advance(TimeSpan.FromMinutes(11));

        Assert.Null(await service.RedeemAsync(pairing!.Code, "python", "1.0", default));
        Assert.Empty(_h.Db.SdkInstallations);
    }

    [Fact]
    public async Task RevokedPairingCannotBeRedeemed()
    {
        var service = Service();
        var pairing = await service.CreateAsync(_applicationId, "dotnet", default);
        Assert.True(await service.RevokePairingAsync(pairing!.PairingId, default));

        Assert.Null(await service.RedeemAsync(pairing.Code, "dotnet", "1.0", default));
        Assert.False(await service.RevokePairingAsync(pairing.PairingId, default));
    }

    [Fact]
    public async Task InstallationCredentialAuthorizesOnlyItsApplicationAndCanBeRevoked()
    {
        var service = Service();
        var pairing = await service.CreateAsync(_applicationId, "python", default);
        var paired = await service.RedeemAsync(pairing!.Code, "python", "1.0", default);
        var batch = Batch(paired!.InstallationId, "orders-api");

        Assert.True(await service.AuthorizeTelemetryAsync(batch, paired.Credential, default));
        Assert.True(await service.AuthorizeLegacyAsync(_h.ProjectId, "orders-api", paired.Credential, default));
        Assert.False(await service.AuthorizeLegacyAsync(_h.ProjectId, "other-app", paired.Credential, default));
        Assert.False(await service.AuthorizeTelemetryAsync(Batch(paired.InstallationId, "other-app"),
            paired.Credential, default));
        Assert.True(await service.RevokeAsync(paired.InstallationRecordId, default));
        Assert.False(await service.AuthorizeTelemetryAsync(batch, paired.Credential, default));
        Assert.False(_h.Db.TelemetrySources.Single().IsActive);
    }

    private SdkPairingService Service() => new(_h.Db, _time,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Product:BackendUrl"] = "http://127.0.0.1:8000" }).Build());

    private NormalizedTelemetryBatchDto Batch(string installationId, string service) => new() { Events = [new()
    {
        EventId = Guid.NewGuid(), ProjectId = _h.ProjectId, EventType = "log", Source = "python-sdk",
        Application = "Orders API", Service = service, Environment = "Development", InstallationId = installationId
    }] };

    public void Dispose() => _h.Dispose();
}

internal sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan duration) => _now += duration;
}
