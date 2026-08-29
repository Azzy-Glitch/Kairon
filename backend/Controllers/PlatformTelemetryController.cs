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
    private readonly PlatformSecurityOptions _security;

    public PlatformTelemetryController(IPlatformTelemetryService telemetry, IProjectCredentialService credentials,
        ISdkInstallationService installations, IOptions<PlatformSecurityOptions> security)
    {
        _telemetry = telemetry;
        _credentials = credentials;
        _installations = installations;
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

        var result = await _telemetry.IngestAsync(batch, cancellationToken);
        return Ok(result);
    }
}
