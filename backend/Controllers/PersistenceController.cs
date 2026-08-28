using AIDIP.Backend.Infrastructure;
using AIDIP.Backend.Services.Audit;
using Microsoft.AspNetCore.Mvc;

namespace AIDIP.Backend.Controllers;

[ApiController]
[Route("api/v1/platform/persistence")]
[RequiresOperator]
public sealed class PersistenceController : ControllerBase
{
    private readonly ISqliteBackupService _backups;
    private readonly IPlatformAuditService _audit;
    private readonly AppDbContext _db;

    public PersistenceController(ISqliteBackupService backups, IPlatformAuditService audit, AppDbContext db)
    { _backups = backups; _audit = audit; _db = db; }

    [HttpPost("backup")]
    public async Task<IActionResult> Backup(CancellationToken cancellationToken)
    {
        var result = await _backups.CreateAsync("operator", cancellationToken);
        if (!result.Created) return StatusCode(StatusCodes.Status503ServiceUnavailable,
            new { error = result.Error ?? "No local database exists to back up." });
        _audit.Record("persistence.backup-created", Actor(), "local-storage", result.FileName ?? "backup",
            data: new { result.FileName });
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { created = true, result.FileName });
    }

    private string Actor() => Request.Headers["X-KAIRON-Operator"].ToString() is { Length: > 0 } value
        ? value : "local-operator";
}
