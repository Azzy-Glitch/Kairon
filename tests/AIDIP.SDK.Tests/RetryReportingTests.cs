using AIDIP.SDK;
using Xunit;

namespace AIDIP.SDK.Tests;

/// <summary>
/// How the SDK reports "no retries".
///
/// From a live demo run: a service that had been retrying stopped, so it reported no retry value
/// at all - and post-remediation verification rendered the missing reading as "still breaching",
/// turning a successful fix into an apparent failure. Absence and zero are different facts, and
/// once an application is known to track retries, zero is the one it should be reporting.
/// </summary>
public class RetryReportingTests
{
    [Fact]
    public void AnApplicationThatNeverRetriesReportsNothing()
    {
        // Null, not zero: this application does not track retries, and inventing a zero would
        // make the retry-storm rule evaluate a metric that was never measured.
        var metrics = new AIDIPMetrics();

        var snapshot = metrics.Drain();

        Assert.False(snapshot.RetriesTracked);
        Assert.Equal(0, snapshot.Retries);
    }

    [Fact]
    public void ReportingRetriesMarksThemAsTracked()
    {
        var metrics = new AIDIPMetrics();
        metrics.RecordRetries(6);

        Assert.True(metrics.Drain().RetriesTracked);
    }

    [Fact]
    public void TrackedStatusSurvivesAnIntervalWithNoRetries()
    {
        // The interval after a retry storm ends is the one that matters: it has to say "zero",
        // not "unknown".
        var metrics = new AIDIPMetrics();
        metrics.RecordRetries(6);
        metrics.Drain();

        var quiet = metrics.Drain();

        Assert.True(quiet.RetriesTracked);
        Assert.Equal(0, quiet.Retries);
    }

    [Fact]
    public void ZeroAndNegativeReportsDoNotMarkTracking()
    {
        var metrics = new AIDIPMetrics();

        metrics.RecordRetries(0);
        metrics.RecordRetries(-3);

        Assert.False(metrics.Drain().RetriesTracked);
    }
}
