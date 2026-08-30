; Adapted from Azzy's productization branch (origin/main / codex/productization-windows-20260828),
; updated for the real desktop shell built in this pass (docs/DESKTOP_SHELL.md). The source
; branch's [Icons]/[Run] launched "KAIRON.exe --desktop --urls ..." - the backend's own exe with a
; flag that opened a system browser. There is now a real native desktop/Kairon.Desktop project
; (Kairon.exe at the package root) that hosts the UI in WebView2 instead, so no flag is needed;
; it starts the backend and AI service itself and waits for both to report real health before
; showing a window. The Windows Service registration Pascal Script below (backend/agent/ai package
; layout, validate-before-reconfigure, recovery policy, SCM verification at every step) is
; unchanged in substance from the source branch - it did not need adapting.
#define MyAppName "Kairon"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Kairon"
#ifndef PackageRoot
  #define PackageRoot "..\artifacts\windows-package"
#endif

[Setup]
AppId={{9BE9042A-B83A-4BED-9651-7C5B7086C9AF}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
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

[Files]
; The desktop shell publishes to the package root; backend/agent/ai each publish to their own
; subfolder (matches desktop/Kairon.Desktop/AppPaths.cs's expected layout exactly).
Source: "{#PackageRoot}\*"; DestDir: "{app}"; Excludes: "backend\*,agent\*,ai\*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageRoot}\backend\*"; DestDir: "{app}\backend"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageRoot}\agent\*"; DestDir: "{app}\agent"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageRoot}\useragent\*"; DestDir: "{app}\useragent"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageRoot}\ai\*"; DestDir: "{app}\ai"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
Name: "{commonappdata}\Kairon\config"; Permissions: users-modify

[Icons]
Name: "{group}\Kairon"; Filename: "{app}\Kairon.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Kairon"; Filename: "{app}\Kairon.exe"; Tasks: desktopicon; WorkingDir: "{app}"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Run]
Filename: "{app}\Kairon.exe"; Description: "Launch Kairon"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop Kairon.Agent"; Flags: runhidden waituntilterminated; RunOnceId: "StopAgent"
Filename: "{sys}\sc.exe"; Parameters: "delete Kairon.Agent"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteAgent"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""Kairon\UserAgent"" /F"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteUserAgentTask"

[Code]
const
  AgentServiceName = 'Kairon.Agent';
  UserAgentTaskName = 'Kairon\UserAgent';
  ErrorServiceDoesNotExist = 1060;
  ErrorServiceNotActive = 1062;
  ErrorServiceAlreadyRunning = 1056;

function AgentExecutablePath: String;
begin
  Result := ExpandConstant('{app}\agent\Kairon.Agent.exe');
end;

function NormalizeImagePath(Value: String): String;
begin
  Result := Trim(Value);
  if (Length(Result) >= 2) and (Result[1] = '"') and
     (Result[Length(Result)] = '"') then
    Result := Copy(Result, 2, Length(Result) - 2);
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
  Arguments: String;
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
  Arguments := 'binPath= "' + AgentExecutablePath + '" start= auto ' +
    'obj= LocalService DisplayName= "Kairon Agent"';

  if Existing then
  begin
    RegistrationAction := 'reconfigure';
    ValidateExistingAgentService;
    if not RunServiceControl('config ' + AgentServiceName + ' ' + Arguments,
      'reconfiguring the existing service', ResultCode) then
      RaiseException('Kairon Setup could not launch Service Control Manager tooling to reconfigure the Agent.');
  end
  else
  begin
    RegistrationAction := 'create';
    if not RunServiceControl('create ' + AgentServiceName + ' ' + Arguments,
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

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if AgentServiceExists then
  begin
    ValidateExistingAgentService;
    StopExistingAgentService;
  end;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    InstallOrReconfigureAgentService;
    InstallOrReconfigureUserAgentTask;
  end;
end;
