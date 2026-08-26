using Kairon.Backend.DTOs.Sre;
using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services;
using Kairon.Backend.Services.Demo;
using Kairon.Backend.Services.Remediation;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// The full lifecycle (PRD sections 3, 13, 18): investigate, diagnose, predict, recommend,
/// approve, remediate, verify, resolve - plus every way it is allowed to fail.
/// </summary>
public class OrchestrationTests : IDisposable
{
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    /// <summary>Seeds the metric history that lets verification find a "before" and an "after".</summary>
    private void SeedRecoveryTelemetry()
    {
        var now = DateTime.UtcNow;

        // Degraded window, before the remediation.
        for (var i = 0; i < 4; i++)
            _h.SeedMetric(now.AddSeconds(-120 + i * 10), cpu: 94, latency: 2400, requests: 20, errors: 7, retries: 30, queue: 85);

        // Recovered window, after it.
        for (var i = 0; i < 4; i++)
            _h.SeedMetric(now.AddSeconds(i), cpu: 24, latency: 120, requests: 20, errors: 0, retries: 0, queue: 2);
    }

    // --- Investigation ---

    [Fact]
    public async Task InvestigationDrivesDetectedThroughToAwaitingApproval()
    {
        var incident = _h.SeedIncident();

        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var updated = await _h.Db.SreIncidents.Include(i => i.Actions).FirstAsync(i => i.Id == incident.Id);

        Assert.Equal(IncidentStatus.AwaitingApproval, updated.Status);
        Assert.Equal("Controlled retry loop causing repeated downstream requests.", updated.RootCause);
        Assert.Equal(0.92, updated.Confidence);
        Assert.NotNull(updated.PredictedImpact);
        Assert.Single(updated.Actions);
        Assert.Equal(RemediationStatus.AwaitingApproval, updated.Actions[0].Status);
    }

    [Fact]
    public async Task InvestigationWritesEveryLifecycleAuditEvent()
    {
        var incident = _h.SeedIncident();

        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var types = _h.Db.IncidentEvents.Where(e => e.IncidentId == incident.Id)
            .Select(e => e.EventType).ToList();

        Assert.Contains(IncidentEventTypes.Investigating, types);
        Assert.Contains(IncidentEventTypes.Diagnosed, types);
        Assert.Contains(IncidentEventTypes.Predicted, types);
        Assert.Contains(IncidentEventTypes.Recommended, types);
        Assert.Contains(IncidentEventTypes.AwaitingApproval, types);
    }

    [Fact]
    public async Task EvidenceIsCollectedAndPersistedForAudit()
    {
        _h.SeedMetric(DateTime.UtcNow.AddSeconds(-30), cpu: 94, latency: 2400, requests: 20, errors: 7);
        _h.SeedTelemetry(DateTime.UtcNow.AddSeconds(-20));

        var incident = _h.SeedIncident();

        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var evidence = _h.Db.IncidentEvidence.Where(e => e.IncidentId == incident.Id).ToList();

        Assert.Contains(evidence, e => e.Kind == EvidenceKinds.IncidentMetadata);
        Assert.Contains(evidence, e => e.Kind == EvidenceKinds.RecentMetrics);
        Assert.Contains(evidence, e => e.Kind == EvidenceKinds.CorrelatedSignals);
    }

    [Fact]
    public async Task TheAiOnlyEverSeesTheRegisteredToolsAsOptions()
    {
        var incident = _h.SeedIncident();

        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var offered = _h.Ai.LastEvidence!.AvailableActions.Select(a => a.Action).ToHashSet();

        Assert.Equal(_h.Tools.All().Select(t => t.Name).ToHashSet(), offered);
    }

    [Fact]
    public async Task EvidenceHandedToTheAiIsBounded()
    {
        for (var i = 0; i < 100; i++)
            _h.SeedMetric(DateTime.UtcNow.AddSeconds(-i), cpu: 90);

        var incident = _h.SeedIncident();

        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        Assert.True(_h.Ai.LastEvidence!.RecentMetrics.Count <= _h.AiOptions.MaxMetricSamples);
    }

    // --- AI failure paths ---

