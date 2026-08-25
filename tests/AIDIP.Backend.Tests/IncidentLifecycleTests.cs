using AIDIP.Backend.Models.Sre;
using Xunit;

namespace AIDIP.Backend.Tests;

/// <summary>
/// Lifecycle state machine (PRD section 5 and 18). The requirement being tested is blunt:
/// "Invalid state transitions must be rejected."
/// </summary>
public class IncidentLifecycleTests
{
    [Theory]
    [InlineData(IncidentStatus.Detected, IncidentStatus.Investigating)]
    [InlineData(IncidentStatus.Investigating, IncidentStatus.Diagnosed)]
    [InlineData(IncidentStatus.Diagnosed, IncidentStatus.Predicted)]
    [InlineData(IncidentStatus.Predicted, IncidentStatus.RecommendationReady)]
    [InlineData(IncidentStatus.RecommendationReady, IncidentStatus.AwaitingApproval)]
    [InlineData(IncidentStatus.AwaitingApproval, IncidentStatus.Remediating)]
    [InlineData(IncidentStatus.Remediating, IncidentStatus.Verifying)]
    [InlineData(IncidentStatus.Verifying, IncidentStatus.Resolved)]
    public void HappyPathTransitionsAreAllowed(IncidentStatus from, IncidentStatus to)
    {
        Assert.True(IncidentLifecycle.CanTransition(from, to));
    }

    [Theory]
    [InlineData(IncidentStatus.Detected, IncidentStatus.Resolved)]
    [InlineData(IncidentStatus.Detected, IncidentStatus.Remediating)]
    [InlineData(IncidentStatus.Investigating, IncidentStatus.Resolved)]
    [InlineData(IncidentStatus.Investigating, IncidentStatus.AwaitingApproval)]
    [InlineData(IncidentStatus.Diagnosed, IncidentStatus.Remediating)]
    [InlineData(IncidentStatus.Predicted, IncidentStatus.Verifying)]
    [InlineData(IncidentStatus.AwaitingApproval, IncidentStatus.Resolved)]
    [InlineData(IncidentStatus.Remediating, IncidentStatus.Resolved)]
    public void SkippingLifecycleStagesIsRejected(IncidentStatus from, IncidentStatus to)
    {
        Assert.False(IncidentLifecycle.CanTransition(from, to));
    }

    [Fact]
    public void RemediatingCannotBeReachedWithoutPassingThroughApproval()
    {
        // The single most important negative case: nothing executes without an approval step.
        foreach (var from in Enum.GetValues<IncidentStatus>())
        {
            if (from == IncidentStatus.AwaitingApproval)
                continue;

            Assert.False(
                IncidentLifecycle.CanTransition(from, IncidentStatus.Remediating),
                $"{from} must not be able to reach Remediating directly");
        }
    }

    [Theory]
    [InlineData(IncidentStatus.Resolved)]
    [InlineData(IncidentStatus.Failed)]
    [InlineData(IncidentStatus.Rejected)]
    [InlineData(IncidentStatus.Cancelled)]
    public void TerminalStatesAcceptNoFurtherTransitions(IncidentStatus terminal)
    {
        Assert.True(IncidentLifecycle.IsTerminal(terminal));
        Assert.Empty(IncidentLifecycle.NextStates(terminal));

        foreach (var to in Enum.GetValues<IncidentStatus>())
            Assert.False(IncidentLifecycle.CanTransition(terminal, to));
    }

    [Theory]
    [InlineData(IncidentStatus.Detected)]
    [InlineData(IncidentStatus.Investigating)]
    [InlineData(IncidentStatus.Diagnosed)]
    [InlineData(IncidentStatus.Predicted)]
    [InlineData(IncidentStatus.RecommendationReady)]
    [InlineData(IncidentStatus.AwaitingApproval)]
    [InlineData(IncidentStatus.Remediating)]
    [InlineData(IncidentStatus.Verifying)]
    public void FailureAndCancellationAreReachableFromEveryWorkingState(IncidentStatus from)
    {
        Assert.True(IncidentLifecycle.CanTransition(from, IncidentStatus.Failed));
        Assert.True(IncidentLifecycle.CanTransition(from, IncidentStatus.Cancelled));
    }

    [Fact]
    public void RejectionIsOnlyReachableFromAwaitingApproval()
    {
        Assert.True(IncidentLifecycle.CanTransition(IncidentStatus.AwaitingApproval, IncidentStatus.Rejected));
        Assert.False(IncidentLifecycle.CanTransition(IncidentStatus.Detected, IncidentStatus.Rejected));
        Assert.False(IncidentLifecycle.CanTransition(IncidentStatus.Remediating, IncidentStatus.Rejected));
    }

    [Fact]
    public void VerificationFailureCanReturnToApproval()
    {
        Assert.True(IncidentLifecycle.CanTransition(IncidentStatus.Verifying, IncidentStatus.AwaitingApproval));
    }

    [Fact]
    public void RecommendationReadyCanResolveWhenThereIsNothingSafeToRun()
    {
        Assert.True(IncidentLifecycle.CanTransition(IncidentStatus.RecommendationReady, IncidentStatus.Resolved));
    }

    [Fact]
    public void TransitionUpdatesTheIncidentAndReturnsThePreviousState()
    {
        var incident = new SreIncident { Status = IncidentStatus.Detected };
        var before = incident.UpdatedAt;

        var previous = IncidentLifecycle.Transition(incident, IncidentStatus.Investigating);

        Assert.Equal(IncidentStatus.Detected, previous);
        Assert.Equal(IncidentStatus.Investigating, incident.Status);
        Assert.True(incident.UpdatedAt >= before);
    }

    [Fact]
    public void TransitionToResolvedStampsResolvedAt()
    {
        var incident = new SreIncident { Status = IncidentStatus.Verifying };

        IncidentLifecycle.Transition(incident, IncidentStatus.Resolved);

        Assert.NotNull(incident.ResolvedAt);
        Assert.True(incident.IsTerminal);
        Assert.False(incident.IsOpen);
    }

    [Fact]
    public void InvalidTransitionThrowsAndLeavesTheIncidentUntouched()
    {
        var incident = new SreIncident { Status = IncidentStatus.Detected };

        var ex = Assert.Throws<InvalidIncidentTransitionException>(
            () => IncidentLifecycle.Transition(incident, IncidentStatus.Resolved));

        Assert.Equal(IncidentStatus.Detected, ex.From);
        Assert.Equal(IncidentStatus.Resolved, ex.To);
        Assert.Equal(IncidentStatus.Detected, incident.Status);
    }

    [Fact]
    public void TransitionRequiresAnIncident()
    {
        Assert.Throws<ArgumentNullException>(() => IncidentLifecycle.Transition(null!, IncidentStatus.Failed));
    }

    [Fact]
    public void NextStatesAlwaysIncludeTheFailurePaths()
    {
        var next = IncidentLifecycle.NextStates(IncidentStatus.Diagnosed);

        Assert.Contains(IncidentStatus.Predicted, next);
        Assert.Contains(IncidentStatus.Failed, next);
        Assert.Contains(IncidentStatus.Cancelled, next);
    }

    [Fact]
    public void ARepeatedDetectionIsNotTreatedAsATransition()
    {
        // Correlation folds new signals into an already-detected incident, so Detected -> Detected
        // has to stay legal while every other self-transition does not.
        Assert.True(IncidentLifecycle.CanTransition(IncidentStatus.Detected, IncidentStatus.Detected));
        Assert.False(IncidentLifecycle.CanTransition(IncidentStatus.Investigating, IncidentStatus.Investigating));
    }
}
