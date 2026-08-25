using AIDIP.Backend.Configuration;
using AIDIP.Backend.Infrastructure;
using AIDIP.Backend.Models.Sre;
using Microsoft.EntityFrameworkCore;

namespace AIDIP.Backend.Services.Audit;

/// <summary>
/// Writes the incident audit trail (PRD section 14). Every meaningful thing that happens to an
/// incident goes through here, which is what makes "who approved this and what did it do" a query
/// rather than an archaeology exercise.
/// </summary>
public interface IAuditService
{
    /// <summary>
    /// Appends an audit event to the incident's in-memory collection. The caller owns the
    /// SaveChanges, so an audit entry and the state change it describes commit together.
    /// </summary>
    IncidentEvent Record(
        SreIncident incident,
        string eventType,
        string actor,
        string? previousState = null,
        string? newState = null,
        string? message = null,
        string? result = null,
        string? error = null,
        string? actionId = null,
        object? data = null);

    Task<List<IncidentEvent>> GetTimelineAsync(Guid incidentId, CancellationToken cancellationToken = default);
}

public class AuditService : IAuditService
{
    private readonly AppDbContext _db;
    private readonly ILogger<AuditService> _logger;

    public AuditService(AppDbContext db, ILogger<AuditService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public IncidentEvent Record(
        SreIncident incident,
        string eventType,
        string actor,
        string? previousState = null,
        string? newState = null,
        string? message = null,
        string? result = null,
        string? error = null,
        string? actionId = null,
        object? data = null)
    {
        ArgumentNullException.ThrowIfNull(incident);

        var evt = new IncidentEvent
        {
            IncidentId = incident.Id,
            Timestamp = DateTime.UtcNow,
            EventType = eventType,
            Actor = actor,
            PreviousState = previousState,
            NewState = newState,
            Message = Redaction.Scrub(message),
            Result = Redaction.Scrub(result),
            // Errors are scrubbed before they are stored: an audit trail is read by humans and
            // must never become the place a secret leaks (PRD section 19, AI PRD section 19).
            Error = Redaction.Scrub(error),
            ActionId = actionId,
            DataJson = data is null ? null : SreJson.Truncate(SreJson.Serialize(data), 8000)
        };

        incident.Events.Add(evt);
        _db.IncidentEvents.Add(evt);

        _logger.LogInformation(
            "Audit {EventType} incident={IncidentKey} actor={Actor} {Previous}->{New}",
            eventType, incident.IncidentKey, actor, previousState ?? "-", newState ?? "-");

        return evt;
    }

    public Task<List<IncidentEvent>> GetTimelineAsync(Guid incidentId, CancellationToken cancellationToken = default) =>
        _db.IncidentEvents
            .AsNoTracking()
            .Where(e => e.IncidentId == incidentId)
            .OrderBy(e => e.Timestamp)
            .ToListAsync(cancellationToken);
}
