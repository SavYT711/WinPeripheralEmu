; Inno Setup script for BlePeripheralEmu.
; Build with the free Inno Setup Compiler: https://jrsoftware.org/isinfo.php
;
; Before compiling:
; 1. Run the dotnet publish command first so the .exe referenced below exists:
;    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
; 2. Leave AppId alone from here on - Windows keys upgrades and uninstalls off
;    it, so changing it would make a new version install alongside the old one
;    instead of replacing it.
; 3. Put this file in your project root (same folder as the .csproj), then
;    open it in the Inno Setup Compiler and click Compile.
;
; Output: installer_output\BlePeripheralEmuSetup.exe - that's the file to upload
; as a GitHub Release asset instead of the raw .exe.

#define MyAppName "BlePeripheralEmu"
#define MyAppVersion "2.0.0"
#define MyAppPublisher "SavYT711"
#define MyAppExeName "BlePeripheralEmu.exe"
#define MyPublishDir "bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"

[Setup]
AppId={{E9D74C84-54CB-4128-9B0F-C818F427C196}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
; Stamped into the installer's own file properties, so the version is visible
; without running it.
VersionInfoVersion={#MyAppVersion}
; The payload is a self-contained win-x64 build, so refuse to install anywhere
; it can't run. x64compatible covers ARM64, where it runs under emulation.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; No admin rights required to install or run this app, so don't ask for them.
PrivilegesRequired=lowest
OutputDir=installer_output
OutputBaseFilename={#MyAppName}Setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"
Name: "startupicon"; Description: "Start {#MyAppName} automatically when Windows starts"; GroupDescription: "Startup:"; Flags: unchecked

[InstallDelete]
; The app and its executable were renamed from "iPad Bridge" /
; BlePeripheralPoc.exe. AppId is unchanged, so this runs as an in-place
; upgrade - without these the old executable and its shortcuts would be left
; behind, still launchable and no longer maintained.
Type: files; Name: "{app}\BlePeripheralPoc.exe"
Type: files; Name: "{group}\iPad Bridge.lnk"
Type: files; Name: "{group}\Uninstall iPad Bridge.lnk"
Type: files; Name: "{autodesktop}\iPad Bridge.lnk"
Type: files; Name: "{userstartup}\iPad Bridge.lnk"

[Files]
Source: "{#MyPublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName} now"; Flags: nowait postinstall skipifsilent
