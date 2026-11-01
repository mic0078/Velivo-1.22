; Instalator „Velivo Komunikator” (Windows) - samodzielny program: rozmowy, pliki, rozmowy wideo i serwer (pośrednik), bez przeglądarki Velivo.
; Kompilacja: ISCC.exe installer-komunikator.iss (po dotnet publish do ..\build\komunikator)
#define AppName "Velivo Komunikator"
#define AppVer "1.22"
#ifndef SrcDir
  #define SrcDir "..\build\komunikator"
#endif

[Setup]
AppId={{9B1F7C2E-5A44-4E0B-8D6E-VELIVOKOMUN01}
AppName={#AppName}
AppVersion={#AppVer}
AppVerName={#AppName} {#AppVer} By MichAel
AppPublisher=MichAel
DefaultDirName={localappdata}\Programs\Velivo Komunikator
UsePreviousAppDir=no
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=Velivo-Komunikator-Setup-{#AppVer}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\komunikator.ico
UninstallDisplayIcon={app}\VelivoKomunikator.exe
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
pl.RunApp=Uruchom Velivo Komunikator
en.RunApp=Launch Velivo Komunikator
pl.GetWebView=Pobieranie Microsoft Edge WebView2 (rozmowy wideo)…
en.GetWebView=Downloading Microsoft Edge WebView2 (video calls)…
pl.StillRunning=Velivo Komunikator nadal działa i instalator nie może go zamknąć. Zamknij go: ikona przy zegarze → Zamknij komunikator, a potem uruchom instalator ponownie.
en.StillRunning=Velivo Komunikator is still running and the installer cannot close it. Close it: tray icon → Close messenger, then run the installer again.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"

[Files]
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; własna ikona i osobny identyfikator - po przypięciu własny przycisk na pasku zadań
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\VelivoKomunikator.exe"; IconFilename: "{app}\komunikator.ico"; AppUserModelID: "Velivo.KomunikatorApp"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\VelivoKomunikator.exe"; IconFilename: "{app}\komunikator.ico"; AppUserModelID: "Velivo.KomunikatorApp"; Tasks: desktopicon

[Registry]
; ▶ start z Windows - serwer i rozmowy działają zawsze (po cichu, w zasobniku); przy odinstalowaniu wpis znika
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Velivo Komunikator"; ValueData: """{app}\VelivoKomunikator.exe"" --autostart"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\VelivoKomunikator.exe"; Description: "{cm:RunApp}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM VelivoKomunikator.exe"; Flags: runhidden; RunOnceId: "ZamknijKomunikator"

[Code]
function HasWebView2: Boolean;
var v: String;
begin
  Result :=
    RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) or
    RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v);
  Result := Result and (v <> '') and (v <> '0.0.0.0');
end;

function Running(Exe: String): Boolean;
var Code: Integer;
begin
  Exec(ExpandConstant('{cmd}'), '/C tasklist /FI "IMAGENAME eq ' + Exe + '" | find /I "' + Exe + '"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := Code = 0;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code, I: Integer;
begin
  Result := '';
  if not HasWebView2 then
  begin
    WizardForm.StatusLabel.Caption := CustomMessage('GetWebView');
    try
      DownloadTemporaryFile('https://go.microsoft.com/fwlink/p/?LinkId=2124703', 'webview2-setup.exe', '', nil);
      ShellExec('', ExpandConstant('{tmp}\webview2-setup.exe'), '/silent /install', '', SW_SHOW, ewWaitUntilTerminated, Code);
    except
    end;
  end;
  // stara wersja Komunikatora - zamknięta, póki pliki się nie zwolnią (do 10 s)
  for I := 1 to 20 do
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM VelivoKomunikator.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Sleep(500);
    if not Running('VelivoKomunikator.exe') then break;
  end;
  if Running('VelivoKomunikator.exe') then begin Result := CustomMessage('StillRunning'); exit; end;
end;

// 🛡 zapora Windows: ten program przyjmuje połączenia w każdej sieci (kabel, Wi-Fi) - jedno „Tak” przy instalacji, gdy właściciel jest przy komputerze;
// kasowana tylko własna reguła (wcześniej wszystkie reguły programu – gdy dodanie nowej się nie udało, Velivo zostawało bez żadnej i nic nie przechodziło)
procedure OpenFirewall(Exe: String);
var Code: Integer;
begin
  ShellExec('runas', ExpandConstant('{cmd}'), '/c "netsh advfirewall firewall delete rule name="' + ExtractFileName(Exe) + ' - Velivo" & netsh advfirewall firewall add rule name="' + ExtractFileName(Exe) + ' - Velivo" dir=in action=allow program="' + Exe + '" enable=yes profile=any' + ' & netsh advfirewall firewall delete rule name="Velivo - porty TCP" & netsh advfirewall firewall add rule name="Velivo - porty TCP" dir=in action=allow protocol=TCP localport=41917-41929,443 enable=yes profile=any' + ' & netsh advfirewall firewall delete rule name="Velivo - porty UDP" & netsh advfirewall firewall add rule name="Velivo - porty UDP" dir=in action=allow protocol=UDP localport=41917-41919 enable=yes profile=any"', '', SW_HIDE, ewWaitUntilTerminated, Code);   // porty Velivo i komunikatora zawsze otwarte (każda sieć), także niezależnie od ścieżki programu
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    SaveStringToFile(ExpandConstant('{app}\language.txt'), ActiveLanguage, False);   // język wybrany w instalatorze
    if not WizardSilent then OpenFirewall(ExpandConstant('{app}\VelivoKomunikator.exe'));
  end;
end;
