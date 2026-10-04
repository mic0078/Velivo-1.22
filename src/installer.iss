; Instalator Velivo - kompilacja: ISCC.exe installer.iss (po dotnet publish do ..\build\velivo)
#define AppName "Velivo"
#define AppVer "1.22"
#ifndef SrcDir
  #define SrcDir "..\build\velivo"
#endif
#ifndef OutName
  #define OutName "Velivo-Setup-" + AppVer
#endif

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
OutputBaseFilename={#OutName}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
SetupIconFile=app.ico
UninstallDisplayIcon={app}\Velivo.exe
DisableProgramGroupPage=yes
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
pl.Shortcuts=Skróty:
en.Shortcuts=Shortcuts:
pl.RunApp=Uruchom %1
en.RunApp=Launch %1
pl.AppDesc=Velivo – lekka i prywatna przeglądarka
en.AppDesc=Velivo – a light and private browser
pl.OldDataTitle=Poprzednie ustawienia
en.OldDataTitle=Previous settings
pl.OldDataDesc=Wykryto dane z wcześniejszej instalacji Velivo.
en.OldDataDesc=Data from an earlier Velivo installation was found.
pl.OldDataQuestion=Wybierz, co zrobić z ustawieniami, zakładkami, historią, hasłami, profilami i Szybkim Dostępem:
en.OldDataQuestion=Choose what to do with settings, bookmarks, history, passwords, profiles and Quick Access:
pl.KeepData=Zachowaj moje ustawienia i dane (aktualizacja)
en.KeepData=Keep my settings and data (update)
pl.CleanInstall=Czysta instalacja – zacznij od zera (stare dane zostaną przeniesione do kopii zapasowej)
en.CleanInstall=Clean install – start from scratch (old data will be moved to a backup)
pl.CleanConfirm=Czysta instalacja: ustawienia, zakładki, historia, zapisane hasła przeglądarki i Szybki Dostęp zostaną przeniesione do kopii zapasowej (folder z dopiskiem "kopia-..."), a Velivo uruchomi się jak nowe.%n%nKontynuować?
en.CleanConfirm=Clean install: settings, bookmarks, history, saved browser passwords and Quick Access will be moved to a backup (folder ending with "kopia-..."), and Velivo will start like new.%n%nContinue?
pl.MoveFailed=Nie udało się przenieść folderu:%n%1%nZamknij Velivo i spróbuj ponownie albo usuń go ręcznie.
en.MoveFailed=Could not move the folder:%n%1%nClose Velivo and try again, or delete it manually.
pl.GetDotnet=Pobieranie i instalacja .NET 10 Desktop Runtime (jednorazowo, ok. 55 MB)…
en.GetDotnet=Downloading and installing .NET 10 Desktop Runtime (one time, about 55 MB)…
pl.GetWebView=Pobieranie i instalacja Microsoft Edge WebView2 Runtime…
en.GetWebView=Downloading and installing Microsoft Edge WebView2 Runtime…
pl.NeedDotnet=Brakuje .NET 10 Desktop Runtime (x64), potrzebnego do działania przeglądarki.%nOtworzyć stronę pobierania? Po instalacji runtime uruchom instalator ponownie.
en.NeedDotnet=.NET 10 Desktop Runtime (x64), required by the browser, is missing.%nOpen the download page? After installing the runtime, run this installer again.
pl.NeedWebView=Nie wykryto Microsoft Edge WebView2 Runtime (zwykle jest w Windows 11).%nBez niego przeglądarka nie wyświetli stron. Otworzyć stronę pobierania?
en.NeedWebView=Microsoft Edge WebView2 Runtime was not found (usually included in Windows 11).%nWithout it the browser cannot display pages. Open the download page?

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:Shortcuts}"

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
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

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
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\Velivo\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "{cm:AppDesc}"
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
Filename: "{app}\Velivo.exe"; Description: "{cm:RunApp,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\unins*.old-*"

[Code]
function HasDesktopRuntime10: Boolean;
var FindRec: TFindRec;
begin
  // potrzebne OBA skladniki: Microsoft.NETCore.App i Microsoft.WindowsDesktop.App w wersji 10
  // (sam WindowsDesktop bez NETCore konczyl sie bledem "You must install or update .NET")
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec) then
  begin
    FindClose(FindRec);
    if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.NETCore.App\10.*'), FindRec) then
    begin
      Result := True;
      FindClose(FindRec);
    end;
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
    CustomMessage('OldDataTitle'), CustomMessage('OldDataDesc'), CustomMessage('OldDataQuestion'), True, False);
  DataPage.Add(CustomMessage('KeepData'));
  DataPage.Add(CustomMessage('CleanInstall'));
  DataPage.SelectedValueIndex := 0;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (DataPage <> nil) and (CurPageID = DataPage.ID) and (DataPage.SelectedValueIndex = 1) then
    Result := MsgBox(CustomMessage('CleanConfirm'), mbConfirmation, MB_YESNO) = IDYES;
