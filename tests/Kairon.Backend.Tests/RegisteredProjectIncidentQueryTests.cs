using Xunit;

namespace Kairon.Backend.Tests;

public sealed class RegisteredProjectIncidentQueryTests : IDisposable
{
    private readonly TestHarness _h = new();

    [Fact]
    public async Task IncidentFeedDoesNotExposeOrphanedProjectHistory()
    {
        _h.SeedIncident();
        _h.Db.Projects.RemoveRange(_h.Db.Projects);
        await _h.Db.SaveChangesAsync();

        var incidents = await _h.CreateQueryService().ListAsync("active", null, null, 50);

        Assert.Empty(incidents);
    }

    [Fact]
    public async Task DashboardDoesNotTreatOrphanedMetricsAsLiveMonitoring()
    {
        _h.SeedMetric(DateTime.UtcNow, cpu: 92, memory: 78);
        _h.Db.Projects.RemoveRange(_h.Db.Projects);
        await _h.Db.SaveChangesAsync();

        var dashboard = await _h.CreateQueryService().GetDashboardAsync(null);

        Assert.Equal(0, dashboard.ActiveIncidents);
        Assert.Null(dashboard.Metrics.CpuPercent);
        Assert.Null(dashboard.Metrics.MemoryPercent);
        Assert.Empty(dashboard.Metrics.Recent);
    }

    [Fact]
    public async Task ActiveRegisteredProjectRemainsVisible()
    {
        var incident = _h.SeedIncident();

        var incidents = await _h.CreateQueryService().ListAsync("active", null, null, 50);

        Assert.Contains(incidents, item => item.Id == incident.Id);
    }

    [Fact]
    public async Task InactiveProjectIsNotPresentedAsConnectedMonitoring()
    {
        _h.SeedIncident();
        var project = _h.Db.Projects.Single(p => p.Id == _h.ProjectId);
        project.IsActive = false;
        await _h.Db.SaveChangesAsync();

        var incidents = await _h.CreateQueryService().ListAsync("active", null, null, 50);

        Assert.Empty(incidents);
    }

    public void Dispose() => _h.Dispose();
}
