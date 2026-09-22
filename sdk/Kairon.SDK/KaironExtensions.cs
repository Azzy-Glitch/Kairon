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
        ArgumentNullException.ThrowIfNull(configure);

        // Preserve the established ASP.NET options timing: the caller's callback still runs when
        // options are resolved. Stored-credential resolution is a post-configuration layer, so a
        // complete explicit ProjectId/ApiKey remains authoritative exactly as before.
        services.AddOptions<KaironOptions>()
            .Configure(configure)
            .PostConfigure(options => KaironConfigurationResolver.ResolveForDependencyInjection(options));

        return AddKaironServices(services);
    }

    /// <summary>
    /// Asynchronously redeems a first-run pairing code, confirms and durably stores the resulting
    /// connection, then registers the normal ASP.NET Core telemetry integration. Later runs should
    /// call <see cref="AddKairon(IServiceCollection, Action{KaironOptions})"/> without the pairing
    /// code; it reuses the same protected stored credential automatically.
    /// </summary>
    public static async Task<IServiceCollection> AddKaironAsync(
        this IServiceCollection services,
        string pairingCode,
        Action<KaironOptions> configure,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingCode);
        return await AddKaironAsyncCore(services, pairingCode, configure, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronous stored-credential registration. Normally a completed credential should use
    /// synchronous <see cref="AddKairon(IServiceCollection, Action{KaironOptions})"/>. Use this
    /// overload when startup must recover a previously lost pairing-confirmation response; no
    /// pairing code is generated or redeemed.
    /// </summary>
    public static Task<IServiceCollection> AddKaironAsync(
        this IServiceCollection services,
        Action<KaironOptions> configure,
        CancellationToken cancellationToken = default) =>
        AddKaironAsyncCore(services, null, configure, cancellationToken);

    private static async Task<IServiceCollection> AddKaironAsyncCore(
        IServiceCollection services,
        string? pairingCode,
        Action<KaironOptions> configure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new KaironOptions();
        configure(options);

        var connection = await KaironConfigurationResolver.ResolveAsync(
                pairingCode,
                string.Equals(options.Endpoint, KaironConfigurationResolver.DefaultEndpoint,
                    StringComparison.OrdinalIgnoreCase) ? null : options.Endpoint,
                options.ProjectId == Guid.Empty ? null : options.ProjectId,
                options.ApiKey,
                options.CredentialPath,
                cancellationToken)
            .ConfigureAwait(false);

        KaironConfigurationResolver.ApplyConnection(options, connection);
        services.AddOptions<KaironOptions>().Configure(target => CopyOptions(options, target));
        return AddKaironServices(services);
    }

    private static IServiceCollection AddKaironServices(IServiceCollection services)
    {
        services.AddSingleton<IKaironTelemetryQueue, KaironTelemetryQueue>();
        services.AddSingleton<IKaironMetrics, KaironMetrics>();

        // A NAMED client rather than services.AddHttpClient<KaironTelemetryClient>(): the typed-
        // client form constructs KaironTelemetryClient via ActivatorUtilities, which only ever
        // finds a PUBLIC constructor - exactly the escape hatch KaironTelemetryClient's constructor
        // is internal to close (see its own remarks). Registering the HttpClient under a name and
        // then building KaironTelemetryClient with an explicit `new` below keeps every other
        // behavior identical (IHttpClientFactory-managed pooling/handler lifetime, the same
        // endpoint validation, the same non-redirecting primary handler) while the internal
        // constructor stays genuinely unreachable from outside this assembly.
        const string HttpClientName = "Kairon.SDK.Telemetry";

        services.AddHttpClient(HttpClientName, (serviceProvider, client) =>
            {
                var options =
                    serviceProvider
                        .GetRequiredService<IOptions<KaironOptions>>()
                        .Value;

                // Defense in depth: resolution validates every endpoint, and the actual transport
                // validates again immediately before assigning BaseAddress.
                KaironEndpointSecurity.EnsureAllowed(options.Endpoint);

                client.BaseAddress =
                    new Uri(
                        options.Endpoint.TrimEnd('/') + "/");

                // Slightly above the per-send timeout so the SDK's own bound is the one that
                // actually applies, and the failure is reported rather than thrown by HttpClient.
                client.Timeout =
                    TimeSpan.FromSeconds(Math.Max(2, options.TimeoutSeconds + 1));
            })
            .ConfigurePrimaryHttpMessageHandler(KaironEndpointSecurity.CreateNonRedirectingHandler);

        services.AddSingleton(serviceProvider => new KaironTelemetryClient(
            serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
            serviceProvider.GetRequiredService<IOptions<KaironOptions>>()));

        // The sender drains the queue; the collector emits process metrics. Both are best-effort
        // background services that never propagate a fault to the host.
        services.AddHostedService<KaironTelemetrySender>();
        services.AddHostedService<KaironMetricsCollector>();

        return services;
    }

    private static void CopyOptions(KaironOptions source, KaironOptions target)
    {
        target.Endpoint = source.Endpoint;
        target.ApiKey = source.ApiKey;
        target.ProjectId = source.ProjectId;
        target.CredentialPath = source.CredentialPath;
        target.MachineId = source.MachineId;
        target.EnableTelemetry = source.EnableTelemetry;
        target.CaptureRequestBody = source.CaptureRequestBody;
        target.CaptureResponseBody = source.CaptureResponseBody;
        target.ApplicationName = source.ApplicationName;
        target.ServiceName = source.ServiceName;
        target.Environment = source.Environment;
        target.TimeoutSeconds = source.TimeoutSeconds;
        target.QueueCapacity = source.QueueCapacity;
        target.MaxBodyCharacters = source.MaxBodyCharacters;
        target.SuccessSampleRate = source.SuccessSampleRate;
        target.EnableMetrics = source.EnableMetrics;
        target.MetricsIntervalSeconds = source.MetricsIntervalSeconds;
        target.IgnoredPathPrefixes = new List<string>(source.IgnoredPathPrefixes);
    }

    public static IApplicationBuilder UseKairon(
        this IApplicationBuilder app)
    {
        return app.UseMiddleware<KaironMiddleware>();
    }
}
