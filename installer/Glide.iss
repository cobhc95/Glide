#define MyAppName "Glide Image Viewer"
#define MyAppShortName "Glide"
#define MyAppVersion "4.2.5"
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
VersionInfoVersion=4.2.5.0
VersionInfoProductName={#MyAppName}
VersionInfoDescription=Glide Image Viewer Setup

[Files]
Source: "..\dist\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "installed.flag"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Registry]
; Normal Windows application registration. Single registered default named "Glide Image Viewer".
Root: HKLM; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "Glide Image Viewer"; ValueData: "Software\Glide Image Viewer\Capabilities"; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Glide Image Viewer\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "Glide Image Viewer"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Glide Image Viewer\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Fast lightweight image viewer"; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\Applications\Glide.exe\shell\open\command"; ValueType: string; ValueData: """{app}\Glide.exe"" ""%1"""; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\Glide.exe"; ValueType: string; ValueData: "{app}\Glide.exe"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\Glide.exe"; ValueType: string; ValueName: "Path"; ValueData: "{app}"; Flags: uninsdeletevalue
; Clean up legacy registry keys if present from earlier versions
Root: HKLM; Subkey: "Software\RegisteredApplications"; ValueType: none; ValueName: "Glide"; Flags: uninsdeletevalue
; Common formats are advertised through Capabilities. The application remains able to open its wider runtime registry.
Root: HKLM; Subkey: "Software\Glide Image Viewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".jpg"; ValueData: "Glide.Image"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Glide Image Viewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".jpeg"; ValueData: "Glide.Image"
Root: HKLM; Subkey: "Software\Glide Image Viewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".png"; ValueData: "Glide.Image"
Root: HKLM; Subkey: "Software\Glide Image Viewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".webp"; ValueData: "Glide.Image"
Root: HKLM; Subkey: "Software\Glide Image Viewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".gif"; ValueData: "Glide.Image"
Root: HKLM; Subkey: "Software\Glide Image Viewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".bmp"; ValueData: "Glide.Image"
Root: HKLM; Subkey: "Software\Glide Image Viewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".tif"; ValueData: "Glide.Image"
Root: HKLM; Subkey: "Software\Glide Image Viewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".tiff"; ValueData: "Glide.Image"
Root: HKLM; Subkey: "Software\Classes\Glide.Image"; ValueType: string; ValueData: "Image file"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\Glide.Image\DefaultIcon"; ValueType: string; ValueData: "{app}\Glide.exe,0"
Root: HKLM; Subkey: "Software\Classes\Glide.Image\shell\open\command"; ValueType: string; ValueData: """{app}\Glide.exe"" ""%1"""

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
