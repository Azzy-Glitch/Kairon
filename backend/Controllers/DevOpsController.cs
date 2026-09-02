using Kairon.Backend.DTOs;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace Kairon.Backend.Controllers;

[ApiController]
[Route("api")]
[RequiresOperator]
public class DevOpsController : ControllerBase
{
    private readonly IDevOpsService _devOpsService;
    private readonly ILogger<DevOpsController> _logger;

    public DevOpsController(IDevOpsService devOpsService, ILogger<DevOpsController> logger)
    {
        _devOpsService = devOpsService;
        _logger = logger;
    }

    [HttpPost("analyze-error")]
    public async Task<IActionResult> AnalyzeError(
        [FromBody] ErrorAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _devOpsService.AnalyzeErrorAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in analyze-error endpoint");
            return StatusCode(500, new { message = "An error occurred while analyzing the error" });
        }
    }

    [HttpPost("validate-api")]
    public async Task<IActionResult> ValidateApi(
        [FromBody] ApiValidationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _devOpsService.ValidateApiAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in validate-api endpoint");
            return StatusCode(500, new { message = "An error occurred while validating the API specification" });
        }
    }

    [HttpPost("predict")]
    public async Task<IActionResult> Predict(
        [FromBody] PredictionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _devOpsService.PredictAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in predict endpoint");
            return StatusCode(500, new { message = "An error occurred while generating prediction" });
        }
    }

    [HttpPost("recommend")]
    public async Task<IActionResult> Recommend(
        [FromBody] RecommendationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _devOpsService.RecommendAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in recommend endpoint");
            return StatusCode(500, new { message = "An error occurred while generating recommendations" });
        }
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory([FromQuery] int limit, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _devOpsService.GetHistoryAsync(limit == 0 ? 25 : limit, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in history endpoint");
            return StatusCode(500, new { message = "An error occurred while retrieving history" });
        }
    }

    [HttpDelete("history")]
    public async Task<IActionResult> ClearHistory(CancellationToken cancellationToken)
    {
        try
        {
            await _devOpsService.ClearHistoryAsync(cancellationToken);
            return Ok(new { message = "History successfully cleared" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing history");
            return StatusCode(500, new { message = "An error occurred while clearing history" });
        }
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _devOpsService.GetStatsAsync(cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in stats endpoint");
            return StatusCode(500, new { message = "An error occurred while retrieving stats" });
        }
    }
}
