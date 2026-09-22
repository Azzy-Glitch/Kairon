namespace Kairon.SDK;

/// <summary>
/// Resolves one coherent KAIRON connection for both the standalone client and ASP.NET Core.
/// Endpoint, project ID and API key always come from the same source; they are never merged
/// field-by-field across a stored credential and ordinary configuration.
/// </summary>
internal static class KaironConfigurationResolver
{
    internal const string DefaultEndpoint = "http://localhost:8000";

    internal static async Task<KaironOptions> ResolveAsync(
        string? pairingCode,
        string? endpoint,
        Guid? projectId,
        string? apiKey,
        string? configPath,
        CancellationToken cancellationToken)
    {
        var path = ResolveCredentialPath(configPath);

        // An explicit pairing code is an instruction to pair now. It wins over both an old stored
        // connection and ordinary configuration, exactly as it did in KaironClient before this
        // resolver was shared with the ASP.NET Core integration.
        if (!string.IsNullOrWhiteSpace(pairingCode))
        {
            var pairingEndpoint = endpoint ?? Environment.GetEnvironmentVariable("KAIRON_ENDPOINT") ?? DefaultEndpoint;
            var paired = await KaironPairingClient.PairAsync(pairingEndpoint, pairingCode, cancellationToken)
                .ConfigureAwait(false);
            if (!paired.Success)
                throw PairingFailure(paired.Error);

            // Persist before reporting success. The pending marker makes a lost confirmation
            // recoverable on a later asynchronous resolution without ever redeeming the code twice.
            KaironCredentialStore.Save(path, paired.Endpoint!, paired.ProjectId, paired.ApiKey!, paired.PairingId);

            if (await KaironPairingClient
                    .ConfirmAsync(paired.Endpoint!, paired.PairingId, paired.ApiKey!, cancellationToken)
                    .ConfigureAwait(false))
            {
                KaironCredentialStore.Save(path, paired.Endpoint!, paired.ProjectId, paired.ApiKey!);
            }

            KaironEndpointSecurity.EnsureAllowed(paired.Endpoint);
            return Connection(paired.Endpoint!, paired.ProjectId, paired.ApiKey!);
        }

        var stored = KaironCredentialStore.Load(path);
        if (stored is { } credential)
        {
            KaironEndpointSecurity.EnsureAllowed(credential.Endpoint);

            if (credential.PendingConfirmationPairingId is { } pendingId &&
                await KaironPairingClient
                    .ConfirmAsync(credential.Endpoint, pendingId, credential.ApiKey, cancellationToken)
                    .ConfigureAwait(false))
            {
                KaironCredentialStore.Save(path, credential.Endpoint, credential.ProjectId, credential.ApiKey);
            }

            return Connection(credential.Endpoint, credential.ProjectId, credential.ApiKey);
        }

        return ResolveOrdinaryConfiguration(endpoint, projectId, apiKey);
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

        var path = ResolveCredentialPath(configured.CredentialPath);
        var stored = KaironCredentialStore.Load(path);
        if (stored is { } credential)
        {
            if (credential.PendingConfirmationPairingId is not null)
                throw new InvalidOperationException(
                    "The stored Kairon credential is awaiting pairing confirmation. Register with " +
                    "AddKaironAsync once so the SDK can recover confirmation safely.");

            KaironEndpointSecurity.EnsureAllowed(credential.Endpoint);
            ApplyConnection(configured, Connection(credential.Endpoint, credential.ProjectId, credential.ApiKey));
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

    private static KaironOptions Connection(string endpoint, Guid projectId, string apiKey) =>
        new() { Endpoint = endpoint, ProjectId = projectId, ApiKey = apiKey };

    private static string ResolveCredentialPath(string? configPath) =>
        configPath ?? KaironCredentialStore.DefaultPath();

    private static InvalidOperationException PairingFailure(string? error) =>
        new(
            $"Kairon pairing failed: {error ?? "the pairing code was rejected."} " +
            "Generate a new pairing code from Kairon and try again.");
}
