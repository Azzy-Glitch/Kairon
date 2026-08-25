using AIDIP.Backend.Models.Sre;
using AIDIP.Backend.Services;
using AIDIP.Backend.Services.Remediation;
using Xunit;

namespace AIDIP.Backend.Tests;

/// <summary>
/// Verification windowing.
///
/// From the live demo run: a remediation that genuinely worked was reported as failed, because the
/// "after" window started at the moment of execution and so averaged the entire recovery ramp.
/// Every metric was improving and none had finished improving, which reads as "still breaching".
/// The settled state is what the question "did it work" is actually about.
/// </summary>
public class VerificationWindowTests : IDisposable
{
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private RemediationAction ExecutedAction(SreIncident incident, DateTime executedAt)
    {
        var action = new RemediationAction
        {
            IncidentId = incident.Id,
            ActionKey = "ACT-0001",
            ActionType = DemoToolNames.DisableDemoRetryLoop,
            Status = RemediationStatus.Executed,
            StartedAt = executedAt,
            CompletedAt = executedAt
        };

        incident.Actions.Add(action);
        _h.Db.RemediationActions.Add(action);
        _h.Db.SaveChanges();
        return action;
    }

    [Fact]
    public async Task TheRecoveryRampIsExcludedFromTheAfterWindow()
    {
        _h.Verification.SettleSeconds = 10;

        var incident = _h.SeedIncident(IncidentStatus.Verifying);
        var executedAt = DateTime.UtcNow.AddSeconds(-60);
        var action = ExecutedAction(incident, executedAt);

        // Degraded before the fix.
        for (var i = 0; i < 4; i++)
            _h.SeedMetric(executedAt.AddSeconds(-40 + i * 5), cpu: 94, latency: 2400, retries: 30);

        // Mid-recovery, inside the settle period. Improving, but still over threshold - this is
        // exactly the data that used to sink an otherwise successful remediation.
        for (var i = 0; i < 3; i++)
            _h.SeedMetric(executedAt.AddSeconds(i * 3), cpu: 88, latency: 1900, retries: 20);

        // Settled, after the settle period.
        for (var i = 0; i < 4; i++)
            _h.SeedMetric(executedAt.AddSeconds(12 + i * 3), cpu: 22, latency: 110, retries: 0);

        var result = await _h.CreateVerificationService().VerifyAsync(incident, action);
        var comparisons = SreJson.Deserialize(result.ComparisonsJson, new List<MetricComparison>());

        Assert.Equal(VerificationStatus.Passed, result.Status);

        var cpu = comparisons.First(c => c.Metric == "cpu");
        Assert.Equal(22, cpu.After);
    }

    [Fact]
    public async Task AGenuinelyUnrecoveredServiceStillFails()
    {
        // The fix must not have turned verification into a rubber stamp.
        _h.Verification.SettleSeconds = 10;

        var incident = _h.SeedIncident(IncidentStatus.Verifying);
        var executedAt = DateTime.UtcNow.AddSeconds(-60);
        var action = ExecutedAction(incident, executedAt);

        for (var i = 0; i < 4; i++)
            _h.SeedMetric(executedAt.AddSeconds(-40 + i * 5), cpu: 94, latency: 2400, retries: 30);

        for (var i = 0; i < 4; i++)
            _h.SeedMetric(executedAt.AddSeconds(12 + i * 3), cpu: 96, latency: 2600, retries: 35);

        var result = await _h.CreateVerificationService().VerifyAsync(incident, action);

        Assert.Equal(VerificationStatus.Failed, result.Status);
        Assert.Equal("metrics-still-breaching", result.FailureReason);
    }

    [Fact]
    public async Task TelemetryOnlyFromInsideTheSettlePeriodIsInconclusive()
    {
        // Improving-but-unsettled data is not evidence of recovery, and must not be reported as
        // either success or failure.
        _h.Verification.SettleSeconds = 30;

        var incident = _h.SeedIncident(IncidentStatus.Verifying);
        var executedAt = DateTime.UtcNow.AddSeconds(-10);
        var action = ExecutedAction(incident, executedAt);

        for (var i = 0; i < 4; i++)
            _h.SeedMetric(executedAt.AddSeconds(-40 + i * 5), cpu: 94, latency: 2400);

        for (var i = 0; i < 3; i++)
            _h.SeedMetric(executedAt.AddSeconds(i * 2), cpu: 60, latency: 900);

        var result = await _h.CreateVerificationService().VerifyAsync(incident, action);

        Assert.Equal(VerificationStatus.Inconclusive, result.Status);
        Assert.Equal("no-post-remediation-telemetry", result.FailureReason);
    }

    [Fact]
    public async Task WaitingForSettledTelemetryIsBounded()
    {
        // Nothing will ever arrive; the verification must give up rather than hang the incident.
        _h.Verification.SettleSeconds = 0;
        _h.Verification.MaxWaitSeconds = 1;
        _h.Verification.MinimumSamples = 5;

        var incident = _h.SeedIncident(IncidentStatus.Verifying);
        var action = ExecutedAction(incident, DateTime.UtcNow.AddSeconds(-5));

        var started = DateTime.UtcNow;
        var result = await _h.CreateVerificationService().VerifyAsync(incident, action);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.Equal(VerificationStatus.Inconclusive, result.Status);
    }

    [Fact]
    public async Task VerificationRespectsCancellation()
    {
        _h.Verification.SettleSeconds = 30;
        _h.Verification.MaxWaitSeconds = 30;

        var incident = _h.SeedIncident(IncidentStatus.Verifying);
        var action = ExecutedAction(incident, DateTime.UtcNow);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _h.CreateVerificationService().VerifyAsync(incident, action, cts.Token));
    }
}
