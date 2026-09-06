namespace Kairon.SDK.Models;

public class TelemetryPayload
{
    public Guid ProjectId { get; set; }
    public Guid? MachineId { get; set; }

    public string ApplicationName { get; set; } = string.Empty;
    public string Environment { get; set; } = "Production";

    /// <summary>Logical service name used by the backend for correlation.</summary>
    public string? Service { get; set; }

    public string Endpoint { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public long Duration { get; set; }

    public string? Error { get; set; }
    public string? ExceptionType { get; set; }
    public string? StackTrace { get; set; }

    public string? RequestBody { get; set; }
    public string? ResponseBody { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
