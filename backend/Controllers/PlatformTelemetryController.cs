using Kairon.Backend.Configuration;
using Kairon.Backend.DTOs;
using Kairon.Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Controllers;

/// <summary>
/// Normalized telemetry ingestion (docs/DESKTOP_SHELL.md) - new, additive capability alongside
/// TelemetryController's existing /api/telemetry/* routes, which are untouched and keep working
/// exactly as they do today. Authorization accepts either an existing project-level credential
/// (ProjectApiCredential, "krn_...") or an installation-scoped credential (SdkInstallation,
/// "ksi_..."), matching whichever tier the caller was issued.
/// </summary>
[ApiController]
[Route("api/v1/telemetry")]
[EnableRateLimiting("telemetry")]
public sealed class PlatformTelemetryController : ControllerBase
{
    private readonly IPlatformTelemetryService _telemetry;
    private readonly IProjectCredentialService _credentials;
    private readonly ISdkInstallationService _installations;
    private readonly IMachineTelemetryBindingService _binding;
    private readonly PlatformSecurityOptions _security;

    public PlatformTelemetryController(IPlatformTelemetryService telemetry, IProjectCredentialService credentials,
        ISdkInstallationService installations, IMachineTelemetryBindingService binding,
        IOptions<PlatformSecurityOptions> security)
    {
        _telemetry = telemetry;
        _credentials = credentials;
        _installations = installations;
        _binding = binding;
        _security = security.Value;
    }

    [HttpPost("events")]
    [RequestSizeLimit(1_048_576)]
    public async Task<ActionResult<NormalizedTelemetryResultDto>> Ingest(
        NormalizedTelemetryBatchDto batch, CancellationToken cancellationToken)
    {
        var projectIds = batch.Events.Select(x => x.ProjectId).Where(x => x != Guid.Empty).Distinct().ToList();
        var key = Request.Headers[_security.TelemetryKeyHeader].ToString();
        if (!await _credentials.AuthorizeAsync(projectIds, key, cancellationToken)
            && !await _installations.AuthorizeBatchAsync(batch, key, cancellationToken))
            return Unauthorized(new { error = "A valid project or installation telemetry key is required." });

        // A relay proof applies to this whole batch only when its project/service/environment
        // scope is homogeneous. Mixed batches remain valid telemetry but cannot gain one machine
        // identity from a single credential and target.
        Guid? machineId = null;
        if (batch.Events.Count > 0)
        {
            var first = batch.Events[0];
            var service = string.IsNullOrWhiteSpace(first.Service) ? first.Application : first.Service;
            var environment = string.IsNullOrWhiteSpace(first.Environment) ? "Development" : first.Environment;
            var homogeneous = batch.Events.All(e => e.ProjectId == first.ProjectId &&
                string.Equals(string.IsNullOrWhiteSpace(e.Service) ? e.Application : e.Service, service, StringComparison.Ordinal) &&
                string.Equals(string.IsNullOrWhiteSpace(e.Environment) ? "Development" : e.Environment,
                    environment, StringComparison.OrdinalIgnoreCase));
            if (homogeneous)
            {
                var scope = await _binding.ResolveAsync(Request, first.ProjectId, environment, service, cancellationToken);
                if (scope.InvalidAgentProof) return Unauthorized(new { error = "Agent relay proof is invalid or expired." });
                machineId = scope.MachineId;
            }
            else if (Request.Headers.ContainsKey("X-Kairon-Machine-Proof"))
                return BadRequest(new { error = "Machine-scoped batches must have one project, service and environment." });
        }

        var result = await _telemetry.IngestAsync(batch, cancellationToken, machineId);
        return Ok(result);
    }
}
