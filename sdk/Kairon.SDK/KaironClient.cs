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
/// Configuration precedence (required order):
///   1. explicit pairingCode - always wins, (re)pairing immediately before anything else below
///      is even consulted (e.g. to force a re-pair over an existing stored credential after it
///      was revoked from the KAIRON UI).
///   2. a previously stored credential (KaironCredentialStore), if one exists - preferred as a
///      whole over ordinary configuration. It represents a real, completed pairing event, the
///      strongest available signal of intended identity once no pairingCode is given; ProjectId
///      and ApiKey are always read from the SAME source together (stored, or explicit/env - never
///      one field from each), which is what makes a mixed configuration - an explicit/env
///      ProjectId paired with a DIFFERENT pairing's stored ApiKey, or vice versa - structurally
///      impossible rather than merely unlikely.
///   3. ordinary explicit constructor arguments, then KAIRON_ENDPOINT/KAIRON_PROJECT_ID/
///      KAIRON_API_KEY environment variables - consulted only when neither of the above applies
///      (first-time onboarding, or a fresh configPath with nothing stored yet).
/// Pairing is always explicit, never automatic: nothing in this SDK ever supplies pairingCode on
/// the caller's behalf (not on HTTP 401, not on startup with a still-valid credential) - it is
/// consulted here only because the caller passed it in this exact call.
/// </summary>
public sealed class KaironClient : IDisposable, IAsyncDisposable
{
    public string Endpoint { get; }
    public Guid ProjectId { get; }

    /// <summary>The most recent delivery failure's safe diagnostic message (e.g. "Kairon server
    /// returned 401. (project authentication rejected)"), or null once delivery has since
    /// succeeded. Never contains the API key. This is a diagnostic only - nothing reads it to
    /// decide whether to re-pair; pairing is always explicit (see the KaironClient class remarks).</summary>
    public string? LastDeliveryError => _queue.LastDeliveryError;

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
        var path = configPath ?? KaironCredentialStore.DefaultPath();

        if (!string.IsNullOrWhiteSpace(pairingCode))
        {
            var resolvedEndpointForPairing = endpoint ?? Environment.GetEnvironmentVariable("KAIRON_ENDPOINT") ?? "http://localhost:8000";
            var paired = KaironPairingClient.PairAsync(resolvedEndpointForPairing, pairingCode)
                .GetAwaiter().GetResult();
            if (!paired.Success)
                throw new InvalidOperationException(
                    $"Kairon pairing failed: {paired.Error ?? "the pairing code was rejected."} " +
                    "Generate a new pairing code from Kairon and try again.");

            // Persisted before being used - a failure here throws and the constructor never
            // completes, so pairing is never reported as successful without a durable credential.
            // The freshly redeemed values win outright, replacing whatever explicit args/env vars
            // resolved above - an explicit pairingCode is a direct instruction to (re)pair now, not
            // a fallback consulted only when everything else came up empty.
            KaironCredentialStore.Save(path, paired.Endpoint!, paired.ProjectId, paired.ApiKey!);

            // Best-effort proof of receipt/persistence for an operator-driven re-pair completion -
            // see KaironPairingClient.ConfirmAsync's remarks. Never throws, never blocks longer
            // than its own short timeout.
            KaironPairingClient.ConfirmAsync(paired.Endpoint!, paired.PairingId, paired.ApiKey!).GetAwaiter().GetResult();

            return new KaironOptions { Endpoint = paired.Endpoint!, ProjectId = paired.ProjectId, ApiKey = paired.ApiKey! };
        }

        // A previously stored credential - if one exists - represents a real, completed pairing
        // and is preferred wholesale over ordinary configuration. ProjectId and ApiKey always come
        // from the SAME source together here - never an explicit/env ProjectId combined with a
        // stored ApiKey (or vice versa) belonging to a different pairing.
        var stored = KaironCredentialStore.Load(path);
        if (stored is { } credential)
        {
            var storedEndpoint = endpoint ?? Environment.GetEnvironmentVariable("KAIRON_ENDPOINT") ?? credential.Endpoint;
            return new KaironOptions { Endpoint = storedEndpoint, ProjectId = credential.ProjectId, ApiKey = credential.ApiKey };
        }

        // First-time onboarding: no pairing code, nothing stored yet - ordinary explicit
        // arguments, then environment variables.
        endpoint ??= Environment.GetEnvironmentVariable("KAIRON_ENDPOINT");

        var resolvedProjectId = projectId;
        if (resolvedProjectId is null)
        {
            var envProjectId = Environment.GetEnvironmentVariable("KAIRON_PROJECT_ID");
            if (!string.IsNullOrWhiteSpace(envProjectId) && Guid.TryParse(envProjectId, out var parsed))
                resolvedProjectId = parsed;
        }

        apiKey ??= Environment.GetEnvironmentVariable("KAIRON_API_KEY");

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
