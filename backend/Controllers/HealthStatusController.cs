using AIDIP.Backend.Configuration;
using AIDIP.Backend.DTOs.Sre;
using AIDIP.Backend.Infrastructure;
using AIDIP.Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AIDIP.Backend.Controllers;

/// <summary>
/// Component-level health for the operator UI. The existing /api/health probe is untouched;
/// this adds the detail the dashboard needs to show which subsystem is down (frontend PRD
/// section 18) without exposing any configuration secret.
/// </summary>
[ApiController]
[Route("api/health")]
public class HealthStatusController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IAiMicroservice _ai;
    private readonly DetectionOptions _detection;
    private readonly RemediationOptions _remediation;

    public HealthStatusController(
        AppDbContext db,
        IAiMicroservice ai,
        IOptions<DetectionOptions> detection,
        IOptions<RemediationOptions> remediation)
    {
        _db = db;
        _ai = ai;
        _detection = detection.Value;
        _remediation = remediation.Value;
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        bool database;

        try
        {
            database = await _db.Database.CanConnectAsync(cancellationToken);
        }
        catch
        {
            database = false;
        }

        return Ok(new SystemHealthDto
        {
            Backend = true,
            Database = database,
            AiService = _ai.IsAvailable,
            DetectionEnabled = _detection.Enabled,
            RemediationEnabled = _remediation.Enabled,
            AiMode = _ai.Mode
        });
    }
}
