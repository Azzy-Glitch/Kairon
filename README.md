# Kairon — AIDIP Autonomous AI SRE

An AI site-reliability platform that runs the full incident loop and stops at the one place it
should: before anything executes.

```
OBSERVE → DETECT → CORRELATE → INVESTIGATE → DIAGNOSE → PREDICT → RECOMMEND
       → APPROVE → REMEDIATE → VERIFY → CLOSE
```

The AI diagnoses and proposes. It never acts. Every remediation is a registered, typed tool,
validated by policy and approved by a named human before it can touch anything.

---

## Quick start

### Integrated KAIRON product host

The existing React dashboard is bundled with the control plane when it is published. On Windows,
the publish output contains `KAIRON.exe`; run it with `--desktop` to start the local product host
and open its dashboard:

```powershell
dotnet publish backend/AIDIP.Backend.csproj -c Release -r win-x64 --self-contained false
backend/bin/Release/net10.0/win-x64/publish/KAIRON.exe --desktop --urls http://127.0.0.1:8000
```

Local Mode defaults to managed SQLite persistence and does not require LocalDB. SQL Server remains
available by setting `Persistence__Provider=SqlServer` for centralized mode. The optional AI
service configuration remains unchanged.

### Local Mode persistence and Agent

On first startup KAIRON creates `%LOCALAPPDATA%\KAIRON` with separate `data`, `logs`, `config`,
`cache`, and `backups` directories. The SQLite database is created at
`%LOCALAPPDATA%\KAIRON\data\kairon.db`, and EF migrations run automatically. An advanced/test
deployment can override the file with `Persistence__DatabasePath`; ordinary users should not.

Local persistence is maintained as a production data store. Raw telemetry retention defaults to
14 days, legacy signals to 30 days, terminal incident history to 180 days, and administrative
audit records to 365 days; advanced deployments can change these under `Persistence`. KAIRON uses
SQLite's online backup API rather than copying a live database file and keeps a bounded set of
startup/upgrade backups in the managed `backups` directory.

For recovery, stop KAIRON, preserve the current `data` directory, copy a verified backup from the
managed `backups` directory to `data/kairon.db`, and restart KAIRON. Never overwrite the active
database while the backend is running. The readiness health check performs SQLite `quick_check`
integrity validation and reports failure instead of silently accepting corruption.

Start the zero-code Agent after the backend:

```powershell
dotnet run --project agent/KAIRON.Agent.csproj
```

The Agent creates a durable random machine identity under the KAIRON config directory, registers
through `/api/agent/register`, and sends bounded process/resource heartbeats through the backend
API. It has no EF Core or database provider dependency and never opens `kairon.db`.

Local and centralized persistence use the same `AppDbContext`, services, APIs, incident engine,
SDK, and Agent. Only backend infrastructure registration differs:

```text
Persistence__Provider=SQLite    # default Local Mode
Persistence__Provider=SqlServer # optional centralized mode; uses DefaultConnection
```

Production container images and the external-database Cloud Mode composition are documented in
[`docs/cloud-deployment.md`](docs/cloud-deployment.md). Cloud Mode does not replace or alter the
zero-database-setup SQLite desktop default.

Run all four in separate terminals. Nothing needs an API key.

```bash
# 1. AI service (Python)
cd ai-service
pip install -r requirements.txt
python -m uvicorn main:app --port 8001

# 2. Backend (.NET 10) - applies migrations on start
cd backend
dotnet run --urls http://localhost:8000

# 3. Frontend (React + Vite)
cd frontend
npm install && npm run dev      # http://localhost:5173

# 4. Demo application (optional - see "The demo" below)
cd demo/AIDIP.DemoApp
dotnet run                      # http://localhost:5080
```

Open <http://localhost:5173>, go to **Incident Simulation**, and press **Run Incident Simulation**.

---

## The 90-second demo

The scenario is a controlled retry loop in an order-processing service. Everything after the
button press is real: real telemetry, real detection, real AI investigation, real approval, real
remediation, real verification.

