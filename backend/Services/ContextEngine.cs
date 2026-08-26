using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models;
using Microsoft.EntityFrameworkCore;

namespace Kairon.Backend.Services;

public interface IContextEngine
{
    Task<EnrichedContext> BuildContextAsync(
        Guid projectId,
        string endpoint,
        Incident? currentIncident = null,
        CancellationToken cancellationToken = default);
}

public class ContextEngine : IContextEngine
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<ContextEngine> _logger;

    public ContextEngine(AppDbContext dbContext, ILogger<ContextEngine> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<EnrichedContext> BuildContextAsync(
        Guid projectId,
        string endpoint,
        Incident? currentIncident = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var context = new EnrichedContext
            {
                ProjectId = projectId,
                Endpoint = endpoint,
                CurrentIncident = currentIncident,
                Environment = "Development"
            };

            // Get recent incidents for this endpoint
            var recentIncidents = await _dbContext.Incidents
                .Where(i => i.ProjectId == projectId && i.Endpoint == endpoint)
                .OrderByDescending(i => i.Timestamp)
                .Take(20)
                .ToListAsync(cancellationToken);

            context.RecentIncidents = recentIncidents;
            context.FailureCount = recentIncidents.Count(i => i.StatusCode >= 400);

            // Get recent metrics
            var recentMetrics = await _dbContext.Metrics
                .Where(m => m.ProjectId == projectId)
                .OrderByDescending(m => m.Timestamp)
                .Take(50)
                .ToListAsync(cancellationToken);

            context.RecentMetrics = recentMetrics;

            if (recentMetrics.Any())
            {
                context.AverageLatency = recentMetrics
                    .Where(m => m.ResponseTimeMs.HasValue)
                    .Average(m => m.ResponseTimeMs ?? 0);

                var totalRequests = recentMetrics.Sum(m => m.RequestCount);
                var totalErrors = recentMetrics.Sum(m => m.ErrorCount);
                context.RecentErrorRate = totalRequests > 0
                    ? (double)totalErrors / totalRequests
                    : 0;
            }

            return context;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building context for project {ProjectId}, endpoint {Endpoint}",
                projectId, endpoint);
            return new EnrichedContext
            {
                ProjectId = projectId,
                Endpoint = endpoint,
                RecentIncidents = new List<Incident>(),
                RecentMetrics = new List<Metric>()
            };
        }
    }
}