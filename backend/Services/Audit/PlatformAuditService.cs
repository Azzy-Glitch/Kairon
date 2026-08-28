using AIDIP.Backend.Configuration;
using AIDIP.Backend.Infrastructure;
using AIDIP.Backend.Models;

namespace AIDIP.Backend.Services.Audit;

public interface IPlatformAuditService
{
    PlatformAuditEvent Record(string action, string actor, string targetType, string targetId,
        Guid? projectId = null, string result = "succeeded", string? message = null, object? data = null);
}

public sealed class PlatformAuditService : IPlatformAuditService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger<PlatformAuditService> _logger;

    public PlatformAuditService(AppDbContext db, TimeProvider time, ILogger<PlatformAuditService> logger)
    {
        _db = db;
        _time = time;
        _logger = logger;
    }

    public PlatformAuditEvent Record(string action, string actor, string targetType, string targetId,
        Guid? projectId = null, string result = "succeeded", string? message = null, object? data = null)
    {
        var evt = new PlatformAuditEvent
        {
            Timestamp = _time.GetUtcNow().UtcDateTime,
            Action = Clean(action, 100),
            Actor = Clean(Redaction.Scrub(actor), 200, "local-operator"),
            TargetType = Clean(targetType, 100),
            TargetId = Clean(targetId, 200),
            ProjectId = projectId,
            Result = Clean(result, 40),
            Message = Truncate(Redaction.Scrub(message), 2000),
            DataJson = data is null ? null : Truncate(Redaction.Scrub(SreJson.Serialize(data)), 8000)
        };
        _db.PlatformAuditEvents.Add(evt);
        _logger.LogInformation("Platform audit {Action} target={TargetType}:{TargetId} actor={Actor} result={Result}",
            evt.Action, evt.TargetType, evt.TargetId, evt.Actor, evt.Result);
        return evt;
    }

    private static string Clean(string? value, int max, string fallback = "unknown") =>
        Truncate(string.IsNullOrWhiteSpace(value) ? fallback : value.Trim(), max)!;

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
