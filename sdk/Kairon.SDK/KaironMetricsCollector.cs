using System.Diagnostics;
using Kairon.SDK.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Kairon.SDK;

/// <summary>
/// Counters the middleware updates and the metrics collector drains. Also the seam a host
/// application uses to report the signals only it can know - retries and queue depth - which are
/// exactly the signals the retry-storm and backlog detection rules need.
/// </summary>
public interface IKaironMetrics
{
    void RecordRequest(long durationMs, bool isError);

    /// <summary>Reports retries the application performed since the last call.</summary>
    void RecordRetries(long count);

    /// <summary>Reports the current depth of the application's pending work queue.</summary>
    void ReportQueueDepth(long depth);

    /// <summary>Called by the SDK middleware when a request starts; requests in progress are the
    /// automatic queue depth. No application needs to call this.</summary>
    void BeginRequest() { }

    /// <summary>Called by the SDK middleware when a request finishes.</summary>
    void EndRequest() { }
}

public class KaironMetrics : IKaironMetrics
{
    private long _requests;
    private long _errors;
    private long _totalDurationMs;
    private long _retries;
    private long _queueDepth = -1;

    // Once an application has reported retries at all, a later interval with none is a meaningful
    // zero rather than an absence. Reporting null in that case makes "we stopped retrying" look
    // identical to "this application does not track retries", which is exactly the distinction
    // post-remediation verification needs.
    private long _retriesEverReported;

    // Measured automatically (KaironAutoSignals): requests in progress and retries of failed
    // outgoing calls. A value the application reports itself always wins.
    private readonly KaironAutoSignals? _signals;
    private long _inflight;
    private long _inflightPeak;
    private long _observedRetries;
    private int _outgoingObserved;

    public KaironMetrics() : this(null) { }

    public KaironMetrics(KaironAutoSignals? signals) => _signals = signals;

    /// <summary>True while <see cref="KaironOutgoingCallObserver"/> is watching outgoing calls.</summary>
    internal bool OutgoingCallsObserved
    {
        get => Volatile.Read(ref _outgoingObserved) == 1;
        set => Volatile.Write(ref _outgoingObserved, value ? 1 : 0);
    }

    public void BeginRequest()
    {
        var current = Interlocked.Increment(ref _inflight);
        long peak;
        while (current > (peak = Interlocked.Read(ref _inflightPeak)) &&
               Interlocked.CompareExchange(ref _inflightPeak, current, peak) != peak) { }
    }

    public void EndRequest()
    {
        long current;
        do current = Interlocked.Read(ref _inflight);
        while (current > 0 && Interlocked.CompareExchange(ref _inflight, current - 1, current) != current);
    }

    internal void RecordObservedRetry() => Interlocked.Increment(ref _observedRetries);

    public void RecordRequest(long durationMs, bool isError)
    {
        Interlocked.Increment(ref _requests);
        Interlocked.Add(ref _totalDurationMs, durationMs);

        if (isError)
            Interlocked.Increment(ref _errors);
    }

    public void RecordRetries(long count)
    {
        if (count <= 0)
            return;

        Interlocked.Add(ref _retries, count);
        Interlocked.Exchange(ref _retriesEverReported, 1);
    }

    public void ReportQueueDepth(long depth) => Interlocked.Exchange(ref _queueDepth, depth);

    /// <summary>Atomically reads and resets the counters, so no interval double-counts.</summary>
    public MetricsSnapshot Drain()
    {
        var requests = Interlocked.Exchange(ref _requests, 0);
        var errors = Interlocked.Exchange(ref _errors, 0);
        var duration = Interlocked.Exchange(ref _totalDurationMs, 0);
        var retries = Interlocked.Exchange(ref _retries, 0);
        var observed = Interlocked.Exchange(ref _observedRetries, 0);
        var autoRetries = _signals?.AutoRetries == true && OutgoingCallsObserved;
        if (autoRetries) retries += observed;

        // Queue depth is a gauge, not a counter: it is read, not reset, because the application's
        // current backlog is still whatever it was after the interval ends. Without an application
        // value, the peak of requests in progress since the last sample is the automatic one.
        var queue = Interlocked.Read(ref _queueDepth);
        var peak = Interlocked.Exchange(ref _inflightPeak, Interlocked.Read(ref _inflight));
        long? queueDepth = queue >= 0 ? queue : _signals?.AutoQueueDepth == true ? peak : null;

        return new MetricsSnapshot(
            requests,
            errors,
            requests > 0 ? (double)duration / requests : 0,
            retries,
            queueDepth,
            RetriesTracked: autoRetries || Interlocked.Read(ref _retriesEverReported) == 1);
    }
}

