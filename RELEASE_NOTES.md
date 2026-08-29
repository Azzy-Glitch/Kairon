# Kairon 1.0.0

Release candidate date: 2026-08-29

Kairon 1.0.0 is the first coherent local-first Windows product release: a real WebView2 desktop
shell, a two-component Windows telemetry architecture (a least-privilege `LocalService` Windows
Service plus a per-interactive-session UserAgent), autonomous SRE detection/diagnosis/remediation/
verification, and a normalized, idempotent telemetry pipeline - all installable from a single
Setup.exe on a clean Windows machine with no SQL Server, Docker, Node.js, Python, or .NET runtime
required.

## Included artifacts

- Kairon Windows installer (`Kairon-Setup-1.0.0-win-x64.exe`): desktop shell, backend, Agent,
  UserAgent, and the PyInstaller-packaged AI service, self-contained
- Kairon .NET SDK 1.0.0 (`Kairon.SDK`)
- Kairon Python SDK 1.0.0 (`kairon-sdk`)
- Backend and AI OCI Dockerfiles plus a centralized-deployment compose file
  (`docker-compose.cloud.yml`)

## Product highlights

- **SQLite Local Mode by default** - automatic creation, WAL, online backup before every startup
  upgrade, configurable retention/pruning, and `PRAGMA quick_check` health reporting. SQL Server
  remains fully supported for centralized deployments via `Persistence:Provider`.
- **Two-component Windows telemetry**: the Windows Service (`LocalService`, auto-start, recovery
  policy) handles machine-level heartbeat and lifecycle; a per-interactive-session UserAgent
  (auto-started via a logon-triggered Scheduled Task) reports real per-process CPU/memory/parent
  PID/session data the Service's account cannot see across session boundaries - without ever
  escalating the Service itself to `LocalSystem`.
- **Normalized, idempotent telemetry** (`POST /api/v1/telemetry/events`): durable receipts keyed by
  a caller-supplied `EventId`, auto-registering Project/Application/Environment/Source identity,
  redaction before persistence, and installation-scoped revocable credentials
  (`SdkInstallation`) alongside the existing project-level pairing flow.
- **Autonomous SRE lifecycle**: multi-signal detection (CPU/memory/latency/error-rate/retry-storm/
  request-burst/statistical deviation), correlation, AI-assisted diagnosis (Qwen/Gemini/Groq, with
  a deterministic mock fallback so telemetry and detection never depend on AI availability), typed
  and policy-gated remediation (no arbitrary shell/SQL/PowerShell execution), and resolution only
  after fresh post-remediation telemetry is verified against a pre-remediation baseline.
- A real desktop application: WebView2 hosting the existing React UI, health-gated startup of the
  backend and AI service, graceful shutdown, single-instance enforcement.

## Compatibility

Existing `/api/telemetry/*` ingestion, SDK pairing (`/api/v1/sdk/pair`), Agent registration/
heartbeat, and the frontend's existing pages are all unchanged - the normalized pipeline and
installation-identity tier are additive, not replacements.

## Known release-gate limitations

- SQLite has no dedicated migration history yet (see `backend/Program.cs`'s database-init block) -
  `EnsureCreated()` is used instead of `Migrate()` for SQLite today, which is safe because there is
  no existing SQLite install base yet to upgrade. Before the next schema change ships, a genuine
  SQLite-specific migration history needs to be built.
- Docker image builds are adapted but not verified in this pass if Docker was unavailable on the
  build machine - see the verification report for what was actually confirmed.
- The published, unsigned Release desktop `Kairon.exe` and the installer's own self-extracting stub
  can both be blocked by Windows Smart App Control on a machine where it's enabled; code-signing
  would resolve this but wasn't available in this environment.
