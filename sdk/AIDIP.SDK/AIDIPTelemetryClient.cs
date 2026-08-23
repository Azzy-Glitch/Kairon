using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using AIDIP.SDK.Models;

namespace AIDIP.SDK;

public class AIDIPTelemetryClient
{
    private readonly HttpClient _http;
    private readonly AIDIPOptions _options;

    public AIDIPTelemetryClient(HttpClient http, IOptions<AIDIPOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public async Task<TelemetryResponse?> SendAsync(
        TelemetryPayload payload,
        CancellationToken cancellationToken = default)
    {
        if (!_options.EnableTelemetry)
            return null;

        // Always send ProjectId
        payload.ProjectId = _options.ProjectId;

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/telemetry/incidents");
        request.Content = JsonContent.Create(payload);

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            request.Headers.Add("X-AIDIP-API-Key", _options.ApiKey);

        try
        {
            var response = await _http.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new TelemetryResponse
                {
                    Success = false,
                    Message = $"AIDIP server returned {(int)response.StatusCode}."
                };
            }

            return await response.Content.ReadFromJsonAsync<TelemetryResponse>(cancellationToken: cancellationToken);
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