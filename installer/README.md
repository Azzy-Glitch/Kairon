# Kairon Windows installer

Adapted from Azzy's productization branch (`origin/main` / `codex/productization-windows-20260828`)
onto this codebase's actual layout — see `docs/DESKTOP_SHELL.md` for why this is an adaptation
rather than a merge.

## What changed from the source branch

- The source branch's `--desktop` flag opened a system browser
  (`ProductDashboardLaunchService` did `Process.Start(url, UseShellExecute: true)`). This pass
  built a real native shell (`desktop/Kairon.Desktop/`, WebView2), so `[Icons]`/`[Run]` in
  `Kairon.iss` now launch `Kairon.exe` directly with no flag.
- Project/file names updated: `backend/Kairon.Backend.csproj` (was `AIDIP.Backend.csproj`),
  `agent/Kairon.Agent/Kairon.Agent.csproj` (was `agent/KAIRON.Agent.csproj`),
  `ai-service/entrypoint.py` (was `launcher.py` — same purpose: `main.py` stays an importable
  ASGI module for local dev, this is what PyInstaller actually packages).
- No dedicated `ai-service/.venv` exists in this repo; the build script falls back to system
  `python` if one isn't present.
- The Windows Service registration Pascal Script (validate-before-reconfigure on upgrade,
  recovery policy, SCM verification at every step) is unchanged in substance — it didn't need
  adapting, just renaming.

## Build

```powershell
.\installer\build-installer.ps1
```

Publishes the desktop shell, backend, and Agent (all self-contained `win-x64`), packages the AI
service with PyInstaller, and compiles the installer with Inno Setup 6 if it's found. Pass
`-SkipInstallerCompile` to stop after staging `artifacts\windows-package\` without requiring Inno
Setup at all.

## What's actually been verified in this pass, and what hasn't

Verified, live, in this environment:
- The full publish pipeline runs end to end with no errors: frontend build → backend publish
  (with the React UI bundled into its own `wwwroot`) → Agent publish → desktop shell publish →
  AI service PyInstaller packaging. Confirmed output layout matches exactly what
  `desktop/Kairon.Desktop/AppPaths.cs` looks for (`{app}\Kairon.exe`,
  `{app}\backend\Kairon.Backend.dll`, `{app}\agent\Kairon.Agent.exe`, `{app}\ai\Kairon.AI.exe`).
- The published backend (`Kairon.Backend.exe`, both invoked directly and via
  `dotnet Kairon.Backend.dll`) starts, connects to SQL Server, and answers `/api/health` — twice,
  independently.
- The packaged AI service (`Kairon.AI.exe`) actually starts a real Uvicorn server and answers
  `/health` — this took a real fix: `main.py` only defines the ASGI `app` for an external
  `uvicorn main:app` process to import, so a first PyInstaller build of `main.py` directly
  produced an executable that initialized and exited immediately without ever binding a port.
  `entrypoint.py` adds the missing `if __name__ == "__main__": uvicorn.run(...)` — confirmed
  needed by finding the source branch's own `launcher.py` doing the same thing after hitting
  this independently.
- `Microsoft.Extensions.Hosting.WindowsServices` / `AddWindowsService()` was added to the Agent
  and confirmed **not** to change its plain console/`dotnet run` behavior at all (still starts,
  tails logs, watches the target process identically to before).
- **The Windows Service registration is real, not just written, and the compiled installer has
  actually been run** — a genuine `C:\Program Files\Kairon` install exists on this machine, put
  there by a versioned `Kairon-Setup-<version>-win-x64.exe` itself (confirmed via the service's own registered
  `BINARY_PATH_NAME`), after the user disabled Windows Smart App Control (their own decision — it
  was blocking the installer's own self-extracting temp executable, not just the published
  desktop shell).
- **Inno Setup 6 is installed** (`winget install JRSoftware.InnoSetup`) and compilation succeeds:
  `ISCC.exe` produces a real versioned `Kairon-Setup-<version>-win-x64.exe` at `artifacts\installer\`.

**A later pass added a second component, `KAIRON.UserAgent`** — see `docs/DESKTOP_SHELL.md` for
the full architecture. In short: `Kairon.Agent` reverted to `LocalService` (least privilege — an
earlier pass had switched it to `LocalSystem` to work around limited cross-session process
visibility; that trade-off is no longer needed now that a separate, per-interactive-session
component handles per-process telemetry instead). `Kairon.iss` now also installs `KAIRON.UserAgent`
and registers it as a logon-triggered Scheduled Task (`Users`-group principal, least privilege) —
live-verified on this machine: the task creates, runs, and the UserAgent reports real per-process
CPU/memory/PID/parent-PID/session data, while the Machine's own Online status stays independent
and unaffected. Three real bugs were found and fixed during this verification, all now covered by
tests: an EF Core unique-index collision when both components report the same physical process; a
crash-prone exception filter in three of the Agent's background loops that treated *any*
cancellation-shaped exception (including an aborted HTTP connection, not just a genuine shutdown)
as fatal; and a `schtasks /Create /XML` encoding quirk (an XML prolog declaring an encoding that
doesn't match the file's actual bytes is rejected — `Kairon.iss` now writes a declaration-less,
plain UTF-8 file, which is what actually gets produced and what schtasks accepts).

**Not verified in this pass, disclosed rather than assumed:**
- **An actual interactive logout/login cycle and a full Windows reboot** were not performed —
  both would end the very session this work was done in. The Scheduled Task's `LogonTrigger` and
  the service's `AUTO_START` are the mechanism that make both cases work; verified by
  configuration review, not by disrupting the environment they'd be tested in.
- **The published desktop shell has not been re-tested since Smart App Control was disabled** in
  this pass — the block that stopped it earlier was a Smart App Control property of this machine
  (now off), not a defect in the shell's own code (already fully verified end-to-end in an earlier
  Debug build), but re-launching the Release build to confirm wasn't repeated here.
