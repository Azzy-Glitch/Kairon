using Kairon.Backend.Infrastructure;
using Kairon.Backend.Services.Evidence;
using Microsoft.AspNetCore.Mvc;

namespace Kairon.Backend.Controllers;

/// <summary>Emerging operational risks computed from stored telemetry and incident history.</summary>
[ApiController]
[Route("api/insights")]
[RequiresOperator]
public sealed class InsightsController(IPredictiveInsightsService insights) : ControllerBase
{
    [HttpGet("risks")]
    public async Task<ActionResult<EmergingRisksDto>> Risks([FromQuery] Guid? projectId, CancellationToken cancellationToken) =>
        Ok(await insights.GetEmergingRisksAsync(projectId, cancellationToken));
}
