# KAIRON Observability Migration — Final Report

Companion to [`OBSERVABILITY_MIGRATION.md`](OBSERVABILITY_MIGRATION.md) (the living
phase-by-phase log). This document is the final write-up: what was actually built, what was
actually tested, what still isn't there, and exactly how to run all of it. Every claim below was
verified against running code in this session — nothing here is asserted from the plan alone.

## 1. What changed, in one picture

```
Before:  HTTP Error (.NET SDK only) -> AI -> Remediation -> Verification

After:   .NET app --SDK-->        \
         Python app --SDK-->       Telemetry -> Detection -> Correlation -> Incident
         Any process/log --Agent--/                                          |
                                                                        AI Diagnosis
                                                                    Recommendation -> Approval
                                                                    Remediation -> Verification
```

The incident lifecycle itself (detect → correlate → investigate → diagnose → predict →
recommend → approve → remediate → verify → close) is **unchanged** — every line of it that
existed before this migration still exists, untouched, and still passes its original tests. What
changed is what can feed into it: HTTP/metric telemetry from a .NET app was the only source
before; now a Python app, a log file, and a monitored OS process can too, and all of them fold
into the same correlation/AI/remediation pipeline with zero special-casing in the incident
lifecycle itself.

## 2. What was built (by phase)

| Phase | Deliverable |
|---|---|
| 1 | `docs/OBSERVABILITY_MIGRATION.md` — scope, architecture, demo-scenario decision |
| 2 | `sdk-python/` — Python telemetry SDK + FastAPI middleware, mirroring the .NET SDK's resilience contract |
| 3 | `agent/Kairon.Agent/` — zero-code log tailer + process watcher, new backend ingestion endpoint (`POST /api/telemetry/events`), new `AgentEvent` table |
| 4 | Detection engine gains 3 new rules (log-pattern-match, process-crash, process-high-resource); correlation engine titles these incidents meaningfully instead of falling back to "Anomaly" |
| 5 | AI evidence package gains a 4th source (`log_events`) alongside metrics/HTTP/history; mock AI provider reasons about it |
| 6 | Frontend: "Agent" badge on the Symptoms table distinguishes Agent-sourced signals from SDK-sourced ones |
| 7 | This report — full-stack live validation, redaction proof, a real bug found and fixed |

## 3. Files changed

**New projects**
- `sdk-python/` — `kairon/client.py`, `kairon/middleware.py`, `tests/` (30 tests), `examples/order_worker.py`
- `agent/Kairon.Agent/` — `AgentOptions.cs`, `AgentEventClient.cs`, `Program.cs`, `LogTailing/{LogEventExtractor,LogPatternMatcher,LogDeduplicator}.cs`, `ProcessWatch/ProcessWatcher.cs`
- `tests/Kairon.Agent.Tests/` — 23 tests

**Backend**
- `Models/AgentEvent.cs`, `Infrastructure/AppDbContext.cs` (new `DbSet<AgentEvent>`), migration `20260827073346_AddAgentEvents`
- `DTOs/AgentEventDto.cs`, `Controllers/TelemetryController.cs` (`POST /api/telemetry/events`, redacts on ingestion)
- `Services/Detection/{DetectionContext,DetectionEngine,DetectionRules}.cs`, `Models/Sre/IncidentEnums.cs` (3 new `DetectionRuleKind` members), `Configuration/KaironOptions.cs`, `Extensions/ServiceExtensions.cs`
- `Services/Correlation/CorrelationEngine.cs` (title/condition switches extended)
- `DTOs/Sre/AiInvestigationDtos.cs` (`AgentEventEvidenceDto`, `EvidencePackageDto.LogEvents`), `Models/Sre/IncidentEvidence.cs`, `Services/Evidence/EvidenceCollector.cs`
- `tests/Kairon.Backend.Tests/` — +37 tests (`AgentEventDetectionTests`, `EvidenceCollectorAgentEventsTests`, `CorrelationTests` additions)

