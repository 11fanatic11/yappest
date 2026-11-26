; Build with ..\build.ps1.  Pass /DLite to leave out the VB-Cable driver pack (the public/update build downloads it from vb-audio.com when needed).
#define AppName "Yappest"
#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif
#define AppExe "Yappest.exe"

[Setup]
AppId={{B7C1E6A0-5B0E-4E7B-9D67-6C2E4F1A9D31}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Fanatic
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; per-user install: no admin prompt. The audio driver step asks for admin once, from inside the app.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=Output
#ifdef Lite
OutputBaseFilename=Yappest-Setup
#else
OutputBaseFilename=Yappest-Setup-{#AppVersion}-full
#endif
SetupIconFile=..\src\Yappest\Assets\yappest.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; tells Explorer to refresh cached icons, so the taskbar and Start menu pick up a changed logo
ChangesAssociations=yes
; ask the user to close Yappest before updating
AppMutex=Yappest.SingleInstance
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "startup"; Description: "Start Yappest with Windows (runs quietly in the tray)"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
#ifdef Lite
Source: "..\dist\Yappest\*"; DestDir: "{app}"; Excludes: "vendor\vbcable\*,streamdeck\tools\*"; Flags: ignoreversion recursesubdirs createallsubdirs
#else
Source: "..\dist\Yappest\*"; DestDir: "{app}"; Excludes: "streamdeck\tools\*"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Yappest"; ValueData: """{app}\{#AppExe}"" --background"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch Yappest"; Flags: nowait postinstall skipifsilent
; after a quiet in-app update, open the new version again
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: ShouldRelaunch

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "KillYappest"

[Code]
function ShouldRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;
