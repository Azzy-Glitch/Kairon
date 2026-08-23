using AIDIP.Backend.DTOs;

namespace AIDIP.Backend.Services;

public interface IAiMicroservice
{
    Task<ErrorAnalysisResponse> AnalyzeErrorAsync(string log, CancellationToken cancellationToken = default);
    Task<PredictionResponse> PredictAsync(List<string> recentLogs, string currentLog, CancellationToken cancellationToken = default);
    Task<RecommendationResponse> RecommendAsync(string context, CancellationToken cancellationToken = default);
    Task<List<FixSuggestionDto>> SuggestFixesAsync(List<MismatchDto> mismatches, CancellationToken cancellationToken = default);
    bool IsAvailable { get; }
}