using Kairon.Backend.DTOs;
using Kairon.Backend.Services;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// Normalized telemetry ingestion (docs/DESKTOP_SHELL.md): idempotent by EventId, auto-creates
/// the Project/Application/Environment/Source hierarchy, redacts before persisting, and produces
/// compatibility Incident/Metric rows the existing detection engine already reads.
/// </summary>
public sealed class PlatformTelemetryTests : IDisposable
{
    private readonly TestHarness _h = new();
    private PlatformTelemetryService Service => new(_h.Db, _h.Queue, TimeProvider.System);

    private static NormalizedTelemetryEventDto Event(Guid eventId, Guid projectId, string eventType = "exception",
        string? message = null) => new()
    {
        EventId = eventId, ProjectId = projectId, Timestamp = DateTime.UtcNow, EventType = eventType,
        Severity = "Error", Source = "dotnet-sdk", Application = "Kairon.DemoApp", Service = "OrderProcessingService",
        Environment = "Demo", InstallationId = "install-1", SourceVersion = "1.0.0",
        Message = message ?? "boom", ExceptionType = "System.Exception"
    };

    [Fact]
    public async Task FirstIngestAcceptsAndCreatesHierarchy()
    {
        var projectId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        var result = await Service.IngestAsync(new NormalizedTelemetryBatchDto { Events = [Event(eventId, projectId)] }, default);

        Assert.Equal(1, result.Accepted);
        Assert.Equal(0, result.Duplicates);
        Assert.Equal(0, result.Rejected);
        Assert.Contains(eventId, result.AcceptedEventIds);

        Assert.Single(_h.Db.Projects, p => p.Id == projectId);
        Assert.Single(_h.Db.MonitoredApplications, a => a.ProjectId == projectId && a.Service == "OrderProcessingService");
        Assert.Single(_h.Db.Environments, e => e.ProjectId == projectId && e.Name == "Demo");
        Assert.Single(_h.Db.TelemetrySources, s => s.ProjectId == projectId && s.InstallationId == "install-1");
        Assert.Single(_h.Db.TelemetryReceipts, r => r.EventId == eventId);
    }

    [Fact]
    public async Task ResendOfSameEventIdIsReportedAsDuplicateNotStoredTwice()
    {
        var projectId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var batch = new NormalizedTelemetryBatchDto { Events = [Event(eventId, projectId)] };

        await Service.IngestAsync(batch, default);
        var second = await Service.IngestAsync(batch, default);

        Assert.Equal(0, second.Accepted);
        Assert.Equal(1, second.Duplicates);
        Assert.Single(_h.Db.TelemetryReceipts, r => r.EventId == eventId);
    }

    [Fact]
    public async Task DuplicateWithinTheSameBatchIsCountedOnceNotTwice()
    {
        var projectId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var batch = new NormalizedTelemetryBatchDto { Events = [Event(eventId, projectId), Event(eventId, projectId)] };

        var result = await Service.IngestAsync(batch, default);

        Assert.Equal(1, result.Accepted);
        Assert.Single(_h.Db.TelemetryReceipts, r => r.EventId == eventId);
    }

    [Fact]
    public async Task MessageIsRedactedBeforePersistence()
    {
        var projectId = Guid.NewGuid();
        var secret = "password=hunter2 leaked in the message";

        await Service.IngestAsync(new NormalizedTelemetryBatchDto
        {
            Events = [Event(Guid.NewGuid(), projectId, message: secret)]
        }, default);

        var receipt = Assert.Single(_h.Db.TelemetryReceipts);
        Assert.DoesNotContain("hunter2", receipt.PayloadJson);

        var incident = Assert.Single(_h.Db.Incidents, i => i.ProjectId == projectId);
        Assert.DoesNotContain("hunter2", incident.ErrorMessage ?? string.Empty);
    }

    [Fact]
    public async Task MetricEventProducesACompatibilityMetricRow()
    {
        var projectId = Guid.NewGuid();
        var metricEvent = Event(Guid.NewGuid(), projectId, eventType: "metric");
        metricEvent.ResourceMetrics = new ResourceTelemetryMetricsDto { CpuPercent = 91, MemoryPercent = 70, RequestCount = 10 };

        await Service.IngestAsync(new NormalizedTelemetryBatchDto { Events = [metricEvent] }, default);

        var metric = Assert.Single(_h.Db.Metrics, m => m.ProjectId == projectId);
        Assert.Equal(91, metric.CpuPercent);
    }

    [Fact]
    public async Task InvalidEventsAreRejectedNotSilentlyAccepted()
    {
        var invalid = new NormalizedTelemetryEventDto { EventId = Guid.NewGuid(), ProjectId = Guid.Empty };

        var result = await Service.IngestAsync(new NormalizedTelemetryBatchDto { Events = [invalid] }, default);

        Assert.Equal(0, result.Accepted);
        Assert.Equal(1, result.Rejected);
    }

    public void Dispose() => _h.Dispose();
}
