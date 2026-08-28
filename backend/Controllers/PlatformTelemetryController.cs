using AIDIP.Backend.DTOs;
using AIDIP.Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AIDIP.Backend.Controllers;

[ApiController]
[Route("api/v1/telemetry")]
[EnableRateLimiting("telemetry")]
public sealed class PlatformTelemetryController : ControllerBase
{
    private readonly IPlatformTelemetryService _telemetry;
    private readonly IProjectCredentialService _credentials;
    private readonly AIDIP.Backend.Configuration.PlatformSecurityOptions _security;
    private readonly ISdkPairingService _pairing;
    public PlatformTelemetryController(IPlatformTelemetryService telemetry, IProjectCredentialService credentials,
        Microsoft.Extensions.Options.IOptions<AIDIP.Backend.Configuration.PlatformSecurityOptions> security,
        ISdkPairingService pairing)
    {
        _telemetry = telemetry;
        _credentials = credentials;
        _security = security.Value;
        _pairing = pairing;
    }

    [HttpPost("events")]
    [RequestSizeLimit(1_048_576)]
    public async Task<ActionResult<NormalizedTelemetryResultDto>> Ingest(
        NormalizedTelemetryBatchDto batch, CancellationToken cancellationToken)
    {
        var projectIds = batch.Events.Select(x => x.ProjectId).Where(x => x != Guid.Empty).Distinct().ToList();
        var key = Request.Headers[_security.TelemetryKeyHeader].ToString();
        if (!await _credentials.AuthorizeAsync(projectIds, key, cancellationToken)
            && !await _pairing.AuthorizeTelemetryAsync(batch, key, cancellationToken))
            return Unauthorized(new { error = "A valid project-scoped telemetry key is required." });
        var result = await _telemetry.IngestAsync(batch, cancellationToken);
        return Ok(result);
    }
}
