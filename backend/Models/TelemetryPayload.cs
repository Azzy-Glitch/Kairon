namespace AIDIP.Backend.Models;

public class TelemetryPayload
{
    public Guid ProjectId { get; set; }
    public string ApplicationName { get; set; } = string.Empty;
    public string Environment { get; set; } = "Development";
    public string Endpoint { get; set; } = string.Empty;
    public string Method { get; set; } = "GET";
    public int StatusCode { get; set; }
    public long Duration { get; set; }
    public string? Error { get; set; }
    public string? ExceptionType { get; set; }
    public string? StackTrace { get; set; }
    public DateTime Timestamp { get; set; }
}
