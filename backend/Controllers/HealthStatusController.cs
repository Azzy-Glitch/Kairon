using Kairon.Backend.Configuration;
using Kairon.Backend.DTOs.Sre;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Controllers;

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
    private readonly PersistenceOptions _persistence;
    private readonly PersistenceMaintenanceState _maintenance;

    public HealthStatusController(
        AppDbContext db,
        IAiMicroservice ai,
        IOptions<DetectionOptions> detection,
        IOptions<RemediationOptions> remediation,
        IOptions<PersistenceOptions> persistence,
        PersistenceMaintenanceState maintenance)
    {
        _db = db;
        _ai = ai;
        _detection = detection.Value;
        _remediation = remediation.Value;
        _persistence = persistence.Value;
        _maintenance = maintenance;
    }

    [HttpGet("status")]
    [RequiresOperator]
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
            AiMode = await _ai.GetModeAsync(cancellationToken),
            DatabaseProvider = _persistence.Provider,
            DatabaseSizeBytes = DatabaseSizeBytes(),
            MaintenanceStatus = _maintenance.Status,
            LastMaintenanceAt = _maintenance.LastCompletedAt,
            LastMaintenanceDeletedRows = _maintenance.LastCompletedAt is null ? null : _maintenance.LastDeletedRows
        });
    }

    private long? DatabaseSizeBytes()
    {
        if (!_persistence.Provider.Equals("SQLite", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var paths = KaironDataPaths.Resolve(_persistence);
            var path = string.IsNullOrWhiteSpace(_persistence.DatabasePath)
                ? paths.DatabasePath
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(_persistence.DatabasePath));
            return System.IO.File.Exists(path) ? new System.IO.FileInfo(path).Length : null;
        }
        catch
        {
            return null;
        }
    }
}
