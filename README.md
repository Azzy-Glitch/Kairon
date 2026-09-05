# KAIRON

**Local-first AI-assisted site reliability and incident response for Windows and server deployments.**

KAIRON collects application and machine telemetry, detects deterministic reliability signals,
correlates them into incidents, asks an AI provider for a bounded diagnosis, and guides a human
operator through approval, remediation, and verification.

| Current release | Platform | Core stack | License |
|---|---|---|---|
| **1.0.1** | Windows x64 | .NET 10, React 18/Vite 7, Python/FastAPI | Apache-2.0 |

Every .NET executable produced from a Git checkout includes the source commit in its product
version, for example `1.0.1+d8d46f68...`. This distinguishes builds that share the same release
version.

## What KAIRON does

```text
OBSERVE -> DETECT -> CORRELATE -> INVESTIGATE -> DIAGNOSE -> PREDICT
        -> RECOMMEND -> APPROVE -> REMEDIATE -> VERIFY -> CLOSE
```

- Collects HTTP incidents and CPU, memory, latency, request, error, retry, and queue metrics.
- Discovers Windows machines and processes through a least-privilege Agent and UserAgent.
- Applies deterministic detection rules before AI reasoning is involved.
- Correlates related signals into one service-level incident.
- Supports Qwen/DashScope, Gemini, Groq, and a deterministic built-in mock provider.
- Restricts remediation to registered typed tools; the model cannot provide an arbitrary command.
- Requires human approval and revalidates policy immediately before execution.
- Resolves an incident only after fresh telemetry verifies recovery.
- Provides project pairing and fail-open telemetry SDKs for .NET and Python/FastAPI.
- Exports or permanently deletes locally stored data from the Settings page.

KAIRON does **not** fabricate telemetry on a clean database. With no connected project there are
no service metrics or incidents. Agent-discovered processes are shown as machine inventory; they
are not treated as connected projects and do not create application incidents. The simulator is
opt-in and runs only when an operator starts it from the Demo page or API.

## Product surfaces

The desktop dashboard is organized around the operator workflow:

| Area | Pages | Purpose |
|---|---|---|
| Monitor | Overview, Services, Live telemetry, Machines | Health, service telemetry, raw observations, machine/process inventory |
| Respond | Incidents, Actions, Audit trail | Incident lifecycle, approvals, remediation history, audit evidence |
| Analyse | Insights, Analytics | Diagnoses, confidence, causes, outcomes, and trends |
| Build | Connect an app, Diagnostics, Demo, Settings | SDK pairing, API tools, opt-in simulation, AI and data configuration |

## Architecture

```text
                       +-----------------------------+
 .NET / Python SDK --->|                             |<--- React dashboard
                       |       .NET backend          |     in WebView2
 Windows Agent ------->| detection, lifecycle,       |
 Windows UserAgent --->| policy, persistence, audit  |
                       +--------------+--------------+
                                      |
                           bounded evidence only
                                      |
                                      v
                       +-----------------------------+
                       |      FastAPI AI service     |
                       | Qwen | Gemini | Groq | Mock |
                       +-----------------------------+
```

| Component | Responsibility |
|---|---|
| `desktop/Kairon.Desktop` | Single-instance WinForms/WebView2 shell; starts and health-checks the backend and AI children |
| `backend` | APIs, projects, ingestion, detection, correlation, incident lifecycle, policy, persistence, audit |
| `ai-service` | Provider-independent investigation, diagnosis, prediction, and recommendation |
| `agent/Kairon.Agent` | Auto-start Windows service running as `LocalService`; machine heartbeat and machine-level monitoring |
| `agent/Kairon.UserAgent` | Per-interactive-session process inventory through an at-logon Scheduled Task |
| `frontend` | Operator dashboard served by the backend in production and Vite during development |
| `sdk/Kairon.SDK` | Fail-open ASP.NET Core telemetry client and middleware |
| `sdk-python` | Dependency-light Python client and optional FastAPI/Starlette middleware |
| `demo/Kairon.DemoApp` | Explicitly started retry-loop scenario for end-to-end testing |

