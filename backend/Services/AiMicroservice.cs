using AIDIP.Backend.DTOs;
using System.Text;
using System.Text.Json;

namespace AIDIP.Backend.Services;

public class AiMicroservice : IAiMicroservice
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AiMicroservice> _logger;
    private readonly bool _mockMode;

    private bool _isAvailable = true;
    private DateTime _lastFailure = DateTime.MinValue;
    private static readonly TimeSpan RecoveryCooldown = TimeSpan.FromMinutes(2);

    public bool IsAvailable => _isAvailable || (DateTime.UtcNow - _lastFailure) > RecoveryCooldown;

    public AiMicroservice(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<AiMicroservice> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
        _mockMode = configuration.GetValue<bool>("AiService:MockMode", false);
    }

    public async Task<ErrorAnalysisResponse> AnalyzeErrorAsync(string log, CancellationToken cancellationToken = default)
    {
        if (_mockMode)
            return GetMockErrorAnalysis();

        if (!IsAvailable)
            throw new InvalidOperationException("AI service is currently unavailable");

        try
        {
            var request = new { log };
            var response = await PostAsync<ErrorAnalysisResponse>("/analyze-error", request, cancellationToken);
            _isAvailable = true;
            return response ?? throw new InvalidOperationException("Empty response from AI service");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call AI service for error analysis");
            _isAvailable = false;
            _lastFailure = DateTime.UtcNow;
            throw;
        }
    }

    public async Task<PredictionResponse> PredictAsync(List<string> recentLogs, string currentLog, CancellationToken cancellationToken = default)
    {
        if (_mockMode)
            return GetMockPrediction();

        if (!IsAvailable)
            throw new InvalidOperationException("AI service is currently unavailable");

        try
        {
            var request = new { recent_logs = recentLogs, current_log = currentLog };
            var response = await PostAsync<PredictionResponse>("/predict", request, cancellationToken);
            _isAvailable = true;
            return response ?? throw new InvalidOperationException("Empty response from AI service");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call AI service for prediction");
            _isAvailable = false;
            _lastFailure = DateTime.UtcNow;
            throw;
        }
    }

    public async Task<RecommendationResponse> RecommendAsync(string context, CancellationToken cancellationToken = default)
    {
        if (_mockMode)
            return GetMockRecommendation();

        if (!IsAvailable)
            throw new InvalidOperationException("AI service is currently unavailable");

        try
        {
            var request = new { context };
            var response = await PostAsync<RecommendationResponse>("/recommend", request, cancellationToken);
            _isAvailable = true;
            return response ?? throw new InvalidOperationException("Empty response from AI service");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call AI service for recommendations");
            _isAvailable = false;
            _lastFailure = DateTime.UtcNow;
            throw;
        }
    }

    public async Task<List<FixSuggestionDto>> SuggestFixesAsync(List<MismatchDto> mismatches, CancellationToken cancellationToken = default)
    {
        if (_mockMode)
            return GetMockFixSuggestions();

        if (!IsAvailable)
            throw new InvalidOperationException("AI service is currently unavailable");

        try
        {
            var request = new { mismatches };
            var response = await PostAsync<SuggestFixesResponse>("/suggest-fixes", request, cancellationToken);
            _isAvailable = true;
            return response?.Suggestions ?? new List<FixSuggestionDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call AI service for fix suggestions");
            _isAvailable = false;
            _lastFailure = DateTime.UtcNow;
            throw;
        }
    }

    private class SuggestFixesResponse
    {
        public List<FixSuggestionDto> Suggestions { get; set; } = new();
    }

    private async Task<T?> PostAsync<T>(string endpoint, object request, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(endpoint, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<T>(responseJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    // --- Mock helpers (only used when AiService:MockMode = true) ---
    private static ErrorAnalysisResponse GetMockErrorAnalysis() => new()
    {
        RootCause = "Mock: Null reference before initialization",
        Severity = "high",
        SeverityScore = 78,
        Fixes = new List<string> { "Add null check", "Initialize default value" },
        Prevention = "Use TypeScript interfaces"
    };

    private static PredictionResponse GetMockPrediction() => new()
    {
        FailureRiskScore = 65,
        RiskLevel = "moderate",
        Reasoning = "Mock: Timeout cascade pattern suggests downstream service degradation"
    };

    private static RecommendationResponse GetMockRecommendation() => new()
    {
        Recommendations = new List<RecommendationItem>
        {
            new() { Category = "performance", Suggestion = "Mock: Add Redis caching for session store" },
            new() { Category = "security", Suggestion = "Mock: Add rate limiting to /login" }
        }
    };

    private static List<FixSuggestionDto> GetMockFixSuggestions() => new()
    {
        new FixSuggestionDto { Path = "mock", Explanation = "Mock: Cast string to integer using parseInt()" }
    };
}
