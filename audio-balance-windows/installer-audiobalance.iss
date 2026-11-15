; Instalator „Velivo Audio Balance” (Windows) - samodzielny program: cały dźwięk Windows przez silnik Audio Balance (kompresor, LUFS, limiter).
; Potrzebny VB-Audio Virtual Cable (osobno, ze strony producenta - licencja nie pozwala dołączyć go do instalatora); program prowadzi przez instalację.
; Kompilacja: ISCC.exe installer-audiobalance.iss (po dotnet publish do ..\build\audiobalance)
#define AppName "Velivo Audio Balance"
#define AppVer "1.22"
#ifndef SrcDir
  #define SrcDir "..\build\audiobalance"
#endif

[Setup]
AppId={{4C2E8A1F-7B3D-4E9A-A6C5-VELIVOAUDBAL1}
AppName={#AppName}
AppVersion={#AppVer}
AppVerName={#AppName} {#AppVer} By MichAel
AppPublisher=MichAel
DefaultDirName={localappdata}\Programs\Velivo Audio Balance
UsePreviousAppDir=no
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=Velivo-AudioBalance-Setup-{#AppVer}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\app.ico
UninstallDisplayIcon={app}\VelivoAudioBalance.exe
ShowLanguageDialog=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Languages]
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
pl.DesktopIcon=Skrót na pulpicie
en.DesktopIcon=Desktop shortcut
pl.RunApp=Uruchom Velivo Audio Balance
en.RunApp=Launch Velivo Audio Balance

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"

[Files]
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\VelivoAudioBalance.exe"; AppUserModelID: "Velivo.AudioBalanceApp"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\VelivoAudioBalance.exe"; AppUserModelID: "Velivo.AudioBalanceApp"; Tasks: desktopicon

[Run]
Filename: "{app}\VelivoAudioBalance.exe"; Description: "{cm:RunApp}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; najpierw Windows wraca do Twoich głośników (gdy CABLE Input był domyślny - bez programu nie byłoby dźwięku)
Filename: "{app}\VelivoAudioBalance.exe"; Parameters: "--przywroc"; Flags: runhidden waituntilterminated; RunOnceId: "PrzywrocGlosniki"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM VelivoAudioBalance.exe"; Flags: runhidden; RunOnceId: "ZamknijAudioBalance"

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[Registry]
; wpis startu z Windows tworzy sam program (pole w oknie) - przy odinstalowaniu usuwany
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "VelivoAudioBalance"; Flags: uninsdeletevalue