/// <summary>One interval's worth of counters.</summary>
public record MetricsSnapshot(
    long Requests,
    long Errors,
    double AvgDurationMs,
    long Retries,
    long? QueueDepth,
    /// <summary>True once the application has reported retries at least once in this process.</summary>
    bool RetriesTracked = false);

/// <summary>
/// Emits process CPU, memory and throughput on an interval (PRD section 4.1: the SDK collects
/// metrics). Sampling is process-local and cheap; nothing here reads the host machine or the OS
/// beyond the current process's own counters.
/// </summary>
public class KaironMetricsCollector : BackgroundService
{
    private readonly IKaironTelemetryQueue _queue;
    private readonly IKaironMetrics _metrics;
    private readonly KaironOptions _options;
    private readonly KaironAutoSignals? _signals;
    private readonly KaironTelemetryClient? _client;
    private static readonly TimeSpan SettingsRefreshInterval = TimeSpan.FromSeconds(60);
    private DateTime _settingsRefreshedAt = DateTime.MinValue;
    private KaironOutgoingCallObserver? _outgoing;

    private TimeSpan _lastCpuTime;
    private DateTime _lastSampleAt;

    public KaironMetricsCollector(
        IKaironTelemetryQueue queue,
        IKaironMetrics metrics,
        IOptions<KaironOptions> options)
    {
        _queue = queue;
        _metrics = metrics;
        _options = options.Value;
    }

    public KaironMetricsCollector(
        IKaironTelemetryQueue queue,
        IKaironMetrics metrics,
        IOptions<KaironOptions> options,
        KaironAutoSignals signals,
        KaironTelemetryClient client) : this(queue, metrics, options)
    {
        _signals = signals;
        _client = client;
    }

