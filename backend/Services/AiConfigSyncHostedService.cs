using Kairon.Backend.DTOs.Sre;

namespace Kairon.Backend.Services;

/// <summary>
/// Keeps a saved AI Configuration applied to the AI service.
///
/// The AI service builds its provider configuration once at process startup and never re-reads
/// it; it also restarts alongside the backend on every KAIRON launch (both are sibling child
/// processes the desktop shell starts, with no way for one to signal the other directly). A saved
/// provider/key would otherwise only apply until the next restart.
///
/// This is a reconcile loop rather than a one-shot startup push: it retries every few seconds
/// until the saved configuration is applied (the AI service starts after the backend, possibly
/// well beyond a minute later), then re-checks periodically and re-applies whenever the AI service
/// reports "unconfigured" again (AI process restarted, a Save's live push failed, a /configure
/// call was rate limited). The saved row is re-read every pass, so a later Save is picked up.
///
/// A deployment that has never saved anything through the UI has no stored row, so this never
/// pushes anything - the AI service's env/appsettings-based behaviour is untouched.
/// </summary>
public sealed class AiConfigSyncHostedService : BackgroundService
{
    private static readonly TimeSpan PendingRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AppliedCheckInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AiConfigSyncHostedService> _logger;

    public AiConfigSyncHostedService(IServiceScopeFactory scopeFactory, ILogger<AiConfigSyncHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var applied = false;
        var warnedUndecryptable = false;
        var pendingSince = DateTime.UtcNow;
        var warnedPending = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var providerConfig = scope.ServiceProvider.GetRequiredService<IAiProviderConfigService>();
                var ai = scope.ServiceProvider.GetRequiredService<IAiMicroservice>();

                var selection = await SafeGetSelectionAsync(providerConfig, stoppingToken);
                if (selection is null)
                {
                    applied = false;
                }
                else if (applied && await ai.GetModeAsync(stoppingToken) != "unconfigured")
                {
                    // Still applied (or the AI service is simply unreachable right now, which a
                    // push cannot fix). Nothing to do.
                }
                else
                {
                    var apiKey = await providerConfig.GetDecryptedApiKeyAsync(stoppingToken);
                    if (string.IsNullOrWhiteSpace(apiKey))
                    {
                        if (!warnedUndecryptable)
                            _logger.LogWarning("A saved AI Configuration exists but its key could not be decrypted; re-save it in Settings.");
                        warnedUndecryptable = true;
                        applied = false;
                    }
                    else
                    {
                        var result = await ai.ConfigureProviderAsync(new AiConfigureRequestDto
                        {
                            Provider = selection.Value.Provider,
                            ApiKey = apiKey,
                            Model = selection.Value.Model,
                            Endpoint = selection.Value.Endpoint,
                        }, stoppingToken);
                        _logger.LogInformation(
                            "Saved AI Configuration applied to the AI service: provider={Provider} effective={Effective} model={Model}",
                            result.Provider, result.EffectiveProvider, result.Model);
                        applied = true;
                        warnedPending = false;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                // Expected while the AI service is still starting or briefly rate limiting.
                if (applied) { pendingSince = DateTime.UtcNow; warnedPending = false; }
                applied = false;
                _logger.LogDebug(ex, "AI service not ready for configuration sync; will retry.");
                if (!warnedPending && DateTime.UtcNow - pendingSince > TimeSpan.FromSeconds(60))
                {
                    _logger.LogWarning("The saved AI Configuration has not been applied yet; still retrying every {Seconds}s.",
                        PendingRetryDelay.TotalSeconds);
                    warnedPending = true;
                }
            }

            try
            {
                await Task.Delay(applied ? AppliedCheckInterval : PendingRetryDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
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
