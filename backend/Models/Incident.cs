namespace AIDIP.Backend.Models;

public class Incident
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public DateTime Timestamp { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string Method { get; set; } = "GET";
    public int StatusCode { get; set; }
    public long DurationMs { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorMessage { get; set; }
    public string? StackTrace { get; set; }
    public string? RequestId { get; set; }
    public string Environment { get; set; } = "Development";
    public string Severity { get; set; } = "Error";
    public bool Resolved { get; set; }
    public string? MetadataJson { get; set; }

    //public static implicit operator Incident(Incident v)
    //{
    //    throw new NotImplementedException();
    //}
}