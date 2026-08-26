using Kairon.Backend.DTOs.Sre;
using Kairon.Backend.Models;
using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services.Detection;
using Kairon.Backend.Services.Remediation;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// Staleness and re-investigation.
///
/// These cover a gap found running the demo end to end: an incident detected on its first two
/// signals was diagnosed from those two, then went on absorbing sixteen more - including the retry
/// storm that was the actual cause - while still showing the original conclusion. The diagnosis is
/// now flagged, and an operator can ask for a fresh one.
/// </summary>
public class ReInvestigationTests : IDisposable
{
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private DetectionSignal Signal(string ruleId, string metric, IncidentSeverity severity = IncidentSeverity.High) => new()
    {
        RuleId = ruleId,
        MetricName = metric,
        ProjectId = _h.ProjectId,
        Application = "Kairon.DemoApp",
        Service = _h.Service,
        Component = _h.Service,
        Environment = _h.Environment,
        Severity = severity,
        Symptom = $"{metric} breached at {Guid.NewGuid():N}",
        Observed = 100,
        Threshold = 50,
        Unit = "%",
        DetectedAt = DateTime.UtcNow
    };

    // --- Staleness ---

    [Fact]
    public async Task ANewDiagnosisIsNotStale()
    {
        var incident = _h.SeedIncident();

        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var updated = await _h.Db.SreIncidents.FirstAsync(i => i.Id == incident.Id);
        Assert.False(updated.DiagnosisStale);
    }

    [Fact]
    public async Task NewSignalsAfterADiagnosisMarkItStale()
    {
        var incident = _h.SeedIncident();
        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        await _h.CreateCorrelationEngine().CorrelateAsync(new[] { Signal("retry-storm", "retries") });

        var updated = await _h.Db.SreIncidents.FirstAsync(i => i.Id == incident.Id);

        Assert.True(updated.DiagnosisStale);
        Assert.Equal(IncidentStatus.AwaitingApproval, updated.Status);
    }

    [Fact]
    public async Task SignalsArrivingBeforeAnyDiagnosisDoNotMarkStaleness()
    {
        // Nothing has concluded anything yet, so there is nothing to be stale.
        var incident = _h.SeedIncident();

        await _h.CreateCorrelationEngine().CorrelateAsync(new[] { Signal("retry-storm", "retries") });

        var updated = await _h.Db.SreIncidents.FirstAsync(i => i.Id == incident.Id);
        Assert.False(updated.DiagnosisStale);
    }

    [Fact]
    public async Task ARepeatedIdenticalSignalDoesNotMarkStaleness()
    {
        var incident = _h.SeedIncident();
        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var signal = Signal("retry-storm", "retries");
        await _h.CreateCorrelationEngine().CorrelateAsync(new[] { signal });

        // Clear the flag, then re-send exactly the same observation.
        var mid = await _h.Db.SreIncidents.FirstAsync(i => i.Id == incident.Id);
        mid.DiagnosisStale = false;
        await _h.Db.SaveChangesAsync();

        await _h.CreateCorrelationEngine().CorrelateAsync(new[] { signal });

        var updated = await _h.Db.SreIncidents.FirstAsync(i => i.Id == incident.Id);
        Assert.False(updated.DiagnosisStale);
    }

    // --- Re-investigation ---

    [Fact]
    public async Task ReInvestigationProducesAFreshDiagnosisAndClearsStaleness()
    {
        var incident = _h.SeedIncident();
        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        await _h.CreateCorrelationEngine().CorrelateAsync(new[] { Signal("retry-storm", "retries") });

        _h.Ai.NextResult = FakeAiService.DefaultResult();
        _h.Ai.NextResult.RootCause = "Revised: controlled retry loop is the leading signal.";

        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var updated = await _h.Db.SreIncidents.FirstAsync(i => i.Id == incident.Id);

        Assert.Equal("Revised: controlled retry loop is the leading signal.", updated.RootCause);
        Assert.False(updated.DiagnosisStale);
        Assert.Equal(IncidentStatus.AwaitingApproval, updated.Status);
    }

