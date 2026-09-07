using Kairon.Backend.Infrastructure;
using Kairon.Backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace Kairon.Backend.Controllers;

[ApiController]
[Route("api/v1/database-config")]
[RequiresOperator]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DatabaseConfigController(DatabaseConfigurationService settings) : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        try { return Ok(settings.Get()); }
        catch { return StatusCode(503, new { error = "Saved database configuration is unavailable. Check the protected configuration store." }); }
    }
    [HttpPost("test")]
    [RequestSizeLimit(8192)]
    public async Task<IActionResult> Test(DatabaseSettingsRequest request, CancellationToken ct)
    {
        try { return Ok(await settings.TestAsync(request, ct)); }
        catch (DatabaseSettingsValidationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return StatusCode(503, new { error = "Database connection test could not be completed." }); }
    }
    [HttpPost]
    [RequestSizeLimit(8192)]
    public async Task<IActionResult> Save(DatabaseSettingsRequest request, CancellationToken ct)
    {
        try { return Ok(await settings.SaveAsync(request, ct)); }
        catch (DatabaseSettingsValidationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return StatusCode(503, new { error = "Database settings could not be saved. Check connection access and the protected configuration store." }); }
    }
}
