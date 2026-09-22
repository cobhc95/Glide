#define MyAppName "Glide Image Viewer"
#define MyAppShortName "Glide"
#define MyAppVersion "4.2.7"
#define MyAppExeName "Glide.exe"

[Setup]
AppId=Glide
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
DefaultDirName={code:GetGlideInstallDir}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist-installer
OutputBaseFilename=Glide Setup
SetupIconFile=..\src\Glide.App\Assets\Glide.ico
Compression=lzma
SolidCompression=no
WizardStyle=modern
UsePreviousAppDir=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion=4.2.7.0
VersionInfoProductName={#MyAppName}
VersionInfoDescription=Glide Image Viewer Setup

[Files]
Source: "..\dist\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "installed.flag"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Registry]
; App Paths is the only association-adjacent key the installer writes directly (it is also how
; GetGlideInstallDir locates an existing install). Every Open With / Capabilities entry for all
; recognised extensions is written machine-wide by Glide itself (see CurStepChanged below), so the
; list can never drift from ImageFormatRegistry and uninstall removes exactly what was added.
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\Glide.exe"; ValueType: string; ValueData: "{app}\Glide.exe"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\Glide.exe"; ValueType: string; ValueName: "Path"; ValueData: "{app}"; Flags: uninsdeletevalue
; Remove the pre-4.2.7 "Glide Image Viewer" application identity if an earlier version registered it.
Root: HKLM; Subkey: "Software\RegisteredApplications"; ValueType: none; ValueName: "Glide Image Viewer"; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Glide Image Viewer"; Flags: deletekey

[Run]
Filename: "{app}\Glide.exe"; Description: "Launch Glide"; Flags: nowait postinstall skipifsilent

[Code]
procedure StopAllGlideProcesses;
var
  Attempt: Integer;
  ResultCode: Integer;
begin
  { Glide's warm-start host may keep one or more hidden processes alive after every
    visible window closes. Stop every matching process tree before files are replaced;
    bounded retries prevent Setup from waiting forever on an unresponsive instance. }
  for Attempt := 1 to 3 do
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM "Glide.exe"', '',
      SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(200);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopAllGlideProcesses;
  Result := '';
end;

function ExistingAppPath(RootKey: Integer; const SubKey: String): String;
var
  ExePath: String;
begin
  Result := '';
  if RegQueryStringValue(RootKey, SubKey, '', ExePath) then
  begin
    if FileExists(ExePath) then
      Result := ExtractFileDir(ExePath);
  end;
end;

function LegacyAppPath(RootKey: Integer; const SubKey: String): String;
var
  CommandValue: String;
  P: Integer;
begin
  Result := '';
  if RegQueryStringValue(RootKey, SubKey, '', CommandValue) then
  begin
    CommandValue := Trim(CommandValue);
    if (Length(CommandValue) > 1) and (CommandValue[1] = '"') then
    begin
      Delete(CommandValue, 1, 1);
      P := Pos('"', CommandValue);
      if P > 0 then
        CommandValue := Copy(CommandValue, 1, P - 1);
    end
    else
    begin
      P := Pos(' ', CommandValue);
      if P > 0 then
        CommandValue := Copy(CommandValue, 1, P - 1);
    end;

    if FileExists(CommandValue) then
      Result := ExtractFileDir(CommandValue);
  end;
end;

function GetGlideInstallDir(Param: String): String;
var
  FoundPath: String;
begin
  FoundPath := ExistingAppPath(HKLM, 'Software\Microsoft\Windows\CurrentVersion\App Paths\Glide.exe');
  if FoundPath <> '' then
  begin
    Result := FoundPath;
    Exit;
  end;

  FoundPath := ExistingAppPath(HKCU, 'Software\Microsoft\Windows\CurrentVersion\App Paths\Glide.exe');
  if FoundPath <> '' then
  begin
    Result := FoundPath;
    Exit;
  end;

  FoundPath := LegacyAppPath(HKLM, 'Software\Classes\Applications\Glide.exe\shell\open\command');
  if FoundPath <> '' then
  begin
    Result := FoundPath;
    Exit;
  end;

  FoundPath := LegacyAppPath(HKLM, 'Software\Classes\Glide.Image\shell\open\command');
  if FoundPath <> '' then
  begin
    Result := FoundPath;
    Exit;
  end;

  Result := ExpandConstant('{autopf}\Glide');
end;

procedure RegisterGlideAssociations;
var
  ResultCode: Integer;
  ExePath: String;
begin
  { Machine-wide Open With / Capabilities for every recognised extension. Glide writes these itself
    so the set is always exactly ImageFormatRegistry; the installer only guarantees it runs once the
    files are in place, which makes a fresh install immediately integrated with no user action. }
  ExePath := ExpandConstant('{app}\Glide.exe');
  if FileExists(ExePath) then
    Exec(ExePath, '--register-file-associations', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure UnregisterGlideAssociations;
var
  ResultCode: Integer;
  ExePath: String;
begin
  ExePath := ExpandConstant('{app}\Glide.exe');
  if FileExists(ExePath) then
    Exec(ExePath, '--unregister-file-associations', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    RegisterGlideAssociations;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    { Stop the warm host so Glide.exe is runnable and not locked while we remove its registration. }
    StopAllGlideProcesses;
    UnregisterGlideAssociations;
  end;
end;
