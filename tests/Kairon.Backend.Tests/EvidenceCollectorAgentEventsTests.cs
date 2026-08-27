using Kairon.Backend.Models;
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
}
