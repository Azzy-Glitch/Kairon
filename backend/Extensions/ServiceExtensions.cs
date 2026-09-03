using Kairon.Backend.Configuration;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Services;
using Kairon.Backend.Services.Audit;
using Kairon.Backend.Services.Correlation;
using Kairon.Backend.Services.Demo;
using Kairon.Backend.Services.Detection;
using Kairon.Backend.Services.Evidence;
using Kairon.Backend.Services.Orchestration;
using Kairon.Backend.Services.Remediation;
using Kairon.Backend.Services.Remediation.Tools;
using Kairon.Backend.Services.Verification;

namespace Kairon.Backend.Extensions;

public static class ServiceExtensions
{
    public static IServiceCollection AddAiServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient<IAiMicroservice, AiMicroservice>(client =>
        {
            client.BaseAddress = new Uri(configuration["AiService:BaseUrl"] ?? "http://localhost:8000");
            client.Timeout = TimeSpan.FromSeconds(configuration.GetValue<int>("AiService:TimeoutSeconds", 30));
            // The Python service bounds provider output, but the transport boundary must not
            // assume its peer is healthy or authentic merely because it is on loopback. Keep the
            // complete JSON envelope bounded before ReadAsStringAsync buffers it in memory.
            client.MaxResponseContentBufferSize = configuration.GetValue<long>(
                "AiService:MaxResponseBytes", 128 * 1024);

            var apiKey = configuration["AiService:ApiKey"];
            if (!string.IsNullOrWhiteSpace(apiKey))
                client.DefaultRequestHeaders.Add("X-Kairon-AI-Key", apiKey);
        });

        // AI Configuration panel (frontend): user-supplied provider/model/key, persisted and
        // encrypted server-side, applied live via AiMicroservice - no .env/appsettings.json edit,
        // no manual restart. AiConfigSyncHostedService re-applies a saved configuration to the AI
        // service on every startup, since that process rebuilds its own config from scratch each
        // launch and has no other way to learn what was saved.
        services.AddScoped<IAiProviderConfigService, AiProviderConfigService>();
        services.AddHostedService<AiConfigSyncHostedService>();

