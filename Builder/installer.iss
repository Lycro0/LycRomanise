; Inno Setup script: builds LycRomanise_Setup.exe from ..\App\LycRomanise.exe
; Made by Lycro. Needs Inno Setup 6 (https://jrsoftware.org/isinfo.php); build.bat or the GitHub release workflow runs it.
; The AppId is the same as v1.x, so installing v2 upgrades an existing install in place.
#define AppName "LycRomanise"
#ifndef AppVersion
  #define AppVersion "2.3.0"
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
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "LycRomanise"; ValueData: """{app}\LycRomanise.exe"""; Flags: uninsdeletevalue; Tasks: autostart

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

{ Start-with-Windows used the value "LycRomanise" (v1) and then "LycRomanise Modern" (v2.0-2.1).
  Keep the user's choice under "LycRomanise", pointing at this install. }
procedure MoveLegacyAutostart;
var
  Old: String;
  Was: Boolean;
begin
  Was := RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'LycRomanise', Old);
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'LycRomanise Modern', Old) then
  begin
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'LycRomanise Modern');
    Was := True;
  end;
  if Was then
    RegWriteStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'LycRomanise', '"' + ExpandConstant('{app}\LycRomanise.exe') + '"');
end;

{ v2.0-2.1 kept settings in %APPDATA%\LycRomanise Modern; it is now %APPDATA%\LycRomanise. The app also
  merges the two on start, for portable copies. }
procedure MoveDataFolder;
var
  OldDir, NewDir: String;
begin
  OldDir := ExpandConstant('{userappdata}\LycRomanise Modern');
  NewDir := ExpandConstant('{userappdata}\LycRomanise');
  if DirExists(OldDir) and not DirExists(NewDir) then RenameFile(OldDir, NewDir);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    MoveLegacyAutostart;
    MoveDataFolder;
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
; Settings, cache and logs live in %APPDATA%\LycRomanise and are kept on uninstall.
