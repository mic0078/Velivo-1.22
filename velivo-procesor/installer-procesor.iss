; Instalator „Velivo Procesor” - inteligentne zarządzanie procesorem (cicho, wydajnie, szybka reakcja) - Intel i AMD. Samodzielny, bez Velivo.
; Kompilacja: ISCC.exe installer-procesor.iss (po dotnet publish do ..\build\procesor)
#define AppName "Velivo Procesor"
#ifndef AppVer
  #define AppVer "1.22"
#endif
#ifndef SrcDir
  #define SrcDir "..\build\procesor"
#endif

[Setup]
UsePreviousTasks=no
AppId={{6D2B9F41-8C3A-4E75-A0D1-VELIVOPROCES}
AppName={#AppName}
AppVersion={#AppVer}
AppVerName={#AppName} {#AppVer} By MichAel
AppPublisher=MichAel
DefaultDirName={localappdata}\Programs\Velivo Procesor
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=Velivo-Procesor-Setup-{#AppVer}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
SetupIconFile=procesor.ico
UninstallDisplayIcon={app}\VelivoProcesor.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Languages]
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
pl.Auto=Uruchamiaj z Windows
en.Auto=Start with Windows
pl.RunApp=Uruchom Velivo Procesor
en.RunApp=Launch Velivo Procesor

[Tasks]
Name: "autostart"; Description: "{cm:Auto}"

[Files]
Source: "{#SrcDir}\*"; DestDir: "{app}"; Excludes: "PawnIO\*,RyzenAdj\*"; Flags: ignoreversion recursesubdirs createallsubdirs
; ⚡ PawnIO (moc AMD: podpisany sterownik + moduł RyzenSMU) - tylko na procesorach AMD; sterownik instaluje sam program (ma uprawnienia administratora)
Source: "{#SrcDir}\PawnIO\*"; DestDir: "{app}\PawnIO"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Check: ProcesorAmd

[InstallDelete]
; RyzenAdj z poprzednich wersji (sterownik WinRing0 blokowany przez Defender) - usuwany; plik uruchomionego sterownika usuwa program po jego zatrzymaniu
Type: filesandordirs; Name: "{app}\RyzenAdj"
Type: filesandordirs; Name: "{app}\PawnIO"; Check: not ProcesorAmd

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\VelivoProcesor.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\VelivoProcesor.exe"

[Registry]
; spis programów Velivo - Velivo Centrum znajduje program po tym wpisie
Root: HKCU; Subkey: "Software\Velivo\Programy\VelivoProcesor"; ValueType: string; ValueName: "Exe"; ValueData: "{app}\VelivoProcesor.exe"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Velivo\Programy\VelivoProcesor"; ValueType: string; ValueName: "Nazwa"; ValueData: "{#AppName}"
Root: HKCU; Subkey: "Software\Velivo\Programy\VelivoProcesor"; ValueType: string; ValueName: "Wersja"; ValueData: "{#AppVer}"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "VelivoProcesor"; ValueData: """{app}\VelivoProcesor.exe"" --zasobnik"; Tasks: autostart; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "VelivoProcesor"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\VelivoProcesor.exe"; Description: "{cm:RunApp}"; Flags: nowait postinstall skipifsilent
; instalacja cicha (pakiet Velivo): program startuje od razu, gdy ma startować z Windows - bez czekania na ponowne uruchomienie komputera
Filename: "{app}\VelivoProcesor.exe"; Parameters: "--zasobnik"; Flags: nowait; Tasks: autostart; Check: WizardSilent

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/end /tn ""Velivo Procesor"""; Flags: runhidden; RunOnceId: "ZatrzymajZadanie"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM VelivoProcesor.exe"; Flags: runhidden; RunOnceId: "ZamknijProcesor"
; ustawienia zasilania Windows sprzed programu wracają, zadanie startu z uprawnieniami (AMD) usuwane
Filename: "{app}\VelivoProcesor.exe"; Parameters: "--przywroc"; Flags: runhidden waituntilterminated; RunOnceId: "PrzywrocWindows"
Filename: "{sys}\schtasks.exe"; Parameters: "/delete /tn ""Velivo Procesor"" /f"; Flags: runhidden; RunOnceId: "UsunZadanie"

[Code]
// procesor AMD - nazwa z rejestru (ta sama, którą czyta program)
function ProcesorAmd: Boolean;
var N: String;
begin
  Result := RegQueryStringValue(HKLM, 'HARDWARE\DESCRIPTION\System\CentralProcessor\0', 'ProcessorNameString', N) and (Pos('AMD', Uppercase(N)) > 0);
end;

// program działa przy zegarze bez okna - Windows nie umie go zamknąć sam (Odmowa dostępu przy podmianie pliku); zamykamy go przed kopiowaniem
function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code, I: Integer;
begin
  Result := '';
  // program uruchomiony przez zadanie z uprawnieniami (moc AMD) - zwykly taskkill go nie zamknie (odmowa dostepu) i stary plik zostawal (2026-11-25);
  // zadanie nalezy do tego konta - jego zatrzymanie konczy program
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/end /tn "Velivo Procesor"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(500);
  for I := 1 to 10 do
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM VelivoProcesor.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
    if Code <> 0 then break;
    Sleep(300);
  end;
  Sleep(500);
end;
