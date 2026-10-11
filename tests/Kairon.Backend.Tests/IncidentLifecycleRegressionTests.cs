using Kairon.Backend.DTOs.Sre;
using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services;
using Kairon.Backend.Services.Orchestration;
using Kairon.Backend.Services.Remediation.Tools;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// Regressions found in end-to-end testing: a new fault on a service whose earlier incident is still
/// open must not vanish into it, and a restart for one kind of fault must not make KAIRON treat a
/// different later fault as "the same problem came back".
/// </summary>
public sealed class IncidentLifecycleRegressionTests : IDisposable
{
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private string Key => $"{_h.ProjectId}|{_h.Environment}|{_h.Service}";

    private SreIncident SeedOpenIncident(string metric, DateTime lastSignalAt, IncidentStatus status = IncidentStatus.RecommendationReady)
    {
        var incident = _h.SeedIncident(status, correlationKey: Key);
        incident.RootCause = "Earlier diagnosis";
        incident.Timestamp = lastSignalAt.AddMinutes(-1);
        incident.CorrelatedMetricsJson = SreJson.Serialize(new List<CorrelatedSignalSnapshot>
        {
            new() { Rule = metric == "cpu" ? "cpu-threshold" : "error-rate-threshold", MetricName = metric,
                    Symptom = metric, Observed = 95, Threshold = metric == "cpu" ? 80 : 10, Unit = "%", Severity = "High", DetectedAt = lastSignalAt }
        });
        _h.Db.SaveChanges();
        return incident;
    }

    private void SeedPaymentFailures()
    {
        for (var i = 0; i < 4; i++)
            _h.SeedMetric(DateTime.UtcNow.AddSeconds(-30 + i * 8), cpu: 10, latency: 120, requests: 10, errors: 6);
    }

    // --- A new fault after the earlier incident went quiet --------------------------------------

    [Theory]
    [InlineData(10)]
    [InlineData(3)]   // no fixed five-minute quiet period: the overload is simply no longer being detected
    public async Task APaymentFailureSpikeAfterAnEarlierFaultStoppedOpensItsOwnIncident(int overloadStoppedMinutesAgo)
    {
        // The overload incident was left open (nobody approved or closed it) and its fault stopped.
        var overload = SeedOpenIncident("cpu", DateTime.UtcNow.AddMinutes(-overloadStoppedMinutesAgo));
        SeedPaymentFailures();

        var touched = await _h.CreateOrchestrator().DetectAndCorrelateAsync(_h.ProjectId, _h.Environment, _h.Service);

        var payment = Assert.Single(_h.Db.SreIncidents.AsNoTracking().Where(i => i.Id != overload.Id));
        Assert.Contains(payment.Id, touched);
        Assert.Contains("errorRate", payment.CorrelatedMetricsJson);
        Assert.Equal(IncidentStatus.Detected, payment.Status);
        var untouched = await _h.Db.SreIncidents.AsNoTracking().SingleAsync(i => i.Id == overload.Id);
        Assert.DoesNotContain("errorRate", untouched.CorrelatedMetricsJson);
    }

    [Fact]
    public async Task ANewKindOfFaultWhileTheIncidentIsStillActiveIsInvestigatedAgainAutomatically()
    {
        var active = SeedOpenIncident("cpu", DateTime.UtcNow.AddSeconds(-40));
        SeedPaymentFailures();

        await _h.CreateOrchestrator().DetectAndCorrelateAsync(_h.ProjectId, _h.Environment, _h.Service);

        // Same ongoing degradation, so the same incident - but the AI is asked again, because it
        // never saw the payment failures.
        Assert.Single(_h.Db.SreIncidents);
        Assert.Contains("errorRate", (await _h.Db.SreIncidents.AsNoTracking().SingleAsync()).CorrelatedMetricsJson);
        var queued = await DrainAsync();
        Assert.Contains(queued, w => w.Kind == WorkItemKind.ReinvestigateIncident && w.IncidentId == active.Id);
    }

    [Fact]
    public async Task AFaultStillBeingDetectedWithAnUnchangedReadingCountsAsOngoing()
    {
        // First detected long ago, but re-detected 30 s ago with the same reading: still happening.
        var overload = SeedOpenIncident("cpu", DateTime.UtcNow.AddMinutes(-10));
        var snapshots = SreJson.Deserialize(overload.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>());
        snapshots[0].LastSeenAt = DateTime.UtcNow.AddSeconds(-30);
        overload.CorrelatedMetricsJson = SreJson.Serialize(snapshots);
        _h.Db.SaveChanges();
        SeedPaymentFailures();

        await _h.CreateOrchestrator().DetectAndCorrelateAsync(_h.ProjectId, _h.Environment, _h.Service);

        Assert.Single(_h.Db.SreIncidents);   // co-occurring symptoms of what is happening now
        Assert.Contains(await DrainAsync(), w => w.Kind == WorkItemKind.ReinvestigateIncident && w.IncidentId == overload.Id);
    }

