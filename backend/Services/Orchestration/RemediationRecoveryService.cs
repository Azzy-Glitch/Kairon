using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services.Audit;
using Microsoft.EntityFrameworkCore;

namespace Kairon.Backend.Services.Orchestration;

// Process-local only. The supported deployment has one backend/executor per target.
// Fixed stripes bound memory; collisions merely serialize unrelated incidents.
public static class RemediationExecutionGate
{
    private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static SemaphoreSlim Gate(Guid id) => Gates[(uint)id.GetHashCode() % (uint)Gates.Length];
    public static async Task<IDisposable> EnterAsync(Guid id, CancellationToken ct)
    {
        var gate = Gate(id); await gate.WaitAsync(ct); return new Lease(gate);
    }
    public static IDisposable? TryEnter(Guid id) => Gate(id).Wait(0) ? new Lease(Gate(id)) : null;
    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}

/// <summary>Reconciles durable action state with the volatile delivery queue. Never replays an uncertain command.</summary>
public sealed class RemediationRecoveryService(AppDbContext db, IIncidentProcessingQueue queue, IAuditService audit)
{
    public const string VerificationResumed = "VerificationResumed";
    public static readonly TimeSpan RecoveryWindow = TimeSpan.FromMinutes(15);

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var ids = await db.SreIncidents.AsNoTracking()
            .Where(i => i.Status == IncidentStatus.Remediating || i.Status == IncidentStatus.Verifying)
            .OrderBy(i => i.UpdatedAt).Select(i => i.Id).Take(50).ToListAsync(ct);
        foreach (var id in ids)
        {
            using var lease = RemediationExecutionGate.TryEnter(id);
            if (lease is null) continue; // Live execution/verification owns the incident.
            var incident = await db.SreIncidents.Include(i => i.Actions).Include(i => i.Verifications)
                .AsSplitQuery().SingleAsync(i => i.Id == id, ct);
            await db.Entry(incident).ReloadAsync(ct);
            foreach (var action in incident.Actions) await db.Entry(action).ReloadAsync(ct);
            if (incident.Status is not (IncidentStatus.Remediating or IncidentStatus.Verifying)) continue;
            var actions = incident.Actions.Where(a => a.Status is RemediationStatus.Approved or RemediationStatus.Executing or RemediationStatus.Executed).ToList();
            // Latest approval identifies the current attempt. Older executed alternatives may have
            // failed verification and must neither invalidate nor replace a newer attempt.
            var active = incident.Actions.Where(a => a.ApprovedAt.HasValue)
                .OrderByDescending(a => a.ApprovedAt).ThenByDescending(a => a.CreatedAt).FirstOrDefault()
                ?? (actions.Count == 1 ? actions[0] : null);
            string? failure = null;
            if (active is null || actions.Any(a => a.Id != active.Id && (a.Status is RemediationStatus.Approved or RemediationStatus.Executing ||
                !incident.Verifications.Any(v => v.Id == a.VerificationResultId && v.Status != VerificationStatus.Pending && v.CompletedAt.HasValue))))
                failure = "interrupted-lifecycle-without-one-recoverable-action";
            else if (active.Status is not (RemediationStatus.Approved or RemediationStatus.Executing or RemediationStatus.Executed))
                failure = "latest-approved-action-already-failed-or-withdrawn";
            else if (active.Status == RemediationStatus.Executing || (active.Status == RemediationStatus.Approved && active.StartedAt.HasValue))
                failure = "execution-outcome-uncertain-no-automatic-replay";
            else if (!active.ApprovedAt.HasValue || DateTime.UtcNow - active.ApprovedAt.Value > RecoveryWindow || active.ApprovedAt > DateTime.UtcNow.AddSeconds(5))
                failure = "approval-recovery-window-expired";
            else if (active.Status == RemediationStatus.Executed && (!active.CompletedAt.HasValue ||
                await db.IncidentEvents.AnyAsync(e => e.IncidentId == id && e.ActionId == active.ActionKey && e.EventType == VerificationResumed, ct)))
                failure = "verification-recovery-exhausted-or-execution-incomplete";

            if (failure is not null)
            {
                foreach (var action in actions)
                {
                    // Preserve a recorded execution success; only its recovery remains unconfirmed.
                    if (action.Status != RemediationStatus.Executed)
                    {
                        action.Status = RemediationStatus.Failed;
                        action.CompletedAt ??= DateTime.UtcNow;
                        action.ExecutionError = failure;
                    }
                }
                foreach (var pending in incident.Verifications.Where(v => v.Status == VerificationStatus.Pending))
                {
                    pending.Status = VerificationStatus.Inconclusive; pending.CompletedAt = DateTime.UtcNow;
                    pending.FailureReason = failure; pending.Summary = "Interrupted verification; operator review required.";
                }
                incident.VerificationState = VerificationStatus.Inconclusive;
                incident.RemediationState = RemediationStatus.Failed;
                incident.FailureReason = failure + "; inspect the target and use fresh evidence and a new approval before any further remediation.";
                var previous = IncidentLifecycle.Transition(incident, IncidentStatus.Failed);
                audit.Record(incident, "RemediationRecoveryFailed", "remediation-recovery", previousState: previous.ToString(),
                    newState: incident.Status.ToString(), actionId: active?.ActionKey, result: "operator-review-required", message: incident.FailureReason);
            }
            else
            {
                var accepted = queue.TryEnqueue(new IncidentWorkItem(WorkItemKind.ExecuteRemediation,
                    incident.ProjectId, incident.Environment, incident.Service, id, active!.Id));
                if (!await db.IncidentEvents.AnyAsync(e => e.IncidentId == id && e.ActionId == active.ActionKey && e.EventType == "RemediationRecoveryQueued", ct) && accepted)
                    audit.Record(incident, "RemediationRecoveryQueued", "remediation-recovery", actionId: active.ActionKey,
                        message: active.Status == RemediationStatus.Approved ? "Recovering persisted unstarted approval; policy will be checked again." : "Recovering recorded execution for verification only; no command replay.");
                // Rotate bounded batches even if queue backpressure delays delivery. Approval age stays fixed.
                incident.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
        }
    }
}

public sealed class RemediationRecoveryWorker(IServiceScopeFactory scopes, ILogger<RemediationRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<RemediationRecoveryService>().ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Remediation reconciliation failed; persisted work retained for next bounded sweep"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
