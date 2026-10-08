using System.Text.Json;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models;
using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services;
using Kairon.Backend.Services.Audit;
using Kairon.Backend.Services.Correlation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// One active incident per logical problem, deterministic server-side dedup, and AI leakage/cost
/// protection: repeated or concurrent telemetry never multiplies incidents or AI requests, and the
/// evidence sent to the model is redacted and confined to the incident's own scope.
/// </summary>
public sealed class IncidentDedupAndAiProtectionTests : IDisposable
{
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private DetectionSignal Signal(string ruleId, string metric, string? service = null, Guid? projectId = null,
        Guid? machineId = null, string? environment = null, params Guid[] telemetry) => new()
    {
        RuleId = ruleId,
        MetricName = metric,
        ProjectId = projectId ?? _h.ProjectId,
        MachineId = machineId,
        Application = "Kairon.DemoApp",
        Service = service ?? _h.Service,
        Component = service ?? _h.Service,
        Environment = environment ?? _h.Environment,
        Severity = IncidentSeverity.High,
        Symptom = $"{metric} breached",
        Observed = 100,
        Threshold = 50,
        Unit = "%",
        DetectedAt = DateTime.UtcNow,
        TelemetryReferences = telemetry.ToList()
    };

    private int AiReservations(Guid? incidentId = null) => _h.Db.IncidentEvents.Count(e =>
        e.EventType == IncidentEventTypes.AiRequestStarted && (!incidentId.HasValue || e.IncidentId == incidentId));

    [Fact]
    public async Task RepeatedIdenticalErrorsCreateOneActiveIncident()
    {
        var engine = _h.CreateCorrelationEngine();
        for (var i = 0; i < 10; i++)
            await engine.CorrelateAsync(new List<DetectionSignal> { Signal("repeated-errors", "errors") });

        var incident = Assert.Single(await _h.Db.SreIncidents.ToListAsync());
        Assert.Equal(IncidentStatus.Detected, incident.Status);
    }

    [Fact]
    public async Task RepeatedLatencyViolationsCreateOneActiveIncident()
    {
        var engine = _h.CreateCorrelationEngine();
        for (var i = 0; i < 10; i++)
            await engine.CorrelateAsync(new List<DetectionSignal> { Signal("latency-threshold", "latency") });

        Assert.Equal(1, await _h.Db.SreIncidents.CountAsync());
    }

    [Fact]
    public async Task AnIdleActiveIncidentStillAbsorbsTheSameProblemInsteadOfDuplicatingIt()
    {
        var engine = _h.CreateCorrelationEngine();
        var created = await engine.CorrelateAsync(new List<DetectionSignal> { Signal("repeated-errors", "errors") });
        created[0].Status = IncidentStatus.AwaitingApproval;
        created[0].UpdatedAt = DateTime.UtcNow.AddHours(-3); // long past any correlation window
        await _h.Db.SaveChangesAsync();

        await engine.CorrelateAsync(new List<DetectionSignal> { Signal("repeated-errors", "errors") });

        Assert.Equal(1, await _h.Db.SreIncidents.CountAsync());
    }

    [Fact]
    public async Task RepeatedTelemetryDoesNotReinvokeTheAi()
    {
        var orchestrator = _h.CreateOrchestrator();
        var engine = _h.CreateCorrelationEngine();
        var incident = (await engine.CorrelateAsync(new List<DetectionSignal> { Signal("repeated-errors", "errors") }))[0];
        await orchestrator.InvestigateAsync(incident.Id);
        Assert.Equal(1, _h.Ai.InvestigateCalls);

        for (var i = 0; i < 20; i++)
        {
            var touched = await engine.CorrelateAsync(new List<DetectionSignal> { Signal("repeated-errors", "errors") });
            // The worker only ever queues an investigation for a Detected incident; an absorbed
            // signal leaves the diagnosed incident where it is.
            Assert.All(touched, t => Assert.NotEqual(IncidentStatus.Detected, t.Status));
            // Even a stray worker run for the same incident is a no-op.
            await orchestrator.InvestigateAsync(incident.Id);
        }

        // Identical readings add nothing, so the diagnosis is not even marked stale.
        Assert.False((await _h.Db.SreIncidents.AsNoTracking().SingleAsync()).DiagnosisStale);

        // Genuinely new evidence (a new rule) is flagged for the operator, never re-sent on its own.
        await engine.CorrelateAsync(new List<DetectionSignal> { Signal("latency-threshold", "latency") });
        await orchestrator.InvestigateAsync(incident.Id);

        Assert.Equal(1, await _h.Db.SreIncidents.CountAsync());
        Assert.Equal(1, _h.Ai.InvestigateCalls);
        Assert.Equal(1, AiReservations(incident.Id));
        Assert.True((await _h.Db.SreIncidents.AsNoTracking().SingleAsync()).DiagnosisStale);
    }

