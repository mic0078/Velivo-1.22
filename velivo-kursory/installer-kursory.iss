; Instalator „Velivo Kursory” - animowane kursory myszy w stylu Velivo. Samodzielny, bez Velivo.
; Kompilacja: ISCC.exe installer-kursory.iss (po dotnet build do ..\build\kursory)
#define AppName "Velivo Kursory"
#define AppVer "1.23"
#ifndef SrcDir
  #define SrcDir "..\build\kursory"
#endif

[Setup]
UsePreviousTasks=no
AppId={{5B8E2F14-3C7A-4D91-A6E0-VELIVOKURSOR}
AppName={#AppName}
AppVersion={#AppVer}
AppVerName={#AppName} {#AppVer} By MichAel
AppPublisher=MichAel
DefaultDirName={localappdata}\Programs\Velivo Kursory
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=Velivo-Kursory-Setup-{#AppVer}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
SetupIconFile=kursory.ico
UninstallDisplayIcon={app}\VelivoKursory.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
CloseApplicationsFilter=VelivoKursory.exe

[Languages]
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
pl.Auto=Ikona przy zegarze po starcie Windows (wielkość, włącz / przywróć)
en.Auto=Tray icon after Windows starts (size, on / restore)
pl.Zastosuj=Od razu włącz kursory Velivo
en.Zastosuj=Turn on Velivo cursors now
pl.RunApp=Otwórz Velivo Kursory (podgląd, rozmiar)
en.RunApp=Open Velivo Cursors (preview, size)

[Tasks]
Name: "zastosuj"; Description: "{cm:Zastosuj}"
Name: "autostart"; Description: "{cm:Auto}"

[Files]
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\VelivoKursory.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\VelivoKursory.exe"

[Registry]
Root: HKCU; Subkey: "Software\Velivo\Programy\VelivoKursory"; ValueType: string; ValueName: "Exe"; ValueData: "{app}\VelivoKursory.exe"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Velivo\Programy\VelivoKursory"; ValueType: string; ValueName: "Nazwa"; ValueData: "{#AppName}"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "VelivoKursory"; ValueData: """{app}\VelivoKursory.exe"" --zasobnik"; Tasks: autostart; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "VelivoKursory"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\VelivoKursory.exe"; Parameters: "--zastosuj"; Flags: runhidden waituntilterminated; Tasks: zastosuj
Filename: "{app}\VelivoKursory.exe"; Description: "{cm:RunApp}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\VelivoKursory.exe"; Parameters: "--zasobnik"; Flags: nowait runhidden; Tasks: autostart; Check: WizardSilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM VelivoKursory.exe"; Flags: runhidden; RunOnceId: "ZamknijKursory"
; odinstalowanie: najpierw kursory Windows (albo Twoje poprzednie), potem usunięcie plików
Filename: "{app}\VelivoKursory.exe"; Parameters: "--przywroc"; Flags: runhidden waituntilterminated; RunOnceId: "PrzywrocKursory"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Velivo\Kursory"

[Code]
// działający program (ikona przy zegarze) trzyma plik - zamykamy go przed podmianą (jak w Velivo Monitor)
function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code, I: Integer;
begin
  Result := '';
  for I := 1 to 10 do
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM VelivoKursory.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
    if Code <> 0 then break;
    Sleep(300);
  end;
  Sleep(300);
end;
