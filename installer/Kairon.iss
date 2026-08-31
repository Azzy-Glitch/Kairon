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
  // `sc create ... obj= LocalService` (no password= needed - it's a passwordless virtual account)
  // is what correctly set SERVICE_START_NAME to NT AUTHORITY\LocalService in the first place, and
  // still works fine for a brand new service. But `sc config` on an EXISTING service is different:
  // confirmed live (a battery of side-by-side variants, run standalone outside the installer) that
  // passing obj= LocalService to `config` at all - with or without password= - fails with error
  // 1057 ("account name is invalid or password is invalid"), while the EXACT SAME config call with
  // obj=/password= omitted entirely succeeds immediately (exit 0). Since this service's account
  // never changes between installs (it is always LocalService, hard-coded, never user-configurable),
  // there is nothing to reconfigure there on an upgrade - so config simply doesn't ask.
  CreateArguments := 'binPath= "' + AgentExecutablePath + '" start= auto ' +
    'obj= LocalService DisplayName= "Kairon Agent"';
  ConfigArguments := 'binPath= "' + AgentExecutablePath + '" start= auto ' +
    'DisplayName= "Kairon Agent"';

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

function RunTakeownOnFile(const FilePath, Action: String): Boolean;
begin
  // /D (default answer to the "deny read permission" prompt) is - like /R - only valid alongside
  // /R; a single file target takes neither (confirmed live: takeown rejects /D without /R).
  Result := RunTakeown(FilePath, '', Action);
end;

// Resets this folder's whole ACL from scratch on every install/upgrade, rather than only adding a
// grant on top of whatever is already there. Three things confirmed live make an additive-only
// grant insufficient: (1) every subfolder created under %ProgramData% inherits a standard Windows
// ACE - BUILTIN\Users:(WD,AD,WEA,WA) - that alone lets an ordinary user overwrite a file here, with
// no explicit Modify grant needed; (2) a folder that has been through an older installer revision
// (e.g. one that used `Permissions: users-modify`) can carry a leftover explicit grant that
// `icacls /grant` alone never strips; (3) resetting the FOLDER's own ACL does not retroactively
// touch a file that already exists inside it - agent-credential.json, generated by an earlier
// install/run under a broader grant, kept its own stale BUILTIN\Users:(M) ACE even after the
// folder above it was reset, since a parent's ACE only propagates to children created after the
// ACE is set, never to ones that already existed. /T (recurse into existing children) on every
// call below is what actually reaches that already-existing file, not just the folder. Breaking
// inheritance (/inheritance:r) removes case (1); explicitly removing any existing Users grant
// before re-adding a clean one handles case (2); /T handles case (3); /grant:r (replace, not add)
// for every trustee means the result is always exactly the same four entries on the folder and
// every file in it, regardless of this folder's install history.
procedure GrantAgentCredentialAcl;
var
  ConfigDir: String;
  CredentialFile: String;
begin
  ConfigDir := ExpandConstant('{commonappdata}\Kairon\config');
  CredentialFile := ConfigDir + '\agent-credential.json';

  // Setup runs elevated as the installing Administrator, but that alone does not grant WRITE_DAC
  // (permission to change an object's ACL) on a file this folder already contains - confirmed
  // live: agent-credential.json's owner was NT AUTHORITY\LOCAL SERVICE (the Agent service created
  // it itself, in an earlier run), its only ACE was BUILTIN\Users:(M), and Modify does not include
  // WRITE_DAC, so even elevated Setup got Access Denied (icacls exit code 5) trying to reset its
  // ACL directly. Taking ownership first is what actually grants WRITE_DAC - NTFS ownership always
  // implies the right to change permissions, regardless of the object's existing ACL.
  RunTakeownOnConfigDir(ConfigDir, 'take ownership of its credential folder');
  RunIcacls(ConfigDir, '/inheritance:r /T',
    'remove inherited permissions from its credential folder');
  RunIcacls(ConfigDir, '/remove:g "BUILTIN\Users" /T',
    'strip any previously granted permissions for the Users group from its credential folder');
  RunIcacls(ConfigDir, '/grant:r "NT AUTHORITY\SYSTEM:(OI)(CI)F" /T',
    'grant SYSTEM access to its credential folder');
  RunIcacls(ConfigDir, '/grant:r "BUILTIN\Administrators:(OI)(CI)F" /T',
    'grant Administrators access to its credential folder');
  RunIcacls(ConfigDir, '/grant:r "BUILTIN\Users:(OI)(CI)RX" /T',
    'grant Users read-only access to its credential folder');
  RunIcacls(ConfigDir, '/grant:r "NT AUTHORITY\LOCAL SERVICE:(OI)(CI)M" /T',
    'grant the Agent service account access to its credential folder');

  Log('Kairon Agent: reset the credential config folder ACL to SYSTEM/Administrators full control, Users read-only, LocalService modify.');

  // The six calls above, despite /T, do NOT reliably land on a file that already exists inside the
  // folder - confirmed live: after all six ran cleanly (exit 0, and the FOLDER's own ACL came out
  // exactly right), the pre-existing agent-credential.json was left with a completely EMPTY DACL
  // (zero ACEs - verified with both icacls and Get-Acl), meaning nobody, not even LocalService,
  // could actually open it; a (OI)(CI)-qualified grant is only meaningful on a container; applying
  // it through /T to a plain file apparently discards the grant on that file entirely rather than
  // stripping the now-meaningless inheritance qualifiers and keeping the underlying right. The
  // fix is to not depend on propagation at all: repeat the same reset directly against the file
  // path itself, with plain (non-qualified) rights, which unambiguously apply to a single object.
  if FileExists(CredentialFile) then
  begin
    RunTakeownOnFile(CredentialFile, 'take ownership of its existing credential file');
    RunIcacls(CredentialFile, '/inheritance:r',
      'remove inherited permissions from its existing credential file');
    RunIcacls(CredentialFile, '/remove:g "BUILTIN\Users"',
      'strip any previously granted permissions for the Users group from its existing credential file');
    RunIcacls(CredentialFile, '/grant:r "NT AUTHORITY\SYSTEM:F"',
      'grant SYSTEM access to its existing credential file');
    RunIcacls(CredentialFile, '/grant:r "BUILTIN\Administrators:F"',
      'grant Administrators access to its existing credential file');
    RunIcacls(CredentialFile, '/grant:r "BUILTIN\Users:RX"',
      'grant Users read-only access to its existing credential file');
    RunIcacls(CredentialFile, '/grant:r "NT AUTHORITY\LOCAL SERVICE:M"',
      'grant the Agent service account access to its existing credential file');

    Log('Kairon Agent: also reset the ACL directly on the pre-existing credential file itself.');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    GrantAgentCredentialAcl;
    InstallOrReconfigureAgentService;
    InstallOrReconfigureUserAgentTask;
  end;
end;
