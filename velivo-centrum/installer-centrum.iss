; Instalator „Velivo Centrum” - jedna ikona przy zegarze i lista programów Velivo. Samodzielny, bez Velivo.
; Kompilacja: ISCC.exe installer-centrum.iss (po dotnet publish do ..\build\centrum)
#define AppName "Velivo Centrum"
#define AppVer "1.22"
#ifndef SrcDir
  #define SrcDir "..\build\centrum"
#endif

[Setup]
UsePreviousTasks=no
AppId={{8B4D2F63-AE5C-4A97-C2F3-VELIVOCENTR1}
AppName={#AppName}
AppVersion={#AppVer}
AppVerName={#AppName} {#AppVer} By MichAel
AppPublisher=MichAel
DefaultDirName={localappdata}\Programs\Velivo Centrum
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=Velivo-Centrum-Setup-{#AppVer}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
SetupIconFile=centrum.ico
UninstallDisplayIcon={app}\VelivoCentrum.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Languages]
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
pl.Auto=Uruchamiaj z Windows
en.Auto=Start with Windows
pl.RunApp=Uruchom Velivo Centrum
en.RunApp=Launch Velivo Centrum

[Tasks]
Name: "autostart"; Description: "{cm:Auto}"

[Files]
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\VelivoCentrum.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\VelivoCentrum.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "VelivoCentrum"; ValueData: """{app}\VelivoCentrum.exe"""; Tasks: autostart; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "VelivoCentrum"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\VelivoCentrum.exe"; Description: "{cm:RunApp}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM VelivoCentrum.exe"; Flags: runhidden; RunOnceId: "ZamknijCentrum"
