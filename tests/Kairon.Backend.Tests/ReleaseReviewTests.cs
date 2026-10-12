using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// Defects found in the final pre-release review. Each test failed before its fix.
/// </summary>
public sealed class ReleaseReviewTests : IDisposable
{
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private string Key => $"{_h.ProjectId}|{_h.Environment}|{_h.Service}";

    private SreIncident SeedCpuIncident(DateTime lastSeen)
    {
        var incident = _h.SeedIncident(IncidentStatus.RecommendationReady, correlationKey: Key);
        incident.RootCause = "CPU saturation";
        incident.CorrelatedMetricsJson = SreJson.Serialize(new List<CorrelatedSignalSnapshot>
        {
            new() { Rule = "cpu-threshold", MetricName = "cpu", Symptom = "CPU 96%", Observed = 96, Threshold = 80, Unit = "%",
                    Severity = "High", DetectedAt = lastSeen.AddMinutes(-3), LastSeenAt = lastSeen }
        });
        _h.Db.SaveChanges();
        return incident;
    }

    // --- Correlation: "still happening" must come from the data, not only from detection timing ---

    [Fact]
    public async Task PaymentErrorsRightAfterTheOverloadStoppedAreANewIncident()
    {
        // The overload was last reported 60 s ago (within the cooldown cadence), but the app's latest
        // samples show CPU back to normal - it has stopped. The payment errors that follow are a new fault.
        var overload = SeedCpuIncident(DateTime.UtcNow.AddSeconds(-60));
        for (var i = 0; i < 4; i++)
            _h.SeedMetric(DateTime.UtcNow.AddSeconds(-40 + i * 10), cpu: 12, latency: 150, requests: 10, errors: 6);

        await _h.CreateOrchestrator().DetectAndCorrelateAsync(_h.ProjectId, _h.Environment, _h.Service);

        var payment = Assert.Single(_h.Db.SreIncidents.AsNoTracking().Where(i => i.Id != overload.Id));
        Assert.Contains("errorRate", payment.CorrelatedMetricsJson);
        Assert.DoesNotContain("errorRate", (await _h.Db.SreIncidents.AsNoTracking().SingleAsync(i => i.Id == overload.Id)).CorrelatedMetricsJson);
    }

    [Fact]
    public async Task ErrorsWhileTheOverloadIsStillMeasurablyOngoingJoinIt()
    {
        var overload = SeedCpuIncident(DateTime.UtcNow.AddSeconds(-60));
        for (var i = 0; i < 4; i++)
            _h.SeedMetric(DateTime.UtcNow.AddSeconds(-40 + i * 10), cpu: 95, latency: 150, requests: 10, errors: 6);

        await _h.CreateOrchestrator().DetectAndCorrelateAsync(_h.ProjectId, _h.Environment, _h.Service);

        Assert.Equal(overload.Id, (await _h.Db.SreIncidents.AsNoTracking().SingleAsync()).Id);
    }

    // --- Evidence window: the run-up to detection must survive the sample cap ---------------------

    private SreIncident SeedErrorIncidentWithRunUp(DateTime detectedAt, int minutesOfLaterData)
    {
        // SDK cadence: one sample every 5 s. Latency rises 200 s before detection; errors 20 s before.
        for (var t = -240; t <= minutesOfLaterData * 60; t += 5)
            _h.SeedMetric(detectedAt.AddSeconds(t), cpu: 20, latency: t >= -200 ? 1800 : 150, requests: 10, errors: t >= -20 ? 6 : 0);
        var incident = _h.SeedIncident(IncidentStatus.Detected, correlationKey: Key);
        incident.Timestamp = detectedAt;
        incident.CorrelatedMetricsJson = SreJson.Serialize(new List<CorrelatedSignalSnapshot>
        {
            new() { Rule = "error-rate-threshold", MetricName = "errorRate", Observed = 60, Threshold = 10, Unit = "%" },
            new() { Rule = "latency-threshold", MetricName = "latency", Observed = 1800, Threshold = 1000, Unit = "ms" }
        });
        _h.Db.SaveChanges();
        return incident;
    }

    [Fact]
    public async Task AtDetectionTheLatencyRunUpBeforeTheErrorsIsKept()
    {
        var incident = SeedErrorIncidentWithRunUp(DateTime.UtcNow.AddSeconds(-5), minutesOfLaterData: 0);

        var evidence = await _h.CreateEvidenceCollector().CollectAsync(incident);

        var latency = Assert.Single(evidence.SignalOnsets, o => o.Metric == "latency");
        Assert.False(latency.BreachedBeforeWindow);
        Assert.InRange(latency.SecondsBeforeDetection, 190, 210);
        Assert.Equal("latency", evidence.SignalOnsets[0].Metric);
        Assert.Contains(evidence.RecentMetrics, m => m.Timestamp < incident.Timestamp.AddSeconds(-200)); // the baseline before the rise
        Assert.True(evidence.RecentMetrics.Count <= 30);
    }

    [Fact]
    public async Task AReInvestigationLaterStillSeesTheRunUpAndTheCurrentState()
    {
        // Re-investigated 20 minutes after detection: 240 newer samples exist.
        var incident = SeedErrorIncidentWithRunUp(DateTime.UtcNow.AddMinutes(-20), minutesOfLaterData: 20);

        var evidence = await _h.CreateEvidenceCollector().CollectAsync(incident);

        Assert.Equal("latency", evidence.SignalOnsets[0].Metric);
        Assert.InRange(evidence.SignalOnsets[0].SecondsBeforeDetection, 190, 210);
        Assert.Contains(evidence.RecentMetrics, m => m.Timestamp <= incident.Timestamp);                 // run-up
        Assert.Contains(evidence.RecentMetrics, m => m.Timestamp >= DateTime.UtcNow.AddMinutes(-2));      // current state
        Assert.True(evidence.RecentMetrics.Count <= 30);
        Assert.Contains(evidence.EvidenceGaps, g => g.Contains("not everything in between"));
        // The forecast describes where things are now (flat at the elevated level), not the old rise.
        var latencyTrend = Assert.Single(evidence.RiskForecast!.Trends, t => t.Metric == "latency");
        Assert.Equal("steady", latencyTrend.Direction);
        Assert.Equal(1800, latencyTrend.Recent);
    }

    [Fact]
    public async Task WhenTheLatestSamplesDoNotMeasureTheEarlierFaultTimingDecides()
    {
        // The app's latest samples carry no CPU reading (delayed or missing telemetry), so they cannot
        // say whether the overload stopped: the incident's last detection 60 s ago decides - it is
        // treated as still going on, and the errors join it as co-occurring evidence.
        var overload = SeedCpuIncident(DateTime.UtcNow.AddSeconds(-60));
        for (var i = 0; i < 4; i++)
            _h.SeedMetric(DateTime.UtcNow.AddSeconds(-40 + i * 10), cpu: null, latency: 150, requests: 10, errors: 6);

        await _h.CreateOrchestrator().DetectAndCorrelateAsync(_h.ProjectId, _h.Environment, _h.Service);

        Assert.Equal(overload.Id, (await _h.Db.SreIncidents.AsNoTracking().SingleAsync()).Id);
    }
}
