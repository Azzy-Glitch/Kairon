using Kairon.Backend.Configuration;
using Kairon.Backend.DTOs.Sre;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services.Audit;
using Kairon.Backend.Services.Correlation;
using Kairon.Backend.Services.Detection;
using Kairon.Backend.Services.Evidence;
using Kairon.Backend.Services.Remediation;
using Kairon.Backend.Services.Verification;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Services.Orchestration;

/// <summary>
/// Drives an incident through the lifecycle:
/// OBSERVE -> DETECT -> CORRELATE -> INVESTIGATE -> DIAGNOSE -> PREDICT -> RECOMMEND -> APPROVE
/// -> REMEDIATE -> VERIFY -> CLOSE (PRD section 3).
///
/// Every transition goes through <see cref="IncidentLifecycle"/> and every step writes an audit
/// event, so the incident's own history is the record of what the system did.
/// </summary>
public interface IIncidentOrchestrator
{
    /// <summary>Runs detection and correlation for a scope. Returns incidents created or updated.</summary>
    Task<IReadOnlyList<Guid>> DetectAndCorrelateAsync(
        Guid projectId, string environment, string? service = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Investigate -> diagnose -> predict -> recommend for one incident. Safe to call again after
    /// an AI failure; the incident simply stays in Investigating until a run succeeds.
    /// </summary>
    Task InvestigateAsync(Guid incidentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Explicit operator-requested re-investigation of a pre-approval incident. Automatic queue
    /// processing never calls this method.
    /// </summary>
    Task ReinvestigateAsync(Guid incidentId, CancellationToken cancellationToken = default);

    Task<RemediationAction?> ApproveAsync(
        Guid incidentId, Guid actionId, string approvedBy, string? note, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an approved action and verifies recovery. Runs off the request thread (queued by
    /// <see cref="ApproveAsync"/>, processed by <see cref="IncidentProcessingWorker"/>) because the
    /// combined execute+verify wait can exceed a minute - the same reason investigation is queued
    /// rather than run inline.
    /// </summary>
    Task ExecuteAndVerifyAsync(Guid incidentId, Guid actionId, CancellationToken cancellationToken = default);

    Task<RemediationAction?> RejectAsync(
        Guid incidentId, Guid actionId, string rejectedBy, string? reason, CancellationToken cancellationToken = default);

    Task CancelAsync(Guid incidentId, string cancelledBy, string? reason, CancellationToken cancellationToken = default);
}

public class IncidentOrchestrator : IIncidentOrchestrator
{
    private readonly AppDbContext _db;
    private readonly IDetectionEngine _detection;
    private readonly ICorrelationEngine _correlation;
    private readonly IEvidenceCollector _evidence;
    private readonly IAiMicroservice _ai;
    private readonly IRemediationPolicy _policy;
    private readonly IRemediationToolRegistry _tools;
    private readonly IRemediationExecutor _executor;
    private readonly IVerificationService _verification;
    private readonly IAuditService _audit;
    private readonly IIncidentKeyGenerator _keys;
    private readonly IIncidentProcessingQueue _queue;
    private readonly AiOrchestrationOptions _aiOptions;
    private readonly RemediationOptions _remediationOptions;
    private readonly ILogger<IncidentOrchestrator> _logger;

    public IncidentOrchestrator(
        AppDbContext db,
        IDetectionEngine detection,
        ICorrelationEngine correlation,
        IEvidenceCollector evidence,
        IAiMicroservice ai,
        IRemediationPolicy policy,
        IRemediationToolRegistry tools,
        IRemediationExecutor executor,
        IVerificationService verification,
        IAuditService audit,
        IIncidentKeyGenerator keys,
        IIncidentProcessingQueue queue,
        IOptions<AiOrchestrationOptions> aiOptions,
        IOptions<RemediationOptions> remediationOptions,
        ILogger<IncidentOrchestrator> logger)
    {
        _db = db;
        _detection = detection;
        _correlation = correlation;
        _evidence = evidence;
        _ai = ai;
        _policy = policy;
        _tools = tools;
        _executor = executor;
        _verification = verification;
        _audit = audit;
        _keys = keys;
        _queue = queue;
        _aiOptions = aiOptions.Value;
        _remediationOptions = remediationOptions.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Guid>> DetectAndCorrelateAsync(
        Guid projectId,
        string environment,
        string? service = null,
        CancellationToken cancellationToken = default)
    {
        var signals = await _detection.EvaluateAsync(projectId, environment, service, null, cancellationToken);
        if (signals.Count == 0)
            return Array.Empty<Guid>();

        var incidents = await _correlation.CorrelateAsync(signals, cancellationToken);

        foreach (var incident in incidents)
        {
            // Only freshly detected incidents need an investigation run. An incident already in
            // the middle of the lifecycle just absorbed the new signals as extra evidence.
            if (incident.Status == IncidentStatus.Detected)
            {
                _queue.TryEnqueue(new IncidentWorkItem(
                    WorkItemKind.ProcessIncident, projectId, environment, service, incident.Id));
            }
        }

        return incidents.Select(i => i.Id).ToList();
    }

    public Task InvestigateAsync(Guid incidentId, CancellationToken cancellationToken = default) =>
        InvestigateCoreAsync(incidentId, allowReinvestigation: false, cancellationToken);

    public Task ReinvestigateAsync(Guid incidentId, CancellationToken cancellationToken = default) =>
        InvestigateCoreAsync(incidentId, allowReinvestigation: true, cancellationToken);

    private async Task InvestigateCoreAsync(
        Guid incidentId,
        bool allowReinvestigation,
        CancellationToken cancellationToken)
    {
        var incident = await LoadAsync(incidentId, cancellationToken);
        if (incident is null)
        {
            _logger.LogWarning("InvestigateAsync called for unknown incident {IncidentId}", incidentId);
            return;
        }

        if (incident.IsTerminal)
        {
            _logger.LogDebug("Incident {Key} is terminal ({Status}); skipping investigation",
                incident.IncidentKey, incident.Status);
            return;
        }

        if (!_aiOptions.Enabled)
        {
            // Detection and correlation still work with AI turned off; the incident simply never
            // acquires a diagnosis. That is the "backend must remain operational when the AI
            // service is unavailable" requirement (PRD section 3).
            incident.FailureReason = "AI orchestration is disabled by configuration.";
            _audit.Record(incident, IncidentEventTypes.Failed, "ai-orchestrator",
                message: incident.FailureReason);
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (incident.Status == IncidentStatus.Detected)
        {
            // Let the incident finish gathering correlated signals before forming an opinion.
            // The sweep re-queues it once it is old enough, so nothing is lost by waiting.
            var age = DateTime.UtcNow - incident.Timestamp;
            if (age < TimeSpan.FromSeconds(_aiOptions.InvestigationDelaySeconds))
            {
                _logger.LogDebug(
                    "Incident {Key} is {Age:F0}s old; waiting for the {Delay}s evidence window before investigating",
                    incident.IncidentKey, age.TotalSeconds, _aiOptions.InvestigationDelaySeconds);
                return;
            }

            var budgetFailure = await GetAiBudgetFailureAsync(incident.Id, cancellationToken);
            if (budgetFailure is not null)
            {
                var budgetPrevious = IncidentLifecycle.Transition(incident, IncidentStatus.Investigating);
                await RecordBudgetFailureAsync(incident, budgetPrevious, budgetFailure, cancellationToken);
                return;
            }

            var previous = IncidentLifecycle.Transition(incident, IncidentStatus.Investigating);
            _audit.Record(incident, IncidentEventTypes.Investigating, "ai-orchestrator",
                previousState: previous.ToString(),
                newState: incident.Status.ToString(),
                message: "Collecting evidence and requesting AI investigation");
            await _db.SaveChangesAsync(cancellationToken);
        }
        else if (allowReinvestigation && IncidentLifecycle.CanReInvestigate(incident.Status))
        {
            var budgetFailure = await GetAiBudgetFailureAsync(incident.Id, cancellationToken);
            if (budgetFailure is not null)
            {
                await RecordBudgetFailureAsync(incident, incident.Status, budgetFailure, cancellationToken);
                return;
            }

            if (!await TryBeginReInvestigationAsync(incident, cancellationToken))
                return;
        }

        if (incident.Status != IncidentStatus.Investigating)
        {
            _logger.LogDebug("Incident {Key} is in {Status}; investigation is not applicable",
                incident.IncidentKey, incident.Status);
            return;
        }


        // An incident left in Investigating after a failed request may be retried explicitly. It
        // still consumes the same durable budget as every other model request.
        var inProgressBudgetFailure = await GetAiBudgetFailureAsync(incident.Id, cancellationToken);
        if (inProgressBudgetFailure is not null)
        {
            await RecordBudgetFailureAsync(incident, incident.Status, inProgressBudgetFailure, cancellationToken);
            return;
        }

        var package = await _evidence.CollectAsync(incident, cancellationToken);
        _evidence.Persist(incident, package);
        _audit.Record(incident, IncidentEventTypes.AiRequestStarted, "ai-orchestrator",
            message: "Reserved one bounded AI investigation request",
            data: new
            {
                MaxPerIncident = Math.Max(1, _aiOptions.MaxInvestigationsPerIncident),
                MaxPerHour = Math.Max(1, _aiOptions.MaxInvestigationsPerHour)
            });
        await _db.SaveChangesAsync(cancellationToken);

        InvestigationResultDto result;

        try
        {
            result = await _ai.InvestigateAsync(package, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AiUnavailableException ex)
        {
            // The incident stays in Investigating rather than moving to Failed: the detection is
            // still valid, only the explanation is missing. An operator (or a later retry) can
            // re-run the investigation without losing the incident.
            incident.FailureReason = Redaction.Scrub(ex.Message);
            incident.UpdatedAt = DateTime.UtcNow;

            _audit.Record(incident, IncidentEventTypes.Failed, "ai-orchestrator",
                result: "ai-unavailable",
                message: "AI investigation could not be completed; the incident remains open for retry.",
                error: ex.Message);

            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogWarning("AI investigation unavailable for {Key}: {Reason}",
                incident.IncidentKey, Redaction.Describe(ex));
            return;
        }
        catch (Exception ex)
        {
            incident.FailureReason = Redaction.Describe(ex);
            _audit.Record(incident, IncidentEventTypes.Failed, "ai-orchestrator",
                result: "ai-error", error: Redaction.Describe(ex));
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogError(ex, "Unexpected error during AI investigation for {Key}", incident.IncidentKey);
            return;
        }

        ApplyDiagnosis(incident, result);
        ApplyPrediction(incident, result);
        await CreateRecommendationsAsync(incident, result, cancellationToken);
    }

    private async Task<string?> GetAiBudgetFailureAsync(Guid incidentId, CancellationToken cancellationToken)
    {
        var perIncidentLimit = Math.Max(1, _aiOptions.MaxInvestigationsPerIncident);
        var incidentAttempts = await _db.IncidentEvents.CountAsync(e =>
            e.IncidentId == incidentId && e.EventType == IncidentEventTypes.AiRequestStarted,
            cancellationToken);

        if (incidentAttempts >= perIncidentLimit)
            return $"AI investigation budget exhausted for this incident ({perIncidentLimit} request limit).";

        var hourlyLimit = Math.Max(1, _aiOptions.MaxInvestigationsPerHour);
        var since = DateTime.UtcNow.AddHours(-1);
        var hourlyAttempts = await _db.IncidentEvents.CountAsync(e =>
            e.EventType == IncidentEventTypes.AiRequestStarted && e.Timestamp >= since,
            cancellationToken);

        return hourlyAttempts >= hourlyLimit
            ? $"AI investigation budget exhausted for the current hour ({hourlyLimit} request limit)."
            : null;
    }

    private async Task RecordBudgetFailureAsync(
        SreIncident incident,
        IncidentStatus previous,
        string reason,
        CancellationToken cancellationToken)
    {
        incident.FailureReason = reason;
        incident.UpdatedAt = DateTime.UtcNow;
        _audit.Record(incident, IncidentEventTypes.AiBudgetExceeded, "ai-orchestrator",
            previousState: previous.ToString(),
            newState: incident.Status.ToString(),
            result: "request-blocked",
            message: reason);
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogWarning("Blocked AI investigation for {Key}: {Reason}", incident.IncidentKey, reason);
    }

    /// <summary>
    /// Sends an already-diagnosed incident back to Investigating so it can be re-examined against
    /// the evidence it has accumulated since.
    ///
    /// Refused outright once any remediation has been approved or has run: at that point the
    /// environment has been touched, and rewinding the incident's state would misrepresent what
    /// actually happened. Returns false when the re-investigation is not permitted.
    /// </summary>
    private async Task<bool> TryBeginReInvestigationAsync(SreIncident incident, CancellationToken cancellationToken)
    {
        var inFlight = incident.Actions.Any(a =>
            a.Status is RemediationStatus.Approved
                     or RemediationStatus.Executing
                     or RemediationStatus.Executed);

        if (inFlight)
        {
            _logger.LogInformation(
                "Refusing to re-investigate {Key}: a remediation has already been approved or executed",
                incident.IncidentKey);
            return false;
        }

        // Pending proposals were derived from the stale diagnosis, so they are withdrawn rather
        // than left approvable alongside a fresh set.
        foreach (var stale in incident.Actions.Where(a =>
                     a.Status is RemediationStatus.AwaitingApproval or RemediationStatus.Proposed))
        {
            stale.Status = RemediationStatus.Cancelled;
            stale.RejectionReason = "Withdrawn: the incident was re-investigated against newer evidence.";
        }

        incident.RemediationState = RemediationStatus.None;

        var previous = IncidentLifecycle.Transition(incident, IncidentStatus.Investigating);

        _audit.Record(incident, IncidentEventTypes.Investigating, "operator",
            previousState: previous.ToString(),
            newState: incident.Status.ToString(),
            message: $"Re-investigating against {incident.SignalCount} correlated signal(s)");

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Investigating -> Diagnosed.</summary>
    private void ApplyDiagnosis(SreIncident incident, InvestigationResultDto result)
    {
        incident.Summary = result.Summary;
        incident.RootCause = result.RootCause;
        incident.ContributingFactorsJson = SreJson.Serialize(result.ContributingFactors);
        incident.Confidence = result.Confidence;
        incident.FailureReason = null;

        // The diagnosis now reflects every signal correlated so far.
        incident.DiagnosisStale = false;

        if (result.AffectedComponents.Count > 0 && string.IsNullOrWhiteSpace(incident.AffectedComponent))
            incident.AffectedComponent = result.AffectedComponents[0];

        var previous = IncidentLifecycle.Transition(incident, IncidentStatus.Diagnosed);

        _audit.Record(incident, IncidentEventTypes.Diagnosed, "ai-orchestrator",
            previousState: previous.ToString(),
            newState: incident.Status.ToString(),
            message: result.RootCause,
            data: new
            {
                confidence = result.Confidence,
                provider = result.Provider,
                model = result.Model,
                contributingFactors = result.ContributingFactors,
                evidence = result.Evidence
            });
    }

    /// <summary>Diagnosed -> Predicted.</summary>
    private void ApplyPrediction(SreIncident incident, InvestigationResultDto result)
    {
        incident.PredictedImpact = result.PredictedFailure;
        incident.PredictedRisk = result.EstimatedRisk;

        var previous = IncidentLifecycle.Transition(incident, IncidentStatus.Predicted);

        _audit.Record(incident, IncidentEventTypes.Predicted, "ai-orchestrator",
            previousState: previous.ToString(),
            newState: incident.Status.ToString(),
            message: result.PredictedFailure,
            data: new { risk = result.EstimatedRisk });
    }

    /// <summary>
    /// Predicted -> RecommendationReady -> AwaitingApproval. Each AI recommendation is validated
    /// against policy here; anything unregistered is recorded but never becomes approvable.
    /// </summary>
    private async Task CreateRecommendationsAsync(
        SreIncident incident,
        InvestigationResultDto result,
        CancellationToken cancellationToken)
    {
        var annotated = new List<RecommendationDto>();
        var approvable = new List<RemediationAction>();

        foreach (var recommendation in result.Recommendations)
        {
            var risk = ParseRisk(recommendation.RiskLevel);
            var decision = _policy.ValidateProposal(incident, recommendation.Action, risk);

            annotated.Add(new RecommendationDto
            {
                Action = recommendation.Action,
                Reason = recommendation.Reason,
                ExpectedOutcome = recommendation.ExpectedOutcome,
                RiskLevel = risk.ToString(),
                IsRegisteredTool = _tools.Contains(recommendation.Action),
                PolicyNote = decision.Allowed ? null : decision.Reason
            });

            if (!decision.Allowed)
            {
                _audit.Record(incident, IncidentEventTypes.PolicyEvaluated, "policy",
                    result: "denied",
                    message: $"Recommendation '{recommendation.Action}' refused: {decision.Reason}");
                continue;
            }

            var action = new RemediationAction
            {
                IncidentId = incident.Id,
                ActionKey = await _keys.NextActionKeyAsync(cancellationToken),
                ActionType = recommendation.Action,
                Reason = recommendation.Reason,
                ExpectedOutcome = recommendation.ExpectedOutcome,
                RiskLevel = risk,
                RequiresApproval = _remediationOptions.RequireApprovalForEveryAction,
                Status = RemediationStatus.Proposed,
                Source = "ai",
                PolicyDecision = $"{decision.Code}: {decision.Reason}",
                ParametersJson = SreJson.Serialize(recommendation.Parameters ?? new Dictionary<string, string>())
            };

            incident.Actions.Add(action);
            _db.RemediationActions.Add(action);
            approvable.Add(action);
        }

        incident.RecommendationsJson = SreJson.Serialize(annotated);

        var previous = IncidentLifecycle.Transition(incident, IncidentStatus.RecommendationReady);
        _audit.Record(incident, IncidentEventTypes.Recommended, "ai-orchestrator",
            previousState: previous.ToString(),
            newState: incident.Status.ToString(),
            message: $"{annotated.Count} recommendation(s), {approvable.Count} executable",
            data: annotated);

        if (approvable.Count == 0)
        {
            // A diagnosis with nothing safe to run is a legitimate outcome, not a failure. The
            // incident is left for a human with the analysis attached.
            incident.RemediationState = RemediationStatus.None;
            incident.FailureReason = annotated.Count == 0
                ? "The AI produced no recommendation for this incident."
                : "No recommended action passed policy validation; operator action is required.";

            _audit.Record(incident, IncidentEventTypes.PolicyEvaluated, "policy",
                result: "no-executable-action",
                message: incident.FailureReason);

            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        foreach (var action in approvable)
            action.Status = RemediationStatus.AwaitingApproval;

        incident.RemediationState = RemediationStatus.AwaitingApproval;

        var beforeApproval = IncidentLifecycle.Transition(incident, IncidentStatus.AwaitingApproval);
        _audit.Record(incident, IncidentEventTypes.AwaitingApproval, "ai-orchestrator",
            previousState: beforeApproval.ToString(),
            newState: incident.Status.ToString(),
            message: $"Awaiting operator approval for {approvable.Count} action(s)");

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<RemediationAction?> ApproveAsync(
        Guid incidentId,
        Guid actionId,
        string approvedBy,
        string? note,
        CancellationToken cancellationToken = default)
    {
        var incident = await LoadAsync(incidentId, cancellationToken);
        if (incident is null)
            return null;

        var action = incident.Actions.FirstOrDefault(a => a.Id == actionId);
        if (action is null)
            return null;

        if (incident.Status != IncidentStatus.AwaitingApproval)
            throw new InvalidIncidentTransitionException(incident.Status, IncidentStatus.Remediating);

        if (action.Status != RemediationStatus.AwaitingApproval)
        {
            throw new InvalidOperationException(
                $"Action {action.ActionKey} is {action.Status} and cannot be approved.");
        }

        action.Status = RemediationStatus.Approved;
        action.ApprovedBy = approvedBy;
        action.ApprovedAt = DateTime.UtcNow;
        incident.RemediationState = RemediationStatus.Approved;

        _audit.Record(incident, IncidentEventTypes.Approved, $"operator:{approvedBy}",
            actionId: action.ActionKey,
            message: note ?? $"Approved {action.ActionType}");

        // Sibling proposals are withdrawn: approving one action is a decision about how to fix the
        // incident, and leaving the others approvable would invite a second, conflicting fix.
        foreach (var sibling in incident.Actions.Where(a => a.Id != action.Id && a.Status == RemediationStatus.AwaitingApproval))
        {
            sibling.Status = RemediationStatus.Cancelled;
            sibling.RejectionReason = $"Superseded by approved action {action.ActionKey}.";
        }

        var previous = IncidentLifecycle.Transition(incident, IncidentStatus.Remediating);
        _audit.Record(incident, IncidentEventTypes.Executing, "orchestrator",
            previousState: previous.ToString(),
            newState: incident.Status.ToString(),
            actionId: action.ActionKey,
            message: $"Remediating with {action.ActionType}");

        await _db.SaveChangesAsync(cancellationToken);

        // Execute + verify happen off the request thread (see ExecuteAndVerifyAsync): the combined
        // wait can run past a minute, and this endpoint returning quickly is what lets the operator
        // watch progress by polling instead of holding a connection open that a client timeout or a
        // closed tab would silently cancel mid-verification.
        var queued = _queue.TryEnqueue(new IncidentWorkItem(
            WorkItemKind.ExecuteRemediation, incident.ProjectId, incident.Environment, incident.Service,
            incident.Id, action.Id));

        if (!queued)
        {
            // The queue only drops under sustained overload (PRD section 21's bounded-queue
            // requirement). Approval is still recorded - nothing unsafe happened - but the operator
            // needs to know execution did not start rather than watching a stalled "Remediating".
            _logger.LogError(
                "Remediation queue is full; {Key} action {Action} approved but not yet queued for execution",
                incident.IncidentKey, action.ActionKey);
        }

        return action;
    }

    /// <summary>
    /// Executes an approved action and verifies recovery (moved off <see cref="ApproveAsync"/> - see
    /// that method and <see cref="WorkItemKind.ExecuteRemediation"/> for why). Reloads the incident
    /// itself since this runs in the worker's own scope, not the approval request's.
    /// </summary>
    public async Task ExecuteAndVerifyAsync(Guid incidentId, Guid actionId, CancellationToken cancellationToken = default)
    {
        var incident = await LoadAsync(incidentId, cancellationToken);
        if (incident is null)
        {
            _logger.LogWarning("ExecuteAndVerifyAsync called for unknown incident {IncidentId}", incidentId);
            return;
        }

        var action = incident.Actions.FirstOrDefault(a => a.Id == actionId);
        if (action is null || action.Status != RemediationStatus.Approved)
        {
            _logger.LogWarning(
                "ExecuteAndVerifyAsync: action {ActionId} on {Key} is not in Approved state (found {Status}); skipping",
                actionId, incident.IncidentKey, action?.Status.ToString() ?? "missing");
            return;
        }

        var result = await _executor.ExecuteAsync(incident, action, cancellationToken);

        if (!result.Success)
        {
            incident.RemediationState = RemediationStatus.Failed;
            incident.FailureReason = Redaction.Scrub(result.Error);

            var beforeFail = IncidentLifecycle.Transition(incident, IncidentStatus.Failed);
            _audit.Record(incident, IncidentEventTypes.Failed, "orchestrator",
                previousState: beforeFail.ToString(),
                newState: incident.Status.ToString(),
                actionId: action.ActionKey,
                error: result.Error);

            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        incident.RemediationState = RemediationStatus.Executed;

        var beforeVerify = IncidentLifecycle.Transition(incident, IncidentStatus.Verifying);
        _audit.Record(incident, IncidentEventTypes.Verifying, "orchestrator",
            previousState: beforeVerify.ToString(),
            newState: incident.Status.ToString(),
            actionId: action.ActionKey,
            message: "Verifying recovery against fresh telemetry");

        await _db.SaveChangesAsync(cancellationToken);

        var verification = await _verification.VerifyAsync(incident, action, cancellationToken);

        if (verification.Status == VerificationStatus.Passed)
        {
            var beforeResolve = IncidentLifecycle.Transition(incident, IncidentStatus.Resolved);
            incident.FailureReason = null;

            _audit.Record(incident, IncidentEventTypes.Resolved, "orchestrator",
                previousState: beforeResolve.ToString(),
                newState: incident.Status.ToString(),
                actionId: action.ActionKey,
                result: "resolved",
                message: verification.Summary);
        }
        else
        {
            // Verification failure hands the incident back to the operator with the remaining
            // options, rather than quietly declaring success (frontend PRD section 13).
            incident.FailureReason = verification.Summary;

            var remaining = incident.Actions
                .Where(a => a.Status == RemediationStatus.Cancelled && a.Id != action.Id)
                .ToList();

            foreach (var candidate in remaining)
            {
                candidate.Status = RemediationStatus.AwaitingApproval;
                candidate.RejectionReason = null;
            }

            // With another action still on the table the operator gets to choose again; with none
            // left there is nothing to approve, so the incident fails rather than parking forever
            // in a state whose only affordance is an empty list.
            var nextStatus = remaining.Count > 0
                ? IncidentStatus.AwaitingApproval
                : IncidentStatus.Failed;

            incident.RemediationState = remaining.Count > 0
                ? RemediationStatus.AwaitingApproval
                : RemediationStatus.Failed;

            var beforeReturn = IncidentLifecycle.Transition(incident, nextStatus);
            _audit.Record(incident, IncidentEventTypes.Failed, "orchestrator",
                previousState: beforeReturn.ToString(),
                newState: incident.Status.ToString(),
                actionId: action.ActionKey,
                result: verification.Status.ToString(),
                message: verification.Summary);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<RemediationAction?> RejectAsync(
        Guid incidentId,
        Guid actionId,
        string rejectedBy,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var incident = await LoadAsync(incidentId, cancellationToken);
        if (incident is null)
            return null;

        var action = incident.Actions.FirstOrDefault(a => a.Id == actionId);
        if (action is null)
            return null;

        if (action.Status != RemediationStatus.AwaitingApproval)
        {
            throw new InvalidOperationException(
                $"Action {action.ActionKey} is {action.Status} and cannot be rejected.");
        }

        action.Status = RemediationStatus.Rejected;
        action.RejectedBy = rejectedBy;
        action.RejectedAt = DateTime.UtcNow;
        action.RejectionReason = reason;

        _audit.Record(incident, IncidentEventTypes.Rejected, $"operator:{rejectedBy}",
            actionId: action.ActionKey,
            message: reason ?? $"Rejected {action.ActionType}");

        var stillPending = incident.Actions.Any(a => a.Status == RemediationStatus.AwaitingApproval);

        if (!stillPending)
        {
            incident.RemediationState = RemediationStatus.Rejected;
            incident.FailureReason = reason ?? "All proposed remediations were rejected by an operator.";

            var previous = IncidentLifecycle.Transition(incident, IncidentStatus.Rejected);
            _audit.Record(incident, IncidentEventTypes.Rejected, $"operator:{rejectedBy}",
                previousState: previous.ToString(),
                newState: incident.Status.ToString(),
                message: "No remaining approvable actions; incident closed as rejected.");
        }

        await _db.SaveChangesAsync(cancellationToken);
        return action;
    }

    public async Task CancelAsync(
        Guid incidentId,
        string cancelledBy,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var incident = await LoadAsync(incidentId, cancellationToken);
        if (incident is null)
            return;

        if (incident.IsTerminal)
            return;

        foreach (var action in incident.Actions.Where(a =>
                     a.Status is RemediationStatus.AwaitingApproval or RemediationStatus.Proposed))
        {
            action.Status = RemediationStatus.Cancelled;
            action.RejectionReason = reason ?? "Incident cancelled.";
        }

        incident.RemediationState = RemediationStatus.Cancelled;
        incident.FailureReason = reason ?? "Cancelled by operator.";

        var previous = IncidentLifecycle.Transition(incident, IncidentStatus.Cancelled);
        _audit.Record(incident, IncidentEventTypes.Cancelled, $"operator:{cancelledBy}",
            previousState: previous.ToString(),
            newState: incident.Status.ToString(),
            message: reason ?? "Cancelled by operator");

        await _db.SaveChangesAsync(cancellationToken);
    }

    // Same reasoning as IncidentQueryService.GetAsync: three collection navigations on one row is
    // exactly the shape EF Core's MultipleCollectionIncludeWarning exists for. AsSplitQuery avoids
    // the cartesian JOIN; safe here too - a single incident loaded by id, tracked so the pipeline's
    // subsequent SaveChangesAsync still works exactly as before (split-query only changes how the
    // initial read is executed, never how the change tracker treats the result).
    private Task<SreIncident?> LoadAsync(Guid incidentId, CancellationToken cancellationToken) =>
        _db.SreIncidents
            .AsSplitQuery()
            .Include(i => i.Actions)
            .Include(i => i.Events)
            .Include(i => i.Verifications)
            .FirstOrDefaultAsync(i => i.Id == incidentId, cancellationToken);

    private static RiskLevel ParseRisk(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "low" => RiskLevel.Low,
        "medium" or "moderate" => RiskLevel.Medium,
        "high" => RiskLevel.High,
        "critical" => RiskLevel.Critical,
        // An unrecognised risk label is treated as high, never as low: an unknown risk is the one
        // you least want to auto-permit.
        _ => RiskLevel.High
    };
}
