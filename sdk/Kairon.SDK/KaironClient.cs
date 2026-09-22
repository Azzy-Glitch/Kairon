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
///
/// Two ways to construct one: the synchronous constructor below (kept for backward compatibility
/// and simple console-app/worker startup, where blocking briefly during construction is normal and
/// there is no captured synchronization context to deadlock against), and the async
/// <see cref="CreateAsync"/> factory (preferred for new code, and required in any host with a
/// synchronization context - e.g. a UI thread - where blocking on async pairing/confirmation work
/// via a synchronous wrapper risks a real deadlock). Both resolve configuration identically
/// (<see cref="KaironConfigurationResolver"/> is the single implementation; the synchronous constructor is a thin
/// blocking wrapper over it) and both fully support pairing-code redemption, confirmation, and
/// confirmation recovery.
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
    private readonly KaironOptions _options;
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
        : this(KaironConfigurationResolver.ResolveAsync(pairingCode, endpoint, projectId, apiKey, configPath, default).GetAwaiter().GetResult(),
              applicationName, serviceName, environment)
    {
    }

    /// <summary>Fully asynchronous equivalent of the constructor above - resolves pairing/
    /// confirmation/stored-credential lookup without ever blocking a thread on async work, so it
    /// is safe to call from a host with a synchronization context (a UI thread, an ASP.NET Core
    /// classic request) where the synchronous constructor's internal blocking could deadlock.
    /// Preferred for new code; the constructor remains for simple synchronous startup and existing
    /// callers.</summary>
    public static async Task<KaironClient> CreateAsync(
        string? pairingCode = null,
        string? endpoint = null,
        Guid? projectId = null,
        string? apiKey = null,
        string? applicationName = null,
        string? serviceName = null,
        string? environment = null,
        string? configPath = null,
        CancellationToken cancellationToken = default)
    {
        var options = await KaironConfigurationResolver.ResolveAsync(
            pairingCode, endpoint, projectId, apiKey, configPath, cancellationToken).ConfigureAwait(false);
        return new KaironClient(options, applicationName, serviceName, environment);
    }

    /// <summary>Purely synchronous wiring over an already-resolved KaironOptions - no resolution
    /// work, no async, nothing that could block. Shared by both the synchronous constructor and
    /// <see cref="CreateAsync"/> so there is exactly one place that builds the queue/sender/
    /// collector/HttpClient graph.</summary>
    private KaironClient(KaironOptions options, string? applicationName, string? serviceName, string? environment)
    {
        if (applicationName is not null) options.ApplicationName = applicationName;
        if (serviceName is not null) options.ServiceName = serviceName;
        if (environment is not null) options.Environment = environment;

        Endpoint = options.Endpoint;
        ProjectId = options.ProjectId;
        _options = options;

        var optionsAccessor = Options.Create(options);
        var metrics = new KaironMetrics();
        _queue = new KaironTelemetryQueue(optionsAccessor);
        _http = new HttpClient(KaironEndpointSecurity.CreateNonRedirectingHandler(), disposeHandler: true)
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
        await _sender.StartAsync(cancellationToken).ConfigureAwait(false);
        await _collector.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops both background loops and attempts a bounded drain. Returns false if
    /// anything was lost or the drain did not finish in time - mirrors the Python SDK's
    /// `stop(timeout_seconds=5)` contract.</summary>
    public bool Stop(int timeoutSeconds = 5) => StopAsync(TimeSpan.FromSeconds(timeoutSeconds)).GetAwaiter().GetResult();

    public async Task<bool> StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try { await _sender.StopAsync(cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
        try { await _collector.StopAsync(cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
        _http.Dispose();
        return _queue.FailedCount == 0 && _queue.DroppedCount == 0;
    }

    /// <summary>
    /// Manually records an exception/incident - the .NET counterpart to sdk-python's
    /// <c>Kairon.capture_exception</c>. <see cref="KaironMiddleware"/> already does the equivalent
    /// enqueue automatically for every instrumented HTTP request in an ASP.NET Core host
    /// (<c>AddKairon</c>/<c>UseKairon</c>); this exists because <see cref="KaironClient"/> is
    /// documented as the entry point for applications that are NOT themselves an ASP.NET Core host
    /// - a worker, console app, or scheduled job - and until this method existed, nothing on this
    /// SDK's public surface let one of those actually report an incident: <see cref="Start"/> only
    /// switches on the background PROCESS-metrics collector, which knows nothing about a caught
    /// exception or a business-level failure the host code itself observed.
    ///
    /// Never throws and never blocks beyond a bounded, non-blocking enqueue (the same contract as
    /// every other telemetry path in this SDK) - queue-full drops the oldest pending item exactly
    /// like <see cref="KaironMiddleware"/>'s own enqueue.
    /// </summary>
    public void CaptureException(Exception exception, string endpoint = "", string method = "",
        int statusCode = 500, long durationMs = 0)
    {
        if (!_options.EnableTelemetry) return;

        _queue.TryEnqueue(new Models.TelemetryPayload
        {
            ProjectId = _options.ProjectId,
            ApplicationName = KaironIdentity.ResolveApplication(_options),
            Service = KaironIdentity.ResolveService(_options),
            Environment = KaironIdentity.ResolveEnvironment(_options),
            Endpoint = endpoint,
            Method = method,
            StatusCode = statusCode,
            Duration = durationMs,
            Error = exception.Message,
            ExceptionType = exception.GetType().FullName,
            StackTrace = exception.StackTrace,
            Timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Manually records a metric sample - the .NET counterpart to sdk-python's
    /// <c>Kairon.record_metric</c>, for the same non-ASP.NET-Core hosts <see cref="CaptureException"/>
    /// targets. <see cref="Start"/>'s automatic process-metrics collection covers CPU/memory on an
    /// interval; this is for a caller-observed value (a request duration, a queue depth, a retry
    /// count) the collector has no way to know about on its own.
    /// </summary>
    public void RecordMetric(double? cpuPercent = null, double? memoryPercent = null,
        double? responseTimeMs = null, long requestCount = 0, long errorCount = 0,
        long? retryCount = null, long? queueDepth = null, string? component = null)
    {
        if (!_options.EnableTelemetry) return;

        _queue.TryEnqueueMetric(new Models.MetricPayload
        {
            ProjectId = _options.ProjectId,
            Timestamp = DateTime.UtcNow,
            CpuPercent = cpuPercent,
            MemoryPercent = memoryPercent,
            ResponseTimeMs = responseTimeMs,
            RequestCount = requestCount,
            ErrorCount = errorCount,
            RetryCount = retryCount,
            QueueDepth = queueDepth,
            Environment = KaironIdentity.ResolveEnvironment(_options),
            Application = KaironIdentity.ResolveApplication(_options),
            Service = KaironIdentity.ResolveService(_options),
            Component = component
        });
    }

    public ValueTask DisposeAsync() => new(StopAsync(TimeSpan.FromSeconds(5)));

    public void Dispose() => Stop();

}