The installed desktop application binds the backend to `127.0.0.1:8000` and the AI service to
`127.0.0.1:8001`. It does not require globally installed .NET or Python runtimes.

## Windows installation

The normal installer contains the desktop shell, production frontend, backend, AI service,
Agent, and UserAgent. A runtime machine needs:

- Windows 10 or Windows 11 x64.
- Microsoft Edge WebView2 Evergreen Runtime. It is already present on most current Windows
  installations; Setup warns if it cannot detect it.
- Administrator approval during installation so the Windows service and Scheduled Task can be
  registered.

The default layout is:

```text
C:\Program Files\Kairon\
  Kairon.exe
  backend\Kairon.Backend.exe
  ai\Kairon.AI.exe
  agent\Kairon.Agent.exe
  useragent\Kairon.UserAgent.exe
```

Setup also creates:

- `Kairon.Agent`: automatic Windows service, running as `NT AUTHORITY\LocalService`.
- `\Kairon\UserAgent`: enabled at-logon Scheduled Task for interactive process visibility.
- Start Menu and optional public Desktop shortcuts targeting the installed `Kairon.exe`.
- An official uninstaller at `C:\Program Files\Kairon\unins000.exe`.

Launch KAIRON from the Start Menu or `Kairon.lnk`. The desktop waits for both child services to
report healthy before loading the dashboard.

### Build the Windows installer

Build prerequisites:

- .NET 10 SDK
- Node.js 22 or newer
- Python 3.13 or newer
- Inno Setup 6

From the repository root:

```powershell
.\installer\build-installer.ps1
```

The official script performs a clean frontend build, self-contained `win-x64` publishes,
PyInstaller packaging, and Inno Setup compilation. The result is:

```text
artifacts\installer\Kairon-Setup-1.0.1-win-x64.exe
```

Pass `-SkipInstallerCompile` to produce only `artifacts\windows-package`. Build dependencies are
installed into a disposable environment under `artifacts`; the script does not alter the global
Python environment.

Local installers are unsigned unless a real certificate is configured through
`KAIRON_SIGN_THUMBPRINT` or `KAIRON_SIGN_PFX_PATH`/`KAIRON_SIGN_PFX_PASSWORD`. The build reports
this explicitly. Do not distribute an unsigned build as a trusted production release.

More packaging details are in [installer/README.md](installer/README.md).

## First use

1. Open KAIRON.
2. Go to **Connect an app** and create or select a project.
3. Generate a project credential or one-time pairing code.
4. Add the .NET or Python SDK to the application and use the shown project ID and key.
5. Start the application and send real requests.
6. Confirm the service appears under **Live telemetry** and **Services**.
7. In **Settings**, configure and test an AI provider if real AI analysis is required.

Until a valid provider connection is configured, KAIRON can use deterministic mock analysis. The
Settings page labels a mock fallback as **not connected**; it is not proof that the selected
external provider or key works.

## Connect a Python FastAPI application

Install the SDK from this checkout:

```powershell
pip install -e ".\sdk-python[fastapi]"
```

Minimal `main.py`:

```python
from fastapi import FastAPI
from kairon import Kairon
from kairon.middleware import KaironMiddleware

app = FastAPI()

kairon = Kairon(
    endpoint="http://127.0.0.1:8000",
    project_id="your-project-id",
    api_key="your-project-api-key",
    service="PaymentService",
    environment="Development",
)
kairon.start()

app.add_middleware(KaironMiddleware)


@app.get("/payments/{payment_id}")
async def get_payment(payment_id: str):
    return {"paymentId": payment_id, "status": "authorized"}
```

Run it normally:

```powershell
uvicorn main:app --port 8088
```

The middleware records request status and latency. A lightweight background sampler reports
process CPU and memory plus accumulated request/error counts every five seconds. It ignores health,
metrics, and favicon paths by default. Background jobs can use `capture_exception()` and
`record_metric()` directly.

The client uses a bounded drop-oldest queue and short timeouts. A KAIRON outage does not block or
raise into the monitored application.

See [sdk-python/README.md](sdk-python/README.md) for pairing, tuning, sampling, and manual metrics.

## Connect an ASP.NET Core application

After adding a package or project reference to `Kairon.SDK`:

```csharp
builder.Services.AddKairon(options =>
{
    options.Endpoint = "http://127.0.0.1:8000";
    options.ProjectId = Guid.Parse("your-project-id");
    options.ApiKey = "your-project-api-key";
    options.ApplicationName = "PaymentsApi";
    options.ServiceName = "PaymentService";
    options.Environment = "Development";
});

app.UseKairon();
```

The .NET SDK follows the same fail-open contract as the Python SDK: bounded buffering, independent
timeouts, cancellation support, and no database, AI, or remediation responsibility inside the
host application.

## AI providers

The Settings page can configure a provider, API key, model, and optional custom endpoint without
editing source or restarting KAIRON.

| Provider | Configuration name | Default model |
|---|---|---|
| Alibaba Qwen / DashScope | `qwen` | `qwen-plus` |
| Google Gemini | `gemini` | `gemini-2.5-flash-lite` |
| Groq | `groq` | `openai/gpt-oss-120b` |
| Deterministic fallback | `mock` | internal |

Provider keys are encrypted at rest with ASP.NET Core Data Protection and are never returned to
the frontend after saving. A custom endpoint must be an absolute HTTP(S) URL. HTTPS is required
unless the address is loopback (`localhost`, `127.0.0.1`, or `::1`), preventing a provider key
from being sent over clear-text remote HTTP.

Server and container deployments can configure the same service with environment variables:

```text
AI__Provider=groq
AI__Model=openai/gpt-oss-120b
AI__Endpoint=
GROQ_API_KEY=...
KAIRON_AI_API_KEY=strong-backend-to-ai-transport-key
```

See [ai-service/.env.example](ai-service/.env.example) for every AI setting.

## Detection and incident lifecycle

Detection is deterministic; AI does not decide whether a metric breached a threshold. Current
rules cover:

- CPU and memory thresholds
- latency threshold
- error-rate threshold
- repeated errors
- retry storms
- request bursts
- queue backlog
- statistical metric deviation

Signals are deduplicated and correlated by service and time window. The AI receives a bounded
evidence package containing incident metadata, recent metrics, related errors, correlated signals,
similar incidents, and the closed list of available actions. The evidence is persisted for audit.

New evidence can mark an existing diagnosis stale. An operator may re-investigate until a
remediation has been approved or executed. Verification has three outcomes:

- **Passed**: enough affected metrics recovered; the incident may resolve.
- **Failed**: the metrics did not recover.
- **Inconclusive**: no adequate settled telemetry arrived.

## Remediation safety

The AI can recommend only a named tool already registered by the backend. It cannot submit a shell
command, script, SQL statement, filesystem path, or arbitrary arguments that expand a tool's
authority.

Four gates precede execution:

1. The registered `IRemediationTool` catalog.
2. Policy validation: enablement, allow/block lists, risk, environment, and limits.
3. Named human approval recorded in the audit trail.
4. Policy revalidation immediately before execution.

By default, remediation is permitted only in `Development`, `Demo`, and `Staging`; `Production` is
not in `Remediation:AllowedEnvironments`.

## Data and persistence

The packaged desktop uses SQLite by default. A centralized deployment can select SQL Server with
`Persistence:Provider=SqlServer` and `ConnectionStrings:DefaultConnection`.

Managed local data is stored beneath:

```text
%LOCALAPPDATA%\Kairon\
  data\kairon.db
  backups\
  logs\
  config\dataprotection-keys\
  cache\
  webview2\
```

Machine and UserAgent credentials are stored separately under
`%ProgramData%\Kairon\config` with hardened ACLs.

SQLite uses WAL mode, foreign keys, a busy timeout, versioned schema migration, a consistent
online backup before startup upgrades, retention maintenance, and health integrity checks.

The **Settings > Your data** controls provide:

- **Download my data**: creates a consistent SQLite snapshot and downloads it to the device.
- **Delete all data**: requires typing `DELETE`, removes all database records and managed backups,
  then keeps the empty schema ready for continued use.

Deletion is currently global to the local KAIRON database; there is no per-project delete API.
Export is available only when SQLite is the active provider.

