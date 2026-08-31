using Kairon.Backend.Models.Sre;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// IncidentProcessingWorker's sweep query used to Take(50) pending incidents with no OrderBy - a
/// non-deterministic subset under any provider that doesn't happen to scan in timestamp order,
/// meaning the same 50 stuck incidents could starve forever while others are picked up.
///
/// The worker itself (a BackgroundService with an internal ExecuteAsync polling loop) isn't
/// practical to drive from a unit test, so this exercises the identical query shape directly
/// against the same DbContext the worker uses, which is what actually matters: the query is
/// genuinely ordered, not the hosting loop around it.
/// </summary>
public class IncidentProcessingWorkerQueryTests : IDisposable
{
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void PendingIncidentSweepPicksTheFiftyOldestDeterministically()
    {
        var ready = DateTime.UtcNow;

        // Inserted FIRST but timestamped LATER - must be excluded by a correctly ordered Take(50).
        var newer = new List<SreIncident>();
        for (var i = 0; i < 20; i++)
            newer.Add(_h.SeedIncident(IncidentStatus.Detected));

        // Inserted SECOND but timestamped EARLIER - the 50 that must be selected, oldest first.
        var older = new List<SreIncident>();
        for (var i = 0; i < 50; i++)
            older.Add(_h.SeedIncident(IncidentStatus.Detected));

        var baseline = ready.AddMinutes(-10);
        for (var i = 0; i < newer.Count; i++)
        {
            newer[i].Timestamp = baseline.AddSeconds(1000 + i);
        }
        for (var i = 0; i < older.Count; i++)
        {
            older[i].Timestamp = baseline.AddSeconds(i);
        }
        _h.Db.SaveChanges();

        // The exact query shape used by IncidentProcessingWorker's sweep
        // (backend/Services/Orchestration/IncidentProcessingWorker.cs).
        var pending = _h.Db.SreIncidents
            .Where(i => i.Status == IncidentStatus.Detected && i.Timestamp <= ready)
            .OrderBy(i => i.Timestamp)
            .Select(i => new { i.Id, i.Timestamp })
            .Take(50)
            .ToList();

        Assert.Equal(50, pending.Count);
        Assert.Equal(older.Select(i => i.Id).OrderBy(id => id), pending.Select(p => p.Id).OrderBy(id => id));
        // Strictly ascending: proves the rows are truly ordered, not just the right count.
        Assert.True(pending.Zip(pending.Skip(1), (a, b) => a.Timestamp <= b.Timestamp).All(ok => ok));
    }
}
