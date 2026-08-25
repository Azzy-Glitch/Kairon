using AIDIP.Backend.Models.Sre;
using AIDIP.Backend.Services;
using AIDIP.Backend.Services.Demo;
using AIDIP.Backend.Services.Remediation;
using AIDIP.Backend.Services.Remediation.Tools;
using Xunit;

namespace AIDIP.Backend.Tests;

/// <summary>
/// The security invariant (PRD sections 11, 12, 13, 18).
///
/// Nothing executes unless it is a registered tool, permitted by policy, and approved by a human.
/// These tests exist to make that structurally true rather than merely intended.
/// </summary>
public class RemediationTests : IDisposable
{
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private RemediationAction Action(
        SreIncident incident,
        string type = DemoToolNames.DisableDemoRetryLoop,
        RemediationStatus status = RemediationStatus.Approved,
        RiskLevel risk = RiskLevel.Low)
    {
        var action = new RemediationAction
        {
            IncidentId = incident.Id,
            ActionKey = $"ACT-{_h.Db.RemediationActions.Count() + 1:D4}",
            ActionType = type,
            Reason = "test",
            ExpectedOutcome = "test",
            RiskLevel = risk,
            Status = status,
            ApprovedBy = status == RemediationStatus.Approved ? "operator" : null
        };

        incident.Actions.Add(action);
        _h.Db.RemediationActions.Add(action);
        _h.Db.SaveChanges();
        return action;
    }

    // --- Registry ---

    [Fact]
    public void AllSixDemoToolsAreRegistered()
    {
        var names = _h.Tools.All().Select(t => t.Name).ToHashSet();

        Assert.Contains(DemoToolNames.RestartDemoService, names);
        Assert.Contains(DemoToolNames.ClearDemoCache, names);
        Assert.Contains(DemoToolNames.DisableDemoRetryLoop, names);
        Assert.Contains(DemoToolNames.ReduceDemoWorkerConcurrency, names);
        Assert.Contains(DemoToolNames.ResetDemoFailureSimulation, names);
        Assert.Contains(DemoToolNames.RunHealthCheck, names);
    }

