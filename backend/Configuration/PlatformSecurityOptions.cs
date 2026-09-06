namespace Kairon.Backend.Configuration;

/// <summary>
/// Gates whether telemetry ingestion (TelemetryController) requires a per-project credential.
/// Enabled by default. Machine-scoped evidence additionally requires the dedicated credential
/// assigned by the operator to that exact target, regardless of this compatibility switch.
/// </summary>
public sealed class PlatformSecurityOptions
{
    public const string SectionName = "PlatformSecurity";
    public bool RequireTelemetryKey { get; set; } = true;

    /// <summary>Matches the header the .NET SDK and Agent already send today
    /// (Kairon.SDK/KaironTelemetryClient.cs, Kairon.Agent/AgentEventClient.cs) - previously sent
    /// but never read server-side.</summary>
    public string TelemetryKeyHeader { get; set; } = "X-Kairon-API-Key";
}
