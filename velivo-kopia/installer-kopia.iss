; Instalator „Velivo Kopia” - kopia i przywracanie danych i ustawień wszystkich programów Velivo, kopia sama wg harmonogramu.
; Kompilacja: ISCC.exe installer-kopia.iss (po dotnet build do ..\build\kopia)
#define AppName "Velivo Kopia"
#define AppVer "1.23"
#ifndef SrcDir
  #define SrcDir "..\build\kopia"
#endif

[Setup]
UsePreviousTasks=no
AppId={{3E7A9C15-B2D4-4F68-9E01-VELIVOKOPIA1}
AppName={#AppName}
AppVersion={#AppVer}
AppVerName={#AppName} {#AppVer} By MichAel
AppPublisher=MichAel
DefaultDirName={localappdata}\Programs\Velivo Kopia
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=Velivo-Kopia-Setup-{#AppVer}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
SetupIconFile=kopia.ico
UninstallDisplayIcon={app}\VelivoKopia.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Languages]
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
pl.Auto=Uruchamiaj z Windows (kopia robi się sama wg harmonogramu)
en.Auto=Start with Windows (backups run on schedule)
pl.RunApp=Uruchom Velivo Kopia
en.RunApp=Launch Velivo Backup

[Tasks]
Name: "autostart"; Description: "{cm:Auto}"

[Files]
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\VelivoKopia.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\VelivoKopia.exe"

[Registry]
; spis programów Velivo - Velivo Centrum znajduje program po tym wpisie
Root: HKCU; Subkey: "Software\Velivo\Programy\VelivoKopia"; ValueType: string; ValueName: "Exe"; ValueData: "{app}\VelivoKopia.exe"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Velivo\Programy\VelivoKopia"; ValueType: string; ValueName: "Nazwa"; ValueData: "{#AppName}"
Root: HKCU; Subkey: "Software\Velivo\Programy\VelivoKopia"; ValueType: string; ValueName: "Wersja"; ValueData: "{#AppVer}"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "VelivoKopia"; ValueData: """{app}\VelivoKopia.exe"" --zasobnik"; Tasks: autostart; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "VelivoKopia"; Flags: uninsdeletevalue
; wybór start z Windows zrobiony w instalatorze - program nie ustawia go sam od nowa
Root: HKCU; Subkey: "Software\Velivo\Kopia"; ValueType: string; ValueName: "kopia-start-ustawiony"; ValueData: "1"

[Run]
Filename: "{app}\VelivoKopia.exe"; Description: "{cm:RunApp}"; Flags: nowait postinstall skipifsilent
; instalacja cicha (pakiet Velivo): program startuje od razu przy zegarze, gdy ma startować z Windows
Filename: "{app}\VelivoKopia.exe"; Parameters: "--zasobnik"; Flags: nowait; Tasks: autostart; Check: WizardSilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM VelivoKopia.exe"; Flags: runhidden; RunOnceId: "ZamknijKopia"

[Code]
// program działa przy zegarze bez okna - Windows nie umie go zamknąć sam (Odmowa dostępu przy podmianie pliku); zamykamy go przed kopiowaniem
function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code, I: Integer;
begin
  Result := '';
  for I := 1 to 10 do
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM VelivoKopia.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
    if Code <> 0 then break;
    Sleep(300);
  end;
  Sleep(500);
end;
