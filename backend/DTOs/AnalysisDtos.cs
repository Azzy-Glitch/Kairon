namespace Kairon.Backend.DTOs;

public class AnalysisHistoryItem
{
    public Guid Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Input { get; set; } = string.Empty;
    public string OutputJson { get; set; } = string.Empty;
    public double? Score { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AnalysisStatsResponse
{
    public int Total { get; set; }
    public double AvgScore { get; set; }
    public int ErrorCount { get; set; }
    public int ApiCount { get; set; }
    public int PredictCount { get; set; }
    public int RecCount { get; set; }
    public DateTime Timestamp { get; set; }
}
