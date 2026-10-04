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

    /// <summary>
    /// Normal host delivery uses the backend's idempotent normalized contract. The existing
    /// public SendAsync/SendMetricAsync methods remain legacy-compatible for direct callers;
    /// both routes use the same Agent proof and server-side machine resolution.
    /// </summary>
    internal Task<TelemetryResponse?> SendNormalizedAsync(TelemetryPayload payload,
        CancellationToken cancellationToken = default) =>
        PostNormalizedAsync(NormalizedTelemetryEvent.From(payload, _options), cancellationToken);

    internal Task<TelemetryResponse?> SendNormalizedAsync(MetricPayload payload,
        CancellationToken cancellationToken = default) =>
        PostNormalizedAsync(NormalizedTelemetryEvent.From(payload, _options), cancellationToken);

    private async Task<TelemetryResponse?> PostNormalizedAsync(NormalizedTelemetryEvent item,
        CancellationToken cancellationToken)
    {
        if (!_options.EnableTelemetry) return null;
        if (!KaironEndpointSecurity.IsAllowed(_http.BaseAddress))
            return new TelemetryResponse { Success = false, Message = "Kairon endpoint rejected: insecure transport." };

        byte[] body;
        try
        {
            body = JsonSerializer.SerializeToUtf8Bytes(new { Events = new[] { item } },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch
        {
            return new TelemetryResponse { Success = false, Message = "Telemetry serialization failed." };
        }
        if (body.Length > 1_048_576)
            return new TelemetryResponse { Success = false, Message = "Telemetry batch exceeds backend size limit." };

        // The same EventId and exact bytes are reused across transient retries. The backend
        // treats a response lost after commit as a duplicate, never a second incident.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/telemetry/events")
                {
                    Content = new ByteArrayContent(body)
                };
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                    request.Headers.TryAddWithoutValidation("X-Kairon-API-Key", _options.ApiKey);
                var proof = await AgentMachineProof.TryAcquireAsync(_http, _options,
                    item.Service, item.Environment, body, timeout.Token, _agentProofPort);
                if (proof.HasValue)
                    request.Headers.TryAddWithoutValidation("X-Kairon-Machine-Proof", proof.Value.ToString());

                using var response = await _http.SendAsync(request, timeout.Token);
                var status = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode)
                {
                    if (attempt == 0 && status is 429 or 500 or 502 or 503 or 504)
                        continue;
                    var suffix = status is 401 or 403 ? " (project authentication rejected)" : "";
                    return new TelemetryResponse { Success = false, Message = $"Kairon server returned {status}.{suffix}" };
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
                var result = document.RootElement;
                if (result.ValueKind == JsonValueKind.Object &&
                    result.TryGetProperty("accepted", out var accepted) && accepted.TryGetInt32(out var acceptedCount) &&
                    result.TryGetProperty("duplicates", out var duplicates) && duplicates.TryGetInt32(out var duplicateCount) &&
                    result.TryGetProperty("rejected", out var rejected) && rejected.TryGetInt32(out var rejectedCount))
                {
                    var success = acceptedCount + duplicateCount == 1 && rejectedCount == 0;
                    return new TelemetryResponse { Success = success,
                        Message = success ? "Delivered." : "Collector rejected telemetry." };
                }
                if (attempt == 1)
                    return new TelemetryResponse { Success = false, Message = "Collector returned an invalid response." };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new TelemetryResponse { Success = false, Message = "Telemetry send cancelled." };
            }
            catch (OperationCanceledException)
            {
                return new TelemetryResponse { Success = false, Message = "Telemetry send timed out." };
            }
            catch (Exception) when (attempt == 0)
            {
                // A lost response may follow a successful commit. Retry only the same EventId.
            }
            catch
            {
                return new TelemetryResponse { Success = false, Message = "Unable to send telemetry to Kairon." };
            }
        }
        return new TelemetryResponse { Success = false, Message = "Unable to send telemetry to Kairon." };
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
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new ByteArrayContent(requestBody)
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                request.Headers.Add("X-Kairon-API-Key", _options.ApiKey);

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
            if (!string.IsNullOrWhiteSpace(service) && !string.IsNullOrWhiteSpace(environment))
            {
                var proof = await AgentMachineProof.TryAcquireAsync(_http, _options,
                    service, environment, requestBody, timeout.Token, _agentProofPort);
                if (proof.HasValue)
                    request.Headers.TryAddWithoutValidation("X-Kairon-Machine-Proof", proof.Value.ToString());
            }

            using var response = await _http.SendAsync(request, timeout.Token);

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
