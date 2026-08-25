using AIDIP.Backend.Models.Sre;
using AIDIP.Backend.Services;
using AIDIP.Backend.Services.Audit;
using AIDIP.Backend.Services.Remediation;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AIDIP.Backend.Tests;

/// <summary>Verification, audit trail and secret-safe logging (PRD sections 13, 14, 19).</summary>
public class VerificationAndAuditTests : IDisposable
{
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private RemediationAction ExecutedAction(SreIncident incident, string type = DemoToolNames.DisableDemoRetryLoop)
    {
        var action = new RemediationAction
        {
            IncidentId = incident.Id,
            ActionKey = "ACT-0001",
            ActionType = type,
            Status = RemediationStatus.Executed,
            StartedAt = DateTime.UtcNow.AddSeconds(-1),
            CompletedAt = DateTime.UtcNow
        };

        incident.Actions.Add(action);
        _h.Db.RemediationActions.Add(action);
        _h.Db.SaveChanges();
        return action;
    }

    // --- Verification ---

    [Fact]
    public async Task RecoveredMetricsPassVerification()
    {
        var incident = _h.SeedIncident(IncidentStatus.Verifying);
        var action = ExecutedAction(incident);

        for (var i = 0; i < 4; i++)
            _h.SeedMetric(incident.Timestamp.AddSeconds(i * 5), cpu: 94, latency: 2400, retries: 30);
        for (var i = 0; i < 4; i++)
            _h.SeedMetric(DateTime.UtcNow.AddSeconds(i), cpu: 22, latency: 110, retries: 0);

        var result = await _h.CreateVerificationService().VerifyAsync(incident, action);

        Assert.Equal(VerificationStatus.Passed, result.Status);
        Assert.True(result.RecoveryScore >= _h.Verification.RequiredRecoveryScore);
    }

    [Fact]
    public async Task StillBreachingMetricsFailVerification()
    {
        var incident = _h.SeedIncident(IncidentStatus.Verifying);
        var action = ExecutedAction(incident);

        for (var i = 0; i < 4; i++)
            _h.SeedMetric(incident.Timestamp.AddSeconds(i * 5), cpu: 94, latency: 2400, retries: 30);
        for (var i = 0; i < 4; i++)
            _h.SeedMetric(DateTime.UtcNow.AddSeconds(i), cpu: 96, latency: 2600, retries: 40);

        var result = await _h.CreateVerificationService().VerifyAsync(incident, action);

        Assert.Equal(VerificationStatus.Failed, result.Status);
        Assert.Equal("metrics-still-breaching", result.FailureReason);
    }

    [Fact]
    public async Task NoPostRemediationTelemetryIsInconclusiveNotSuccess()
    {
        // The one outcome that must never be reported as success.
        var incident = _h.SeedIncident(IncidentStatus.Verifying);
        var action = ExecutedAction(incident);

        var result = await _h.CreateVerificationService().VerifyAsync(incident, action);

        Assert.Equal(VerificationStatus.Inconclusive, result.Status);
        Assert.Equal("no-post-remediation-telemetry", result.FailureReason);
        Assert.Equal(0, result.RecoveryScore);
    }

    [Fact]
    public async Task VerificationProducesBeforeAndAfterComparisons()
    {
        var incident = _h.SeedIncident(IncidentStatus.Verifying);
        var action = ExecutedAction(incident);

        for (var i = 0; i < 4; i++)
            _h.SeedMetric(incident.Timestamp.AddSeconds(i * 5), cpu: 94, latency: 2400, retries: 30);
        for (var i = 0; i < 4; i++)
            _h.SeedMetric(DateTime.UtcNow.AddSeconds(i), cpu: 22, latency: 110, retries: 0);

        var result = await _h.CreateVerificationService().VerifyAsync(incident, action);
        var comparisons = SreJson.Deserialize(result.ComparisonsJson, new List<MetricComparison>());

        Assert.NotEmpty(comparisons);

        var cpu = comparisons.First(c => c.Metric == "cpu");
        Assert.Equal(94, cpu.Before);
        Assert.Equal(22, cpu.After);
        Assert.True(cpu.Improved);
        Assert.True(cpu.MeetsThreshold);
    }

    [Fact]
    public async Task OnlyTheMetricsThatBreachedAreCompared()
    {
        var incident = _h.SeedIncident(IncidentStatus.Verifying);
        var action = ExecutedAction(incident);

        for (var i = 0; i < 4; i++)
            _h.SeedMetric(DateTime.UtcNow.AddSeconds(i), cpu: 22, memory: 40, latency: 110, retries: 0);

        var result = await _h.CreateVerificationService().VerifyAsync(incident, action);
        var metrics = SreJson.Deserialize(result.ComparisonsJson, new List<MetricComparison>())
            .Select(c => c.Metric).ToHashSet();

        // The seeded incident breached retries and cpu; the executed tool also affects latency,
        // error rate and queue. Memory was never part of the problem.
        Assert.DoesNotContain("memory", metrics);
        Assert.Contains("cpu", metrics);
        Assert.Contains("retries", metrics);
    }

