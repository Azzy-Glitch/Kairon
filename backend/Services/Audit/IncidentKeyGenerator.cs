using AIDIP.Backend.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AIDIP.Backend.Services.Audit;

/// <summary>
/// Produces the short human-facing keys operators actually say out loud ("INC-0007", "ACT-0003").
/// </summary>
public interface IIncidentKeyGenerator
{
    Task<string> NextIncidentKeyAsync(CancellationToken cancellationToken = default);
    Task<string> NextActionKeyAsync(CancellationToken cancellationToken = default);
}

public class IncidentKeyGenerator : IIncidentKeyGenerator
{
    // Key allocation is serialized process-wide. The keys are cosmetic - the Guid id is the real
    // identity - so a single-process gate is the right amount of machinery here, and a collision
    // would only ever produce a duplicate label, never a duplicate incident.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly AppDbContext _db;

    public IncidentKeyGenerator(AppDbContext db) => _db = db;

    public Task<string> NextIncidentKeyAsync(CancellationToken cancellationToken = default) =>
        NextAsync<Models.Sre.SreIncident>(
            "INC", () => _db.SreIncidents.CountAsync(cancellationToken), cancellationToken);

    public Task<string> NextActionKeyAsync(CancellationToken cancellationToken = default) =>
        NextAsync<Models.Sre.RemediationAction>(
            "ACT", () => _db.RemediationActions.CountAsync(cancellationToken), cancellationToken);

    private async Task<string> NextAsync<TEntity>(
        string prefix,
        Func<Task<int>> counter,
        CancellationToken cancellationToken) where TEntity : class
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            // Entities already added but not yet saved count too. Correlation creates several
            // incidents inside one SaveChanges, and counting only persisted rows would hand every
            // one of them the same key and trip the unique index.
            var pending = _db.ChangeTracker
                .Entries<TEntity>()
                .Count(e => e.State == EntityState.Added);

            var count = await counter();
            return $"{prefix}-{count + pending + 1:D4}";
        }
        finally
        {
            Gate.Release();
        }
    }
}
