# Kairon 1.1.0

Cumulative release notes superseding the 1.0.0 candidate notes below. Most of 1.1.0 is
backward-compatible hardening - existing SDK integrations and installations continue to work
unchanged - but this cumulative revision of the notes corrects two claims that no longer hold now
that the machine-enrollment boundary has changed (below): this is **not** a no-breaking-changes
release for a centralized/cloud deployment that had configured the previous Agent-registration
behavior, and Agent registration is explicitly listed as changed, not unchanged, in Compatibility.
A local, single-machine install is unaffected either way - it registers over loopback automatically,
before and after.

## Included artifacts

- Kairon Windows installer (`Kairon-Setup-1.1.0-win-x64.exe`): desktop shell, backend, Agent,
  UserAgent, and the PyInstaller-packaged AI service, self-contained
- Kairon .NET SDK 1.1.0 (`Kairon.SDK`)
- Kairon Python SDK 1.1.0 (`kairon-sdk`)
- Backend and AI OCI Dockerfiles plus a centralized-deployment compose file
  (`docker-compose.cloud.yml`)

## What changed since 1.0.0

- **Restart the connected application, with no setup**: a plain SDK-connected app (FastAPI, Flask,
  Django, ASP.NET Core, ...) can now be remediated without becoming a Windows service. Connect an
  App offers *Let KAIRON restart this app when I approve*; the Remediation Targets wizard offers
  *The application itself*.
  - The Agent identifies the app's process from the OS (the owner of the proof's loopback
    connection).
  - After approval, the KAIRON UserAgent in the app owner's session restarts exactly the command
    that was started, re-verifying the process first.
  - Verification requires a new process of the same app to report recovered telemetry.
  - There is no permission script, no wrapper, and no Windows service.
  - New readiness states: *Waiting for the app* and *User not signed in*.
- **CPU scope is explicit**: both SDKs report the app's *process* CPU as a share of the CPUs it may
  use, and label it `cpu.scope = process`. Incidents and charts now say "App process CPU".
