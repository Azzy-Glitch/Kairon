# Observability migration: from HTTP-error analyzer to multi-source SRE platform

Status: **in progress.** This document is the north star for the migration and is kept up to
date as each phase lands. It also records what was deliberately cut and why, so the scope
decision is visible rather than silently discovered later.

## Why

Kairon's incident pipeline (detection → correlation → AI investigation → approval →
remediation → verification) already works end to end, but every signal that reaches it today
comes from one of two sources: HTTP/API telemetry (via `Incident` rows) or numeric metrics (via
`Metric` rows), both reported by the .NET SDK. The architectural goal is to make those two
sources *one category among several*, not the platform's central definition — a Python
application, a log file, or a monitored process should be able to feed the same pipeline without
the core detection/correlation/AI code caring which language or collection mechanism produced
the signal.

```
Before:  HTTP Error -> AI

After:   Application/System -> Telemetry -> Normalization -> Context -> Correlation
                                                            -> Incident -> AI Diagnosis
                                                            -> Recommendation -> Approval
                                                            -> Remediation -> Verification
```

## Scope for this pass

The full request this migration is based on described a production observability platform on
the scale of Datadog/New Relic — a packaged Windows Agent installer, SDKs for six languages, a
5-tier permission system, Windows Event Log integration, and a full frontend redesign. That is
genuinely weeks of engineering. This pass is a deliberately scoped slice that proves the
architecture is real and demoable within days, not weeks, while changing nothing about the
incident lifecycle that already works.

**Built in this pass:**
- A Python SDK (`sdk-python/`), mirroring the .NET SDK's exact resilience shape, for FastAPI/
  general Python apps.
- A KAIRON Agent (`agent/Kairon.Agent/`) — a .NET worker process that tails one specific,
  well-understood log rotation scheme (dated daily files) and watches a named process's CPU/
  memory/crash state. This is zero-code monitoring in the sense that the *target* application
  needs no SDK — but it is a console/worker app for this pass, not a packaged Windows Service
  installer.
- A normalized log/process event path through the backend: new `LogEvent`/`ProcessEvent` table,
  two new deterministic detection rules, correlation support so a log-only or process-only
  signal folds into an incident the same way an HTTP or metric signal does, and AI evidence that
  includes this new evidence type when present.
- Ingestion-time redaction for raw log content (the one new path that handles unredacted-by-
  default text — see "Redaction" below).

**Explicitly deferred, not built, documented here as future work:**
- Node.js, Java, Go, PHP SDKs. The telemetry contract (PascalCase HTTP/metric fields, the new
  log/process event shape) is designed so any of these could follow the same pattern the Python
  SDK demonstrates, without backend changes.
- A packaged Windows installer/MSI for the Agent. The Agent is structured so it could become a
  `Microsoft.Extensions.Hosting.WindowsServices` service later without a rewrite, but packaging
  is out of scope now.
- Windows Event Log integration.
- A 5-tier permission system (Observe/Diagnose/Recommend/Remediate/Administer). Today's single
  operator-identity model (an operator name recorded on every approval, per the existing audit
  trail) is unchanged.
- A full frontend redesign into 15 dedicated views. The existing incident-detail evidence viewer
  already renders arbitrary evidence payloads generically, so new evidence types are visible
  there without new pages. Small additions to existing pages only, if time remains.
- General-purpose log rotation handling (copy-truncate, size-based rotation, arbitrary formats).
  The Agent supports exactly the dated-daily-file scheme the backend's own Serilog config
  already uses, applied to the demo app too.
- Configurable log pattern matching. New detection rules use a small hardcoded pattern set
  (unhandled exception, OOM marker, stack-trace marker), matching every existing
  `DetectionRules.cs` rule's own style — a deliberate consistency choice, not a shortcut.

## Demo scenario decision

`DetectionSignal.CorrelationKey` is `ProjectId|Environment|Service` — correlation folds signals
together only within one `Service` name; there is no cross-service correlation. So proving
".NET + Python + Agent-collected signals fold into one incident" requires all three sources to
report the **same** service name.

All three sources in this migration target the existing `demo/Kairon.DemoApp` retry-storm
scenario, tagged `Service = "OrderProcessingService"`:
1. **.NET SDK** (already wired, unchanged) — HTTP/metric signals from the demo app itself.
2. **Python SDK example app** (`sdk-python/examples/order_worker.py`) — a small FastAPI
   companion app representing a worker for the same service.
3. **KAIRON Agent** — tails the demo app's new log file output and watches the demo app's own
   process, both tagged `OrderProcessingService`.

One demo run produces one incident correlating HTTP + metric + log + process evidence from three
different collection mechanisms, reusing the existing retry-storm scenario rather than inventing
a second one.

## Key architectural facts this plan relies on (verified against the code, not assumed)

- `DetectionSignal`/`DetectionRuleKind` already use free-text fields (`MetricName`, `Symptom`),
  so a non-HTTP signal needs no changes to those types.
- `Metric` and `Incident` (the two existing raw-telemetry tables) are fixed-shape — numeric
  gauges and HTTP-call fields respectively — neither is a generic event store, so log/process
  events get their own new table.
- `DetectionContext`/`DetectionEngine.EvaluateAsync` need two new window-bounded queries and two
  new list properties to expose log/process events to rules, following the exact pattern already
  used for `Metrics`/`Telemetry`.
- `CorrelationEngine`'s title-building switch needs two more cases so a log/process-only
  incident titles itself meaningfully instead of the generic "Anomaly" fallback.
- `EvidenceCollector` builds the AI package from independent per-source blocks (query → bound/
  truncate → map to DTO → append → persist) — already done 3 times, a 4th source is the same
  pattern again.
- `Redaction` (`backend/Services/Audit/Redaction.cs`) is a stateless regex scrubber currently
  called only on outbound paths (AI evidence, audit log) — nothing redacts on ingestion today.
  The new log-event ingestion path calls it explicitly, since it is the first path handling raw,
  potentially-secret-containing text.
- The .NET SDK's resilience shape — one bounded drop-oldest queue, one background sender loop,
  one item per POST, fail-open by construction, short independent timeouts, no circuit breaker —
  is what the Python SDK mirrors. Wire format is PascalCase JSON matching `TelemetryPayload`/
  `MetricDto` field-for-field, so the existing `/api/telemetry/incidents` and `/api/telemetry/
  metrics` endpoints need zero changes for the Python SDK to work.

## Progress

| Phase | Status |
|---|---|
| 1. Foundation (this document) | Done |
| 2. Python SDK | Done — 29/29 tests passing |
| 3. KAIRON Agent + backend ingestion | Not started |
| 4. Detection & correlation extension | Not started |
| 5. AI evidence extension | Not started |
| 6. Frontend | Not started |
| 7. End-to-end validation & final report | Not started |
