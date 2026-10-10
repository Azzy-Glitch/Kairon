using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;

namespace Kairon.SDK;

/// <summary>
/// Which automatic signals this app collects - set per app in the KAIRON desktop and fetched by the
/// SDK itself (<see cref="KaironTelemetryClient.RefreshAutoSignalsAsync"/>), so the application needs
/// no code or configuration. Both signals default on; a KAIRON that cannot be reached leaves them so.
/// Matches the Python SDK.
/// </summary>
public sealed class KaironAutoSignals
{
    private volatile bool _autoQueueDepth = true;
    private volatile bool _autoRetries = true;
    private long _retryWindowTicks = TimeSpan.FromSeconds(10).Ticks;

    /// <summary>Report the peak number of requests in progress per sample as queue depth.</summary>
    public bool AutoQueueDepth { get => _autoQueueDepth; set => _autoQueueDepth = value; }

    /// <summary>Count a repeat of a failed outgoing HTTP call as a retry.</summary>
    public bool AutoRetries { get => _autoRetries; set => _autoRetries = value; }

    /// <summary>How soon after a failure a repeat of the same call counts as a retry (1-300 s).</summary>
    public TimeSpan RetryWindow
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _retryWindowTicks));
        set
        {
            if (value >= TimeSpan.FromSeconds(1) && value <= TimeSpan.FromSeconds(300))
                Interlocked.Exchange(ref _retryWindowTicks, value.Ticks);
        }
    }
}

/// <summary>
/// Counts retries of the application's outgoing HTTP calls with no application code: a repeat of the
/// same call (method, scheme, host, port and path) within the retry window after that call failed (an
/// exception, HTTP 429 or a 5xx) is a retry - whatever library, Polly policy or hand-written loop made
/// it. Observed through HttpClient's built-in diagnostics; calls to the KAIRON endpoint itself (the
/// SDK's own delivery) are never counted, and observation never affects the application's call.
/// </summary>
internal sealed class KaironOutgoingCallObserver :
    IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private const string ListenerName = "HttpHandlerDiagnosticListener";
    private const string StopEvent = "System.Net.Http.HttpRequestOut.Stop";
    private const int MaxTrackedCalls = 256;

    private static readonly ConcurrentDictionary<Type, (PropertyInfo? Request, PropertyInfo? Response, PropertyInfo? Status)> Payloads = new();

    private readonly KaironMetrics _metrics;
    private readonly KaironAutoSignals _signals;
    private readonly string? _excludedAuthority;
    private readonly ConcurrentDictionary<string, long> _failures = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _subscriptions = new();
    private IDisposable? _allListeners;

    public KaironOutgoingCallObserver(KaironMetrics metrics, KaironAutoSignals signals, string? kaironEndpoint)
    {
        _metrics = metrics;
        _signals = signals;
        _excludedAuthority = Uri.TryCreate(kaironEndpoint, UriKind.Absolute, out var endpoint)
            ? endpoint.Authority
            : null;
    }

    public void Start()
    {
        _allListeners ??= DiagnosticListener.AllListeners.Subscribe(this);
        _metrics.OutgoingCallsObserved = true;
    }

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
    {
        if (listener.Name != ListenerName) return;
        lock (_subscriptions)
            _subscriptions.Add(listener.Subscribe(this, name => name.StartsWith("System.Net.Http.HttpRequestOut", StringComparison.Ordinal)));
    }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Key != StopEvent || value.Value is null || !_signals.AutoRetries) return;
        try
        {
            var properties = Payloads.GetOrAdd(value.Value.GetType(), type => (
                type.GetProperty("Request"), type.GetProperty("Response"), type.GetProperty("RequestTaskStatus")));
            if (properties.Request?.GetValue(value.Value) is not HttpRequestMessage request) return;
            var response = properties.Response?.GetValue(value.Value) as HttpResponseMessage;
            var status = properties.Status?.GetValue(value.Value) is TaskStatus taskStatus ? taskStatus : TaskStatus.RanToCompletion;
            Observe(request.RequestUri, request.Method.Method, response is null ? null : (int)response.StatusCode,
                faulted: status != TaskStatus.RanToCompletion || response is null);
        }
        catch
        {
            // Observation must never affect the application's call.
        }
    }

    /// <summary>Records one finished outgoing call and counts it as a retry when it repeats a recent failure.</summary>
    internal void Observe(Uri? uri, string method, int? statusCode, bool faulted)
    {
        if (uri is null || !uri.IsAbsoluteUri) return;
        if (_excludedAuthority is not null && string.Equals(uri.Authority, _excludedAuthority, StringComparison.OrdinalIgnoreCase)) return;

        var key = $"{method.ToUpperInvariant()} {uri.Scheme}://{uri.Authority.ToLowerInvariant()}{uri.AbsolutePath}";
        var now = Stopwatch.GetTimestamp();
        var window = (long)(_signals.RetryWindow.TotalSeconds * Stopwatch.Frequency);

        if (_failures.TryGetValue(key, out var lastFailure) && now - lastFailure <= window)
            _metrics.RecordObservedRetry();

        var failed = faulted || statusCode is 429 or >= 500;
        if (!failed)
        {
            _failures.TryRemove(key, out _);
            return;
        }

        _failures[key] = now;
        if (_failures.Count > MaxTrackedCalls)
        {
            foreach (var stale in _failures.Where(f => now - f.Value > window).Select(f => f.Key).ToList())
                _failures.TryRemove(stale, out _);
        }
    }

    void IObserver<DiagnosticListener>.OnCompleted() { }
    void IObserver<DiagnosticListener>.OnError(Exception error) { }
    void IObserver<KeyValuePair<string, object?>>.OnCompleted() { }
    void IObserver<KeyValuePair<string, object?>>.OnError(Exception error) { }

    public void Dispose()
    {
        _allListeners?.Dispose();
        lock (_subscriptions)
        {
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
        }
        _metrics.OutgoingCallsObserved = false;
    }
}
