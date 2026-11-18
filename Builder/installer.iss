; Inno Setup script: builds LycRomanise_Setup.exe from ..\App\LycRomanise.exe
; Made by Lycro. Needs Inno Setup 6 (https://jrsoftware.org/isinfo.php); build.bat or the GitHub release workflow runs it.
; The AppId is the same as v1.x, so installing v2 upgrades an existing install in place.
#define AppName "LycRomanise"
#ifndef AppVersion
  #define AppVersion "2.1.0"
#endif

[Setup]
AppId={{B7C2F1A4-5D3E-4E8A-9F61-0A1B2C3D4E5F}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Lycro
AppPublisherURL=https://github.com/Lycro0/LycRomanise
AppUpdatesURL=https://github.com/Lycro0/LycRomanise/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline dialog
OutputDir=..\App
OutputBaseFilename=LycRomanise_Setup
SetupIconFile=..\Source Code\LycRomanise\Assets\lyrics-overlay.ico
UninstallDisplayIcon={app}\LycRomanise.exe
VersionInfoCompany=Lycro
VersionInfoDescription={#AppName} installer
VersionInfoVersion={#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked
Name: "autostart"; Description: "Start LycRomanise when Windows starts"; Flags: unchecked

[Files]
Source: "..\App\LycRomanise.exe"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; v1.x left its Python runtime files beside the exe in some installs.
Type: filesandordirs; Name: "{app}\_internal"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\LycRomanise.exe"
Name: "{group}\{#AppName} lyrics"; Filename: "{app}\LycRomanise.exe"; Parameters: "--lyrics"; Comment: "Open the full lyrics window"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\LycRomanise.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "LycRomanise Modern"; ValueData: """{app}\LycRomanise.exe"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\LycRomanise.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
; In-app update (the app runs this installer with /SILENT /RELAUNCH=1): start the new version when done.
Filename: "{app}\LycRomanise.exe"; Flags: nowait runasoriginaluser; Check: RelaunchRequested

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /T /IM LycRomanise.exe"; Flags: runhidden; RunOnceId: "KillApp"

[Code]
function RelaunchRequested: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

{ v1 could be installed for all users (Program Files (x86), 32-bit registry). v2 installs per user, so
  Inno does not see that copy as the same install: once v2 is in place, uninstall the old one so there
  are not two. Windows asks for permission, as removing it needs admin rights. }
const
  LegacyKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{B7C2F1A4-5D3E-4E8A-9F61-0A1B2C3D4E5F}_is1';

procedure RemoveLegacyInstall;
var
  Uninstaller, Location: String;
  ResultCode: Integer;
begin
  if not RegQueryStringValue(HKLM32, LegacyKey, 'UninstallString', Uninstaller) then Exit;
  RegQueryStringValue(HKLM32, LegacyKey, 'InstallLocation', Location);
  if CompareText(RemoveBackslash(Location), RemoveBackslash(ExpandConstant('{app}'))) = 0 then Exit;
  Uninstaller := RemoveQuotes(Uninstaller);
  if not FileExists(Uninstaller) then Exit;
  Log('Removing the all-users LycRomanise 1.x install at ' + Location);
  ShellExec('runas', Uninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

{ v1 started with Windows under the value "LycRomanise"; keep that choice, pointing at v2. }
procedure MoveLegacyAutostart;
var
  Old: String;
begin
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'LycRomanise', Old) then
  begin
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'LycRomanise');
    RegWriteStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'LycRomanise Modern', '"' + ExpandConstant('{app}\LycRomanise.exe') + '"');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    MoveLegacyAutostart;
    RemoveLegacyInstall;
  end;
end;

{ A running LycRomanise.exe can't be replaced, so close every copy first. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM LycRomanise.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(800);
  Result := '';
end;

[UninstallDelete]
; Settings, cache and logs live in %APPDATA%\LycRomanise Modern and are kept on uninstall.