    [Fact]
    public async Task AnUnavailableAiLeavesTheIncidentOpenForRetry()
    {
        _h.Ai.NextException = new AiUnavailableException("AI service is down");
        var incident = _h.SeedIncident();

        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var updated = await _h.Db.SreIncidents.FirstAsync(i => i.Id == incident.Id);

        // Not Failed: the detection is still valid, only the explanation is missing.
        Assert.Equal(IncidentStatus.Investigating, updated.Status);
        Assert.NotNull(updated.FailureReason);
        Assert.False(updated.IsTerminal);
    }

    [Fact]
    public async Task AFailedInvestigationCanBeRetriedSuccessfully()
    {
        var incident = _h.SeedIncident();
        var orchestrator = _h.CreateOrchestrator();

        _h.Ai.NextException = new AiUnavailableException("down");
        await orchestrator.InvestigateAsync(incident.Id);

        _h.Ai.NextException = null;
        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var updated = await _h.Db.SreIncidents.FirstAsync(i => i.Id == incident.Id);

        Assert.Equal(IncidentStatus.AwaitingApproval, updated.Status);
        Assert.Null(updated.FailureReason);
    }

    [Fact]
    public async Task AnUnexpectedAiErrorIsContained()
    {
        _h.Ai.NextException = new InvalidOperationException("something odd");
        var incident = _h.SeedIncident();

        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var updated = await _h.Db.SreIncidents.FirstAsync(i => i.Id == incident.Id);
        Assert.NotNull(updated.FailureReason);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        _h.Ai.Delay = TimeSpan.FromSeconds(5);
        var incident = _h.SeedIncident();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _h.CreateOrchestrator().InvestigateAsync(incident.Id, cts.Token));
    }

    [Fact]
    public async Task DisablingAiStillLetsDetectionWork()
    {
        _h.AiOptions.Enabled = false;
        var incident = _h.SeedIncident();

        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var updated = await _h.Db.SreIncidents.FirstAsync(i => i.Id == incident.Id);

        Assert.Equal(IncidentStatus.Detected, updated.Status);
        Assert.Equal(0, _h.Ai.InvestigateCalls);
        Assert.NotNull(updated.FailureReason);
    }

    [Fact]
    public async Task AnUnknownIncidentIsIgnoredRatherThanThrowing()
    {
        await _h.CreateOrchestrator().InvestigateAsync(Guid.NewGuid());
        Assert.Equal(0, _h.Ai.InvestigateCalls);
    }

    [Fact]
    public async Task ATerminalIncidentIsNotReInvestigated()
    {
        var incident = _h.SeedIncident(IncidentStatus.Resolved);

        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        Assert.Equal(0, _h.Ai.InvestigateCalls);
    }

    // --- Recommendations and policy ---

    [Fact]
    public async Task AnUnregisteredRecommendationNeverBecomesApprovable()
    {
        _h.Ai.NextResult = FakeAiService.DefaultResult();
        _h.Ai.NextResult.Recommendations = new List<AiRecommendationDto>
        {
            new() { Action = "rm -rf /", Reason = "malicious", ExpectedOutcome = "bad", RiskLevel = "low" }
        };

        var incident = _h.SeedIncident();

        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var updated = await _h.Db.SreIncidents.Include(i => i.Actions).FirstAsync(i => i.Id == incident.Id);

        Assert.Empty(updated.Actions);
        Assert.Equal(IncidentStatus.RecommendationReady, updated.Status);

        // The refused recommendation is still shown to the operator, annotated with why.
        var recommendations = SreJson.Deserialize(updated.RecommendationsJson, new List<RecommendationDto>());
        Assert.Single(recommendations);
        Assert.False(recommendations[0].IsRegisteredTool);
        Assert.NotNull(recommendations[0].PolicyNote);
    }

    [Fact]
    public async Task AnUnrecognisedRiskLabelIsTreatedAsHighNotLow()
    {
        _h.Remediation.MaxAllowedRisk = RiskLevel.Medium;
        _h.Ai.NextResult = FakeAiService.DefaultResult();
        _h.Ai.NextResult.Recommendations[0].RiskLevel = "totally-safe-trust-me";

        var incident = _h.SeedIncident();

        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var updated = await _h.Db.SreIncidents.Include(i => i.Actions).FirstAsync(i => i.Id == incident.Id);
        Assert.Empty(updated.Actions);
    }

    [Fact]
    public async Task NoRecommendationsAtAllIsAValidOutcome()
    {
        _h.Ai.NextResult = FakeAiService.DefaultResult();
        _h.Ai.NextResult.Recommendations = new List<AiRecommendationDto>();

        var incident = _h.SeedIncident();

        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var updated = await _h.Db.SreIncidents.FirstAsync(i => i.Id == incident.Id);

        Assert.Equal(IncidentStatus.RecommendationReady, updated.Status);
        Assert.NotNull(updated.FailureReason);
    }

    // --- Approval, remediation, verification ---

    [Fact]
    public async Task ApprovalExecutesVerifiesAndResolves()
    {
        SeedRecoveryTelemetry();
        var incident = _h.SeedIncident();
        var orchestrator = _h.CreateOrchestrator();

        await orchestrator.InvestigateAsync(incident.Id);

        var action = await _h.Db.RemediationActions.FirstAsync(a => a.IncidentId == incident.Id);
        await _h.CreateOrchestrator().ApproveAsync(incident.Id, action.Id, "alice", "looks right");

        // Execute + verify run off the request thread in production (queued for the incident
        // processing worker - see WorkItemKind.ExecuteRemediation); the test drives that step
        // directly rather than draining the queue, the same way InvestigateAsync above is called
        // directly instead of going through DetectAndCorrelateAsync's enqueue.
        await _h.CreateOrchestrator().ExecuteAndVerifyAsync(incident.Id, action.Id);

        var updated = await _h.Db.SreIncidents
            .Include(i => i.Actions)
            .Include(i => i.Verifications)
            .FirstAsync(i => i.Id == incident.Id);

        Assert.Equal(IncidentStatus.Resolved, updated.Status);
        Assert.Equal(RemediationStatus.Executed, updated.Actions[0].Status);
        Assert.Equal("alice", updated.Actions[0].ApprovedBy);
        Assert.Equal(VerificationStatus.Passed, updated.VerificationState);
        Assert.NotNull(updated.ResolvedAt);
        Assert.Contains(DemoCommands.DisableRetryLoop, _h.Demo.Commands);
    }

    [Fact]
    public async Task VerificationFailureDoesNotResolveTheIncident()
    {
        // Degraded before and still degraded after: nothing recovered.
        var now = DateTime.UtcNow;
        for (var i = 0; i < 4; i++)
            _h.SeedMetric(now.AddSeconds(-120 + i * 10), cpu: 94, latency: 2400, requests: 20, errors: 7, retries: 30);
        for (var i = 0; i < 4; i++)
            _h.SeedMetric(now.AddSeconds(i), cpu: 96, latency: 2600, requests: 20, errors: 8, retries: 35);

        var incident = _h.SeedIncident();
        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var action = await _h.Db.RemediationActions.FirstAsync(a => a.IncidentId == incident.Id);
        await _h.CreateOrchestrator().ApproveAsync(incident.Id, action.Id, "alice", null);
        await _h.CreateOrchestrator().ExecuteAndVerifyAsync(incident.Id, action.Id);

        var updated = await _h.Db.SreIncidents.FirstAsync(i => i.Id == incident.Id);

        Assert.NotEqual(IncidentStatus.Resolved, updated.Status);
        Assert.Equal(VerificationStatus.Failed, updated.VerificationState);
    }

    [Fact]
    public async Task AFailedRemediationFailsTheIncidentWithoutVerifying()
    {
        _h.Demo.ShouldFail = true;
        var incident = _h.SeedIncident();

        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);
        var action = await _h.Db.RemediationActions.FirstAsync(a => a.IncidentId == incident.Id);
        await _h.CreateOrchestrator().ApproveAsync(incident.Id, action.Id, "alice", null);
        await _h.CreateOrchestrator().ExecuteAndVerifyAsync(incident.Id, action.Id);

        var updated = await _h.Db.SreIncidents.Include(i => i.Verifications).FirstAsync(i => i.Id == incident.Id);

        Assert.Equal(IncidentStatus.Failed, updated.Status);
        Assert.Empty(updated.Verifications);
    }

    [Fact]
    public async Task ApprovingAnIncidentThatIsNotAwaitingApprovalIsRejected()
    {
        var incident = _h.SeedIncident();
        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var action = await _h.Db.RemediationActions.FirstAsync(a => a.IncidentId == incident.Id);

        var orchestrator = _h.CreateOrchestrator();
        await orchestrator.ApproveAsync(incident.Id, action.Id, "alice", null);

        // A second approval of the same, now-terminal incident must not run anything again.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => _h.CreateOrchestrator().ApproveAsync(incident.Id, action.Id, "bob", null));
    }

    [Fact]
    public async Task ApprovalOfAnUnknownActionReturnsNull()
    {
        var incident = _h.SeedIncident();
        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var result = await _h.CreateOrchestrator().ApproveAsync(incident.Id, Guid.NewGuid(), "alice", null);

        Assert.Null(result);
    }

    // --- Rejection and cancellation ---

    [Fact]
    public async Task RejectingTheOnlyActionClosesTheIncidentAsRejected()
    {
        var incident = _h.SeedIncident();
        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var action = await _h.Db.RemediationActions.FirstAsync(a => a.IncidentId == incident.Id);
        await _h.CreateOrchestrator().RejectAsync(incident.Id, action.Id, "alice", "not now");

        var updated = await _h.Db.SreIncidents.Include(i => i.Actions).FirstAsync(i => i.Id == incident.Id);

        Assert.Equal(IncidentStatus.Rejected, updated.Status);
        Assert.Equal(RemediationStatus.Rejected, updated.Actions[0].Status);
        Assert.Equal("alice", updated.Actions[0].RejectedBy);
        Assert.Empty(_h.Demo.Commands);
    }

    [Fact]
    public async Task RejectionIsAudited()
    {
        var incident = _h.SeedIncident();
        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var action = await _h.Db.RemediationActions.FirstAsync(a => a.IncidentId == incident.Id);
        await _h.CreateOrchestrator().RejectAsync(incident.Id, action.Id, "alice", "not now");

        var rejected = _h.Db.IncidentEvents
            .Where(e => e.IncidentId == incident.Id && e.EventType == IncidentEventTypes.Rejected)
            .ToList();

        Assert.NotEmpty(rejected);
        Assert.Contains(rejected, e => e.Actor == "operator:alice");
    }

    [Fact]
    public async Task CancellationClosesTheIncidentAndItsPendingActions()
    {
        var incident = _h.SeedIncident();
        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        await _h.CreateOrchestrator().CancelAsync(incident.Id, "alice", "false alarm");

        var updated = await _h.Db.SreIncidents.Include(i => i.Actions).FirstAsync(i => i.Id == incident.Id);

        Assert.Equal(IncidentStatus.Cancelled, updated.Status);
        Assert.All(updated.Actions, a => Assert.Equal(RemediationStatus.Cancelled, a.Status));
    }

    [Fact]
    public async Task CancellingATerminalIncidentIsANoOp()
    {
        var incident = _h.SeedIncident(IncidentStatus.Resolved);

        await _h.CreateOrchestrator().CancelAsync(incident.Id, "alice", null);

        var updated = await _h.Db.SreIncidents.FirstAsync(i => i.Id == incident.Id);
        Assert.Equal(IncidentStatus.Resolved, updated.Status);
    }

    // --- Detection to correlation entry point ---

    [Fact]
    public async Task DetectAndCorrelateCreatesAnIncidentAndQueuesInvestigation()
    {
        for (var i = 0; i < 4; i++)
            _h.SeedMetric(DateTime.UtcNow.AddSeconds(-30 + i * 10), cpu: 94, latency: 2400, requests: 20, errors: 7);

        var ids = await _h.CreateOrchestrator()
            .DetectAndCorrelateAsync(_h.ProjectId, _h.Environment, _h.Service);

        Assert.NotEmpty(ids);
        Assert.True(_h.Queue.Count > 0, "a newly detected incident should be queued for investigation");
    }

    [Fact]
    public async Task DetectAndCorrelateFindsNothingInAHealthySystem()
    {
        for (var i = 0; i < 4; i++)
            _h.SeedMetric(DateTime.UtcNow.AddSeconds(-30 + i * 10), cpu: 20, latency: 100, requests: 20, errors: 0);

        var ids = await _h.CreateOrchestrator()
            .DetectAndCorrelateAsync(_h.ProjectId, _h.Environment, _h.Service);

        Assert.Empty(ids);
    }
}
