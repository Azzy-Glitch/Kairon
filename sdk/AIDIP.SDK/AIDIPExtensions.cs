using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AIDIP.SDK;

public static class AIDIPExtensions
{
    /// <summary>
    /// Registers AIDIP telemetry collection. Two lines in a host application - this plus
    /// <see cref="UseAIDIP"/> - and nothing else changes about how the application runs.
    /// </summary>
    public static IServiceCollection AddAIDIP(
        this IServiceCollection services,
        Action<AIDIPOptions> configure)
    {
        services.Configure(configure);

        services.AddSingleton<IAIDIPTelemetryQueue, AIDIPTelemetryQueue>();
        services.AddSingleton<IAIDIPMetrics, AIDIPMetrics>();

        services.AddHttpClient<AIDIPTelemetryClient>(
            (serviceProvider, client) =>
            {
                var options =
                    serviceProvider
                        .GetRequiredService<IOptions<AIDIPOptions>>()
                        .Value;

                client.BaseAddress =
                    new Uri(
                        options.Endpoint.TrimEnd('/') + "/");

                // Slightly above the per-send timeout so the SDK's own bound is the one that
                // actually applies, and the failure is reported rather than thrown by HttpClient.
                client.Timeout =
                    TimeSpan.FromSeconds(Math.Max(2, options.TimeoutSeconds + 1));
            });

        // The sender drains the queue; the collector emits process metrics. Both are best-effort
        // background services that never propagate a fault to the host.
        services.AddHostedService<AIDIPTelemetrySender>();
        services.AddHostedService<AIDIPMetricsCollector>();

        return services;
    }

    public static IApplicationBuilder UseAIDIP(
        this IApplicationBuilder app)
    {
        return app.UseMiddleware<AIDIPMiddleware>();
    }
}
