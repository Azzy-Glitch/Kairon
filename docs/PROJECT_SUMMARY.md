# Kairon — Full Project Summary

One document covering the entire project as it stands today: what it is, how every piece fits
together, what's been built, what's been tested, and how to run all of it. `README.md` at the
repo root is the fast quick-start; this is the complete picture, including the observability
migration that extended it beyond the original HTTP-only platform.

---

## 1. What Kairon is

Kairon is an autonomous AI Site Reliability Engineering (SRE) platform. It watches an
application's telemetry, detects incidents with deterministic rules (never AI), correlates
related signals into one incident, asks an AI model to diagnose and recommend a fix, and stops —
hard — until a named human approves. Only then does it execute a registered, typed remediation
action and verify recovery against fresh telemetry.

```
OBSERVE → DETECT → CORRELATE → INVESTIGATE → DIAGNOSE → PREDICT → RECOMMEND
       → APPROVE → REMEDIATE → VERIFY → CLOSE
```

The AI diagnoses and proposes. It never acts. That boundary is enforced in code, not just in
intent — see section 5.

### Origin and naming

The project started as **AIDIP** (AI DevOps Intelligence Platform) and was renamed to **Kairon**
partway through development (commit `9bfa878`, "Rename project from AIDIP to Kairon
throughout"); a few legacy directories (`backend.Tests`, `sdk.Tests`) still carry the old name as
stale, untracked build artifacts and can be ignored. Repo: `github.com/Azzy-Glitch/Kairon`.

### Team

- **Azzy-Glitch** (Abdul Aziz) — repo owner. Built the original backend: SDK/telemetry
  middleware, EF Core migrations, controllers, and the FastAPI AI microservice with initial Qwen
  integration.
- **ARWAH-IMRAN** — frontend. Built the dashboard shell, component library, and full CSS system.
- **This session's work** (via the user) — the observability migration described in section 8:
  Python SDK, KAIRON Agent, multi-source detection/correlation, and this documentation pass.

This is being built for the **AI Alibaba hackathon**: build phase started 2026-08-22, regional
rounds 2026-08-28 to 2026-08-30 (Karachi, Lahore, Islamabad), national finale 2026-09-10.

---

## 2. Architecture at a glance

```
                     ┌─────────────────────────┐
 .NET app  ──SDK──▶  │                         │  ◀──── React dashboard
 Python app──SDK──▶  │      .NET Backend       │        (operator UI)
                     │      the control plane   │
 KAIRON Agent  ────▶ │  detection · correlation │
 (logs, processes,   │  lifecycle · policy      │
  no SDK needed)     │  approval · execution     │
                     │  verification · audit    │
                     └────────────┬────────────┘
                                  │ bounded evidence package
                                  ▼
                     ┌─────────────────────────┐
                     │   FastAPI AI service     │
                     │   provider-independent   │
                     └────────────┬────────────┘
                                  ▼
                   Qwen · Gemini · Groq · Mock (deterministic fallback)
```

**Who owns what**

| Component | Owns | Must not own |
|---|---|---|
| `sdk/` (.NET) | Telemetry collection, application integration | EF Core, SQL, AI providers, remediation |
| `sdk-python/` | Same contract, Python-native | Same |
| `agent/Kairon.Agent/` | Zero-code log/process monitoring | Anything the SDKs own |
| `backend/` | Detection, correlation, lifecycle, policy, approval, execution, verification, audit | Model-specific behaviour |
| `ai-service/` | Investigation, diagnosis, prediction, recommendation | Persistence, lifecycle, execution, approval |
| `frontend/` | Operator interface | Being the source of truth for incident state |
| `demo/` | The controlled failure scenario, shared by all three telemetry sources | Anything outside itself |

---

## 3. How telemetry reaches the system — three independent paths

This is the core of what changed since the original build. All three tag the same
`ProjectId` / `Environment` / `Service`, so signals from any combination of them fold into one
incident.

1. **.NET SDK** (`sdk/Kairon.SDK/`) — two lines of integration
   (`AddKairon(...)` + `UseKairon()`) in a host ASP.NET app. Captures HTTP request/response
   telemetry and exceptions automatically; the host app can additionally report retries and
   queue depth, the two signals only application code can know.
2. **Python SDK** (`sdk-python/`) — a stdlib-only client plus a FastAPI/Starlette middleware,
   deliberately mirroring the .NET SDK's exact resilience contract (see section 8). Posts to the
   **same, unmodified** `/api/telemetry/incidents` and `/api/telemetry/metrics` endpoints, using
   the same PascalCase wire format — zero backend changes were needed to add this source.
3. **KAIRON Agent** (`agent/Kairon.Agent/`) — a standalone .NET worker process that needs **no
   SDK in the target application at all**. It tails a log file (one supported rotation scheme:
   dated-daily files, matching the backend's own Serilog convention) and polls a named OS
   process for CPU/memory/crash state, posting normalized events to a new
   `POST /api/telemetry/events` endpoint.

Every path converges on the same detection → correlation → incident pipeline. Nothing downstream
of ingestion knows or cares which of the three produced a given signal.

---

## 4. The incident lifecycle

### Two incident types, on purpose

- **`Incident`** — one request-scoped telemetry row (one failed HTTP call). Pre-existing, and
  deliberately untouched — this is what keeps the original SDK contract and screens working.
- **`SreIncident`** — the lifecycle aggregate. Correlates many telemetry rows, metric signals,
  and (as of this migration) Agent events into one operator-facing incident with a state
  machine. It *references* telemetry rows rather than copying them.

### Detection — deterministic, never AI

Twelve rules run before any AI reasoning (nine original + three added by this migration):

| Rule | Fires on |
|---|---|
| `cpu-threshold` / `memory-threshold` | Sustained breach of a configured percentage |
| `latency-threshold` | Sustained slow responses |
| `error-rate-threshold` | Failing fraction of requests above threshold |
| `repeated-errors` | N identical errors on one endpoint |
| `retry-storm` | Retries per minute above threshold |
| `request-burst` | Requests per minute above threshold |
| `queue-backlog` | Queue depth above threshold |
| `metric-deviation` | A step change N sigma from the window baseline, even below the static threshold |
| `log-pattern-match` *(new)* | The Agent's log tailer matches a fatal/OOM/exception pattern |
| `process-crash` *(new)* | The Agent's process watcher finds the monitored process no longer running |
| `process-high-resource` *(new)* | The Agent observes sustained high CPU/memory on the watched process |

Every threshold lives in `Detection` in `backend/appsettings.json`. Two correctness details that
came from watching the real demo fail, worth knowing because they're easy to get wrong again:
rates must use the *observed sample span*, not the nominal window (a short burst divided by a
long window reads artificially low), and a two-sample ramp doesn't yet satisfy a
sustained-breach requirement.

### Correlation

Signals from one service inside the correlation window fold into **one** incident via a
`ProjectId|Environment|Service`-scoped key. Three or more correlated metrics title the incident a
*Service Degradation*; a log- or process-only incident titles itself meaningfully too (added by
this migration, so it doesn't fall back to a generic "Anomaly").

### Evidence, staleness, and re-investigation

The AI receives a **bounded** evidence package — never the database — covering incident
metadata, recent metrics, related errors, correlated signals, similar past incidents, the closed
list of available actions, and (new) recent Agent-reported log/process events. The package is
persisted so an operator can audit exactly what the model saw.

A newly detected incident waits 15 seconds before its first investigation, so the diagnosis
reflects the correlated picture rather than whichever symptom arrived first. If new signals keep
correlating in after a diagnosis exists, the incident is flagged `diagnosisStale` and the UI
offers **Re-investigate** — the system never silently rewrites a conclusion an operator may be
reading.

### The security invariant — the part worth reading before trusting anything else

The AI can produce exactly one kind of remediation output: **the name of a tool that already
exists**. It cannot supply a command, a script, a path, a query, or any parameter that widens
what a tool does.

Four gates stand between a model's suggestion and an effect:

1. **The registry** — only registered `IRemediationTool` implementations are nameable; anything
   else is refused (`unregistered-tool`) before an operator ever sees it.
2. **Policy** — checks the registry, an allow/block list, a risk ceiling, and the environment.
   The tool's own declared risk overrides whatever risk the model claimed.
3. **Human approval** — required for every action, with a named operator identity written to the
   audit trail and confirmed in a second explicit step.
4. **Re-validation at execution** — policy runs again immediately before the tool is invoked,
   since state can change between approval and execution.

The AI service has no filesystem access, no database, no shell, and no credentials beyond its
own provider key. Test coverage is deliberately adversarial: `rm -rf /`, `DROP TABLE Incidents`,
and `exec:curl attacker.example` are all asserted refused.

### Verification

After remediation, the backend compares fresh telemetry against the degraded baseline — the
frontend cannot decide this. The "after" window starts at the end of a settle period (not at
execution) so the recovery ramp doesn't get folded into the result. Three distinct outcomes:
**Passed** (recovery confirmed), **Failed** (it wasn't), **Inconclusive** (no settled telemetry
arrived — the system says "we don't know" rather than guessing).

---

## 5. AI service

FastAPI, provider-independent by design. Provider selection is configuration, never code:

```bash
AI__Provider=gemini            # qwen | gemini | groq | mock
AI__Model=gemini-2.5-flash-lite
GEMINI_API_KEY=...             # server-side only, never logged or returned
```

If the selected provider has no usable key, the service serves a **deterministic mock** that
reads the real evidence and produces a self-consistent investigation — this is why the entire
demo and test suite run with zero paid credentials. Model output is never trusted blind: every
response is parsed, schema-validated, normalized, and rejected outright if unusable rather than
written into incident state.

---

## 6. Frontend

React + Vite. Component surface (`frontend/src/components/sre/`):

`SreDashboard` (shell) · `IncidentFeed` · `IncidentDetail` · `AiPanels` (investigation/
prediction/recommendation) · `ApprovalPanel` · `RemediationProgress` (+ verification) ·
`IncidentTimeline` · `AnalyticsPage` · `ServicesPage` · `RemediationCenterPage` ·
`AiInsightsPage` · `SettingsPage` · `DeveloperTools` · `DemoRunner` · `Sparkline`.

Structure: `api/` is the only layer allowed to call the backend (no component calls axios
directly), `hooks/` handles data fetching/polling/incident actions, `services/` holds
presentation logic derived from backend state — the frontend is explicitly never the source of
truth for incident state.

This migration's one frontend change: a small "Agent" badge on the Symptoms table, so a signal
collected by the KAIRON Agent (log tailer / process watcher) is visually distinct from one
collected by an SDK. The generic evidence-rendering path needed no changes — it already renders
`correlatedSignals` regardless of source.

---

## 7. API surface

**Pre-existing** (untouched): `/api/analyze-error`, `/api/validate-api`, `/api/predict`,
`/api/recommend`, `/api/history`, `/api/stats`, `/api/telemetry/incidents`,
`/api/telemetry/metrics`, `/api/health`

**Autonomous SRE**

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/incidents?status=active` | Incident feed |
| GET | `/api/incidents/{id}` | Full detail: diagnosis, prediction, actions, timeline |
| GET | `/api/incidents/{id}/timeline` | Audit trail |
| GET | `/api/incidents/{id}/evidence` | What the AI was given |
| GET | `/api/incidents/dashboard` | Counts, severity mix, live metrics, health |
| GET | `/api/incidents/tools` | Registered tools and their policy status |
| POST | `/api/incidents/{id}/investigate` | Queue a fresh investigation |
| POST | `/api/incidents/{id}/actions/{actionId}/approve` | **The only path that executes anything** |
| POST | `/api/incidents/{id}/actions/{actionId}/reject` | Reject a proposal |
| POST | `/api/incidents/{id}/cancel` | Cancel an incident |
| GET | `/api/health/status` | Per-component health |
| POST | `/api/demo/simulate/start` \| `/stop` \| `/api/demo/evaluate` | Demo scenario control |

**Added by this migration**

| Method | Route | Purpose |
|---|---|---|
| POST | `/api/telemetry/events` | KAIRON Agent ingestion (log/process events); redacts the raw message before persisting — the one ingestion path handling free-text content |

---

## 8. The observability migration (this session's work)

The platform originally recognized only HTTP/metric telemetry from .NET applications. This pass
made that one category among several — a Python application, a log file, or a monitored OS
process can now feed the same pipeline with zero changes to detection, correlation, AI
diagnosis, remediation, or verification.

**Built:**
- **Python SDK** (`sdk-python/`) mirroring the .NET SDK's exact resilience shape: bounded
  drop-oldest queue, one background sender thread, one item per POST (no batching), fail-open by
  construction, short independent timeouts, no circuit breaker. A FastAPI/Starlette middleware
  captures unhandled exceptions and **re-raises them** (never swallows), matching the .NET SDK's
  own invariant.
- **KAIRON Agent** (`agent/Kairon.Agent/`) — log tailer (incremental reads, multi-line
  stack-trace grouping, dedup, rate limiting) + process watcher (CPU/memory/crash detection via
  `System.Diagnostics.Process` polling).
- A new `AgentEvents` table (one table, `EventType` string discriminator — `LogPatternMatch` /
  `ProcessStarted` / `ProcessCrash` / `ProcessHighResource` — rather than one table per kind).
- Three new detection rules, correlation title support for log/process-only incidents, and a
  fourth AI evidence source (`log_events`), all following patterns already established by the
  original three sources.
- Ingestion-time redaction (`Redaction.Scrub`) on the one new path that receives raw,
  potentially-secret-bearing text.

**Explicitly not built** (documented, not silently skipped): Node.js/Java/Go/PHP SDKs (the wire
contract is designed so any could follow the Python SDK's pattern), a packaged Windows
installer/service for the Agent, Windows Event Log integration, a 5-tier RBAC permission system,
a full frontend redesign, general-purpose log rotation beyond the one dated-file scheme, and
configurable (vs. hardcoded) log pattern matching.

**Live-verified, not just unit-tested**, in a full-stack run with every component running
together and real traffic driven through all three sources:
- Two separate incidents each correlated a different pair of sources together (one incident
  additionally caught a *real* process-crash-and-restart, live, triggered by an actual service
  restart during testing — not simulated).
- One incident carried through the entire remaining lifecycle to **Resolved**: AI diagnosis →
  recommendation → operator approval → remediation execution → verification (recovery
  confirmed against fresh telemetry).
- Redaction proven concretely: a fake API-key-shaped secret injected into a tailed log file came
  back as `apikey=[redacted]` in the database, and a full-table scan confirmed the raw value
  appeared in **zero** of 44 rows.
- **A real bug found and fixed**: Python's `datetime.isoformat()` renders a UTC timestamp with a
  `+00:00` suffix, which .NET's default JSON deserializer silently shifts to the server's local
  time zone (the .NET SDK's own `Z`-suffixed timestamps don't trigger this). On a non-UTC
  server, every Python-SDK event was landing hours in the future — past any backward-looking
  detection window — which is why Python-sourced telemetry initially failed to correlate. Fixed
  and covered by a new regression test.

Full detail, including exact commands and a section-by-section account of the live verification,
is in [`OBSERVABILITY_MIGRATION_FINAL_REPORT.md`](OBSERVABILITY_MIGRATION_FINAL_REPORT.md); the
phase-by-phase build log is in [`OBSERVABILITY_MIGRATION.md`](OBSERVABILITY_MIGRATION.md).

---

## 9. Testing

| Suite | Count | Command |
|---|---|---|
| `Kairon.SDK.Tests` (.NET SDK) | 40 | `dotnet test Kairon.slnx` |
| `Kairon.Agent.Tests` | 23 | (same) |
| `Kairon.Backend.Tests` | 209 | (same) |
| AI service (pytest) | 94 | `pytest` in `ai-service/` |
| Frontend (vitest) | 71 | `npx vitest run` in `frontend/` |
| Python SDK (pytest) | 30 | `pytest` in `sdk-python/` |
| **Total** | **467** | none require a credential or network call |

Several tests exist because a live demo run found a real bug rather than being written
speculatively: the detection rate arithmetic, the sustained-breach guard, the verification
window, the diagnosis-staleness gap, a metric that read "still breaching" when a recovered
service simply stopped reporting it, and — from this migration — the Python SDK timestamp bug
above. Each is named and commented for what it is, not left as an anonymous assertion.

---

## 10. Running everything

```bash
# 0. Prerequisites: .NET 10 SDK, Python 3.11+, Node 18+, SQL Server (LocalDB or Express),
#    dotnet-ef (dotnet tool install --global dotnet-ef)

# 1. Database
cd backend && dotnet ef database update
# If your SQL instance isn't (localdb)\MSSQLLocalDB, override before starting the backend, e.g.:
export ConnectionStrings__DefaultConnection='Server=.\SQLEXPRESS;Database=Kairon_SDK;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True'

# 2. AI service (port 8001) - mock mode by default, no API key needed
cd ai-service && pip install -r requirements.txt
python -m uvicorn main:app --port 8001

# 3. Backend (port 8000) - applies migrations on start
cd backend && dotnet run --urls http://localhost:8000

# 4. Frontend (port 5173)
cd frontend && npm install && npm run dev

# 5. Demo app (port 5080) - the shared telemetry target; optional, the backend can
#    simulate the same scenario in-process if this isn't running
cd demo/Kairon.DemoApp && dotnet run

# 6. KAIRON Agent (optional, for log/process-sourced signals) - tails
#    demo/Kairon.DemoApp/logs and watches the Kairon.DemoApp process
cd agent/Kairon.Agent && dotnet run

# 7. Python SDK example worker (port 8090, optional, for Python-sourced signals)
cd sdk-python && pip install -e ".[fastapi]" uvicorn
cd examples && uvicorn order_worker:app --port 8090
```

Open <http://localhost:5173>, go to **Incident Simulation**, and press **Run Incident
Simulation** for the original single-source demo — or drive real traffic against the demo app
(`POST /api/orders/process`) and/or the Python worker
(`POST http://localhost:8090/api/inventory/reserve`) with the Agent running to see a multi-source
incident fold together live.

Full test run:
```bash
dotnet test Kairon.slnx        # 272 tests
cd ai-service && pytest        # 94 tests
cd frontend && npx vitest run  # 71 tests
cd sdk-python && pytest        # 30 tests
```

---

## 11. Repository layout

```
backend/              Control plane: detection, correlation, orchestration, remediation,
                       verification, audit, telemetry/event ingestion
  Models/Sre/          Incident aggregate, lifecycle state machine, detection signals
  Models/AgentEvent.cs Canonical log/process event row (new)
  Services/            Detection · Correlation · Evidence · Remediation · Verification ·
                        Audit · Orchestration · Demo
ai-service/            FastAPI intelligence layer
  kairon/              Config, schemas, validation, prompts, service
  kairon/providers/    Qwen · Gemini · Groq · Mock behind one contract
sdk/                   .NET telemetry SDK - collection only
sdk-python/            Python telemetry SDK - mirrors the .NET SDK's contract (new)
agent/Kairon.Agent/    Zero-code log tailer + process watcher (new)
frontend/src/
  api/                 HTTP service layer - no component calls axios directly
  hooks/               Data fetching, polling, incident actions
  services/            Presentation logic derived from backend state
  components/sre/      Feed, detail, AI panels, approval, remediation, verification, demo runner
demo/                  The controlled retry-loop scenario, shared by all telemetry sources
tests/                 Kairon.Backend.Tests, Kairon.SDK.Tests, Kairon.Agent.Tests
docs/
  PROJECT_SUMMARY.md                   This document
  OBSERVABILITY_MIGRATION.md           Phase-by-phase build log for the migration
  OBSERVABILITY_MIGRATION_FINAL_REPORT.md  Detailed migration report + live verification
  REMEDIATION_VERIFICATION.md          A specific historical bug fix, documented with live proof
```

---

## 12. Known limitations (current, honest state)

- No Node.js/Java/Go/PHP SDK — only .NET and Python.
- The KAIRON Agent runs as a console/worker process, not a packaged Windows Service or
  installer.
- No Windows Event Log integration.
- Single operator-identity model (a name recorded per approval) — no multi-tier RBAC.
- The Agent's log tailer supports exactly one rotation scheme (dated-daily files) and a small
  hardcoded pattern set for what counts as a reportable log event, not a configurable rule
  engine.
- The mock AI provider (used whenever no real API key is configured) picks a root cause by a
  fixed priority order when multiple signal types are present on one incident — it tells one
  story, not a genuine multi-source synthesis the way a real LLM would. A real provider (Qwen /
  Gemini / Groq) does not have this limitation.
- `RepeatedErrorsRule` emits one signal per evaluation for whichever endpoint/error-type group
  has the most occurrences — if two sources produce concurrent error bursts on different
  endpoints, only the larger group surfaces as a named signal in that pass (both are still
  correctly stored; this affects which one gets a *label* first).