    [Fact]
    public async Task AResolvedIncidentFollowedByANewFailureCreatesANewInvestigatedIncident()
    {
        var orchestrator = _h.CreateOrchestrator();
        var engine = _h.CreateCorrelationEngine();
        var first = (await engine.CorrelateAsync(new List<DetectionSignal> { Signal("repeated-errors", "errors") }))[0];
        await orchestrator.InvestigateAsync(first.Id);
        first.Status = IncidentStatus.Resolved;
        await _h.Db.SaveChangesAsync();

        var second = (await engine.CorrelateAsync(new List<DetectionSignal> { Signal("repeated-errors", "errors") }))[0];
        await orchestrator.InvestigateAsync(second.Id);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, await _h.Db.SreIncidents.CountAsync());
        Assert.Equal(2, _h.Ai.InvestigateCalls);
    }

    [Fact]
    public async Task DifferentProjectsMachinesServicesAndEnvironmentsAreNeverMerged()
    {
        var otherProject = Guid.NewGuid();
        _h.Db.Projects.Add(new Kairon.Backend.Models.Platform.Project
        {
            Id = otherProject, Name = "Billing", Slug = "billing", IsActive = true, CreatedAt = DateTime.UtcNow
        });
        await _h.Db.SaveChangesAsync();
        var machineA = _h.SeedMachine("host-a").Id;
        var machineB = _h.SeedMachine("host-b").Id;

        var engine = _h.CreateCorrelationEngine();
        await engine.CorrelateAsync(new List<DetectionSignal>
        {
            Signal("repeated-errors", "errors"),
            Signal("repeated-errors", "errors", projectId: otherProject),
            Signal("repeated-errors", "errors", service: "PaymentService"),
            Signal("repeated-errors", "errors", environment: "Production"),
            Signal("repeated-errors", "errors", machineId: machineA),
            Signal("repeated-errors", "errors", machineId: machineB)
        });
        // And again: each scope dedups only against itself.
        await engine.CorrelateAsync(new List<DetectionSignal>
        {
            Signal("repeated-errors", "errors", machineId: machineA),
            Signal("repeated-errors", "errors", projectId: otherProject)
        });

        var incidents = await _h.Db.SreIncidents.ToListAsync();
        Assert.Equal(6, incidents.Count);
        Assert.Equal(6, incidents.Select(i => i.CorrelationKey).Distinct().Count());
    }

    [Fact]
    public async Task AiEvidenceIsRedactedAndConfinedToTheIncidentsOwnScope()
    {
        var machineA = _h.SeedMachine("host-a").Id;
        var machineB = _h.SeedMachine("host-b").Id;
        var now = DateTime.UtcNow;

        Incident Row(string message, string? service = null, string? environment = null, Guid? machine = null,
            string endpoint = "/checkout", int status = 500, long duration = 2400, string? errorType = null)
        {
            var row = new Incident
            {
                Id = Guid.NewGuid(), ProjectId = _h.ProjectId, MachineId = machine ?? machineA, Timestamp = now,
                Endpoint = endpoint, Method = "POST", StatusCode = status, DurationMs = duration,
                ErrorType = errorType, ErrorMessage = message, Environment = environment ?? _h.Environment,
                Application = "Kairon.DemoApp", Service = service ?? _h.Service
            };
            _h.Db.Incidents.Add(row);
            return row;
        }

        var linked = Row("upstream failed access_token=AT_SECRET_111 for order");
        Row("Cookie: session=COOKIE_SECRET_222; theme=dark");
        Row("auth krn_LIVEKEY_333333333 rejected");
        Row("Authorization: Bearer eyJSECRETTOKEN444.payload");
        Row("retry with client_secret=CS_SECRET_555", endpoint: "/checkout?client_secret=QS_SECRET_666&x=1");
        Row("OTHER_MACHINE_MARKER", machine: machineB);
        Row("OTHER_SERVICE_MARKER", service: "PaymentService");
        Row("OTHER_ENV_MARKER", environment: "Production");
        Row(string.Empty, endpoint: "/health", status: 200, duration: 3);
        await _h.Db.SaveChangesAsync();

        var incident = (await _h.CreateCorrelationEngine().CorrelateAsync(new List<DetectionSignal>
        {
            Signal("repeated-errors", "errors", machineId: machineA, telemetry: linked.Id)
        }))[0];

        // History: another environment's and another machine's root causes must not leak in.
        var otherEnv = _h.SeedIncident(IncidentStatus.Resolved, correlationKey: "other-env");
        otherEnv.Environment = "Production";
        otherEnv.RootCause = "OTHER_ENV_HISTORY";
        var otherMachine = _h.SeedIncident(IncidentStatus.Resolved, correlationKey: "other-machine");
        otherMachine.RootCause = "OTHER_MACHINE_HISTORY";
        otherMachine.CorrelatedMetricsJson = SreJson.Serialize(new List<CorrelatedSignalSnapshot> { new() { MachineId = machineB, Rule = "r" } });
        var sameScope = _h.SeedIncident(IncidentStatus.Resolved, correlationKey: "same");
        sameScope.RootCause = "SAME_SCOPE_HISTORY";
        sameScope.CorrelatedMetricsJson = SreJson.Serialize(new List<CorrelatedSignalSnapshot> { new() { MachineId = machineA, Rule = "r" } });
        await _h.Db.SaveChangesAsync();

        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var json = JsonSerializer.Serialize(_h.Ai.LastEvidence);
        foreach (var secret in new[] { "AT_SECRET_111", "COOKIE_SECRET_222", "krn_LIVEKEY_333333333", "SECRETTOKEN444", "CS_SECRET_555", "QS_SECRET_666" })
            Assert.DoesNotContain(secret, json);
        foreach (var foreign in new[] { "OTHER_MACHINE_MARKER", "OTHER_SERVICE_MARKER", "OTHER_ENV_MARKER", "OTHER_ENV_HISTORY", "OTHER_MACHINE_HISTORY", "host-a", "host-b" })
            Assert.DoesNotContain(foreign, json);
        Assert.Contains("SAME_SCOPE_HISTORY", json);

        // The breakdown separates the failing endpoint (no application exception, slow) from a
        // healthy one, using only this scope's rows.
        var checkout = Assert.Single(_h.Ai.LastEvidence!.EndpointBreakdown, e => e.Endpoint == "/checkout");
        Assert.Equal(4, checkout.ServerErrors);
        Assert.Null(checkout.TopErrorType);
        Assert.Equal(2400, checkout.MaxDurationMs);
        Assert.Contains(_h.Ai.LastEvidence.EndpointBreakdown, e => e.Endpoint == "/health" && e.ServerErrors == 0);
        Assert.Null(_h.Ai.LastEvidence.RemediationTarget); // no target configured for this scope
    }

    [Fact]
    public async Task AiRateLimitsHoldUnderAnErrorStorm()
    {
        _h.AiOptions.MaxInvestigationsPerHour = 3;
        var orchestrator = _h.CreateOrchestrator();
        var engine = _h.CreateCorrelationEngine();

        // A storm on one service: hundreds of signals are still one incident and one AI request.
        for (var i = 0; i < 200; i++)
        {
            var touched = await engine.CorrelateAsync(new List<DetectionSignal>
            {
                Signal("repeated-errors", "errors"), Signal("latency-threshold", "latency"), Signal("cpu-threshold", "cpu")
            });
            foreach (var t in touched.Where(t => t.Status == IncidentStatus.Detected))
                await orchestrator.InvestigateAsync(t.Id);
        }
        Assert.Equal(1, await _h.Db.SreIncidents.CountAsync());
        Assert.Equal(1, _h.Ai.InvestigateCalls);

        // A storm across many services: the hourly budget caps the model requests.
        for (var s = 0; s < 10; s++)
        {
            var created = await engine.CorrelateAsync(new List<DetectionSignal> { Signal("repeated-errors", "errors", service: $"Svc{s}") });
            await orchestrator.InvestigateAsync(created[0].Id);
        }

        Assert.Equal(11, await _h.Db.SreIncidents.CountAsync());
        Assert.Equal(3, _h.Ai.InvestigateCalls);
        Assert.Equal(3, AiReservations());
        Assert.Equal(8, _h.Db.IncidentEvents.Count(e => e.EventType == IncidentEventTypes.AiBudgetExceeded));
    }

    [Fact]
    public async Task ConcurrentInvestigationsOfOneIncidentReserveOneAiRequest()
    {
        var incident = _h.SeedIncident();
        _h.Ai.Delay = TimeSpan.FromMilliseconds(200);
        var orchestrator = _h.CreateOrchestrator();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => orchestrator.InvestigateAsync(incident.Id)));

        Assert.Equal(1, _h.Ai.InvestigateCalls);
        Assert.Equal(1, AiReservations(incident.Id));
    }

    [Fact]
    public async Task ConcurrentTelemetryCannotRaceIntoDuplicateIncidents()
    {
        // A real file database with a connection per caller: genuinely concurrent requests, each
        // with its own DbContext, as the API and the worker would have.
        var path = Path.Combine(Path.GetTempPath(), $"kairon-dedup-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 30 }.ToString();
        AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options);
        try
        {
            await using (var setup = NewDb())
            {
                await setup.Database.EnsureCreatedAsync();
                setup.Projects.Add(new Kairon.Backend.Models.Platform.Project
                {
                    Id = _h.ProjectId, Name = "Order API", Slug = "order-api", IsActive = true, CreatedAt = DateTime.UtcNow
                });
                await setup.SaveChangesAsync();
            }

            await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(async () =>
            {
                await using var db = NewDb();
                var engine = new CorrelationEngine(db, new AuditService(db, NullLogger<AuditService>.Instance),
                    new IncidentKeyGenerator(db), TestHarness.Opt(_h.Detection), NullLogger<CorrelationEngine>.Instance);
                await engine.CorrelateAsync(new List<DetectionSignal>
                {
                    Signal(i % 2 == 0 ? "repeated-errors" : "latency-threshold", i % 2 == 0 ? "errors" : "latency")
                });
            })));

            await using var check = NewDb();
            var incident = Assert.Single(await check.SreIncidents.ToListAsync());
            Assert.Equal(2, incident.SignalCount); // one row per distinct rule
            Assert.Equal(IncidentStatus.Detected, incident.Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData("GET /x?access_token=abc123def&page=2", "abc123def")]
    [InlineData("refresh-token: rt_998877", "rt_998877")]
    [InlineData("X-Session-Id=sess-42-xyz", "sess-42-xyz")]
    [InlineData("client_secret=\"shh-secret\"", "shh-secret")]
    [InlineData("Set-Cookie: auth=abcdef; HttpOnly", "abcdef")]
    [InlineData("db_password=hunter2;", "hunter2")]
    public void RedactionMasksCompoundSecretNamesAndCookies(string input, string secret)
    {
        var scrubbed = Redaction.Scrub(input)!;
        Assert.DoesNotContain(secret, scrubbed);
        Assert.Contains("[redacted]", scrubbed);
    }

    [Fact]
    public void RedactionLeavesOrdinaryDiagnosticTextReadable()
    {
        const string text = "TimeoutException: inventory call exceeded 2000ms on /api/orders (tokenizer ok)";
        Assert.Equal(text, Redaction.Scrub(text));
    }
}
