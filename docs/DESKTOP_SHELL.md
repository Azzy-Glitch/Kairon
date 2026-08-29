# Windows productization: desktop shell, installer, and the two-component Agent

Status: **live-verified on this machine**, except where marked otherwise below. This document is
the reference dozens of code comments across `desktop/`, `agent/`, `installer/`, and
`backend/Controllers/AgentController.cs` already point to by name - it did not exist as a file
until this pass; this is that gap being closed, not a rewrite of anything.

## Why

The platform needed to become something a user can actually install and run on their own Windows
machine, not just a `dotnet run`/`npm run dev` pair of dev servers: a native desktop shell, a
packaged installer, and a Windows Service Agent for zero-code, no-SDK-required monitoring of a
machine and its processes.

## Desktop shell (`desktop/Kairon.Desktop/`)

A WinForms host with a `Microsoft.Web.WebView2` control, not a browser tab. On launch it spawns
the backend and AI service as child processes (self-contained, published alongside it - see
`AppPaths.cs`'s dual-mode discovery: published layout in production, walking up to `Kairon.slnx`
for `dotnet run` in dev), polls their real health endpoints (`/api/health`, `/health`) until both
report healthy, then navigates WebView2 to the running frontend. A named Mutex enforces a single
instance; closing the window gracefully stops both child processes.

## The Windows Service Agent (`agent/Kairon.Agent/`) - `Kairon.Agent`, LocalService

Registered via `Microsoft.Extensions.Hosting.WindowsServices`'s `AddWindowsService()` (additive -
plain `dotnet run` console-mode is unaffected). Runs as `LocalService`: least privilege, and
sufficient for everything this service actually does:

- **Machine heartbeat and identity** (`MachineRegistrationService.cs`): registers a `Machine` row
  once (MachineId derived deterministically from the hostname - SHA-256, first 16 bytes as a
  GUID, so re-registering the same physical machine always resolves to the same identity with no
  local state file to manage) and heartbeats on an interval. This machine-level heartbeat is what
  drives a machine's Online/Offline status - nothing else does.
- **Log tailing and one named process watch** (`LogTailing/`, `ProcessWatch/ProcessWatcher.cs`):
  the original "basic monitoring" this Agent shipped with, reporting start/crash/high-CPU
  *events* for one configured `TargetProcessName` into the existing incident pipeline
  (`AgentEvent`) - distinct from the Machine/DiscoveredApplication *inventory* below.
- **Service lifecycle**: `sc.exe`-based install/upgrade/recovery, all in
  `installer/Kairon.iss`'s Pascal script (`InstallOrReconfigureAgentService`) - validate-before-
  reconfigure on upgrade (refuses to touch a service pointing at a different executable), a
  recovery policy (`restart/5000/restart/15000/none/0`), and SCM verification after every step.

## Why not `LocalSystem`

An earlier pass of this Agent tried to read per-process CPU/memory/start-time for its one watched
process directly inside this LocalService-based heartbeat, and hit a real Windows security
boundary: `Win32Exception: Access is denied` opening a query handle to a process in a different
(interactive) session - confirmed live, not theoretical. The expedient fix at the time was
switching the whole service to `LocalSystem`, which does have that cross-session access. That
traded away least privilege for a problem that has a properly-scoped fix: a second, cooperating
component that runs *as* the interactive user instead of escalating the service that runs as
nobody's session at all.

## `Kairon.UserAgent` (`agent/Kairon.UserAgent/`) - runs as the interactive user

Not a Windows Service - a `WinExe` (no console window) auto-started per user session by a
Scheduled Task (`InstallOrReconfigureUserAgentTask` in `installer/Kairon.iss`): a `LogonTrigger`
with a `Users`-group principal (`S-1-5-32-545`) at `LeastPrivilege`, the standard Windows pattern
for "run unprivileged, on every interactive logon, for whoever logs on" without per-user setup.

Because it runs inside the interactive session, it can read CPU/memory/start-time/parent-PID for
that session's own processes directly - no ACL grants, no elevated account. Scope is deliberately
bounded to **its own session** (`Process.SessionId` matching its own), not every process on the
box - the precise boundary the Windows Service cannot cross, and naturally excludes system-session
noise.

- **CPU is never fabricated**: `CpuSampler`/`CpuBaselineCache` reuse `ProcessWatcher`'s existing
  processor-time-delta formula, generalized to many processes via a `(pid, startTime)`-keyed
  baseline cache. A process is only ever reported once it has a real second sample - never a
  fabricated first-tick value (unlike the older Windows-Service heartbeat path, which has always
  hardcoded `cpuPercent = 0.0` for its one watched process; a pre-existing simplification, left
  alone since it's outside this component's own contract).
- **Parent PID** via a single `CreateToolhelp32Snapshot` native call per poll (`ToolhelpProcessSnapshot.cs`)
  rather than a per-process WMI query - the same mechanism Task Manager uses, available without
  elevation.
- **Failure isolation**: `SessionProcessCollector.CollectSamples` isolates one process's read
  failure (access denied, exited mid-read) to that process alone - the rest of the batch, and the
  heartbeat itself, are unaffected. Unit-tested in `tests/Kairon.UserAgent.Tests/` against a fake
  `IProcessSnapshotSource` (no real OS processes needed to prove this).

## Machine identity: no new pairing needed

Both components derive the same `MachineId` from the same hostname via the identical algorithm
(`SessionProcessCollector.DeriveMachineId`, kept in parity with `MachineRegistrationService`'s
copy and guarded by a dedicated test) and authenticate with the same default `AgentKey` the
Windows Service already registered the machine with. There is no IPC between the two components -
each is an independent HTTP client of the backend (exactly like the Windows Service already was),
which is where correlation actually happens: `POST api/agent/machines/{id}/heartbeat` (Windows
Service) and `POST api/agent/machines/{id}/user-session/heartbeat` (UserAgent) both write into the
same `Machine` row's `DiscoveredApplication` children, tagged by a `Source` column
(`"MachineAgent"` / `"UserAgent"`) so one source's heartbeat can never mark the other's rows
stale.

**Online/offline semantics are intentionally split**: `Machine.LastSeenAt` (driven only by the
Windows Service's own heartbeat) decides the machine's own Online/Offline status;
`Machine.LastUserAgentSeenAt` decides a separate `UserSessionStatus`
(`Online`/`Offline`/`NeverConnected`) shown alongside it in the Machines page. A machine reads as
Online with no interactive user logged in at all, and a user session can go offline (logout)
without the machine itself ever appearing down.

## What's verified live vs. what isn't

Verified on this machine: the Windows Service registered and heartbeating as `LocalService`; the
UserAgent run directly (interactively) against a real demo-app process, with real CPU/memory/PID/
start time/parent PID/session recorded under `Source = "UserAgent"`, while the Machine stayed
Online throughout via the unrelated machine heartbeat; the full backend/UserAgent test suites
green.

**Not independently live-tested**: an actual interactive logout/login cycle and a full Windows
reboot. Both would end the session this work was done in - the Scheduled Task's `AUTO_START`
service policy and `LogonTrigger` are the mechanism that make both cases work, verified by
configuration review rather than by actually disrupting the environment they'd be tested in.
