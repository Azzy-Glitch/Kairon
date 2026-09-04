using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// Correlation (PRD sections 8 and 18). The requirement: five related signals become one incident,
/// not five duplicates.
/// </summary>
public class CorrelationTests : IDisposable
{
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private DetectionSignal Signal(
        string ruleId,
        string metric,
        IncidentSeverity severity = IncidentSeverity.High,
        string? service = null,
        DateTime? at = null,
        params Guid[] telemetry) => new()
    {
        RuleId = ruleId,
        MetricName = metric,
        ProjectId = _h.ProjectId,
        Application = "Kairon.DemoApp",
        Service = service ?? _h.Service,
        Component = service ?? _h.Service,
        Environment = _h.Environment,
        Severity = severity,
        Symptom = $"{metric} breached",
        Observed = 100,
        Threshold = 50,
        Unit = "%",
        DetectedAt = at ?? DateTime.UtcNow,
        TelemetryReferences = telemetry.ToList()
    };

    [Fact]
    public async Task FiveRelatedSignalsProduceOneIncident()
    {
        var signals = new List<DetectionSignal>
        {
            Signal("cpu-threshold", "cpu"),
            Signal("latency-threshold", "latency"),
            Signal("error-rate-threshold", "errorRate"),
            Signal("retry-storm", "retries"),
            Signal("queue-backlog", "queue")
        };

        var incidents = await _h.CreateCorrelationEngine().CorrelateAsync(signals);

        Assert.Single(incidents);
        Assert.Equal(1, await _h.Db.SreIncidents.CountAsync());
        Assert.Equal(5, incidents[0].SignalCount);
    }

    [Fact]
    public async Task ThreeOrMoreCorrelatedMetricsBecomeAServiceDegradation()
    {
        var signals = new List<DetectionSignal>
        {
            Signal("cpu-threshold", "cpu"),
            Signal("latency-threshold", "latency"),
            Signal("error-rate-threshold", "errorRate")
        };

        var incidents = await _h.CreateCorrelationEngine().CorrelateAsync(signals);

        Assert.Equal($"{_h.Service} Service Degradation", incidents[0].Title);
    }

    [Fact]
    public async Task ASingleSignalGetsASpecificTitle()
    {
        var incidents = await _h.CreateCorrelationEngine()
            .CorrelateAsync(new List<DetectionSignal> { Signal("retry-storm", "retries") });

        Assert.Equal($"{_h.Service} Retry Storm", incidents[0].Title);
    }

    [Fact]
    public async Task ADeviationIsLabeledAsAnAnomalyNotAThresholdBreach()
    {
        var incidents = await _h.CreateCorrelationEngine()
            .CorrelateAsync(new List<DetectionSignal> { Signal("metric-deviation:cpu", "cpu") });

        Assert.Equal($"{_h.Service} CPU Anomaly", incidents[0].Title);
    }

    [Fact]
    public async Task ALogPatternOnlySignalGetsAMeaningfulTitleNotAnomaly()
    {
        var incidents = await _h.CreateCorrelationEngine()
            .CorrelateAsync(new List<DetectionSignal> { Signal("log-pattern-match", "logPattern") });

        Assert.Equal($"{_h.Service} Log Pattern Match", incidents[0].Title);
    }

    [Fact]
    public async Task AProcessCrashOnlySignalGetsAMeaningfulTitleNotAnomaly()
    {
        var incidents = await _h.CreateCorrelationEngine()
            .CorrelateAsync(new List<DetectionSignal> { Signal("process-crash", "processCrash") });

        Assert.Equal($"{_h.Service} Process Crash", incidents[0].Title);
    }

    [Fact]
    public async Task ALogPatternAndAProcessCrashCorrelateIntoOneIncident()
    {
        // Two Agent-sourced signals for the same service, same correlation window - proving
        // Agent signals fold together exactly the way metric/HTTP signals already do.
        var signals = new List<DetectionSignal>
        {
            Signal("log-pattern-match", "logPattern"),
            Signal("process-crash", "processCrash")
        };

        var incidents = await _h.CreateCorrelationEngine().CorrelateAsync(signals);

        Assert.Single(incidents);
        Assert.Equal(2, incidents[0].SignalCount);
    }

    [Fact]
    public async Task DifferentServicesProduceSeparateIncidents()
    {
        var signals = new List<DetectionSignal>
        {
            Signal("cpu-threshold", "cpu", service: "OrderProcessingService"),
            Signal("cpu-threshold", "cpu", service: "InventoryService")
        };

        var incidents = await _h.CreateCorrelationEngine().CorrelateAsync(signals);

        Assert.Equal(2, incidents.Count);
    }

    [Fact]
    public async Task NewSignalsFoldIntoAnExistingOpenIncident()
    {
        var engine = _h.CreateCorrelationEngine();

        await engine.CorrelateAsync(new List<DetectionSignal> { Signal("cpu-threshold", "cpu") });
        await engine.CorrelateAsync(new List<DetectionSignal> { Signal("latency-threshold", "latency") });

        Assert.Equal(1, await _h.Db.SreIncidents.CountAsync());

        var incident = await _h.Db.SreIncidents.FirstAsync();
        Assert.Equal(2, incident.SignalCount);

        var symptoms = SreJson.Deserialize(incident.SymptomsJson, new List<string>());
        Assert.Equal(2, symptoms.Count);
    }

    [Fact]
    public async Task AnIdenticalRepeatedSignalDoesNotBloatTheIncident()
    {
        var engine = _h.CreateCorrelationEngine();
        var signal = Signal("cpu-threshold", "cpu");

        await engine.CorrelateAsync(new List<DetectionSignal> { signal });
        await engine.CorrelateAsync(new List<DetectionSignal> { Signal("cpu-threshold", "cpu") });

        var incident = await _h.Db.SreIncidents.FirstAsync();
        Assert.Equal(1, incident.SignalCount);
    }

