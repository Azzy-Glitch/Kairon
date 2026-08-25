using System.Diagnostics;
using AIDIP.SDK.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AIDIP.SDK;

/// <summary>
/// Counters the middleware updates and the metrics collector drains. Also the seam a host
/// application uses to report the signals only it can know - retries and queue depth - which are
/// exactly the signals the retry-storm and backlog detection rules need.
/// </summary>
public interface IAIDIPMetrics
{
    void RecordRequest(long durationMs, bool isError);

    /// <summary>Reports retries the application performed since the last call.</summary>
    void RecordRetries(long count);

    /// <summary>Reports the current depth of the application's pending work queue.</summary>
    void ReportQueueDepth(long depth);
}

public class AIDIPMetrics : IAIDIPMetrics
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

        // Queue depth is a gauge, not a counter: it is read, not reset, because the application's
        // current backlog is still whatever it was after the interval ends.
        var queue = Interlocked.Read(ref _queueDepth);

        return new MetricsSnapshot(
            requests,
            errors,
            requests > 0 ? (double)duration / requests : 0,
            retries,
            queue >= 0 ? queue : null,
            RetriesTracked: Interlocked.Read(ref _retriesEverReported) == 1);
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
public class AIDIPMetricsCollector : BackgroundService
{
    private readonly IAIDIPTelemetryQueue _queue;
    private readonly IAIDIPMetrics _metrics;
    private readonly AIDIPOptions _options;

    private TimeSpan _lastCpuTime;
    private DateTime _lastSampleAt;

    public AIDIPMetricsCollector(
        IAIDIPTelemetryQueue queue,
        IAIDIPMetrics metrics,
        IOptions<AIDIPOptions> options)
    {
        _queue = queue;
        _metrics = metrics;
        _options = options.Value;
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
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
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
        var counters = ((AIDIPMetrics)_metrics).Drain();

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
            Environment = AIDIPIdentity.ResolveEnvironment(_options),
            Application = AIDIPIdentity.ResolveApplication(_options),
            Service = AIDIPIdentity.ResolveService(_options),
            Component = AIDIPIdentity.ResolveService(_options)
        });
    }
}

/// <summary>
/// Resolves the identity fields the backend correlates on. Options win; then the historical
/// environment variables, so existing deployments keep the names they already set.
/// </summary>
internal static class AIDIPIdentity
{
    public static string ResolveApplication(AIDIPOptions options) =>
        !string.IsNullOrWhiteSpace(options.ApplicationName)
            ? options.ApplicationName!
            : Environment.GetEnvironmentVariable("AIDIP_APPLICATION_NAME")
              ?? System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name
              ?? "UnknownApplication";

    public static string ResolveService(AIDIPOptions options) =>
        !string.IsNullOrWhiteSpace(options.ServiceName)
            ? options.ServiceName!
            : ResolveApplication(options);

    public static string ResolveEnvironment(AIDIPOptions options) =>
        !string.IsNullOrWhiteSpace(options.Environment)
            ? options.Environment!
            : Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
              ?? "Production";
}
