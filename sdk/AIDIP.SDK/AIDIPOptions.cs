namespace AIDIP.SDK;

public class AIDIPOptions
{
    public string Endpoint { get; set; } = "http://localhost:8000";
    public string? ApiKey { get; set; }
    public Guid ProjectId { get; set; }        
    public bool EnableTelemetry { get; set; } = true;
    public bool CaptureRequestBody { get; set; } = false;
    public bool CaptureResponseBody { get; set; } = false;
}