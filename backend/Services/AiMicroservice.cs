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

public class AiMicroservice : IAiMicroservice
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly AiOrchestrationOptions _aiOptions;
    private readonly ILogger<AiMicroservice> _logger;
    private readonly IAiProviderConfigService _providerConfig;
    private readonly bool _staticMockModeDefault;

    private bool _isAvailable = true;
    private DateTime _lastFailure = DateTime.MinValue;
    private static readonly TimeSpan RecoveryCooldown = TimeSpan.FromMinutes(2);

    public bool IsAvailable => _isAvailable || (DateTime.UtcNow - _lastFailure) > RecoveryCooldown;

    public AiMicroservice(
        HttpClient httpClient,
        IConfiguration configuration,
        IOptions<AiOrchestrationOptions> aiOptions,
        IAiProviderConfigService providerConfig,
        ILogger<AiMicroservice> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _aiOptions = aiOptions.Value;
        _providerConfig = providerConfig;
        _logger = logger;
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
        if (selection is { } config && await _providerConfig.HasValidConfigurationAsync(cancellationToken))
            return config.Provider;
        return _staticMockModeDefault ? "mock" : "live";
    }

    public async Task<ErrorAnalysisResponse> AnalyzeErrorAsync(string log, CancellationToken cancellationToken = default)
    {
        if (await EffectiveMockModeAsync(cancellationToken))
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
        if (await EffectiveMockModeAsync(cancellationToken))
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
        if (await EffectiveMockModeAsync(cancellationToken))
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
        if (await EffectiveMockModeAsync(cancellationToken))
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

    // --- Autonomous SRE investigation (PRD section 9) ---

    public async Task<InvestigationResultDto> InvestigateAsync(
        EvidencePackageDto evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (await EffectiveMockModeAsync(cancellationToken))
            return GetMockInvestigation(evidence);

        if (!IsAvailable)
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

                _isAvailable = true;

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

        _isAvailable = false;
        _lastFailure = DateTime.UtcNow;

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

    /// <summary>
    /// Deterministic mock investigation (AI PRD section 13). It reads the actual evidence so the
    /// demo shows real numbers, but it never calls a provider and needs no credentials.
    /// </summary>
    private static InvestigationResultDto GetMockInvestigation(EvidencePackageDto evidence)
    {
        var signals = evidence.CorrelatedSignals;
        var hasRetryStorm = signals.Any(s => s.Metric == "retries");
        var hasCpu = signals.Any(s => s.Metric == "cpu");
        var hasLatency = signals.Any(s => s.Metric == "latency");
        var hasErrors = signals.Any(s => s.Metric is "errorRate" or "errors");

        var rootCause = hasRetryStorm
            ? "Controlled retry loop causing repeated downstream requests, saturating worker threads."
            : hasCpu && hasLatency
                ? "CPU saturation is driving request latency above the configured threshold."
                : hasErrors
                    ? "A repeating downstream failure is driving the error rate above threshold."
                    : "Resource pressure on the affected service.";

        var recommendation = hasRetryStorm
            ? new AiRecommendationDto
            {
                Action = DemoToolNames.DisableDemoRetryLoop,
                Reason = "Retry volume is the leading signal; every other metric follows it.",
                ExpectedOutcome = "Retry count returns to baseline and CPU utilization decreases.",
                RiskLevel = "low"
            }
            : hasCpu
                ? new AiRecommendationDto
                {
                    Action = DemoToolNames.ReduceDemoWorkerConcurrency,
                    Reason = "Worker concurrency is above what the service can sustain at this load.",
                    ExpectedOutcome = "CPU utilization drops back under the threshold.",
                    RiskLevel = "low"
                }
                : new AiRecommendationDto
                {
                    Action = DemoToolNames.RunHealthCheck,
                    Reason = "Evidence is insufficient for a targeted action; confirm current state first.",
                    ExpectedOutcome = "Fresh health data for the affected service.",
                    RiskLevel = "low"
                };

        return new InvestigationResultDto
        {
            Summary = $"{evidence.Incident.Service} is degraded: {string.Join("; ", evidence.Incident.Symptoms.Take(3))}",
            RootCause = rootCause,
            ContributingFactors = signals.Select(s => s.Symptom).Take(5).ToList(),
            Evidence = signals
                .Select(s => $"{s.Metric} {s.Observed}{s.Unit} vs threshold {s.Threshold}{s.Unit}")
                .Take(6)
                .ToList(),
            Confidence = hasRetryStorm ? 0.92 : 0.74,
            Severity = evidence.Incident.Severity.ToLowerInvariant(),
            AffectedComponents = new List<string>
            {
                evidence.Incident.AffectedComponent,
                evidence.Incident.Service
            }.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList(),
            PredictedFailure = hasRetryStorm
                ? "Request backlog will continue growing and order processing latency will keep increasing."
                : "Degradation will continue and begin affecting dependent endpoints.",
            EstimatedRisk = evidence.Incident.Severity.ToLowerInvariant() is "critical" or "high" ? "high" : "medium",
            Recommendations = new List<AiRecommendationDto> { recommendation },
            Provider = "mock",
            Model = "deterministic-mock"
        };
    }
}
