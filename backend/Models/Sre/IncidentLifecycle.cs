namespace AIDIP.Backend.Models.Sre;

/// <summary>
/// Raised when a caller attempts a lifecycle transition the state machine forbids (PRD section 5:
/// "Invalid state transitions must be rejected").
/// </summary>
public class InvalidIncidentTransitionException : InvalidOperationException
{
    public IncidentStatus From { get; }
    public IncidentStatus To { get; }

    public InvalidIncidentTransitionException(IncidentStatus from, IncidentStatus to)
        : base($"Invalid incident transition {from} -> {to}.")
    {
        From = from;
        To = to;
    }
}

/// <summary>
/// The single source of truth for incident state transitions. Everything that moves an incident
/// (orchestrator, controllers, remediation executor) goes through here, so an illegal move is
/// impossible rather than merely discouraged.
/// </summary>
public static class IncidentLifecycle
{
    /// <summary>Failure paths reachable from any non-terminal state (PRD section 5).</summary>
    private static readonly IncidentStatus[] FailurePaths =
    {
        IncidentStatus.Failed,
        IncidentStatus.Cancelled
    };

    private static readonly IReadOnlyDictionary<IncidentStatus, IncidentStatus[]> Allowed =
        new Dictionary<IncidentStatus, IncidentStatus[]>
        {
            [IncidentStatus.Detected] = new[]
            {
                IncidentStatus.Investigating,
                // Correlation can re-open detection into a fresh signal on the same incident.
                IncidentStatus.Detected
            },
            [IncidentStatus.Investigating] = new[]
            {
                IncidentStatus.Diagnosed
            },
            [IncidentStatus.Diagnosed] = new[]
            {
                IncidentStatus.Predicted,
                // Re-investigation: see the note on ReInvestigable below.
                IncidentStatus.Investigating
            },
            [IncidentStatus.Predicted] = new[]
            {
                IncidentStatus.RecommendationReady,
                IncidentStatus.Investigating
            },
            [IncidentStatus.RecommendationReady] = new[]
            {
                IncidentStatus.AwaitingApproval,
                // A diagnosis with no safe recommendation ends here rather than pretending.
                IncidentStatus.Resolved,
                IncidentStatus.Investigating
            },
            [IncidentStatus.AwaitingApproval] = new[]
            {
                IncidentStatus.Remediating,
                IncidentStatus.Rejected,
                IncidentStatus.Investigating
            },
            [IncidentStatus.Remediating] = new[]
            {
                IncidentStatus.Verifying
            },
            [IncidentStatus.Verifying] = new[]
            {
                IncidentStatus.Resolved,
                // Verification failure returns to the operator for another decision.
                IncidentStatus.AwaitingApproval
            },
            // Terminal states.
            [IncidentStatus.Resolved] = Array.Empty<IncidentStatus>(),
            [IncidentStatus.Failed] = Array.Empty<IncidentStatus>(),
            [IncidentStatus.Rejected] = Array.Empty<IncidentStatus>(),
            [IncidentStatus.Cancelled] = Array.Empty<IncidentStatus>()
        };

    /// <summary>
    /// States an incident may be sent back to Investigating from.
    ///
    /// This is a deliberate backward edge, and the second one in the machine - verification failure
    /// already returns Verifying to AwaitingApproval. It exists because an open incident keeps
    /// absorbing evidence: a diagnosis reached from two signals can end up sitting above eighteen,
    /// and the honest response is to look again rather than to leave a stale conclusion on screen.
    ///
    /// It is only ever taken on an explicit operator request, and the orchestrator refuses it once
    /// any remediation has been approved or executed, so it can never rewind work that has already
    /// touched the environment.
    /// </summary>
    private static readonly IncidentStatus[] ReInvestigable =
    {
        IncidentStatus.Diagnosed,
        IncidentStatus.Predicted,
        IncidentStatus.RecommendationReady,
        IncidentStatus.AwaitingApproval
    };

    public static bool CanReInvestigate(IncidentStatus status) => ReInvestigable.Contains(status);

    public static bool IsTerminal(IncidentStatus status) =>
        status is IncidentStatus.Resolved
               or IncidentStatus.Failed
               or IncidentStatus.Rejected
               or IncidentStatus.Cancelled;

    public static bool CanTransition(IncidentStatus from, IncidentStatus to)
    {
        if (from == to && from != IncidentStatus.Detected)
            return false;

        if (IsTerminal(from))
            return false;

        if (FailurePaths.Contains(to))
            return true;

        return Allowed.TryGetValue(from, out var next) && next.Contains(to);
    }

    public static IReadOnlyList<IncidentStatus> NextStates(IncidentStatus from)
    {
        if (IsTerminal(from))
            return Array.Empty<IncidentStatus>();

        var next = Allowed.TryGetValue(from, out var v) ? v.ToList() : new List<IncidentStatus>();
        next.AddRange(FailurePaths);
        return next.Distinct().ToList();
    }

    /// <summary>
    /// Applies a transition, throwing <see cref="InvalidIncidentTransitionException"/> if the move
    /// is not allowed. Returns the previous status so callers can write an audit entry.
    /// </summary>
    public static IncidentStatus Transition(SreIncident incident, IncidentStatus to)
    {
        ArgumentNullException.ThrowIfNull(incident);

        var from = incident.Status;
        if (!CanTransition(from, to))
            throw new InvalidIncidentTransitionException(from, to);

        incident.Status = to;
        incident.UpdatedAt = DateTime.UtcNow;

        if (to == IncidentStatus.Resolved)
            incident.ResolvedAt = DateTime.UtcNow;

        return from;
    }
}
