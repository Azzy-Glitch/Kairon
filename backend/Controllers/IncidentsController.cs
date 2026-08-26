using Kairon.Backend.DTOs;
using Kairon.Backend.DTOs.Sre;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services.Audit;
using Kairon.Backend.Services.Orchestration;
using Microsoft.AspNetCore.Mvc;

namespace Kairon.Backend.Controllers;

/// <summary>
/// The operator API for the Autonomous SRE lifecycle. Entirely additive - every pre-existing
/// endpoint keeps its original route and behaviour (PRD section 15).
/// </summary>
[ApiController]
[Route("api/incidents")]
public class IncidentsController : ControllerBase
{
    private readonly IIncidentQueryService _query;
    private readonly IIncidentOrchestrator _orchestrator;
    private readonly IIncidentProcessingQueue _queue;
    private readonly ILogger<IncidentsController> _logger;

    public IncidentsController(
        IIncidentQueryService query,
        IIncidentOrchestrator orchestrator,
        IIncidentProcessingQueue queue,
        ILogger<IncidentsController> logger)
    {
        _query = query;
        _orchestrator = orchestrator;
        _queue = queue;
        _logger = logger;
    }

    /// <summary>Incident feed. Supports status=active for the live operator view.</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] string? severity,
        [FromQuery] string? service,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var incidents = await _query.ListAsync(status, severity, service, limit, cancellationToken);
        return Ok(incidents);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var incident = await _query.GetAsync(id, cancellationToken);
        if (incident is null)
            return NotFound(Error("Incident not found.", "INCIDENT_NOT_FOUND", StatusCodes.Status404NotFound));

        return Ok(incident);
    }

    [HttpGet("{id:guid}/timeline")]
    public async Task<IActionResult> Timeline(Guid id, CancellationToken cancellationToken) =>
        Ok(await _query.GetTimelineAsync(id, cancellationToken));

    /// <summary>The evidence package the AI was given, so an operator can audit the diagnosis.</summary>
    [HttpGet("{id:guid}/evidence")]
    public async Task<IActionResult> Evidence(Guid id, CancellationToken cancellationToken) =>
        Ok(await _query.GetEvidenceAsync(id, cancellationToken));

    /// <summary>Dashboard aggregate: active incidents, severity mix, live metrics, health.</summary>
    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard([FromQuery] Guid? projectId, CancellationToken cancellationToken) =>
        Ok(await _query.GetDashboardAsync(projectId, cancellationToken));

    /// <summary>The registered remediation tools and whether policy currently permits each one.</summary>
    [HttpGet("tools")]
    public IActionResult Tools() => Ok(_query.GetTools());

    /// <summary>
    /// Re-runs AI investigation. Queued rather than executed inline so a slow model call cannot
    /// hold an HTTP request open (PRD section 6).
    /// </summary>
    [HttpPost("{id:guid}/investigate")]
    public async Task<IActionResult> Investigate(Guid id, CancellationToken cancellationToken)
    {
        var incident = await _query.GetAsync(id, cancellationToken);
        if (incident is null)
            return NotFound(Error("Incident not found.", "INCIDENT_NOT_FOUND", StatusCodes.Status404NotFound));

        var queued = _queue.TryEnqueue(new IncidentWorkItem(
            WorkItemKind.ProcessIncident, Guid.Empty, incident.Environment, incident.Service, id));

        if (!queued)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                Error("The analysis queue is full; try again shortly.", "QUEUE_FULL", StatusCodes.Status503ServiceUnavailable));
        }

        return Accepted(new { message = "Investigation queued.", incidentId = id });
    }

    /// <summary>
    /// Approves and runs one remediation action, then verifies it. This is the only path through
    /// which anything is ever executed.
    /// </summary>
    [HttpPost("{id:guid}/actions/{actionId:guid}/approve")]
    [RequiresOperator]
    public async Task<IActionResult> Approve(
        Guid id,
        Guid actionId,
        [FromBody] ApproveActionRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.ApprovedBy))
        {
            return BadRequest(Error("An approver identity is required.", "APPROVER_REQUIRED",
                StatusCodes.Status400BadRequest));
        }

        try
        {
            var action = await _orchestrator.ApproveAsync(id, actionId, request.ApprovedBy, request.Note, cancellationToken);
            if (action is null)
                return NotFound(Error("Incident or action not found.", "ACTION_NOT_FOUND", StatusCodes.Status404NotFound));

            var detail = await _query.GetAsync(id, cancellationToken);
            return Ok(detail);
        }
        catch (InvalidIncidentTransitionException ex)
        {
            return Conflict(Error(ex.Message, "INVALID_TRANSITION", StatusCodes.Status409Conflict));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(Error(ex.Message, "INVALID_ACTION_STATE", StatusCodes.Status409Conflict));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Approval failed for incident {IncidentId} action {ActionId}", id, actionId);
            return StatusCode(StatusCodes.Status500InternalServerError,
                Error("Approval failed.", "APPROVAL_FAILED", StatusCodes.Status500InternalServerError));
        }
    }

    [HttpPost("{id:guid}/actions/{actionId:guid}/reject")]
    [RequiresOperator]
    public async Task<IActionResult> Reject(
        Guid id,
        Guid actionId,
        [FromBody] RejectActionRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.RejectedBy))
        {
            return BadRequest(Error("A rejecter identity is required.", "REJECTER_REQUIRED",
                StatusCodes.Status400BadRequest));
        }

        try
        {
            var action = await _orchestrator.RejectAsync(id, actionId, request.RejectedBy, request.Reason, cancellationToken);
            if (action is null)
                return NotFound(Error("Incident or action not found.", "ACTION_NOT_FOUND", StatusCodes.Status404NotFound));

            var detail = await _query.GetAsync(id, cancellationToken);
            return Ok(detail);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(Error(ex.Message, "INVALID_ACTION_STATE", StatusCodes.Status409Conflict));
        }
    }

    [HttpPost("{id:guid}/cancel")]
    [RequiresOperator]
    public async Task<IActionResult> Cancel(
        Guid id,
        [FromBody] CancelIncidentRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.CancelledBy))
        {
            return BadRequest(Error("A canceller identity is required.", "CANCELLER_REQUIRED",
                StatusCodes.Status400BadRequest));
        }

        try
        {
            await _orchestrator.CancelAsync(id, request.CancelledBy, request.Reason, cancellationToken);
            var detail = await _query.GetAsync(id, cancellationToken);
            if (detail is null)
                return NotFound(Error("Incident not found.", "INCIDENT_NOT_FOUND", StatusCodes.Status404NotFound));

            return Ok(detail);
        }
        catch (InvalidIncidentTransitionException ex)
        {
            return Conflict(Error(ex.Message, "INVALID_TRANSITION", StatusCodes.Status409Conflict));
        }
    }

    private static ApiResponse<object> Error(string message, string code, int status) => new()
    {
        Success = false,
        Error = message,
        ErrorCode = code,
        StatusCode = status
    };
}
