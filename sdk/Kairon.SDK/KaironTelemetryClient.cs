using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Kairon.SDK.Models;

namespace Kairon.SDK;

/// <summary>
/// Sends telemetry to the Kairon backend.
///
/// The contract this class exists to keep (PRD section 17): it never throws, never blocks the
/// caller for longer than the configured timeout, and never turns an Kairon outage into an
/// application outage. Every failure path returns a result object rather than propagating.
/// </summary>
public class KaironTelemetryClient
{
    private readonly HttpClient _http;
    private readonly KaironOptions _options;
    private readonly int? _agentProofPort;

    /// <summary>
    /// Internal by design, not merely by convention: an already-constructed <see cref="HttpClient"/>
    /// exposes no public way to inspect or change the handler chain baked into it, so once one
    /// reaches here this class has no way to verify - let alone enforce - that it will not silently
    /// follow a redirect and replay <c>X-Kairon-API-Key</c> at whatever a malicious or compromised
    /// backend's 3xx response names. A publicly constructible overload would therefore be an
    /// unrestricted escape hatch around this SDK's entire redirect-safety guarantee: any external
    /// caller could hand in a plain <c>new HttpClient()</c> (auto-redirect on by default) and this
    /// class would have no way to know.
    ///
    /// The only safe way to build one is for the SDK itself to build the <see cref="HttpClient"/>
    /// first - see <see cref="KaironExtensions.AddKairon"/> (a named client via
    /// <c>IHttpClientFactory</c>, its primary handler forced through
    /// <see cref="KaironEndpointSecurity.CreateNonRedirectingHandler"/>) and
    /// <see cref="KaironClient"/>'s own private constructor (same pattern, no DI). Both live in this
    /// assembly, so the internal accessibility here costs them nothing. The SDK and backend test
    /// assemblies are friends (<c>InternalsVisibleTo</c> in the project file) so tests can
    /// exercise the transport and exact normalized wire shape without adding a public constructor.
    /// </summary>
    internal KaironTelemetryClient(HttpClient http, IOptions<KaironOptions> options, int? agentProofPort = null)
    {
        _http = http;
        _options = options.Value;
        _agentProofPort = agentProofPort;
    }

    /// <summary>The same options instance the sender uses to normalize queued items.</summary>
    internal KaironOptions Options => _options;

    /// <summary>
    /// Normal host delivery uses the backend's idempotent normalized contract. The existing
    /// public SendAsync/SendMetricAsync methods remain legacy-compatible for direct callers;
    /// both routes use the same Agent proof and server-side machine resolution.
    /// </summary>
    internal Task<TelemetryResponse?> SendNormalizedAsync(TelemetryPayload payload,
        CancellationToken cancellationToken = default) =>
        SendOneNormalizedAsync(NormalizedTelemetryEvent.From(payload, _options), cancellationToken);

    internal Task<TelemetryResponse?> SendNormalizedAsync(MetricPayload payload,
        CancellationToken cancellationToken = default) =>
        SendOneNormalizedAsync(NormalizedTelemetryEvent.From(payload, _options), cancellationToken);

    private async Task<TelemetryResponse?> SendOneNormalizedAsync(NormalizedTelemetryEvent item,
        CancellationToken cancellationToken)
    {
        if (!_options.EnableTelemetry) return null;
        var result = await SendNormalizedBatchAsync(new[] { item }, cancellationToken).ConfigureAwait(false);
        return new TelemetryResponse { Success = result.Delivered == 1, Message = result.Message };
    }

    /// <summary>The backend's own per-batch bound (RequestSizeLimit on api/v1/telemetry/events).</summary>
    internal const int MaxBatchBytes = 1_048_576;

