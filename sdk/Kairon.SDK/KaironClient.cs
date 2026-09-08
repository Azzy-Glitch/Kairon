using Microsoft.Extensions.Options;

namespace Kairon.SDK;

/// <summary>
/// Standalone, non-DI entry point for applications that are not themselves an ASP.NET Core host -
/// a worker, console app, or any process that wants Kairon telemetry without wiring
/// IServiceCollection. This is the .NET counterpart to sdk-python's plain `Kairon` class - named
/// KaironClient rather than bare "Kairon" because that name collides with this project's own
/// "Kairon.SDK" namespace root: any consumer outside the Kairon namespace tree that writes
/// `using Kairon.SDK;` then references a bare `Kairon` hits CS0118 ("'Kairon' is a namespace but
/// is used like a type"), confirmed by building a plain top-level-statements Program.cs against
/// this package. ASP.NET Core applications keep using AddKairon/UseKairon (KaironExtensions.cs)
/// unchanged; this class reuses the exact same building blocks - KaironTelemetryQueue,
/// KaironTelemetryClient, KaironTelemetrySender, KaironMetricsCollector - instead of duplicating
/// them, just wires them up imperatively instead of through the DI container.
///
/// Configuration precedence, matching the documented SDK behavior: explicit constructor
/// arguments, then KAIRON_ENDPOINT/KAIRON_PROJECT_ID/KAIRON_API_KEY environment variables, then a
/// previously stored paired credential (KaironCredentialStore), then - only if none of those
/// resolved a project and API key - redeeming the supplied pairingCode through the existing
/// KaironPairingClient.PairAsync, whose result is persisted for future runs. A pairing code is
/// therefore an initial-onboarding bootstrap, not something a later run needs again once a
/// credential is stored.
/// </summary>
public sealed class KaironClient : IDisposable, IAsyncDisposable
{
    public string Endpoint { get; }
    public Guid ProjectId { get; }

    private readonly KaironTelemetryQueue _queue;
    private readonly HttpClient _http;
    private readonly KaironTelemetrySender _sender;
    private readonly KaironMetricsCollector _collector;
    private bool _started;

    public KaironClient(
        string? pairingCode = null,
        string? endpoint = null,
        Guid? projectId = null,
        string? apiKey = null,
        string? applicationName = null,
        string? serviceName = null,
        string? environment = null,
        string? configPath = null)
    {
        var options = Resolve(pairingCode, endpoint, projectId, apiKey, configPath);
        if (applicationName is not null) options.ApplicationName = applicationName;
        if (serviceName is not null) options.ServiceName = serviceName;
        if (environment is not null) options.Environment = environment;

        Endpoint = options.Endpoint;
        ProjectId = options.ProjectId;

        var optionsAccessor = Options.Create(options);
        var metrics = new KaironMetrics();
        _queue = new KaironTelemetryQueue(optionsAccessor);
        _http = new HttpClient
        {
            BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(Math.Max(2, options.TimeoutSeconds + 1))
        };
        var client = new KaironTelemetryClient(_http, optionsAccessor);
        _sender = new KaironTelemetrySender(_queue, client);
        _collector = new KaironMetricsCollector(_queue, metrics, optionsAccessor);
    }

    /// <summary>Starts the background telemetry sender and metrics collector. Synchronous for
    /// parity with the documented `client.Start()` call; both background loops begin and this
    /// returns almost immediately.</summary>
    public void Start() => StartAsync().GetAwaiter().GetResult();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started) return;
        _started = true;
        await _sender.StartAsync(cancellationToken);
        await _collector.StartAsync(cancellationToken);
    }

    /// <summary>Stops both background loops and attempts a bounded drain. Returns false if
    /// anything was lost or the drain did not finish in time - mirrors the Python SDK's
    /// `stop(timeout_seconds=5)` contract.</summary>
    public bool Stop(int timeoutSeconds = 5) => StopAsync(TimeSpan.FromSeconds(timeoutSeconds)).GetAwaiter().GetResult();

    public async Task<bool> StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try { await _sender.StopAsync(cts.Token); } catch (OperationCanceledException) { }
        try { await _collector.StopAsync(cts.Token); } catch (OperationCanceledException) { }
        _http.Dispose();
        return _queue.FailedCount == 0 && _queue.DroppedCount == 0;
    }

    public ValueTask DisposeAsync() => new(StopAsync(TimeSpan.FromSeconds(5)));

    public void Dispose() => Stop();

    private static KaironOptions Resolve(string? pairingCode, string? endpoint, Guid? projectId, string? apiKey, string? configPath)
    {
        endpoint ??= Environment.GetEnvironmentVariable("KAIRON_ENDPOINT");

        var resolvedProjectId = projectId;
        if (resolvedProjectId is null)
        {
            var envProjectId = Environment.GetEnvironmentVariable("KAIRON_PROJECT_ID");
            if (!string.IsNullOrWhiteSpace(envProjectId) && Guid.TryParse(envProjectId, out var parsed))
                resolvedProjectId = parsed;
        }

        apiKey ??= Environment.GetEnvironmentVariable("KAIRON_API_KEY");

        var path = configPath ?? KaironCredentialStore.DefaultPath();

        if (resolvedProjectId is null || string.IsNullOrWhiteSpace(apiKey))
        {
            var stored = KaironCredentialStore.Load(path);
            if (stored is { } credential)
            {
                endpoint ??= credential.Endpoint;
                resolvedProjectId ??= credential.ProjectId;
                apiKey ??= credential.ApiKey;
            }
        }

        if ((resolvedProjectId is null || string.IsNullOrWhiteSpace(apiKey)) && !string.IsNullOrWhiteSpace(pairingCode))
        {
            var paired = KaironPairingClient.PairAsync(endpoint ?? "http://localhost:8000", pairingCode)
                .GetAwaiter().GetResult();
            if (!paired.Success)
                throw new InvalidOperationException(
                    $"Kairon pairing failed: {paired.Error ?? "the pairing code was rejected."} " +
                    "Generate a new pairing code from Kairon and try again.");

            // Persisted before being used - a failure here throws and the constructor never
            // completes, so pairing is never reported as successful without a durable credential.
            KaironCredentialStore.Save(path, paired.Endpoint!, paired.ProjectId, paired.ApiKey!);

            endpoint = paired.Endpoint;
            resolvedProjectId = paired.ProjectId;
            apiKey = paired.ApiKey;
        }

        if (resolvedProjectId is null || string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Kairon needs a project and API key. Provide projectId/apiKey directly, set " +
                "KAIRON_PROJECT_ID/KAIRON_API_KEY, pass pairingCode from a Kairon-generated pairing " +
                "code, or pair once so the stored configuration can be reused.");

        return new KaironOptions
        {
            Endpoint = endpoint ?? "http://localhost:8000",
            ProjectId = resolvedProjectId.Value,
            ApiKey = apiKey
        };
    }
}
