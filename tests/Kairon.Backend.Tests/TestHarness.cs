using Kairon.Backend.Configuration;
using Kairon.Backend.DTOs.Sre;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models;
using Kairon.Backend.Models.Sre;
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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Tests;

/// <summary>
/// Wires a real dependency graph over an in-memory SQLite database.
///
/// SQLite rather than the EF in-memory provider: the SRE layer relies on relational behaviour
/// (unique indexes on incident keys, cascade deletes, string-converted enums), and a provider that
/// silently ignores those would let a broken model configuration pass its own tests.
/// </summary>
public sealed class TestHarness : IDisposable
{
    private readonly SqliteConnection _connection;

    public AppDbContext Db { get; }
    public DetectionOptions Detection { get; }
    public RemediationOptions Remediation { get; }
    public VerificationOptions Verification { get; }
    public AiOrchestrationOptions AiOptions { get; }
    public FakeAiService Ai { get; }
    public FakeDemoEnvironment Demo { get; }
    public IAuditService Audit { get; }
    public IIncidentProcessingQueue Queue { get; }
    public IRemediationToolRegistry Tools { get; }
    public IRemediationPolicy Policy { get; }

    public TestHarness(Action<TestHarness>? configure = null)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        Db = new AppDbContext(options);
        Db.Database.EnsureCreated();

        Detection = new DetectionOptions
        {
            // Tightened for tests: the production defaults require sustained breaches over real
            // wall-clock time, which would make every detection test slow for no benefit.
            EvaluationWindowSeconds = 120,
            SustainedBreachSeconds = 5,
            MinimumSamples = 2,
            CooldownSeconds = 60,
            CorrelationWindowSeconds = 300
        };

        Remediation = new RemediationOptions();
        // No settle delay and no waiting for late telemetry: tests seed the telemetry they want
        // up front, so any wait here would be dead time rather than realism.
        Verification = new VerificationOptions
        {
            SettleSeconds = 0,
            WindowSeconds = 60,
            MaxWaitSeconds = 0,
            MinimumSamples = 1
        };
        // The evidence window is disabled by default here: most tests seed a complete incident
        // and investigate it immediately. InvestigationDelayTests opts back in.
        AiOptions = new AiOrchestrationOptions { InvestigationDelaySeconds = 0 };

        Ai = new FakeAiService();
        Demo = new FakeDemoEnvironment();

        Audit = new AuditService(Db, NullLogger<AuditService>.Instance);

        Tools = new RemediationToolRegistry(new IRemediationTool[]
        {
            new RestartDemoServiceTool(Demo, NullLogger<RestartDemoServiceTool>.Instance),
            new ClearDemoCacheTool(Demo, NullLogger<ClearDemoCacheTool>.Instance),
            new DisableDemoRetryLoopTool(Demo, NullLogger<DisableDemoRetryLoopTool>.Instance),
            new ReduceDemoWorkerConcurrencyTool(Demo, NullLogger<ReduceDemoWorkerConcurrencyTool>.Instance),
            new ResetDemoFailureSimulationTool(Demo, NullLogger<ResetDemoFailureSimulationTool>.Instance),
            new RunHealthCheckTool(Demo, NullLogger<RunHealthCheckTool>.Instance)
        });

        Policy = new RemediationPolicy(Tools, Opt(Remediation), NullLogger<RemediationPolicy>.Instance);

        Queue = new IncidentProcessingQueue(Opt(AiOptions), NullLogger<IncidentProcessingQueue>.Instance);

