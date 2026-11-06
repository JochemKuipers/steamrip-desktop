; Inno Setup script for the Windows installer. Compiled by scripts/package-windows.ps1 (and the Release
; workflow on a windows-latest runner) with /DAppVersion=<version> after `dotnet publish -r win-x64`.
; Per-user install (no UAC): %LOCALAPPDATA%\Programs\SteamRIP Desktop. Upgrades install over the top;
; the app's own data (%APPDATA%\steamrip-desktop) and the game library are never touched.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppName "SteamRIP Desktop"
#define AppExe "steamrip-desktop.exe"
#define AppPublisher "Jochem Kuipers"
#define AppUrl "https://github.com/JochemKuipers/steamrip-desktop"

[Setup]
AppId={{7E5C0B6A-2E0B-4D7D-9C0E-4B2F6F1A3D21}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\..\dist
OutputBaseFilename=steamrip-desktop-{#AppVersion}-setup
SetupIconFile=..\..\src\SteamRip.App\Assets\app.ico
LicenseFile=..\..\LICENSE
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Upgrading while the app sits in the tray: ask Windows to close it, then restart it afterwards.
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\..\out\win-x64\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; Parameters: "--show"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Parameters: "--show"; Tasks: desktopicon

[Registry]
; The app writes this Run value itself when "Start with the system" is on; make sure uninstall removes it.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "{#AppName}"; Flags: uninsdeletevalue dontcreatekey

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "--show"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
