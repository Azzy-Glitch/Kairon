namespace Kairon.SDK;

/// <summary>
/// Resolves one coherent KAIRON connection for both the standalone client and ASP.NET Core.
/// Endpoint, project ID and API key always come from the same source; they are never merged
/// field-by-field across a stored credential and ordinary configuration.
/// </summary>
internal static class KaironConfigurationResolver
{
    internal const string DefaultEndpoint = "http://localhost:8000";

    /// <summary>How long a redemption waits for another thread/process redeeming into the same
    /// credential path. Covers the pairing call's own 10-second timeout plus the save.</summary>
    private static readonly TimeSpan DefaultPairingLockWait = TimeSpan.FromSeconds(15);

    /// <summary>When redemption could not be serialized and the code was rejected, how long to look
    /// for another process having redeemed the same code into the store.</summary>
    private static readonly TimeSpan DefaultPairingRaceWindow = TimeSpan.FromSeconds(5);

    internal static async Task<KaironOptions> ResolveAsync(
        string? pairingCode,
        string? endpoint,
        Guid? projectId,
        string? apiKey,
        string? configPath,
        CancellationToken cancellationToken,
        TimeSpan? pairingLockWait = null,
        TimeSpan? pairingRaceWindow = null,
        string? applicationName = null)
    {
        var path = ResolveCredentialPath(configPath, applicationName);
        var legacyPaths = LegacyPaths(configPath);

        // An explicit pairing code is an instruction to pair now. It wins over both an old stored
        // connection and ordinary configuration, exactly as it did in KaironClient before this
        // resolver was shared with the ASP.NET Core integration - unless the stored credential was
        // issued for this very code, in which case the code was simply left in configuration (or
        // several processes started with it) and redeeming it again could only fail.
        if (!string.IsNullOrWhiteSpace(pairingCode))
        {
            var codeHash = KaironCredentialStore.HashPairingCode(pairingCode);
            KaironPairingResult paired;
            var redeemLock = await KaironCredentialStore
                .AcquirePairingLockAsync(path, pairingLockWait ?? DefaultPairingLockWait, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (KaironCredentialStore.LoadOrMigrate(path, legacyPaths) is { } existing &&
                    KaironCredentialStore.IssuedFor(existing, codeHash))
                    return await UseStoredAsync(path, existing, cancellationToken).ConfigureAwait(false);

                var pairingEndpoint = endpoint ?? Environment.GetEnvironmentVariable("KAIRON_ENDPOINT") ?? DefaultEndpoint;
                paired = await KaironPairingClient.PairAsync(pairingEndpoint, pairingCode, cancellationToken)
                    .ConfigureAwait(false);
                if (!paired.Success)
                {
                    // Holding the lock, any winner has already saved; without it, a concurrent
                    // process may still be between its redemption and its save, so look briefly.
                    // A 429 or unreachable backend never consumed the code - nothing to wait for.
                    if (paired.StatusCode == 400 &&
                        await WaitForCredentialIssuedForAsync(path, codeHash,
                                redeemLock is null ? pairingRaceWindow ?? DefaultPairingRaceWindow : TimeSpan.Zero,
                                cancellationToken)
                            .ConfigureAwait(false) is { } winner)
                        return await UseStoredAsync(path, winner, cancellationToken).ConfigureAwait(false);
                    throw PairingFailure(paired);
                }

                // Persist before reporting success. The pending marker makes a lost confirmation
                // recoverable on a later asynchronous resolution without ever redeeming the code twice.
                KaironCredentialStore.Save(path, new StoredKaironCredential(paired.Endpoint!, paired.ProjectId,
                    paired.ApiKey!, paired.PairingId, paired.Environment, paired.Service, codeHash));
            }
            finally
            {
                redeemLock?.Dispose();
            }

            if (await KaironPairingClient
                    .ConfirmAsync(paired.Endpoint!, paired.PairingId, paired.ApiKey!, cancellationToken)
                    .ConfigureAwait(false))
            {
                KaironCredentialStore.Save(path, new StoredKaironCredential(paired.Endpoint!, paired.ProjectId,
                    paired.ApiKey!, null, paired.Environment, paired.Service, codeHash));
            }

            KaironEndpointSecurity.EnsureAllowed(paired.Endpoint);
            return Connection(paired.Endpoint!, paired.ProjectId, paired.ApiKey!, paired.Environment, paired.Service);
        }

        if (KaironCredentialStore.LoadOrMigrate(path, legacyPaths) is { } credential)
            return await UseStoredAsync(path, credential, cancellationToken).ConfigureAwait(false);

        return ResolveOrdinaryConfiguration(endpoint, projectId, apiKey);
    }

    private static async Task<KaironOptions> UseStoredAsync(string path, StoredKaironCredential credential,
        CancellationToken cancellationToken)
    {
        KaironEndpointSecurity.EnsureAllowed(credential.Endpoint);

        if (credential.PendingConfirmationPairingId is { } pendingId &&
            await KaironPairingClient
                .ConfirmAsync(credential.Endpoint, pendingId, credential.ApiKey, cancellationToken)
                .ConfigureAwait(false))
        {
            KaironCredentialStore.Save(path, credential with { PendingConfirmationPairingId = null });
        }

        return Connection(credential.Endpoint, credential.ProjectId, credential.ApiKey,
            credential.Environment, credential.Service);
    }

