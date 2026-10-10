using Kairon.Backend.Services.Remediation.Tools;
using Kairon.Backend.Configuration;
using Kairon.Backend.DTOs;
using Kairon.Backend.DTOs.Sre;
using Kairon.Backend.Services.Audit;
using Kairon.Backend.Services.Remediation;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Kairon.Backend.Services;

/// <summary>Process-wide AI reachability/circuit-breaker state. AiMicroservice is a typed
/// HttpClient (transient), so per-instance fields were reset on every request: a revoked key kept
/// reporting "reachable" and the cooldown never applied. One singleton instance is shared by every
/// AiMicroservice the container creates.</summary>
public sealed class AiAvailabilityState
{
    private readonly object _gate = new();
    private bool _available = true;
    private DateTime _failedAt = DateTime.MinValue;

    public bool IsAvailable { get { lock (_gate) return _available; } }
    public DateTime LastFailure { get { lock (_gate) return _failedAt; } }

    public void Succeeded() { lock (_gate) _available = true; }

    public void Failed(DateTime at) { lock (_gate) { _available = false; _failedAt = at; } }
}

public class AiMicroservice : IAiMicroservice
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly AiOrchestrationOptions _aiOptions;
    private readonly ILogger<AiMicroservice> _logger;
    private readonly IAiProviderConfigService _providerConfig;
    private readonly bool _staticMockModeDefault;
    private readonly TimeProvider _time;

    private readonly AiAvailabilityState _state;
    private static readonly TimeSpan RecoveryCooldown = TimeSpan.FromMinutes(2);

    /// <summary>
    /// RB-007: whether the provider is ACTUALLY known to be reachable right now - true from
    /// process start (an optimistic default, since nothing has failed yet) or after the most
    /// recent real call attempt succeeded. This is what "AiProviderReachable" reports externally
    /// (HealthStatusController, IncidentQueryService) and it deliberately never flips back to true
    /// merely because <see cref="RecoveryCooldown"/> elapsed - a cooldown window expiring proves
    /// nothing about whether the provider actually came back; only a real, successful call does.
    /// A provider that is still down keeps reporting unreachable, however long its cooldown has
    /// been over, until an attempt genuinely succeeds again.
    /// </summary>
    public bool IsAvailable => _state.IsAvailable;

    /// <summary>Internal gate only: whether a NEW call attempt should even be tried right now.
    /// After the cooldown window, exactly one attempt is let through again (a half-open circuit-
    /// breaker probe) - this does not itself mean the provider is reachable (see
    /// <see cref="IsAvailable"/>), only that it is worth trying. A failed probe immediately resets
    /// the cooldown clock, so a still-down provider is retried at most once per window rather than
    /// on every request.</summary>
    private bool ShouldAttempt => _state.IsAvailable || (_time.GetUtcNow().UtcDateTime - _state.LastFailure) > RecoveryCooldown;

    public AiMicroservice(
        HttpClient httpClient,
        IConfiguration configuration,
        IOptions<AiOrchestrationOptions> aiOptions,
        IAiProviderConfigService providerConfig,
        ILogger<AiMicroservice> logger,
        TimeProvider? time = null,
        AiAvailabilityState? state = null)
    {
        _state = state ?? new AiAvailabilityState();
        _httpClient = httpClient;
        _configuration = configuration;
        _aiOptions = aiOptions.Value;
        _providerConfig = providerConfig;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _staticMockModeDefault = configuration.GetValue<bool>("AiService:MockMode", false);
    }

    /// <summary>A saved AI Configuration always overrides appsettings.json's static MockMode
    /// default (frontend PRD section 13: user configuration saved through the KAIRON UI takes
    /// precedence). A deployment that has never touched the new UI has no saved row, so this
    /// falls straight through to the existing appsettings.json behaviour - unchanged.</summary>
    private async Task<bool> EffectiveMockModeAsync(CancellationToken cancellationToken)
    {
        if (await _providerConfig.HasValidConfigurationAsync(cancellationToken)) return false;
        return _staticMockModeDefault;
    }

    public async Task<string> GetModeAsync(CancellationToken cancellationToken = default)
    {
        var selection = await _providerConfig.GetSelectionAsync(cancellationToken);
        var configured = selection is not null && await _providerConfig.HasValidConfigurationAsync(cancellationToken);
        if (!configured && _staticMockModeDefault) return "mock";
        try {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await _httpClient.GetAsync("/health", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(4096, timeout.Token);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var mode = body.RootElement.GetProperty("mode").GetString();
            if (mode == "mock") return "mock";
            // A real provider was selected but the AI service itself has no usable credential for
            // it (kairon.providers.UnconfiguredProvider) - the process is genuinely up and
            // reachable, so this must never be conflated with "unknown" (this response was
            // unparsable/unexpected) or "unavailable" (could not even be reached). Both the
            // dashboard and the per-incident AiMode field read this exact string.
            if (mode == "unconfigured") return "unconfigured";
            if (mode == "live") return configured ? selection!.Value.Provider : "live";
            return "unknown";
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return "unavailable"; }
    }

    public async Task<ErrorAnalysisResponse> AnalyzeErrorAsync(string log, CancellationToken cancellationToken = default)
    {
        if (await EffectiveMockModeAsync(cancellationToken))
            return GetMockErrorAnalysis();

        if (!ShouldAttempt)
            throw new InvalidOperationException("AI service is currently unavailable");

        try
        {
            var request = new { log };
            var response = await PostAsync<ErrorAnalysisResponse>("/analyze-error", request, cancellationToken);
            _state.Succeeded();
            return response ?? throw new InvalidOperationException("Empty response from AI service");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call AI service for error analysis");
            _state.Failed(_time.GetUtcNow().UtcDateTime);
            throw;
        }
    }

    public async Task<PredictionResponse> PredictAsync(List<string> recentLogs, string currentLog, CancellationToken cancellationToken = default)
    {
        if (await EffectiveMockModeAsync(cancellationToken))
            return GetMockPrediction();

        if (!ShouldAttempt)
            throw new InvalidOperationException("AI service is currently unavailable");

        try
        {
            var request = new { recent_logs = recentLogs, current_log = currentLog };
            var response = await PostAsync<PredictionResponse>("/predict", request, cancellationToken);
            _state.Succeeded();
            return response ?? throw new InvalidOperationException("Empty response from AI service");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call AI service for prediction");
            _state.Failed(_time.GetUtcNow().UtcDateTime);
            throw;
        }
    }

    public async Task<RecommendationResponse> RecommendAsync(string context, CancellationToken cancellationToken = default)
    {
        if (await EffectiveMockModeAsync(cancellationToken))
            return GetMockRecommendation();

        if (!ShouldAttempt)
            throw new InvalidOperationException("AI service is currently unavailable");

        try
        {
            var request = new { context };
            var response = await PostAsync<RecommendationResponse>("/recommend", request, cancellationToken);
            _state.Succeeded();
            return response ?? throw new InvalidOperationException("Empty response from AI service");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call AI service for recommendations");
            _state.Failed(_time.GetUtcNow().UtcDateTime);
            throw;
        }
    }

    public async Task<List<FixSuggestionDto>> SuggestFixesAsync(List<MismatchDto> mismatches, CancellationToken cancellationToken = default)
    {
        if (await EffectiveMockModeAsync(cancellationToken))
            return GetMockFixSuggestions();

        if (!ShouldAttempt)
            throw new InvalidOperationException("AI service is currently unavailable");

        try
        {
            var request = new { mismatches };
            var response = await PostAsync<SuggestFixesResponse>("/suggest-fixes", request, cancellationToken);
            _state.Succeeded();
            return response?.Suggestions ?? new List<FixSuggestionDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call AI service for fix suggestions");
            _state.Failed(_time.GetUtcNow().UtcDateTime);
            throw;
        }
    }

    // --- Autonomous SRE investigation (PRD section 9) ---

    public async Task<InvestigationResultDto> InvestigateAsync(
        EvidencePackageDto evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (await EffectiveMockModeAsync(cancellationToken))
            return GetMockInvestigation(evidence);

        if (!ShouldAttempt)
            throw new AiUnavailableException("AI service is in a failed state and is cooling down");

        var attempts = Math.Max(1, _aiOptions.MaxRetries + 1);
        Exception? last = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stopwatch = Stopwatch.StartNew();

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(_aiOptions.TimeoutSeconds));

                var result = await PostAsync<InvestigationResultDto>("/analyze", evidence, timeout.Token);

                if (result is null || string.IsNullOrWhiteSpace(result.RootCause))
                {
                    // A structurally empty answer is a malformed answer. Rejecting it is what
                    // stops model noise from being written into incident state (AI PRD section 8).
                    throw new AiUnavailableException("AI service returned an empty or unusable investigation result");
                }

                _state.Succeeded();

                _logger.LogInformation(
                    "AI investigation for {IncidentKey} succeeded in {Ms}ms on attempt {Attempt} (provider={Provider}, model={Model}, confidence={Confidence})",
                    evidence.Incident.IncidentKey, stopwatch.ElapsedMilliseconds, attempt,
                    result.Provider ?? "unknown", result.Model ?? "unknown", result.Confidence);

                return Normalize(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Caller-initiated cancellation is not a service failure; do not trip the breaker.
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                _logger.LogWarning(
                    "AI investigation attempt {Attempt}/{Attempts} for {IncidentKey} failed after {Ms}ms: {Error}",
                    attempt, attempts, evidence.Incident.IncidentKey, stopwatch.ElapsedMilliseconds,
                    Redaction.Describe(ex));

                if (attempt < attempts)
                {
                    // Bounded linear backoff. Never retries indefinitely (AI PRD section 12).
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
                }
            }
        }

        _state.Failed(_time.GetUtcNow().UtcDateTime);

        throw new AiUnavailableException(
            $"AI investigation failed after {attempts} attempt(s): {Redaction.Describe(last!)}", last!);
    }

    // --- AI Configuration panel (frontend) ---

    public async Task<AiConfigureResponseDto> ConfigureProviderAsync(
        AiConfigureRequestDto request, CancellationToken cancellationToken = default)
    {
        var response = await PostAsync<AiConfigureResponseDto>("/configure", request, cancellationToken);
        return response ?? throw new InvalidOperationException("Empty response from AI service");
    }

    public async Task<AiConfigureResponseDto> ClearProviderAsync(CancellationToken cancellationToken = default)
    {
        var response = await PostAsync<AiConfigureResponseDto>("/configure/clear", new { }, cancellationToken);
        return response ?? throw new InvalidOperationException("Empty response from AI service");
    }

    public async Task<AiTestConnectionResponseDto> TestProviderConnectionAsync(
        AiConfigureRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await PostAsync<AiTestConnectionResponseDto>("/configure/test", request, cancellationToken);
            return response ?? new AiTestConnectionResponseDto { Success = false, Error = "Empty response from AI service." };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // The AI service being unreachable IS the test result here, not an error to bubble up -
            // "Test Connection" exists precisely to surface this to the user (never the raw key).
            _logger.LogWarning(ex, "AI service unreachable while testing a provider connection");
            return new AiTestConnectionResponseDto
            {
                Provider = request.Provider,
                Success = false,
                Error = "Could not reach the AI service. Is KAIRON fully started?",
            };
        }
    }

    public async Task<AiModelsResponseDto> ListProviderModelsAsync(
        AiConfigureRequestDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await PostAsync<AiModelsResponseDto>("/models", request, cancellationToken);
            return response ?? new AiModelsResponseDto { Provider = request.Provider, Supported = false };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "AI service unreachable while listing provider models");
            return new AiModelsResponseDto
            {
                Provider = request.Provider,
                Supported = false,
                Error = "Could not reach the AI service.",
            };
        }
    }

    /// <summary>
    /// Clamps and defaults whatever the AI service returned. The service validates its own schema,
    /// but the backend does not trust that as a guarantee - this is the second gate.
    /// </summary>
    private static InvestigationResultDto Normalize(InvestigationResultDto result)
    {
        result.Confidence = Math.Clamp(result.Confidence, 0, 1);
        result.Severity = string.IsNullOrWhiteSpace(result.Severity) ? "medium" : result.Severity.ToLowerInvariant();
        result.EstimatedRisk = string.IsNullOrWhiteSpace(result.EstimatedRisk) ? "medium" : result.EstimatedRisk.ToLowerInvariant();
        result.ContributingFactors ??= new List<string>();
        result.Evidence ??= new List<string>();
        result.AffectedComponents ??= new List<string>();
        result.Recommendations ??= new List<AiRecommendationDto>();

        foreach (var rec in result.Recommendations)
        {
            rec.RiskLevel = string.IsNullOrWhiteSpace(rec.RiskLevel) ? "medium" : rec.RiskLevel.ToLowerInvariant();
            rec.Action = rec.Action?.Trim() ?? string.Empty;
        }

        return result;
    }

    private class SuggestFixesResponse
    {
        public List<FixSuggestionDto> Suggestions { get; set; } = new();
    }

    private async Task<T?> PostAsync<T>(string endpoint, object request, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(await DescribeFailureAsync(response, cancellationToken), null, response.StatusCode);

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<T>(responseJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    /// <summary>The AI service answers failures with {"error": "..."} naming the real cause
    /// (invalid provider key, provider rate limit, timeout, malformed model output). Keep that
    /// reason - bounded - so an incident shows WHY investigation failed, not just "503".</summary>
    private static async Task<string> DescribeFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = $"AI service returned HTTP {(int)response.StatusCode}";
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (body.Length > 4096) body = body[..4096];
            using var json = JsonDocument.Parse(body);
            foreach (var name in new[] { "error", "detail", "message" })
                if (json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    var reason = value.GetString()!;
                    return $"{status}: {(reason.Length > 500 ? reason[..500] : reason)}";
                }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or HttpRequestException) { }
        return status;
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

    /// <summary>
    /// Deterministic mock investigation (AI PRD section 13): reads the actual evidence and applies the
    /// same decision rules a real model is instructed to follow (DeterministicInvestigation), but
    /// never calls a provider and needs no credentials.
    /// </summary>
    private static InvestigationResultDto GetMockInvestigation(EvidencePackageDto evidence) =>
        DeterministicInvestigation.Investigate(evidence);
}
