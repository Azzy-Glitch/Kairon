# Windows service remediation

KAIRON can remediate a Windows service when, and only when, an operator has explicitly authorized
that exact service as a **remediation target**, the backend's Windows identity has been granted
the minimum rights on that one service, the AI recommends one of the target's allowed operations
for a real incident, and an operator approves it. Every one of those gates is re-checked
immediately before the operation runs.

## Architecture

| Component | Runs as | Role in remediation |
|---|---|---|
| **Kairon.Backend** Windows service | `NT SERVICE\Kairon.Backend` (virtual account, no password, not an administrator, SID unique to KAIRON) | Detection, AI orchestration, approval, **executes** the SCM operation (`sc.exe` query/start/stop) and verifies recovery. Supervises the AI service. |
| Kairon AI service | Child of the backend service (same identity), loopback `127.0.0.1:8001` | Diagnosis and recommendations only. Never executes anything. |
| **Kairon.Agent** Windows service | `NT AUTHORITY\LocalService` | Machine enrollment, heartbeat, and **machine proof** for application telemetry. It does not execute remediation and holds no service-control rights. |
| Kairon.UserAgent | The interactive user (logon task) | Per-session process inventory. No remediation role. |
| Kairon.exe (desktop) | The interactive user | UI host only (WebView2). It reads the operator key the backend publishes and attaches it to UI requests. It does not start or stop the backend. |

Data lives in `%ProgramData%\Kairon\backend` (`data`, `logs`, `config`, `cache`, `backups`,
`operator`). The installer resets that folder's ACL on every install/upgrade: SYSTEM and
Administrators full control, `NT SERVICE\Kairon.Backend` modify, no access for other users.
Only `operator\operator.key` is readable by interactively logged-on users; the backend writes a
fresh 256-bit operator key there on every start (a service restart rotates it).

Remediation is **local-only**: a target must be the machine the backend runs on. A remote target
(`sc.exe \\host`) is refused as `RemoteNotSupported`, because nothing would bind that remote SCM
endpoint to the enrolled Agent.

## What can be targeted

Only stand-alone application services (`SERVICE_WIN32_OWN_PROCESS`) whose executable is outside
the Windows directory. KAIRON always refuses:

- KAIRON's own services (`Kairon*`), so it can never disable its witness or itself;
- drivers, shared `svchost` services and per-user service templates;
- launch-protected (PPL) services;
- executables under `%SystemRoot%` (Windows components such as Spooler or W32Time).

Supported operations: `RunHealthCheck` (read-only), `StartService`, `StopService` (high risk) and
`RestartService` (requires the service to be running). Each target lists the operations it allows.

## Setting up a target

1. **Connect the application.** In KAIRON, *Connect an app* → choose the framework, project,
   environment and service name → generate a pairing code → add the one-line SDK call to the app
   (`Kairon.attach(app, pairing_code="...")` for Python, `AddKaironAsync("...")` for .NET). Run it
   on the same machine as KAIRON so the Agent can confirm its telemetry.
