using Kairon.Backend.DTOs;
using Kairon.Backend.DTOs.Sre;

namespace Kairon.Backend.Services;

/// <summary>
/// The backend's only view of AI. Everything model-specific (Qwen, Gemini, Groq, prompts,
/// provider selection) lives behind the FastAPI service; the backend never references a provider
/// SDK (PRD section 9).
/// </summary>
public interface IAiMicroservice
{
    // --- Existing capabilities. Preserved exactly; the original screens still call these. ---
    Task<ErrorAnalysisResponse> AnalyzeErrorAsync(string log, CancellationToken cancellationToken = default);
    Task<PredictionResponse> PredictAsync(List<string> recentLogs, string currentLog, CancellationToken cancellationToken = default);
    Task<RecommendationResponse> RecommendAsync(string context, CancellationToken cancellationToken = default);
    Task<List<FixSuggestionDto>> SuggestFixesAsync(List<MismatchDto> mismatches, CancellationToken cancellationToken = default);
    bool IsAvailable { get; }

    // --- Autonomous SRE addition. ---

    /// <summary>
    /// Runs a full investigation over a bounded evidence package and returns a structured,
    /// already-validated result. Throws <see cref="AiUnavailableException"/> when the AI service
    /// cannot produce a usable answer - callers must treat that as "no diagnosis", never as a
    /// reason to stop ingesting telemetry.
    /// </summary>
    Task<InvestigationResultDto> InvestigateAsync(
        EvidencePackageDto evidence,
        CancellationToken cancellationToken = default);

    /// <summary>Reports whether the client is serving mock responses, for the health endpoint.</summary>
    string Mode { get; }
}

/// <summary>
/// Raised when the AI service is unreachable, times out, or returns something that fails schema
/// validation. Distinct from a generic exception so orchestration can degrade gracefully instead
/// of marking an incident failed for the wrong reason.
/// </summary>
public class AiUnavailableException : Exception
{
    public AiUnavailableException(string message) : base(message) { }
    public AiUnavailableException(string message, Exception inner) : base(message, inner) { }
}
