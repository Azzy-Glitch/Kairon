using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AIDIP.SDK;

public static class AIDIPExtensions
{
    public static IServiceCollection AddAIDIP(
        this IServiceCollection services,
        Action<AIDIPOptions> configure)
    {
        services.Configure(configure);

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

                client.Timeout =
                    TimeSpan.FromSeconds(10);
            });

        return services;
    }

    public static IApplicationBuilder UseAIDIP(
        this IApplicationBuilder app)
    {
        return app.UseMiddleware<AIDIPMiddleware>();
    }
}