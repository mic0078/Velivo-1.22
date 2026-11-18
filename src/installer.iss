; Instalator Velivo - kompilacja: ISCC.exe installer.iss (po dotnet publish do ..\build\velivo)
#define AppVer "1.22"
; 🧩 jeden instalator, kilka programów: ISCC /DOdtwarzacz albo /DPrzegladarka (bez = pełne Velivo Premium)
#ifdef Odtwarzacz
  #define Wariant
  #define AppName "Velivo Odtwarzacz"
  #define Exe "VelivoOdtwarzacz.exe"
  #define Pid "VelivoOdtwarzacz"
  #define Ikona "odtwarzacz.ico"
  #define AppIdV "{{6E4C2B7A-3F1D-4C8E-9A51-ODTWARZACZ01}"
  #define SrcDir "..\build\odtwarzacz"
  #define OutName "Velivo-Odtwarzacz-Setup-" + AppVer
#elif defined(Serwer)
  #define Wariant
  #define AppName "Velivo Serwer filmów"
  #define Exe "VelivoSerwer.exe"
  #define Pid "VelivoSerwer"
  #define Ikona "serwer.ico"
  #define AppIdV "{{6E4C2B7A-3F1D-4C8E-9A51-SERWERFILMY}"
  #define SrcDir "..\build\serwer"
  #define OutName "Velivo-Serwer-Setup-" + AppVer
#elif defined(Klawiatura)
  #define Wariant
  #define AppName "Velivo Klawiatura"
  #define Exe "VelivoKlawiatura.exe"
  #define Pid "VelivoKlawiatura"
  #define Ikona "klawiatura.ico"
  #define AppIdV "{{6E4C2B7A-3F1D-4C8E-9A51-KLAWIATURA1}"
  #define SrcDir "..\build\klawiatura"
  #define OutName "Velivo-Klawiatura-Setup-" + AppVer
#elif defined(Przegladarka)
  #define Wariant
  #define AppName "Velivo Przeglądarka"
  #define Exe "VelivoPrzegladarka.exe"
  #define Pid "VelivoPrzegladarka"
  #define Ikona "przegladarka.ico"
  #define AppIdV "{{6E4C2B7A-3F1D-4C8E-9A51-PRZEGLADARKA02}"
  #define SrcDir "..\build\przegladarka"
  #define OutName "Velivo-Przegladarka-Setup-" + AppVer
#else
  #define AppName "Velivo"
  #define Exe "Velivo.exe"
  #define Pid "Velivo"
  #define Ikona "app.ico"
  #define AppIdV "{{6E4C2B7A-3F1D-4C8E-9A51-PRZEGLADARKA01}"
#endif
; 🧩 co program ma: NieStrony - bez rejestracji przeglądarki i PDF (Odtwarzacz, Klawiatura); NieFilmy - bez filmów (Przeglądarka, Klawiatura)
#if defined(Odtwarzacz) || defined(Klawiatura) || defined(Serwer)
  #define NieStrony
#endif
#if defined(Przegladarka) || defined(Klawiatura)
  #define NieFilmy
#endif
#ifdef Wariant
  #define VlcFlag Pid + "-odtwarzacz-usun.flag"
#else
  #define VlcFlag "odtwarzacz-usun.flag"
#endif
#ifndef SrcDir
  #define SrcDir "..\build\velivo"
#endif
#ifndef OutName
  #define OutName "Velivo-Setup-" + AppVer
#endif