    [Fact]
    public async Task ReInvestigationWithdrawsTheProposalsItSupersedes()
    {
        var incident = _h.SeedIncident();
        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var original = await _h.Db.RemediationActions.FirstAsync(a => a.IncidentId == incident.Id);

        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var actions = await _h.Db.RemediationActions
            .Where(a => a.IncidentId == incident.Id)
            .ToListAsync();

        var superseded = actions.First(a => a.Id == original.Id);

        Assert.Equal(RemediationStatus.Cancelled, superseded.Status);
        Assert.Contains("re-investigated", superseded.RejectionReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        // Exactly one action is approvable afterwards, so the operator is never shown two
        // competing proposals from two different diagnoses.
        Assert.Single(actions, a => a.Status == RemediationStatus.AwaitingApproval);
    }

    [Fact]
    public async Task ReInvestigationIsRefusedOnceARemediationHasRun()
    {
        // The environment has already been touched. Rewinding the incident's state at that point
        // would misrepresent what actually happened.
        var incident = _h.SeedIncident();
        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var action = await _h.Db.RemediationActions.FirstAsync(a => a.IncidentId == incident.Id);
        action.Status = RemediationStatus.Executed;
        await _h.Db.SaveChangesAsync();

        var callsBefore = _h.Ai.InvestigateCalls;
        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var updated = await _h.Db.SreIncidents.FirstAsync(i => i.Id == incident.Id);

        Assert.Equal(callsBefore, _h.Ai.InvestigateCalls);
        Assert.Equal(IncidentStatus.AwaitingApproval, updated.Status);
    }

    [Fact]
    public async Task ReInvestigationIsRefusedOnceAnActionIsApproved()
    {
        var incident = _h.SeedIncident();
        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var action = await _h.Db.RemediationActions.FirstAsync(a => a.IncidentId == incident.Id);
        action.Status = RemediationStatus.Approved;
        await _h.Db.SaveChangesAsync();

        var callsBefore = _h.Ai.InvestigateCalls;
        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        Assert.Equal(callsBefore, _h.Ai.InvestigateCalls);
    }

    [Fact]
    public async Task ReInvestigationIsRefusedOnATerminalIncident()
    {
        var incident = _h.SeedIncident(IncidentStatus.Resolved);

        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        Assert.Equal(0, _h.Ai.InvestigateCalls);
    }

    [Fact]
    public async Task ReInvestigationIsAudited()
    {
        var incident = _h.SeedIncident();
        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);
        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var operatorEvents = _h.Db.IncidentEvents
            .Where(e => e.IncidentId == incident.Id
                        && e.EventType == IncidentEventTypes.Investigating
                        && e.Actor == "operator")
            .ToList();

        Assert.NotEmpty(operatorEvents);
        Assert.Contains(operatorEvents, e => (e.Message ?? string.Empty).Contains("Re-investigating"));
    }

    [Fact]
    public void TheLifecycleAllowsReInvestigationOnlyFromPreApprovalStates()
    {
        Assert.True(IncidentLifecycle.CanReInvestigate(IncidentStatus.Diagnosed));
        Assert.True(IncidentLifecycle.CanReInvestigate(IncidentStatus.Predicted));
        Assert.True(IncidentLifecycle.CanReInvestigate(IncidentStatus.RecommendationReady));
        Assert.True(IncidentLifecycle.CanReInvestigate(IncidentStatus.AwaitingApproval));

        // Once remediation has started, the machine only moves forward.
        Assert.False(IncidentLifecycle.CanReInvestigate(IncidentStatus.Remediating));
        Assert.False(IncidentLifecycle.CanReInvestigate(IncidentStatus.Verifying));
        Assert.False(IncidentLifecycle.CanReInvestigate(IncidentStatus.Resolved));
        Assert.False(IncidentLifecycle.CanReInvestigate(IncidentStatus.Failed));
    }

    [Fact]
    public void RemediatingStillCannotBeReachedWithoutApproval()
    {
        // The re-investigation edge must not have opened a path around the approval gate.
        foreach (var from in Enum.GetValues<IncidentStatus>())
        {
            if (from == IncidentStatus.AwaitingApproval) continue;

            Assert.False(IncidentLifecycle.CanTransition(from, IncidentStatus.Remediating),
                $"{from} must not reach Remediating directly");
        }
    }
}

/// <summary>
/// The sustained-breach guard. Also from the live run: two samples three seconds apart were
/// satisfying a six-second sustained requirement, so every threshold rule fired on the leading
/// edge of a ramp instead of on a sustained breach.
/// </summary>
public class SustainedBreachTests : IDisposable
{
    private readonly TestHarness _h = new();
    private readonly DateTime _now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _h.Dispose();

    private List<Metric> Ramp(int count, int intervalSeconds, double cpu) =>
        Enumerable.Range(0, count)
            .Select(i => new Metric
            {
                Id = Guid.NewGuid(),
                ProjectId = _h.ProjectId,
                Timestamp = _now.AddSeconds(-(intervalSeconds * (count - 1 - i))),
                CpuPercent = cpu,
                Environment = _h.Environment,
                Service = _h.Service
            })
            .ToList();

    [Fact]
    public void TheLeadingEdgeOfARampIsNotASustainedBreach()
    {
        // Two samples three seconds apart, against a five-second sustained requirement.
        _h.Detection.SustainedBreachSeconds = 5;

        Assert.Null(new CpuThresholdRule().Evaluate(_h.Context(_now, Ramp(2, 3, 94))));
    }

    [Fact]
    public void ABreachSustainedLongEnoughStillFires()
    {
        _h.Detection.SustainedBreachSeconds = 5;

        var signal = new CpuThresholdRule().Evaluate(_h.Context(_now, Ramp(4, 3, 94)));

        Assert.NotNull(signal);
    }

    [Fact]
    public void CoarseSamplingIsStillHonoured()
    {
        // A collector reporting every 30 seconds produces few samples, but they span well past the
        // sustained window, so the rule must still fire.
        _h.Detection.SustainedBreachSeconds = 20;

        var signal = new CpuThresholdRule().Evaluate(_h.Context(_now, Ramp(3, 30, 94)));

        Assert.NotNull(signal);
    }
}
