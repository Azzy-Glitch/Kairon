using System.Text.Json.Serialization;

namespace Kairon.Backend.DTOs;

public class ApiResponse<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
    public int StatusCode { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

// Request DTOs — shaped to match what the frontend already sends and what
// ai-service/main.py already accepts.
public class ErrorAnalysisRequest
{
    public string Log { get; set; } = string.Empty;
}

public class ApiValidationRequest
{
    public Dictionary<string, string> Expected { get; set; } = new();
    public Dictionary<string, object> Actual { get; set; } = new();
}

public class PredictionRequest
{
    [JsonPropertyName("recent_logs")]
    public List<string> RecentLogs { get; set; } = new();

    [JsonPropertyName("current_log")]
    public string CurrentLog { get; set; } = string.Empty;
}

public class RecommendationRequest
{
    public string Context { get; set; } = string.Empty;
}

// Response DTOs — property names carry [JsonPropertyName] wherever the
// existing AI service (ai-service/main.py) and frontend components rely on
// snake_case JSON keys, so the same DTO deserializes the AI service's reply
// and serializes back to the frontend without any translation layer.
public class ErrorAnalysisResponse
{
    [JsonPropertyName("root_cause")]
    public string RootCause { get; set; } = string.Empty;

    public string Severity { get; set; } = "medium";

    [JsonPropertyName("severity_score")]
    public int SeverityScore { get; set; }

    public List<string> Fixes { get; set; } = new();

    public string Prevention { get; set; } = string.Empty;
}

public class MismatchDto
{
    public string Path { get; set; } = string.Empty;
    public string Issue { get; set; } = string.Empty;
    public string? Expected { get; set; }
    public string? Actual { get; set; }
}

public class FixSuggestionDto
{
    public string Path { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
}

public class ApiValidationResponse
{
    public List<MismatchDto> Mismatches { get; set; } = new();

    [JsonPropertyName("reliability_score")]
    public int ReliabilityScore { get; set; }

    public List<FixSuggestionDto> Suggestions { get; set; } = new();
}

public class PredictionResponse
{
    [JsonPropertyName("failure_risk_score")]
    public int FailureRiskScore { get; set; }

    [JsonPropertyName("risk_level")]
    public string RiskLevel { get; set; } = "low";

    public string Reasoning { get; set; } = string.Empty;
}

public class RecommendationItem
{
    public string Category { get; set; } = string.Empty;
    public string Suggestion { get; set; } = string.Empty;
}

public class RecommendationResponse
{
    public List<RecommendationItem> Recommendations { get; set; } = new();
}
