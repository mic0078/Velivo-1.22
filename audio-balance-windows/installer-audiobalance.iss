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
SetupIconFile=..\src\audiobalance.ico
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
pl.WhyDl=Powód: pobieranie z serwera VB-Audio nie udało się (internet, zapora albo antywirus blokuje download.vb-audio.com).
en.WhyDl=Reason: download from the VB-Audio server failed (internet, firewall or antivirus blocks download.vb-audio.com).
pl.WhyZip=Powód: nie udało się rozpakować pobranego pakietu.
en.WhyZip=Reason: the downloaded package could not be unpacked.
pl.WhyRun=Powód: instalator VB-Cable nie uruchomił się (odmowa w oknie Kontrola konta użytkownika?).
en.WhyRun=Reason: the VB-Cable installer did not start (refused in the User Account Control window?).
pl.WhySetup=Powód: instalator VB-Cable zakończył się kodem
en.WhySetup=Reason: the VB-Cable installer ended with code
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
var CableDone: Boolean; CableWhy: String;

// VB-CABLE już jest - sterownik w systemie (vbaudio_cable*.sys); instalator jest 32-bitowy - sprawdzamy System32 i Sysnative
function CableFile(Dir: String): Boolean;
var F: TFindRec;
begin
  Result := FindFirst(Dir + '\drivers\vbaudio_cable*.sys', F);
  if Result then FindClose(F);
end;
function CableInstalled: Boolean;
begin
  Result := CableFile(ExpandConstant('{sys}')) or CableFile(ExpandConstant('{win}\Sysnative')) or CableFile(ExpandConstant('{win}\System32'));
end;

// pobranie z serwera VB-Audio (najpierw nowy pakiet, zapasowo starszy), rozpakowanie (PowerShell) i instalacja po cichu (-i -h) jako administrator; CableWhy - powód błędu słowami
function InstallCable: Boolean;
var Code: Integer; Zip, Dir, Ps: String;
begin
  Result := False; CableWhy := '';
  Zip := ExpandConstant('{tmp}\vbcable.zip'); Dir := ExpandConstant('{tmp}\vbcable');
  // 1) VB-Cable w instalatorze (dodatki\VBCABLE przy budowie) - bez internetu, jak w innych programach
  if FileExists(ExpandConstant('{app}\VBCABLE\VBCABLE_Setup_x64.exe')) then
  begin
    Dir := ExpandConstant('{app}\VBCABLE');
    WizardForm.StatusLabel.Caption := CustomMessage('CableInst');
    if not ShellExec('runas', Dir + '\VBCABLE_Setup_x64.exe', '-i -h', Dir, SW_SHOW, ewWaitUntilTerminated, Code) then begin CableWhy := CustomMessage('WhyRun') + ' ' + SysErrorMessage(Code); exit; end;
    Result := True; exit;
  end;
  // 2) zapas: pobranie z serwera VB-Audio
  WizardForm.StatusLabel.Caption := CustomMessage('CableDl');
  try
    DownloadTemporaryFile('https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip', 'vbcable.zip', '', nil);
  except
    CableWhy := 'Pack45: ' + GetExceptionMessage;
    try
      DownloadTemporaryFile('https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip', 'vbcable.zip', '', nil);
    except
      CableWhy := CustomMessage('WhyDl') + #13#10 + CableWhy + #13#10 + 'Pack43: ' + GetExceptionMessage;
      exit;
    end;
  end;
  Ps := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  if not FileExists(Ps) then Ps := 'powershell.exe';
  Exec(Ps, '-NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -LiteralPath ''' + Zip + ''' -DestinationPath ''' + Dir + ''' -Force"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  if not FileExists(Dir + '\VBCABLE_Setup_x64.exe') then begin CableWhy := CustomMessage('WhyZip') + ' (PowerShell: ' + IntToStr(Code) + ')'; exit; end;
  WizardForm.StatusLabel.Caption := CustomMessage('CableInst');
  if not ShellExec('runas', Dir + '\VBCABLE_Setup_x64.exe', '-i -h', Dir, SW_SHOW, ewWaitUntilTerminated, Code) then begin CableWhy := CustomMessage('WhyRun') + ' ' + SysErrorMessage(Code); exit; end;
  Result := (Code = 0) or CableInstalled;
  if not Result then CableWhy := CustomMessage('WhySetup') + ' ' + IntToStr(Code);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('vbcable') and not CableInstalled then
  begin
    CableDone := InstallCable;
    if not CableDone then MsgBox(CustomMessage('CableFail') + #13#10#13#10 + CableWhy, mbInformation, MB_OK);
  end;
end;

// po instalacji sterownika - ponowne uruchomienie komputera (karta pojawia się w Windows)
function NeedRestart: Boolean;
begin
  Result := CableDone;
end;