**AI service**
- `ai-service/kairon/schemas.py` (`AgentEvent`, `EvidencePackage.log_events`), `prompts.py`, `providers/mock.py` (process-crash and log-pattern diagnosis branches)
- `ai-service/tests/` — +3 tests

**Frontend**
- `frontend/src/components/sre/IncidentDetail.jsx` (Agent badge), `frontend/src/styles/sre.css`, `frontend/src/components/sre/sre.test.jsx` — +1 test

**Demo**
- `demo/Kairon.DemoApp/` — added Serilog file logging (matches the backend's own dated-daily-file pattern) so the Agent has a real log to tail

## 4. Schema

One new table, `AgentEvents`, with an `EventType` string discriminator
(`LogPatternMatch` / `ProcessStarted` / `ProcessCrash` / `ProcessHighResource`) rather than
separate tables per event kind — deliberately matching the "canonical, language-agnostic event"
philosophy the spec asked for:

```
AgentEvents
  Id, ProjectId, Environment, Service, Application,
  EventType, Severity, Message, Source, Timestamp
```

Wire format for both new SDKs matches the existing PascalCase `TelemetryPayload`/`MetricDto`
contract field-for-field — the Python SDK posts to the **same, unmodified**
`/api/telemetry/incidents` and `/api/telemetry/metrics` endpoints the .NET SDK always used.

## 5. Capabilities per component

- **Python SDK** (`sdk-python/`): background daemon thread, bounded drop-oldest queue, one POST
  per item, fail-open (never raises into host code), ASGI middleware captures unhandled
  exceptions and **re-raises them** (never swallows), matching the .NET SDK's own invariant.
- **KAIRON Agent** (`agent/Kairon.Agent/`): tails exactly one log rotation scheme (dated-daily
  files, Serilog's own default format), groups multi-line stack traces into one entry,
  deduplicates repeated identical lines within a window, rate-limits before sending. Polls a
  named OS process for CPU/memory/crash state.
- **Backend ingestion**: `POST /api/telemetry/events` redacts the raw message via the existing
  `Redaction.Scrub` **before** it is ever persisted — this is the one new path that handles
  free-text, potentially-secret-bearing content (the two pre-existing endpoints only ever
  received structured fields).
- **Detection/Correlation**: 3 new rules fold Agent-sourced signals into the same
  Service-scoped `CorrelationKey` every other signal already uses — no new correlation logic was
  needed, only new rules that emit `DetectionSignal`s in the shape that already existed.
- **AI evidence**: a 4th evidence source (`log_events`) appended via the same
  query→bound→map→persist pattern the other 3 sources already use.
- **Frontend**: a small "Agent" badge on the Symptoms table's Rule column — the actual
  evidence-rendering path (`SymptomsPanel`) needed **no** changes, since it already renders
  `correlatedSignals` generically regardless of source.

## 6. Tests run — 467/467 passing

| Suite | Count | Command |
|---|---|---|
| `Kairon.SDK.Tests` (.NET SDK, pre-existing) | 40 | `dotnet test Kairon.slnx` |
| `Kairon.Agent.Tests` (new) | 23 | (same) |
| `Kairon.Backend.Tests` | 209 | (same) |
| AI service (pytest) | 94 | `pytest` in `ai-service/` |
| Frontend (vitest) | 71 | `npx vitest run` in `frontend/` |
| Python SDK (pytest) | 30 | `pytest` in `sdk-python/` |
| **Total** | **467** | |

All four suites were re-run clean in this final pass (not just after their own phase) — see
section 8 for the live/manual verification that goes beyond unit tests.

## 7. Live end-to-end verification (this session, against the running stack)

Every item below was performed against the actually-running backend (`:8000`), AI service
(`:8001`), frontend (`:5173`), demo app (`:5080`), Python worker (`:8090`), and Agent — not
inferred from code reading.

**Multi-source correlation into one incident** — proven twice, each combining a different pair
of the three source types:
- **INC-0021**: 11 correlated signals in one incident — metric-deviation (cpu/memory/latency/
  queue), latency-threshold, repeated-errors, error-rate-threshold, retry-storm, queue-backlog
  (all .NET SDK-sourced), **plus** `log-pattern-match` and `process-crash` (both Agent-sourced,
  the latter triggered for real when I restarted the demo app mid-session and the Agent's
  process watcher correctly detected the crash-then-restart as a real `ProcessCrash` →
  `ProcessStarted` pair).
- **INC-0022**: `5 repeated errors on /api/inventory/reserve (RuntimeError), threshold 5` — a
  detection signal generated entirely from the **Python SDK's** own exception telemetry — folded
  into the same `OrderProcessingService`-scoped incident alongside .NET SDK metric-deviation
  signals (latency/cpu/memory).

**Full lifecycle to resolution** — INC-0021 was carried through the entire remaining pipeline
live: AI diagnosis (confidence 0.92, citing real numbers — 24 errors, 46.7% error rate, 2331ms
latency, all matching what was actually generated) → recommendation
(`DisableDemoRetryLoop`) → operator approval → remediation execution
(`"Demo app accepted 'disable-retry-loop'."`) → verification (5/5 applicable metrics recovered) →
**status: Resolved**. This confirms the pre-existing lifecycle is untouched by everything this
migration added.

**Redaction, concretely proven end-to-end** — a fake secret
(`apikey=SECRET-abc123XYZ-should-never-be-queryable`) was written directly into the log file the
Agent tails. The Agent picked it up, sent it to the backend, and the persisted row reads:

```
Message: "Payment gateway auth failed: apikey=[redacted]"
```

A full-table scan (`SELECT COUNT(*) FROM AgentEvents WHERE Message LIKE '%SECRET-abc123XYZ%'`)
returned **0** across all 44 rows in the table — the raw secret never became queryable.

## 8. A real bug found and fixed during this validation

Live-testing the Python-worker correlation (INC-0022 above) initially **failed** — the Python
worker's errors were being received and stored correctly, but never produced a detection signal.
Root cause: Python's `datetime.isoformat()` renders a UTC-aware timestamp with a `+00:00` suffix,
not `Z`. The backend's `Timestamp` fields are `DateTime`, not `DateTimeOffset`, and
`System.Text.Json`'s default `DateTime` converter treats an explicit `+00:00` offset as needing
conversion to the **server's local time zone** — while a `Z`-suffixed value (what the .NET SDK's
own `DateTime.UtcNow` always serializes to) round-trips as UTC untouched. On this machine
(UTC+5), every Python-SDK event was landing **5 hours in the future**, past the leading edge of
any backward-looking detection window — which is exactly why it never correlated.

Fixed in `sdk-python/kairon/client.py` (`_utcnow_iso()` now emits the same `Z` suffix the .NET
SDK already uses) with a new regression test
(`test_timestamp_is_z_suffixed_not_numeric_offset`). Live-reverified after the fix: a fresh burst
landed at the correct UTC instant and correlated into INC-0022 as shown above. This bug would
have affected **any** production deployment where the backend server does not run in UTC — a
realistic scenario, not an edge case.

## 9. Limitations and explicitly deferred scope

Unchanged from the Phase 1 scope decision, restated here for completeness — **not built**:
- Node.js, Java, Go, PHP SDKs (the wire contract is designed so any of them could follow the
  Python SDK's pattern with zero backend changes, but none were implemented).
- A packaged Windows installer/MSI or Windows Service registration for the Agent (it runs as a
  console/worker process).
- Windows Event Log integration.
- A 5-tier RBAC permission system (today's single operator-identity-on-every-approval model is
  unchanged).
- A 15-view frontend redesign (one small addition — the Agent badge — to the existing incident
  detail page).
- General-purpose log rotation handling beyond the one dated-daily-file scheme.
- Configurable/DSL-based log pattern matching (a small hardcoded pattern set, matching every
  existing detection rule's own style).

**New, honest nuances surfaced by this validation pass** (not defects, but worth knowing):
- The mock AI provider selects root cause by a **fixed priority order**
  (process-crash > retries > cpu+latency > errors > log-pattern > generic) when multiple signal
  types are present on one incident — it picks one story, not a genuine multi-source synthesis a
  production LLM would do. On INC-0021, a re-investigation triggered while `retries` and
  `process-crash` were both present selected the retries branch (0.92 confidence) because that
  specific evidence snapshot's correlated-signal list did not include `processCrash` at that
  instant — the underlying incident's aggregate signal list did (and still does), but the
  point-in-time snapshot handed to that specific AI call did not. This is a property of how
  evidence snapshots are taken relative to a continuously-updating correlation loop, not a defect
  in either the evidence collector or the correlation engine.
- `RepeatedErrorsRule` (pre-existing logic, unmodified by this migration) only emits **one**
  signal per evaluation, for whichever endpoint/error-type group has the most occurrences. When
  the .NET demo app's retry storm and the Python worker's own errors were active
  *simultaneously*, the larger .NET-sourced group won and the Python-sourced errors were
  correctly computed but not individually surfaced as their own named signal that pass — this is
  why the first attempt at proving 3-way correlation needed a second, isolated Python-only burst
  (documented in section 7) rather than a single concurrent one.

## 10. Exact run / install steps

**Prerequisites**: .NET 10 SDK, Python 3.11+, Node 18+, SQL Server (LocalDB or Express),
`dotnet-ef` (`dotnet tool install --global dotnet-ef`).

```bash
# 1. Database (from repo root)
cd backend && dotnet ef database update

# 2. Backend (port 8000) — point ConnectionStrings__DefaultConnection at your SQL instance
#    if it isn't (localdb)\MSSQLLocalDB, e.g.:
export ConnectionStrings__DefaultConnection='Server=.\SQLEXPRESS;Database=Kairon_SDK;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True'
cd backend && dotnet run --urls http://localhost:8000

# 3. AI service (port 8001, mock mode by default)
cd ai-service && pip install -r requirements.txt && uvicorn kairon.main:app --port 8001

# 4. Frontend (port 5173)
cd frontend && npm install && npm run dev

# 5. Demo app (port 5080) — the shared telemetry target for all 3 sources
cd demo/Kairon.DemoApp && dotnet run --urls http://localhost:5080

# 6. KAIRON Agent — tails demo/Kairon.DemoApp/logs, watches the Kairon.DemoApp process
cd agent/Kairon.Agent && dotnet run

# 7. Python SDK example worker (port 8090) — the 3rd telemetry source
cd sdk-python && pip install -e ".[fastapi]" uvicorn
cd examples && uvicorn order_worker:app --port 8090
```

**Demo flow**: `POST http://localhost:5080/kairon-control/start-retry-storm`, then drive traffic
against `POST /api/orders/process` (.NET) and `POST http://localhost:8090/api/inventory/reserve`
(Python) — one incident on `OrderProcessingService` will fold signals from whichever sources are
active. Open `http://localhost:5173` to watch it live, including the "Agent" badge on any
log/process-sourced row.

**Test suites**:
```bash
dotnet test Kairon.slnx                 # 272 tests (.NET SDK + Agent + Backend)
cd ai-service && pytest                 # 94 tests
cd frontend && npx vitest run           # 71 tests
cd sdk-python && pytest                 # 30 tests
```

## 11. Conclusion

The architectural claim — a language-agnostic telemetry contract where HTTP/metric/log/process
signals from any of 3 different collection mechanisms (.NET SDK, Python SDK, Agent) fold into
the *same* incident and drive the *same* AI-diagnosis → approval → remediation → verification
pipeline with zero changes to that pipeline — is demonstrated, not just described: two live
incidents each proved a different pair of sources correlating together, one was carried through
to a resolved state, redaction was proven with a real injected secret and a full-table scan
finding zero leaks, and a genuine cross-language bug was found and fixed rather than glossed
over. What's explicitly not done is listed in section 9, matching the scope this migration
deliberately committed to at the start.
