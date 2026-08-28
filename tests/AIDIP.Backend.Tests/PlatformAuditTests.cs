using AIDIP.Backend.Services.Audit;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AIDIP.Backend.Tests;

public sealed class PlatformAuditTests : IDisposable
{
    private readonly TestHarness _h = new();

    [Fact]
    public async Task AdministrativeAuditIsDurableBoundedAndRedacted()
    {
        var service = new PlatformAuditService(_h.Db, TimeProvider.System,
            NullLogger<PlatformAuditService>.Instance);

        var evt = service.Record("credential.created", "operator:alice", "project-credential",
            Guid.NewGuid().ToString(), _h.ProjectId, message: "password=super-secret-value",
            data: new { KeyPrefix = "krn_safe", Authorization = "Bearer secret-token-value" });
        await _h.Db.SaveChangesAsync();
        _h.Db.ChangeTracker.Clear();

        var persisted = await _h.Db.PlatformAuditEvents.FindAsync(evt.Id);
        Assert.NotNull(persisted);
        Assert.DoesNotContain("super-secret-value", persisted.Message);
        Assert.DoesNotContain("secret-token-value", persisted.DataJson);
        Assert.Contains("krn_safe", persisted.DataJson);
    }

    public void Dispose() => _h.Dispose();
}