The official Windows uninstaller removes the registered service/task, installed binaries, the
managed SQLite database, and managed KAIRON backup files. Export important data before uninstalling.
It does not recursively delete arbitrary source, repository, export, credential, log, or unrelated
directories.

## Security model and deployment boundaries

- The installed WebView2 host generates an ephemeral operator key and attaches it to `/api/*`
  requests at the native network boundary. It is not embedded in JavaScript, a URL, or browser
  storage.
- The backend-to-AI transport key is also generated per desktop launch.
- Project API keys are returned once and stored only as SHA-256 hashes with revocation metadata.
- AI-provider keys are encrypted at rest and omitted from every API response and log.
- Normalized and legacy ingestion endpoints are rate-limited and payload-sized.
- Custom AI endpoints require HTTPS except for loopback development endpoints.
- The Agent service runs as `LocalService`, not `LocalSystem`.
- Remediation has no arbitrary command execution tool and always requires approval by default.

Important deployment distinction: the local desktop configuration currently uses
`PlatformSecurity:RequireTelemetryKey=false` for loopback development convenience. Projects must
still exist and be active, but the project key is not enforced in this mode. The cloud compose
configuration sets telemetry-key enforcement to `true`. Do not expose the desktop backend beyond
loopback or reuse its defaults for an internet-facing deployment.

Agent registration is a bootstrap endpoint; subsequent Agent and UserAgent heartbeats require
their scoped machine credentials. A centralized deployment should place bootstrap registration
behind a trusted provisioning/network boundary before exposing the backend.

## Local development

Use three terminals from the repository root. The values below are development-only transport
keys and must be replaced outside a local machine.

### 1. AI service

```powershell
Set-Location ai-service
python -m pip install -r requirements.txt
$env:KAIRON_AI_API_KEY = 'kairon-local-development-ai-transport-key'
python -m uvicorn main:app --port 8001
```

### 2. Backend

```powershell
$env:AiService__ApiKey = 'kairon-local-development-ai-transport-key'
$env:SreSecurity__OperatorKey = 'kairon-local-development-operator-key'
$env:Persistence__DatabasePath = Join-Path $env:TEMP 'kairon-dev\kairon.db'
dotnet run --project backend\Kairon.Backend.csproj -- --urls http://127.0.0.1:8000
```

### 3. Frontend

```powershell
Set-Location frontend
$env:KAIRON_OPERATOR_KEY = 'kairon-local-development-operator-key'
npm ci
npm run dev
```

Open `http://127.0.0.1:5173`. Vite binds to loopback and proxies `/api` to the backend while
injecting the operator key server-side.

### Optional controlled scenario

```powershell
dotnet run --project demo\Kairon.DemoApp\Kairon.DemoApp.csproj
```

Then explicitly start the scenario from the Demo page. It is not seeded or started automatically.

## Configuration

Primary backend settings are in [backend/appsettings.json](backend/appsettings.json) and can be
overridden with standard .NET environment-variable syntax (`Section__Property`).

| Section | Controls |
|---|---|
| `Persistence` | SQLite/SQL Server, database path, retention, maintenance, backup count |
| `PlatformSecurity` | project telemetry-key enforcement and header name |
| `SreSecurity` | operator-key enforcement and header name |
| `AiService` | AI-service URL, transport key, timeout, response limit, mock mode |
| `Detection` | rules, thresholds, windows, cooldown, correlation, evaluation interval |
| `AiOrchestration` | evidence limits, queue, timeout, delay, retry and investigation budgets |
| `Remediation` | enablement, approval, risk, tools, environments, timeout, action limit |
| `Verification` | settle time, comparison window, sample count, recovery score |
| `DemoEnvironment` | controlled scenario endpoint, timing, local fallback |
| `Cors` | explicitly allowed development origins |

Never commit real operator, telemetry, Agent, AI transport, or provider credentials.

## API overview

The backend exposes OpenAPI/Swagger in development. Main route groups are:

