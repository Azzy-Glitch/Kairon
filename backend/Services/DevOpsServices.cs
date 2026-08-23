using AIDIP.Backend.DTOs;
using AIDIP.Backend.Infrastructure;
using AIDIP.Backend.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AIDIP.Backend.Services;

public class DevOpsService : IDevOpsService
{
    private readonly IAiMicroservice _aiService;
    private readonly IContractValidator _contractValidator;
    private readonly AppDbContext _db;
    private readonly ILogger<DevOpsService> _logger;
    private readonly Guid _defaultProjectId;

    public DevOpsService(
        IAiMicroservice aiService,
        IContractValidator contractValidator,
        AppDbContext db,
        IConfiguration configuration,
        ILogger<DevOpsService> logger)
    {
        _aiService = aiService;
        _contractValidator = contractValidator;
        _db = db;
        _logger = logger;
        _defaultProjectId = Guid.TryParse(configuration["AIDIP:ProjectId"], out var id) ? id : Guid.Empty;
    }

    public async Task<ErrorAnalysisResponse> AnalyzeErrorAsync(ErrorAnalysisRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Log))
        {
            return new ErrorAnalysisResponse
            {
                RootCause = "Cannot analyze empty input",
                Severity = "unknown",
                SeverityScore = 0,
                Fixes = new List<string> { "Please provide a valid error log" },
                Prevention = string.Empty
            };
        }

        var result = await _aiService.AnalyzeErrorAsync(request.Log, cancellationToken);
        await RecordAnalysisAsync("error", request.Log, result, result.SeverityScore, cancellationToken);
        return result;
    }

    public async Task<PredictionResponse> PredictAsync(PredictionRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _aiService.PredictAsync(request.RecentLogs, request.CurrentLog, cancellationToken);
        await RecordAnalysisAsync("predict", request.CurrentLog, result, result.FailureRiskScore, cancellationToken);
        return result;
    }

    public async Task<RecommendationResponse> RecommendAsync(RecommendationRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _aiService.RecommendAsync(request.Context, cancellationToken);
        await RecordAnalysisAsync("recommend", request.Context, result, 100, cancellationToken);
        return result;
    }

    public async Task<ApiValidationResponse> ValidateApiAsync(ApiValidationRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        var mismatches = _contractValidator.Compare(request.Expected, request.Actual);
        var score = Math.Max(0, 100 - mismatches.Count * 15);
        var suggestions = mismatches.Count > 0
            ? await _aiService.SuggestFixesAsync(mismatches, cancellationToken)
            : new List<FixSuggestionDto>();

        var result = new ApiValidationResponse
        {
            Mismatches = mismatches,
            ReliabilityScore = score,
            Suggestions = suggestions
        };

        var input = JsonSerializer.Serialize(new { expected = request.Expected, actual = request.Actual });
        await RecordAnalysisAsync("api", input, result, score, cancellationToken);
        return result;
    }

    public async Task<List<AnalysisHistoryItem>> GetHistoryAsync(int limit = 25, CancellationToken cancellationToken = default)
    {
        return await _db.Analyses
            .OrderByDescending(a => a.CreatedAt)
            .Take(limit)
            .Select(a => new AnalysisHistoryItem
            {
                Id = a.Id,
                Type = a.Type,
                Input = a.Input,
                OutputJson = a.OutputJson,
                Score = a.Score,
                CreatedAt = a.CreatedAt
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<AnalysisStatsResponse> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var total = await _db.Analyses.CountAsync(cancellationToken);
        var avgScore = total > 0 ? await _db.Analyses.AverageAsync(a => a.Score ?? 0, cancellationToken) : 0;

        return new AnalysisStatsResponse
        {
            Total = total,
            AvgScore = Math.Round(avgScore, 1),
            ErrorCount = await _db.Analyses.CountAsync(a => a.Type == "error", cancellationToken),
            ApiCount = await _db.Analyses.CountAsync(a => a.Type == "api", cancellationToken),
            PredictCount = await _db.Analyses.CountAsync(a => a.Type == "predict", cancellationToken),
            RecCount = await _db.Analyses.CountAsync(a => a.Type == "recommend", cancellationToken),
            Timestamp = DateTime.UtcNow
        };
    }

    public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        _db.Analyses.RemoveRange(_db.Analyses);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static readonly JsonSerializerOptions AnalysisJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private async Task RecordAnalysisAsync(string type, string input, object output, double score, CancellationToken cancellationToken)
    {
        _db.Analyses.Add(new Analysis
        {
            Id = Guid.NewGuid(),
            ProjectId = _defaultProjectId,
            Type = type,
            Input = input,
            OutputJson = JsonSerializer.Serialize(output, AnalysisJsonOptions),
            Score = score,
            CreatedAt = DateTime.UtcNow
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist analysis record for type {Type}", type);
        }
    }
}