[Setup]
; AppId bez zmian od czasow nazwy "Przegladarka" - dzieki temu instalator aktualizuje stara wersje
AppId={#AppIdV}
AppName={#AppName}
AppVersion={#AppVer}
; 💎 pełne Velivo: zbuduj.ps1 podaje /DPremium (wersja bezpłatna -Podstawowa bez tego napisu)
#ifdef Premium
AppVerName={#AppName} Premium {#AppVer} By MichAel
#else
AppVerName={#AppName} {#AppVer} By MichAel
#endif
AppPublisher=MichAel
DefaultDirName={localappdata}\Programs\{#AppName}
UsePreviousAppDir=no
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
OutputDir=..\Instalator
OutputBaseFilename={#OutName}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#Ikona}
UninstallDisplayIcon={app}\{#Exe}
DisableProgramGroupPage=yes
ShowLanguageDialog=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Languages]
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
pl.VelivoRunning=Velivo nadal działa (prawdopodobnie uruchomione jako administrator) i instalator nie może go zamknąć. Zamknij Velivo: ikona przy zegarze → Zakończ (albo Menedżer zadań → Velivo → Zakończ zadanie), potem uruchom instalację ponownie.
en.VelivoRunning=Velivo is still running (probably started as administrator) and the installer cannot close it. Close Velivo: tray icon → Exit (or Task Manager → Velivo → End task), then run the installation again.
pl.DesktopIcon=Skrót na pulpicie
pl.MessengerIcon=Skrót Komunikatora Velivo na pulpicie
pl.Addons=Dodatki (opcjonalne):
en.Addons=Add-ons (optional):
pl.VideoPlayer=Odtwarzacz filmów jak VLC – AVI, MKV, WMV i inne formaty, biblioteka filmów (pobiera ok. 145 MB z oficjalnej paczki VideoLAN; można go później odinstalować w Velivo)
en.VideoPlayer=VLC-like video player – AVI, MKV, WMV and more, video library (downloads about 145 MB from the official VideoLAN package; can be uninstalled later in Velivo)
pl.Iptv=Telewizja IPTV – kanały z listy M3U (plik albo adres URL) i program TV (EPG); można później odinstalować w Velivo
en.Iptv=IPTV television – channels from an M3U list (file or URL) and TV guide (EPG); can be uninstalled later in Velivo
pl.VideoPlayerFailed=Nie udało się pobrać odtwarzacza filmów:%n%1%n%nVelivo zainstaluje się bez niego – możesz uruchomić instalator ponownie później.
en.VideoPlayerFailed=Could not download the video player:%n%1%n%nVelivo will be installed without it – you can run the installer again later.
pl.VideoPlayerUnpack=Nie udało się rozpakować odtwarzacza filmów – Velivo działa bez niego. Uruchom instalator ponownie, aby spróbować jeszcze raz.
en.VideoPlayerUnpack=Could not unpack the video player – Velivo works without it. Run the installer again to retry.
en.DesktopIcon=Desktop shortcut
en.MessengerIcon=Velivo Messenger desktop shortcut
pl.Network=Sieć:
en.Network=Network:
pl.Firewall=Otwórz porty Velivo w zaporze Windows (polecane) – bez tego rozmowy, pliki i serwer nie działają przez internet ani między komputerami. Windows zapyta raz – kliknij „Tak”.
en.Firewall=Open Velivo ports in Windows Firewall (recommended) – without it chats, files and the server do not work over the internet or between computers. Windows will ask once – click "Yes".
pl.Shortcuts=Skróty:
en.Shortcuts=Shortcuts:
pl.RunApp=Uruchom %1
en.RunApp=Launch %1
pl.AppDesc=Velivo – lekka i prywatna przeglądarka
en.AppDesc=Velivo – a light and private browser
pl.Upgrade=Velivo Premium zawiera w sobie %1.%n%nUsunąć osobny program %1? Twoje ustawienia, biblioteka, postęp filmów, lektorzy i parowanie telefonu zostają (są wspólne).
en.Upgrade=Velivo Premium already includes %1.%n%nRemove the separate %1 program? Your settings, library, playback progress, voices and phone pairing stay (they are shared).
pl.KomGone=Velivo prowadzi na tym komputerze komunikator (rozmowy, a na serwerze także skrzynkę i pośrednika dla innych osób), a osobny Velivo Komunikator nie jest zainstalowany.%n%nPo odinstalowaniu rozmowy na tym komputerze przestaną działać. Najpierw zainstaluj Velivo Komunikator – przejmie rozmowy i serwer z tymi samymi danymi.%n%nOdinstalować Velivo mimo to?
en.KomGone=Velivo runs the messenger on this computer (chats, and on a server also the mailbox and relay for other people), and the separate Velivo Messenger is not installed.%n%nAfter uninstalling, chats on this computer will stop working. Install Velivo Messenger first – it takes over chats and the server with the same data.%n%nUninstall Velivo anyway?
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
pl.CleanConfirm=Czysta instalacja: ustawienia, zakładki, historia, zapisane hasła przeglądarki i Szybki Dostęp zostaną przeniesione do kopii zapasowej (folder z dopiskiem "kopia-..."), a Velivo uruchomi się jak nowe. Dotyczy to też Velivo Odtwarzacza i Velivo Przeglądarki – dane są wspólne.%n%nKontynuować?
en.CleanConfirm=Clean install: settings, bookmarks, history, saved browser passwords and Quick Access will be moved to a backup (folder ending with "kopia-..."), and Velivo will start like new. This also applies to Velivo Player and Velivo Browser – the data is shared.%n%nContinue?
pl.MoveFailed=Nie udało się przenieść folderu:%n%1%nZamknij Velivo i spróbuj ponownie albo usuń go ręcznie.
en.MoveFailed=Could not move the folder:%n%1%nClose Velivo and try again, or delete it manually.
pl.GetWebView=Pobieranie i instalacja Microsoft Edge WebView2 Runtime…
en.GetWebView=Downloading and installing Microsoft Edge WebView2 Runtime…
pl.NeedWebView=Nie wykryto Microsoft Edge WebView2 Runtime (zwykle jest w Windows 11).%nBez niego przeglądarka nie wyświetli stron. Otworzyć stronę pobierania?
pl.KlawAuto=Uruchamiaj klawiaturę razem z Windows (ikona w zasobniku)
en.KlawAuto=Start the keyboard with Windows (tray icon)
pl.SerwerAuto=Uruchamiaj serwer filmów razem z Windows (działa w tle – Twoje komputery widzą bibliotekę)
en.SerwerAuto=Start the movie server with Windows (runs in the background – your computers see the library)
en.NeedWebView=Microsoft Edge WebView2 Runtime was not found (usually included in Windows 11).%nWithout it the browser cannot display pages. Open the download page?

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:Shortcuts}"
#ifndef Wariant
Name: "komunikatoricon"; Description: "{cm:MessengerIcon}"; GroupDescription: "{cm:Shortcuts}"
#endif
#ifdef Klawiatura
Name: "autostart"; Description: "{cm:KlawAuto}"; GroupDescription: "{cm:Shortcuts}"
#else
Name: "firewall"; Description: "{cm:Firewall}"; GroupDescription: "{cm:Network}"
#endif
#ifdef Serwer
Name: "autostart"; Description: "{cm:SerwerAuto}"; GroupDescription: "{cm:Shortcuts}"
#endif
#ifdef Odtwarzacz
Name: "videoplayer"; Description: "{cm:VideoPlayer}"; GroupDescription: "{cm:Addons}"
Name: "videoplayer\iptv"; Description: "{cm:Iptv}"
#elif !defined(NieFilmy)
; odtwarzacz filmow (libvlc) - tylko na wyrazne zyczenie uzytkownika
Name: "videoplayer"; Description: "{cm:VideoPlayer}"; GroupDescription: "{cm:Addons}"; Flags: unchecked
; telewizja IPTV - osobny modul na odtwarzaczu filmow (bez wlasnego pobierania)
Name: "videoplayer\iptv"; Description: "{cm:Iptv}"; Flags: unchecked
#endif

[InstallDelete]
; pozostalosci po poprzednich nazwach programu ("Przegladarka", "Tarcza") - tylko pliki programu i skroty,
; folder z danymi uzytkownika (%LOCALAPPDATA%\Programs\Velivo\Dane, dawniej %LOCALAPPDATA%\Przegladarka) zostaje nietkniety
Type: filesandordirs; Name: "{localappdata}\Programs\Przegladarka"
Type: filesandordirs; Name: "{localappdata}\Programs\Tarcza"
Type: files; Name: "{autodesktop}\Przeglądarka.lnk"
Type: files; Name: "{autoprograms}\Przeglądarka.lnk"
Type: files; Name: "{autodesktop}\Tarcza.lnk"
Type: files; Name: "{autoprograms}\Tarcza.lnk"
; tlumaczenia .NET na jezyki, ktorych Velivo nie uzywa (od wersji z SatelliteResourceLanguages=pl nie sa instalowane)
Type: filesandordirs; Name: "{app}\cs"
Type: filesandordirs; Name: "{app}\de"
Type: filesandordirs; Name: "{app}\es"
Type: filesandordirs; Name: "{app}\fr"
Type: filesandordirs; Name: "{app}\it"
Type: filesandordirs; Name: "{app}\ja"
Type: filesandordirs; Name: "{app}\ko"
Type: filesandordirs; Name: "{app}\pt-BR"
Type: filesandordirs; Name: "{app}\ru"
Type: filesandordirs; Name: "{app}\tr"
Type: filesandordirs; Name: "{app}\zh-Hans"
Type: filesandordirs; Name: "{app}\zh-Hant"

[Files]
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; kopia tego instalatora w folderze programu - Velivo dolacza ja do kopii bezpieczenstwa (odtworzenie bez internetu)
Source: "{srcexe}"; DestDir: "{app}"; DestName: "Velivo-instalator.exe"; Flags: external ignoreversion
; ikona programu jako plik - skróty wskazują ją wprost (Windows nie pokaże starej ikony z pamięci po zmianie)
Source: "{#Ikona}"; DestDir: "{app}"; DestName: "ikona-programu.ico"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#Exe}"; IconFilename: "{app}\ikona-programu.ico"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#Exe}"; IconFilename: "{app}\ikona-programu.ico"; Tasks: desktopicon
#ifndef Wariant
; sama klawiatura ekranowa - osobny identyfikator, wiec po przypieciu ma wlasna ikone na pasku zadan
Name: "{autoprograms}\{#AppName} Klawiatura"; Filename: "{app}\{#Exe}"; Parameters: "--klawiatura"; AppUserModelID: "Velivo.Klawiatura"
; 💬 sam komunikator - wlasna ikona i osobny identyfikator (po przypieciu wlasna ikona na pasku zadan)
Name: "{autoprograms}\Komunikator {#AppName}"; Filename: "{app}\{#Exe}"; Parameters: "--komunikator"; IconFilename: "{app}\komunikator.ico"; AppUserModelID: "Velivo.Komunikator"
Name: "{autodesktop}\Komunikator {#AppName}"; Filename: "{app}\{#Exe}"; Parameters: "--komunikator"; IconFilename: "{app}\komunikator.ico"; AppUserModelID: "Velivo.Komunikator"; Tasks: komunikatoricon
#endif

[Registry]
; 🧩 spis programów Velivo - każdy program wie o innych (przed instalacją i po deinstalacji); deinstalator wykreśla tylko siebie
Root: HKCU; Subkey: "Software\Velivo\Programy\{#Pid}"; ValueType: string; ValueName: "Exe"; ValueData: "{app}\{#Exe}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Velivo\Programy\{#Pid}"; ValueType: string; ValueName: "Nazwa"; ValueData: "{#AppName}"
Root: HKCU; Subkey: "Software\Velivo\Programy\{#Pid}"; ValueType: string; ValueName: "Wersja"; ValueData: "{#AppVer}"
Root: HKCU; Subkey: "Software\Velivo"; ValueType: string; ValueName: "Dane"; ValueData: "{localappdata}\Velivo\Dane"
; autostart wlaczany w Ustawieniach Velivo - przy odinstalowaniu wpis znika (ValueType none = instalator go nie tworzy)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "{#Pid}"; Flags: uninsdeletevalue
#if defined(Klawiatura) || defined(Serwer)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#Pid}"; ValueData: """{app}\{#Exe}"" --autostart 10"; Tasks: autostart; Flags: uninsdeletevalue
#endif
; rejestracja jako przegladarka (pojawia sie na liscie "Aplikacje domyslne"); wybor robi uzytkownik w Ustawieniach
#ifndef NieStrony
Root: HKCU; Subkey: "Software\Classes\{#Pid}HTML"; ValueType: string; ValueName: ""; ValueData: "Velivo HTML Document"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\{#Pid}HTML\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#Exe},0"
Root: HKCU; Subkey: "Software\Classes\{#Pid}HTML\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#Exe}"" ""%1"""
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\{#AppName}"; ValueType: string; ValueName: ""; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\{#AppName}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#Exe},0"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\{#AppName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#Exe}"""
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "{cm:AppDesc}"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: "{app}\{#Exe},0"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\{#AppName}\Capabilities\URLAssociations"; ValueType: string; ValueName: "http"; ValueData: "{#Pid}HTML"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\{#AppName}\Capabilities\URLAssociations"; ValueType: string; ValueName: "https"; ValueData: "{#Pid}HTML"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".htm"; ValueData: "{#Pid}HTML"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".html"; ValueData: "{#Pid}HTML"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".shtml"; ValueData: "{#Pid}HTML"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".xhtml"; ValueData: "{#Pid}HTML"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".svg"; ValueData: "{#Pid}HTML"
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\{#AppName}\Capabilities\StartMenu"; ValueType: string; ValueName: "StartMenuInternet"; ValueData: "{#AppName}"
#endif
; PDF: Velivo na liscie "Otworz za pomoca" i w Aplikacjach domyslnych (wbudowany czytnik PDF silnika)
#ifndef NieStrony
Root: HKCU; Subkey: "Software\Classes\{#Pid}PDF"; ValueType: string; ValueName: ""; ValueData: "Velivo PDF Document"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\{#Pid}PDF\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#Exe},0"
Root: HKCU; Subkey: "Software\Classes\{#Pid}PDF\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#Exe}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "{#Pid}PDF"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pdf"; ValueData: "{#Pid}PDF"
#endif
; filmy z dysku: odtwarzacz Velivo w "Otworz za pomoca" i w Aplikacjach domyslnych
#ifndef NieFilmy
Root: HKCU; Subkey: "Software\Classes\{#Pid}Video"; ValueType: string; ValueName: ""; ValueData: "Velivo Video"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\{#Pid}Video\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#Exe},0"
Root: HKCU; Subkey: "Software\Classes\{#Pid}Video\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#Exe}"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\.mp4\OpenWithProgids"; ValueType: string; ValueName: "{#Pid}Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mp4"; ValueData: "{#Pid}Video"
Root: HKCU; Subkey: "Software\Classes\.m4v\OpenWithProgids"; ValueType: string; ValueName: "{#Pid}Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m4v"; ValueData: "{#Pid}Video"
Root: HKCU; Subkey: "Software\Classes\.webm\OpenWithProgids"; ValueType: string; ValueName: "{#Pid}Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".webm"; ValueData: "{#Pid}Video"
Root: HKCU; Subkey: "Software\Classes\.mkv\OpenWithProgids"; ValueType: string; ValueName: "{#Pid}Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mkv"; ValueData: "{#Pid}Video"
Root: HKCU; Subkey: "Software\Classes\.mov\OpenWithProgids"; ValueType: string; ValueName: "{#Pid}Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mov"; ValueData: "{#Pid}Video"
Root: HKCU; Subkey: "Software\Classes\.ogv\OpenWithProgids"; ValueType: string; ValueName: "{#Pid}Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ogv"; ValueData: "{#Pid}Video"
#endif
; lista "Otworz za pomoca" w Eksploratorze (filmy i PDF)
Root: HKCU; Subkey: "Software\Classes\Applications\{#Exe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Applications\{#Exe}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#Exe},0"
Root: HKCU; Subkey: "Software\Classes\Applications\{#Exe}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#Exe}"" ""%1"""
#ifndef NieStrony
Root: HKCU; Subkey: "Software\Classes\Applications\{#Exe}\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""
#endif
#ifndef NieFilmy
Root: HKCU; Subkey: "Software\Classes\Applications\{#Exe}\SupportedTypes"; ValueType: string; ValueName: ".mp4"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#Exe}\SupportedTypes"; ValueType: string; ValueName: ".m4v"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#Exe}\SupportedTypes"; ValueType: string; ValueName: ".webm"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#Exe}\SupportedTypes"; ValueType: string; ValueName: ".mkv"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#Exe}\SupportedTypes"; ValueType: string; ValueName: ".mov"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#Exe}\SupportedTypes"; ValueType: string; ValueName: ".ogv"; ValueData: ""
#endif
; torrenty: plik .torrent i linki magnet: (pobiera tylko, gdy torrenty sa wlaczone w Ustawieniach)
#ifndef NieStrony
Root: HKCU; Subkey: "Software\Classes\{#Pid}Torrent"; ValueType: string; ValueName: ""; ValueData: "Velivo Torrent"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\{#Pid}Torrent\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#Exe},0"
Root: HKCU; Subkey: "Software\Classes\{#Pid}Torrent\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#Exe}"" ""%1"""
#ifndef Wariant
Root: HKCU; Subkey: "Software\Classes\.torrent"; ValueType: string; ValueName: ""; ValueData: "{#Pid}Torrent"
#endif
Root: HKCU; Subkey: "Software\Classes\.torrent\OpenWithProgids"; ValueType: string; ValueName: "{#Pid}Torrent"; ValueData: ""; Flags: uninsdeletevalue
; magnet: jeden wpis w Windows dla wszystkich - tylko pełne Velivo (odinstalowanie innego programu go nie skasuje)
#ifndef Wariant
Root: HKCU; Subkey: "Software\Classes\magnet"; ValueType: string; ValueName: ""; ValueData: "URL:Magnet Link"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\magnet"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\magnet\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#Exe},0"
Root: HKCU; Subkey: "Software\Classes\magnet\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#Exe}"" ""%1"""
#endif
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".torrent"; ValueData: "{#Pid}Torrent"
#ifndef Wariant
Root: HKCU; Subkey: "Software\Clients\StartMenuInternet\{#AppName}\Capabilities\URLAssociations"; ValueType: string; ValueName: "magnet"; ValueData: "{#Pid}Torrent"
#endif
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#AppName}"; ValueData: "Software\Clients\StartMenuInternet\{#AppName}\Capabilities"; Flags: uninsdeletevalue
#endif

[Run]
Filename: "{app}\{#Exe}"; Description: "{cm:RunApp,{#AppName}}"; Flags: nowait postinstall skipifsilent
; ciche instalowanie (aktualizacja serwera) - Velivo, które działało przed instalacją, wraca samo (serwer nie zostaje wyłączony)
Filename: "{app}\{#Exe}"; Flags: nowait runasoriginaluser; Check: WizardSilent and WasRunning

[UninstallDelete]
Type: files; Name: "{app}\unins*.old-*"
Type: filesandordirs; Name: "{app}\vlc"
Type: files; Name: "{app}\iptv.on"

#include "dotnet10.iss"

[Code]
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
  DownloadPage: TDownloadWizardPage;

// Odtwarzacz filmow: oficjalna paczka VideoLAN z NuGet, sprawdzana suma SHA-256 (podmieniony plik = brak instalacji).
// Rozpakowujemy tylko 64-bitowa biblioteke bez wtyczek do nadawania strumieni, wizualizacji itp. (test w CI robi to samo).
const
  VlcUrl = 'https://api.nuget.org/v3-flatcontainer/videolan.libvlc.windows/3.0.24/videolan.libvlc.windows.3.0.24.nupkg';
  VlcSha256 = '3da3f37ee2b2b10e5c788d830a3b513658c36521ea1b0f966ed86eb69f41ef3c';
  VlcTarArgs = '--exclude build/x64/plugins/stream_out --exclude build/x64/plugins/access_output --exclude build/x64/plugins/mux ' +
    '--exclude build/x64/plugins/visualization --exclude build/x64/plugins/services_discovery --exclude build/x64/plugins/lua ' +
    '--exclude build/x64/plugins/video_splitter --exclude build/x64/plugins/keystore build/x64/libvlc.dll build/x64/libvlccore.dll build/x64/plugins';

function VlcWanted: Boolean;
begin
  Result := WizardIsTaskSelected('videoplayer') and not FileExists(ExpandConstant('{app}\vlc\libvlc.dll'));
end;

// dane uzytkownika: %LOCALAPPDATA%\Programs\Velivo\Dane; dawniej %LOCALAPPDATA%\Przegladarka (Velivo przenosi go przy starcie)
function UserDataDir: String;
begin
  // 🧩 od 2026-11 dane wspólne poza folderem programu (%LOCALAPPDATA%\Velivo\Dane); starsze instalacje - w folderze Velivo (program przenosi je przy starcie)
  Result := ExpandConstant('{localappdata}\Velivo\Dane');
  if not DirExists(Result) and DirExists(ExpandConstant('{localappdata}\Programs\Velivo\Dane')) then Result := ExpandConstant('{localappdata}\Programs\Velivo\Dane');
end;

function LegacyDataDir: String;
begin
  Result := ExpandConstant('{localappdata}\Przegladarka');
end;

procedure InstallVlc;
var Code: Integer; Tmp, Zip: String; Ok: Boolean;
begin
  Tmp := ExpandConstant('{app}\vlc-rozpakowanie');
  DelTree(Tmp, True, True, True);
  ForceDirectories(Tmp);
  Ok := Exec(ExpandConstant('{sys}\tar.exe'), '-xf "' + ExpandConstant('{tmp}\libvlc.nupkg') + '" -C "' + Tmp + '" ' + VlcTarArgs, '', SW_HIDE, ewWaitUntilTerminated, Code)
     and (Code = 0) and FileExists(Tmp + '\build\x64\libvlc.dll');
  // starszy Windows 10 (przed 1803, np. tablety CHUWI) nie ma tar.exe - rozpakowanie przez PowerShell (paczka to zwykly ZIP)
  if not Ok then
  begin
    Zip := ExpandConstant('{tmp}\libvlc.zip');
    if FileCopy(ExpandConstant('{tmp}\libvlc.nupkg'), Zip, False) then
      Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), '-NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -LiteralPath ''' + Zip + ''' -DestinationPath ''' + Tmp + ''' -Force"', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Ok := FileExists(Tmp + '\build\x64\libvlc.dll');
    DelTree(Tmp + '\build\x64\plugins\video_splitter', True, True, True);   // jak przy tar - bez pominiętych wtyczek
    DelTree(Tmp + '\build\x64\plugins\keystore', True, True, True);
  end;
  if Ok then
  begin
    DelTree(ExpandConstant('{app}\vlc'), True, True, True);
    if not RenameFile(Tmp + '\build\x64', ExpandConstant('{app}\vlc')) then MsgBox(CustomMessage('VideoPlayerUnpack'), mbError, MB_OK);
    // odinstalowanie zlecone wczesniej w Velivo - ponowna instalacja je odwoluje
    DeleteFile(UserDataDir + '\{#VlcFlag}');
    DeleteFile(LegacyDataDir + '\odtwarzacz-usun.flag');
  end
  else
    MsgBox(CustomMessage('VideoPlayerUnpack'), mbError, MB_OK);
  DelTree(Tmp, True, True, True);
end;


function QuickAccessDataDir: String;
begin
  Result := ExpandConstant('{localappdata}\Programs\Velivo\Szybki Dostęp');
end;

function HasOldData: Boolean;
begin
  Result := DirExists(UserDataDir) or DirExists(LegacyDataDir) or DirExists(QuickAccessDataDir);
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
#ifdef Wariant
  exit;   // dane wspólne z Velivo (%LOCALAPPDATA%\Velivo\Dane) - osobny program nigdy ich nie czyści
#endif
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
  if (CurPageID = wpReady) and VlcWanted then
  begin
    DownloadPage.Clear;
    DownloadPage.Add(VlcUrl, 'libvlc.nupkg', VlcSha256);
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        // bez odtwarzacza instalacja idzie dalej (Velivo dziala jak dotad)
        if not DownloadPage.AbortedByUser then MsgBox(FmtMessage(CustomMessage('VideoPlayerFailed'), [GetExceptionMessage]), mbError, MB_OK);
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

procedure BackupDir(Dir: String; Stamp: String);
begin
  if DirExists(Dir) then
    if not RenameFile(Dir, Dir + '.kopia-' + Stamp) then
      MsgBox(FmtMessage(CustomMessage('MoveFailed'), [Dir]), mbError, MB_OK);
end;

// 🛡 zapora Windows: ten program przyjmuje połączenia w każdej sieci (kabel, Wi-Fi) - jedno „Tak” przy instalacji, gdy właściciel jest przy komputerze;
// kasowana tylko własna reguła (wcześniej wszystkie reguły programu – gdy dodanie nowej się nie udało, Velivo zostawało bez żadnej i nic nie przechodziło)
procedure OpenFirewall(Exe: String);
var Code: Integer;
begin
  ShellExec('runas', ExpandConstant('{cmd}'), '/c "netsh advfirewall firewall delete rule name="' + ExtractFileName(Exe) + ' - Velivo" & netsh advfirewall firewall add rule name="' + ExtractFileName(Exe) + ' - Velivo" dir=in action=allow program="' + Exe + '" enable=yes profile=any' + ' & netsh advfirewall firewall delete rule name="Velivo - porty TCP" & netsh advfirewall firewall add rule name="Velivo - porty TCP" dir=in action=allow protocol=TCP localport=41917-41929,443 enable=yes profile=any' + ' & netsh advfirewall firewall delete rule name="Velivo - porty UDP" & netsh advfirewall firewall add rule name="Velivo - porty UDP" dir=in action=allow protocol=UDP localport=41917-41919 enable=yes profile=any"', '', SW_HIDE, ewWaitUntilTerminated, Code);   // porty Velivo i komunikatora zawsze otwarte (każda sieć), także niezależnie od ścieżki programu
end;

#ifndef Wariant
// 🧩 przejście na Velivo Premium: osobna Przeglądarka / Odtwarzacz są w nim zawarte - za zgodą usuwamy je (dane wspólne zostają)
function OtherUninstall(AppIdV: String): String;
begin
  Result := '';
  if not RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + AppIdV + '_is1', 'UninstallString', Result) then Result := '';
end;

procedure RemoveOther(AppIdV, Name, Exe, Pid: String);
var U, V, RunKey: String; Code, P: Integer;
begin
  U := OtherUninstall(AppIdV);
  if U = '' then exit;
  if SuppressibleMsgBox(FmtMessage(CustomMessage('Upgrade'), [Name]), mbConfirmation, MB_YESNO, IDNO) <> IDYES then exit;
  // usuwany program uruchamiał się z Windows - autostart przechodzi na Velivo (to samo opóźnienie)
  RunKey := 'Software\Microsoft\Windows\CurrentVersion\Run';
  if RegQueryStringValue(HKCU, RunKey, Pid, V) and not RegValueExists(HKCU, RunKey, 'Velivo') then
  begin
    P := Pos(' --autostart', V);
    if P > 0 then RegWriteStringValue(HKCU, RunKey, 'Velivo', '"' + ExpandConstant('{app}\{#Exe}') + '"' + Copy(V, P, 100));
  end;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM ' + Exe, '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(500);
  Exec(RemoveQuotes(U), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function OdtwarzaczDir: String;
begin
  Result := ExpandConstant('{localappdata}\Programs\Velivo Odtwarzacz');
end;

// był Odtwarzacz albo jest biblioteka filmów - odtwarzacz i TV zaznaczone same (nikt nie traci filmów przez niezaznaczone pole)
var Preselected: Boolean;
procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID <> wpSelectTasks) or Preselected then exit;
  Preselected := True;
  if FileExists(ExpandConstant('{app}\vlc\libvlc.dll')) then exit;
  if FileExists(OdtwarzaczDir + '\VelivoOdtwarzacz.exe') or FileExists(UserDataDir + '\filmy.json') then WizardSelectTasks('videoplayer,!videoplayer\iptv');   // sam odtwarzacz - TV tylko, gdy było włączone
  if FileExists(OdtwarzaczDir + '\iptv.on') then WizardSelectTasks('videoplayer\iptv');
end;
#endif

procedure CurStepChanged(CurStep: TSetupStep);
var Stamp: String;
begin
  // jezyk wybrany w instalatorze = jezyk programu (Velivo czyta go, gdy w ustawieniach jest "automatycznie")
  if CurStep = ssPostInstall then
    SaveStringToFile(ExpandConstant('{app}\language.txt'), ActiveLanguage, False);
#ifndef Wariant
  if CurStep = ssPostInstall then
  begin
    RemoveOther('{6E4C2B7A-3F1D-4C8E-9A51-PRZEGLADARKA02}', 'Velivo Przeglądarka', 'VelivoPrzegladarka.exe', 'VelivoPrzegladarka');
    RemoveOther('{6E4C2B7A-3F1D-4C8E-9A51-ODTWARZACZ01}', 'Velivo Odtwarzacz', 'VelivoOdtwarzacz.exe', 'VelivoOdtwarzacz');
  end;
#endif
#ifndef Klawiatura
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('firewall') then
    OpenFirewall(ExpandConstant('{app}\{#Exe}'));
#endif
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('videoplayer') and FileExists(ExpandConstant('{tmp}\libvlc.nupkg')) then
    InstallVlc;
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('videoplayer\iptv') then
    SaveStringToFile(ExpandConstant('{app}\iptv.on'), '1', False);
  if (CurStep = ssInstall) and (DataPage <> nil) and (DataPage.SelectedValueIndex = 1) then
  begin
    Stamp := GetDateTimeString('yyyymmdd-hhnnss', '-', '-');
    BackupDir(UserDataDir, Stamp);
    BackupDir(LegacyDataDir, Stamp);
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
var Code: Integer;
begin
  Result := '';
#ifndef Pelny
  Result := InstallDotnet10;   // wspólny .NET 10 (program bez wbudowanego)
  if Result <> '' then exit;
#endif
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

var VelivoWasRunning: Boolean;
function WasRunning: Boolean;
begin
  Result := VelivoWasRunning;
end;

// czy program działa - przez WMI, bez cmd.exe (Bitdefender brał instalator uruchamiający ukryty cmd za szkodliwy)
function ProcRunning(Name: String): Boolean;
var L, S, R: Variant;
begin
  Result := False;
  try
    L := CreateOleObject('WbemScripting.SWbemLocator');
    S := L.ConnectServer('.', 'root\CIMV2');
    R := S.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''' + Name + '''');
    Result := R.Count > 0;
  except
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code, I: Integer;
begin
  Result := InstallPrereqs;
  if Result <> '' then exit;
  VelivoWasRunning := ProcRunning('{#Exe}');
  // ukryte procesy Velivo (np. sprzatanie przy zamykaniu) razem z ich procesami WebView2
  // czekamy, aż Velivo naprawdę zniknie (pliki zwolnione) - do 10 s; nie da się zamknąć (Velivo jako administrator) - jasny komunikat zamiast „Odmowa dostępu”
  for I := 1 to 20 do
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM {#Exe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Sleep(500);
    if not ProcRunning('{#Exe}') then break;
  end;
  if ProcRunning('{#Exe}') then begin Result := CustomMessage('VelivoRunning'); exit; end;
  Sleep(500);
  MoveAsideUninstallFiles;
end;

#ifndef Wariant
// 🧩 odinstalowanie pełnego Velivo: komunikator (i serwer) prowadzi tylko Velivo albo osobny Komunikator - bez Komunikatora ostrzeżenie
function InitializeUninstall: Boolean;
begin
  Result := True;
  if FileExists(UserDataDir + '\komunikator.dat')
     and not FileExists(ExpandConstant('{localappdata}\Programs\Velivo Komunikator\VelivoKomunikator.exe')) then
    Result := SuppressibleMsgBox(CustomMessage('KomGone'), mbConfirmation, MB_YESNO, IDYES) = IDYES;
end;
#endif

function InitializeSetup: Boolean;
var Err: Integer;
begin
  // .NET i WebView2 doinstalowujemy sami w PrepareToInstall (pobranie z Microsoft, bez pytan)
  Result := True;
end;