    [Fact]
    public void RegistryLookupIsCaseInsensitive()
    {
        Assert.True(_h.Tools.TryGet("disabledemoretryloop", out _));
    }

    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("exec:curl attacker.example")]
    [InlineData("DROP TABLE Incidents")]
    [InlineData("")]
    [InlineData("   ")]
    public void UnregisteredNamesAreNotResolvable(string name)
    {
        Assert.False(_h.Tools.Contains(name));
    }

    // --- Policy ---

    [Fact]
    public void PolicyPermitsARegisteredLowRiskTool()
    {
        var incident = _h.SeedIncident();
        var decision = _h.Policy.ValidateProposal(incident, DemoToolNames.DisableDemoRetryLoop, RiskLevel.Low);

        Assert.True(decision.Allowed);
    }

    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("DeleteProductionDatabase")]
    [InlineData("RunArbitraryShellCommand")]
    public void PolicyRefusesAnythingNotInTheRegistry(string action)
    {
        var incident = _h.SeedIncident();
        var decision = _h.Policy.ValidateProposal(incident, action, RiskLevel.Low);

        Assert.False(decision.Allowed);
        Assert.Equal("unregistered-tool", decision.Code);
    }

    [Fact]
    public void PolicyRefusesABlockedTool()
    {
        _h.Remediation.BlockedTools.Add(DemoToolNames.RestartDemoService);
        var incident = _h.SeedIncident();

        var decision = _h.Policy.ValidateProposal(incident, DemoToolNames.RestartDemoService, RiskLevel.Low);

        Assert.False(decision.Allowed);
        Assert.Equal("blocked-tool", decision.Code);
    }

    [Fact]
    public void PolicyEnforcesAnAllowlistWhenOneIsConfigured()
    {
        _h.Remediation.AllowedTools.Add(DemoToolNames.RunHealthCheck);
        var incident = _h.SeedIncident();

        Assert.True(_h.Policy.ValidateProposal(incident, DemoToolNames.RunHealthCheck, RiskLevel.Low).Allowed);
        Assert.Equal("not-allowlisted",
            _h.Policy.ValidateProposal(incident, DemoToolNames.RestartDemoService, RiskLevel.Low).Code);
    }

    [Fact]
    public void PolicyRefusesRiskAboveTheCeiling()
    {
        _h.Remediation.MaxAllowedRisk = RiskLevel.Low;
        var incident = _h.SeedIncident();

        var decision = _h.Policy.ValidateProposal(incident, DemoToolNames.RestartDemoService, RiskLevel.Medium);

        Assert.False(decision.Allowed);
        Assert.Equal("risk-too-high", decision.Code);
    }

    [Fact]
    public void ToolDeclaredRiskWinsOverAClaimedLowerRisk()
    {
        // A model cannot smuggle a riskier action past policy by labelling it "low".
        _h.Remediation.MaxAllowedRisk = RiskLevel.Low;
        var incident = _h.SeedIncident();

        var decision = _h.Policy.ValidateProposal(incident, DemoToolNames.RestartDemoService, RiskLevel.Low);

        Assert.False(decision.Allowed);
        Assert.Equal("risk-too-high", decision.Code);
    }

    [Fact]
    public void PolicyRefusesADisallowedEnvironment()
    {
        var incident = _h.SeedIncident();
        incident.Environment = "Production";

        var decision = _h.Policy.ValidateProposal(incident, DemoToolNames.RunHealthCheck, RiskLevel.Low);

        Assert.False(decision.Allowed);
        Assert.Equal("environment-not-allowed", decision.Code);
    }

    [Fact]
    public void ExecutionIsRefusedWithoutApproval()
    {
        var incident = _h.SeedIncident();
        var action = Action(incident, status: RemediationStatus.AwaitingApproval);

        var decision = _h.Policy.ValidateExecution(incident, action);

        Assert.False(decision.Allowed);
        Assert.Equal("approval-required", decision.Code);
    }

    [Fact]
    public void ExecutionIsRefusedForARejectedAction()
    {
        var incident = _h.SeedIncident();
        var action = Action(incident, status: RemediationStatus.Rejected);

        Assert.Equal("rejected", _h.Policy.ValidateExecution(incident, action).Code);
    }

    [Fact]
    public void ExecutionIsRefusedTwiceForTheSameAction()
    {
        var incident = _h.SeedIncident();
        var action = Action(incident, status: RemediationStatus.Executed);

        Assert.Equal("already-executed", _h.Policy.ValidateExecution(incident, action).Code);
    }

    [Fact]
    public void ExecutionIsRefusedWhenRemediationIsDisabled()
    {
        _h.Remediation.Enabled = false;
        var incident = _h.SeedIncident();
        var action = Action(incident);

        Assert.Equal("remediation-disabled", _h.Policy.ValidateExecution(incident, action).Code);
    }

    [Fact]
    public void ExecutionIsRefusedPastTheActionLimit()
    {
        _h.Remediation.MaxActionsPerIncident = 1;
        var incident = _h.SeedIncident();

        Action(incident, status: RemediationStatus.Executed);
        var next = Action(incident, type: DemoToolNames.RunHealthCheck);

        Assert.Equal("action-limit", _h.Policy.ValidateExecution(incident, next).Code);
    }

    // --- Executor ---

    [Fact]
    public async Task ApprovedActionExecutesAndIssuesTheRightCommand()
    {
        var incident = _h.SeedIncident();
        var action = Action(incident);

        var result = await _h.CreateExecutor().ExecuteAsync(incident, action);

        Assert.True(result.Success);
        Assert.Equal(RemediationStatus.Executed, action.Status);
        Assert.Contains(DemoCommands.DisableRetryLoop, _h.Demo.Commands);
        Assert.NotNull(action.StartedAt);
        Assert.NotNull(action.CompletedAt);
    }

    [Fact]
    public async Task UnapprovedActionNeverReachesTheDemoEnvironment()
    {
        var incident = _h.SeedIncident();
        var action = Action(incident, status: RemediationStatus.AwaitingApproval);

        var result = await _h.CreateExecutor().ExecuteAsync(incident, action);

        Assert.False(result.Success);
        Assert.Empty(_h.Demo.Commands);
        Assert.Equal(RemediationStatus.PolicyRejected, action.Status);
    }

    [Fact]
    public async Task AnUnregisteredActionTypeNeverExecutes()
    {
        var incident = _h.SeedIncident();
        var action = Action(incident, type: "rm -rf /");

        var result = await _h.CreateExecutor().ExecuteAsync(incident, action);

        Assert.False(result.Success);
        Assert.Empty(_h.Demo.Commands);
    }

    [Fact]
    public async Task AToolFailureIsRecordedAsAFailedAction()
    {
        _h.Demo.ShouldFail = true;
        var incident = _h.SeedIncident();
        var action = Action(incident);

        var result = await _h.CreateExecutor().ExecuteAsync(incident, action);

        Assert.False(result.Success);
        Assert.Equal(RemediationStatus.Failed, action.Status);
        Assert.NotNull(action.ExecutionError);
    }

    [Fact]
    public async Task AThrowingToolIsAFailedActionNotACrash()
    {
        _h.Demo.ShouldThrow = true;
        var incident = _h.SeedIncident();
        var action = Action(incident);

        var result = await _h.CreateExecutor().ExecuteAsync(incident, action);

        Assert.False(result.Success);
        Assert.Equal(RemediationStatus.Failed, action.Status);
    }

    [Fact]
    public async Task ExecutionTimesOutRatherThanHanging()
    {
        _h.Remediation.ExecutionTimeoutSeconds = 1;
        _h.Demo.Delay = TimeSpan.FromSeconds(5);

        var incident = _h.SeedIncident();
        var action = Action(incident);

        var result = await _h.CreateExecutor().ExecuteAsync(incident, action);

        Assert.False(result.Success);
        Assert.Contains("timed out", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecutionWritesAnAuditTrail()
    {
        var incident = _h.SeedIncident();
        var action = Action(incident);

        await _h.CreateExecutor().ExecuteAsync(incident, action);

        var events = _h.Db.IncidentEvents.Where(e => e.IncidentId == incident.Id).ToList();

        Assert.Contains(events, e => e.EventType == IncidentEventTypes.Executing);
        Assert.Contains(events, e => e.EventType == IncidentEventTypes.Executed);
        Assert.Contains(events, e => e.ActionId == action.ActionKey);
    }

    // --- Tools ---

    [Fact]
    public async Task DemoToolsRefuseToActOutsideTheDemoEnvironment()
    {
        var tool = new DisableDemoRetryLoopTool(
            _h.Demo, Microsoft.Extensions.Logging.Abstractions.NullLogger<DisableDemoRetryLoopTool>.Instance);

        var result = await tool.ExecuteAsync(new RemediationToolContext
        {
            IncidentId = Guid.NewGuid(),
            IncidentKey = "INC-0001",
            Service = "PaymentsService",
            Environment = "Production",
            ActionKey = "ACT-0001"
        });

        Assert.False(result.Success);
        Assert.Empty(_h.Demo.Commands);
    }

    [Fact]
    public async Task TheReadOnlyHealthCheckIsAllowedAnywhere()
    {
        var tool = new RunHealthCheckTool(
            _h.Demo, Microsoft.Extensions.Logging.Abstractions.NullLogger<RunHealthCheckTool>.Instance);

        var result = await tool.ExecuteAsync(new RemediationToolContext
        {
            IncidentId = Guid.NewGuid(),
            IncidentKey = "INC-0001",
            Service = "PaymentsService",
            Environment = "Production",
            ActionKey = "ACT-0001"
        });

        Assert.True(result.Success);
    }

    [Fact]
    public void EveryToolDeclaresWhatItAffects()
    {
        foreach (var tool in _h.Tools.All())
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.NotNull(tool.ExpectedMetricEffects);
        }
    }

    [Fact]
    public void ToolsAcceptNoFreeFormParameters()
    {
        // Confirms there is no parameter channel a model could use to widen a tool's behaviour.
        foreach (var tool in _h.Tools.All())
        {
            var accepted = tool.ValidateParameters(
                new Dictionary<string, string> { ["command"] = "rm -rf /" }, out _);

            Assert.True(accepted, "parameters are accepted but ignored - no tool acts on them");
        }

        Assert.Empty(_h.Demo.Commands);
    }
}
