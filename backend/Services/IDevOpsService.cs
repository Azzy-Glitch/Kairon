using Kairon.Backend.DTOs;

namespace Kairon.Backend.Services;

public interface IDevOpsService
{
    Task<ErrorAnalysisResponse> AnalyzeErrorAsync(ErrorAnalysisRequest request, CancellationToken cancellationToken = default);
    Task<ApiValidationResponse> ValidateApiAsync(ApiValidationRequest request, CancellationToken cancellationToken = default);
    Task<PredictionResponse> PredictAsync(PredictionRequest request, CancellationToken cancellationToken = default);
    Task<RecommendationResponse> RecommendAsync(RecommendationRequest request, CancellationToken cancellationToken = default);
    Task<List<AnalysisHistoryItem>> GetHistoryAsync(int limit = 25, CancellationToken cancellationToken = default);
    Task<AnalysisStatsResponse> GetStatsAsync(CancellationToken cancellationToken = default);
    Task ClearHistoryAsync(CancellationToken cancellationToken = default);
}