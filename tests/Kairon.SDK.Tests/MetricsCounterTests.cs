using Kairon.SDK;
using Xunit;

namespace Kairon.SDK.Tests;

/// <summary>
/// The counters behind the SDK's interval metrics. These feed the retry-storm and backlog
/// detection rules, so the arithmetic being right is what makes those rules trustworthy.
/// </summary>
public class MetricsCounterTests
{
    [Fact]
    public void RequestsErrorsAndAverageDurationAreCounted()
    {
        var metrics = new KaironMetrics();

        metrics.RecordRequest(100, isError: false);
        metrics.RecordRequest(300, isError: true);

        var snapshot = metrics.Drain();

        Assert.Equal(2, snapshot.Requests);
        Assert.Equal(1, snapshot.Errors);
        Assert.Equal(200, snapshot.AvgDurationMs);
    }

    [Fact]
    public void CountersResetAfterDrainingSoIntervalsDoNotDoubleCount()
    {
        var metrics = new KaironMetrics();
        metrics.RecordRequest(100, false);

        metrics.Drain();
        var second = metrics.Drain();

        Assert.Equal(0, second.Requests);
        Assert.Equal(0, second.AvgDurationMs);
    }

    [Fact]
    public void RetriesAccumulateAcrossCalls()
    {
        var metrics = new KaironMetrics();

        metrics.RecordRetries(6);
        metrics.RecordRetries(6);
        metrics.RecordRetries(0);

        Assert.Equal(12, metrics.Drain().Retries);
    }

    [Fact]
    public void NegativeRetryReportsAreIgnored()
    {
        var metrics = new KaironMetrics();

        metrics.RecordRetries(-5);

        Assert.Equal(0, metrics.Drain().Retries);
    }

    [Fact]
    public void QueueDepthIsAbsentUntilTheApplicationReportsIt()
    {
        // Absent rather than zero: "the application never told us" and "the queue is empty" are
        // different facts, and the detection rules need to be able to tell them apart.
        var metrics = new KaironMetrics();

        Assert.Null(metrics.Drain().QueueDepth);

        metrics.ReportQueueDepth(42);

        Assert.Equal(42, metrics.Drain().QueueDepth);
    }

    [Fact]
    public void QueueDepthIsAGaugeAndSurvivesADrain()
    {
        var metrics = new KaironMetrics();
        metrics.ReportQueueDepth(7);

        metrics.Drain();

        Assert.Equal(7, metrics.Drain().QueueDepth);
    }

    [Fact]
    public void AReportedQueueDepthOfZeroIsStillAReading()
    {
        var metrics = new KaironMetrics();
        metrics.ReportQueueDepth(0);

        Assert.Equal(0, metrics.Drain().QueueDepth);
    }

    [Fact]
    public void AverageDurationIsZeroWithNoRequests()
    {
        Assert.Equal(0, new KaironMetrics().Drain().AvgDurationMs);
    }

    [Fact]
    public void CountersAreSafeUnderConcurrentUpdates()
    {
        var metrics = new KaironMetrics();

        Parallel.For(0, 1000, _ => metrics.RecordRequest(10, isError: true));

        var snapshot = metrics.Drain();

        Assert.Equal(1000, snapshot.Requests);
        Assert.Equal(1000, snapshot.Errors);
    }
}
