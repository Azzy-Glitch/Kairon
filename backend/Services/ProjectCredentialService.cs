using System.Security.Cryptography;
using System.Text;
using Kairon.Backend.Configuration;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Services;

public sealed record CreatedProjectCredential(Guid Id, string Name, string KeyPrefix, string ApiKey, DateTime CreatedAt);

/// <summary>Ported near-verbatim from Azzy's productization branch - the persistent, non-expiring
/// credential a pairing redemption ultimately issues (see SdkPairingService).</summary>
public interface IProjectCredentialService
{
    Task<CreatedProjectCredential?> CreateAsync(Guid projectId, string name, CancellationToken cancellationToken);
    Task<bool> RevokeAsync(Guid projectId, Guid credentialId, CancellationToken cancellationToken);
    Task<bool> AuthorizeAsync(Guid projectId, string? suppliedKey, CancellationToken cancellationToken);

    /// <summary>Batch variant for the normalized telemetry endpoint (PlatformTelemetryController),
    /// which can reference more than one project per request - authorizes only when every distinct
    /// project id in the batch accepts the same supplied key.</summary>
    Task<bool> AuthorizeAsync(IEnumerable<Guid> projectIds, string? suppliedKey, CancellationToken cancellationToken);
}

public sealed class ProjectCredentialService : IProjectCredentialService
{
    private readonly AppDbContext _db;
    private readonly PlatformSecurityOptions _options;
    private readonly TimeProvider _time;

    public ProjectCredentialService(AppDbContext db, IOptions<PlatformSecurityOptions> options, TimeProvider time)
    {
        _db = db;
        _options = options.Value;
        _time = time;
    }

    public async Task<CreatedProjectCredential?> CreateAsync(Guid projectId, string name, CancellationToken cancellationToken)
    {
        if (!await _db.Projects.AnyAsync(x => x.Id == projectId, cancellationToken)) return null;
        var key = "krn_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = _time.GetUtcNow().UtcDateTime;
        var credential = new ProjectApiCredential
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = string.IsNullOrWhiteSpace(name) ? "Telemetry" : name.Trim(),
            KeyPrefix = key[..12],
            KeyHash = Hash(key),
            CreatedAt = now
        };
        _db.ProjectApiCredentials.Add(credential);
        await _db.SaveChangesAsync(cancellationToken);
        return new CreatedProjectCredential(credential.Id, credential.Name, credential.KeyPrefix, key, now);
    }

    public async Task<bool> RevokeAsync(Guid projectId, Guid credentialId, CancellationToken cancellationToken)
    {
        var credential = await _db.ProjectApiCredentials.SingleOrDefaultAsync(
            x => x.Id == credentialId && x.ProjectId == projectId, cancellationToken);
        if (credential is null) return false;
        credential.RevokedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> AuthorizeAsync(Guid projectId, string? suppliedKey, CancellationToken cancellationToken)
    {
        if (!_options.RequireTelemetryKey) return true;
        if (string.IsNullOrWhiteSpace(suppliedKey) || suppliedKey.Length < 16) return false;
        var prefix = suppliedKey[..Math.Min(12, suppliedKey.Length)];
        var candidates = await _db.ProjectApiCredentials.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.KeyPrefix == prefix && x.RevokedAt == null)
            .Select(x => x.KeyHash)
            .ToListAsync(cancellationToken);
        var suppliedHash = Hash(suppliedKey);
        return candidates.Any(candidate => CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(suppliedHash)));
    }

    public async Task<bool> AuthorizeAsync(IEnumerable<Guid> projectIds, string? suppliedKey,
        CancellationToken cancellationToken)
    {
        var ids = projectIds.Distinct().ToList();
        if (ids.Count == 0) return false;
        foreach (var projectId in ids)
        {
            if (!await AuthorizeAsync(projectId, suppliedKey, cancellationToken)) return false;
        }
        return true;
    }

    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