    [Fact]
    public async Task VerificationIsAudited()
    {
        var incident = _h.SeedIncident(IncidentStatus.Verifying);
        var action = ExecutedAction(incident);

        await _h.CreateVerificationService().VerifyAsync(incident, action);

        var types = _h.Db.IncidentEvents.Where(e => e.IncidentId == incident.Id)
            .Select(e => e.EventType).ToList();

        Assert.Contains(IncidentEventTypes.Verifying, types);
        Assert.Contains(IncidentEventTypes.Verified, types);
    }

    // --- Audit trail ---

    [Fact]
    public void AuditEntriesCaptureActorAndStateChange()
    {
        var incident = _h.SeedIncident();

        _h.Audit.Record(incident, IncidentEventTypes.Approved, "operator:alice",
            previousState: "AwaitingApproval", newState: "Remediating",
            actionId: "ACT-0001", message: "approved");

        _h.Db.SaveChanges();

        var evt = _h.Db.IncidentEvents.Single(e => e.EventType == IncidentEventTypes.Approved);

        Assert.Equal("operator:alice", evt.Actor);
        Assert.Equal("AwaitingApproval", evt.PreviousState);
        Assert.Equal("Remediating", evt.NewState);
        Assert.Equal("ACT-0001", evt.ActionId);
    }

    [Fact]
    public async Task TimelineIsReturnedInChronologicalOrder()
    {
        var incident = _h.SeedIncident();

        _h.Audit.Record(incident, IncidentEventTypes.Detected, "detection-engine");
        _h.Db.SaveChanges();
        await Task.Delay(10);
        _h.Audit.Record(incident, IncidentEventTypes.Investigating, "ai-orchestrator");
        _h.Db.SaveChanges();

        var timeline = await _h.Audit.GetTimelineAsync(incident.Id);

        Assert.Equal(IncidentEventTypes.Detected, timeline[0].EventType);
        Assert.Equal(IncidentEventTypes.Investigating, timeline[1].EventType);
    }

    [Fact]
    public void AuditRequiresAnIncident()
    {
        Assert.Throws<ArgumentNullException>(() => _h.Audit.Record(null!, "X", "actor"));
    }

    // --- Redaction ---

    [Theory]
    [InlineData("Request failed with api_key=sk-abc123def456ghi789", "sk-abc123def456ghi789")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9", "eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("GEMINI_API_KEY=AIzaSyD1234567890abcdefghijklmnop", "AIzaSyD1234567890abcdefghijklmnop")]
    [InlineData("password=hunter2 was rejected", "hunter2")]
    [InlineData("Server=db;User ID=sa;Password=P@ssw0rd!;", "P@ssw0rd!")]
    public void SecretsAreScrubbedFromAuditText(string input, string secret)
    {
        var scrubbed = Redaction.Scrub(input);

        Assert.DoesNotContain(secret, scrubbed);
        Assert.Contains("[redacted]", scrubbed);
    }

    [Fact]
    public void BareProviderKeyShapesAreScrubbed()
    {
        Assert.DoesNotContain("sk-abcdefghij1234567890",
            Redaction.Scrub("the token sk-abcdefghij1234567890 was rejected"));
    }

    [Fact]
    public void OrdinaryTextSurvivesRedaction()
    {
        const string message = "CPU sustained at 94.2% (peak 96.0%), threshold 80%";
        Assert.Equal(message, Redaction.Scrub(message));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RedactionHandlesEmptyInput(string? input)
    {
        Assert.Equal(input, Redaction.Scrub(input));
    }

    [Fact]
    public void ExceptionDescriptionOmitsTheStackTrace()
    {
        Exception captured;

        try
        {
            throw new InvalidOperationException("connection string Password=secret failed");
        }
        catch (Exception ex)
        {
            captured = ex;
        }

        var described = Redaction.Describe(captured);

        Assert.Contains("InvalidOperationException", described);
        Assert.DoesNotContain("secret", described);
        Assert.DoesNotContain("at AIDIP", described);
    }

    [Fact]
    public void AuditScrubsSecretsBeforePersisting()
    {
        var incident = _h.SeedIncident();

        _h.Audit.Record(incident, IncidentEventTypes.Failed, "ai-orchestrator",
            error: "call failed: api_key=sk-abc123def456ghi789");

        _h.Db.SaveChanges();

        var evt = _h.Db.IncidentEvents.Single(e => e.EventType == IncidentEventTypes.Failed);

        Assert.DoesNotContain("sk-abc123def456ghi789", evt.Error);
    }
}
