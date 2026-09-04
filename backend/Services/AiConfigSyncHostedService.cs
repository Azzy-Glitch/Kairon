using Kairon.Backend.DTOs.Sre;

namespace Kairon.Backend.Services;

/// <summary>
/// Re-applies a saved AI Configuration to the AI service on every backend startup.
///
/// The AI service builds its provider configuration once from ai-service/.env at process
/// startup and never re-reads it; it also restarts alongside the backend on every KAIRON launch
/// (both are sibling child processes the desktop shell starts together, with no way for one to
/// signal the other directly). So a user's saved provider/key would otherwise only apply until the
/// next restart, defeating the whole point of "restart preserves configuration". This is what
/// closes that gap: if a row is stored, push it live, retrying briefly while the AI service is
/// still starting.
///
/// A deployment that has never saved anything through the new UI has no stored row, so this is a
/// no-op for it - the AI service's existing env/appsettings-based behaviour is completely
/// untouched (frontend PRD section 13: backward compatibility).
/// </summary>
public sealed class AiConfigSyncHostedService : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AiConfigSyncHostedService> _logger;

    public AiConfigSyncHostedService(IServiceScopeFactory scopeFactory, ILogger<AiConfigSyncHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var providerConfig = scope.ServiceProvider.GetRequiredService<IAiProviderConfigService>();

        var selection = await SafeGetSelectionAsync(providerConfig, stoppingToken);
        if (selection is null)
        {
            _logger.LogInformation("No saved AI Configuration - AI service keeps its env/appsettings-based defaults.");
            return;
        }

        var apiKey = await providerConfig.GetDecryptedApiKeyAsync(stoppingToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("A saved AI Configuration exists but its key could not be decrypted; skipping sync.");
            return;
        }

        var request = new AiConfigureRequestDto
        {
            Provider = selection.Value.Provider,
            ApiKey = apiKey,
            Model = selection.Value.Model,
            Endpoint = selection.Value.Endpoint,
        };

        var deadline = DateTime.UtcNow + GiveUpAfter;
        while (!stoppingToken.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            using var attemptScope = _scopeFactory.CreateScope();
            var ai = attemptScope.ServiceProvider.GetRequiredService<IAiMicroservice>();

            try
            {
                var applied = await ai.ConfigureProviderAsync(request, stoppingToken);
                _logger.LogInformation(
                    "Saved AI Configuration applied to the AI service: provider={Provider} effective={Effective} model={Model}",
                    applied.Provider, applied.EffectiveProvider, applied.Model);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Expected while the AI service is still starting - not worth more than a debug
                // note per attempt.
                _logger.LogDebug(ex, "AI service not ready yet for configuration sync, will retry.");
            }

            try
            {
                await Task.Delay(RetryDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        _logger.LogWarning(
            "Could not apply the saved AI Configuration within {Seconds}s - it will be retried on the next Save or Test Connection.",
            GiveUpAfter.TotalSeconds);
    }

    private async Task<(string Provider, string Model, string Endpoint)?> SafeGetSelectionAsync(
        IAiProviderConfigService providerConfig, CancellationToken cancellationToken)
    {
        try
        {
            return await providerConfig.GetSelectionAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Startup must never fail because of this - a saved configuration is a convenience,
            // not a dependency the rest of the product relies on to boot.
            _logger.LogWarning(ex, "Could not read the saved AI Configuration at startup.");
            return null;
        }
    }
}
