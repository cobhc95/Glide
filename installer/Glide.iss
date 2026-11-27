#define MyAppName "Glide Image Viewer"
#define MyAppShortName "Glide"
; Version keys below (MyAppVersion/VersionInfoVersion) are owned by the release lead (4.5.2 -> 4.6.0);
; hygiene passes must not bump them.
#ifndef MyAppVersion
#error MyAppVersion must be supplied by build-installer.cmd
#endif
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
; Glide is a self-contained .NET 10 NativeAOT exe (no runtime prerequisite). .NET 10 supports Windows 10 1607
; (build 14393) and later, the same floor as .NET 8; older Windows gets a clear Setup message instead of a
; silent startup failure.
MinVersion=10.0.14393
; Shows the GNU GPL v3 on a licence page before installation (path is relative to this script).
LicenseFile=..\LICENSE
OutputDir=..\dist-installer
OutputBaseFilename=Glide Setup
SetupIconFile=..\src\Glide.App\Assets\Glide.ico
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
LZMANumFastBytes=273
LZMADictionarySize=65536
WizardStyle=modern
UsePreviousAppDir=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductName={#MyAppName}
VersionInfoDescription=Glide Image Viewer Setup

[Tasks]
; Ticked by default (also for silent installs). Unticked, Setup installs the files only: no Open With,
; no default-viewer claims (Settings -> Windows integration can add them later).
Name: "integrate"; Description: "Integrate with Windows for all supported image formats (Open With, default viewer for formats no other app opens)"; GroupDescription: "Windows integration:"
; Ticked by default (also silent: /MERGETASKS="!thumbnails" opts out). Remembered for upgrades like every
; task. Unticked, Glide's provider is unregistered so Windows' own thumbnails are used; either way the
; choice is written to the installing user's Explorer thumbnails setting and the thumbnail cache is cleared.
Name: "thumbnails"; Description: "Use Glide's thumbnails in File Explorer (recommended)"; GroupDescription: "Windows integration:"

[Files]
Source: "..\.artifacts\stage\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
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
; runasoriginaluser: the "Launch Glide" checkbox must not start the viewer with Setup's elevated token.
Filename: "{app}\Glide.exe"; Description: "Launch Glide"; Flags: nowait postinstall skipifsilent runasoriginaluser

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

  // Security: never take the install directory from HKCU. This setup runs elevated, then runs the
  // installed Glide.exe elevated and registers it machine-wide; a per-user value (writable by any
  // non-elevated process of this user) could point all of that at a user-writable folder.

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

// ---------------------------------------------------------------- install folder permissions ---
// Security (R2): Setup runs elevated, then runs Glide.exe from the install folder elevated
// (--register-*) and registers that folder machine-wide (Open With, the Explorer thumbnail provider
// that every user's Explorer loads). A custom folder such as C:\Glide inherits "Authenticated Users:
// Modify" from the drive root, so any local program could plant or replace a DLL there and have it run
// elevated or inside other users' Explorer. Such folders get the Program Files permission set:
// inheritance removed, owner Administrators, SYSTEM and Administrators full control, Users and app
// packages read and execute.
// - Under Program Files the inherited permissions are already that, so nothing changes.
// - A per-user folder inside this user's own profile (for example %LOCALAPPDATA%\Programs\Glide) is
//   left as it is: the profile is already private to the user, and Glide keeps its settings beside it
//   (%LOCALAPPDATA%\Glide), which needs the user's write access.

function IsUnderFolder(const Path, Root: String): Boolean;
var
  P, R: String;
begin
  Result := False;
  if (Path = '') or (Root = '') then Exit;
  P := AddBackslash(Lowercase(ExpandFileName(Path)));
  R := AddBackslash(Lowercase(ExpandFileName(Root)));
  Result := (Length(R) > 3) and (Copy(P, 1, Length(R)) = R);
end;

function IsProgramFilesFolder(const Dir: String): Boolean;
begin
  Result := IsUnderFolder(Dir, ExpandConstant('{commonpf64}')) or IsUnderFolder(Dir, ExpandConstant('{commonpf32}'));
end;

function IsUserProfileFolder(const Dir: String): Boolean;
begin
  Result := IsUnderFolder(Dir, GetEnv('USERPROFILE')) or IsUnderFolder(Dir, ExpandConstant('{localappdata}')) or
            IsUnderFolder(Dir, ExpandConstant('{userappdata}'));
end;

function InstallFolderNeedsLockdown(const Dir: String): Boolean;
begin
  Result := not IsProgramFilesFolder(Dir) and not IsUserProfileFolder(Dir);
end;

procedure RunIcacls(const Params: String);
var
  ResultCode: Integer;
begin
  if not Exec(ExpandConstant('{sys}\icacls.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    Log(Format('icacls %s failed (exit code %d)', [Params, ResultCode]));
end;

var
  { True when the install folder is Glide's own: created by this Setup or holding a previous Glide. }
  InstallFolderIsGlides: Boolean;

procedure LockDownInstallFolder(const Dir: String; IncludeContents: Boolean);
begin
  { Well-known SIDs, so this works on every Windows display language:
    S-1-5-18 SYSTEM, S-1-5-32-544 Administrators, S-1-5-32-545 Users,
    S-1-15-2-1 ALL APPLICATION PACKAGES, S-1-15-2-2 ALL RESTRICTED APPLICATION PACKAGES. }
  if not DirExists(Dir) then ForceDirectories(Dir);
  { Owner first: the owner of a folder (or of a file someone replaced earlier) keeps the right to change
    its permissions, so it could otherwise grant itself write access again. }
  if IncludeContents then
    RunIcacls('"' + Dir + '" /setowner *S-1-5-32-544 /T /C /Q')
  else
    RunIcacls('"' + Dir + '" /setowner *S-1-5-32-544 /C /Q');
  { /reset drops every explicit entry, /inheritance:r then removes the inherited ones, and the grants
    rebuild exactly the Program Files set. Windows propagates the new inheritable entries to the files. }
  RunIcacls('"' + Dir + '" /reset /C /Q');
  RunIcacls('"' + Dir + '" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F ' +
            '*S-1-5-32-545:(OI)(CI)RX *S-1-15-2-1:(OI)(CI)RX *S-1-15-2-2:(OI)(CI)RX /C /Q');
  { Glide's own folder: also drop explicit entries someone added to files inside it, so every file
    inherits only from the folder. An existing unrelated folder chosen as the install folder keeps its
    other files' explicit entries (they belong to other software). }
  if IncludeContents and InstallFolderIsGlides then
    RunIcacls('"' + AddBackslash(Dir) + '*" /reset /T /C /Q');
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

procedure OpenDefaultAppsForGlide;
var
  ErrorCode: Integer;
begin
  { Windows does not let any app make itself the default for types another app owns (the protected
    UserChoice). Glide already became the default for formats nothing else opens; for the rest, show the
    user Windows' own Default apps page for Glide, where one "Set default" press does it. Opened as the
    original (non-elevated) user, since Settings belongs to that session. Falls back to the general page. }
  if not ShellExecAsOriginalUser('open', 'ms-settings:defaultapps?registeredAppMachine=Glide', '', '',
    SW_SHOWNORMAL, ewNoWait, ErrorCode) then
    ShellExecAsOriginalUser('open', 'ms-settings:defaultapps', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
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

procedure RegisterGlideThumbnails;
var
  ResultCode: Integer;
  ExePath: String;
begin
  { Explorer thumbnail provider: the CLSID plus the recommended per-extension ShellEx entries. Glide
    derives the extension set from its own format registry, so new formats need no installer change. }
  ExePath := ExpandConstant('{app}\Glide.exe');
  if FileExists(ExePath) then
    Exec(ExePath, '--register-thumbnails', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure UnregisterGlideThumbnails;
var
  ResultCode: Integer;
  ExePath: String;
begin
  ExePath := ExpandConstant('{app}\Glide.exe');
  if FileExists(ExePath) then
    Exec(ExePath, '--unregister-thumbnails', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure RefreshGlideThumbnailsForUser(Enabled: Boolean);
var
  ResultCode: Integer;
  ExePath, Params: String;
begin
  { Same as Settings > Explorer thumbnails > Refresh thumbnails, plus the task choice saved to the
    Settings toggle. The thumbnail cache and settings are per-user, so this runs as the original user;
    registration above stays elevated. SHCNE_ASSOCCHANGED makes Explorer pick up the change. }
  ExePath := ExpandConstant('{app}\Glide.exe');
  Params := '--refresh-thumbnails';
  if not Enabled then Params := Params + ' --disabled';
  if FileExists(ExePath) then
    ExecAsOriginalUser(ExePath, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  AppDir: String;
begin
  AppDir := ExpandConstant('{app}');
  { Before the files are copied, so they are never written into a folder others can modify. }
  if CurStep = ssInstall then
  begin
    InstallFolderIsGlides := (not DirExists(AppDir)) or FileExists(AddBackslash(AppDir) + '{#MyAppExeName}');
    if InstallFolderNeedsLockdown(AppDir) then
      LockDownInstallFolder(AppDir, False);
  end;
  if CurStep = ssPostInstall then
  begin
    { Again with every file, before Glide.exe runs elevated from this folder. }
    if InstallFolderNeedsLockdown(AppDir) then
      LockDownInstallFolder(AppDir, True);
    if WizardIsTaskSelected('integrate') then
    begin
      { --register-file-associations: Open With for every recognised format, plus Glide as the default
        viewer for formats no other app handles (recorded, so uninstall removes exactly those). }
      RegisterGlideAssociations;
      if not WizardSilent then
        OpenDefaultAppsForGlide;
    end;
    if WizardIsTaskSelected('thumbnails') then
      RegisterGlideThumbnails
    else
      UnregisterGlideThumbnails;
    RefreshGlideThumbnailsForUser(WizardIsTaskSelected('thumbnails'));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    { Stop the warm host so Glide.exe is runnable and not locked while we remove its registration. }
    StopAllGlideProcesses;
    UnregisterGlideThumbnails;
    UnregisterGlideAssociations;
  end;
end;
