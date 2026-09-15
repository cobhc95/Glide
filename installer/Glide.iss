#define MyAppName "Glide Image Viewer"
#define MyAppShortName "Glide"
#define MyAppVersion "4.1"
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
VersionInfoVersion=4.1.0.0
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

function GetGlideInstallDir(Param: String): String;
var
  Candidate: String;
begin
  { Prefer the existing legacy/current Glide location so setup upgrades in place. }
  Candidate := ExistingAppPath(HKLM64, 'Software\Microsoft\Windows\CurrentVersion\App Paths\Glide.exe');
  if Candidate = '' then Candidate := ExistingAppPath(HKLM32, 'Software\Microsoft\Windows\CurrentVersion\App Paths\Glide.exe');
  if Candidate = '' then Candidate := ExistingAppPath(HKCU, 'Software\Microsoft\Windows\CurrentVersion\App Paths\Glide.exe');
  if Candidate <> '' then
    Result := Candidate
  else
    Result := ExpandConstant('{autopf}\Glide');
end;

function GlideProcessIsRunning(): Boolean;
var
  ResultCode: Integer;
  Output: TExecOutput;
  I: Integer;
begin
  Result := False;
  if not ExecAndCaptureOutput(ExpandConstant('{cmd}'),
    '/C tasklist /FI "IMAGENAME eq Glide.exe" /FO CSV /NH', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode, Output) then
    Exit;
  if ResultCode <> 0 then
    Exit;
  for I := 0 to GetArrayLength(Output.StdOut) - 1 do
    if Pos('"Glide.exe"', Output.StdOut[I]) > 0 then
    begin
      Result := True;
      Exit;
    end;
end;

function CloseRunningGlide(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{cmd}'),
    '/C taskkill /IM Glide.exe /T /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and
    (ResultCode = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Choice: Integer;
  Attempts: Integer;
begin
  Result := '';
  NeedsRestart := False;
  Attempts := 0;
  while GlideProcessIsRunning() do
  begin
    Choice := MsgBox('Glide is currently running and must be closed before Setup can continue.' + #13#10 + #13#10 +
      'Would you like Setup to close Glide automatically?', mbConfirmation, MB_YESNOCANCEL);
    if Choice = IDCANCEL then
    begin
      Result := 'Please close Glide before continuing Setup.';
      Exit;
    end;
    if Choice = IDYES then
    begin
      CloseRunningGlide();
      Sleep(250);
      Inc(Attempts);
      if Attempts >= 20 then
      begin
        Result := 'Setup could not close Glide. Please close it manually and run Setup again.';
        Exit;
      end;
    end
    else
    begin
      if MsgBox('Please close Glide manually, then choose Yes to retry Setup.', mbInformation, MB_YESNO) <> IDYES then
      begin
        Result := 'Please close Glide before continuing Setup.';
        Exit;
      end;
    end;
  end;
end;