- **SDK parity**: the .NET SDK now keeps one credential file per application (pairing a second
  app no longer replaces the first's), masks credentials in exception text, samples every 5 s, and
  has configurable delivery attempts and shutdown drain. The Python SDK honours `Retry-After` and
  splits oversized batches. Both SDKs report the process identity used for application restart.

- **Backend runs as a Windows service**: the installer registers `Kairon.Backend` under its own
  virtual account `NT SERVICE\Kairon.Backend` (no administrator rights). It hosts the API and
  dashboard, supervises the AI service, and stores data in `%ProgramData%\Kairon\backend` with a
  closed ACL. `Kairon.exe` is now only the UI host, so detection and approved remediation continue
  when the window is closed. The operator key is generated per service start and readable only by
  SYSTEM, Administrators, the service account and interactively logged-on users.
- **Windows service remediation, end to end**: remediation targets are validated by a read-only
  pre-flight (local host, service exists and is an eligible application service, Windows rights for
  each allowed operation), bound to the service's executable identity, local-only, and re-checked
  immediately before every SCM call. Failures are classified (`PermissionMissing`,
  `ServiceMissing`, `ServiceIdentityChanged`, ...). Rights are granted per service by an
  administrator with `tools\remediation\Set-KaironServicePermission.ps1`; uninstall removes them.
- **Simpler onboarding**: pairing codes carry the environment and service chosen in *Connect an
  app*; both SDKs need only the pairing code, reuse it safely across restarts and workers, and
  report connection, machine and telemetry status back to the page.
- **Approval**: the incident page lists every pending alternative and approves only the one the
  operator selects; the required approver name and confirmation wording are explicit.

- **Remediation target management**: operators can create, list, update, and disable remediation
  targets through an authorized management API, on top of the Phase 1 persistence/resolver
  foundation. Environment matching (create, list/filter, update, uniqueness, and runtime
  resolution) is now consistently case-insensitive end to end, using the same normalized
  environment value everywhere - a target's displayed environment name is never altered.
- **SDK pairing and re-pairing**: a lost pairing response, an interrupted confirmation, or a
  crashed application no longer strands a connection. Re-pairing issues a fresh credential,
  confirms it before finalizing, and only then revokes the previous one; the frontend's SDK page
  recovers an in-flight pairing/re-pair across a browser refresh from sanitized, non-secret
  session metadata only.
- **Credential and audit atomicity**: creating or revoking a project API credential and recording
  its audit event now commit as a single atomic operation. If the audit record cannot be
  persisted, the credential mutation is rolled back and the request reports failure; a credential
  mutation is never left in place without its corresponding audit entry, including under
  concurrent requests.
- **AI health and readiness semantics**: the backend and frontend now distinguish three concepts
  that were previously conflated - whether the AI process is running, whether a real provider is
  configured, and whether that configured provider is currently reachable. An AI provider outage
  is reported distinctly from "no provider configured"; the AI service never silently substitutes
  mock analysis for a missing or unreachable provider, and mock mode is only ever used when
  explicitly enabled for local development or testing.
- **Browser-stored pairing state is sanitized on load**: recovered SDK pairing/re-pair state is
  parsed defensively, validated against an explicit allow-list of known fields, and never restores
  a pairing code (including from state written by an older build) - unrecognized or malformed
  fields are stripped and the sanitized value is rewritten back to storage.
- **SDK configuration precedence clarified**: once a connection has been paired and stored, its
  endpoint, project ID, and API key are always used together as one atomic unit. Explicit
  constructor arguments and `KAIRON_ENDPOINT`/`KAIRON_PROJECT_ID`/`KAIRON_API_KEY` apply only
  during first-time onboarding, before anything is stored; to change an already-paired connection,
  redeem a fresh pairing code. This behavior is now identical between the .NET and Python SDKs.
- **SDK transport security**: both SDKs now enforce HTTPS for any non-loopback KAIRON backend.
  Plain HTTP is accepted only for genuine loopback destinations (`localhost`, `127.0.0.0/8`,
  `::1`); a remote plaintext endpoint - whether supplied explicitly, via an environment variable,
  from a stored connection, or returned by a pairing response - is rejected before any request is
  sent, at every point an endpoint enters the SDK.
- **Agent runs with production defaults**: no development/mock behavior is enabled by default in
  the Windows Agent or UserAgent.
- **Installer preserves user data on uninstall**: uninstalling (including a repair/reinstall)
  never deletes the local SQLite database, its WAL/SHM files, or backup history.
- **Machine enrollment is its own trust boundary, not the operator key**: `POST
  /api/agent/register` is now gated by `AgentEnrollmentSecurity:EnrollmentKeys`/
  `X-Kairon-Enrollment-Key` (with current+previous key rotation support), never by the operator
  key that gates remediation approval/execution. This is a genuine behavior change, not merely
  additive hardening: reusing the operator key here previously meant an Agent would need to hold
  the same credential that approves and executes remediation, and could never actually work
  against the packaged desktop, which mints a fresh operator key per launch and gives it only to
  the backend - the installed Agent had no way to learn it and looped on 401 indefinitely. A
  local, single-machine install keeps registering over loopback automatically, with nothing to
  configure; a centralized/cloud deployment must configure `AgentEnrollmentSecurity:EnrollmentKeys`
  (`KAIRON_AGENT_ENROLLMENT_KEY` in `docker-compose.cloud.yml`) and set `Agent:EnrollmentKey` on
  each Agent - the previous `Agent:OperatorKey` setting no longer has any effect.
- **Caddy now authenticates the human operator for a centralized deployment**:
  `deploy/Caddyfile.cloud.example` strips any inbound `X-Kairon-Operator-Key`/`Authorization`,
  requires a dashboard login, and injects the operator key upstream only after that succeeds; SDK
  and Agent routes remain unauthenticated by a human login, exactly as before, since they carry
  their own credentials. A deployment using the previous example file must adopt the new one (and
  its `KAIRON_DASHBOARD_USER`/`KAIRON_DASHBOARD_PASSWORD_HASH` variables) to keep the human
  boundary genuinely enforced rather than merely reverse-proxied.
- **Pairing no longer hands a centralized deployment's SDKs the loopback address**: a new
  `Product:BackendUrl`/`Product:RequirePublicBackendUrl` pair validates (HTTPS, absolute, no
  loopback/userinfo/query/fragment) before a pairing code is consumed or a credential is issued.
  The packaged desktop is unaffected (still `http://127.0.0.1:8000`); a centralized deployment
  should set `Product:BackendUrl` (`KAIRON_PUBLIC_BACKEND_URL`) so paired applications receive a
  real, reachable address instead of one that only ever named the backend's own machine.
- **.NET SDK redirect-safety hardening**: `KaironTelemetryClient`'s constructor is now internal -
  it was previously public and accepted an arbitrary caller-supplied `HttpClient`, which (unlike
  the SDK's own DI wiring and `KaironClient`) had no way to guarantee that client's handler
  wouldn't follow a redirect and replay `X-Kairon-API-Key` at an attacker-controlled Location.
  `KaironPairingClient.PairAsync`/`ConfirmAsync`'s optional test-stub `handler:` parameter now has
  its redirect-following forcibly disabled before use, for the same reason. Neither change affects
  `AddKairon()`/`UseKairon()`/`KaironClient` - the documented, supported ways to use this SDK -
  which were already safe; only a caller directly constructing `KaironTelemetryClient` with its
  own `HttpClient` is affected, and that was never a documented usage pattern.
- **`KaironClient.CaptureException`/`RecordMetric`**: the .NET SDK's standalone entry point - "for
  applications that are not themselves an ASP.NET Core host" - previously had no way to report an
  incident or a metric directly; only automatic background process-metrics collection. These two
  new public methods bring it to parity with the Python SDK's long-standing
  `capture_exception`/`record_metric`.

## Product highlights carried forward from 1.0.0

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
  (`SdkInstallation`) alongside the project-level pairing flow.
- **Autonomous SRE lifecycle**: multi-signal detection (CPU/memory/latency/error-rate/retry-storm/
  request-burst/statistical deviation), correlation, AI-assisted diagnosis (Qwen/Gemini/Groq),
  typed and policy-gated remediation (no arbitrary shell/SQL/PowerShell execution), and resolution
  only after fresh post-remediation telemetry is verified against a pre-remediation baseline.
- A real desktop application: WebView2 hosting the existing React UI, health-gated startup of the
  backend and AI service, graceful shutdown, single-instance enforcement.

## Compatibility

Existing `/api/telemetry/*` ingestion, SDK pairing (`/api/v1/sdk/pair`) request/response shape,
Agent heartbeat, and the frontend's existing pages are all unchanged - no existing SDK integration
needs to change. **Agent registration (`/api/agent/register`) is changed, not unchanged**: see
"Machine enrollment is its own trust boundary" above. A local, single-machine install keeps working
with no configuration changes; a centralized/cloud deployment must configure
`AgentEnrollmentSecurity:EnrollmentKeys` and each Agent's `EnrollmentKey` - the previous
`Agent:OperatorKey` setting is no longer read for registration. A centralized deployment using the
previous `deploy/Caddyfile.cloud.example` should also adopt the new one (see above) to keep the
dashboard genuinely authenticated.

## Upgrade notes (desktop-hosted install to the backend service)

- The installer copies the existing `%LOCALAPPDATA%\Kairon\data\kairon.db` into
  `%ProgramData%\Kairon\backend\data` once, when the service has no database yet. The original is
  left in place as a rollback copy. The copy uses the profile of the account that runs Setup.
- Secrets encrypted for the user account (the AI provider key, a saved SQL Server connection) cannot
  be decrypted by the service account. **Re-enter the AI provider key in Settings → AI
  configuration** (and any SQL Server connection) once after upgrading.
- Remediation targets enabled before this release have no recorded service identity and report
  *Needs re-confirmation*; re-save each one to confirm it.
- Remediation rights must be granted to the new executor identity (`NT SERVICE\Kairon.Backend`);
  grants made to a user account for earlier testing are no longer used and can be revoked with
  `Set-KaironServicePermission.ps1 -Remove`.

## Known limitations

- The published, unsigned Release desktop `Kairon.exe` and the installer's own self-extracting stub
  can both be blocked by Windows Smart App Control on a machine where it's enabled; code-signing
  would resolve this but is not configured in this environment.
- Installer compilation requires Inno Setup 6 (`ISCC.exe`).
- Windows service remediation is local-only (the target must be on the backend's machine), and one
  backend instance must own each target; multi-replica remediation is not supported.
- Machine proof is applied to telemetry whose environment name is at most 50 characters; only
  Development/Staging/Production can be remediation targets, so this does not affect remediation.