end;

procedure BackupDir(Dir: String; Stamp: String);
begin
  if DirExists(Dir) then
    if not RenameFile(Dir, Dir + '.kopia-' + Stamp) then
      MsgBox(FmtMessage(CustomMessage('MoveFailed'), [Dir]), mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Stamp: String;
begin
  // jezyk wybrany w instalatorze = jezyk programu (Velivo czyta go, gdy w ustawieniach jest "automatycznie")
  if CurStep = ssPostInstall then
    SaveStringToFile(ExpandConstant('{app}\language.txt'), ActiveLanguage, False);
  if (CurStep = ssInstall) and (DataPage <> nil) and (DataPage.SelectedValueIndex = 1) then
  begin
    Stamp := GetDateTimeString('yyyymmdd-hhnnss', '-', '-');
    BackupDir(UserDataDir, Stamp);
    BackupDir(QuickAccessDataDir, Stamp);
  end;
end;

// Zablokowane unins000.exe/.dat (Eksplorator, indeksowanie, antywirus albo zawieszony ukryty proces Velivo)
// konczyly instalacje bledem "plik jest uzywany przez inny proces" przy zapisie informacji o dezinstalacji.
// Zmiana nazwy dziala nawet na otwartym pliku - stare kopie sprzatamy przy nastepnej okazji.
procedure MoveAsideUninstallFiles;
var FindRec: TFindRec; Dir, Stamp: String; Names: TStringList; I: Integer;
begin
  Dir := ExpandConstant('{app}');
  if FindFirst(Dir + '\unins*.old-*', FindRec) then
  begin
    repeat DeleteFile(Dir + '\' + FindRec.Name); until not FindNext(FindRec);
    FindClose(FindRec);
  end;
  Stamp := GetDateTimeString('yyyymmddhhnnss', #0, #0);
  // najpierw lista, potem zmiana nazw - zmiana w trakcie przegladania katalogu moglaby trafic drugi raz na ten sam plik
  Names := TStringList.Create;
  try
    if FindFirst(Dir + '\unins???.*', FindRec) then
    begin
      repeat
        if Pos('.old-', FindRec.Name) = 0 then Names.Add(FindRec.Name);
      until not FindNext(FindRec);
      FindClose(FindRec);
    end;
    for I := 0 to Names.Count - 1 do
      RenameFile(Dir + '\' + Names[I], Dir + '\' + Names[I] + '.old-' + Stamp);
  finally
    Names.Free;
  end;
end;

// brakujace skladniki pobieramy i instalujemy sami - jeden instalator wystarczy
function InstallPrereqs: String;
var Code: Integer; F: String;
begin
  Result := '';
  if not HasDesktopRuntime10 then
  begin
    WizardForm.StatusLabel.Caption := CustomMessage('GetDotnet');
    try
      DownloadTemporaryFile('https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe', 'dotnet-desktop.exe', '', nil);
      F := ExpandConstant('{tmp}\dotnet-desktop.exe');
      if not ShellExec('', F, '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, Code) or not HasDesktopRuntime10 then
        Result := CustomMessage('NeedDotnet');
    except
      Result := CustomMessage('NeedDotnet');
    end;
    if Result <> '' then exit;
  end;
  if not HasWebView2 then
  begin
    WizardForm.StatusLabel.Caption := CustomMessage('GetWebView');
    try
      DownloadTemporaryFile('https://go.microsoft.com/fwlink/p/?LinkId=2124703', 'webview2-setup.exe', '', nil);
      ShellExec('', ExpandConstant('{tmp}\webview2-setup.exe'), '/silent /install', '', SW_SHOW, ewWaitUntilTerminated, Code);
    except
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code: Integer;
begin
  Result := InstallPrereqs;
  if Result <> '' then exit;
  // ukryte procesy Velivo (np. sprzatanie przy zamykaniu) razem z ich procesami WebView2
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM Velivo.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(500);
  MoveAsideUninstallFiles;
end;

function InitializeSetup: Boolean;
var Err: Integer;
begin
  // .NET i WebView2 doinstalowujemy sami w PrepareToInstall (pobranie z Microsoft, bez pytan)
  Result := True;
end;
