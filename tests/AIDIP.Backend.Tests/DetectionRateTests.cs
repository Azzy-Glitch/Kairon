using AIDIP.Backend.Models;
using AIDIP.Backend.Services.Detection;
using Xunit;

namespace AIDIP.Backend.Tests;

/// <summary>
/// Rate arithmetic for the per-minute rules.
///
/// These cover a bug found running the real demo end to end: rates were divided by the nominal
/// evaluation window rather than the span the samples actually cover, so a retry storm that had
/// been building for twenty seconds read six times too low against a two-minute window and never
/// tripped its threshold while it was ramping.
/// </summary>
public class DetectionRateTests : IDisposable
{
    private readonly TestHarness _h = new();
    private readonly DateTime _now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _h.Dispose();

    private List<Metric> Ticks(int count, int intervalSeconds, long retriesPerTick) =>
        Enumerable.Range(0, count)
            .Select(i => new Metric
            {
                Id = Guid.NewGuid(),
                ProjectId = _h.ProjectId,
                Timestamp = _now.AddSeconds(-(intervalSeconds * (count - 1 - i))),
                RequestCount = 20,
                RetryCount = retriesPerTick,
                Environment = _h.Environment,
                Service = _h.Service
            })
            .ToList();

    [Fact]
    public void RateIsMeasuredOverTheObservedSpanNotTheNominalWindow()
    {
        // Five 3-second ticks carrying 3 retries each: 15 retries over ~15 seconds, which is
        // 60/min. Against the nominal 120-second window it would read 7.5/min and stay silent.
        var metrics = Ticks(5, 3, 3);

        var signal = new RetryStormRule().Evaluate(_h.Context(_now, metrics));

        Assert.NotNull(signal);
        Assert.True(signal!.Observed >= 50,
            $"expected roughly 60 retries/min from the observed span, got {signal.Observed}");
    }

    [Fact]
    public void ASlowTrickleStillDoesNotTripTheThreshold()
    {
        // One retry per 30-second tick is 2/min. The fix must not turn a quiet system into an
        // incident just because the sample span is short.
        var metrics = Ticks(5, 30, 1);

        Assert.Null(new RetryStormRule().Evaluate(_h.Context(_now, metrics)));
    }

    [Fact]
    public void ObservedSpanIsClampedToTheNominalWindow()
    {
        // Samples older than the window cannot stretch the divisor beyond it, so a rate can never
        // be diluted below what the configured window would give.
        var metrics = Ticks(5, 600, 100);
        var context = _h.Context(_now, metrics);

        Assert.True(context.WindowMinutes <= _h.Detection.EvaluationWindowSeconds / 60.0 + 0.001);
    }

    [Fact]
    public void ASingleSampleUsesAConservativeShortSpan()
    {
        var metrics = Ticks(1, 3, 100);
        var context = _h.Context(_now, metrics);

        // With no span to measure, the divisor stays small rather than defaulting to the full
        // window, so one sample carrying a large count is not silently discounted.
        Assert.True(context.WindowMinutes <= 1.0 / 6.0 + 0.001);
    }

    [Fact]
    public void RequestBurstUsesTheSameSpanArithmetic()
    {
        // 5 ticks x 200 requests over ~15s is roughly 4000/min, well past the 600/min threshold.
        var metrics = Enumerable.Range(0, 5)
            .Select(i => new Metric
            {
                Id = Guid.NewGuid(),
                ProjectId = _h.ProjectId,
                Timestamp = _now.AddSeconds(-(3 * (4 - i))),
                RequestCount = 200,
                Environment = _h.Environment,
                Service = _h.Service
            })
            .ToList();

        var signal = new RequestBurstRule().Evaluate(_h.Context(_now, metrics));

        Assert.NotNull(signal);
        Assert.True(signal!.Observed > 600);
    }

    [Fact]
    public void WindowMinutesIsNeverZero()
    {
        // Every rate rule divides by this; a zero would produce infinity and a bogus signal.
        var identical = Enumerable.Range(0, 4)
            .Select(_ => new Metric
            {
                Id = Guid.NewGuid(),
                ProjectId = _h.ProjectId,
                Timestamp = _now,
                RetryCount = 5,
                Environment = _h.Environment,
                Service = _h.Service
            })
            .ToList();

        var context = _h.Context(_now, identical);

        Assert.True(context.WindowMinutes > 0);
        Assert.True(double.IsFinite(context.WindowMinutes));
    }
}
