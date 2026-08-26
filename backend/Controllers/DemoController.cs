using Kairon.Backend.DTOs;
using Kairon.Backend.Services.Demo;
using Kairon.Backend.Services.Orchestration;
using Microsoft.AspNetCore.Mvc;

namespace Kairon.Backend.Controllers;

/// <summary>
/// Drives the deterministic competition demo (frontend PRD section 14). One button starts the
/// controlled order-processing retry-loop scenario; everything after that is the real pipeline.
/// </summary>
[ApiController]
[Route("api/demo")]
public class DemoController : ControllerBase
{
    private readonly IDemoEnvironmentClient _demo;
    private readonly ILocalDemoSimulator _simulator;
    private readonly IIncidentProcessingQueue _queue;
    private readonly ILogger<DemoController> _logger;

    public DemoController(
        IDemoEnvironmentClient demo,
        ILocalDemoSimulator simulator,
        IIncidentProcessingQueue queue,
        ILogger<DemoController> logger)
    {
        _demo = demo;
        _simulator = simulator;
        _queue = queue;
        _logger = logger;
    }

    [HttpGet("state")]
    public async Task<IActionResult> State(CancellationToken cancellationToken) =>
        Ok(await _demo.GetStateAsync(cancellationToken));

    /// <summary>Starts the retry-loop scenario. Detection, AI and remediation then run for real.</summary>
    [HttpPost("simulate/start")]
    public async Task<IActionResult> Start(CancellationToken cancellationToken)
    {
        var result = await _demo.SendAsync(DemoCommands.StartRetryStorm, cancellationToken);

        if (!result.Success)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new ApiResponse<object>
            {
                Success = false,
                Error = result.Error ?? "Could not start the demo scenario.",
                ErrorCode = "DEMO_START_FAILED",
                StatusCode = StatusCodes.Status502BadGateway
            });
        }

        var state = result.State ?? _simulator.GetState();

        _logger.LogInformation("Demo scenario started (localSimulator={Local})", state.UsingLocalSimulator);
        return Ok(state);
    }

    /// <summary>Stops the scenario and returns the environment to baseline.</summary>
    [HttpPost("simulate/stop")]
    public async Task<IActionResult> Stop(CancellationToken cancellationToken)
    {
        var result = await _demo.SendAsync(DemoCommands.Stop, cancellationToken);
        return Ok(result.State ?? _simulator.GetState());
    }

    /// <summary>
    /// Forces an immediate detection pass instead of waiting for the sweep. Useful when presenting,
    /// so the incident appears the moment the metrics justify it.
    /// </summary>
    [HttpPost("evaluate")]
    public async Task<IActionResult> Evaluate(CancellationToken cancellationToken)
    {
        var state = await _demo.GetStateAsync(cancellationToken);

        var queued = _queue.TryEnqueue(new IncidentWorkItem(
            WorkItemKind.EvaluateDetection, state.ProjectId, state.Environment, state.Service));

        return Accepted(new { queued, projectId = state.ProjectId, environment = state.Environment });
    }
}
