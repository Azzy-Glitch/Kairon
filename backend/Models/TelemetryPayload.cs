namespace AIDIP.Backend.Models;

public class TelemetryPayload
{
    public Guid ProjectId { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string Method { get; set; } = "GET";
    public int StatusCode { get; set; }
    public long Duration { get; set; }
    public string? Error { get; set; }
    public DateTime Timestamp { get; set; }
}