| What you see | What is actually happening |
|---|---|
| Metrics climb: retries, CPU, latency, errors, queue | The demo service is failing and retrying; the SDK reports it |
| One incident appears (not five) | Correlation folds every related signal into a single incident |
| A short pause, then a diagnosis | The incident gathers ~15s of evidence before the AI is asked |
| Root cause + confidence, visually distinct | AI output, explicitly marked as an estimate |
| Predicted impact and risk | Labelled as a prediction, never as fact |
| One recommended action, awaiting approval | Policy already refused anything unregistered |
| You type your name and confirm twice | Approval is deliberate by design |
| Remediation runs, metrics fall | A registered tool calls the demo service's control API |
| Before/after table, then **Resolved** | The backend verified recovery against fresh telemetry |

If the demo application is not running, the backend simulates the same scenario in-process, so the
whole flow works with only the backend and the dashboard.

---

## Architecture

```
                       ┌───────────────────────┐
  Your app  ──SDK──▶   │   .NET Backend        │  ◀──── React dashboard
  (telemetry only)     │   the control plane   │
                       └───────────┬───────────┘
                                   │ bounded evidence
                                   ▼
                       ┌───────────────────────┐
                       │  FastAPI AI service   │
                       │  provider-independent │
                       └───────────┬───────────┘
                                   ▼
                    Qwen  ·  Gemini  ·  Groq  ·  Mock
```

**Who owns what**

| Component | Owns | Must not own |
|---|---|---|
| `sdk/` | Telemetry collection, application integration | EF Core, SQL, AI providers, remediation |
| `backend/` | Detection, correlation, lifecycle, policy, approval, execution, verification, audit | Model-specific behaviour |
| `ai-service/` | Investigation, diagnosis, prediction, recommendation | Persistence, lifecycle, execution, approval |
| `frontend/` | Operator interface | Being the source of truth for incident state |
| `demo/` | The controlled failure scenario | Anything outside itself |

---

## The security invariant

This is the part worth reading before trusting anything else.

The AI can produce exactly one kind of remediation output: **the name of a tool that already
exists**. It cannot supply a command, a script, a path, a query, or a parameter that widens what a
tool does. There is no tool that takes a command string.

Four gates stand between a model's suggestion and an effect:

1. **The registry** — `IRemediationTool` implementations registered at startup. An action naming
   anything else is refused (`unregistered-tool`) before an operator ever sees it.
2. **Policy** — `IRemediationPolicy` checks the registry, an allow/block list, a risk ceiling, and
   the environment. The tool's own declared risk overrides whatever risk the model claimed, so a
   model cannot talk a risky action through by labelling it "low".
3. **Human approval** — required for every action. The operator supplies a named identity, which
   is written to the audit trail, and confirms in a second explicit step.
4. **Re-validation at execution** — policy runs again immediately before the tool is invoked,
   because configuration and incident state can change between approval and execution.

The AI service is an intelligence component. It has no filesystem access, no database, no shell,
and no credentials beyond its own provider key.

Test coverage for this is deliberately adversarial — `RemediationTests` asserts that `rm -rf /`,
`DROP TABLE Incidents` and `exec:curl attacker.example` are all refused, and that a model
explicitly asking for a shell command yields zero executable recommendations.

---

## The two incident types

The codebase has `Incident` and `SreIncident`, and the distinction is deliberate:

- **`Incident`** — one request-scoped telemetry row. One failed HTTP call. This is the pre-existing
  type; it is untouched, which is what keeps the SDK contract and the original screens working.
- **`SreIncident`** — the lifecycle aggregate. Correlates many telemetry rows and metric signals
  into one operator-facing incident with a state machine. It *references* telemetry rows rather
  than copying them.

---

## Detection

Nine deterministic rules run before any AI reasoning. The AI never decides that something is wrong.

