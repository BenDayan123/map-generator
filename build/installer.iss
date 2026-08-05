; Inno Setup script — wraps the win-x64 self-contained publish into a friendly Setup.exe.
;
; Built in CI (release.yml) with:
;   ISCC /DAppVersion=<x.y.z> /DSourceDir=<publish\win-x64> build\installer.iss
; Defaults below let you run it locally after `dotnet publish ... -r win-x64 -o publish\win-x64`.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif

#define AppName "My Maps Generator"
#define AppExe "GmapPlanner.App.exe"
#define AppPublisher "My Maps Generator"

[Setup]
; Stable AppId — never change it, or upgrades install as a second app instead of in place.
AppId={{8F2B6E7A-3C4D-4E5F-9A1B-2C3D4E5F6A7B}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
; Per-user install → no admin (UAC) prompt, friendlier for a non-technical user.
PrivilegesRequired=lowest
DefaultDirName={autopf}\MyMapsGenerator
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=.
OutputBaseFilename=MyMapsGenerator-Setup-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\GmapPlanner.App\Assets\icon.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
; The in-app self-update runs this installer with /SILENT after the app has already quit
; itself (so its files are free). Still ask Restart Manager to close any straggler.
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Files]
; Everything dotnet published: the single-file exe plus the loose .playwright node driver.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName} now"; Flags: nowait postinstall skipifsilent
; A silent run is the in-app self-update, which quit the app before installing —
; relaunch it so the update finishes on the new version by itself.
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: WizardSilent
