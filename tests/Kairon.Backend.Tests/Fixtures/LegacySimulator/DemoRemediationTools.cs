using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services.Demo;

namespace Kairon.Backend.Services.Remediation.Tools;

/// <summary>
/// Shared plumbing for the demo remediation tools (PRD section 11). Every tool is a thin, fixed
/// mapping from a tool name to one closed demo command. There is deliberately no parameter that
/// can widen what a tool does.
/// </summary>
public abstract class DemoRemediationToolBase : IRemediationTool
{
    private readonly IDemoEnvironmentClient _demo;
    private readonly ILogger _logger;

    protected DemoRemediationToolBase(IDemoEnvironmentClient demo, ILogger logger)
    {
        _demo = demo;
        _logger = logger;
    }

    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract RiskLevel RiskLevel { get; }
    public abstract IReadOnlyList<string> ExpectedMetricEffects { get; }

    /// <summary>The single demo command this tool is permitted to issue.</summary>
    protected abstract string Command { get; }

    /// <summary>
    /// Tools take no free-form parameters by default. Overriding this is how a tool opts into a
    /// narrow, explicitly validated parameter, never into arbitrary input.
    /// </summary>
    public virtual bool ValidateParameters(IReadOnlyDictionary<string, string> parameters, out string? error)
    {
        error = null;
        return true;
    }

    /// <summary>
    /// Restricts execution to the controlled demo environment. A tool named after the demo must
    /// never be able to touch anything else, even if policy were misconfigured.
    /// </summary>
    protected virtual bool RequiresDemoEnvironment => true;

    public async Task<RemediationToolResult> ExecuteAsync(
        RemediationToolContext context,
        CancellationToken cancellationToken = default)
    {
        if (RequiresDemoEnvironment &&
            !context.Environment.Equals("Development", StringComparison.OrdinalIgnoreCase) &&
            !context.Environment.Equals("Development", StringComparison.OrdinalIgnoreCase))
        {
            return RemediationToolResult.Fail(
                $"{Name} only operates on the controlled demo environment; incident environment is '{context.Environment}'.");
        }

        if (!ValidateParameters(context.Parameters, out var parameterError))
            return RemediationToolResult.Fail(parameterError ?? "Invalid parameters.");

        _logger.LogInformation(
            "Executing {Tool} for incident {IncidentKey} action {ActionKey}",
            Name, context.IncidentKey, context.ActionKey);

        var result = await _demo.SendAsync(Command, cancellationToken);

        if (!result.Success)
            return RemediationToolResult.Fail(result.Error ?? "Demo environment rejected the command.");

        var details = new Dictionary<string, string>
        {
            ["command"] = Command,
            ["service"] = context.Service,
            ["environment"] = context.Environment
        };

        if (result.State is not null)
        {
            details["phase"] = result.State.Phase;
            details["retryLoopEnabled"] = result.State.RetryLoopEnabled.ToString();
            details["workerConcurrency"] = result.State.WorkerConcurrency.ToString();
        }

        return RemediationToolResult.Ok(result.Message, details);
    }
}

public class RestartDemoServiceTool : DemoRemediationToolBase
{
    public RestartDemoServiceTool(IDemoEnvironmentClient demo, ILogger<RestartDemoServiceTool> logger)
        : base(demo, logger) { }

    public override string Name => DemoToolNames.RestartDemoService;
    public override string Description =>
        "Restarts the controlled demo order-processing service, clearing its in-memory queue and resetting memory.";
    public override RiskLevel RiskLevel => RiskLevel.Medium;
    public override IReadOnlyList<string> ExpectedMetricEffects => new[] { "memory", "queue", "latency" };
    protected override string Command => DemoCommands.RestartService;
}

public class ClearDemoCacheTool : DemoRemediationToolBase
{
    public ClearDemoCacheTool(IDemoEnvironmentClient demo, ILogger<ClearDemoCacheTool> logger)
        : base(demo, logger) { }

    public override string Name => DemoToolNames.ClearDemoCache;
    public override string Description =>
        "Clears and repopulates the demo service cache. Causes a short cold-start latency cost.";
    public override RiskLevel RiskLevel => RiskLevel.Low;
    public override IReadOnlyList<string> ExpectedMetricEffects => new[] { "memory" };
    protected override string Command => DemoCommands.ClearCache;
}

public class DisableDemoRetryLoopTool : DemoRemediationToolBase
{
    public DisableDemoRetryLoopTool(IDemoEnvironmentClient demo, ILogger<DisableDemoRetryLoopTool> logger)
        : base(demo, logger) { }

    public override string Name => DemoToolNames.DisableDemoRetryLoop;
    public override string Description =>
        "Disables the controlled retry loop in the demo order-processing service, stopping repeated downstream calls.";
    public override RiskLevel RiskLevel => RiskLevel.Low;
    public override IReadOnlyList<string> ExpectedMetricEffects =>
        new[] { "retries", "cpu", "latency", "errorRate", "queue" };
    protected override string Command => DemoCommands.DisableRetryLoop;
}

public class ReduceDemoWorkerConcurrencyTool : DemoRemediationToolBase
{
    public ReduceDemoWorkerConcurrencyTool(IDemoEnvironmentClient demo, ILogger<ReduceDemoWorkerConcurrencyTool> logger)
        : base(demo, logger) { }

    public override string Name => DemoToolNames.ReduceDemoWorkerConcurrency;
    public override string Description =>
        "Halves the demo service worker concurrency to relieve CPU pressure. Does not address a retry loop.";
    public override RiskLevel RiskLevel => RiskLevel.Medium;
    public override IReadOnlyList<string> ExpectedMetricEffects => new[] { "cpu" };
    protected override string Command => DemoCommands.ReduceConcurrency;
}

public class ResetDemoFailureSimulationTool : DemoRemediationToolBase
{
    public ResetDemoFailureSimulationTool(IDemoEnvironmentClient demo, ILogger<ResetDemoFailureSimulationTool> logger)
        : base(demo, logger) { }

    public override string Name => DemoToolNames.ResetDemoFailureSimulation;
    public override string Description =>
        "Resets the demo failure simulation to its healthy baseline, clearing every injected fault.";
    public override RiskLevel RiskLevel => RiskLevel.Low;
    public override IReadOnlyList<string> ExpectedMetricEffects =>
        new[] { "cpu", "memory", "latency", "errorRate", "retries", "queue" };
    protected override string Command => DemoCommands.ResetFailureSimulation;
}

/// <summary>
/// Read-only probe. Safe enough that it is the fallback recommendation whenever the evidence does
/// not justify a targeted action.
/// </summary>
public class RunHealthCheckTool : DemoRemediationToolBase
{
    public RunHealthCheckTool(IDemoEnvironmentClient demo, ILogger<RunHealthCheckTool> logger)
        : base(demo, logger) { }

    public override string Name => DemoToolNames.RunHealthCheck;
    public override string Description =>
        "Runs a read-only health check against the affected service and reports its current state.";
    public override RiskLevel RiskLevel => RiskLevel.Low;
    public override IReadOnlyList<string> ExpectedMetricEffects => Array.Empty<string>();
    protected override string Command => DemoCommands.HealthCheck;

    // A read-only probe is safe anywhere, so it is the one tool not pinned to the demo environment.
    protected override bool RequiresDemoEnvironment => false;
}