| Rule | Fires on |
|---|---|
| `cpu-threshold` / `memory-threshold` | Sustained breach of a configured percentage |
| `latency-threshold` | Sustained slow responses, from metrics or telemetry rows |
| `error-rate-threshold` | Failing fraction of requests above threshold |
| `repeated-errors` | N identical errors on one endpoint |
| `retry-storm` | Retries per minute above threshold |
| `request-burst` | Requests per minute above threshold |
| `queue-backlog` | Queue depth above threshold |
| `metric-deviation` | A step change N sigma from the window baseline, even below the static threshold |

Every threshold is configuration (`Detection` in `appsettings.json`). Signals are deduplicated and
cooled down, then correlated: signals from one service inside the correlation window become **one**
incident, and three or more correlated metrics are titled a *Service Degradation*.

Two behaviours here came from watching the real demo fail:

- **Rates use the observed sample span, not the nominal window.** Twenty seconds of samples divided
  by a two-minute window reads six times too low — exactly when a retry storm is ramping.
- **The leading edge of a ramp is not a sustained breach.** Two samples three seconds apart no
  longer satisfy a twelve-second sustained requirement.

---

## Evidence, staleness, and re-investigation

The AI receives a **bounded** evidence package: incident metadata, recent metrics, related errors,
correlated signals, similar past incidents, and the closed list of available actions. Never the
database. The package is persisted, so an operator can audit exactly what the model saw.

An incident detected on its first signal keeps absorbing evidence afterwards. Two things follow:

- A newly detected incident waits `AiOrchestration:InvestigationDelaySeconds` (default 15s) before
  its first investigation, so the first diagnosis is formed from the correlated picture rather than
  from whichever symptom happened to arrive first.
- If signals still correlate in after a diagnosis exists, the incident is flagged
  `diagnosisStale` and the UI offers **Re-investigate**. The system never silently rewrites a
  conclusion an operator may be reading, and re-investigation is refused once any remediation has
  been approved or executed.

---

## Verification

After remediation the backend compares fresh telemetry against the degraded baseline. Only a
**Passed** verification lets an incident reach Resolved — the frontend cannot decide this.

The "after" window starts at the **end** of the settle period, not at the moment of execution. A
fix takes time to take effect, and averaging from execution folds the whole recovery ramp into the
result, which reads as "still breaching" even when the service has fully recovered.

Three distinct outcomes, and the difference matters:

- **Passed** — enough affected metrics returned within threshold.
- **Failed** — they did not. The incident returns to the operator with the remaining options.
- **Inconclusive** — no settled telemetry arrived. We do not know, and say so.

---

## AI providers

Provider selection is configuration, never code:

```bash
AI__Provider=gemini            # qwen | gemini | groq | mock
AI__Model=gemini-2.5-flash-lite
GEMINI_API_KEY=...             # server-side only
```

If the selected provider has no usable key, the service serves a **deterministic mock** that reads
the real evidence and produces a self-consistent investigation. That is why the whole demo and the
entire test suite run with no paid credentials.

Model output is never trusted: every response is parsed, schema-validated, normalised, and
**rejected** if unusable rather than written into incident state. A recommendation naming a tool
the backend did not offer is dropped at the source.

See `ai-service/.env.example`. No credential is ever logged, returned by an endpoint, or sent to
the frontend.

---

## Running the tests

```bash
dotnet test AIDIP.slnx        # 245 tests: backend + SDK + Agent
cd ai-service && pytest       # 91 tests, no API key required
cd frontend && npm test       # 58 tests
```

Total: **394 tests**, none requiring a credential or a network call.

Several exist because the live demo found a real bug — the detection rate arithmetic, the
sustained-breach guard, the verification window, the diagnosis-staleness gap, and a metric that
rendered as "still breaching" when a recovered service simply stopped reporting it. Those tests are
named and commented for what they are.

---

## Configuration reference

All in `backend/appsettings.json`.

| Section | Controls |
|---|---|
| `Detection` | Thresholds, evaluation window, sustained-breach period, dedup cooldown, correlation window, sweep interval |
| `AiOrchestration` | AI timeout, bounded retries, evidence caps, queue capacity, investigation delay |
| `Remediation` | Master switch, approval requirement, risk ceiling, allow/block lists, permitted environments, execution timeout, per-incident action limit |
| `Verification` | Settle period, comparison window, max wait for settled telemetry, required recovery score |
| `SreSecurity` | Optional operator key on approve/reject/cancel (`RequireOperatorKey`) |
| `DemoEnvironment` | Demo app URL, tick rate, in-process simulator fallback |

