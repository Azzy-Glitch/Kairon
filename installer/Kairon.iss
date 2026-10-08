; Adapted from Azzy's productization branch (origin/main / codex/productization-windows-20260828),
; updated for the real desktop shell built in this pass (docs/DESKTOP_SHELL.md). The source
; branch's [Icons]/[Run] launched "KAIRON.exe --desktop --urls ..." - the backend's own exe with a
; flag that opened a system browser. There is now a real native desktop/Kairon.Desktop project
; (Kairon.exe at the package root) that hosts the UI in WebView2 instead, so no flag is needed;
; it starts the backend and AI service itself and waits for both to report real health before
; showing a window. The Windows Service registration Pascal Script below (backend/agent/ai package
; layout, validate-before-reconfigure, recovery policy, SCM verification at every step) is
; unchanged in substance from the source branch - it did not need adapting.
; Architecture (1.1.0+): the backend runs as the Kairon.Backend Windows service under its own
; virtual account NT SERVICE\Kairon.Backend (least privilege; remediation rights are granted per
; target service to that SID only). It supervises the packaged AI service and keeps its data under
; %ProgramData%\Kairon\backend. Kairon.exe is only the UI host; it no longer starts the backend.
#define MyAppName "Kairon"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "Kairon"
#ifndef PackageRoot
  #define PackageRoot "..\artifacts\windows-package"
#endif