    [Fact]
    public async Task UnchangedReDetectionsRecordThatTheFaultIsStillHappeningWithoutTouchingTheIncident()
    {
        _h.Detection.CooldownSeconds = 0;
        SeedPaymentFailures();
        await _h.CreateOrchestrator().DetectAndCorrelateAsync(_h.ProjectId, _h.Environment, _h.Service);
        var incident = await _h.Db.SreIncidents.AsNoTracking().SingleAsync();
        var firstSeen = SreJson.Deserialize(incident.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>()).Max(s => s.LastSeenAt);
        await DrainAsync();

        await Task.Delay(50);
        await _h.CreateOrchestrator().DetectAndCorrelateAsync(_h.ProjectId, _h.Environment, _h.Service);

        var again = await _h.Db.SreIncidents.AsNoTracking().SingleAsync();
        Assert.True(SreJson.Deserialize(again.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>()).Max(s => s.LastSeenAt) > firstSeen);
        Assert.Equal(incident.SignalCount, again.SignalCount);
        Assert.Empty(await DrainAsync());
        Assert.DoesNotContain(_h.Db.IncidentEvents, e => e.IncidentId == incident.Id && e.EventType == IncidentEventTypes.Correlated);
    }

    [Fact]
    public async Task TheSameFaultRefiringNeitherDuplicatesTheIncidentNorCallsTheAiAgain()
    {
        var ongoing = SeedOpenIncident("errorRate", DateTime.UtcNow.AddSeconds(-40));
        SeedPaymentFailures();

        await _h.CreateOrchestrator().DetectAndCorrelateAsync(_h.ProjectId, _h.Environment, _h.Service);
        await _h.CreateOrchestrator().DetectAndCorrelateAsync(_h.ProjectId, _h.Environment, _h.Service);

        Assert.Single(_h.Db.SreIncidents);
        Assert.DoesNotContain(await DrainAsync(), w => w.Kind == WorkItemKind.ReinvestigateIncident);
        Assert.Equal(0, _h.Ai.InvestigateCalls);
        Assert.Equal(ongoing.Id, (await _h.Db.SreIncidents.AsNoTracking().SingleAsync()).Id);
    }

    [Theory]
    [InlineData(IncidentStatus.Resolved)]
    [InlineData(IncidentStatus.Failed)]
    [InlineData(IncidentStatus.Rejected)]
    [InlineData(IncidentStatus.Cancelled)]
    public async Task AClosedIncidentNeverSuppressesANewOccurrenceOfTheSameFault(IncidentStatus closed)
    {
        var earlier = SeedOpenIncident("errorRate", DateTime.UtcNow.AddSeconds(-40), closed);
        SeedPaymentFailures();

        await _h.CreateOrchestrator().DetectAndCorrelateAsync(_h.ProjectId, _h.Environment, _h.Service);

        var fresh = Assert.Single(_h.Db.SreIncidents.AsNoTracking().Where(i => i.Id != earlier.Id));
        Assert.Equal(IncidentStatus.Detected, fresh.Status);
    }

    // --- "It came back after a restart" means the SAME fault --------------------------------------

    [Theory]
    [InlineData("cpu", false)]          // a restart for overload says nothing about payment failures
    [InlineData("errorRate", true)]     // payment failures back right after a restart for payment failures
    public async Task OnlyTheSameKindOfFaultCountsAsReturningAfterARestart(string restartedFor, bool expected)
    {
        var restarted = SeedOpenIncident(restartedFor, DateTime.UtcNow.AddMinutes(-8), IncidentStatus.Resolved);
        _h.Db.RemediationActions.Add(new RemediationAction
        {
            IncidentId = restarted.Id, ActionKey = "ACT-0001", ActionType = ServiceToolNames.RestartApplication,
            Status = RemediationStatus.Executed, CompletedAt = DateTime.UtcNow.AddMinutes(-6)
        });
        _h.Db.SaveChanges();
        var payment = SeedOpenIncident("errorRate", DateTime.UtcNow.AddSeconds(-10), IncidentStatus.Detected);
        payment.Timestamp = DateTime.UtcNow;
        payment.CorrelationKey = Key + "|new";
        _h.Db.SaveChanges();

        var evidence = await _h.CreateEvidenceCollector().CollectAsync(payment);

        Assert.Equal(expected, evidence.Recurrence!.RecurredAfterRestart);
        Assert.Equal(1, evidence.Recurrence.IncidentsLast24h);
    }

    private async Task<List<IncidentWorkItem>> DrainAsync()
    {
        var items = new List<IncidentWorkItem>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try
        {
            await foreach (var item in _h.Queue.ReadAllAsync(timeout.Token))
            {
                items.Add(item);
                _h.Queue.Complete(item);
            }
        }
        catch (OperationCanceledException) { }
        return items;
    }
}