    private static async Task<StoredKaironCredential?> WaitForCredentialIssuedForAsync(string path, string codeHash,
        TimeSpan window, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + window;
        while (true)
        {
            if (KaironCredentialStore.Load(path) is { } stored && KaironCredentialStore.IssuedFor(stored, codeHash))
                return stored;
            if (DateTime.UtcNow >= deadline) return null;
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Synchronous ASP.NET Core registration deliberately performs no network I/O. A complete
    /// explicit configuration keeps the historical AddKairon behavior. When identity is omitted,
    /// a complete stored pairing credential is reused as one unit. A credential awaiting pairing
    /// confirmation must go through AddKaironAsync so confirmation is not silently bypassed.
    /// </summary>
    internal static KaironOptions ResolveForDependencyInjection(KaironOptions configured)
    {
        ArgumentNullException.ThrowIfNull(configured);

        var hasProject = configured.ProjectId != Guid.Empty;
        var hasApiKey = !string.IsNullOrWhiteSpace(configured.ApiKey);

        if (hasProject != hasApiKey)
            throw new InvalidOperationException(
                "Kairon ASP.NET Core configuration is incomplete. Provide both ProjectId and ApiKey, " +
                "or omit both so AddKairon can reuse a stored pairing credential.");

        if (hasProject)
        {
            KaironEndpointSecurity.EnsureAllowed(configured.Endpoint);
            return configured;
        }

        var path = ResolveCredentialPath(configured.CredentialPath, configured.ServiceName ?? configured.ApplicationName);
        var stored = KaironCredentialStore.LoadOrMigrate(path, LegacyPaths(configured.CredentialPath));
        if (stored is { } credential)
        {
            if (credential.PendingConfirmationPairingId is not null)
                throw new InvalidOperationException(
                    "The stored Kairon credential is awaiting pairing confirmation. Register with " +
                    "AddKaironAsync once so the SDK can recover confirmation safely.");

            KaironEndpointSecurity.EnsureAllowed(credential.Endpoint);
            ApplyConnection(configured, Connection(credential.Endpoint, credential.ProjectId, credential.ApiKey,
                credential.Environment, credential.Service));
            return configured;
        }

        throw new InvalidOperationException(
            "Kairon needs a project and API key. Provide both in AddKairon, pass a pairing code " +
            "to AddKaironAsync on the first run, or pair once so AddKairon can reuse the stored " +
            "credential on later runs.");
    }

    internal static void ApplyConnection(KaironOptions target, KaironOptions connection)
    {
        target.Endpoint = connection.Endpoint;
        target.ProjectId = connection.ProjectId;
        target.ApiKey = connection.ApiKey;
        // Pairing defaults belong to the connection they came with; explicit identity settings on
        // the target are untouched and still win in KaironIdentity.
        target.PairedEnvironment = connection.PairedEnvironment;
        target.PairedService = connection.PairedService;
    }

    private static KaironOptions ResolveOrdinaryConfiguration(string? endpoint, Guid? projectId, string? apiKey)
    {
        var resolvedProjectId = projectId;
        if (resolvedProjectId is null || resolvedProjectId == Guid.Empty)
        {
            var envProjectId = Environment.GetEnvironmentVariable("KAIRON_PROJECT_ID");
            if (!string.IsNullOrWhiteSpace(envProjectId) && Guid.TryParse(envProjectId, out var parsed))
                resolvedProjectId = parsed;
        }

        apiKey ??= Environment.GetEnvironmentVariable("KAIRON_API_KEY");

        if (resolvedProjectId is null || resolvedProjectId == Guid.Empty || string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Kairon needs a project and API key. Provide ProjectId/ApiKey, set " +
                "KAIRON_PROJECT_ID/KAIRON_API_KEY, pass a pairing code to AddKaironAsync or " +
                "KaironClient, or pair once so the stored credential can be reused.");

        var resolvedEndpoint = endpoint ?? Environment.GetEnvironmentVariable("KAIRON_ENDPOINT") ?? DefaultEndpoint;

        KaironEndpointSecurity.EnsureAllowed(resolvedEndpoint);
        return Connection(resolvedEndpoint, resolvedProjectId.Value, apiKey);
    }

    private static KaironOptions Connection(string endpoint, Guid projectId, string apiKey,
        string? pairedEnvironment = null, string? pairedService = null) =>
        new()
        {
            Endpoint = endpoint, ProjectId = projectId, ApiKey = apiKey,
            PairedEnvironment = pairedEnvironment, PairedService = pairedService
        };

    private static string ResolveCredentialPath(string? configPath, string? applicationName) =>
        configPath ?? KaironCredentialStore.DefaultPath(KaironCredentialStore.ApplicationKey(applicationName));

    /// <summary>Files adopted once when this application has no file of its own yet. An explicit
    /// CredentialPath is authoritative and never migrates.</summary>
    private static string?[] LegacyPaths(string? configPath) => configPath is null
        ? [KaironCredentialStore.DefaultPath(), KaironCredentialStore.LegacyDefaultPath()]
        : [];

    private static InvalidOperationException PairingFailure(KaironPairingResult paired) =>
        new(
            $"Kairon pairing failed: {paired.Error ?? "the pairing code was rejected."} " +
            (paired.RateLimited
                ? "Try again shortly with the same pairing code."
                : "Generate a new pairing code from Kairon and try again."));
}