[Setup]
AppId={{9BE9042A-B83A-4BED-9651-7C5B7086C9AF}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright=Copyright (c) 2026 Abdul Aziz Qureshi
DefaultDirName={autopf}\Kairon
DefaultGroupName=Kairon
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=Kairon-Setup-{#MyAppVersion}-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\Kairon.exe
SetupIconFile=..\assets\kairon-icon.ico
VersionInfoVersion={#MyAppVersion}

[InstallDelete]
; Runs BEFORE [Files] is copied (unlike [UninstallDelete], which this installer deliberately does
; not use - see the remarks further down about never deleting the user's database on uninstall).
; Everything under wwwroot is installer-owned build output, never user data: Vite content-hashes
; each bundle's filename, so an upgrade ADDS index-<newhash>.js and leaves the previous release's
; index-<oldhash>.js behind forever - ignoreversion overwrites matching names, it never removes
; orphans. That accumulation is not just dead bytes: it lets a browser/WebView2 cache still holding
; the OLD index.html keep resolving that old bundle successfully, so an upgraded install silently
; renders the previous release's UI (confirmed live on a 1.0.1 -> 1.1.0 upgrade). Clearing the
; directory first makes a stale shell fail loudly (404 -> reload) instead of silently succeeding.
Type: filesandordirs; Name: "{app}\backend\wwwroot"

[Files]
; The desktop shell publishes to the package root; backend/agent/ai each publish to their own
; subfolder (matches desktop/Kairon.Desktop/AppPaths.cs's expected layout exactly).
Source: "{#PackageRoot}\*"; DestDir: "{app}"; Excludes: "backend\*,agent\*,ai\*,useragent\*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageRoot}\backend\*"; DestDir: "{app}\backend"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageRoot}\agent\*"; DestDir: "{app}\agent"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageRoot}\useragent\*"; DestDir: "{app}\useragent"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageRoot}\ai\*"; DestDir: "{app}\ai"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
; No Permissions clause here: Inno's Permissions directive only ever ADDS grants, it never removes
; an inherited or previously-installed one, so it cannot be trusted to narrow this folder's ACL by
; itself (confirmed live - a folder that has been through an older installer revision using
; `Permissions: users-modify` kept that grant across upgrades). GrantAgentCredentialAcl below resets
; this folder's whole ACL from scratch on every install/upgrade instead.
Name: "{commonappdata}\Kairon\config"

[Icons]
Name: "{group}\Kairon"; Filename: "{app}\Kairon.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Kairon"; Filename: "{app}\Kairon.exe"; Tasks: desktopicon; WorkingDir: "{app}"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Run]
Filename: "{app}\Kairon.exe"; Description: "Launch Kairon"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; Remove only the remediation grants made to NT SERVICE\Kairon.Backend (its SID is unique to KAIRON),
; then stop (waiting) and delete the backend service. Its data under %ProgramData%\Kairon\backend is
; kept, exactly like the database policy below.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\tools\remediation\Remove-KaironBackendServiceGrants.ps1"""; Flags: runhidden waituntilterminated; RunOnceId: "SweepBackendGrants"
Filename: "{sys}\net.exe"; Parameters: "stop Kairon.Backend"; Flags: runhidden waituntilterminated; RunOnceId: "StopBackend"
Filename: "{sys}\sc.exe"; Parameters: "delete Kairon.Backend"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteBackend"
Filename: "{sys}\sc.exe"; Parameters: "stop Kairon.Agent"; Flags: runhidden waituntilterminated; RunOnceId: "StopAgent"
Filename: "{sys}\sc.exe"; Parameters: "delete Kairon.Agent"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteAgent"

[UninstallDelete]
; The only uninstall-time deletion: the per-start operator key (a credential for a service that no
; longer exists after uninstall). This is not user data; the database policy below is unchanged.
Type: filesandordirs; Name: "{commonappdata}\Kairon\backend\operator"

; Deliberately no other [UninstallDelete] entries: a normal uninstall must never delete kairon.db, its
; WAL/SHM files, or anything under Kairon\backups - those are the user's actual incident/telemetry/
; remediation history, not installer-owned state. [UninstallDelete] entries run on every ordinary
; uninstall (Add/Remove Programs, or re-running this same installer to repair/reinstall) with no
; separate confirmation step, so listing the database here would silently destroy production data
; on what a user reasonably expects to be a recoverable, undo-able action - reinstalling KAIRON
; afterward would then start from an empty database instead of picking up where they left off. A
; genuine "erase all local data" operation already exists in-app (DataManagementController /
; SettingsPage.jsx's "Delete all data" action) as an explicit, deliberate operator choice - that is
; where destructive data removal belongs, never bundled into uninstall.

[Code]
const
  AgentServiceName = 'Kairon.Agent';
  BackendServiceName = 'Kairon.Backend';
  UserAgentTaskName = 'Kairon\UserAgent';
  ErrorServiceDoesNotExist = 1060;
  ErrorServiceNotActive = 1062;
  ErrorServiceAlreadyRunning = 1056;

function AgentExecutablePath: String;
begin
  Result := ExpandConstant('{app}\agent\Kairon.Agent.exe');
end;

// Kairon.Desktop hosts the UI in a WebView2 control (Microsoft.Web.WebView2 package). Windows 11
// and current Windows 10 ship the Evergreen WebView2 Runtime pre-installed, so this has never
// failed on a development machine - but this installer never actually verifies it, so a
// locked-down or older Windows image without it would pass installation cleanly and then fail
// opaquely the first time Kairon.exe tries to create the WebView2 environment. Checked for real,
// not assumed: the registry key/value below is Microsoft's own documented detection method
// (https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution#detect-if-a-suitable-webview2-runtime-is-already-installed),
// covering both machine-wide and per-user Evergreen installs across both registry views.
function IsWebView2RuntimeInstalled: Boolean;
var
  Version: String;
  WebView2ClientKey: String;
begin
  WebView2ClientKey := '\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  Result :=
    RegQueryStringValue(HKLM64, 'SOFTWARE' + WebView2ClientKey, 'pv', Version) or
    RegQueryStringValue(HKLM32, 'SOFTWARE\WOW6432Node' + WebView2ClientKey, 'pv', Version) or
    RegQueryStringValue(HKCU, 'SOFTWARE' + WebView2ClientKey, 'pv', Version);
end;

function NormalizeImagePath(Value: String): String;
begin
  Result := Trim(Value);
  if (Length(Result) >= 2) and (Result[1] = '"') and
     (Result[Length(Result)] = '"') then
    Result := Copy(Result, 2, Length(Result) - 2);
end;

// RB-009: runs before the wizard shows its first page - the earliest point to tell the operator
// about a missing prerequisite, rather than after they have already clicked through the whole
// install and Setup reports success while the desktop application cannot actually start (it fails
// opaquely the first time Kairon.exe tries to create the WebView2 environment). This is a
// registry-based heuristic (Microsoft's own documented one, but still a heuristic), so an
// unconditional hard block risks refusing an install on a false negative - the runtime installed
// through some path these three registry checks do not cover. The operator is the one who can
// actually tell the difference, so on a negative result THEY are asked to confirm before
// continuing rather than Setup silently deciding either way: OK proceeds anyway (the operator
// asserts WebView2 really is present, or will install it before first launch), Cancel aborts
// Setup outright - a genuinely unsuccessful, non-misleading installation result, not a warning
// that changes nothing.
function InitializeSetup: Boolean;
begin
  if IsWebView2RuntimeInstalled then
  begin
    Result := True;
    exit;
  end;

  Result := (IDOK = MsgBox(
    'Kairon uses the Microsoft Edge WebView2 Runtime to display its interface, and it was not ' +
    'detected on this machine. Without it, Kairon will install but its window will fail to open.'#13#10#13#10 +
    'Windows 11 and most current Windows 10 installations already include it, so this may be a ' +
    'false alarm. If you are not certain it is installed, click Cancel, install the "Evergreen ' +
    'Bootstrapper" from Microsoft''s WebView2 download page, then run Setup again.'#13#10#13#10 +
    'Click OK only if you know WebView2 is already installed on this machine.',
    mbError, MB_OKCANCEL));
end;

function RunServiceControl(const Arguments, Action: String; var ResultCode: Integer): Boolean;
begin
  Log(Format('Kairon Agent: %s.', [Action]));
  Result := Exec(ExpandConstant('{sys}\sc.exe'), Arguments, '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
  if Result then
    Log(Format('Kairon Agent: %s completed with exit code %d.', [Action, ResultCode]))
  else
    Log(Format('Kairon Agent: could not launch sc.exe for %s (Win32 error %d: %s).', [Action, DLLGetLastError, SysErrorMessage(DLLGetLastError)]));
end;

function QueryAgentService(var ResultCode: Integer): Boolean;
begin
  Result := RunServiceControl('query ' + AgentServiceName,
    'querying the Windows Service Control Manager', ResultCode);
end;

function AgentServiceExists: Boolean;
var
  ResultCode: Integer;
begin
  if not QueryAgentService(ResultCode) then
    RaiseException('Kairon Setup could not query the Windows Service Control Manager. ' +
      'Review the installer log and verify administrative permissions.');

  if ResultCode = 0 then
    Result := True
  else if ResultCode = ErrorServiceDoesNotExist then
    Result := False
  else
    RaiseException(Format(
      'Kairon Setup could not determine whether the %s service exists (sc.exe exit code %d). ' +
      'Review the installer log.', [AgentServiceName, ResultCode]));
end;

procedure ValidateExistingAgentService;
var
  ExistingImagePath: String;
begin
  if not RegQueryStringValue(HKLM,
    'SYSTEM\CurrentControlSet\Services\' + AgentServiceName,
    'ImagePath', ExistingImagePath) then
    RaiseException('An existing Kairon.Agent service was found, but its executable path could not be verified. ' +
      'Setup will not replace a service it cannot identify.');

  if CompareText(NormalizeImagePath(ExistingImagePath), AgentExecutablePath) <> 0 then
    RaiseException(Format(
      'An existing Kairon.Agent service points to a different executable (%s). ' +
      'Setup will not overwrite an unrelated service.', [NormalizeImagePath(ExistingImagePath)]));

  Log('Kairon Agent: existing service belongs to this installation and will be reconfigured.');
end;

procedure StopExistingAgentService;
var
  ResultCode: Integer;
begin
  if not RunServiceControl('stop ' + AgentServiceName,
    'stopping the existing service for upgrade', ResultCode) then
    RaiseException('Kairon Setup could not launch Service Control Manager tooling to stop the existing Agent.');

  if (ResultCode <> 0) and (ResultCode <> ErrorServiceNotActive) then
    RaiseException(Format(
      'Kairon Setup could not stop the existing %s service (sc.exe exit code %d). ' +
      'Stop the service and retry setup.', [AgentServiceName, ResultCode]));
end;

procedure InstallOrReconfigureAgentService;
var
  Existing: Boolean;
  ResultCode: Integer;
  CreateArguments: String;
  ConfigArguments: String;
  RegistrationAction: String;
begin
  // LocalService, least privilege: per-process telemetry (CPU/memory/start-time/parent PID for
  // an arbitrary process) does NOT run in this service - that's KAIRON.UserAgent's job, a separate
  // component that runs *as* the interactive user (registered below, InstallOrReconfigureUserAgentTask)
  // and so can read its own session's processes without any elevated account. This service only
  // ever needs machine-level heartbeat and its own lifecycle, which LocalService already does
  // today without issue. (A prior pass of this installer used LocalSystem here to work around the
  // Windows Service's own limited cross-session process visibility - docs/DESKTOP_SHELL.md - but
  // that traded away least privilege for a problem the UserAgent now solves properly.)
  Existing := AgentServiceExists;
  // sc.exe requires the built-in account's fully qualified name. The shorthand `LocalService`
  // fails on a fresh create with error 1057 on current Windows builds. Use the canonical account
  // for both create and upgrade so an older installation can never retain a more privileged
  // identity merely because Setup took the reconfigure path. LocalService is passwordless, so no
  // password= argument is required.
  CreateArguments := 'binPath= "' + AgentExecutablePath + '" start= auto ' +
    'obj= "NT AUTHORITY\LocalService" DisplayName= "Kairon Agent"';
  ConfigArguments := 'binPath= "' + AgentExecutablePath + '" start= auto ' +
    'obj= "NT AUTHORITY\LocalService" DisplayName= "Kairon Agent"';

  if Existing then
  begin
    RegistrationAction := 'reconfigure';
    ValidateExistingAgentService;
    if not RunServiceControl('config ' + AgentServiceName + ' ' + ConfigArguments,
      'reconfiguring the existing service', ResultCode) then
      RaiseException('Kairon Setup could not launch Service Control Manager tooling to reconfigure the Agent.');
  end
  else
  begin
    RegistrationAction := 'create';
    if not RunServiceControl('create ' + AgentServiceName + ' ' + CreateArguments,
      'creating the service', ResultCode) then
      RaiseException('Kairon Setup could not launch Service Control Manager tooling to create the Agent.');
  end;

  if ResultCode <> 0 then
    RaiseException(Format(
      'Kairon Setup could not %s the %s service (sc.exe exit code %d). ' +
      'Installation cannot continue. Review the installer log and administrative permissions.', [RegistrationAction, AgentServiceName, ResultCode]));

  if not AgentServiceExists then
    RaiseException('Kairon Setup completed the service registration command, but Kairon.Agent ' +
      'is not present in the Windows Service Control Manager.');

  if not RunServiceControl(
    'failure ' + AgentServiceName + ' reset= 86400 actions= restart/5000/restart/15000/none/0',
    'configuring service recovery', ResultCode) then
    RaiseException('Kairon Setup could not launch Service Control Manager tooling to configure Agent recovery.');
  if ResultCode <> 0 then
    RaiseException(Format(
      'Kairon Setup created the Agent service but could not configure recovery (sc.exe exit code %d). ' +
      'Installation cannot continue.', [ResultCode]));

  if not RunServiceControl('start ' + AgentServiceName,
    'starting the service', ResultCode) then
    RaiseException('Kairon Setup could not launch Service Control Manager tooling to start the Agent.');
  if (ResultCode <> 0) and (ResultCode <> ErrorServiceAlreadyRunning) then
    RaiseException(Format(
      'Kairon Setup registered the Agent service but could not start it (sc.exe exit code %d). ' +
      'Installation cannot continue. Review the installer log.', [ResultCode]));

  if not AgentServiceExists then
    RaiseException('Kairon Setup started the Agent registration flow, but Kairon.Agent could not be verified in SCM.');

  Log('Kairon Agent: service registration, recovery configuration, startup, and SCM verification succeeded.');
end;

function UserAgentExecutablePath: String;
begin
  Result := ExpandConstant('{app}\useragent\Kairon.UserAgent.exe');
end;

// Returns every running process whose image name and full executable path both identify the
// UserAgent installed by this exact Kairon installation. The name narrows the WMI query only; the
// path comparison is the security boundary that prevents an unrelated process with the same name
// from being touched. WMI sees processes in all interactive sessions when the uninstaller runs
// elevated, so this also handles more than one legitimate UserAgent instance.
function ProcessInstalledUserAgents(TerminateMatches: Boolean): Integer;
var
  Locator: Variant;
  Services: Variant;
  Processes: Variant;
  Process: Variant;
  I: Integer;
  ProcessId: Integer;
  SessionId: Integer;
  TerminateResult: Integer;
  ProcessPath: String;
  ExpectedPath: String;
begin
  Result := 0;
  ExpectedPath := ExpandFileName(UserAgentExecutablePath);

  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    // Terminating a process owned by another interactive session can require SeDebugPrivilege.
    // The uninstaller is already elevated; enable that existing token privilege only for this WMI
    // connection rather than introducing a service/helper or changing the UserAgent identity.
    try
      Locator.Security_.Privileges.AddAsString('SeDebugPrivilege', True);
    except
      Log('Kairon UserAgent: SeDebugPrivilege was unavailable; continuing with the elevated uninstall token.');
    end;
    Services := Locator.ConnectServer('.', 'root\cimv2');
    Services.Security_.ImpersonationLevel := 3;
    Processes := Services.ExecQuery(
      'SELECT ProcessId, SessionId, ExecutablePath FROM Win32_Process ' +
      'WHERE Name = ''Kairon.UserAgent.exe''');

    for I := 0 to Processes.Count - 1 do
    begin
      Process := Processes.ItemIndex(I);
      if not VarIsNull(Process.ExecutablePath) then
      begin
        ProcessPath := ExpandFileName(Process.ExecutablePath);
        if CompareText(ProcessPath, ExpectedPath) = 0 then
        begin
          Result := Result + 1;
          ProcessId := Process.ProcessId;
          SessionId := Process.SessionId;
          if TerminateMatches then
          begin
            Log(Format(
              'Kairon UserAgent: terminating verified installed process PID %d in session %d (%s).', [ProcessId, SessionId, ProcessPath]));
            TerminateResult := Process.Terminate(0);
            if TerminateResult <> 0 then
              RaiseException(Format(
                'Kairon Uninstall could not terminate its verified UserAgent process PID %d ' +
                '(WMI result %d). Close the process and retry uninstall.', [ProcessId, TerminateResult]));
          end;
        end
        else
          Log(Format(
            'Kairon UserAgent: leaving same-named process PID %d untouched because its path is %s.', [Integer(Process.ProcessId), ProcessPath]));
      end;
    end;
  except
    RaiseException('Kairon Uninstall could not inspect/stop its UserAgent processes: ' +
      GetExceptionMessage + '. No same-named process was terminated without path verification.');
  end;
end;

function WaitForInstalledUserAgentsToExit(TimeoutMilliseconds: Integer): Boolean;
var
  WaitedMilliseconds: Integer;
begin
  WaitedMilliseconds := 0;
  while (ProcessInstalledUserAgents(False) > 0) and
        (WaitedMilliseconds < TimeoutMilliseconds) do
  begin
    Sleep(250);
    WaitedMilliseconds := WaitedMilliseconds + 250;
  end;
  Result := ProcessInstalledUserAgents(False) = 0;
end;

procedure StopInstalledUserAgentsForLifecycle;
var
  MatchCount: Integer;
begin
  MatchCount := ProcessInstalledUserAgents(False);
  if MatchCount = 0 then
  begin
    Log('Kairon UserAgent: no running process belonging to this installation was found.');
    Exit;
  end;

  Log(Format('Kairon UserAgent: found %d verified installed process(es) to stop before file replacement/removal.', [MatchCount]));
  ProcessInstalledUserAgents(True);
  if not WaitForInstalledUserAgentsToExit(5000) then
    RaiseException('Kairon Uninstall timed out waiting for its verified UserAgent process(es) to exit. ' +
      'Installed files will not be removed while they may still be locked.');

  Log('Kairon UserAgent: all verified installed processes exited before file removal.');
end;

procedure StartInstalledUserAgentTask;
var
  ResultCode: Integer;
begin
  // The logon trigger does not fire merely because Setup created/replaced the task in an already
  // interactive session. Start it once after successful installation so fresh installs and
  // upgrades immediately run the newly installed version; IgnoreNew prevents a duplicate if an
  // instance is already active.
  if not (Exec(ExpandConstant('{sys}\schtasks.exe'),
      '/Run /TN "' + UserAgentTaskName + '"', '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode) and (ResultCode = 0)) then
    RaiseException(Format(
      'Kairon Setup registered its UserAgent task but could not start it (schtasks.exe exit code %d).', [ResultCode]));

  Log('Kairon UserAgent: scheduled task start requested successfully.');
end;

procedure RemoveUserAgentTaskForUninstall;
var
  ResultCode: Integer;
begin
  // Query first so uninstall remains idempotent when the task was already removed. If it exists,
  // require deletion to succeed so Windows cannot later launch a now-uninstalled executable.
  if not Exec(ExpandConstant('{sys}\schtasks.exe'),
      '/Query /TN "' + UserAgentTaskName + '"', '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode) then
    RaiseException('Kairon Uninstall could not query its UserAgent scheduled task.');

  if ResultCode <> 0 then
  begin
    Log('Kairon UserAgent: scheduled task was already absent.');
    Exit;
  end;

  if not (Exec(ExpandConstant('{sys}\schtasks.exe'),
      '/Delete /TN "' + UserAgentTaskName + '" /F', '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode) and (ResultCode = 0)) then
    RaiseException(Format(
      'Kairon Uninstall could not remove its UserAgent scheduled task (schtasks.exe exit code %d).', [ResultCode]));

  Log('Kairon UserAgent: scheduled task removed after all verified processes exited.');
end;

// KAIRON.UserAgent runs *as the interactive user*, not as a service - it needs the process
// visibility the LocalService-based Kairon.Agent Windows Service deliberately does not have
// (docs/DESKTOP_SHELL.md). A Scheduled Task with a LogonTrigger and a "Users" group principal
// (S-1-5-32-545) at LeastPrivilege is the standard, documented Windows mechanism for "run
// unprivileged, per-user, on every interactive logon" without per-user configuration - the same
// approach many desktop agents (AV clients, VPN clients) use. schtasks /Create /XML is used
// instead of the simpler /SC ONLOGON flags because those default the task to the *installing*
// (admin) account only; the XML form is what lets the principal be "any user who logs on".
procedure InstallOrReconfigureUserAgentTask;
var
  XmlPath: String;
  Xml: String;
  ResultCode: Integer;
begin
  XmlPath := ExpandConstant('{tmp}\KaironUserAgentTask.xml');
  // Deliberately no <?xml ... encoding="..."?> prolog: schtasks.exe's XML parser was tested
  // against several encoding/BOM combinations, and only two actually work - a real UTF-16LE file
  // declaring encoding="UTF-16", or (this one) a declaration-less file in plain ANSI/ASCII bytes,
  // which is what SaveStringToFile below actually writes for this pure-ASCII content (no non-ASCII
  // characters ever appear in this XML, so ANSI and UTF-8 are byte-identical here). A declared
  // "UTF-8" prolog over a BOM-less file, or a BOM over a declared "UTF-8" prolog, both fail with
  // "The task XML is malformed" - confirmed live, not a guess. XML defaults to UTF-8 with no
  // prolog, matching the real bytes exactly, so this is not a spec violation.
  Xml :=
    '<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">' + #13#10 +
    '  <Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>' + #13#10 +
    '  <Principals><Principal id="Author"><GroupId>S-1-5-32-545</GroupId><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>' + #13#10 +
    '  <Settings>' + #13#10 +
    '    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>' + #13#10 +
    '    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>' + #13#10 +
    '    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>' + #13#10 +
    '    <StartWhenAvailable>true</StartWhenAvailable>' + #13#10 +
    '    <AllowStartOnDemand>true</AllowStartOnDemand>' + #13#10 +
    '    <Enabled>true</Enabled>' + #13#10 +
    '  </Settings>' + #13#10 +
    '  <Actions Context="Author"><Exec><Command>' + UserAgentExecutablePath + '</Command></Exec></Actions>' + #13#10 +
    '</Task>';

  if not SaveStringToFile(XmlPath, Xml, False) then
    RaiseException('Kairon Setup could not write the KAIRON.UserAgent scheduled task definition.');

  Log('Kairon UserAgent: registering logon-triggered scheduled task.');
  // /F overwrites an existing task from a previous install/upgrade unconditionally - unlike the
  // Windows Service above, a scheduled task holds no running-process lock and re-creating it from
  // scratch on every install is simple and safe.
  if not (Exec(ExpandConstant('{sys}\schtasks.exe'),
      '/Create /TN "' + UserAgentTaskName + '" /XML "' + XmlPath + '" /F', '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode) and (ResultCode = 0)) then
    RaiseException(Format(
      'Kairon Setup could not register the KAIRON.UserAgent scheduled task (schtasks.exe exit code %d). ' +
      'Installation cannot continue. Review the installer log and administrative permissions.', [ResultCode]));

  Log('Kairon UserAgent: scheduled task registered successfully.');
end;

function BackendExecutablePath: String;
begin
  Result := ExpandConstant('{app}\backend\Kairon.Backend.exe');
end;

function BackendDataRoot: String;
begin
  Result := ExpandConstant('{commonappdata}\Kairon\backend');
end;

function BackendServiceExists: Boolean;
var
  ResultCode: Integer;
begin
  if not RunServiceControl('query ' + BackendServiceName, 'querying the backend service', ResultCode) then
    RaiseException('Kairon Setup could not query the Windows Service Control Manager. ' +
      'Review the installer log and verify administrative permissions.');
  if ResultCode = 0 then
    Result := True
  else if ResultCode = ErrorServiceDoesNotExist then
    Result := False
  else
    RaiseException(Format(
      'Kairon Setup could not determine whether the %s service exists (sc.exe exit code %d). ' +
      'Review the installer log.', [BackendServiceName, ResultCode]));
end;

procedure ValidateExistingBackendService;
var
  ExistingImagePath: String;
begin
  if not RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Services\' + BackendServiceName,
    'ImagePath', ExistingImagePath) then
    RaiseException('An existing Kairon.Backend service was found, but its executable path could not be verified. ' +
      'Setup will not replace a service it cannot identify.');
  if CompareText(NormalizeImagePath(ExistingImagePath), BackendExecutablePath) <> 0 then
    RaiseException(Format(
      'An existing Kairon.Backend service points to a different executable (%s). ' +
      'Setup will not overwrite an unrelated service.', [NormalizeImagePath(ExistingImagePath)]));
end;

// net.exe stop waits for the service (and, through its job object, the AI child) to exit, so the
// binaries are unlocked before [Files] replaces them. Exit code 2 means "not started".
procedure StopExistingBackendService;
var
  ResultCode: Integer;
begin
  if not Exec(ExpandConstant('{sys}\net.exe'), 'stop ' + BackendServiceName, '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode) then
    RaiseException('Kairon Setup could not launch net.exe to stop the existing backend service.');
  if (ResultCode <> 0) and (ResultCode <> 2) then
    RaiseException(Format(
      'Kairon Setup could not stop the existing %s service (net.exe exit code %d). ' +
      'Stop the service and retry setup.', [BackendServiceName, ResultCode]));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  // Restart Manager cannot close the hidden WinExe UserAgent because it has no top-level window.
  // Stop only exact-path instances before Restart Manager evaluates the remaining desktop-owned
  // processes, otherwise a silent upgrade aborts while the UserAgent keeps its binaries locked.
  StopInstalledUserAgentsForLifecycle;
  if AgentServiceExists then
  begin
    ValidateExistingAgentService;
    StopExistingAgentService;
  end;
  if BackendServiceExists then
  begin
    ValidateExistingBackendService;
    StopExistingBackendService;
  end;
  Result := '';
end;

function RunIcacls(const ConfigDir, Arguments, Action: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\icacls.exe'), '"' + ConfigDir + '" ' + Arguments,
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if not Result then
    RaiseException(Format('Kairon Setup could not launch icacls to %s.', [Action]));
  if ResultCode <> 0 then
    RaiseException(Format('Kairon Setup could not %s (icacls exit code %d).', [Action, ResultCode]));
end;

// icacls's own /setowner does NOT auto-enable SeTakeOwnershipPrivilege, and fails Access Denied
// even under a fully elevated Administrator token if that token doesn't already have WRITE_OWNER
// on the object - confirmed live (whoami /priv shows SeTakeOwnershipPrivilege as Disabled even
// while elevated, and icacls /setowner failed on exactly this file with exit code 5). takeown.exe
// enables that privilege itself before acting, which is why it succeeds where icacls /setowner
// does not - confirmed live with a side-by-side takeown /F attempt on the same file that
// succeeded immediately. /A assigns ownership to the Administrators group rather than the single
// installing user, matching what GrantAgentCredentialAcl's later icacls calls expect to own.
function RunTakeown(const Path, ExtraArgs, Action: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\takeown.exe'), '/F "' + Path + '" /A ' + ExtraArgs,
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if not Result then
    RaiseException(Format('Kairon Setup could not launch takeown to %s.', [Action]));
  if ResultCode <> 0 then
    RaiseException(Format('Kairon Setup could not %s (takeown exit code %d).', [Action, ResultCode]));
end;

function RunTakeownOnConfigDir(const ConfigDir, Action: String): Boolean;
begin
  // /R (recurse) is valid only for a directory target - passing it against a plain file makes
  // takeown reject the whole call (confirmed live: exit code 1) - hence the split into two
  // wrappers rather than one flag set reused for both a folder and a file target.
  Result := RunTakeown(ConfigDir, '/R /D Y', Action);
end;

// Resets this folder's whole ACL from scratch on every install/upgrade. The transition must remain
// writable by elevated Setup at every step: removing inheritance before adding an explicit
// Administrators ACE can remove Setup's only effective access and make the very next icacls call
// fail with Access Denied. First establish explicit SYSTEM/Administrators access recursively,
// then remove inheritance and apply the final least-privilege ACL. Interactive Users receive
// traverse access to the directory only; read access is added solely to the scoped UserAgent file
// after the LocalService Agent creates it. The machine credential is never readable by Users.
procedure GrantAgentCredentialAcl;
var
  ConfigDir: String;
begin
  ConfigDir := ExpandConstant('{commonappdata}\Kairon\config');

  // Setup runs elevated as the installing Administrator, but that alone does not grant WRITE_DAC
  // (permission to change an object's ACL) on a file this folder already contains - confirmed
  // live: agent-credential.json's owner was NT AUTHORITY\LOCAL SERVICE (the Agent service created
  // it itself, in an earlier run), its only ACE was BUILTIN\Users:(M), and Modify does not include
  // WRITE_DAC, so even elevated Setup got Access Denied (icacls exit code 5) trying to reset its
  // ACL directly. Taking ownership first is what actually grants WRITE_DAC - NTFS ownership always
  // implies the right to change permissions, regardless of the object's existing ACL.
  RunTakeownOnConfigDir(ConfigDir, 'take ownership of its credential folder');
  RunIcacls(ConfigDir, '/grant:r "BUILTIN\Administrators:F" /T',
    'preserve Administrator access while hardening its credential folder');
  RunIcacls(ConfigDir, '/grant:r "NT AUTHORITY\SYSTEM:F" /T',
    'preserve SYSTEM access while hardening its credential folder');
  RunIcacls(ConfigDir, '/inheritance:r /T',
    'remove inherited permissions from its credential folder');
  RunIcacls(ConfigDir, '/remove:g "BUILTIN\Users" /T',
    'strip any previously granted permissions for the Users group from its credential folder');
  RunIcacls(ConfigDir, '/grant:r "NT AUTHORITY\SYSTEM:F" /T',
    'grant SYSTEM access to existing credential files');
  RunIcacls(ConfigDir, '/grant:r "BUILTIN\Administrators:F" /T',
    'grant Administrators access to existing credential files');
  RunIcacls(ConfigDir, '/grant:r "NT AUTHORITY\LOCAL SERVICE:M" /T',
    'grant the Agent service account access to existing credential files');

  // Replace the root folder's plain transition ACEs with inheritable container ACEs. Existing
  // files keep the explicit plain ACEs applied above; future files inherit the same rights.
  RunIcacls(ConfigDir, '/grant:r "NT AUTHORITY\SYSTEM:(OI)(CI)F"',
    'make SYSTEM access inheritable on its credential folder');
  RunIcacls(ConfigDir, '/grant:r "BUILTIN\Administrators:(OI)(CI)F"',
    'make Administrators access inheritable on its credential folder');
  RunIcacls(ConfigDir, '/grant:r "BUILTIN\Users:RX"',
    'grant Users directory traversal without inheriting access to credential files');
  RunIcacls(ConfigDir, '/grant:r "NT AUTHORITY\LOCAL SERVICE:(OI)(CI)M"',
    'make the Agent service account access inheritable on its credential folder');

  Log('Kairon Agent: reset credential folder ACL; machine credentials are not readable by Users.');
end;

procedure HardenCredentialFile(const CredentialPath: String; GrantUsersRead: Boolean);
begin
  RunTakeown(CredentialPath, '', 'take ownership of a generated credential file');
  RunIcacls(CredentialPath, '/grant:r "BUILTIN\Administrators:F"',
    'preserve Administrator access to a generated credential file');
  RunIcacls(CredentialPath, '/grant:r "NT AUTHORITY\SYSTEM:F"',
    'preserve SYSTEM access to a generated credential file');
  RunIcacls(CredentialPath, '/inheritance:r',
    'remove inherited access from a generated credential file');
  RunIcacls(CredentialPath, '/remove:g "BUILTIN\Users"',
    'remove broad Users access from a generated credential file');
  RunIcacls(CredentialPath, '/grant:r "NT AUTHORITY\LOCAL SERVICE:M"',
    'grant LocalService access to a generated credential file');

  if GrantUsersRead then
    RunIcacls(CredentialPath, '/grant:r "BUILTIN\Users:R"',
      'grant Users read access to the scoped UserAgent credential');
end;

procedure HardenGeneratedAgentCredentials;
var
  AgentCredential: String;
  UserAgentCredential: String;
  I: Integer;
begin
  AgentCredential := ExpandConstant('{commonappdata}\Kairon\config\agent-credential.json');
  UserAgentCredential := ExpandConstant('{commonappdata}\Kairon\config\useragent-credential.json');

  // Agent resolves both credentials before its hosted service starts. Allow a bounded startup
  // window, then fail installation rather than start UserAgent with missing/insecure material.
  for I := 1 to 100 do
  begin
    if FileExists(AgentCredential) and FileExists(UserAgentCredential) then
      Break;
    Sleep(100);
  end;

  if not FileExists(AgentCredential) or not FileExists(UserAgentCredential) then
    RaiseException('Kairon Setup started the Agent, but its scoped credentials were not generated within 10 seconds.');

  HardenCredentialFile(AgentCredential, False);
  HardenCredentialFile(UserAgentCredential, True);
  Log('Kairon Agent: hardened separate machine and interactive UserAgent credentials.');
end;

// Kairon.Backend runs under its own virtual account (NT SERVICE\Kairon.Backend): no password, no
// interactive logon, no administrator rights, and a SID unique to KAIRON so remediation grants made
// to it can be audited and swept on uninstall. Configuration is the service's own Environment
// registry value, so the command line is just the quoted executable.
procedure InstallOrReconfigureBackendService;
var
  Existing: Boolean;
  ResultCode: Integer;
  Arguments: String;
  Action: String;
  Environment: String;
begin
  Existing := BackendServiceExists;
  Arguments := 'binPath= "' + BackendExecutablePath + '" start= auto ' +
    'obj= "NT SERVICE\' + BackendServiceName + '" DisplayName= "Kairon Backend"';
  if Existing then
  begin
    Action := 'reconfigure';
    ValidateExistingBackendService;
    if not RunServiceControl('config ' + BackendServiceName + ' ' + Arguments, 'reconfiguring the backend service', ResultCode) then
      RaiseException('Kairon Setup could not launch Service Control Manager tooling to reconfigure the backend.');
  end
  else
  begin
    Action := 'create';
    if not RunServiceControl('create ' + BackendServiceName + ' ' + Arguments, 'creating the backend service', ResultCode) then
      RaiseException('Kairon Setup could not launch Service Control Manager tooling to create the backend.');
  end;
  if ResultCode <> 0 then
    RaiseException(Format('Kairon Setup could not %s the %s service (sc.exe exit code %d).', [Action, BackendServiceName, ResultCode]));

  if not RunServiceControl('sidtype ' + BackendServiceName + ' unrestricted', 'enabling the backend service SID', ResultCode) or (ResultCode <> 0) then
    RaiseException(Format('Kairon Setup could not enable the %s service SID (sc.exe exit code %d).', [BackendServiceName, ResultCode]));
  RunServiceControl('description ' + BackendServiceName + ' "Kairon backend: detection, AI investigation, approval-gated remediation. Supervises the Kairon AI service."',
    'describing the backend service', ResultCode);
  if not RunServiceControl('failure ' + BackendServiceName + ' reset= 86400 actions= restart/5000/restart/15000/restart/60000',
    'configuring backend service recovery', ResultCode) or (ResultCode <> 0) then
    RaiseException(Format('Kairon Setup could not configure %s recovery (sc.exe exit code %d).', [BackendServiceName, ResultCode]));

  Environment :=
    'ASPNETCORE_URLS=http://127.0.0.1:8000' + #0 +
    'Persistence__DataRoot=' + BackendDataRoot + #0 +
    'SreSecurity__OperatorKeyFile=' + BackendDataRoot + '\operator\operator.key' + #0 +
    'AiService__ExecutablePath=' + ExpandConstant('{app}\ai\Kairon.AI.exe');
  if not RegWriteMultiStringValue(HKLM, 'SYSTEM\CurrentControlSet\Services\' + BackendServiceName, 'Environment', Environment) then
    RaiseException('Kairon Setup could not write the backend service configuration.');

  if not BackendServiceExists then
    RaiseException('Kairon Setup registered Kairon.Backend, but it is not present in the Service Control Manager.');
  Log('Kairon Backend: service registered as NT SERVICE\Kairon.Backend with recovery and configuration.');
end;

// One-time move of the desktop-era database into the service data root: only when the service
// has no database yet. The original under %LOCALAPPDATA%\Kairon is left in place (rollback copy).
// Encrypted settings (AI provider key, SQL Server password) are protected with the installing
// user's DPAPI key and cannot be read by the service account; they must be re-entered once.
procedure MigrateDesktopDatabase;
var
  Source: String;
  Target: String;
  Suffix: String;
  I: Integer;
begin
  Target := BackendDataRoot + '\data\kairon.db';
  Source := ExpandConstant('{localappdata}\Kairon\data\kairon.db');
  if FileExists(Target) or not FileExists(Source) then exit;
  for I := 0 to 2 do
  begin
    case I of
      0: Suffix := '';
      1: Suffix := '-wal';
    else
      Suffix := '-shm';
    end;
    if FileExists(Source + Suffix) and not CopyFile(Source + Suffix, Target + Suffix, True) then
      RaiseException('Kairon Setup could not copy the existing Kairon database into ' + BackendDataRoot + '.');
  end;
  Log('Kairon Backend: copied the existing desktop database into the service data root (original kept).');
end;

// Resets the data root's ACL on every install/upgrade: SYSTEM and Administrators full control,
// the backend service account modify, no Users/Authenticated Users access (ProgramData's default
// would let any user create files here). Children inherit; only the operator-key folder adds
// read-only access for interactively logged-on users so the local desktop shell can sign in.
procedure PrepareBackendDataRoot;
var
  Root: String;
begin
  Root := BackendDataRoot;
  ForceDirectories(Root + '\data');
  ForceDirectories(Root + '\logs');
  ForceDirectories(Root + '\config');
  ForceDirectories(Root + '\cache');
  ForceDirectories(Root + '\backups');
  ForceDirectories(Root + '\operator');
  MigrateDesktopDatabase;

  RunTakeownOnConfigDir(Root, 'take ownership of the backend data folder');
  RunIcacls(Root, '/grant:r "NT AUTHORITY\SYSTEM:(OI)(CI)F" "BUILTIN\Administrators:(OI)(CI)F"',
    'grant SYSTEM and Administrators access to the backend data folder');
  RunIcacls(Root, '/inheritance:r', 'remove inherited ProgramData permissions from the backend data folder');
  RunIcacls(Root, '/grant:r "NT SERVICE\' + BackendServiceName + ':(OI)(CI)M"',
    'grant the backend service account access to its data folder');
  RunIcacls(Root + '\*', '/reset /T /C', 'make existing backend data inherit the data folder permissions');
  RunIcacls(Root + '\operator', '/grant:r "*S-1-5-4:(OI)(CI)R"',
    'let interactively logged-on users read the local operator key');
  Log('Kairon Backend: data root ACL reset (SYSTEM/Administrators/service account; INTERACTIVE read on operator).');
end;

procedure StartBackendService;
var
  ResultCode: Integer;
begin
  if not RunServiceControl('start ' + BackendServiceName, 'starting the backend service', ResultCode) then
    RaiseException('Kairon Setup could not launch Service Control Manager tooling to start the backend.');
  if (ResultCode <> 0) and (ResultCode <> ErrorServiceAlreadyRunning) then
    RaiseException(Format('Kairon Setup registered the backend service but could not start it (sc.exe exit code %d). ' +
      'Review %s\logs.', [ResultCode, BackendDataRoot]));
  Log('Kairon Backend: service started.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    GrantAgentCredentialAcl;
    InstallOrReconfigureAgentService;
    HardenGeneratedAgentCredentials;
    InstallOrReconfigureBackendService;
    PrepareBackendDataRoot;
    StartBackendService;
    InstallOrReconfigureUserAgentTask;
    StartInstalledUserAgentTask;
  end;
end;

var
  UserAgentUninstallPrepared: Boolean;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usUninstall) and not UserAgentUninstallPrepared then
  begin
    UserAgentUninstallPrepared := True;
    StopInstalledUserAgentsForLifecycle;
    RemoveUserAgentTaskForUninstall;
  end;
end;
