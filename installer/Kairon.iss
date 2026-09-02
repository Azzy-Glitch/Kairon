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
#define MyAppVersion "1.0.1"
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
Filename: "{sys}\sc.exe"; Parameters: "stop Kairon.Agent"; Flags: runhidden waituntilterminated; RunOnceId: "StopAgent"
Filename: "{sys}\sc.exe"; Parameters: "delete Kairon.Agent"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteAgent"

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

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    GrantAgentCredentialAcl;
    InstallOrReconfigureAgentService;
    HardenGeneratedAgentCredentials;
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