2. **Configure the target.** *Remediation Targets* → *Configure Target*: project, environment and
   logical service (the app's service name), the machine, the Windows service (picked from the
   machine's eligible services), and the operations. Start with `RunHealthCheck` only.
3. **Read the pre-flight.** KAIRON checks, read-only, that the project, credential, machine
   enrollment, local host, heartbeat, service existence and eligibility, and the backend's Windows
   rights are all in place. *Enable* stays unavailable until every blocking check passes.
4. **Grant the minimum rights** if the pre-flight reports *Needs Permission*. It shows the exact
   command; run it once, elevated (it is also at `C:\Program Files\Kairon\tools\remediation`):

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Program Files\Kairon\tools\remediation\Set-KaironServicePermission.ps1" -ServiceName OrdersService -Sid <executor SID from the pre-flight> -Rights Query,Start,Stop
   ```

   The helper adds exactly one allow ACE to that one service for that one SID: `Query`
   (`SERVICE_QUERY_STATUS`), plus `Start` (`SERVICE_START`) and/or `Stop` (`SERVICE_STOP`) as the
   allowed operations require. It never grants configuration, DACL or ownership rights, never
   touches another service, refuses `Kairon*` services and broad group SIDs, and refuses to modify
   any ACE it did not create. KAIRON itself never holds `WRITE_DAC` and cannot grant itself rights.
   Revoke with `-Remove`. Uninstalling KAIRON removes every grant made to the backend service SID.
5. **Enable.** Enabling binds the target to the service's current identity (service type,
   executable path, run-as account). If the service is later recreated around a different
   executable or account, execution is refused (`ServiceIdentityChanged`) until an operator
   re-confirms the target.

A target reports its readiness: *Ready*, *Needs Permission*, *Service Missing*, *Machine Offline*,
*Waiting for app telemetry* (the Agent has not yet confirmed fresh telemetry from the app since the
target last changed), *Needs re-confirmation*, *Blocked* (denylisted or remote), *Disabled* or
*Unsupported*.

## Authorization chain

```text
App telemetry (SDK) ──proof──▶ Agent confirms this machine ──▶ machine-scoped telemetry
  ▶ detection & correlation (scoped to the target's machine)
  ▶ AI diagnosis; recommendations limited to the target's allowed operations, no parameters
  ▶ policy: registered tool, risk ceiling, allowed environment
  ▶ server-generated target fingerprint (project, environment, service, machine, credential,
    host, Windows service, operation, Agent key, service identity)
  ▶ operator approval (operator key + named approver, recorded in the audit trail)
  ▶ immediately before execution: policy re-check, fingerprint re-derived from the live target,
    machine heartbeat ≤ 90 s, Agent confirmation of the credential ≤ 5 min and newer than the
    target, live service probe (exists, eligible, same identity, required right present)
  ▶ sc.exe (fixed verbs, argument array) under NT SERVICE\Kairon.Backend
  ▶ verification: after settling, fresh telemetry for the same project/environment/service/machine
    must show every breached signal back within threshold, plus a fresh SCM state probe
  ▶ Resolved, or Failed / back to approval with the classified reason
```

Telemetry is accepted without a machine scope when it carries no valid Agent proof, or when no
enabled target yet names that credential, project, environment, service and machine (for example,
everything an app sent before its target was created). Such telemetry is visible and alertable, but
it cannot drive Windows service remediation.

Approval is always required: nothing executes an action that an operator has not approved. The
operator chooses which pending alternative to approve; approving one cancels the others. An
approval must start executing within 15 minutes, and a target or identity change after approval
invalidates it.

Failures are classified, never generic: `PermissionMissing`, `ServiceMissing`,
`ServiceIdentityChanged`, `Denylisted`, `DependentServicesRunning`, `ServiceTimeout`,
`ServiceDisabled`, `RemoteNotSupported`, … A Windows `ERROR_ACCESS_DENIED` is reported as a missing
permission, not as an opaque `sc.exe` exit code.

## Verification semantics

SCM success never resolves an incident. Restart/Start actions resolve only when fresh telemetry
from the same machine shows every breached signal recovered and the service is running. A
`StopService` action is containment, not recovery: it requires two fresh "stopped" probes and a
post-operation heartbeat. Missing or insufficient telemetry is *Inconclusive*, never *Passed*.
Timeouts and failures are recorded rather than retried; Windows may already have acted, so inspect
the service before approving another action. An interrupted execution is never replayed
automatically; only an unstarted approval or a pending verification is resumed after a restart.

## Incidents, AI requests and evidence

**One active incident per problem.** Deduplication is deterministic and server-side; the AI never
decides it. A detection signal's correlation key is `project | environment | service`, plus
`| machine` when the telemetry carries an Agent-proven machine. Any non-terminal incident with the
same key absorbs new signals however long it has been idle. Terminal means Resolved, Failed,
Rejected or Cancelled. Only after a terminal state does a later failure open a new incident.

The rule details:

- A rule that re-fires with the same reading changes nothing.
- A new rule, or a changed reading, replaces that rule's row. It never appends duplicates.
- Different projects, environments, services or machines are never merged.
- Find-or-create is serialized inside the backend, so concurrent telemetry cannot create twins.
- `Detection:CorrelationWindowSeconds` is still accepted, but it no longer bounds correlation.

**When the AI is called.** A request goes to the AI only in two cases:

- a newly created incident still in `Detected`, after the evidence-delay window;
- an explicit operator *Re-investigate*, before any remediation is approved.

Repeated or new telemetry on an incident that is already diagnosed never calls the AI again. A
genuinely new reading only marks the diagnosis *stale* for the operator.

Every request is reserved durably first, as an `AiRequestStarted` audit event, and is capped by:

- `AiOrchestration:MaxInvestigationsPerIncident` (default 3);
- `AiOrchestration:MaxInvestigationsPerHour` (default 60, across all incidents).

A blocked request is recorded as `AiBudgetExceeded`. A per-incident gate makes concurrent callers
reserve at most one request for the same incident.

**What the AI sees.** The evidence is bounded and confined to the incident's exact scope: the same
project, environment and service, and the same machine for a machine-scoped incident. That rule
covers metrics, related errors, Agent events and similar past incidents. The evidence includes:

- a per-endpoint breakdown (requests, 5xx/4xx counts, average and maximum latency, the most common
  exception type), so the model can tell exception-driven application errors from slow,
  exception-less dependency failures;
- CPU, memory and latency trends;
- the configured Windows service and its current state, read-only, when one enabled target matches
  the incident's machine.

Host names, machine ids, credentials and fingerprints are never sent.

Every text field passes through the backend redaction first. Redaction masks:

- bearer tokens, cookie headers, and `key=value` / `key: value` pairs whose name contains token,
  secret, password, api key, session id, signature or credential (including compound names such as
  `access_token` or `client_secret`);
- provider and KAIRON credential shapes (`sk-`, `AIza`, `AKIA`, `ghp_`, JWTs, `krn_`/`ksi_`/`pair_`);
- connection-string passwords, user-profile paths, IP addresses and e-mail addresses.

## Operations notes

- **Logs:** `%ProgramData%\Kairon\backend\logs`. Service state: `Get-Service Kairon.Backend`.
- **One executor per target.** Coordination between multiple backends is not implemented.
- **AI provider:** configure it in *Settings → AI configuration*. The key is encrypted with the
  backend service's data-protection key ring and never returned to the UI.
- **Upgrading from a desktop-hosted (pre-service) install:** the installer copies the existing
  `%LOCALAPPDATA%\Kairon` database into the service data root (the original is kept). Secrets that
  were encrypted for the user account cannot be decrypted by the service account, so **re-enter the
  AI provider key** (and any SQL Server connection) once in *Settings*. Remediation targets enabled
  before schema version 11 must be re-confirmed (re-saved) so their service identity is recorded.
- **Validating real execution:** `tools/remediation/New-KaironScmTestService.ps1` creates a
  disposable dependency service for the opt-in real-SCM tests (`KAIRON_REAL_SCM_TEST_SERVICE`);
  `Remove-KaironScmTestService.ps1` removes it. Never point validation at a production or system
  service.
