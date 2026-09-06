using Kairon.Backend.Services.Audit;

namespace Kairon.Backend.Services.Orchestration;

/// <summary>
/// Drains the incident processing queue. This is where detection, correlation and AI investigation
/// actually run, off the request thread, so telemetry ingestion never waits on a model call
/// (PRD section 6).
/// </summary>
public class IncidentProcessingWorker : BackgroundService
{
    private readonly IIncidentProcessingQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IncidentProcessingWorker> _logger;

    public IncidentProcessingWorker(
        IIncidentProcessingQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<IncidentProcessingWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Incident processing worker started");

        await foreach (var item in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(item, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A single bad work item must never stop the worker; the next incident still needs
                // processing.
                _logger.LogError(ex, "Failed to process work item {Kind} for project {ProjectId}",
                    item.Kind, item.ProjectId);
            }
            finally
            {
                _queue.Complete(item);
            }
        }

        _logger.LogInformation("Incident processing worker stopped");
    }

    private async Task ProcessAsync(IncidentWorkItem item, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IIncidentOrchestrator>();

        switch (item.Kind)
        {
            case WorkItemKind.EvaluateDetection:
                await orchestrator.DetectAndCorrelateAsync(
                    item.ProjectId, item.Environment, item.Service, cancellationToken);
                break;

            case WorkItemKind.ProcessIncident when item.IncidentId.HasValue:
                await orchestrator.InvestigateAsync(item.IncidentId.Value, cancellationToken);
                break;

            case WorkItemKind.ReinvestigateIncident when item.IncidentId.HasValue:
                await orchestrator.ReinvestigateAsync(item.IncidentId.Value, cancellationToken);
                break;

            case WorkItemKind.ExecuteRemediation when item.IncidentId.HasValue && item.ActionId.HasValue:
                await orchestrator.ExecuteAndVerifyAsync(item.IncidentId.Value, item.ActionId.Value, cancellationToken);
                break;

            default:
                _logger.LogWarning("Ignoring malformed work item {Kind}", item.Kind);
                break;
        }
    }
}

/// <summary>
/// Periodic safety net for detection. Telemetry ingestion enqueues an evaluation on every write,
/// but a sweep guarantees that a quiet period followed by a threshold breach is still noticed even
/// if an enqueue was dropped by a full queue.
/// </summary>
public class DetectionSweepWorker : BackgroundService
{
    private readonly IIncidentProcessingQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DetectionSweepWorker> _logger;

    public DetectionSweepWorker(
        IIncidentProcessingQueue queue,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<DetectionSweepWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = _configuration.GetValue("Detection:SweepIntervalSeconds", 20);
        if (intervalSeconds <= 0)
        {
            _logger.LogInformation("Detection sweep disabled by configuration");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<Infrastructure.AppDbContext>();

                var cutoff = DateTime.UtcNow.AddMinutes(-10);

                // Only sweep scopes that have produced telemetry recently. Sweeping every project
                // that ever existed would turn a safety net into a table scan.
                var scopes = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                    db.Metrics
                        .Where(m => m.Timestamp >= cutoff)
                        .Select(m => new { m.ProjectId, m.Environment, m.Service })
                        .Distinct(),
                    stoppingToken);

                foreach (var s in scopes)
                {
                    _queue.TryEnqueue(new IncidentWorkItem(
                        WorkItemKind.EvaluateDetection, s.ProjectId, s.Environment, s.Service));
                }

                // Incidents wait out an evidence window before their first investigation, so the
                // sweep is what picks them up once that window has passed.
                var investigationDelay = _configuration.GetValue("AiOrchestration:InvestigationDelaySeconds", 15);
                var ready = DateTime.UtcNow.AddSeconds(-investigationDelay);

                var pending = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                    db.SreIncidents
                        .Where(i => i.Status == Models.Sre.IncidentStatus.Detected && i.Timestamp <= ready)
                        .OrderBy(i => i.Timestamp)
                        .Select(i => new { i.Id, i.ProjectId, i.Environment, i.Service })
                        .Take(50),
                    stoppingToken);

                foreach (var incident in pending)
                {
                    _queue.TryEnqueue(new IncidentWorkItem(
                        WorkItemKind.ProcessIncident, incident.ProjectId, incident.Environment,
                        incident.Service, incident.Id));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Detection sweep failed: {Error}", Redaction.Describe(ex));
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return await timer.WaitForNextTickAsync(token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