        configure?.Invoke(this);
    }

    public static IOptions<T> Opt<T>(T value) where T : class => Options.Create(value);

    public IReadOnlyList<IDetectionRule> AllRules() => new IDetectionRule[]
    {
        new CpuThresholdRule(),
        new MemoryThresholdRule(),
        new LatencyThresholdRule(),
        new ErrorRateRule(),
        new RetryStormRule(),
        new RequestBurstRule(),
        new MetricDeviationRule(),
        new RepeatedErrorsRule(),
        new QueueBacklogRule()
    };

    public DetectionEngine CreateDetectionEngine(IDetectionCooldownStore? cooldown = null) =>
        new(Db, AllRules(), cooldown ?? new InMemoryDetectionCooldownStore(),
            Opt(Detection), NullLogger<DetectionEngine>.Instance);

    public CorrelationEngine CreateCorrelationEngine()
    {
        EnsureProject();
        return new(Db, Audit, new IncidentKeyGenerator(Db), Opt(Detection), NullLogger<CorrelationEngine>.Instance);
    }

    public EvidenceCollector CreateEvidenceCollector() =>
        new(Db, Tools, Opt(AiOptions), Opt(Detection), NullLogger<EvidenceCollector>.Instance);

    public RemediationExecutor CreateExecutor() =>
        new(Tools, Policy, Audit, Db, Opt(Remediation), NullLogger<RemediationExecutor>.Instance);

    public Action? BeforeVerification { get; set; }

    public VerificationService CreateVerificationService() =>
        new(Db, Audit, new RemediationToolRegistryAccessor(Tools), Opt(Verification), Opt(Detection),
            NullLogger<VerificationService>.Instance);

    public IncidentOrchestrator CreateOrchestrator() =>
        new(Db, CreateDetectionEngine(), CreateCorrelationEngine(), CreateEvidenceCollector(), Ai,
            Policy, Tools, CreateExecutor(), new BeforeVerificationService(CreateVerificationService(), () => BeforeVerification?.Invoke()), Audit,
            new IncidentKeyGenerator(Db), Queue, Opt(AiOptions), Opt(Remediation),
            NullLogger<IncidentOrchestrator>.Instance);

    public IncidentQueryService CreateQueryService() =>
        new(Db, Tools, Policy, Ai, Opt(Detection), Opt(Remediation));

    // --- Seeding helpers ---

    public Guid ProjectId { get; } = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");
    public string Environment { get; } = "Development";
    public string Service { get; } = "OrderProcessingService";

    public Metric SeedMetric(
        DateTime timestamp,
        double? cpu = null,
        double? memory = null,
        double? latency = null,
        long requests = 0,
        long errors = 0,
        long? retries = null,
        long? queue = null)
    {
        EnsureProject();
        var metric = new Metric
        {
            Id = Guid.NewGuid(),
            ProjectId = ProjectId,
            Timestamp = timestamp,
            CpuPercent = cpu,
            MemoryPercent = memory,
            ResponseTimeMs = latency,
            RequestCount = requests,
            ErrorCount = errors,
            RetryCount = retries,
            QueueDepth = queue,
            Environment = Environment,
            Application = "Kairon.DemoApp",
            Service = Service,
            Component = Service
        };

        Db.Metrics.Add(metric);
        Db.SaveChanges();
        return metric;
    }

    public Incident SeedTelemetry(
        DateTime timestamp,
        string endpoint = "/api/orders/process",
        int statusCode = 503,
        long durationMs = 2400,
        string? errorType = "DownstreamTimeoutException",
        string? errorMessage = "Order processing retry exhausted")
    {
        EnsureProject();
        var row = new Incident
        {
            Id = Guid.NewGuid(),
            ProjectId = ProjectId,
            Timestamp = timestamp,
            Endpoint = endpoint,
            Method = "POST",
            StatusCode = statusCode,
            DurationMs = durationMs,
            ErrorType = errorType,
            ErrorMessage = errorMessage,
            Environment = Environment,
            Application = "Kairon.DemoApp",
            Service = Service
        };

        Db.Incidents.Add(row);
        Db.SaveChanges();
        return row;
    }

    /// <summary>Builds a detection context directly, for rule-level unit tests.</summary>
    public DetectionContext Context(
        DateTime now,
        IEnumerable<Metric>? metrics = null,
        IEnumerable<Incident>? telemetry = null,
        IEnumerable<AgentEvent>? agentEvents = null) => new()
    {
        Options = Detection,
        ProjectId = ProjectId,
        Application = "Kairon.DemoApp",
        Service = Service,
        Environment = Environment,
        Metrics = (metrics ?? Array.Empty<Metric>()).OrderBy(m => m.Timestamp).ToList(),
        Telemetry = (telemetry ?? Array.Empty<Incident>()).OrderBy(t => t.Timestamp).ToList(),
        AgentEvents = (agentEvents ?? Array.Empty<AgentEvent>()).OrderBy(e => e.Timestamp).ToList(),
        Now = now
    };

    public SreIncident SeedIncident(
        IncidentStatus status = IncidentStatus.Detected,
        IncidentSeverity severity = IncidentSeverity.High,
        string? correlationKey = null)
    {
        EnsureProject();
        var incident = new SreIncident
        {
            IncidentKey = $"INC-{Db.SreIncidents.Count() + 1:D4}",
            ProjectId = ProjectId,
            Timestamp = DateTime.UtcNow.AddMinutes(-2),
            Application = "Kairon.DemoApp",
            Service = Service,
            Environment = Environment,
            Severity = severity,
            Status = status,
            AffectedComponent = Service,
            AffectedEndpoint = "/api/orders/process",
            Title = $"{Service} Service Degradation",
            CorrelationKey = correlationKey ?? $"{ProjectId}|{Environment}|{Service}",
            SignalCount = 3,
            SymptomsJson = SreJson.Serialize(new List<string> { "Retry rate 90/min", "CPU 94%" }),
            CorrelatedMetricsJson = SreJson.Serialize(new List<CorrelatedSignalSnapshot>
            {
                new() { Rule = "retry-storm", MetricName = "retries", Symptom = "Retry rate 90/min",
                        Observed = 90, Threshold = 30, Unit = "/min", Severity = "High", DetectedAt = DateTime.UtcNow },
                new() { Rule = "cpu-threshold", MetricName = "cpu", Symptom = "CPU 94%",
                        Observed = 94, Threshold = 80, Unit = "%", Severity = "High", DetectedAt = DateTime.UtcNow }
            })
        };

        Db.SreIncidents.Add(incident);
        Db.SaveChanges();
        return incident;
    }

    public void EnsureProject()
    {
        if (Db.Projects.Local.Any(p => p.Id == ProjectId) || Db.Projects.Any(p => p.Id == ProjectId))
            return;

        Db.Projects.Add(new Kairon.Backend.Models.Platform.Project
        {
            Id = ProjectId,
            Name = "Order API",
            Slug = "order-api",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        Db.SaveChanges();
    }

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}

/// <summary>
/// Scriptable AI client. Every AI behaviour the PRD requires tests for - success, malformed
/// response, timeout, cancellation, unavailable service - is expressed by setting a field here.
/// </summary>
public class FakeAiService : IAiMicroservice
{
    public InvestigationResultDto? NextResult { get; set; }
    public Exception? NextException { get; set; }
    public int InvestigateCalls { get; private set; }
    public EvidencePackageDto? LastEvidence { get; private set; }
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public bool IsAvailable { get; set; } = true;
    public Task<string> GetModeAsync(CancellationToken cancellationToken = default) => Task.FromResult("fake");

    public Task<InvestigationResultDto> InvestigateAsync(
        EvidencePackageDto evidence, CancellationToken cancellationToken = default)
        => InvestigateCore(evidence, cancellationToken);

    private async Task<InvestigationResultDto> InvestigateCore(
        EvidencePackageDto evidence, CancellationToken cancellationToken)
    {
        InvestigateCalls++;
        LastEvidence = evidence;

        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (NextException is not null)
            throw NextException;

        return NextResult ?? DefaultResult();
    }

    public static InvestigationResultDto DefaultResult() => new()
    {
        Summary = "OrderProcessingService is degraded",
        RootCause = "Controlled retry loop causing repeated downstream requests.",
        ContributingFactors = new List<string> { "Retry rate 90/min", "CPU 94%" },
        Evidence = new List<string> { "retries 90/min vs threshold 30/min" },
        Confidence = 0.92,
        Severity = "high",
        AffectedComponents = new List<string> { "OrderProcessingService" },
        PredictedFailure = "Request backlog will continue growing.",
        EstimatedRisk = "high",
        Recommendations = new List<AiRecommendationDto>
        {
            new()
            {
                Action = DemoToolNames.DisableDemoRetryLoop,
                Reason = "Retry volume is the leading signal.",
                ExpectedOutcome = "Retry count returns to baseline.",
                RiskLevel = "low"
            }
        },
        Provider = "fake",
        Model = "fake-model"
    };

    // Legacy surface, unused by the SRE tests but required by the interface.
    public Task<Kairon.Backend.DTOs.ErrorAnalysisResponse> AnalyzeErrorAsync(string log, CancellationToken ct = default)
        => Task.FromResult(new Kairon.Backend.DTOs.ErrorAnalysisResponse { RootCause = "fake" });

    public Task<Kairon.Backend.DTOs.PredictionResponse> PredictAsync(List<string> recentLogs, string currentLog, CancellationToken ct = default)
        => Task.FromResult(new Kairon.Backend.DTOs.PredictionResponse());

    public Task<Kairon.Backend.DTOs.RecommendationResponse> RecommendAsync(string context, CancellationToken ct = default)
        => Task.FromResult(new Kairon.Backend.DTOs.RecommendationResponse());

    public Task<List<Kairon.Backend.DTOs.FixSuggestionDto>> SuggestFixesAsync(
        List<Kairon.Backend.DTOs.MismatchDto> mismatches, CancellationToken ct = default)
        => Task.FromResult(new List<Kairon.Backend.DTOs.FixSuggestionDto>());

    // AI Configuration panel surface - unused by the SRE investigation tests but required by the
    // interface. Real behaviour is covered by AiConfigControllerTests / AiProviderConfigServiceTests.
    public Task<Kairon.Backend.DTOs.Sre.AiConfigureResponseDto> ConfigureProviderAsync(
        Kairon.Backend.DTOs.Sre.AiConfigureRequestDto request, CancellationToken cancellationToken = default)
        => Task.FromResult(new Kairon.Backend.DTOs.Sre.AiConfigureResponseDto
        {
            Applied = true, Provider = request.Provider, EffectiveProvider = request.Provider, Model = request.Model ?? ""
        });

    public Task<Kairon.Backend.DTOs.Sre.AiTestConnectionResponseDto> TestProviderConnectionAsync(
        Kairon.Backend.DTOs.Sre.AiConfigureRequestDto request, CancellationToken cancellationToken = default)
        => Task.FromResult(new Kairon.Backend.DTOs.Sre.AiTestConnectionResponseDto
        {
            Success = true, Provider = request.Provider, EffectiveProvider = request.Provider, Model = request.Model ?? ""
        });

    public Task<Kairon.Backend.DTOs.Sre.AiModelsResponseDto> ListProviderModelsAsync(
        Kairon.Backend.DTOs.Sre.AiConfigureRequestDto request, CancellationToken cancellationToken = default)
        => Task.FromResult(new Kairon.Backend.DTOs.Sre.AiModelsResponseDto { Provider = request.Provider, Supported = false });
}

/// <summary>Records the commands remediation tools issue, so tests can assert on the effect.</summary>
public class FakeDemoEnvironment : IDemoEnvironmentClient
{
    public List<string> Commands { get; } = new();
    public bool ShouldFail { get; set; }
    public bool ShouldThrow { get; set; }
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public DemoStateDto State { get; set; } = new()
    {
        Phase = "Degraded",
        RetryLoopEnabled = true,
        WorkerConcurrency = 16,
        Service = "OrderProcessingService",
        Environment = "Development"
    };

    public async Task<DemoCommandResult> SendAsync(string command, CancellationToken cancellationToken = default)
    {
        Commands.Add(command);

        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, cancellationToken);

        if (ShouldThrow)
            throw new InvalidOperationException("demo environment exploded");

        if (ShouldFail)
            return DemoCommandResult.Fail("demo environment refused the command");

        if (command == DemoCommands.DisableRetryLoop)
            State.RetryLoopEnabled = false;

        return DemoCommandResult.Ok($"executed {command}", State);
    }

    public Task<DemoStateDto> GetStateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(State);
}

internal sealed class BeforeVerificationService(IVerificationService inner, Action callback) : IVerificationService
{
    public Task<VerificationResult> VerifyAsync(SreIncident incident, RemediationAction action, CancellationToken cancellationToken = default)
    {
        callback();
        return inner.VerifyAsync(incident, action, cancellationToken);
    }
}
