using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services;
using Kairon.Backend.Services.Evidence;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// Emerging risks come only from real trends in stored data: a forecast horizon only when the trend
/// is consistent, nothing for noise or too little data, and a current breach is never presented as a
/// prediction.
/// </summary>
public sealed class PredictiveInsightsTests : IDisposable
{
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private PredictiveInsightsService Service() => new(_h.Db, TestHarness.Opt(_h.Detection));

    private void SeedSeries(int count, Func<int, (double? cpu, double? memory, double? latency, long errors, long? queue)> at)
    {
        var start = DateTime.UtcNow.AddMinutes(-count);
        for (var i = 0; i < count; i++)
        {
            var (cpu, memory, latency, errors, queue) = at(i);
            _h.SeedMetric(start.AddMinutes(i), cpu: cpu, memory: memory, latency: latency, requests: 20, errors: errors, queue: queue);
        }
    }

    [Fact]
    public async Task SteadyMemoryGrowthIsForecastWithAHorizonToTheThreshold()
    {
        // 50% -> ~79.5% over 30 minutes, about +1%/minute; the threshold is 85%.
        SeedSeries(30, i => (20, 50 + i * 1.0 + (i % 2 == 0 ? 0.3 : -0.3), 150, 0, null));

        var risks = await Service().GetEmergingRisksAsync(null);

        var memory = Assert.Single(risks.Risks, r => r.Component == "memory");
        Assert.Equal("capacity", memory.Kind);
        Assert.InRange(memory.HorizonMinutes!.Value, 3, 10);
        Assert.Contains("Memory rising", memory.Observation);
        Assert.Contains("R²", memory.Observation);
        Assert.Contains("leak", memory.PreventiveAction);
        Assert.InRange(memory.Confidence, 0.5, 0.9);
    }

    [Fact]
    public async Task NoiseWithoutATrendProducesNoRisk()
    {
        var random = new Random(7);
        SeedSeries(40, _ => (30 + random.NextDouble() * 20, 40 + random.NextDouble() * 10, 150 + random.NextDouble() * 50, 0, null));

        var risks = await Service().GetEmergingRisksAsync(null);

        Assert.Empty(risks.Risks);
        Assert.Empty(risks.InsufficientData);
    }

    [Fact]
    public async Task TooLittleDataIsReportedAsInsufficientInsteadOfGuessed()
    {
        SeedSeries(5, i => (40 + i * 10, null, 150, 0, null));

        var risks = await Service().GetEmergingRisksAsync(null);

        Assert.Empty(risks.Risks);
        var gap = Assert.Single(risks.InsufficientData);
        Assert.Contains("at least 12", gap.Reason);
    }

    [Fact]
    public async Task AMetricAlreadyOverItsThresholdIsACurrentProblemNotAPrediction()
    {
        SeedSeries(30, i => (60 + i * 1.5, null, 150, 0, null)); // CPU passes 80% part-way through

        var risks = await Service().GetEmergingRisksAsync(null);

        Assert.DoesNotContain(risks.Risks, r => r.Component == "cpu");
    }

    [Fact]
    public async Task RisingLatencyWhileErrorsAreStillLowIsFlaggedAsAPrecursor()
    {
        SeedSeries(30, i => (20, null, 300 + i * 20, 0, null)); // 300 ms -> 880 ms, threshold 1000 ms

        var risks = await Service().GetEmergingRisksAsync(null);

        var latency = Assert.Single(risks.Risks, r => r.Kind == "precursor");
        Assert.Contains("precedes timeouts and errors", latency.Consequence);
        Assert.NotNull(latency.HorizonMinutes);
    }

    [Fact]
    public async Task AGrowingQueueIsFlaggedAsABacklogRisk()
    {
        SeedSeries(30, i => (20, null, 150, 0, i)); // 0 -> 29 items, threshold 50

        var risks = await Service().GetEmergingRisksAsync(null);

        var queue = Assert.Single(risks.Risks, r => r.Component == "queue");
        Assert.Equal("backlog", queue.Kind);
        Assert.Null(queue.HorizonMinutes);
    }

