; Instalator Velivo - kompilacja: ISCC.exe installer.iss (po dotnet publish do ..\build\velivo)
#define AppName "Velivo"
#define AppVer "1.22"

[Setup]
; AppId bez zmian od czasow nazwy "Przegladarka" - dzieki temu instalator aktualizuje stara wersje
AppId={{6E4C2B7A-3F1D-4C8E-9A51-PRZEGLADARKA01}
AppName={#AppName}
AppVersion={#AppVer}
AppVerName={#AppName} {#AppVer}
AppPublisher=andro
DefaultDirName={localappdata}\Programs\Velivo
UsePreviousAppDir=no
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
OutputDir=..\Instalator
OutputBaseFilename=Velivo-Setup-{#AppVer}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
SetupIconFile=app.ico
UninstallDisplayIcon={app}\Velivo.exe
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Languages]
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"

[Tasks]
Name: "desktopicon"; Description: "Skrót na pulpicie"; GroupDescription: "Skróty:"

[InstallDelete]
; pozostalosci po poprzednich nazwach programu ("Przegladarka", "Tarcza") - tylko pliki programu i skroty,
; folder z danymi uzytkownika (%LOCALAPPDATA%\Przegladarka) zostaje nietkniety
Type: filesandordirs; Name: "{localappdata}\Programs\Przegladarka"
Type: filesandordirs; Name: "{localappdata}\Programs\Tarcza"
Type: files; Name: "{autodesktop}\Przeglądarka.lnk"
Type: files; Name: "{autoprograms}\Przeglądarka.lnk"
Type: files; Name: "{autodesktop}\Tarcza.lnk"
Type: files; Name: "{autoprograms}\Tarcza.lnk"

[Files]
Source: "..\build\velivo\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\Velivo.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Velivo.exe"; Tasks: desktopicon

[Registry]
; rejestracja jako przegladarka (pojawia sie na liscie "Aplikacje domyslne"); wybor robi uzytkownik w Ustawieniach
Root: HKCU; Subkey: "Software\Classes\VelivoHTML"; ValueType: string; ValueName: ""; ValueData: "Velivo HTML Document"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\VelivoHTML\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\Velivo.exe,0"
Root: HKCU; Subkey: "Software\Classes\VelivoHTML\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Velivo.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Velivo"; ValueType: string; ValueName: ""; ValueData: "Velivo"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Velivo\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\Velivo.exe,0"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Velivo\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Velivo.exe"""
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Velivo\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "Velivo"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Velivo\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Velivo – lekka i prywatna przeglądarka"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Velivo\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: "{app}\Velivo.exe,0"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Velivo\Capabilities\URLAssociations"; ValueType: string; ValueName: "http"; ValueData: "VelivoHTML"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Velivo\Capabilities\URLAssociations"; ValueType: string; ValueName: "https"; ValueData: "VelivoHTML"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Velivo\Capabilities\FileAssociations"; ValueType: string; ValueName: ".htm"; ValueData: "VelivoHTML"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Velivo\Capabilities\FileAssociations"; ValueType: string; ValueName: ".html"; ValueData: "VelivoHTML"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Velivo\Capabilities\FileAssociations"; ValueType: string; ValueName: ".shtml"; ValueData: "VelivoHTML"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Velivo\Capabilities\FileAssociations"; ValueType: string; ValueName: ".xhtml"; ValueData: "VelivoHTML"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Velivo\Capabilities\FileAssociations"; ValueType: string; ValueName: ".svg"; ValueData: "VelivoHTML"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Velivo\Capabilities\StartMenu"; ValueType: string; ValueName: "StartMenuInternet"; ValueData: "Velivo"
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "Velivo"; ValueData: "Software\Clients\StartMenuInternet\Velivo\Capabilities"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Velivo.exe"; Description: "Uruchom {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
function HasDesktopRuntime10: Boolean;
var FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec) then
  begin
    Result := True;
    FindClose(FindRec);
  end;
end;

function HasWebView2: Boolean;
var v: String;
begin
  Result :=
    RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) or
    RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v);
  Result := Result and (v <> '') and (v <> '0.0.0.0');
end;

var
  DataPage: TInputOptionWizardPage;

function UserDataDir: String;
begin
  Result := ExpandConstant('{localappdata}\Przegladarka');
end;

function QuickAccessDataDir: String;
begin
  Result := ExpandConstant('{localappdata}\Programs\Velivo\Szybki Dostęp');
end;

function HasOldData: Boolean;
begin
  Result := DirExists(UserDataDir) or DirExists(QuickAccessDataDir);
end;

procedure InitializeWizard;
begin
  if not HasOldData then exit;
  DataPage := CreateInputOptionPage(wpSelectTasks,
    'Poprzednie ustawienia', 'Wykryto dane z wcześniejszej instalacji Velivo.',
    'Wybierz, co zrobić z ustawieniami, zakładkami, historią, hasłami, profilami i Szybkim Dostępem:',
    True, False);
  DataPage.Add('Zachowaj moje ustawienia i dane (aktualizacja)');
  DataPage.Add('Czysta instalacja – zacznij od zera (stare dane zostaną przeniesione do kopii zapasowej)');
  DataPage.SelectedValueIndex := 0;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (DataPage <> nil) and (CurPageID = DataPage.ID) and (DataPage.SelectedValueIndex = 1) then
    Result := MsgBox('Czysta instalacja: ustawienia, zakładki, historia, zapisane hasła przeglądarki i Szybki Dostęp ' +
      'zostaną przeniesione do kopii zapasowej (folder z dopiskiem "kopia-..."), a Velivo uruchomi się jak nowe.' + #13#10#13#10 +
      'Kontynuować?', mbConfirmation, MB_YESNO) = IDYES;
end;

procedure BackupDir(Dir: String; Stamp: String);
begin
  if DirExists(Dir) then
    if not RenameFile(Dir, Dir + '.kopia-' + Stamp) then
      MsgBox('Nie udało się przenieść folderu:' + #13#10 + Dir + #13#10 +
        'Zamknij Velivo i spróbuj ponownie albo usuń go ręcznie.', mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Stamp: String;
begin
  if (CurStep = ssInstall) and (DataPage <> nil) and (DataPage.SelectedValueIndex = 1) then
  begin
    Stamp := GetDateTimeString('yyyymmdd-hhnnss', '-', '-');
    BackupDir(UserDataDir, Stamp);
    BackupDir(QuickAccessDataDir, Stamp);
  end;
end;

function InitializeSetup: Boolean;
var Err: Integer;
begin
  Result := True;
  if not HasDesktopRuntime10 then
  begin
    if MsgBox('Brakuje .NET 10 Desktop Runtime (x64), potrzebnego do działania przeglądarki.' + #13#10 +
              'Otworzyć stronę pobierania? Po instalacji runtime uruchom instalator ponownie.',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, Err);
    Result := False;
    exit;
  end;
  if not HasWebView2 then
    if MsgBox('Nie wykryto Microsoft Edge WebView2 Runtime (zwykle jest w Windows 11).' + #13#10 +
              'Bez niego przeglądarka nie wyświetli stron. Otworzyć stronę pobierania?',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://developer.microsoft.com/microsoft-edge/webview2/', '', '', SW_SHOWNORMAL, ewNoWait, Err);
end;