    /// <summary>
    /// One POST per batch instead of one per event: the backend's telemetry rate limit is per
    /// request (600/minute/IP), so per-event sends throttled a busy host long before the collector
    /// was actually loaded. Never throws. A batch over the size bound is split in half until each
    /// part fits; a single event that alone exceeds it is reported as failed. A 429 is returned
    /// to the caller with the server's Retry-After instead of being retried in the same window.
    /// </summary>
    internal async Task<NormalizedBatchResult> SendNormalizedBatchAsync(
        IReadOnlyList<NormalizedTelemetryEvent> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0) return new NormalizedBatchResult(0, 0, "Delivered.");
        if (!_options.EnableTelemetry) return new NormalizedBatchResult(0, events.Count, null);
        if (!KaironEndpointSecurity.IsAllowed(_http.BaseAddress))
            return new NormalizedBatchResult(0, events.Count, "Kairon endpoint rejected: insecure transport.");

        byte[] body;
        try
        {
            body = JsonSerializer.SerializeToUtf8Bytes(new { Events = events },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch
        {
            return new NormalizedBatchResult(0, events.Count, "Telemetry serialization failed.");
        }
        if (body.Length > MaxBatchBytes)
        {
            if (events.Count == 1)
                return new NormalizedBatchResult(0, 1, "Telemetry batch exceeds backend size limit.");
            var half = events.Count / 2;
            var first = await SendNormalizedBatchAsync(events.Take(half).ToList(), cancellationToken).ConfigureAwait(false);
            if (first.RateLimited) return first with { Failed = first.Failed + events.Count - half };
            var second = await SendNormalizedBatchAsync(events.Skip(half).ToList(), cancellationToken).ConfigureAwait(false);
            return new NormalizedBatchResult(first.Delivered + second.Delivered, first.Failed + second.Failed,
                second.Failed > 0 ? second.Message : first.Message, second.RetryAfter);
        }

        // The backend binds a proof to one project/service/environment and answers a proof-bearing
        // mixed batch with 400, so only a homogeneous batch ever asks the Agent for one.
        var useProof = NormalizedBatchScope.IsHomogeneous(events);

        // The same EventIds and exact bytes are reused across transient retries. The backend
        // treats a response lost after commit as a duplicate, never a second incident.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));
        // Same default as the Python SDK: up to three attempts for a transient failure.
        var retriesLeft = Math.Clamp(_options.DeliveryAttempts, 1, 5) - 1;
        while (true)
        {
            var proofAttached = false;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/telemetry/events")
                {
                    Content = new ByteArrayContent(body)
                };
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                    request.Headers.TryAddWithoutValidation("X-Kairon-API-Key", _options.ApiKey);
                if (useProof)
                {
                    var head = events[0];
                    var proof = await AgentMachineProof.TryAcquireAsync(_http, _options,
                        string.IsNullOrWhiteSpace(head.Service) ? head.Application : head.Service,
                        head.Environment, body, timeout.Token, _agentProofPort);
                    if (proof.HasValue)
                    {
                        request.Headers.TryAddWithoutValidation("X-Kairon-Machine-Proof", proof.Value.ToString());
                        proofAttached = true;
                    }
                }

                using var response = await _http.SendAsync(request, timeout.Token);
                var status = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode)
                {
                    // A rejected proof (expired, consumed by a lost earlier attempt, Agent not
                    // enrolled for this backend) is not an authentication failure: the API key may
                    // be perfectly valid, and unscoped telemetry is still telemetry. Resend the
                    // same bytes once without the proof; only a 401 WITHOUT a proof means the
                    // project credential itself was rejected.
                    if (proofAttached && status is 400 or 401)
                    {
                        useProof = false;
                        continue;
                    }
                    if (status == 429)
                        return new NormalizedBatchResult(0, events.Count,
                            "Kairon server returned 429. (rate limited; backing off)", RetryAfterOf(response));
                    if (retriesLeft > 0 && status is 500 or 502 or 503 or 504)
                    {
                        retriesLeft--;
                        continue;
                    }
                    var suffix = status is 401 or 403 ? " (project authentication rejected)" : "";
                    return new NormalizedBatchResult(0, events.Count, $"Kairon server returned {status}.{suffix}");
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
                var result = document.RootElement;
                if (result.ValueKind == JsonValueKind.Object &&
                    result.TryGetProperty("accepted", out var accepted) && accepted.TryGetInt32(out var acceptedCount) &&
                    result.TryGetProperty("duplicates", out var duplicates) && duplicates.TryGetInt32(out var duplicateCount) &&
                    result.TryGetProperty("rejected", out var rejected) && rejected.TryGetInt32(out var rejectedCount))
                {
                    // The backend reports counts, not which events; never claim more than were sent.
                    var delivered = Math.Clamp(acceptedCount + duplicateCount, 0, events.Count);
                    var success = delivered == events.Count && rejectedCount == 0;
                    if (success) return new NormalizedBatchResult(delivered, 0, "Delivered.");
                    delivered = Math.Min(delivered, Math.Max(0, events.Count - rejectedCount));
                    return new NormalizedBatchResult(delivered, events.Count - delivered, "Collector rejected telemetry.");
                }
                if (retriesLeft <= 0)
                    return new NormalizedBatchResult(0, events.Count, "Collector returned an invalid response.");
                retriesLeft--;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new NormalizedBatchResult(0, events.Count, "Telemetry send cancelled.");
            }
            catch (OperationCanceledException)
            {
                return new NormalizedBatchResult(0, events.Count, "Telemetry send timed out.");
            }
            catch (Exception) when (retriesLeft > 0)
            {
                // A lost response may follow a successful commit. Retry only the same EventIds.
                retriesLeft--;
            }
            catch
            {
                return new NormalizedBatchResult(0, events.Count, "Unable to send telemetry to Kairon.");
            }
        }
    }

