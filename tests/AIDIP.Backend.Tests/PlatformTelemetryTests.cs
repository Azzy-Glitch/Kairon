using AIDIP.Backend.DTOs;
using AIDIP.Backend.Services;
using AIDIP.Backend.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AIDIP.Backend.Tests;

public sealed class PlatformTelemetryTests : IDisposable
{
    private readonly TestHarness _h = new();

    [Fact]
    public async Task NormalizedBatchCreatesPlatformIdentityAndFeedsExistingSignalPipeline()
    {
        var service = new PlatformTelemetryService(_h.Db, _h.Queue, TimeProvider.System);
        var metricId = Guid.NewGuid();
        var exceptionId = Guid.NewGuid();
        var batch = new NormalizedTelemetryBatchDto { Events =
        [
            Event(metricId, "resource_metric", resource: new ResourceTelemetryMetricsDto
                { CpuPercent = 91, MemoryPercent = 72, ResponseTimeMs = 1250, RequestCount = 10, ErrorCount = 2 }),
            Event(exceptionId, "exception", message: "Authorization: Bearer secret-token-value")
        ]};

        var result = await service.IngestAsync(batch, default);

        Assert.Equal(2, result.Accepted);
        Assert.Equal(0, result.Duplicates);
        Assert.Single(_h.Db.Projects);
        Assert.Single(_h.Db.MonitoredApplications);
        Assert.Single(_h.Db.Environments);
        Assert.Single(_h.Db.TelemetrySources);
        Assert.Equal(2, _h.Db.TelemetryReceipts.Count());
        Assert.Single(_h.Db.Metrics);
        Assert.Equal(1250, _h.Db.Metrics.Single().ResponseTimeMs);
        Assert.Single(_h.Db.Incidents);
        Assert.Equal(1, _h.Queue.Count);
        Assert.DoesNotContain("secret-token-value", _h.Db.TelemetryReceipts.Single(x => x.EventId == exceptionId).PayloadJson);
        Assert.DoesNotContain("secret-token-value", _h.Db.Incidents.Single().ErrorMessage);
    }

    [Fact]
    public async Task ReusedEventIdIsIdempotentAndDoesNotDuplicateLegacySignals()
    {
        var service = new PlatformTelemetryService(_h.Db, _h.Queue, TimeProvider.System);
        var id = Guid.NewGuid();
        var batch = new NormalizedTelemetryBatchDto { Events = [Event(id, "exception", message: "failed")] };

        Assert.Equal(1, (await service.IngestAsync(batch, default)).Accepted);
        var replay = await service.IngestAsync(batch, default);

        Assert.Equal(0, replay.Accepted);
        Assert.Equal(1, replay.Duplicates);
        Assert.Single(_h.Db.TelemetryReceipts);
        Assert.Single(_h.Db.Incidents);
    }

    [Fact]
    public async Task InvalidEventsAreRejectedWithoutPartialRows()
    {
        var service = new PlatformTelemetryService(_h.Db, _h.Queue, TimeProvider.System);
        var result = await service.IngestAsync(new NormalizedTelemetryBatchDto
            { Events = [new NormalizedTelemetryEventDto { EventId = Guid.NewGuid(), EventType = "log" }] }, default);

        Assert.Equal(1, result.Rejected);
        Assert.Empty(_h.Db.TelemetryReceipts);
        Assert.Empty(_h.Db.Projects);
    }

    [Fact]
    public async Task ProjectCredentialIsReturnedOnceScopedAndRevocable()
    {
        _h.Db.Projects.Add(new AIDIP.Backend.Models.KaironProject { Id = _h.ProjectId,
            Name = "Orders", Slug = "orders", CreatedAt = DateTime.UtcNow });
        await _h.Db.SaveChangesAsync();
        var credentials = new ProjectCredentialService(_h.Db,
            Options.Create(new PlatformSecurityOptions { RequireTelemetryKey = true }), TimeProvider.System);

        var created = await credentials.CreateAsync(_h.ProjectId, "SDK", default);

        Assert.NotNull(created);
        Assert.StartsWith("krn_", created.ApiKey);
        Assert.DoesNotContain(created.ApiKey, _h.Db.ProjectApiCredentials.Single().KeyHash);
        Assert.True(await credentials.AuthorizeAsync([_h.ProjectId], created.ApiKey, default));
        Assert.False(await credentials.AuthorizeAsync([Guid.NewGuid()], created.ApiKey, default));
        Assert.True(await credentials.RevokeAsync(_h.ProjectId, created.Id, default));
        Assert.False(await credentials.AuthorizeAsync([_h.ProjectId], created.ApiKey, default));
    }

    private NormalizedTelemetryEventDto Event(Guid id, string type, string? message = null,
        ResourceTelemetryMetricsDto? resource = null) => new()
    {
        EventId = id, ProjectId = _h.ProjectId, EventType = type, Severity = "Error", Source = "dotnet-sdk",
        Application = "orders", Service = "orders-api", Environment = "Test", Runtime = ".NET",
        InstallationId = "sdk-installation-1", Message = message, ResourceMetrics = resource,
        HttpContext = type == "exception" ? new HttpTelemetryContextDto { Endpoint = "/orders", StatusCode = 500 } : null
    };

    public void Dispose() => _h.Dispose();
}
