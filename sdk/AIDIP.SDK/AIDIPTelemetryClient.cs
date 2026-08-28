using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using AIDIP.SDK.Models;

namespace AIDIP.SDK;

/// <summary>
/// Sends telemetry to the AIDIP backend.
///
/// The contract this class exists to keep (PRD section 17): it never throws, never blocks the
/// caller for longer than the configured timeout, and never turns an AIDIP outage into an
/// application outage. Every failure path returns a result object rather than propagating.
/// </summary>
public class AIDIPTelemetryClient
{
    private readonly HttpClient _http;
    private readonly AIDIPOptions _options;

    public AIDIPTelemetryClient(HttpClient http, IOptions<AIDIPOptions> options)
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

        return PostAsync("api/telemetry/incidents", payload, cancellationToken);
    }

    public Task<TelemetryResponse?> SendMetricAsync(
        MetricPayload payload,
        CancellationToken cancellationToken = default)
    {
        if (!_options.EnableTelemetry)
            return Task.FromResult<TelemetryResponse?>(null);

        payload.ProjectId = _options.ProjectId;

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
            request.Headers.Add(_options.ApiKeyHeader, _options.ApiKey);

        try
        {
            // Bound every send independently of the ambient token, so a caller that passes
            // CancellationToken.None still cannot be held indefinitely by a hung collector.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));

            var response = await _http.SendAsync(request, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                return new TelemetryResponse
                {
                    Success = false,
                    Message = $"AIDIP server returned {(int)response.StatusCode}."
                };
            }

            // A 200 with an unparseable body is still a delivered telemetry item; the response
            // body is informational, so a deserialization problem is reported, never thrown.
            try
            {
                return await response.Content.ReadFromJsonAsync<TelemetryResponse>(cancellationToken: timeout.Token);
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
                Message = "Unable to send telemetry to AIDIP."
            };
        }
    }
}