    /// <summary>Fetches this app's automatic-signal settings (every minute) and starts or stops
    /// watching outgoing calls to match. Best effort: never throws.</summary>
    private async Task RefreshAutoSignalsAsync(CancellationToken stoppingToken)
    {
        if (_signals is null) return;
        try
        {
            if (_client is not null && DateTime.UtcNow - _settingsRefreshedAt >= SettingsRefreshInterval)
            {
                _settingsRefreshedAt = DateTime.UtcNow;
                await _client.RefreshAutoSignalsAsync(_signals, stoppingToken);
            }

            if (_signals.AutoRetries && _outgoing is null && _metrics is KaironMetrics metrics)
            {
                _outgoing = new KaironOutgoingCallObserver(metrics, _signals, _options.Endpoint);
                _outgoing.Start();
            }
            else if (!_signals.AutoRetries && _outgoing is not null)
            {
                _outgoing.Dispose();
                _outgoing = null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // Settings are best-effort; the current ones stay in force.
        }
    }

    public override void Dispose()
    {
        _outgoing?.Dispose();
        _outgoing = null;
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableTelemetry || !_options.EnableMetrics)
            return;

        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.MetricsIntervalSeconds));

        using var process = Process.GetCurrentProcess();
        _lastCpuTime = process.TotalProcessorTime;
        _lastSampleAt = DateTime.UtcNow;

        using var timer = new PeriodicTimer(interval);

        try
        {
            await RefreshAutoSignalsAsync(stoppingToken);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RefreshAutoSignalsAsync(stoppingToken);
                try
                {
                    Emit();
                }
                catch
                {
                    // Metrics are best-effort. A failed sample must never surface to the host.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch
        {
            // Never propagate.
        }
    }

    private void Emit()
    {
        var counters = ((KaironMetrics)_metrics).Drain();

        using var process = Process.GetCurrentProcess();
        process.Refresh();

        var now = DateTime.UtcNow;
        var cpuTime = process.TotalProcessorTime;
        var elapsed = (now - _lastSampleAt).TotalMilliseconds;

        // CPU as a share of one logical core's wall-clock time over the interval. Divided by
        // processor count so the number reads as "percent of the machine", matching what an
        // operator expects from a CPU threshold.
        double? cpuPercent = null;
        if (elapsed > 0)
        {
            var used = (cpuTime - _lastCpuTime).TotalMilliseconds;
            cpuPercent = Math.Clamp(used / (elapsed * Environment.ProcessorCount) * 100, 0, 100);
        }

        _lastCpuTime = cpuTime;
        _lastSampleAt = now;

        var workingSetBytes = process.WorkingSet64;
        var gcInfo = GC.GetGCMemoryInfo();

        // Total available memory is the honest denominator when the runtime reports it (containers
        // included); otherwise memory percent is left null rather than invented.
        double? memoryPercent = gcInfo.TotalAvailableMemoryBytes > 0
            ? Math.Clamp((double)workingSetBytes / gcInfo.TotalAvailableMemoryBytes * 100, 0, 100)
            : null;

        _queue.TryEnqueueMetric(new MetricPayload
        {
            Timestamp = now,
            CpuPercent = cpuPercent.HasValue ? Math.Round(cpuPercent.Value, 1) : null,
            MemoryPercent = memoryPercent.HasValue ? Math.Round(memoryPercent.Value, 1) : null,
            ResponseTimeMs = counters.Requests > 0 ? Math.Round(counters.AvgDurationMs, 1) : null,
            RequestCount = counters.Requests,
            ErrorCount = counters.Errors,
            // Zero rather than null once retries are known to be tracked, so a recovered service
            // reports "no retries" instead of reporting nothing at all.
            RetryCount = counters.Retries > 0
                ? counters.Retries
                : counters.RetriesTracked ? 0 : null,
            QueueDepth = counters.QueueDepth,
            Environment = KaironIdentity.ResolveEnvironment(_options),
            Application = KaironIdentity.ResolveApplication(_options),
            Service = KaironIdentity.ResolveService(_options),
            Component = KaironIdentity.ResolveService(_options)
        });
    }
}

/// <summary>
/// Resolves the identity fields the backend correlates on. Options win; then the historical
/// environment variables, so existing deployments keep the names they already set.
/// </summary>
internal static class KaironIdentity
{
    public static string ResolveApplication(KaironOptions options) =>
        !string.IsNullOrWhiteSpace(options.ApplicationName)
            ? options.ApplicationName!
            : Environment.GetEnvironmentVariable("KAIRON_APPLICATION_NAME")
              ?? Environment.GetEnvironmentVariable("Kairon_APPLICATION_NAME")
              ?? options.PairedService
              ?? System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name
              ?? "UnknownApplication";

    public static string ResolveService(KaironOptions options) =>
        !string.IsNullOrWhiteSpace(options.ServiceName)
            ? options.ServiceName!
            : ResolveApplication(options);

    public static string ResolveEnvironment(KaironOptions options) =>
        ResolveEnvironment(options, Environment.GetEnvironmentVariable);

    /// <summary>Same precedence as the Python SDK: explicit option, then KAIRON_ENVIRONMENT, then the
    /// environment the operator chose when generating the pairing code, and only then the host
    /// framework's ambient ASPNETCORE_ENVIRONMENT/DOTNET_ENVIRONMENT. The ambient value used to
    /// outrank the pairing choice, so an app paired for "Staging" but hosted with
    /// ASPNETCORE_ENVIRONMENT=Production reported Production and never matched its Staging target.</summary>
    internal static string ResolveEnvironment(KaironOptions options, Func<string, string?> environmentVariable) =>
        !string.IsNullOrWhiteSpace(options.Environment)
            ? options.Environment!
            : NonEmpty(environmentVariable("KAIRON_ENVIRONMENT"))
              ?? NonEmpty(options.PairedEnvironment)
              ?? NonEmpty(environmentVariable("ASPNETCORE_ENVIRONMENT"))
              ?? NonEmpty(environmentVariable("DOTNET_ENVIRONMENT"))
              ?? "Production";

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
