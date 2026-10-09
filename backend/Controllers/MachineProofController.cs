using System.ComponentModel.DataAnnotations;
using Kairon.Backend.Configuration;
using Kairon.Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Controllers;

/// <summary>
/// SDK key creates a body-bound, 30-second challenge; the enrolled Agent separately confirms it.
/// Neither endpoint grants operator privileges. The challenge is usable only once on telemetry.
/// </summary>
[ApiController]
public sealed class MachineProofController : ControllerBase
{
    private readonly IMachineTelemetryBindingService _bindings;
    private readonly PlatformSecurityOptions _security;

    public MachineProofController(IMachineTelemetryBindingService bindings,
        IOptions<PlatformSecurityOptions> security)
    {
        _bindings = bindings;
        _security = security.Value;
    }

    [HttpPost("api/v1/telemetry/machine-proofs")]
    [EnableRateLimiting("machine-proof")]
    public async Task<IActionResult> Create([FromBody] CreateMachineProofRequest request, CancellationToken ct)
    {
        var key = Request.Headers[_security.TelemetryKeyHeader].ToString();
        var id = await _bindings.CreateProofAsync(request.ProjectId, request.Environment,
            request.Service, request.BodySha256, key, ct);
        return id.HasValue ? Ok(new { proofId = id.Value }) : Unauthorized(new { error = "Valid SDK credential and scope required." });
    }

    [HttpPost("api/agent/machines/{machineId:guid}/telemetry-proofs/{proofId:guid}/confirm")]
    [EnableRateLimiting("machine-proof")]
    public async Task<IActionResult> Confirm(Guid machineId, Guid proofId,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] ConfirmMachineProofRequest? body,
        CancellationToken ct)
    {
        var agentKey = Request.Headers["X-Kairon-Agent-Key"].ToString();
        // Older Agents send no body; the process is then simply unknown.
        return await _bindings.ConfirmProofAsync(proofId, machineId, agentKey, ct, body?.ProcessId)
            ? Ok(new { confirmed = true })
            : Unauthorized(new { error = "Agent proof rejected or expired." });
    }
}

public sealed class ConfirmMachineProofRequest
{
    /// <summary>Owner of the loopback connection that carried the proof, from the Agent's OS TCP table.</summary>
    [Range(1, int.MaxValue)] public int? ProcessId { get; set; }
}

public sealed class CreateMachineProofRequest
{
    public Guid ProjectId { get; set; }
    [Required, MaxLength(50)] public string Environment { get; set; } = string.Empty;
    [Required, MaxLength(200)] public string Service { get; set; } = string.Empty;
    [Required, StringLength(64, MinimumLength = 64)] public string BodySha256 { get; set; } = string.Empty;
}
