using Kairon.Backend.DTOs.Sre;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace Kairon.Backend.Controllers;

/// <summary>
/// The frontend's AI Configuration panel: pick a provider, paste an API key, optionally pick a
/// model, Test Connection, Save - no .env, no appsettings.json, no restart. Operator-gated the
/// same way ProjectsController is, since this is exactly as security-relevant as project
/// credential management.
/// </summary>
[ApiController]
[Route("api/v1/ai-config")]
[RequiresOperator]
public sealed class AiConfigController : ControllerBase
{
    /// <summary>The only providers the frontend may select - matches the AI service's own
    /// PROVIDERS registry minus "mock", which is an internal fallback, not a user-facing choice.</summary>
    private static readonly HashSet<string> SupportedProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "groq", "qwen", "gemini"
    };

    private readonly IAiProviderConfigService _config;
    private readonly IAiMicroservice _ai;

    public AiConfigController(IAiProviderConfigService config, IAiMicroservice ai)
    {
        _config = config;
        _ai = ai;
    }

    [HttpGet]
    [RequiresOperator]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var r = ToResponse(await _config.GetAsync(cancellationToken));
        return Ok(new { r.provider, r.model, r.endpoint, r.hasApiKey, r.updatedAt });
    }

    [HttpPost]
    [RequiresOperator]
    public async Task<IActionResult> Save([FromBody] SaveAiConfigRequest request, CancellationToken cancellationToken)
    {
        var provider = (request.Provider ?? string.Empty).Trim().ToLowerInvariant();
        if (!SupportedProviders.Contains(provider))
            return BadRequest(new { error = $"Unsupported provider. Choose one of: {string.Join(", ", SupportedProviders)}." });

        // Persist first - if the AI service call below fails (e.g. it hasn't finished starting
        // yet), the user's key is not lost, and the startup sync applies it on the next attempt.
        var summary = await _config.SaveAsync(provider, request.ApiKey, request.Model, request.Endpoint, cancellationToken);

        var apiKey = request.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = await _config.GetDecryptedApiKeyAsync(cancellationToken);

        var r = ToResponse(summary);
        try
        {
            await _ai.ConfigureProviderAsync(
                new AiConfigureRequestDto { Provider = provider, ApiKey = apiKey, Model = request.Model, Endpoint = request.Endpoint },
                cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Saved, but not yet live - the startup sync (or the next Save/Test) will apply it.
            // Still a successful save from the operator's point of view; the key is never lost.
            return Ok(new
            {
                r.provider, r.model, r.endpoint, r.hasApiKey, r.updatedAt,
                applied = false,
                warning = "Saved, but the AI service did not respond - it may still be starting.",
            });
        }

        return Ok(new { r.provider, r.model, r.endpoint, r.hasApiKey, r.updatedAt, applied = true });
    }

    [HttpPost("test")]
    [RequiresOperator]
    public async Task<IActionResult> Test([FromBody] SaveAiConfigRequest request, CancellationToken cancellationToken)
    {
        var provider = (request.Provider ?? string.Empty).Trim().ToLowerInvariant();
        if (!SupportedProviders.Contains(provider))
            return BadRequest(new { error = $"Unsupported provider. Choose one of: {string.Join(", ", SupportedProviders)}." });

        var apiKey = await ResolveApiKeyForTestAsync(provider, request.ApiKey, cancellationToken);
        var endpoint = await ResolveEndpointForTestAsync(provider, request.Endpoint, cancellationToken);

        var result = await _ai.TestProviderConnectionAsync(
            new AiConfigureRequestDto { Provider = provider, ApiKey = apiKey, Model = request.Model, Endpoint = endpoint },
            cancellationToken);

        return Ok(new
        {
            success = result.Success,
            provider = result.Provider,
            effectiveProvider = result.EffectiveProvider,
            model = result.Model,
            endpoint = result.Endpoint,
            error = result.Error,
        });
    }

    [HttpPost("models")]
    [RequiresOperator]
    public async Task<IActionResult> Models([FromBody] SaveAiConfigRequest request, CancellationToken cancellationToken)
    {
        var provider = (request.Provider ?? string.Empty).Trim().ToLowerInvariant();
        if (!SupportedProviders.Contains(provider))
            return BadRequest(new { error = $"Unsupported provider. Choose one of: {string.Join(", ", SupportedProviders)}." });

        var apiKey = await ResolveApiKeyForTestAsync(provider, request.ApiKey, cancellationToken);

        var result = await _ai.ListProviderModelsAsync(
            new AiConfigureRequestDto { Provider = provider, ApiKey = apiKey }, cancellationToken);

        return Ok(new { provider = result.Provider, supported = result.Supported, models = result.Models, error = result.Error });
    }

    /// <summary>A blank key in the request means "use whatever is already saved for this provider"
    /// - lets the operator test or browse models again without retyping a key that already works.
    /// Only applies when the stored config is for the SAME provider being tested.</summary>
    private async Task<string?> ResolveApiKeyForTestAsync(string provider, string? suppliedKey, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(suppliedKey)) return suppliedKey;

        var selection = await _config.GetSelectionAsync(cancellationToken);
        if (selection is { } current && string.Equals(current.Provider, provider, StringComparison.OrdinalIgnoreCase))
            return await _config.GetDecryptedApiKeyAsync(cancellationToken);

        return null;
    }

    /// <summary>Mirrors <see cref="ResolveApiKeyForTestAsync"/>: a blank endpoint in the request
    /// means "use whatever is already saved for this provider", not "clear the override".</summary>
    private async Task<string?> ResolveEndpointForTestAsync(string provider, string? suppliedEndpoint, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(suppliedEndpoint)) return suppliedEndpoint;

        var selection = await _config.GetSelectionAsync(cancellationToken);
        if (selection is { } current && string.Equals(current.Provider, provider, StringComparison.OrdinalIgnoreCase))
            return current.Endpoint;

        return null;
    }

    private static (string provider, string model, string endpoint, bool hasApiKey, DateTime? updatedAt) ToResponse(AiProviderConfigSummary summary) =>
        (summary.Provider, summary.Model, summary.Endpoint, summary.HasApiKey, summary.UpdatedAt);
}

public sealed class SaveAiConfigRequest
{
    public string Provider { get; set; } = string.Empty;

    /// <summary>Optional. Omit to keep the previously saved key (e.g. when only changing model).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Optional/blank means "Auto / Recommended".</summary>
    public string? Model { get; set; }

    /// <summary>Optional. Overrides the provider's default endpoint - for a provider fronted by a
    /// dedicated/regional URL instead of the shared public one. Blank means the provider's
    /// default.</summary>
    public string? Endpoint { get; set; }
}
