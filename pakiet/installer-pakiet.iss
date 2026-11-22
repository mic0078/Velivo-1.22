; 📦 Velivo – pakiet: jeden instalator, wybór programów (Pełny / Typowy / Własny) - dokumentacja/makieta-instalator-pakiet.html.
; W środku gotowe instalatory programów z build\instalator (budowane wcześniej przez zbuduj.ps1 i zbuduj-*.ps1); każdy instaluje się
; po cichu jak osobno (własna ikona, własne odinstalowanie, wspólne dane). Już zainstalowane programy zaznaczone same (aktualizacja).
#define AppVer "1.22"
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
pl.TFull=Pełny – wszystkie programy Velivo
en.TFull=Full – all Velivo programs
pl.TTyp=Typowy – Velivo z filmami, dźwiękiem i monitorem
en.TTyp=Typical – Velivo with movies, sound and monitor
pl.TOwn=Własny – wybierz sam
en.TOwn=Custom – choose yourself
pl.CVelivo=Velivo (przeglądarka, komunikator, klawiatura, Pilot)
en.CVelivo=Velivo (browser, messenger, keyboard, Remote)
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
pl.CCen=Velivo Centrum (wszystkie programy w jednym miejscu)
en.CCen=Velivo Centre (all programs in one place)
pl.CSep=Osobne wersje (zwykle niepotrzebne przy pełnym Velivo)
en.CSep=Separate editions (usually not needed with full Velivo)
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
pl.Inst=Instaluję: %1…
en.Inst=Installing: %1…

[Types]
Name: "pelny"; Description: "{cm:TFull}"
Name: "typowy"; Description: "{cm:TTyp}"
Name: "wlasny"; Description: "{cm:TOwn}"; Flags: iscustom

[Components]
Name: "velivo"; Description: "{cm:CVelivo}"; Types: pelny typowy
Name: "velivo\player"; Description: "{cm:CPlayer}"; Types: pelny typowy
Name: "velivo\player\iptv"; Description: "{cm:CIptv}"; Types: pelny; Flags: dontinheritcheck
Name: "ab"; Description: "{cm:CAb}"; Types: pelny typowy
Name: "monitor"; Description: "{cm:CMon}"; Types: pelny typowy
Name: "kursory"; Description: "{cm:CKur}"; Types: pelny
Name: "centrum"; Description: "{cm:CCen}"; Types: pelny typowy
Name: "osobne"; Description: "{cm:CSep}"
Name: "osobne\odtwarzacz"; Description: "{cm:COdt}"
Name: "osobne\przegladarka"; Description: "{cm:CPrz}"
Name: "osobne\klawiatura"; Description: "{cm:CKla}"
Name: "osobne\komunikator"; Description: "{cm:CKom}"
Name: "osobne\serwer"; Description: "{cm:CSrv}"

[Files]
Source: "{#Src}Velivo-Setup-{#AppVer}.exe"; DestDir: "{tmp}"; Components: velivo; Flags: deleteafterinstall
Source: "{#Src}Velivo-AudioBalance-for-Windows-Setup-{#AppVer}.exe"; DestDir: "{tmp}"; Components: ab; Flags: deleteafterinstall
Source: "{#Src}Velivo-Monitor-Setup-{#AppVer}.exe"; DestDir: "{tmp}"; Components: monitor; Flags: deleteafterinstall
Source: "{#Src}Velivo-Kursory-Setup-{#AppVer}.exe"; DestDir: "{tmp}"; Components: kursory; Flags: deleteafterinstall
Source: "{#Src}Velivo-Centrum-Setup-{#AppVer}.exe"; DestDir: "{tmp}"; Components: centrum; Flags: deleteafterinstall
Source: "{#Src}Velivo-Odtwarzacz-Setup-{#AppVer}.exe"; DestDir: "{tmp}"; Components: osobne\odtwarzacz; Flags: deleteafterinstall
Source: "{#Src}Velivo-Przegladarka-Setup-{#AppVer}.exe"; DestDir: "{tmp}"; Components: osobne\przegladarka; Flags: deleteafterinstall
Source: "{#Src}Velivo-Klawiatura-Setup-{#AppVer}.exe"; DestDir: "{tmp}"; Components: osobne\klawiatura; Flags: deleteafterinstall
Source: "{#Src}Velivo-Komunikator-Setup-{#AppVer}.exe"; DestDir: "{tmp}"; Components: osobne\komunikator; Flags: deleteafterinstall
Source: "{#Src}Velivo-Serwer-Setup-{#AppVer}.exe"; DestDir: "{tmp}"; Components: osobne\serwer; Flags: deleteafterinstall

