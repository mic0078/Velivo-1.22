# 🧹 Velivo - bezpieczne odinstalowanie i porzadki
# Uzycie (PowerShell, NIE jako administrator):
#   .\odinstaluj-velivo.ps1              - pokazuje, co zrobi, pyta, potem odinstalowuje WSZYSTKIE programy Velivo i sprzata resztki
#   .\odinstaluj-velivo.ps1 -Proba       - tylko pokazuje, co by zrobil (nic nie zmienia)
#   .\odinstaluj-velivo.ps1 -Porzadki    - programy zostaja; sprzata tylko resztki po starych / odinstalowanych programach Velivo
#   .\odinstaluj-velivo.ps1 -UsunDane    - dodatkowo folder danych (biblioteka, kontakty, hasla, nauka klawiatury) do KOSZA (da sie odzyskac)
# Bezpieczenstwo:
#   - kazdy program odinstalowuje jego wlasny deinstalator (przywraca ustawienia zasilania - Procesor, glosniki - Audio Balance, kursory - Kursory)
#   - usuwane sa tylko rzeczy z nazwa Velivo, ktorych program juz nie istnieje (skroty, wpisy Windows, foldery w Programs)
#   - folder danych %LOCALAPPDATA%\Velivo\Dane zostaje (chyba ze -UsunDane: wtedy do Kosza, nie na zawsze)
#   - reguly zapory Windows nie sa ruszane (zostaja dla nastepnej instalacji)
#   - dziennik na pulpicie: velivo-deinstalacja.txt
param([switch]$Proba, [switch]$Porzadki, [switch]$UsunDane, [switch]$Tak)
$ErrorActionPreference = 'Continue'
$local = [Environment]::GetFolderPath('LocalApplicationData')
$progs = Join-Path $local 'Programs'
$dane = Join-Path $local 'Velivo\Dane'
$log = Join-Path ([Environment]::GetFolderPath('Desktop')) 'velivo-deinstalacja.txt'
$plan = New-Object System.Collections.Generic.List[string]
function Zapisz($t, $kolor = 'Gray') { Write-Host $t -ForegroundColor $kolor; Add-Content -Path $log -Value ((Get-Date -Format 'HH:mm:ss') + '  ' + $t) -Encoding UTF8 }
function ExeZ($s) {   # sciezka exe z polecenia ("C:\...\x.exe" --arg / C:\...\x.exe,0)
    if (-not $s) { return $null }
    $s = [Environment]::ExpandEnvironmentVariables([string]$s).Trim()
    if ($s.StartsWith('"')) { $e = $s.IndexOf('"', 1); if ($e -gt 1) { return $s.Substring(1, $e - 1) } }
    $i = $s.ToLower().IndexOf('.exe'); if ($i -gt 0) { return $s.Substring(0, $i + 4) }
    return $null
}
function Brak($exe) { return ($exe -and -not (Test-Path -LiteralPath $exe)) }

Set-Content -Path $log -Value ('Velivo - deinstalacja / porzadki ' + (Get-Date)) -Encoding UTF8
if ($Proba) { Zapisz 'TRYB PROBY - nic nie bedzie zmieniane.' Yellow }

# 1. zainstalowane programy Velivo (wpisy deinstalacji uzytkownika)
$klucze = @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*')
$apps = @(Get-ItemProperty $klucze -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'Velivo*' -and $_.UninstallString })
# kolejnosc: najpierw programy, ktore cos w Windows przywracaja (zasilanie, glosniki, kursory), pakiet na koncu
$waga = { param($n) if ($n -like '*Procesor*') { 0 } elseif ($n -like '*Audio Balance*') { 1 } elseif ($n -like '*Kursory*') { 2 } elseif ($n -like '*pakiet*') { 9 } else { 5 } }
$apps = @($apps | Sort-Object { & $waga $_.DisplayName }, DisplayName)
if (-not $Porzadki) {
    if ($apps.Count -eq 0) { Zapisz 'Nie znaleziono zainstalowanych programow Velivo.' }
    else { Zapisz ('Programy do odinstalowania (' + $apps.Count + '):') Cyan; foreach ($a in $apps) { Zapisz ('  - ' + $a.DisplayName + '  ' + $a.DisplayVersion) } }
}
Zapisz ('Dane zostaja: ' + $dane) Green
if ($UsunDane) { Zapisz ('UWAGA: folder danych pojdzie do Kosza: ' + $dane) Yellow }
if (-not $Proba -and -not $Tak) {
    $odp = Read-Host 'Kontynuowac? (T = tak)'
    if ($odp -notmatch '^[TtYy]') { Zapisz 'Przerwano - nic nie zmieniono.'; exit 0 }
}

