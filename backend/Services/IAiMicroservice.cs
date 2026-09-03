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

    /// <summary>"mock", or the active provider name (e.g. "groq") once a real AI Configuration has
    /// been saved - async because it may need to check the saved configuration.</summary>
    Task<string> GetModeAsync(CancellationToken cancellationToken = default);

    // --- AI Configuration panel (frontend) ---

    /// <summary>Applies a provider/model/key change on the AI service immediately - no restart.
    /// Does not persist anything itself; the caller (AiConfigController) owns persistence via
    /// IAiProviderConfigService and calls this only after a successful save.</summary>
    Task<AiConfigureResponseDto> ConfigureProviderAsync(
        AiConfigureRequestDto request, CancellationToken cancellationToken = default);

    /// <summary>Validates a provider/key/model combination with one real call, without touching
    /// whatever is currently configured and live.</summary>
    Task<AiTestConnectionResponseDto> TestProviderConnectionAsync(
        AiConfigureRequestDto request, CancellationToken cancellationToken = default);

    /// <summary>Live model discovery where the provider supports it (Groq only today); other
    /// providers report `Supported = false` so the frontend falls back to manual model entry.</summary>
    Task<AiModelsResponseDto> ListProviderModelsAsync(
        AiConfigureRequestDto request, CancellationToken cancellationToken = default);
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
