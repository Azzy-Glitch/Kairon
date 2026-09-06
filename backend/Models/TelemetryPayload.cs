namespace Kairon.Backend.Models;

public class TelemetryPayload
{
    public Guid ProjectId { get; set; }
    public Guid? MachineId { get; set; }
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

    /// <summary>
    /// Logical service name for correlation. Optional: older SDK builds do not send it, and the
    /// backend falls back to <see cref="ApplicationName"/> when it is absent.
    /// </summary>
    public string? Service { get; set; }

    /// <summary>Optional captured bodies. Only populated when the SDK is explicitly configured to.</summary>
    public string? RequestBody { get; set; }
    public string? ResponseBody { get; set; }
}
