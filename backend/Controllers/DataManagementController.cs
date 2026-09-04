using Kairon.Backend.Infrastructure;
using Kairon.Backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace Kairon.Backend.Controllers;

[ApiController]
[Route("api/v1/data")]
[RequiresOperator]
public sealed class DataManagementController : ControllerBase
{
    private readonly IDataManagementService _data;

    public DataManagementController(IDataManagementService data) => _data = data;

    [HttpGet("export")]
    public async Task<IActionResult> Export(CancellationToken cancellationToken)
    {
        var export = await _data.CreateExportAsync(cancellationToken);
        if (!export.Created || export.FullPath is null || export.FileName is null)
            return Conflict(new { error = export.Error ?? "Database export failed." });

        // DeleteOnClose prevents the server-side export copy becoming another retained data file;
        // the user's downloaded copy is unaffected.
        var stream = new FileStream(
            export.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        return File(stream, "application/vnd.sqlite3", export.FileName);
    }

    [HttpDelete]
    public async Task<IActionResult> DeleteAll(
        [FromBody] DeleteAllDataRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Confirmation, "DELETE", StringComparison.Ordinal))
            return BadRequest(new { error = "Type DELETE exactly to confirm permanent data deletion." });

        var result = await _data.DeleteAllAsync(cancellationToken);
        return Ok(new
        {
            result.DeletedRecords,
            result.DeletedBackups,
            message = "All Kairon database data was deleted. The empty database schema remains ready for use."
        });
    }
}

public sealed class DeleteAllDataRequest
{
    public string Confirmation { get; set; } = string.Empty;
}
