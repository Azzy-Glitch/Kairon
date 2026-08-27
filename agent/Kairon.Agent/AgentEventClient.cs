using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Kairon.Agent;

/// <summary>
/// Posts a normalized event to the Kairon backend's POST /api/telemetry/events. Mirrors
/// sdk/Kairon.SDK/KaironTelemetryClient.cs's own contract exactly: never throws, bounded by its
/// own timeout independent of the caller's token, and a failed send is reported as a false
/// return, never an exception - a KAIRON backend outage must never take the Agent down with it.
/// </summary>
public class AgentEventClient
{
    private readonly HttpClient _http;
    private readonly AgentOptions _options;
    private readonly ILogger<AgentEventClient> _logger;

    public AgentEventClient(HttpClient http, IOptions<AgentOptions> options, ILogger<AgentEventClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> SendAsync(AgentEventPayload payload, CancellationToken cancellationToken = default)
    {
        payload.ProjectId = _options.ProjectId;
        payload.Environment = _options.Environment;
        payload.Application ??= _options.Application;
        payload.Service ??= _options.Service;

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/telemetry/events")
        {
            Content = JsonContent.Create(payload)
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            request.Headers.Add("X-Kairon-API-Key", _options.ApiKey);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));

            var response = await _http.SendAsync(request, timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            // Backend unreachable, timed out, or an unexpected transport fault - reported at
            // debug level only. Telemetry delivery failing must never look like the Agent itself
            // failing.
            _logger.LogDebug(ex, "kairon-agent: failed to send event {EventType}", payload.EventType);
            return false;
        }
    }
}

/// <summary>Wire shape matching backend/DTOs/AgentEventDto.cs field-for-field.</summary>
public class AgentEventPayload
{
    public Guid ProjectId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string EventType { get; set; } = string.Empty;
    public string Environment { get; set; } = "Production";
    public string? Application { get; set; }
    public string? Service { get; set; }
    public string? Component { get; set; }
    public string Severity { get; set; } = "Info";
    public string Message { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public int OccurrenceCount { get; set; } = 1;
    public string? MetadataJson { get; set; }
}
