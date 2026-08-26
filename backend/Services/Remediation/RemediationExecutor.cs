using Kairon.Backend.Configuration;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services.Audit;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Services.Remediation;

public interface IRemediationExecutor
{
    /// <summary>
    /// Executes an approved action. Re-validates policy immediately before running, so approval
    /// alone is never sufficient authority (PRD section 12).
    /// </summary>
    Task<RemediationToolResult> ExecuteAsync(
        SreIncident incident,
        RemediationAction action,
        CancellationToken cancellationToken = default);
}

public class RemediationExecutor : IRemediationExecutor
{
    private readonly IRemediationToolRegistry _registry;
    private readonly IRemediationPolicy _policy;
    private readonly IAuditService _audit;
    private readonly AppDbContext _db;
    private readonly RemediationOptions _options;
    private readonly ILogger<RemediationExecutor> _logger;

    public RemediationExecutor(
        IRemediationToolRegistry registry,
        IRemediationPolicy policy,
        IAuditService audit,
        AppDbContext db,
        IOptions<RemediationOptions> options,
        ILogger<RemediationExecutor> logger)
    {
        _registry = registry;
        _policy = policy;
        _audit = audit;
        _db = db;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<RemediationToolResult> ExecuteAsync(
        SreIncident incident,
        RemediationAction action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(action);

        var decision = _policy.ValidateExecution(incident, action);
        action.PolicyDecision = $"{decision.Code}: {decision.Reason}";

        if (!decision.Allowed)
        {
            action.Status = RemediationStatus.PolicyRejected;
            action.CompletedAt = DateTime.UtcNow;
            action.ExecutionError = decision.Reason;

            _audit.Record(incident, IncidentEventTypes.PolicyEvaluated, "policy",
                actionId: action.ActionKey,
                result: "denied",
                message: decision.Reason);

            _logger.LogWarning("Policy denied execution of {ActionKey} on {IncidentKey}: {Reason}",
                action.ActionKey, incident.IncidentKey, decision.Reason);

            return RemediationToolResult.Fail(decision.Reason);
        }

        // The registry lookup is the last gate. Even a policy-approved name that is somehow not
        // registered cannot reach an executor.
        if (!_registry.TryGet(action.ActionType, out var tool))
        {
            var message = $"'{action.ActionType}' is not a registered remediation tool.";
            action.Status = RemediationStatus.Failed;
            action.CompletedAt = DateTime.UtcNow;
            action.ExecutionError = message;

            _audit.Record(incident, IncidentEventTypes.Failed, "remediation-executor",
                actionId: action.ActionKey, result: "failed", error: message);

            return RemediationToolResult.Fail(message);
        }

        action.Status = RemediationStatus.Executing;
        action.StartedAt = DateTime.UtcNow;

        _audit.Record(incident, IncidentEventTypes.Executing, "remediation-executor",
            actionId: action.ActionKey,
            message: $"Executing {tool.Name}");

        await _db.SaveChangesAsync(cancellationToken);

        var parameters = SreJson.Deserialize(action.ParametersJson, new Dictionary<string, string>());

        var context = new RemediationToolContext
        {
            IncidentId = incident.Id,
            IncidentKey = incident.IncidentKey,
            Service = incident.Service,
            Environment = incident.Environment,
            ActionKey = action.ActionKey,
            Parameters = parameters
        };

        RemediationToolResult result;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.ExecutionTimeoutSeconds));

            result = await tool.ExecuteAsync(context, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = RemediationToolResult.Fail(
                $"{tool.Name} timed out after {_options.ExecutionTimeoutSeconds}s.");
        }
        catch (Exception ex)
        {
            // A throwing tool is a failed action, not a crashed backend.
            _logger.LogError(ex, "Remediation tool {Tool} threw during {ActionKey}", tool.Name, action.ActionKey);
            result = RemediationToolResult.Fail(Redaction.Describe(ex));
        }

        action.CompletedAt = DateTime.UtcNow;

        if (result.Success)
        {
            action.Status = RemediationStatus.Executed;
            action.ExecutionResult = Redaction.Scrub(result.Message);
            action.ExecutionError = null;

            _audit.Record(incident, IncidentEventTypes.Executed, "remediation-executor",
                actionId: action.ActionKey,
                result: "success",
                message: result.Message,
                data: result.Details);

            _logger.LogInformation("Executed {Tool} for {IncidentKey}: {Message}",
                tool.Name, incident.IncidentKey, result.Message);
        }
        else
        {
            action.Status = RemediationStatus.Failed;
            action.ExecutionError = Redaction.Scrub(result.Error);

            _audit.Record(incident, IncidentEventTypes.Failed, "remediation-executor",
                actionId: action.ActionKey,
                result: "failed",
                error: result.Error);

            _logger.LogWarning("Remediation {Tool} failed for {IncidentKey}: {Error}",
                tool.Name, incident.IncidentKey, result.Error);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return result;
    }
}