    [Fact]
    public async Task SeverityEscalatesWhenAWorseSignalArrives()
    {
        var engine = _h.CreateCorrelationEngine();

        await engine.CorrelateAsync(new List<DetectionSignal> { Signal("cpu-threshold", "cpu", IncidentSeverity.Medium) });
        await engine.CorrelateAsync(new List<DetectionSignal> { Signal("retry-storm", "retries", IncidentSeverity.Critical) });

        var incident = await _h.Db.SreIncidents.FirstAsync();
        Assert.Equal(IncidentSeverity.Critical, incident.Severity);
    }

    [Fact]
    public async Task SeverityIsNeverDowngradedByALaterMinorSignal()
    {
        var engine = _h.CreateCorrelationEngine();

        await engine.CorrelateAsync(new List<DetectionSignal> { Signal("retry-storm", "retries", IncidentSeverity.Critical) });
        await engine.CorrelateAsync(new List<DetectionSignal> { Signal("cpu-threshold", "cpu", IncidentSeverity.Low) });

        var incident = await _h.Db.SreIncidents.FirstAsync();
        Assert.Equal(IncidentSeverity.Critical, incident.Severity);
    }

    [Fact]
    public async Task AResolvedIncidentDoesNotAbsorbNewSignals()
    {
        var engine = _h.CreateCorrelationEngine();

        var first = await engine.CorrelateAsync(new List<DetectionSignal> { Signal("cpu-threshold", "cpu") });
        first[0].Status = IncidentStatus.Resolved;
        await _h.Db.SaveChangesAsync();

        await engine.CorrelateAsync(new List<DetectionSignal> { Signal("latency-threshold", "latency") });

        Assert.Equal(2, await _h.Db.SreIncidents.CountAsync());
    }

    [Fact]
    public async Task SignalsOutsideTheCorrelationWindowStartANewIncident()
    {
        _h.Detection.CorrelationWindowSeconds = 60;
        var engine = _h.CreateCorrelationEngine();

        var created = await engine.CorrelateAsync(new List<DetectionSignal> { Signal("cpu-threshold", "cpu") });

        // Age the incident past the window.
        created[0].UpdatedAt = DateTime.UtcNow.AddMinutes(-10);
        await _h.Db.SaveChangesAsync();

        await engine.CorrelateAsync(new List<DetectionSignal> { Signal("latency-threshold", "latency") });

        Assert.Equal(2, await _h.Db.SreIncidents.CountAsync());
    }

    [Fact]
    public async Task SupportingTelemetryIsLinkedToTheIncident()
    {
        var row = _h.SeedTelemetry(DateTime.UtcNow);

        var incidents = await _h.CreateCorrelationEngine()
            .CorrelateAsync(new List<DetectionSignal> { Signal("repeated-errors", "errors", telemetry: row.Id) });

        var linked = await _h.Db.Incidents.FirstAsync(i => i.Id == row.Id);

        Assert.Equal(incidents[0].Id, linked.SreIncidentId);

        var references = SreJson.Deserialize(incidents[0].TelemetryReferencesJson, new List<Guid>());
        Assert.Contains(row.Id, references);
    }

    [Fact]
    public async Task CreatingAnIncidentWritesADetectedAuditEvent()
    {
        await _h.CreateCorrelationEngine()
            .CorrelateAsync(new List<DetectionSignal> { Signal("cpu-threshold", "cpu") });

        var events = await _h.Db.IncidentEvents.ToListAsync();

        Assert.Contains(events, e => e.EventType == IncidentEventTypes.Detected);
        Assert.Contains(events, e => e.Actor == "detection-engine");
    }

    [Fact]
    public async Task FoldingSignalsWritesACorrelatedAuditEvent()
    {
        var engine = _h.CreateCorrelationEngine();

        await engine.CorrelateAsync(new List<DetectionSignal> { Signal("cpu-threshold", "cpu") });
        await engine.CorrelateAsync(new List<DetectionSignal> { Signal("retry-storm", "retries") });

        Assert.Contains(await _h.Db.IncidentEvents.ToListAsync(),
            e => e.EventType == IncidentEventTypes.Correlated);
    }

    [Fact]
    public async Task IncidentKeysAreSequentialAndUnique()
    {
        var engine = _h.CreateCorrelationEngine();

        await engine.CorrelateAsync(new List<DetectionSignal> { Signal("cpu-threshold", "cpu", service: "A") });
        await engine.CorrelateAsync(new List<DetectionSignal> { Signal("cpu-threshold", "cpu", service: "B") });

        var keys = await _h.Db.SreIncidents.Select(i => i.IncidentKey).ToListAsync();

        Assert.Equal(new[] { "INC-0001", "INC-0002" }, keys.OrderBy(k => k).ToArray());
    }

    [Fact]
    public async Task NoSignalsProduceNoIncidents()
    {
        var incidents = await _h.CreateCorrelationEngine().CorrelateAsync(Array.Empty<DetectionSignal>());

        Assert.Empty(incidents);
        Assert.Equal(0, await _h.Db.SreIncidents.CountAsync());
    }

    [Fact]
    public async Task SignalsCannotRecreateAnIncidentAfterTheirProjectWasDeleted()
    {
        var engine = _h.CreateCorrelationEngine();
        _h.Db.Projects.RemoveRange(_h.Db.Projects);
        await _h.Db.SaveChangesAsync();

        var incidents = await engine.CorrelateAsync(
            new List<DetectionSignal> { Signal("cpu-threshold", "cpu") });

        Assert.Empty(incidents);
        Assert.Empty(_h.Db.SreIncidents);
    }
}
