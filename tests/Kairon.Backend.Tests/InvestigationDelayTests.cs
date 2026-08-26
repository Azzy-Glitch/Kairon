using Kairon.Backend.Models.Sre;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// The evidence window before the first investigation.
///
/// From the live demo run: the incident tripped on repeated errors at three seconds, was
/// investigated immediately, and was diagnosed as an error-rate problem - while the retry storm
/// actually causing it was still ramping and only became measurable seconds later. Letting an
/// incident finish gathering its correlated signals before forming an opinion produces a first
/// diagnosis that is usually the right one.
/// </summary>
public class InvestigationDelayTests : IDisposable
{
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task AFreshIncidentIsNotInvestigatedImmediately()
    {
        _h.AiOptions.InvestigationDelaySeconds = 30;

        var incident = _h.SeedIncident();
        incident.Timestamp = DateTime.UtcNow;
        await _h.Db.SaveChangesAsync();

        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var updated = await _h.Db.SreIncidents.FirstAsync(i => i.Id == incident.Id);

        Assert.Equal(0, _h.Ai.InvestigateCalls);
        Assert.Equal(IncidentStatus.Detected, updated.Status);
    }

    [Fact]
    public async Task AnIncidentPastTheWindowIsInvestigated()
    {
        _h.AiOptions.InvestigationDelaySeconds = 15;

        var incident = _h.SeedIncident();
        incident.Timestamp = DateTime.UtcNow.AddSeconds(-30);
        await _h.Db.SaveChangesAsync();

        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var updated = await _h.Db.SreIncidents.FirstAsync(i => i.Id == incident.Id);

        Assert.Equal(1, _h.Ai.InvestigateCalls);
        Assert.Equal(IncidentStatus.AwaitingApproval, updated.Status);
    }

    [Fact]
    public async Task WaitingIsNotAFailureState()
    {
        // An incident inside its evidence window is simply still Detected. It must not acquire a
        // failure reason or look stuck to an operator.
        _h.AiOptions.InvestigationDelaySeconds = 30;

        var incident = _h.SeedIncident();
        incident.Timestamp = DateTime.UtcNow;
        await _h.Db.SaveChangesAsync();

        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var updated = await _h.Db.SreIncidents.FirstAsync(i => i.Id == incident.Id);

        Assert.Null(updated.FailureReason);
        Assert.False(updated.IsTerminal);
    }

    [Fact]
    public async Task DelayedInvestigationSeesTheSignalsThatArrivedWhileItWaited()
    {
        // The whole point: the evidence package handed to the AI contains the signals that
        // correlated in during the window, not only the one that tripped detection.
        _h.AiOptions.InvestigationDelaySeconds = 1;

        var incident = _h.SeedIncident();
        incident.Timestamp = DateTime.UtcNow.AddSeconds(-5);
        await _h.Db.SaveChangesAsync();

        await _h.CreateCorrelationEngine().CorrelateAsync(new[]
        {
            new DetectionSignal
            {
                RuleId = "retry-storm",
                MetricName = "retries",
                ProjectId = _h.ProjectId,
                Service = _h.Service,
                Component = _h.Service,
                Environment = _h.Environment,
                Severity = IncidentSeverity.High,
                Symptom = "Retry rate 90/min",
                Observed = 90,
                Threshold = 30,
                Unit = "/min",
                DetectedAt = DateTime.UtcNow
            }
        });

        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var signals = _h.Ai.LastEvidence!.CorrelatedSignals.Select(s => s.Metric).ToList();

        Assert.Contains("retries", signals);
    }

    [Fact]
    public async Task ADisabledWindowInvestigatesImmediately()
    {
        _h.AiOptions.InvestigationDelaySeconds = 0;

        var incident = _h.SeedIncident();
        incident.Timestamp = DateTime.UtcNow;
        await _h.Db.SaveChangesAsync();

        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        Assert.Equal(1, _h.Ai.InvestigateCalls);
    }

    [Fact]
    public async Task TheWindowOnlyAppliesToTheFirstInvestigation()
    {
        // Re-investigation is an explicit operator request and is not made to wait.
        _h.AiOptions.InvestigationDelaySeconds = 0;

        var incident = _h.SeedIncident();
        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        _h.AiOptions.InvestigationDelaySeconds = 300;
        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        Assert.Equal(2, _h.Ai.InvestigateCalls);
    }
}
