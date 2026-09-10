#define MyAppName "Glide Image Viewer"
#define MyAppVersion "0.12107-alpha"
#define MyAppExeName "Glide.exe"
#define MyRegisteredAppName "Glide Image Viewer"

[Setup]
AppId={{FD969ECD-ADFE-496E-8A5F-8A97F4DBAC28}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=Glide Image Viewer
DefaultDirName={autopf}\Glide Image Viewer
DefaultGroupName=Glide Image Viewer
DisableProgramGroupPage=yes
PrivilegesRequired=admin
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern dynamic
SetupIconFile=..\Glide.ico
UninstallDisplayIcon={app}\Glide.exe
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no
Compression=lzma2/max
SolidCompression=yes
OutputDir=..\installer_output
OutputBaseFilename=Glide_Setup_Alpha_0.12107_x64
VersionInfoVersion=0.12.107.0
VersionInfoProductName=Glide Image Viewer
VersionInfoProductVersion=0.12107-alpha
VersionInfoDescription=Glide Image Viewer Setup
VersionInfoCompany=Glide Image Viewer

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\dist\Glide.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\codecs\*"; DestDir: "{app}\codecs"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "installed.flag"; DestDir: "{app}"; DestName: "installed.flag"; Flags: ignoreversion; Attribs: hidden
Source: "..\dist\diagnostic_fixtures\*"; DestDir: "{app}\diagnostic_fixtures"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Glide Image Viewer"; Filename: "{app}\Glide.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Glide Image Viewer"; Filename: "{app}\Glide.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; Application-specific ProgID. We deliberately do NOT overwrite the default value of .jpg/.png/etc.
Root: HKLM64; Subkey: "Software\Classes\Glide.Image"; ValueType: string; ValueData: "Glide Image"; Flags: uninsdeletekey
Root: HKLM64; Subkey: "Software\Classes\Glide.Image\DefaultIcon"; ValueType: string; ValueData: "{app}\Glide.exe,0"
Root: HKLM64; Subkey: "Software\Classes\Glide.Image\shell\open\command"; ValueType: string; ValueData: """{app}\Glide.exe"" ""%1"""

; Open With / Applications registration.
Root: HKLM64; Subkey: "Software\Classes\Applications\Glide.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "Glide Image Viewer"; Flags: uninsdeletekey
Root: HKLM64; Subkey: "Software\Classes\Applications\Glide.exe\DefaultIcon"; ValueType: string; ValueData: "{app}\Glide.exe,0"
Root: HKLM64; Subkey: "Software\Classes\Applications\Glide.exe\shell\open\command"; ValueType: string; ValueData: """{app}\Glide.exe"" ""%1"""

; Register in Windows Default Apps as a per-machine application.
Root: HKLM64; Subkey: "Software\Glide Image Viewer\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "Glide Image Viewer"; Flags: uninsdeletekey
Root: HKLM64; Subkey: "Software\Glide Image Viewer\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Fast, lightweight Windows image viewer"
Root: HKLM64; Subkey: "Software\Glide Image Viewer\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: "{app}\Glide.exe,0"
Root: HKLM64; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#MyRegisteredAppName}"; ValueData: "Software\Glide Image Viewer\Capabilities"; Flags: uninsdeletevalue

; App Paths allows Windows and shell integrations to resolve Glide.exe normally.
Root: HKLM64; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\Glide.exe"; ValueType: string; ValueData: "{app}\Glide.exe"; Flags: uninsdeletekey
Root: HKLM64; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\Glide.exe"; ValueType: string; ValueName: "Path"; ValueData: "{app}"

#include "associations.generated.iss"

[Run]
Filename: "{app}\Glide.exe"; Description: "Launch Glide Image Viewer"; Flags: nowait postinstall skipifsilent runasoriginaluser
Filename: "ms-settings:defaultapps?registeredAppMachine=Glide%20Image%20Viewer"; Description: "Choose Glide as the default app for image files"; Flags: postinstall shellexec skipifsilent runasoriginaluser
