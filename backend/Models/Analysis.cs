namespace AIDIP.Backend.Models;

public class Analysis
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Type { get; set; } = string.Empty; // Error, Prediction, Recommendation, Fix
    public string Input { get; set; } = string.Empty;
    public string OutputJson { get; set; } = string.Empty;
    public double? Score { get; set; }
    public DateTime CreatedAt { get; set; }
}