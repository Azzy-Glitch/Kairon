using AIDIP.SDK;

namespace AIDIP.DemoApp;

/// <summary>
/// Generates steady order-processing traffic against this application's own endpoint, so the
/// scenario produces continuous telemetry without a human clicking anything (PRD section 20).
///
/// It also reports the two signals only the application can know - retries performed and queue
/// depth - through the SDK's metrics seam, which is what lets the backend's retry-storm and
/// backlog rules fire on real reported data rather than inference.
/// </summary>
public class OrderTrafficGenerator : BackgroundService
{
    private readonly DemoScenario _scenario;
    private readonly IAIDIPMetrics _metrics;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OrderTrafficGenerator> _logger;

    public OrderTrafficGenerator(
        DemoScenario scenario,
        IAIDIPMetrics metrics,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<OrderTrafficGenerator> logger)
    {
        _scenario = scenario;
        _metrics = metrics;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Demo:GenerateTraffic", true))
        {
            _logger.LogInformation("Demo traffic generation is disabled");
            return;
        }

        var intervalMs = _configuration.GetValue("Demo:TrafficIntervalMs", 1000);
        var selfUrl = _configuration["Demo:SelfUrl"] ?? "http://localhost:5080";

        var client = _httpClientFactory.CreateClient("self");
        client.BaseAddress = new Uri(selfUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(15);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(100, intervalMs)));

        _logger.LogInformation("Demo traffic generator started against {Url}", selfUrl);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    // Calling our own endpoint means the SDK middleware instruments it exactly as
                    // it would instrument a real caller - no special-cased telemetry path.
                    using var response = await client.PostAsync("api/orders/process", null, stoppingToken);
                    _ = response.StatusCode;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // A failed self-call is part of the scenario, not an error in the generator.
                }

                // Report the application-only signals for this interval.
                _metrics.RecordRetries(_scenario.DrainRetries());
                _metrics.ReportQueueDepth(_scenario.QueueDepth);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