    /// <summary>Upper bound on any server-requested pause, so a hostile or misconfigured
    /// Retry-After can never park the sender indefinitely.</summary>
    internal static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(5);

    private static TimeSpan RetryAfterOf(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        TimeSpan? requested = header?.Delta
            ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        var delay = requested ?? DefaultRetryAfter;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        return delay > MaxRetryAfter ? MaxRetryAfter : delay;
    }

    /// <summary>
    /// Fetches this app's automatic-signal settings (set in the KAIRON desktop) and applies them.
    /// Never throws; returns false when KAIRON could not answer, leaving the current settings.
    /// </summary>
    public async Task<bool> RefreshAutoSignalsAsync(KaironAutoSignals signals, CancellationToken cancellationToken = default)
    {
        if (_options.ProjectId == Guid.Empty || string.IsNullOrWhiteSpace(_options.ApiKey)) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/sdk/settings")
            {
                Content = JsonContent.Create(new { projectId = _options.ProjectId })
            };
            request.Headers.TryAddWithoutValidation("X-Kairon-API-Key", _options.ApiKey);
            using var response = await _http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) return false;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
            var root = document.RootElement;
            if (root.TryGetProperty("autoQueueDepth", out var queue) && queue.ValueKind is JsonValueKind.True or JsonValueKind.False)
                signals.AutoQueueDepth = queue.GetBoolean();
            if (root.TryGetProperty("autoRetries", out var retries) && retries.ValueKind is JsonValueKind.True or JsonValueKind.False)
                signals.AutoRetries = retries.GetBoolean();
            if (root.TryGetProperty("retryWindowSeconds", out var window) && window.TryGetInt32(out var seconds))
                signals.RetryWindow = TimeSpan.FromSeconds(seconds);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public Task<TelemetryResponse?> SendAsync(
        TelemetryPayload payload,
        CancellationToken cancellationToken = default)
    {
        if (!_options.EnableTelemetry)
            return Task.FromResult<TelemetryResponse?>(null);

        // Always send ProjectId
        payload.ProjectId = _options.ProjectId;
        payload.MachineId = _options.MachineId;

        return PostAsync("api/telemetry/incidents", payload, cancellationToken);
    }

    public Task<TelemetryResponse?> SendMetricAsync(
        MetricPayload payload,
        CancellationToken cancellationToken = default)
    {
        if (!_options.EnableTelemetry)
            return Task.FromResult<TelemetryResponse?>(null);

        payload.ProjectId = _options.ProjectId;
        payload.MachineId = _options.MachineId;

        return PostAsync("api/telemetry/metrics", payload, cancellationToken);
    }

