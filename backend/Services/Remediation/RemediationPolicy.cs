using Kairon.Backend.Configuration;
using Kairon.Backend.Models.Sre;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Services.Remediation;

public class PolicyDecision
{
    public bool Allowed { get; init; }
    public string Reason { get; init; } = string.Empty;

    /// <summary>Machine-readable code, so the UI and the audit trail agree on why.</summary>
    public string Code { get; init; } = string.Empty;

    public static PolicyDecision Allow(string reason = "Permitted by policy") =>
        new() { Allowed = true, Reason = reason, Code = "allowed" };

    public static PolicyDecision Deny(string code, string reason) =>
        new() { Allowed = false, Reason = reason, Code = code };
}

/// <summary>
/// Validates a proposed remediation action before a human ever sees it, and again before it runs
/// (PRD section 12 and 13). Policy is the layer that makes an AI proposal harmless: no matter what
/// the model asks for, only what policy permits can reach an executor.
/// </summary>
public interface IRemediationPolicy
{
    /// <summary>Checked when the action is proposed, before it is offered for approval.</summary>
    PolicyDecision ValidateProposal(SreIncident incident, string actionType, RiskLevel riskLevel);

    /// <summary>Re-checked immediately before execution, after approval.</summary>
    PolicyDecision ValidateExecution(SreIncident incident, RemediationAction action);
}

public class RemediationPolicy : IRemediationPolicy
{
    private readonly IRemediationToolRegistry _registry;
    private readonly RemediationOptions _options;
    private readonly ILogger<RemediationPolicy> _logger;

    public RemediationPolicy(
        IRemediationToolRegistry registry,
        IOptions<RemediationOptions> options,
        ILogger<RemediationPolicy> logger)
    {
        _registry = registry;
        _options = options.Value;
        _logger = logger;
    }

    public PolicyDecision ValidateProposal(SreIncident incident, string actionType, RiskLevel riskLevel)
    {
        if (string.IsNullOrWhiteSpace(actionType))
            return PolicyDecision.Deny("empty-action", "No action was named.");

        // The single most important check in the system: the AI can only name a tool that already
        // exists in the registry. An invented action never becomes executable.
        if (!_registry.TryGet(actionType, out var tool))
        {
            _logger.LogWarning(
                "Policy rejected unregistered action {Action} proposed for incident {Key}",
                actionType, incident.IncidentKey);
            return PolicyDecision.Deny("unregistered-tool",
                $"'{actionType}' is not a registered remediation tool and cannot be executed.");
        }

        if (_options.BlockedTools.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
            return PolicyDecision.Deny("blocked-tool", $"'{tool.Name}' is explicitly blocked by policy.");

        if (_options.AllowedTools.Count > 0 &&
            !_options.AllowedTools.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
        {
            return PolicyDecision.Deny("not-allowlisted", $"'{tool.Name}' is not on the policy allowlist.");
        }

        // The tool's own declared risk wins over whatever risk the model claimed, so a model
        // cannot talk a dangerous action past policy by labelling it "low".
        var effectiveRisk = (RiskLevel)Math.Max((int)tool.RiskLevel, (int)riskLevel);
        if (effectiveRisk > _options.MaxAllowedRisk)
        {
            return PolicyDecision.Deny("risk-too-high",
                $"Risk {effectiveRisk} exceeds the configured maximum of {_options.MaxAllowedRisk}.");
        }

        if (!_options.AllowedEnvironments.Contains(incident.Environment, StringComparer.OrdinalIgnoreCase))
        {
            return PolicyDecision.Deny("environment-not-allowed",
                $"Remediation is not permitted in environment '{incident.Environment}'.");
        }

        return PolicyDecision.Allow($"'{tool.Name}' is registered, permitted, and within the risk ceiling.");
    }

    public PolicyDecision ValidateExecution(SreIncident incident, RemediationAction action)
    {
        if (!_options.Enabled)
            return PolicyDecision.Deny("remediation-disabled", "Remediation execution is disabled by configuration.");

        // Re-run every proposal check. Configuration or incident state can change between the
        // proposal and the approval, and the moment of execution is the one that matters.
        var proposal = ValidateProposal(incident, action.ActionType, action.RiskLevel);
        if (!proposal.Allowed)
            return proposal;

        // The specific reason wins over the generic one: an operator told "approval required" for
        // an action they already rejected would reasonably think approving it again would help.
        if (action.Status == RemediationStatus.Rejected)
            return PolicyDecision.Deny("rejected", "This action was rejected by an operator.");

        if (action.Status is RemediationStatus.Executed or RemediationStatus.Executing)
            return PolicyDecision.Deny("already-executed", "This action has already been executed.");

        if (action.Status == RemediationStatus.Cancelled)
            return PolicyDecision.Deny("cancelled", "This action was cancelled.");

        if (_options.RequireApprovalForEveryAction && action.Status != RemediationStatus.Approved)
        {
            return PolicyDecision.Deny("approval-required",
                "Human approval is required before this action can execute.");
        }

        if (incident.Actions.Count(a => a.Status == RemediationStatus.Executed) >= _options.MaxActionsPerIncident)
        {
            return PolicyDecision.Deny("action-limit",
                $"This incident has reached the limit of {_options.MaxActionsPerIncident} executed actions.");
        }

        return PolicyDecision.Allow("Approved, registered, and within all policy limits.");
    }
}
