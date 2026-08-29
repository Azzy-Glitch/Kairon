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

**Not verified in this pass, disclosed rather than assumed:**
- The **published desktop shell** (`artifacts\windows-package\Kairon.exe`, self-contained Release
  build) could not be launched in this environment: Windows Smart App Control blocked the
  freshly-built, unsigned binary (`FileLoadException`, "An Application Control policy has blocked
  this file", 0x800711C7) and the block did not clear after several retries and a delay, unlike an
  earlier, transient instance of the same block on a test DLL this session. This is a code-signing/
  environment property, not a defect in the desktop shell's logic — the same `MainForm.cs`/
  `AppPaths.cs` code (unchanged between Debug and Release builds) was fully live-verified end to
  end in the earlier Debug build (native window, WebView2 loading the real UI, health-gated
  backend/AI startup, graceful shutdown, single-instance, immediate relaunch — see the observability
  migration's own verification notes). Signing the published binary would very likely resolve this;
  that step wasn't available here.
- **The actual Windows Service registration** (`sc.exe create`, `net start`) was not run against
  this machine — that's a real, persistent system change (survives reboots, visible in
  `services.msc`) that wasn't taken without it being an explicit, deliberate choice, not a
  side effect of testing a build script.
- **Inno Setup itself is not installed in this environment**, so `Kairon.iss` was not compiled
  into an actual `Kairon-Setup-1.0.0-win-x64.exe`. The script was written and reviewed against the
  real, verified package layout above, but the compile step itself is unverified.
