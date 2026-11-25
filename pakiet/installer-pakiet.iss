; 📦 Velivo – pakiet: jeden instalator, wybór programów (Pełny / Typowy / Własny) - dokumentacja/makieta-instalator-pakiet.html.
; W środku gotowe instalatory programów z build\instalator (budowane wcześniej przez zbuduj.ps1 i zbuduj-*.ps1); każdy instaluje się
; po cichu jak osobno (własna ikona, własne odinstalowanie, wspólne dane). Już zainstalowane programy zaznaczone same (aktualizacja).
#ifndef AppVer
  #define AppVer "1.22"
#endif
; Monitor, Kursory i Centrum - .NET Framework z Windows (bez .NET 10), zawsze 1.22
#define Fw "1.22"
#define Src "..\build\instalator\"

[Setup]
AppId={{2F6A9C31-7E4B-4D52-9B10-VELIVOPAKIET}
AppName=Velivo – pakiet
AppVersion={#AppVer}
AppVerName=Velivo {#AppVer} – pakiet By MichAel
AppPublisher=MichAel
DefaultDirName={localappdata}\Programs\Velivo Pakiet
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=Velivo-Pakiet-Setup-{#AppVer}
SetupIconFile=..\src\app.ico
Compression=lzma2/fast
SolidCompression=no
WizardStyle=modern
Uninstallable=no
CreateUninstallRegKey=no

[Languages]
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
pl.SrvAuto=Uruchamiaj Serwer filmów razem z Windows (w tle – Twoje komputery i telefon widzą bibliotekę)
en.SrvAuto=Start the Movie server with Windows (in the background – your computers and phone see the library)
pl.NieZamkniete=Nie udało się zamknąć wszystkich programów Velivo (prawdopodobnie któryś działa jako administrator). Uruchom komputer ponownie i zainstaluj jeszcze raz.
en.NieZamkniete=Could not close all Velivo programs (one is probably running as administrator). Restart the computer and install again.
pl.TFull=Pełny – wszystkie programy Velivo
en.TFull=Full – all Velivo programs
pl.TTyp=Typowy – programy do codziennej pracy (bez Kursorów i Serwera filmów)
en.TTyp=Typical – everyday programs (without Cursors and Movie server)
pl.TOwn=Własny – wybierz sam
en.TOwn=Custom – choose yourself
pl.CVelivo=Velivo wszystko w jednym (zamiast osobnych programów – zwykle niepotrzebne)
en.CVelivo=Velivo all-in-one (instead of separate programs – usually not needed)
pl.CPlayer=Odtwarzacz filmów
en.CPlayer=Video player
pl.CIptv=Telewizja IPTV
en.CIptv=IPTV television
pl.CAb=Velivo Audio Balance (wyrównywanie głośności)
en.CAb=Velivo Audio Balance (volume levelling)
pl.CMon=Velivo Monitor (sieć, procesor na pasku)
en.CMon=Velivo Monitor (network, CPU on the taskbar)
pl.CKur=Velivo Kursory
en.CKur=Velivo Cursors
pl.CProc=Velivo Procesor (cicho, wydajnie – sam dobiera moc procesora)
en.CProc=Velivo Processor (quiet, efficient – adjusts CPU power itself)
pl.CCen=Velivo Centrum (wszystkie programy w jednym miejscu)
en.CCen=Velivo Centre (all programs in one place)
pl.CSep=Programy Velivo (każdy działa sam)
en.CSep=Velivo programs (each works on its own)
pl.COdt=Velivo Odtwarzacz (sam odtwarzacz)
en.COdt=Velivo Player (player only)
pl.CPrz=Velivo Przeglądarka (sama przeglądarka)
en.CPrz=Velivo Browser (browser only)
pl.CKla=Velivo Klawiatura (sama klawiatura)
en.CKla=Velivo Keyboard (keyboard only)
pl.CKom=Velivo Komunikator (sam komunikator)
en.CKom=Velivo Messenger (messenger only)
pl.CSrv=Velivo Serwer filmów (biblioteka dla moich komputerów)
en.CSrv=Velivo Movie Server (library for my computers)
pl.ObceKonto=Uruchomiono jako administrator – programy Velivo zainstalują się dla konta: %1 (nie dla konta, na którym pracujesz, jeśli wpisano hasło innego konta).%n%nKażde konto ma własne Velivo. Uruchom instalator zwykłym dwuklikiem, bez „Uruchom jako administrator”.%n%nInstalować mimo to dla konta %1?
pl.Inst=Instaluję: %1…
pl.Odinst=Odinstaluj wszystkie programy Velivo i posprzątaj (najpierw pyta, proponuje kopię danych)
en.Odinst=Uninstall all Velivo programs and clean up (asks first, offers a data backup)
en.ObceKonto=Started as administrator – Velivo will be installed for the account: %1 (not for the account you are working on, if another account’s password was entered).%n%nEach account has its own Velivo. Start the installer with a normal double-click, without “Run as administrator”.%n%nInstall anyway for the account %1?
en.Inst=Installing: %1…

[Types]
Name: "pelny"; Description: "{cm:TFull}"
Name: "typowy"; Description: "{cm:TTyp}"
Name: "wlasny"; Description: "{cm:TOwn}"; Flags: iscustom

[Components]
Name: "velivo"; Description: "{cm:CVelivo}"
Name: "velivo\player"; Description: "{cm:CPlayer}"
Name: "velivo\player\iptv"; Description: "{cm:CIptv}"; Flags: dontinheritcheck
Name: "ab"; Description: "{cm:CAb}"; Types: pelny typowy
Name: "monitor"; Description: "{cm:CMon}"; Types: pelny typowy
Name: "kursory"; Description: "{cm:CKur}"; Types: pelny
Name: "procesor"; Description: "{cm:CProc}"; Types: pelny typowy
Name: "centrum"; Description: "{cm:CCen}"; Types: pelny typowy
Name: "osobne"; Description: "{cm:CSep}"; Types: pelny typowy
Name: "osobne\odtwarzacz"; Types: pelny typowy; Description: "{cm:COdt}"; Flags: dontinheritcheck
Name: "osobne\przegladarka"; Types: pelny typowy; Description: "{cm:CPrz}"; Flags: dontinheritcheck
Name: "osobne\klawiatura"; Types: pelny typowy; Description: "{cm:CKla}"; Flags: dontinheritcheck
Name: "osobne\komunikator"; Types: pelny typowy; Description: "{cm:CKom}"; Flags: dontinheritcheck
Name: "osobne\serwer"; Types: pelny; Description: "{cm:CSrv}"; Flags: dontinheritcheck

[Tasks]
; ▶ wszystkie programy startują z Windows same i cicho (w tle, bez okien); pytanie tylko o serwer filmów (właściciel 2026-11-25)
Name: "serwerauto"; Description: "{cm:SrvAuto}"; Components: osobne\serwer

[Files]
Source: "{#Src}Velivo-Setup-{#AppVer}.exe"; DestDir: "{tmp}"; Components: velivo; Flags: deleteafterinstall
Source: "{#Src}Velivo-AudioBalance-for-Windows-Setup-{#AppVer}.exe"; DestDir: "{tmp}"; Components: ab; Flags: deleteafterinstall
Source: "{#Src}Velivo-Monitor-Setup-{#Fw}.exe"; DestDir: "{tmp}"; Components: monitor; Flags: deleteafterinstall
Source: "{#Src}Velivo-Kursory-Setup-{#Fw}.exe"; DestDir: "{tmp}"; Components: kursory; Flags: deleteafterinstall
Source: "{#Src}Velivo-Procesor-Setup-{#Fw}.exe"; DestDir: "{tmp}"; Components: procesor; Flags: deleteafterinstall
Source: "{#Src}Velivo-Centrum-Setup-{#Fw}.exe"; DestDir: "{tmp}"; Components: centrum; Flags: deleteafterinstall
Source: "{#Src}Velivo-Odtwarzacz-Setup-{#AppVer}.exe"; DestDir: "{tmp}"; Components: osobne\odtwarzacz; Flags: deleteafterinstall
Source: "{#Src}Velivo-Przegladarka-Setup-{#AppVer}.exe"; DestDir: "{tmp}"; Components: osobne\przegladarka; Flags: deleteafterinstall
Source: "{#Src}Velivo-Klawiatura-Setup-{#AppVer}.exe"; DestDir: "{tmp}"; Components: osobne\klawiatura; Flags: deleteafterinstall
Source: "{#Src}Velivo-Komunikator-Setup-{#AppVer}.exe"; DestDir: "{tmp}"; Components: osobne\komunikator; Flags: deleteafterinstall
Source: "{#Src}Velivo-Serwer-Setup-{#AppVer}.exe"; DestDir: "{tmp}"; Components: osobne\serwer; Flags: deleteafterinstall

; 🎞 FFmpeg (kodery i dekodery: napisy wbudowane dla lektora, pobieranie filmów, MP3) - obowiązkowo z każdym programem z filmami;
; do folderu narzędzi Velivo (ten sam, do którego program pobierał go sam); istniejący zostaje
; 🧹 „Odinstaluj Velivo” - bezpieczne odinstalowanie wszystkich programów Velivo i porządki, z propozycją kopii danych (skrót na pulpicie)
Source: "..\odinstaluj-velivo.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\build\dodatki\ffmpeg.exe"; DestDir: "{code:NarzDir}"; Components: velivo osobne\odtwarzacz osobne\serwer; Flags: onlyifdoesntexist

[Icons]
Name: "{autodesktop}\Odinstaluj Velivo"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\odinstaluj-velivo.ps1"""; IconFilename: "{sys}\shell32.dll"; IconIndex: 31; Comment: "{cm:Odinst}"
Name: "{autoprograms}\Odinstaluj Velivo"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\odinstaluj-velivo.ps1"""; IconFilename: "{sys}\shell32.dll"; IconIndex: 31; Comment: "{cm:Odinst}"

[Run]
; każdy po kolei, po cichu; zadania jak domyślnie w jego własnym instalatorze (zapora, skróty, start z Windows)
Filename: "{tmp}\Velivo-Setup-{#AppVer}.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=""{code:VelivoTasks}"""; StatusMsg: "{cm:Inst,Velivo}"; Components: velivo
Filename: "{tmp}\Velivo-AudioBalance-for-Windows-Setup-{#AppVer}.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=""desktopicon,vbcable,autostart"""; StatusMsg: "{cm:Inst,Audio Balance}"; Components: ab
Filename: "{tmp}\Velivo-Monitor-Setup-{#Fw}.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=""autostart"""; StatusMsg: "{cm:Inst,Monitor}"; Components: monitor
Filename: "{tmp}\Velivo-Kursory-Setup-{#Fw}.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=""zastosuj,autostart"""; StatusMsg: "{cm:Inst,Kursory}"; Components: kursory
Filename: "{tmp}\Velivo-Procesor-Setup-{#Fw}.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=""autostart"""; StatusMsg: "{cm:Inst,Procesor}"; Components: procesor
Filename: "{tmp}\Velivo-Centrum-Setup-{#Fw}.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=""autostart"""; StatusMsg: "{cm:Inst,Centrum}"; Components: centrum
Filename: "{tmp}\Velivo-Odtwarzacz-Setup-{#AppVer}.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=""desktopicon,firewall,autostart,videoplayer,videoplayer\iptv"""; StatusMsg: "{cm:Inst,Odtwarzacz}"; Components: osobne\odtwarzacz
Filename: "{tmp}\Velivo-Przegladarka-Setup-{#AppVer}.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=""desktopicon,firewall,autostart"""; StatusMsg: "{cm:Inst,Przeglądarka}"; Components: osobne\przegladarka
Filename: "{tmp}\Velivo-Klawiatura-Setup-{#AppVer}.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=""desktopicon,autostart"""; StatusMsg: "{cm:Inst,Klawiatura}"; Components: osobne\klawiatura
Filename: "{tmp}\Velivo-Komunikator-Setup-{#AppVer}.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=""desktopicon,firewall,autostart"""; StatusMsg: "{cm:Inst,Komunikator}"; Components: osobne\komunikator
Filename: "{tmp}\Velivo-Serwer-Setup-{#AppVer}.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=""desktopicon,firewall{code:SrvAuto}"""; StatusMsg: "{cm:Inst,Serwer}"; Components: osobne\serwer

[Code]

// 🛡 instalacja per konto: „Uruchom jako administrator” (albo hasło innego konta) instalowało Velivo na CUDZYM koncie (2026-11-22: Testowe → andro) - pytamy, domyślnie Nie
function KontoOk: Boolean;
begin
  Result := True;
  if IsAdmin and not WizardSilent then
    Result := MsgBox(FmtMessage(CustomMessage('ObceKonto'), [GetUserNameString]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

function InitializeSetup: Boolean;
begin
  Result := KontoOk;
end;

// 🧹 przed instalacją WSZYSTKIE programy Velivo zamknięte (właściciel 2026-11-25): zadanie Procesora z uprawnieniami zatrzymane, każdy proces z podrzędnymi
//    (WebView2, odtwarzacz) zakończony, czekanie aż naprawdę znikną (do 15 s) - potem czysta podmiana plików; dane użytkownika zostają
const VelivoProcs = 'Velivo.exe VelivoOdtwarzacz.exe VelivoPrzegladarka.exe VelivoKlawiatura.exe VelivoSerwer.exe VelivoKomunikator.exe VelivoAudioBalance.exe VelivoMonitor.exe VelivoKursory.exe VelivoProcesor.exe VelivoCentrum.exe';
// tylko programy z listy (instalator pakietu też ma „Velivo” w nazwie - nie może czekać sam na siebie)
function VelivoDziala: Boolean;
var L, S, R: Variant; Lista, N, Q: String;
begin
  Result := False;
  Lista := VelivoProcs + ' '; Q := '';
  while Pos(' ', Lista) > 0 do
  begin
    N := Copy(Lista, 1, Pos(' ', Lista) - 1); Delete(Lista, 1, Pos(' ', Lista));
    if N <> '' then begin if Q <> '' then Q := Q + ' OR '; Q := Q + 'Name = ''' + N + ''''; end;
  end;
  try
    L := CreateOleObject('WbemScripting.SWbemLocator'); S := L.ConnectServer('.', 'root\CIMV2');
    R := S.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE ' + Q);
    Result := R.Count > 0;
  except
  end;
end;
function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code, I: Integer; Lista, N: String;
begin
  Result := '';
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/end /tn "Velivo Procesor"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  for I := 1 to 30 do
  begin
    Lista := VelivoProcs + ' ';
    while Pos(' ', Lista) > 0 do
    begin
      N := Copy(Lista, 1, Pos(' ', Lista) - 1); Delete(Lista, 1, Pos(' ', Lista));
      if N <> '' then Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM ' + N, '', SW_HIDE, ewWaitUntilTerminated, Code);
    end;
    Sleep(500);
    if not VelivoDziala then break;
  end;
  if VelivoDziala then Result := CustomMessage('NieZamkniete');
end;
// serwer filmów z Windows - tylko gdy zaznaczone na stronie zadań pakietu
function SrvAuto(P: String): String;
begin
  if WizardIsTaskSelected('serwerauto') then Result := ',autostart' else Result := ',!autostart';
end;
function VelivoTasks(P: String): String;
begin
  Result := 'desktopicon,komunikatoricon,kanapaicon,klawiaturaicon,firewall,autostart,videoplayer';   // odtwarzacz filmów (kodeki VLC) zawsze
  if WizardIsComponentSelected('velivo\player\iptv') then Result := Result + ',videoplayer\iptv' else Result := Result + ',!videoplayer\iptv';
end;

// folder narzędzi Velivo (ffmpeg, yt-dlp): folder danych ze spisu Velivo (HKCU\Software\Velivo „Dane”), zapasowo domyślny
function NarzDir(P: String): String;
var D: String;
begin
  if not (RegQueryStringValue(HKCU, 'Software\Velivo', 'Dane', D) and DirExists(D)) then D := ExpandConstant('{localappdata}\Velivo\Dane');
  Result := AddBackslash(D) + 'narzedzia';
end;

function Installed(Id: String): Boolean;
begin
  Result := RegKeyExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + Id + '_is1');
end;

// już zainstalowane programy - zaznaczone same (pakiet służy też do aktualizacji); pierwsza instalacja - Typowy
var Done: Boolean;
procedure CurPageChanged(CurPageID: Integer);
var S: String;
begin
  if (CurPageID <> wpSelectComponents) or Done then exit;
  Done := True;
  S := '';
  if Installed('{6E4C2B7A-3F1D-4C8E-9A51-PRZEGLADARKA01}') then S := S + ',velivo';
  if FileExists(ExpandConstant('{localappdata}\Programs\Velivo\vlc\libvlc.dll')) then S := S + ',velivo\player';
  if Installed('{4C2E8A1F-7B3D-4E9A-A6C5-VELIVOAUDBAL1}') then S := S + ',ab';
  if Installed('{7A3C1E52-9D4B-4F86-B1E2-VELIVOMONIT1}') then S := S + ',monitor';
  if Installed('{5B8E2F14-3C7A-4D91-A6E0-VELIVOKURSOR}') then S := S + ',kursory';
  if Installed('{6D2B9F41-8C3A-4E75-A0D1-VELIVOPROCES}') then S := S + ',procesor';
  if Installed('{8B4D2F63-AE5C-4A97-C2F3-VELIVOCENTR1}') then S := S + ',centrum';
  if Installed('{6E4C2B7A-3F1D-4C8E-9A51-ODTWARZACZ01}') then S := S + ',osobne\odtwarzacz';
  if Installed('{6E4C2B7A-3F1D-4C8E-9A51-PRZEGLADARKA02}') then S := S + ',osobne\przegladarka';
  if Installed('{6E4C2B7A-3F1D-4C8E-9A51-KLAWIATURA1}') then S := S + ',osobne\klawiatura';
  if Installed('{9B1F7C2E-5A44-4E0B-8D6E-VELIVOKOMUN01}') then S := S + ',osobne\komunikator';
  if Installed('{6E4C2B7A-3F1D-4C8E-9A51-SERWERFILMY}') then S := S + ',osobne\serwer';
  if S <> '' then WizardSelectComponents(Copy(S, 2, Length(S)));
end;
