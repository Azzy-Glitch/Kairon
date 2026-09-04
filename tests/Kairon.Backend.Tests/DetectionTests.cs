using Kairon.Backend.Models;
using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services.Detection;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// Deterministic detection (PRD sections 7 and 18): CPU, memory, latency, error rate, repeated
/// errors, retry storms, request bursts, sudden deviation - plus dedup and cooldown.
/// </summary>
public class DetectionTests : IDisposable
{
    private readonly TestHarness _h = new();
    private readonly DateTime _now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _h.Dispose();

    private List<Metric> Samples(int count, Func<int, Metric> factory) =>
        Enumerable.Range(0, count).Select(factory).ToList();

    private Metric Sample(int index, double? cpu = null, double? memory = null, double? latency = null,
        long requests = 0, long errors = 0, long? retries = null, long? queue = null) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = _h.ProjectId,
        // Ten seconds apart, so four samples comfortably exceed the 5s sustained-breach window.
        Timestamp = _now.AddSeconds(-(10 * (3 - index))),
        CpuPercent = cpu,
        MemoryPercent = memory,
        ResponseTimeMs = latency,
        RequestCount = requests,
        ErrorCount = errors,
        RetryCount = retries,
        QueueDepth = queue,
        Environment = _h.Environment,
        Service = _h.Service
    };

    // --- CPU ---

    [Fact]
    public void CpuRuleFiresOnASustainedBreach()
    {
        var metrics = Samples(4, i => Sample(i, cpu: 94));
        var signal = new CpuThresholdRule().Evaluate(_h.Context(_now, metrics));

        Assert.NotNull(signal);
        Assert.Equal("cpu", signal!.MetricName);
        Assert.Equal(94, signal.Observed);
        Assert.Equal(80, signal.Threshold);
        Assert.Contains("CPU", signal.Symptom);
    }

    [Fact]
    public void CpuRuleStaysQuietBelowThreshold()
    {
        var metrics = Samples(4, i => Sample(i, cpu: 45));
        Assert.Null(new CpuThresholdRule().Evaluate(_h.Context(_now, metrics)));
    }

    [Fact]
    public void ASingleSpikeIsNotAnIncident()
    {
        // One breaching sample among healthy ones is noise, not a sustained breach.
        var metrics = new List<Metric> { Sample(0, cpu: 20), Sample(1, cpu: 22), Sample(2, cpu: 96), Sample(3, cpu: 21) };
        Assert.Null(new CpuThresholdRule().Evaluate(_h.Context(_now, metrics)));
    }

    [Theory]
    [InlineData(85, nameof(IncidentSeverity.Medium))]
    [InlineData(105, nameof(IncidentSeverity.High))]
    [InlineData(135, nameof(IncidentSeverity.Critical))]
    public void SeverityScalesWithHowBadlyTheThresholdIsBreached(double cpu, string expected)
    {
        var metrics = Samples(4, i => Sample(i, cpu: cpu));
        var signal = new CpuThresholdRule().Evaluate(_h.Context(_now, metrics));

        Assert.NotNull(signal);
        Assert.Equal(expected, signal!.Severity.ToString());
    }

    // --- Memory ---

    [Fact]
    public void MemoryRuleFiresOnASustainedBreach()
    {
        var metrics = Samples(4, i => Sample(i, memory: 91));
        var signal = new MemoryThresholdRule().Evaluate(_h.Context(_now, metrics));

        Assert.NotNull(signal);
        Assert.Equal("memory", signal!.MetricName);
    }

    // --- Latency ---

    [Fact]
    public void LatencyRuleFiresFromMetricSamples()
    {
        var metrics = Samples(4, i => Sample(i, latency: 2400));
        var signal = new LatencyThresholdRule().Evaluate(_h.Context(_now, metrics));

        Assert.NotNull(signal);
        Assert.Equal(2400, signal!.Observed);
    }

    [Fact]
    public void LatencyRuleAlsoFiresFromSlowTelemetryAlone()
    {
        // A service reporting no metric samples still produces slow requests; the rule must see
        // them rather than going blind.
        var telemetry = new List<Incident>
        {
            new() { Id = Guid.NewGuid(), Timestamp = _now.AddSeconds(-20), Endpoint = "/api/orders/process", DurationMs = 3000, StatusCode = 200 },
            new() { Id = Guid.NewGuid(), Timestamp = _now.AddSeconds(-10), Endpoint = "/api/orders/process", DurationMs = 2800, StatusCode = 200 }
        };

        var signal = new LatencyThresholdRule().Evaluate(_h.Context(_now, telemetry: telemetry));

        Assert.NotNull(signal);
        Assert.Equal("/api/orders/process", signal!.Endpoint);
        Assert.Equal(2, signal.TelemetryReferences.Count);
    }

    // --- Error rate ---

    [Fact]
    public void ErrorRateRuleFiresFromCounterMetrics()
    {
        var metrics = Samples(4, i => Sample(i, requests: 20, errors: 7));
        var signal = new ErrorRateRule().Evaluate(_h.Context(_now, metrics));

        Assert.NotNull(signal);
        Assert.Equal("errorRate", signal!.MetricName);
        Assert.Equal(35, signal.Observed);
    }

    [Fact]
    public void ErrorRateRuleFallsBackToTelemetryWhenNoCountersExist()
    {
        var telemetry = Enumerable.Range(0, 10)
            .Select(i => new Incident
            {
                Id = Guid.NewGuid(),
                Timestamp = _now.AddSeconds(-i),
                Endpoint = "/api/orders/process",
                StatusCode = i < 5 ? 503 : 200,
                ErrorMessage = i < 5 ? "boom" : null
            })
            .ToList();

        var signal = new ErrorRateRule().Evaluate(_h.Context(_now, telemetry: telemetry));

        Assert.NotNull(signal);
        Assert.Equal(50, signal!.Observed);
    }

    [Fact]
    public void ErrorRateRuleStaysQuietWhenNothingIsFailing()
    {
        var metrics = Samples(4, i => Sample(i, requests: 100, errors: 0));
        Assert.Null(new ErrorRateRule().Evaluate(_h.Context(_now, metrics)));
    }

    // --- Retry storm ---

    [Fact]
    public void RetryStormRuleFiresOnHighRetryRate()
    {
        var metrics = Samples(4, i => Sample(i, retries: 30));
        var signal = new RetryStormRule().Evaluate(_h.Context(_now, metrics));

        Assert.NotNull(signal);
        Assert.Equal("retries", signal!.MetricName);
        Assert.Equal("/min", signal.Unit);
    }

    [Fact]
    public void RetryStormRuleIsSilentWhenNoRetriesAreReported()
    {
        var metrics = Samples(4, i => Sample(i, cpu: 90));
        Assert.Null(new RetryStormRule().Evaluate(_h.Context(_now, metrics)));
    }

    // --- Request burst ---

    [Fact]
    public void RequestBurstRuleFiresOnHighThroughput()
    {
        var metrics = Samples(4, i => Sample(i, requests: 500));
        var signal = new RequestBurstRule().Evaluate(_h.Context(_now, metrics));

        Assert.NotNull(signal);
        Assert.Equal("requests", signal!.MetricName);
    }

    // --- Repeated errors ---

    [Fact]
    public void RepeatedErrorsRuleGroupsByEndpointAndType()
    {
        var telemetry = Enumerable.Range(0, 6)
            .Select(i => new Incident
            {
                Id = Guid.NewGuid(),
                Timestamp = _now.AddSeconds(-i),
                Endpoint = "/api/orders/process",
                StatusCode = 503,
                ErrorType = "DownstreamTimeoutException",
                ErrorMessage = "retry exhausted"
            })
            .ToList();

        var signal = new RepeatedErrorsRule().Evaluate(_h.Context(_now, telemetry: telemetry));

        Assert.NotNull(signal);
        Assert.Equal(6, signal!.Observed);
        Assert.Equal("/api/orders/process", signal.Endpoint);
    }

    [Fact]
    public void RepeatedErrorsRuleNeedsEnoughOccurrences()
    {
        var telemetry = Enumerable.Range(0, 2)
            .Select(i => new Incident { Id = Guid.NewGuid(), Timestamp = _now, Endpoint = "/a", StatusCode = 503 })
            .ToList();

        Assert.Null(new RepeatedErrorsRule().Evaluate(_h.Context(_now, telemetry: telemetry)));
    }

    // --- Sudden deviation ---

    [Fact]
    public void DeviationRuleCatchesAStepChangeBelowTheStaticThreshold()
    {
        // Every value here is under the 80% CPU threshold, so only the deviation rule can catch it.
        var metrics = new List<Metric>
        {
            Sample(0, cpu: 20), Sample(1, cpu: 21), Sample(2, cpu: 20), Sample(3, cpu: 70)
        };

        var signal = new MetricDeviationRule().Evaluate(_h.Context(_now, metrics));

        Assert.NotNull(signal);
        Assert.Contains("deviation", signal!.Symptom, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeviationRuleIgnoresAFlatSeries()
    {
        var metrics = Samples(6, i => Sample(i, cpu: 30));
        Assert.Null(new MetricDeviationRule().Evaluate(_h.Context(_now, metrics)));
    }

    [Fact]
    public void DeviationRuleIgnoresStatisticallyLargeButOperationallyTinyIdleJitter()
    {
        var metrics = new List<Metric>
        {
            Sample(0, cpu: 0.0), Sample(1, cpu: 0.1), Sample(2, cpu: 0.0), Sample(3, cpu: 0.2)
        };

        Assert.Null(new MetricDeviationRule().Evaluate(_h.Context(_now, metrics)));
    }

    [Fact]
    public void DeviationRuleNeedsABaseline()
    {
        var metrics = new List<Metric> { Sample(0, cpu: 20), Sample(1, cpu: 90) };
        Assert.Null(new MetricDeviationRule().Evaluate(_h.Context(_now, metrics)));
    }

    // --- Queue backlog ---

    [Fact]
    public void QueueBacklogRuleFiresOnDeepQueues()
    {
        var metrics = Samples(4, i => Sample(i, queue: 85));
        var signal = new QueueBacklogRule().Evaluate(_h.Context(_now, metrics));

        Assert.NotNull(signal);
        Assert.Equal("queue", signal!.MetricName);
    }

    // --- Engine behaviour ---

    [Fact]
    public void EngineRunsEveryRuleAndReturnsAllFiringSignals()
    {
        var metrics = Samples(4, i => Sample(i, cpu: 94, memory: 91, latency: 2400, requests: 20, errors: 7, retries: 30, queue: 85));
        var signals = _h.CreateDetectionEngine().Evaluate(_h.Context(_now, metrics));

        var rules = signals.Select(s => s.RuleId).ToHashSet();

        Assert.Contains("cpu-threshold", rules);
        Assert.Contains("memory-threshold", rules);
        Assert.Contains("latency-threshold", rules);
        Assert.Contains("error-rate-threshold", rules);
        Assert.Contains("retry-storm", rules);
        Assert.Contains("queue-backlog", rules);
    }

    [Fact]
    public void CooldownSuppressesADuplicateSignal()
    {
        var metrics = Samples(4, i => Sample(i, cpu: 94));
        var cooldown = new InMemoryDetectionCooldownStore();
        var engine = _h.CreateDetectionEngine(cooldown);

        var first = engine.Evaluate(_h.Context(_now, metrics));
        var second = engine.Evaluate(_h.Context(_now.AddSeconds(5), metrics));

        Assert.NotEmpty(first);
        Assert.Empty(second);
    }

    [Fact]
    public void SignalFiresAgainOnceTheCooldownExpires()
    {
        var metrics = Samples(4, i => Sample(i, cpu: 94));
        var cooldown = new InMemoryDetectionCooldownStore();
        var engine = _h.CreateDetectionEngine(cooldown);

        engine.Evaluate(_h.Context(_now, metrics));
        var later = engine.Evaluate(_h.Context(_now.AddSeconds(_h.Detection.CooldownSeconds + 1), metrics));

        Assert.NotEmpty(later);
    }

    [Fact]
    public void DisablingDetectionSilencesTheEngine()
    {
        _h.Detection.Enabled = false;
        var metrics = Samples(4, i => Sample(i, cpu: 99));

        Assert.Empty(_h.CreateDetectionEngine().Evaluate(_h.Context(_now, metrics)));
    }

    [Fact]
    public void ADeduplicationKeyIsStablePerRuleAndService()
    {
        var metrics = Samples(4, i => Sample(i, cpu: 94));
        var signal = new CpuThresholdRule().Evaluate(_h.Context(_now, metrics))!;

        Assert.Contains("cpu-threshold", signal.DedupKey);
        Assert.Contains(_h.Service, signal.DedupKey);
        Assert.Contains(_h.Service, signal.CorrelationKey);
        Assert.DoesNotContain("cpu-threshold", signal.CorrelationKey);
    }

    [Fact]
    public async Task EngineReadsFromTheDatabaseAndScopesToTheRightService()
    {
        for (var i = 0; i < 4; i++)
            _h.SeedMetric(DateTime.UtcNow.AddSeconds(-30 + i * 10), cpu: 94);

        var signals = await _h.CreateDetectionEngine()
            .EvaluateAsync(_h.ProjectId, _h.Environment, _h.Service);

        Assert.Contains(signals, s => s.RuleId == "cpu-threshold");
        Assert.All(signals, s => Assert.Equal(_h.Service, s.Service));
    }

    [Fact]
    public async Task EngineReturnsNothingWhenThereIsNoTelemetry()
    {
        var signals = await _h.CreateDetectionEngine()
            .EvaluateAsync(Guid.NewGuid(), "Production");

        Assert.Empty(signals);
    }

    [Fact]
    public async Task EngineIgnoresTelemetryForADeletedOrUnregisteredProject()
    {
        _h.SeedMetric(DateTime.UtcNow, cpu: 99);
        _h.Db.Projects.RemoveRange(_h.Db.Projects);
        await _h.Db.SaveChangesAsync();

        var signals = await _h.CreateDetectionEngine()
            .EvaluateAsync(_h.ProjectId, _h.Environment, _h.Service);

        Assert.Empty(signals);
    }

    [Fact]
    public void ARuleThatThrowsDoesNotBlindTheEngine()
    {
        var engine = new DetectionEngine(
            _h.Db,
            new IDetectionRule[] { new ThrowingRule(), new CpuThresholdRule() },
            new InMemoryDetectionCooldownStore(),
            TestHarness.Opt(_h.Detection),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DetectionEngine>.Instance);

        var metrics = Samples(4, i => Sample(i, cpu: 94));
        var signals = engine.Evaluate(_h.Context(_now, metrics));

        Assert.Single(signals);
        Assert.Equal("cpu-threshold", signals[0].RuleId);
    }

    private class ThrowingRule : IDetectionRule
    {
        public DetectionRuleKind Kind => DetectionRuleKind.CpuThreshold;
        public string RuleId => "throwing-rule";
        public DetectionSignal? Evaluate(DetectionContext context) => throw new InvalidOperationException("boom");
    }
}
