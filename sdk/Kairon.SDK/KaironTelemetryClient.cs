using System.Net.Http.Json;
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
    /// assembly, so the internal accessibility here costs them nothing. <c>Kairon.SDK.Tests</c> is
    /// the one friend assembly (<c>InternalsVisibleTo</c> in the project file) allowed to construct
    /// this directly, and only ever does so with in-memory <c>HttpMessageHandler</c> test doubles
    /// that stub responses without a real handler chain to redirect through in the first place.
    /// </summary>
    internal KaironTelemetryClient(HttpClient http, IOptions<KaironOptions> options)
    {
        _http = http;
        _options = options.Value;
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
        // The actual, effective destination - not _options.Endpoint - because this HttpClient may
        // not have been the one AddKairon/KaironClient configured: this class's constructor is
        // public and a caller can supply any HttpClient directly, bypassing every endpoint check
        // upstream of here. This is the lowest public transport boundary before a real network
        // send, so it is validated on every call rather than trusted because "something else
        // already checked this" - including the case where BaseAddress and _options.Endpoint
        // disagree (whichever HttpClient actually resolves is what must be safe).
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
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(payload, payload.GetType())
            };

            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                request.Headers.Add("X-Kairon-API-Key", _options.ApiKey);

            // Bound every send independently of the ambient token, so a caller that passes
            // CancellationToken.None still cannot be held indefinitely by a hung collector.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));

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