# 2. odinstalowanie wlasnymi deinstalatorami (cicho)
if (-not $Porzadki) {
    foreach ($a in $apps) {
        if (-not (Test-Path $a.PSPath)) { Zapisz ('  ' + $a.DisplayName + ': juz odinstalowany'); continue }   # np. razem z innym programem
        $exe = ExeZ $a.UninstallString
        if (-not $exe -or (Brak $exe)) { Zapisz ('  ' + $a.DisplayName + ': brak deinstalatora - wpis zostanie usuniety w porzadkach') Yellow; continue }
        if ($Proba) { Zapisz ('  [proba] odinstalowalbym: ' + $a.DisplayName); continue }
        Zapisz ('Odinstalowuje: ' + $a.DisplayName + ' ...') Cyan
        try { Start-Process -FilePath $exe -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -ErrorAction Stop } catch { Zapisz ('  blad: ' + $_.Exception.Message) Red; continue }
        # deinstalator Inno uruchamia swoja kopie z TEMP - czekamy, az wpis zniknie (do 2 min)
        $key = $a.PSPath
        for ($i = 0; $i -lt 120 -and (Test-Path $key); $i++) { Start-Sleep 1 }
        if (Test-Path $key) { Zapisz ('  ' + $a.DisplayName + ': deinstalator nie skonczyl w 2 min - sprawdz recznie (Ustawienia -> Aplikacje)') Red }
        else { Zapisz ('  gotowe') Green }
    }
}

