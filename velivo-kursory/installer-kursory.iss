; Instalator „Velivo Kursory” - animowane kursory myszy w stylu Velivo. Samodzielny, bez Velivo.
; Kompilacja: ISCC.exe installer-kursory.iss (po dotnet build do ..\build\kursory)
#define AppName "Velivo Kursory"
#define AppVer "1.22"
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

[Languages]
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
pl.Zastosuj=Od razu włącz kursory Velivo
en.Zastosuj=Turn on Velivo cursors now
pl.RunApp=Otwórz Velivo Kursory (podgląd, rozmiar)
en.RunApp=Open Velivo Cursors (preview, size)

[Tasks]
Name: "zastosuj"; Description: "{cm:Zastosuj}"

[Files]
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\VelivoKursory.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\VelivoKursory.exe"

[Run]
Filename: "{app}\VelivoKursory.exe"; Parameters: "--zastosuj"; Flags: runhidden waituntilterminated; Tasks: zastosuj
Filename: "{app}\VelivoKursory.exe"; Description: "{cm:RunApp}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; odinstalowanie: najpierw kursory Windows (albo Twoje poprzednie), potem usunięcie plików
Filename: "{app}\VelivoKursory.exe"; Parameters: "--przywroc"; Flags: runhidden waituntilterminated; RunOnceId: "PrzywrocKursory"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Velivo\Kursory"