| Route | Purpose |
|---|---|
| `/api/health`, `/api/health/status` | process, database, AI, and overall health |
| `/api/telemetry/*` | legacy SDK incident and metric ingestion/readback |
| `/api/v1/telemetry/events` | normalized idempotent event ingestion |
| `/api/v1/projects/*` | projects and revocable project credentials |
| `/api/v1/sdk/pair` | one-time SDK pairing redemption |
| `/api/v1/platform/*` | applications and SDK installation identities |
| `/api/agent/*` | machine registration, authenticated heartbeats, inventory |
| `/api/incidents/*` | SRE feed, evidence, timeline, investigation, approval, cancellation |
| `/api/v1/ai-config/*` | provider configuration, connection testing, model listing |
| `/api/v1/data/export`, `/api/v1/data` | SQLite export and confirmed global deletion |
| `/api/analyze-error`, `/api/validate-api`, `/api/predict`, `/api/recommend` | diagnostic utilities |
| `/api/demo/*` | explicitly controlled simulation endpoints |

Sensitive reads and control-plane writes require the operator key when enforcement is enabled.

## Tests and verification

```powershell
dotnet test Kairon.slnx --configuration Release

Set-Location frontend
npm ci
npm test -- --run
npm run build

Set-Location ..\ai-service
python -m pip install -r requirements.txt
pytest -q

Set-Location ..\sdk-python
python -m pip install -e ".[test]"
pytest -q
```

The latest full local run for version 1.0.1 passed **737 tests**:

- 438 .NET tests
- 121 frontend tests
- 139 AI-service tests
- 39 Python SDK tests

CI runs the solution tests, frontend tests/build/audit, Python tests/package audit/build, NuGet
vulnerability inspection, SDK packaging, and both Docker image builds. The Windows installer
workflow runs manually and for `v*` tags.

## Centralized deployment

`docker-compose.cloud.yml` builds the backend and AI service for a server deployment. It requires:

- a SQL Server connection string;
- a strong backend-to-AI service key;
- a strong operator key;
- an explicit allowed frontend origin;
- provider credentials when real AI analysis is enabled.

```powershell
docker compose -f docker-compose.cloud.yml up --build
```

The compose configuration enables telemetry-key enforcement. It does not include a SQL Server
container; point `KAIRON_SQL_CONNECTION` at a separately managed SQL Server.

## Repository layout

```text
agent/                    Windows Agent service and interactive UserAgent
ai-service/               FastAPI AI service and provider adapters
assets/                   Product icon and branding source assets
backend/                  .NET control plane, APIs, persistence, migrations
demo/                     Opt-in controlled scenario application
desktop/                  WinForms/WebView2 desktop shell
docs/                     Architecture, migration, and verification notes
frontend/                 React/Vite operator dashboard
installer/                Inno Setup definition and complete build script
sdk/Kairon.SDK/           .NET telemetry SDK
sdk-python/               Python/FastAPI telemetry SDK
tests/                    .NET unit, integration, and acceptance tests
.github/workflows/        Cross-platform CI and Windows installer workflow
```

## Current limitations

- Local development installers are unsigned unless real code-signing material is configured.
- WebView2 is an external Windows prerequisite.
- The desktop owns ports `8000` and `8001`; it refuses to start if another process already uses
  either port.
- Local desktop telemetry-key enforcement is disabled by default and is not a safe internet-facing
  configuration.
- Initial Agent registration assumes a trusted provisioning boundary.
- Data deletion is global; per-project deletion is not implemented.
- External AI-provider connectivity still depends on the user's provider account, key, model,
  region, network, and endpoint. Passing mock-mode tests does not verify those external systems.
- The current frontend production bundle triggers Vite's `>500 kB` chunk warning, and the desktop
  build emits a non-fatal WebView2/`WindowsBase` reference-version warning.
- Interactive logout/login and full-reboot behavior is configured through the service and at-logon
  task but cannot be exercised without ending the current Windows session.

## Versioning

The release version is mirrored in:

- `VERSION`
- `Directory.Build.props`
- `frontend/package.json`
- `sdk-python/pyproject.toml`
- `installer/Kairon.iss`

The installer build validates the shared .NET/Inno Setup version before packaging. Update all
version sources together for a release and build from a clean, reviewed commit.

## License

KAIRON is licensed under the [Apache License 2.0](LICENSE).

Copyright (c) 2026 Abdul Aziz Qureshi.
