using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Kairon.SDK;

public static class KaironExtensions
{
    /// <summary>
    /// Registers Kairon telemetry collection. Two lines in a host application - this plus
    /// <see cref="UseKairon"/> - and nothing else changes about how the application runs.
    /// </summary>
    public static IServiceCollection AddKairon(
        this IServiceCollection services,
        Action<KaironOptions> configure)
    {
        services.Configure(configure);

        services.AddSingleton<IKaironTelemetryQueue, KaironTelemetryQueue>();
        services.AddSingleton<IKaironMetrics, KaironMetrics>();

        services.AddHttpClient<KaironTelemetryClient>(
            (serviceProvider, client) =>
            {
                var options =
                    serviceProvider
                        .GetRequiredService<IOptions<KaironOptions>>()
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
        services.AddHostedService<KaironTelemetrySender>();
        services.AddHostedService<KaironMetricsCollector>();

        return services;
    }

    public static IApplicationBuilder UseKairon(
        this IApplicationBuilder app)
    {
        return app.UseMiddleware<KaironMiddleware>();
    }
}
