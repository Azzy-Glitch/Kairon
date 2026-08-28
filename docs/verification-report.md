# KAIRON product verification report

Date: 2026-08-28  
Target: 1.0.0 productization candidate  
Host: Windows 11 x64, .NET 10.0.400, Node 22, Python 3.14

## Automated regression results

| Component | Command | Result |
|---|---|---|
| Backend | `dotnet test AIDIP.slnx --no-restore` | 212 passed |
| .NET SDK | same solution run | 42 passed |
| Agent | same solution run | 2 passed |
| Frontend | `npm test -- --run` | 59 passed |
| AI service | `pytest -q` | 91 passed; one upstream deprecation warning |
| Python SDK | `pytest -q` | 8 passed; one upstream deprecation warning |

Total: 414 passing tests, zero failing tests. NuGet vulnerability metadata lookup emitted `NU1900`
when the sandbox could not reach NuGet; compilation, restore state, and tests succeeded.

## Product and persistence path

- The Windows packaging pipeline built the React production bundle, self-contained x64 backend and
  Agent, one-file AI gateway, and Inno Setup installer successfully.
- Final versioned installer artifact: `KAIRON-Setup-1.0.0-win-x64.exe`, 87,949,473 bytes,
  SHA-256 `0C5919DFD042CF6D156583EAD4BD56B78F473ADC7E204D403DB2D0F86A62DB85`.
- The installed `KAIRON.exe` served the bundled UI with HTTP 200 and reported readiness healthy.
- Startup created and migrated a fresh SQLite database without interaction.
- Installed AI gateway returned `healthy` in deterministic mock mode.
- A telemetry incident was ingested through the backend API and read back from SQLite.
- After terminating and restarting the installed backend, the same incident remained available.
- Restart created a consistent online SQLite startup/upgrade backup.
- Silent uninstall returned exit code 0, removed installed product files, and preserved the separate
  data/database directory.
- Agent failure-path execution remained alive with the backend unavailable and emitted bounded retry
  warnings instead of failing the monitored application.

## Cloud path

- `docker compose -f docker-compose.cloud.yml config --quiet` passed with required test placeholders.
- The production AI image built, ran as non-root UID/GID 999, and returned `healthy:mock`.
- The backend image's Node/frontend stage built successfully in Docker.
- The native backend build and packaged Linux-independent application tests passed. The local Docker
  daemon repeatedly stalled downloading the final large .NET SDK base layer, so a completed local
  backend-image runtime smoke test is not claimed. CI repeats both image builds from a clean checkout.
- SQL Server provider registration and existing compatibility remain compiled and covered by the
  backend suite. No external SQL Server was provisioned in this local verification environment, so
  a live centralized-database migration is not claimed.

## Host limitation

The isolated installer copied the complete product and generated a working uninstaller, but its
Windows Service registration step returned installer code 4 because this execution host does not
grant a service-control-manager administrator token. No `KAIRON.Agent` service was left behind.
The installer script itself compiled, and the same packaged Agent passed console lifecycle and
backend-unavailable tests. Windows Service creation/startup must be repeated on the clean elevated
Windows release VM before signing off the clean-machine acceptance checkbox.

## Verified safety boundaries

- Backend remains the only persistence owner; Agent, both SDKs, AI service, and frontend contain no
  SQLite/EF database access path.
- Pairing reuse, expiry/revocation, wrong scope, authentication, redaction, malformed telemetry,
  bounded queues, controlled remediation, and post-action verification are exercised by the passing
  regression suites.
- No provider credential is included in the executable, container images, installer, or repository.
- Unrestricted AI-generated shell execution remains unavailable.
