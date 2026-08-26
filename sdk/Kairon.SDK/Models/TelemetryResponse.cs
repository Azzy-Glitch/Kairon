namespace Kairon.SDK.Models;

public class TelemetryResponse
{
    public bool Success { get; set; }

    public string? Message { get; set; }

    public string? TelemetryId { get; set; }
}