---

## API

Everything that existed before still works on the same routes. The SRE layer is additive.

**Versioned platform and normalized telemetry**

| Method | Route | Purpose |
|---|---|---|
| POST | `/api/v1/platform/projects` | Create a project through the operator boundary |
| GET | `/api/v1/platform/projects` | List project identities and inventory counts |
| GET | `/api/v1/platform/applications` | List logical applications and telemetry sources |
| POST | `/api/v1/platform/projects/{id}/credentials` | Issue a project-scoped key once; only its hash is stored |
| DELETE | `/api/v1/platform/projects/{id}/credentials/{credentialId}` | Revoke an ingestion key |
| POST | `/api/v1/telemetry/events` | Ingest up to 200 normalized events in one bounded batch |

Normalized events are durably deduplicated by `eventId`, redacted, associated with project,
application, environment, and source identities, then adapted into the existing metric/incident
pipeline. The legacy `/api/telemetry/incidents` and `/metrics` routes use this same adapter and
remain compatible with existing SDKs.

Local Mode accepts loopback telemetry without manual credentials by default. Centralized mode can
set `PlatformSecurity__RequireTelemetryKey=true`; callers then supply `X-KAIRON-API-Key`. Keys are
project-scoped, stored only as SHA-256 hashes, returned only when created, and revocable.

**Pre-existing** — `/api/analyze-error`, `/api/validate-api`, `/api/predict`, `/api/recommend`,
`/api/history`, `/api/stats`, `/api/telemetry/incidents`, `/api/telemetry/metrics`, `/api/health`

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

---

## Curl walkthrough

```bash
# Start the scenario
curl -X POST http://localhost:8000/api/demo/simulate/start

# Watch for the incident (takes ~25s: detection, then the evidence window)
curl "http://localhost:8000/api/incidents?status=active"

# Read the investigation
curl http://localhost:8000/api/incidents/{id}

# Approve - an operator identity is required and is recorded
curl -X POST http://localhost:8000/api/incidents/{id}/actions/{actionId}/approve \
  -H "Content-Type: application/json" \
  -d '{"approvedBy":"your-name","note":"retry loop is the leading signal"}'

# The full audit trail
curl http://localhost:8000/api/incidents/{id}/timeline
```

---

## SDK integration

Two lines in a host application:

```csharp
builder.Services.AddAIDIP(options =>
{
    options.Endpoint = "http://localhost:8000";
    options.ProjectId = Guid.Parse("...");
    options.ServiceName = "OrderProcessingService";
});

app.UseAIDIP();
```

The SDK never turns an AIDIP outage into an application outage. Sends are bounded, timed out,
cancellation-aware, and fail open: every failure path returns a result rather than throwing. The
outbound queue is bounded and drops the oldest item under pressure rather than growing.

Applications can report the two signals only they know:

```csharp
metrics.RecordRetries(retriesThisInterval);
metrics.ReportQueueDepth(pendingWork.Count);
```

---

## Layout

```
backend/          Control plane. Detection, correlation, orchestration, remediation, verification, audit
  Models/Sre/     Incident aggregate, lifecycle state machine, detection signals
  Services/       Detection · Correlation · Evidence · Remediation · Verification · Audit · Orchestration · Demo
ai-service/       FastAPI intelligence layer
  aidip/          Config, schemas, validation, prompts, service
  aidip/providers/  Qwen · Gemini · Groq · Mock behind one contract
sdk/              Telemetry collection only
frontend/src/
  api/            HTTP service layer - no component calls axios directly
  hooks/          Data fetching, polling, incident actions
  services/       Presentation logic derived from backend state
  components/sre/ Feed, detail, AI panels, approval, remediation, verification, demo runner
demo/             The controlled retry-loop scenario
tests/            Backend and SDK test suites
```
