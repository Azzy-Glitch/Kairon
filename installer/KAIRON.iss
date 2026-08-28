#define MyAppName "KAIRON"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "KAIRON"
#ifndef PackageRoot
  #define PackageRoot "..\artifacts\windows-package"
#endif

[Setup]
AppId={{9BE9042A-B83A-4BED-9651-7C5B7086C9AF}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\KAIRON
DefaultGroupName=KAIRON
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=KAIRON-Setup-{#MyAppVersion}-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\KAIRON.exe
VersionInfoVersion={#MyAppVersion}

[Files]
Source: "{#PackageRoot}\backend\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageRoot}\agent\*"; DestDir: "{app}\agent"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageRoot}\ai\*"; DestDir: "{app}\ai"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
Name: "{commonappdata}\KAIRON\config"; Permissions: users-modify

[Icons]
Name: "{group}\KAIRON"; Filename: "{app}\KAIRON.exe"; Parameters: "--desktop --urls http://127.0.0.1:8000"; WorkingDir: "{app}"
Name: "{autodesktop}\KAIRON"; Filename: "{app}\KAIRON.exe"; Parameters: "--desktop --urls http://127.0.0.1:8000"; Tasks: desktopicon; WorkingDir: "{app}"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Run]
Filename: "{sys}\sc.exe"; Parameters: "create KAIRON.Agent binPath= ""{app}\agent\KAIRON.Agent.exe"" start= auto obj= ""NT AUTHORITY\LocalService"" DisplayName= ""KAIRON Agent"""; Flags: runhidden waituntilterminated; StatusMsg: "Installing KAIRON Agent service..."
Filename: "{sys}\sc.exe"; Parameters: "failure KAIRON.Agent reset= 86400 actions= restart/5000/restart/15000/none/0"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "start KAIRON.Agent"; Flags: runhidden waituntilterminated; StatusMsg: "Starting KAIRON Agent..."
Filename: "{app}\KAIRON.exe"; Parameters: "--desktop --urls http://127.0.0.1:8000"; Description: "Launch KAIRON"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop KAIRON.Agent"; Flags: runhidden waituntilterminated; RunOnceId: "StopAgent"
Filename: "{sys}\sc.exe"; Parameters: "delete KAIRON.Agent"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteAgent"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop KAIRON.Agent', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'delete KAIRON.Agent', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