# 3. porzadki - tylko resztki Velivo, ktorych program juz nie istnieje
Zapisz 'Porzadki...' Cyan
function Usun($opis, [scriptblock]$co) {
    if ($Proba) { Zapisz ('  [proba] usunalbym: ' + $opis); return }
    try { & $co; Zapisz ('  usuniete: ' + $opis) } catch { Zapisz ('  nie udalo sie: ' + $opis + ' - ' + $_.Exception.Message) Yellow }
}
$zywe = @(Get-ItemProperty $klucze -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'Velivo*' -and $_.InstallLocation } | ForEach-Object { $_.InstallLocation.TrimEnd('\') })
# 3a. foldery programow w %LOCALAPPDATA%\Programs: Velivo* bez wpisu deinstalacji + stare nazwy (Przegladarka, Tarcza); folder "Dane" w srodku nigdy
$foldery = @(Get-ChildItem -LiteralPath $progs -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'Velivo*' -or $_.Name -eq 'Przegladarka' -or $_.Name -eq 'Tarcza' })
foreach ($f in $foldery) {
    if ($zywe -contains $f.FullName.TrimEnd('\')) { continue }
    $d = Join-Path $f.FullName 'Dane'
    if (Test-Path -LiteralPath $d) {   # dane ze starej wersji (do 2026-11 w folderze programu) - tylko pliki programu, Dane zostaja
        $f2 = $f
        Usun ($f.FullName + ' (bez folderu Dane)') { Get-ChildItem -LiteralPath $f2.FullName -Force | Where-Object { $_.Name -ne 'Dane' } | Remove-Item -Recurse -Force -ErrorAction Stop }
        Zapisz ('  zostawiony (dane ze starej wersji): ' + $d) Yellow
    } else { $f2 = $f; Usun $f.FullName { Remove-Item -LiteralPath $f2.FullName -Recurse -Force -ErrorAction Stop } }
}
# 3b. skroty (pulpit, menu Start, autostart) z Velivo w nazwie, ktore prowadza do nieistniejacego programu
$sh = New-Object -ComObject WScript.Shell
$miejsca = @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Startup'), (Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'))
foreach ($m in $miejsca) {
    if (-not (Test-Path -LiteralPath $m)) { continue }
    foreach ($l in @(Get-ChildItem -LiteralPath $m -Filter '*.lnk' -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'Velivo|Przegl.darka|Tarcza|Komunikator' })) {
        $t = $null; try { $t = $sh.CreateShortcut($l.FullName).TargetPath } catch { }
        if ($t -and (Brak $t) -and ($t -like '*Velivo*' -or $t -like '*Przegladarka*' -or $t -like '*Tarcza*')) { $l2 = $l; Usun ('skrot ' + $l.FullName) { Remove-Item -LiteralPath $l2.FullName -Force -ErrorAction Stop } }
    }
}
# puste foldery Velivo w menu Start
Get-ChildItem -LiteralPath ([Environment]::GetFolderPath('Programs')) -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'Velivo*' -and -not (Get-ChildItem -LiteralPath $_.FullName -Force) } | ForEach-Object { $p = $_; Usun ('pusty folder menu Start ' + $p.FullName) { Remove-Item -LiteralPath $p.FullName -Force -ErrorAction Stop } }
# 3c. wpisy Windows (HKCU) wskazujace na nieistniejacy program Velivo
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$rp = Get-ItemProperty $run -ErrorAction SilentlyContinue
if ($rp) { foreach ($p in $rp.PSObject.Properties) { if ($p.Name -like 'PS*') { continue }; $e = ExeZ $p.Value; if ($e -like '*Velivo*' -and (Brak $e)) { $n = $p.Name; Usun ('autostart ' + $n) { Remove-ItemProperty -Path $run -Name $n -ErrorAction Stop } } } }
foreach ($k in @(Get-ChildItem 'HKCU:\Software\Velivo\Programy' -ErrorAction SilentlyContinue)) {
    $e = (Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue).Exe
    if (Brak $e) { $k2 = $k; Usun ('spis programow: ' + $k.PSChildName) { Remove-Item $k2.PSPath -Recurse -Force -ErrorAction Stop } }
}
foreach ($k in @(Get-ChildItem 'HKCU:\Software\Classes' -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like 'Velivo*' })) {
    $cmd = (Get-ItemProperty ($k.PSPath + '\shell\open\command') -ErrorAction SilentlyContinue).'(default)'
    $e = ExeZ $cmd
    if (Brak $e) { $k2 = $k; Usun ('skojarzenie plikow ' + $k.PSChildName) { Remove-Item $k2.PSPath -Recurse -Force -ErrorAction Stop } }
}
foreach ($k in @(Get-ChildItem 'HKCU:\Software\Clients\StartMenuInternet' -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like 'Velivo*' })) {
    $e = ExeZ (Get-ItemProperty ($k.PSPath + '\shell\open\command') -ErrorAction SilentlyContinue).'(default)'
    if (Brak $e) {
        $k2 = $k; Usun ('przegladarka na liscie Windows: ' + $k.PSChildName) { Remove-Item $k2.PSPath -Recurse -Force -ErrorAction Stop }
        $n = $k.PSChildName; if ((Get-ItemProperty 'HKCU:\Software\RegisteredApplications' -ErrorAction SilentlyContinue).$n) { Usun ('RegisteredApplications ' + $n) { Remove-ItemProperty 'HKCU:\Software\RegisteredApplications' -Name $n -ErrorAction Stop } }
    }
}
foreach ($a in @(Get-ItemProperty $klucze -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'Velivo*' })) {
    $e = ExeZ $a.UninstallString
    if (-not $e -or (Brak $e)) { $a2 = $a; Usun ('martwy wpis w Aplikacjach: ' + $a.DisplayName) { Remove-Item $a2.PSPath -Recurse -Force -ErrorAction Stop } }
}
# 3d. zaplanowane zadania Velivo z nieistniejacym programem (zadanie administratora - moze wymagac uprawnien)
foreach ($t in @(Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like 'Velivo*' })) {
    $e = ExeZ ($t.Actions | Select-Object -First 1).Execute
    if (Brak $e) { $t2 = $t; Usun ('zadanie ' + $t.TaskName) { Unregister-ScheduledTask -TaskName $t2.TaskName -TaskPath $t2.TaskPath -Confirm:$false -ErrorAction Stop } }
}
# 3e. pliki tymczasowe Velivo (TEMP) i niedokonczone zapisy w danych (starsze niz 2 dni)
Get-ChildItem -LiteralPath $env:TEMP -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'Velivo*' -and $_.LastWriteTime -lt (Get-Date).AddDays(-1) } | ForEach-Object { $p = $_; Usun ('TEMP ' + $p.Name) { Remove-Item -LiteralPath $p.FullName -Recurse -Force -ErrorAction Stop } }
if (Test-Path -LiteralPath $dane) {
    Get-ChildItem -LiteralPath $dane -File -Recurse -Force -ErrorAction SilentlyContinue | Where-Object { ($_.Extension -eq '.tmp' -or $_.Extension -eq '.part') -and $_.LastWriteTime -lt (Get-Date).AddDays(-2) } | ForEach-Object { $p = $_; Usun ('niedokonczony zapis ' + $p.FullName) { Remove-Item -LiteralPath $p.FullName -Force -ErrorAction Stop } }
}
# 4. dane - tylko na wyrazne zyczenie, do Kosza
if ($UsunDane -and (Test-Path -LiteralPath $dane)) {
    if ($Proba) { Zapisz ('  [proba] folder danych do Kosza: ' + $dane) }
    else {
        try { Add-Type -AssemblyName Microsoft.VisualBasic; [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($dane, 'OnlyErrorDialogs', 'SendToRecycleBin'); Zapisz ('Folder danych w Koszu: ' + $dane) Yellow }
        catch { Zapisz ('Folderu danych nie udalo sie przeniesc do Kosza: ' + $_.Exception.Message) Red }
    }
}
Zapisz ''
Zapisz ('Gotowe. Dziennik: ' + $log) Green
if (-not $UsunDane) { Zapisz ('Twoje dane sa w: ' + $dane + ' - nowa instalacja Velivo je wczyta.') Green }
