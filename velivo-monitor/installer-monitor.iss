; Instalator „Velivo Monitor” - sieć ↑↓ i procesor na pasku zadań + widżet na pulpicie. Samodzielny, bez Velivo.
; Kompilacja: ISCC.exe installer-monitor.iss (po dotnet publish do ..\build\monitor)
#define AppName "Velivo Monitor"
#define AppVer "1.22"
#ifndef SrcDir
  #define SrcDir "..\build\monitor"
#endif

[Setup]
AppId={{7A3C1E52-9D4B-4F86-B1E2-VELIVOMONIT1}
AppName={#AppName}
AppVersion={#AppVer}
AppVerName={#AppName} {#AppVer} By MichAel
AppPublisher=MichAel
DefaultDirName={localappdata}\Programs\Velivo Monitor
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=Velivo-Monitor-Setup-{#AppVer}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
SetupIconFile=monitor.ico
UninstallDisplayIcon={app}\VelivoMonitor.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Languages]
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
pl.Auto=Uruchamiaj z Windows
en.Auto=Start with Windows
pl.RunApp=Uruchom Velivo Monitor
en.RunApp=Launch Velivo Monitor

[Tasks]
Name: "autostart"; Description: "{cm:Auto}"

[Files]
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\VelivoMonitor.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "VelivoMonitor"; ValueData: """{app}\VelivoMonitor.exe"""; Tasks: autostart; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "VelivoMonitor"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\VelivoMonitor.exe"; Description: "{cm:RunApp}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM VelivoMonitor.exe"; Flags: runhidden; RunOnceId: "ZamknijMonitor"