        return services;
    }

    /// <summary>
    /// Registers Projects, per-project credentials, SDK pairing, and platform-level audit -
    /// ported from Azzy's productization branch and adapted onto this codebase's model
    /// (docs/DESKTOP_SHELL.md).
    /// </summary>
    public static IServiceCollection AddPlatformServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PlatformSecurityOptions>(configuration.GetSection(PlatformSecurityOptions.SectionName));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IPlatformAuditService, PlatformAuditService>();
        services.AddScoped<IProjectCredentialService, ProjectCredentialService>();
        services.AddScoped<ISdkPairingService, SdkPairingService>();
        services.AddScoped<IAgentRegistrationService, AgentRegistrationService>();

        // Normalized telemetry pipeline + SDK installation identity (docs/DESKTOP_SHELL.md) -
        // additive alongside the services above, not a replacement for any of them.
        services.AddScoped<IPlatformTelemetryService, PlatformTelemetryService>();
        services.AddScoped<ISdkInstallationService, SdkInstallationService>();

        return services;
    }

    /// <summary>
    /// SQLite backup/retention/maintenance (docs/DESKTOP_SHELL.md) - adapted from Azzy's
    /// productization branch. Registered unconditionally; SqliteBackupService itself is a no-op
    /// under the SqlServer provider (an external database's own backup story applies instead).
    /// </summary>
    public static IServiceCollection AddPersistenceMaintenance(this IServiceCollection services)
    {
        services.AddSingleton<PersistenceMaintenanceState>();
        services.AddScoped<ISqliteBackupService, SqliteBackupService>();
        services.AddScoped<ILocalSchemaMigrator, SqliteSchemaMigrator>();
        services.AddHostedService<PersistenceMaintenanceService>();

        return services;
    }

    /// <summary>
    /// Registers the Autonomous AI SRE control plane: detection, correlation, evidence, AI
    /// orchestration, remediation, verification and audit (PRD section 4.2).
    /// </summary>
    public static IServiceCollection AddAutonomousSre(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DetectionOptions>(configuration.GetSection(DetectionOptions.SectionName));
        services.Configure<RemediationOptions>(configuration.GetSection(RemediationOptions.SectionName));
        services.Configure<VerificationOptions>(configuration.GetSection(VerificationOptions.SectionName));
        services.Configure<AiOrchestrationOptions>(configuration.GetSection(AiOrchestrationOptions.SectionName));
        services.Configure<SreSecurityOptions>(configuration.GetSection(SreSecurityOptions.SectionName));
        services.Configure<DemoEnvironmentOptions>(configuration.GetSection(DemoEnvironmentOptions.SectionName));

        // --- Detection. Every rule is registered explicitly; adding a rule is one line here and
        // one class, and nothing else in the pipeline changes.
        services.AddSingleton<IDetectionCooldownStore, InMemoryDetectionCooldownStore>();
        services.AddScoped<IDetectionRule, CpuThresholdRule>();
        services.AddScoped<IDetectionRule, MemoryThresholdRule>();
        services.AddScoped<IDetectionRule, LatencyThresholdRule>();
        services.AddScoped<IDetectionRule, ErrorRateRule>();
        services.AddScoped<IDetectionRule, RetryStormRule>();
        services.AddScoped<IDetectionRule, RequestBurstRule>();
        services.AddScoped<IDetectionRule, MetricDeviationRule>();
        services.AddScoped<IDetectionRule, RepeatedErrorsRule>();
        services.AddScoped<IDetectionRule, QueueBacklogRule>();
        services.AddScoped<IDetectionRule, LogPatternMatchRule>();
        services.AddScoped<IDetectionRule, ProcessCrashRule>();
        services.AddScoped<IDetectionRule, ProcessHighResourceRule>();
        services.AddScoped<IDetectionEngine, DetectionEngine>();

        services.AddScoped<ICorrelationEngine, CorrelationEngine>();
        services.AddScoped<IEvidenceCollector, EvidenceCollector>();

        // --- Audit.
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IIncidentKeyGenerator, IncidentKeyGenerator>();

        // --- Remediation. The registry is built from the registered tools, which is what makes
        // "only a registered tool can execute" structurally true rather than a convention.
        services.AddScoped<IRemediationTool, RestartDemoServiceTool>();
        services.AddScoped<IRemediationTool, ClearDemoCacheTool>();
        services.AddScoped<IRemediationTool, DisableDemoRetryLoopTool>();
        services.AddScoped<IRemediationTool, ReduceDemoWorkerConcurrencyTool>();
        services.AddScoped<IRemediationTool, ResetDemoFailureSimulationTool>();
        services.AddScoped<IRemediationTool, RunHealthCheckTool>();
        services.AddScoped<IRemediationToolRegistry, RemediationToolRegistry>();
        services.AddScoped<IRemediationToolRegistryAccessor, RemediationToolRegistryAccessor>();
        services.AddScoped<IRemediationPolicy, RemediationPolicy>();
        services.AddScoped<IRemediationExecutor, RemediationExecutor>();

        services.AddScoped<IVerificationService, VerificationService>();

        // --- Orchestration.
        services.AddSingleton<IIncidentProcessingQueue, IncidentProcessingQueue>();
        services.AddScoped<IIncidentOrchestrator, IncidentOrchestrator>();
        services.AddScoped<IIncidentQueryService, IncidentQueryService>();

        // --- Demo environment. The simulator is a singleton because it holds the scenario state.
        services.AddSingleton<ILocalDemoSimulator, LocalDemoSimulator>();
        services.AddHttpClient<IDemoEnvironmentClient, DemoEnvironmentClient>((provider, client) =>
        {
            var options = configuration.GetSection(DemoEnvironmentOptions.SectionName).Get<DemoEnvironmentOptions>()
                          ?? new DemoEnvironmentOptions();

            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        // --- Background workers. This is what keeps AI off the ingestion path.
        services.AddHostedService<IncidentProcessingWorker>();
        services.AddHostedService<DetectionSweepWorker>();
        services.AddHostedService<DemoSimulationWorker>();

        return services;
    }
}