    [Fact]
    public async Task RepeatedDependencyFailuresAndRecurringIncidentsAreReported()
    {
        for (var i = 0; i < 6; i++)
        {
            var call = _h.SeedTelemetry(DateTime.UtcNow.AddMinutes(-10 + i), endpoint: "payments.example:443/charge",
                statusCode: i < 4 ? 503 : 200, errorType: null, errorMessage: null);
            call.Method = "DEPENDENCY";
        }
        for (var i = 0; i < 3; i++)
        {
            var incident = _h.SeedIncident(IncidentStatus.Resolved, correlationKey: $"k{i}");
            incident.Timestamp = DateTime.UtcNow.AddHours(-i * 3);
            incident.CorrelatedMetricsJson = SreJson.Serialize(new List<CorrelatedSignalSnapshot> { new() { Rule = "cpu-threshold", MetricName = "cpu" } });
        }
        _h.Db.SaveChanges();

        var risks = await Service().GetEmergingRisksAsync(null);

        var dependency = Assert.Single(risks.Risks, r => r.Kind == "dependency");
        Assert.Contains("4 of 6 calls to payments.example:443/charge failed", dependency.Observation);
        var recurring = Assert.Single(risks.Risks, r => r.Kind == "recurrence");
        Assert.Equal("cpu", recurring.Component);
        Assert.Contains("3 incidents", recurring.Observation);
    }

    // --- Audit: forecasts must not extrapolate from inadequate or stale data ---------------------

    [Fact]
    public async Task ATrendThatEndedLongAgoIsStaleNotAForecast()
    {
        // A steady memory climb - but the app stopped reporting 40 minutes ago.
        var start = DateTime.UtcNow.AddMinutes(-58);
        for (var i = 0; i < 18; i++)
            _h.SeedMetric(start.AddMinutes(i), cpu: 20, memory: 50 + i * 1.5, requests: 20);

        var risks = await Service().GetEmergingRisksAsync(null);

        Assert.Empty(risks.Risks);
        Assert.Contains("stale", Assert.Single(risks.InsufficientData).Reason);
    }

    [Fact]
    public async Task AStepChangeInWorkloadIsNotExtrapolatedAsASteadyClimb()
    {
        // Flat at 30%, then flat at 65% - a workload change, not a trend toward 80%.
        SeedSeries(30, i => (i < 15 ? 30 : 65, null, 150, 0, null));

        var risks = await Service().GetEmergingRisksAsync(null);

        Assert.DoesNotContain(risks.Risks, r => r.Component == "cpu");
    }

    [Fact]
    public async Task IrregularIntervalsAndMissingSamplesUseRealTimestamps()
    {
        // 25 samples spread unevenly over 40 minutes, a few without a memory reading.
        var start = DateTime.UtcNow.AddMinutes(-40);
        var minute = 0.0;
        for (var i = 0; i < 25; i++)
        {
            minute += i % 3 == 0 ? 2.5 : 1.0;
            _h.SeedMetric(start.AddMinutes(minute), cpu: 20, memory: i % 7 == 3 ? null : 40 + minute * 0.8, requests: 20);
        }

        var risks = await Service().GetEmergingRisksAsync(null);

        var memory = Assert.Single(risks.Risks, r => r.Component == "memory");
        Assert.Contains("+48%/hour", memory.Observation); // 0.8 %/minute, measured on real time, not sample index
    }

    [Fact]
    public async Task ALatestReadingAlreadyOverTheThresholdIsNotForecastEvenIfTheFittedLineIsBelowIt()
    {
        // Climbing 50% -> 79% and then the last reading jumps to 90%: a current problem, not a forecast.
        SeedSeries(30, i => (20, i == 29 ? 90 : 50 + i, 150, 0, null));

        var risks = await Service().GetEmergingRisksAsync(null);

        Assert.DoesNotContain(risks.Risks, r => r.Component == "memory");
    }

    [Fact]
    public async Task RetriesAreJudgedPerMinuteAgainstAPerMinuteThreshold()
    {
        // One sample per minute, retries climbing 0 -> 29 per sample = per minute; threshold 30/min.
        var start = DateTime.UtcNow.AddMinutes(-30);
        for (var i = 0; i < 30; i++)
            _h.SeedMetric(start.AddMinutes(i), cpu: 20, latency: 150, requests: 20, retries: i);

        var risks = await Service().GetEmergingRisksAsync(null);

        var retries = Assert.Single(risks.Risks, r => r.Component == "retries");
        Assert.Contains("/min", retries.Observation);
        Assert.DoesNotContain("/sample", retries.Observation);
    }

    [Fact]
    public async Task AShortLivedSpikeIsNotATrend()
    {
        SeedSeries(30, i => (i is 20 or 21 ? 78 : 30, null, 150, 0, null));

        Assert.Empty((await Service().GetEmergingRisksAsync(null)).Risks);
    }

    [Fact]
    public async Task ARiseThatHasAlreadyReversedIsNotExtrapolated()
    {
        // Climbed for 20 minutes, falling for the last 10: the workload changed.
        SeedSeries(30, i => (20, i < 20 ? 40 + i * 1.5 : 70 - (i - 20) * 1.5, 150, 0, null));

        Assert.DoesNotContain((await Service().GetEmergingRisksAsync(null)).Risks, r => r.Component == "memory");
    }
}
