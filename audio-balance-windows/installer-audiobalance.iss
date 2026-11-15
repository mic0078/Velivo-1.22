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
pl.Cable=Pobierz i zainstaluj VB-Audio Virtual Cable (polecane – bez niego program nie działa). VB-CABLE to darmowy program firmy VB-Audio (donationware, vb-audio.com). Windows zapyta raz o zgodę na sterownik – kliknij „Zainstaluj”.
en.Cable=Download and install VB-Audio Virtual Cable (recommended – the program does not work without it). VB-CABLE is free software by VB-Audio (donationware, vb-audio.com). Windows will ask once to allow the driver – click "Install".
pl.CableGroup=Wirtualna karta dźwiękowa:
en.CableGroup=Virtual sound card:
pl.CableDl=Pobieranie VB-Audio Virtual Cable (vb-audio.com)…
en.CableDl=Downloading VB-Audio Virtual Cable (vb-audio.com)…
pl.CableInst=Instalowanie VB-Audio Virtual Cable – Windows zapyta o zgodę na sterownik, kliknij „Zainstaluj”…
en.CableInst=Installing VB-Audio Virtual Cable – Windows will ask to allow the driver, click "Install"…
pl.CableFail=Nie udało się pobrać albo zainstalować VB-Audio Virtual Cable. Program pokaże, jak zainstalować go ręcznie (vb-audio.com/Cable).
en.CableFail=VB-Audio Virtual Cable could not be downloaded or installed. The program will show how to install it manually (vb-audio.com/Cable).
en.RunApp=Launch Velivo Audio Balance

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"
Name: "vbcable"; Description: "{cm:Cable}"; GroupDescription: "{cm:CableGroup}"; Check: not CableInstalled

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

[Code]
var CableDone: Boolean;

// VB-CABLE już jest - sterownik w systemie (pliki vbaudio_cable*.sys)
function CableInstalled: Boolean;
var F: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{sys}\drivers\vbaudio_cable*.sys'), F);
  if Result then FindClose(F);
end;

// pobranie z serwera VB-Audio (najpierw nowy pakiet, zapasowo starszy), rozpakowanie (PowerShell - działa też na starszym Windows 10) i instalacja po cichu (-i -h) jako administrator
function InstallCable: Boolean;
var Code: Integer; Zip, Dir: String;
begin
  Result := False;
  Zip := ExpandConstant('{tmp}\vbcable.zip'); Dir := ExpandConstant('{tmp}\vbcable');
  WizardForm.StatusLabel.Caption := CustomMessage('CableDl');
  try
    DownloadTemporaryFile('https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip', 'vbcable.zip', '', nil);
  except
    try
      DownloadTemporaryFile('https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip', 'vbcable.zip', '', nil);
    except
      exit;
    end;
  end;
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), '-NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -LiteralPath ''' + Zip + ''' -DestinationPath ''' + Dir + ''' -Force"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  if not FileExists(Dir + '\VBCABLE_Setup_x64.exe') then exit;
  WizardForm.StatusLabel.Caption := CustomMessage('CableInst');
  if not ShellExec('runas', Dir + '\VBCABLE_Setup_x64.exe', '-i -h', Dir, SW_SHOW, ewWaitUntilTerminated, Code) then exit;
  Result := CableInstalled;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('vbcable') and not CableInstalled then
  begin
    CableDone := InstallCable;
    if not CableDone then MsgBox(CustomMessage('CableFail'), mbInformation, MB_OK);
  end;
end;

// po instalacji sterownika - ponowne uruchomienie komputera (karta pojawia się w Windows)
function NeedRestart: Boolean;
begin
  Result := CableDone;
end;