    private async Task<TelemetryResponse?> PostAsync(
        string path,
        object payload,
        CancellationToken cancellationToken)
    {
        // Validate the actual effective destination at the lowest transport boundary. Both
        // production constructors supply a non-redirecting handler; callers cannot supply one.
        if (!KaironEndpointSecurity.IsAllowed(_http.BaseAddress))
        {
            return new TelemetryResponse
            {
                Success = false,
                Message = "Kairon endpoint rejected: plain HTTP is only allowed to localhost/127.0.0.0/8/::1. Use HTTPS for any non-local KAIRON backend."
            };
        }

        try
        {
            var requestBody = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            // Bound every send independently of the ambient token, so a caller that passes
            // CancellationToken.None still cannot be held indefinitely by a hung collector.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));

            var service = payload switch
            {
                TelemetryPayload incident => incident.Service ?? incident.ApplicationName ?? "",
                MetricPayload metric => metric.Service ?? metric.Application ?? "",
                _ => ""
            };
            var environment = payload switch
            {
                TelemetryPayload incident => incident.Environment,
                MetricPayload metric => metric.Environment,
                _ => ""
            };
            var useProof = !string.IsNullOrWhiteSpace(service) && !string.IsNullOrWhiteSpace(environment);
            HttpResponseMessage response;
            while (true)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, path)
                {
                    Content = new ByteArrayContent(requestBody)
                };
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

                if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                    request.Headers.Add("X-Kairon-API-Key", _options.ApiKey);

                var proofAttached = false;
                if (useProof)
                {
                    var proof = await AgentMachineProof.TryAcquireAsync(_http, _options,
                        service, environment, requestBody, timeout.Token, _agentProofPort);
                    if (proof.HasValue)
                    {
                        request.Headers.TryAddWithoutValidation("X-Kairon-Machine-Proof", proof.Value.ToString());
                        proofAttached = true;
                    }
                }

                response = await _http.SendAsync(request, timeout.Token);
                // Same rule as the normalized path: a rejected proof is retried once unscoped, and
                // only a 401 without a proof is reported as an authentication failure.
                if (proofAttached && (int)response.StatusCode is 400 or 401)
                {
                    response.Dispose();
                    useProof = false;
                    continue;
                }
                break;
            }
            using var responseScope = response;

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                // 401/403 is never treated as "delete the stored credential and re-pair" - it is
                // only ever reported, exactly like every other delivery failure. The clarifying
                // suffix mirrors the Python SDK's diagnostic wording (last_delivery_error) so an
                // operator sees the same message regardless of which SDK reported it.
                var suffix = status is 401 or 403 ? " (project authentication rejected)" : "";
                return new TelemetryResponse
                {
                    Success = false,
                    Message = $"Kairon server returned {status}.{suffix}"
                };
            }

            // A 200 with an unparseable body is still a delivered telemetry item; the response
            // body is informational, so a deserialization problem is reported, never thrown.
            try
            {
                // Legacy metric ingestion responds with {status:"recorded"}, not Success.
                var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: timeout.Token);
                var rejected = body.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    body.TryGetProperty("success", out var success) && success.ValueKind == System.Text.Json.JsonValueKind.False;
                return new TelemetryResponse {
                    Success = !rejected, Message = rejected ? "Collector rejected telemetry." : "Delivered.",
                    TelemetryId = body.ValueKind == System.Text.Json.JsonValueKind.Object &&
                        body.TryGetProperty("telemetryId", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String ? id.GetString() : null
                };
            }
            catch
            {
                return new TelemetryResponse { Success = true, Message = "Delivered." };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown or an explicit caller cancellation. Reported, not thrown: the host is
            // going away and must not receive an exception from its telemetry SDK on the way out.
            return new TelemetryResponse
            {
                Success = false,
                Message = "Telemetry send cancelled."
            };
        }
        catch (OperationCanceledException)
        {
            return new TelemetryResponse
            {
                Success = false,
                Message = $"Telemetry send timed out after {_options.TimeoutSeconds}s."
            };
        }
        catch
        {
            return new TelemetryResponse
            {
                Success = false,
                Message = "Unable to send telemetry to Kairon."
            };
        }
    }
}
