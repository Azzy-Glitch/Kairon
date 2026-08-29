namespace Kairon.Backend.Configuration;

/// <summary>
/// Gates whether telemetry ingestion (TelemetryController) requires a per-project credential.
/// Off by default, matching this codebase's existing SreSecurity:RequireOperatorKey convention -
/// a project with no issued credential keeps working unauthenticated, so the existing demo
/// scenario and every current test keep passing unmodified. Turning it on is a config change,
/// not a code change - the enforcement path is real, not a stub.
/// </summary>
public sealed class PlatformSecurityOptions
{
    public const string SectionName = "PlatformSecurity";
    public bool RequireTelemetryKey { get; set; } = false;

    /// <summary>Matches the header the .NET SDK and Agent already send today
    /// (Kairon.SDK/KaironTelemetryClient.cs, Kairon.Agent/AgentEventClient.cs) - previously sent
    /// but never read server-side.</summary>
    public string TelemetryKeyHeader { get; set; } = "X-Kairon-API-Key";
}
