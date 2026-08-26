# Remediation fix: incidents stuck in "Verifying" forever

Status: **Fixed and verified live.** Branch `dev/abdullah`, commit `bffad63`.

## The bug

Approving a remediation action could leave the incident stuck in `Verifying` status
permanently, with no way for the operator to recover it. This blocked the core product loop —
an incident could never reach `Resolved`, which is the entire point of the platform.

### Root cause

`IncidentOrchestrator.ApproveAsync` executed the approved action and verified recovery
**synchronously**, on the same HTTP request thread that handled the approve call:

```
ApproveAsync (HTTP request)
  └─ Executor.ExecuteAsync(...)        up to ExecutionTimeoutSeconds
  └─ VerificationService.VerifyAsync(...)  settle + up to MaxWaitSeconds
```

That chain can take **70+ seconds**. The frontend's axios client
([client.js](../frontend/src/api/client.js)) has a hard **30-second** timeout. Whenever the
chain ran long — which is the normal case, not an edge case — the browser gave up on the
request with a `TaskCanceledException` on the backend side, or the tab was simply closed. Either
way, nothing was left watching that in-flight execute/verify work, so the incident was
orphaned in `Verifying` with no code path back to a terminal state.

This is the same class of problem the codebase had already solved once: `ProcessIncident`
(AI investigation) used to risk blocking the ingestion path the same way, and was moved onto a
background worker queue for exactly this reason. `ApproveAsync` had not received the same
treatment.

## The fix

Move execute+verify off the request thread, onto the existing background worker queue —
the same pattern `ProcessIncident` already uses.

| File | Change |
|---|---|
| [`IncidentProcessingQueue.cs`](../backend/Services/Orchestration/IncidentProcessingQueue.cs) | New `WorkItemKind.ExecuteRemediation`; `IncidentWorkItem` gained an `ActionId` field |
| [`IncidentOrchestrator.cs`](../backend/Services/Orchestration/IncidentOrchestrator.cs) | `ApproveAsync` now transitions the incident to `Remediating`, enqueues an `ExecuteRemediation` work item, and returns immediately. The execute+verify logic moved into a new `ExecuteAndVerifyAsync(incidentId, actionId)` method |
| [`IncidentProcessingWorker.cs`](../backend/Services/Orchestration/IncidentProcessingWorker.cs) | New switch case dispatches `ExecuteRemediation` items to `ExecuteAndVerifyAsync` |
| [`OrchestrationTests.cs`](../tests/Kairon.Backend.Tests/OrchestrationTests.cs) | 3 tests updated to call `ExecuteAndVerifyAsync` directly after `ApproveAsync`, mirroring how tests already call `InvestigateAsync` directly instead of via the queue |

`ApproveAsync` now returns as soon as the incident is transitioned and the work item is queued
— no HTTP request waits on execution or verification anymore.

## Tests

```
dotnet build Kairon.slnx                          0 warnings, 0 errors
dotnet test tests/Kairon.Backend.Tests             192/192 passed
dotnet test tests/Kairon.SDK.Tests                  40/40 passed
```

## Live verification

Ran the full stack (backend :8000, AI service :8001, frontend :5173) and drove a fresh incident
through the entire lifecycle in a real browser (Playwright), start to finish:

```
OBSERVE → DETECT → CORRELATE → INVESTIGATE → DIAGNOSE → PREDICT → RECOMMEND
       → APPROVE → REMEDIATE → VERIFY → CLOSE
```

| t | Event |
|---|---|
| +0s | Loaded app, confirmed **Kairon** branding |
| +9.3s | Demo Center → **Run Incident Simulation** |
| +23.6s | INC-0009 detected (retry-storm, latency, error-rate, queue-backlog signals correlated) |
| +45.0s | AI diagnosis ready: retry loop root cause, 92% confidence, recommending **Disable Retry Loop** |
| +47.3s | Approved |
| **+47.5s** | **`POST /actions/{id}/approve` responded in 0.3s** — previously this call itself took 70s+ and would exceed the frontend's 30s timeout |
| +49.2s | Status → **Remediating** |
| +56.5s | Status → **Verifying** |
| +70.8s | Status → **Resolved** — verification passed 5/5 metrics |

Verification result:

| Metric | Before | After | Threshold | Result |
|---|---|---|---|---|
| CPU | 72.3% | 24.4% | 80% | Within threshold |
| Latency | 1676ms | 126ms | 1000ms | Within threshold |
| Error rate | 23.6% | 0% | 10% | Within threshold |
| Retries | 66.1/min | 0/min | 30/min | Within threshold |
| Queue depth | 59.5 | 2.5 | 50 | Within threshold |

The incident never stalled in `Verifying`. Total time from clicking approve to `Resolved` was
about 23.5 seconds — well within normal bounds and nowhere near the old 30s timeout.

Screenshots from this run (app load with Kairon branding confirmed, the AI diagnosis and
recommendation, the `Verifying` state, and the final `Resolved` state with the verification
table) were captured during testing but are kept out of the repo; ask if you want them shared
separately.

## A second, related question — investigated, no fix needed

There was a theoretical concern that the AI might diagnose off incomplete evidence: if the
retry-storm signal hadn't correlated into the incident yet by the time the first investigation
runs, the mock provider's decision logic (`ai-service/kairon/providers/mock.py`) would fall
through to a CPU/latency-based diagnosis and recommend a plausible-but-wrong action (reduce
worker concurrency) instead of the actually-correct one (disable the retry loop).

In this fresh run, the retry-storm signal correlated at +23.6s, well before the investigation
ran at +42s, so the AI picked the correct action from the start. The mock's decision logic
already prioritizes the retry-storm signal correctly when it's present — no code change was
needed here. Flagging it as a known theoretical edge case (not reproduced) rather than fixing
something that isn't currently broken.

## Known limitations

- One pre-existing incident (`INC-0001`) in the local dev database is a genuine orphan from
  **before** this fix was deployed — created by the old synchronous bug, permanently stuck in
  `Verifying`. It's stale local test data, not reproducible with the fix in place. Reset/reseed
  the local demo database before a live run so it doesn't show up as the "top active incident"
  on the Overview page.
- A narrow residual risk remains: the work queue is an in-memory channel, so if the backend
  process crashes/restarts in the split second between `ApproveAsync` enqueueing the work item
  and the worker picking it up, that item is lost and the incident stays `Remediating`
  permanently (the same limitation `ProcessIncident` already has). Considered out of scope for
  now — very unlikely during a short demo, and building persistence/recovery for it would be
  disproportionate to the risk.
