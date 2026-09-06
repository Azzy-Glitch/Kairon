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

    public KaironTelemetryClient(HttpClient http, IOptions<KaironOptions> options)
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
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload, payload.GetType())
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            request.Headers.Add("X-Kairon-API-Key", _options.ApiKey);

        try
        {
            // Bound every send independently of the ambient token, so a caller that passes
            // CancellationToken.None still cannot be held indefinitely by a hung collector.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));

            using var response = await _http.SendAsync(request, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                return new TelemetryResponse
                {
                    Success = false,
                    Message = $"Kairon server returned {(int)response.StatusCode}."
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
