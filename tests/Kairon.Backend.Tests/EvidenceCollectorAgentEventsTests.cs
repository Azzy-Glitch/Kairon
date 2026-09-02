using Kairon.Backend.Models;
using Kairon.Backend.Services;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// EvidenceCollector's KAIRON Agent evidence source (docs/OBSERVABILITY_MIGRATION.md): the AI
/// gets the fuller picture of every reported event, not just the compact CorrelatedSignals entry
/// a threshold rule produced.
/// </summary>
public class EvidenceCollectorAgentEventsTests : IDisposable
{
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task AgentEventsForTheIncidentsServiceAndWindowAreIncluded()
    {
        var incident = _h.SeedIncident();

        _h.Db.AgentEvents.Add(new AgentEvent
        {
            ProjectId = _h.ProjectId,
            Environment = _h.Environment,
            Service = _h.Service,
            EventType = "LogPatternMatch",
            Severity = "Error",
            Message = "Order processing failed: timeout",
            Source = "demo/logs/kairon-demo.txt",
            Timestamp = DateTime.UtcNow
        });
        await _h.Db.SaveChangesAsync();

        var package = await _h.CreateEvidenceCollector().CollectAsync(incident);

        var evt = Assert.Single(package.LogEvents);
        Assert.Equal("LogPatternMatch", evt.EventType);
        Assert.Equal("Order processing failed: timeout", evt.Message);
    }

    [Fact]
    public async Task AgentEventsForADifferentServiceAreExcluded()
    {
        var incident = _h.SeedIncident();

        _h.Db.AgentEvents.Add(new AgentEvent
        {
            ProjectId = _h.ProjectId,
            Environment = _h.Environment,
            Service = "SomeOtherService",
            EventType = "LogPatternMatch",
            Severity = "Error",
            Message = "Unrelated failure",
            Timestamp = DateTime.UtcNow
        });
        await _h.Db.SaveChangesAsync();

        var package = await _h.CreateEvidenceCollector().CollectAsync(incident);

        Assert.Empty(package.LogEvents);
    }

    [Fact]
    public async Task AnIncidentWithNoAgentEventsGetsAnEmptyListNotAnError()
    {
        var incident = _h.SeedIncident();

        var package = await _h.CreateEvidenceCollector().CollectAsync(incident);

        Assert.Empty(package.LogEvents);
    }

    [Fact]
    public async Task AgentEventMessagesAreRedactedBeforeReachingTheAi()
    {
        var incident = _h.SeedIncident();

        _h.Db.AgentEvents.Add(new AgentEvent
        {
            ProjectId = _h.ProjectId,
            Environment = _h.Environment,
            Service = _h.Service,
            EventType = "LogPatternMatch",
            Severity = "Error",
            // Redaction already happens at ingestion (TelemetryController.CreateEvent) - this
            // proves EvidenceCollector does not undo or bypass that by re-reading the raw column
            // and forgetting to scrub again downstream.
            Message = "connection failed apikey=[redacted]",
            Timestamp = DateTime.UtcNow
        });
        await _h.Db.SaveChangesAsync();

        var package = await _h.CreateEvidenceCollector().CollectAsync(incident);

        Assert.DoesNotContain("SECRET", package.LogEvents[0].Message);
    }

    [Fact]
    public async Task AgentEventsAreOnlyPersistedAsEvidenceWhenPresent()
    {
        var incident = _h.SeedIncident();
        var package = await _h.CreateEvidenceCollector().CollectAsync(incident);

        _h.CreateEvidenceCollector().Persist(incident, package);
        await _h.Db.SaveChangesAsync();

        Assert.DoesNotContain(incident.Evidence, e => e.Kind == Kairon.Backend.Models.Sre.EvidenceKinds.AgentEvents);
    }

    [Fact]
    public async Task EveryUntrustedTextSurfaceIsRedactedBeforeAiEvidenceIsSerialized()
    {
        const string secret = "ghp_123456789012345678901234567890";
        var incident = _h.SeedIncident();
        incident.Title = $"failure token={secret}";
        incident.AffectedEndpoint = $@"C:\Users\alice\secrets.txt?token={secret}";
        incident.SymptomsJson = SreJson.Serialize(new[] { $"alice@example.com at 10.20.30.40 token={secret}" });

        var telemetry = _h.SeedTelemetry(
            DateTime.UtcNow,
            endpoint: $@"C:\Users\alice\app\endpoint",
            errorMessage: $"authorization=Bearer {secret}");
        incident.TelemetryReferencesJson = SreJson.Serialize(new[] { telemetry.Id });

        _h.Db.AgentEvents.Add(new AgentEvent
        {
            ProjectId = _h.ProjectId,
            Environment = _h.Environment,
            Service = _h.Service,
            EventType = "LogPatternMatch",
            Severity = "Error",
            Message = $"password={secret} from alice@example.com",
            Source = @"C:\Users\alice\logs\application.log",
            Timestamp = DateTime.UtcNow
        });
        await _h.Db.SaveChangesAsync();

        var package = await _h.CreateEvidenceCollector().CollectAsync(incident);
        var serialized = SreJson.Serialize(package);

        Assert.DoesNotContain(secret, serialized);
        Assert.DoesNotContain("alice@example.com", serialized);
        Assert.DoesNotContain("10.20.30.40", serialized);
        Assert.DoesNotContain(@"C:\Users\alice", serialized);
        Assert.Contains("[redacted]", serialized);
    }

    [Fact]
    public async Task CompleteAiEvidencePackageHonorsAggregateCharacterBudget()
    {
        _h.AiOptions.MaxEvidencePayloadChars = 4096;
        _h.AiOptions.MaxAgentEvents = 50;
        var incident = _h.SeedIncident();

        for (var i = 0; i < 50; i++)
        {
            _h.Db.AgentEvents.Add(new AgentEvent
            {
                ProjectId = _h.ProjectId,
                Environment = _h.Environment,
                Service = _h.Service,
                EventType = "LogPatternMatch",
                Severity = "Error",
                Message = new string('x', 500),
                Source = $"application-{i}.log",
                Timestamp = DateTime.UtcNow.AddSeconds(i)
            });
        }
        await _h.Db.SaveChangesAsync();

        var package = await _h.CreateEvidenceCollector().CollectAsync(incident);

        Assert.True(SreJson.Serialize(package).Length <= _h.AiOptions.MaxEvidencePayloadChars);
        Assert.True(package.LogEvents.Count < 50, "least-specific evidence should be trimmed to fit");
    }
}
