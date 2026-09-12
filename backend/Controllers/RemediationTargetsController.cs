using Kairon.Backend.DTOs;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Services.Remediation;
using Microsoft.AspNetCore.Mvc;

namespace Kairon.Backend.Controllers;

/// <summary>
/// Operator-authorized management API for the database-backed RemediationTarget system (Phase 1 -
/// Services/Remediation/RemediationTargetResolver.cs). This is configuration management only: it
/// never runs sc.exe, never approves or creates a remediation action, and never bypasses the
/// runtime resolver's own re-resolution at execution time - it only ever changes what is
/// persisted. This is the backend contract the future KAIRON React UI (Phase 3) will drive.
///
/// Every write here is security-sensitive (it changes what KAIRON is authorized to remediate), so
/// the whole controller requires an operator key exactly like ProjectsController/IncidentsController
/// - reusing [RequiresOperator]/OperatorAuthorizationFilter rather than a parallel auth scheme. A
/// normal project/telemetry API credential is never sufficient here.
/// </summary>
[ApiController]
[Route("api/v1/remediation-targets")]
[RequiresOperator]
public sealed class RemediationTargetsController : ControllerBase
{
    private readonly IRemediationTargetManagementService _targets;

    public RemediationTargetsController(IRemediationTargetManagementService targets) => _targets = targets;

    /// <summary>Lists targets across all projects (frontend PRD: the Remediation Targets table).
    /// All filters are optional and combine with AND.</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid? projectId,
        [FromQuery] Guid? machineId,
        [FromQuery] bool? enabled,
        [FromQuery] string? environment,
        CancellationToken cancellationToken) =>
        Ok(await _targets.ListAsync(new RemediationTargetFilter(projectId, machineId, enabled, environment), cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var target = await _targets.GetAsync(id, cancellationToken);
        return target is null
            ? NotFound(Error("Remediation target not found.", "target-not-found", StatusCodes.Status404NotFound))
            : Ok(target);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRemediationTargetRequest request, CancellationToken cancellationToken)
    {
        var result = await _targets.CreateAsync(request, Actor(), cancellationToken);
        return result.Outcome switch
        {
            RemediationTargetOperationOutcome.Success => Created($"/api/v1/remediation-targets/{result.Target!.Id}", result.Target),
            RemediationTargetOperationOutcome.Conflict => Conflict(Error(result.Error!, result.ErrorCode!, StatusCodes.Status409Conflict)),
            _ => UnprocessableEntity(Error(result.Error!, result.ErrorCode!, 422))
        };
    }

    /// <summary>Full replace. Every security-sensitive field is revalidated as if this were a fresh
    /// create - see RemediationTargetManagementService's remarks. Does not touch any previously
    /// approved RemediationAction; a stale approval is invalidated naturally the next time it is
    /// executed, via the existing fingerprint re-derivation in WindowsServiceTool.</summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateRemediationTargetRequest request, CancellationToken cancellationToken)
    {
        var result = await _targets.UpdateAsync(id, request, Actor(), cancellationToken);
        return result.Outcome switch
        {
            RemediationTargetOperationOutcome.Success => Ok(result.Target),
            RemediationTargetOperationOutcome.NotFound => NotFound(Error(result.Error!, result.ErrorCode!, StatusCodes.Status404NotFound)),
            RemediationTargetOperationOutcome.Conflict => Conflict(Error(result.Error!, result.ErrorCode!, StatusCodes.Status409Conflict)),
            _ => UnprocessableEntity(Error(result.Error!, result.ErrorCode!, 422))
        };
    }

    /// <summary>Soft-delete: disables the target rather than physically removing the row, matching
    /// ProjectsController.RevokeCredential's existing DELETE-never-physically-deletes precedent for
    /// the same reason - a security-sensitive configuration's history must outlive the operator
    /// action that changed it. The row can be re-enabled via <see cref="Enable"/>.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await _targets.SetEnabledAsync(id, false, Actor(), cancellationToken);
        return result.Outcome switch
        {
            RemediationTargetOperationOutcome.Success => NoContent(),
            RemediationTargetOperationOutcome.NotFound => NotFound(Error(result.Error!, result.ErrorCode!, StatusCodes.Status404NotFound)),
            // A concurrent/stale delete is the same "someone else changed this row first" conflict
            // Update/Enable already report as 409 - not a validation failure, so it must not fall
            // through to the generic 422 below.
            RemediationTargetOperationOutcome.Conflict => Conflict(Error(result.Error!, result.ErrorCode!, StatusCodes.Status409Conflict)),
            _ => UnprocessableEntity(Error(result.Error!, result.ErrorCode!, 422))
        };
    }

    /// <summary>Re-enables a disabled target. Fully revalidated first - see
    /// RemediationTargetManagementService.SetEnabledAsync's remarks; a disabled target that has
    /// gone stale cannot become executable just by flipping Enabled back to true.</summary>
    [HttpPost("{id:guid}/enable")]
    public async Task<IActionResult> Enable(Guid id, CancellationToken cancellationToken)
    {
        var result = await _targets.SetEnabledAsync(id, true, Actor(), cancellationToken);
        return result.Outcome switch
        {
            RemediationTargetOperationOutcome.Success => Ok(result.Target),
            RemediationTargetOperationOutcome.NotFound => NotFound(Error(result.Error!, result.ErrorCode!, StatusCodes.Status404NotFound)),
            RemediationTargetOperationOutcome.Conflict => Conflict(Error(result.Error!, result.ErrorCode!, StatusCodes.Status409Conflict)),
            _ => UnprocessableEntity(Error(result.Error!, result.ErrorCode!, 422))
        };
    }

    /// <summary>Preflight-only validation for a prospective target. Never persists anything, never
    /// executes sc.exe, never creates an incident or remediation action, never changes runtime
    /// state - purely a validation pass the future UI can call before submitting Create.</summary>
    [HttpPost("validate")]
    public async Task<IActionResult> Validate([FromBody] CreateRemediationTargetRequest request, CancellationToken cancellationToken)
    {
        var errors = await _targets.ValidateAsync(request, cancellationToken);
        return Ok(new RemediationTargetValidationResponse { Valid = errors.Count == 0, Errors = errors.ToList() });
    }

    private string Actor() => Request.Headers["X-Kairon-Operator"].ToString() is { Length: > 0 } value
        ? value
        : "local-operator";

    private static ApiResponse<object> Error(string message, string code, int status) => new()
    {
        Success = false,
        Error = message,
        ErrorCode = code,
        StatusCode = status
    };
}