[Run]
; każdy po kolei, po cichu; zadania jak domyślnie w jego własnym instalatorze (zapora, skróty, start z Windows)
Filename: "{tmp}\Velivo-Setup-{#AppVer}.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=""{code:VelivoTasks}"""; StatusMsg: "{cm:Inst,Velivo}"; Components: velivo
Filename: "{tmp}\Velivo-AudioBalance-for-Windows-Setup-{#AppVer}.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=""desktopicon"""; StatusMsg: "{cm:Inst,Audio Balance}"; Components: ab
Filename: "{tmp}\Velivo-Monitor-Setup-{#AppVer}.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=""autostart"""; StatusMsg: "{cm:Inst,Monitor}"; Components: monitor
Filename: "{tmp}\Velivo-Kursory-Setup-{#AppVer}.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=""zastosuj,autostart"""; StatusMsg: "{cm:Inst,Kursory}"; Components: kursory
Filename: "{tmp}\Velivo-Centrum-Setup-{#AppVer}.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=""autostart"""; StatusMsg: "{cm:Inst,Centrum}"; Components: centrum
Filename: "{tmp}\Velivo-Odtwarzacz-Setup-{#AppVer}.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=""desktopicon,firewall,videoplayer,videoplayer\iptv"""; StatusMsg: "{cm:Inst,Odtwarzacz}"; Components: osobne\odtwarzacz
Filename: "{tmp}\Velivo-Przegladarka-Setup-{#AppVer}.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=""desktopicon,firewall"""; StatusMsg: "{cm:Inst,Przeglądarka}"; Components: osobne\przegladarka
Filename: "{tmp}\Velivo-Klawiatura-Setup-{#AppVer}.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=""desktopicon,autostart"""; StatusMsg: "{cm:Inst,Klawiatura}"; Components: osobne\klawiatura
Filename: "{tmp}\Velivo-Komunikator-Setup-{#AppVer}.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=""desktopicon,firewall"""; StatusMsg: "{cm:Inst,Komunikator}"; Components: osobne\komunikator
Filename: "{tmp}\Velivo-Serwer-Setup-{#AppVer}.exe"; Parameters: "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=""desktopicon,firewall,autostart"""; StatusMsg: "{cm:Inst,Serwer}"; Components: osobne\serwer

[Code]
function VelivoTasks(P: String): String;
begin
  Result := 'desktopicon,komunikatoricon,firewall';
  if WizardIsComponentSelected('velivo\player') then
  begin
    Result := Result + ',videoplayer';
    if WizardIsComponentSelected('velivo\player\iptv') then Result := Result + ',videoplayer\iptv' else Result := Result + ',!videoplayer\iptv';
  end;
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
  if Installed('{8B4D2F63-AE5C-4A97-C2F3-VELIVOCENTR1}') then S := S + ',centrum';
  if Installed('{6E4C2B7A-3F1D-4C8E-9A51-ODTWARZACZ01}') then S := S + ',osobne\odtwarzacz';
  if Installed('{6E4C2B7A-3F1D-4C8E-9A51-PRZEGLADARKA02}') then S := S + ',osobne\przegladarka';
  if Installed('{6E4C2B7A-3F1D-4C8E-9A51-KLAWIATURA1}') then S := S + ',osobne\klawiatura';
  if Installed('{9B1F7C2E-5A44-4E0B-8D6E-VELIVOKOMUN01}') then S := S + ',osobne\komunikator';
  if Installed('{6E4C2B7A-3F1D-4C8E-9A51-SERWERFILMY}') then S := S + ',osobne\serwer';
  if S <> '' then WizardSelectComponents(Copy(S, 2, Length(S)));
end;
