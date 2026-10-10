using Kairon.Backend.Configuration;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Controllers;

/// <summary>
/// Automatic SDK signals (queue depth from requests in progress, retries of failed outgoing calls),
/// configured per paired app in the desktop. The SDK fetches its own settings with its telemetry
/// key - POST, like every other SDK call, so the cloud gateway's POST-only SDK rule covers it.
/// </summary>
[ApiController]
public sealed class SdkSettingsController(ISdkSettingsService settings, IOptions<PlatformSecurityOptions> security) : ControllerBase
{
    [HttpPost("api/v1/sdk/settings")]
    [EnableRateLimiting("telemetry")]
    public async Task<IActionResult> ForSdk([FromBody] SdkSettingsRequest request, CancellationToken cancellationToken)
    {
        var found = await settings.GetForKeyAsync(request.ProjectId, Request.Headers[security.Value.TelemetryKeyHeader].ToString(), cancellationToken);
        return found is null ? Unauthorized(new { error = "A valid project telemetry key is required." }) : Ok(found);
    }

    [HttpGet("api/v1/projects/{projectId:guid}/credentials/{credentialId:guid}/settings")]
    [RequiresOperator]
    public async Task<IActionResult> Get(Guid projectId, Guid credentialId, CancellationToken cancellationToken) =>
        await settings.GetAsync(projectId, credentialId, cancellationToken) is { } found ? Ok(found) : NotFound();

    [HttpPut("api/v1/projects/{projectId:guid}/credentials/{credentialId:guid}/settings")]
    [RequiresOperator]
    public async Task<IActionResult> Update(Guid projectId, Guid credentialId, [FromBody] SdkAutoSignalSettings request,
        CancellationToken cancellationToken)
    {
        var actor = Request.Headers["X-Kairon-Operator"].ToString() is { Length: > 0 } value ? value : "local-operator";
        return await settings.UpdateAsync(projectId, credentialId, request, actor, cancellationToken) switch
        {
            SdkSettingsUpdateOutcome.Updated => Ok(await settings.GetAsync(projectId, credentialId, cancellationToken)),
            SdkSettingsUpdateOutcome.Invalid => BadRequest(new { error = "The retry window must be between 1 and 300 seconds." }),
            _ => NotFound()
        };
    }
}

public sealed class SdkSettingsRequest
{
    public Guid ProjectId { get; set; }
}
