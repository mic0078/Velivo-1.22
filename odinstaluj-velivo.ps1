# 🧹 Velivo - bezpieczne odinstalowanie i porzadki
# Najprosciej: skrot „Odinstaluj Velivo” na pulpicie (zaklada go pakiet Velivo) - pyta okienkami, proponuje kopie danych (zip na pulpicie).
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
$velivoDir = Join-Path $local 'Velivo'   # dane wszystkich programow Velivo (Dane, Kursory…) - to idzie do kopii
$script:usuniete = 0; $script:kopia = $null
Add-Type -AssemblyName System.Windows.Forms
function Pytaj($tekst, $przyciski = 'YesNo', $ikona = 'Question') { return [System.Windows.Forms.MessageBox]::Show($tekst, 'Odinstaluj Velivo', $przyciski, $ikona).ToString() }
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

# 💾 kopia danych (zip na pulpicie) - pliki czytane bez blokowania, pamięć podręczna stron pominięta (odtwarza się sama)
function Kopia {
    foreach ($n in 'Velivo', 'VelivoOdtwarzacz', 'VelivoPrzegladarka', 'VelivoKlawiatura', 'VelivoSerwer', 'VelivoKomunikator', 'VelivoCentrum') { Get-Process -Name $n -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue }
    Start-Sleep 2   # programy Velivo zamknięte - pliki danych się nie zmieniają w trakcie kopii (zapis Velivo jest „wszystko albo nic”)
    $pomin = @('Cache', 'Code Cache', 'GPUCache', 'GrShaderCache', 'ShaderCache', 'DawnCache', 'DawnGraphiteCache', 'DawnWebGPUCache', 'Crashpad', 'component_crx_cache')
    $pliki = @(Get-ChildItem -LiteralPath $velivoDir -Recurse -File -Force -ErrorAction SilentlyContinue | Where-Object {
        $rel = $_.FullName.Substring($velivoDir.Length + 1); $seg = $rel.Split('\')
        -not ($seg | Where-Object { $pomin -contains $_ }) -and $_.Extension -ne '.tmp' -and $_.Extension -ne '.part' -and $_.Name -ne 'vlc.log' })
    $suma = ($pliki | Measure-Object Length -Sum).Sum; if (-not $suma) { $suma = 0 }
    $zip = Join-Path ([Environment]::GetFolderPath('Desktop')) ('Velivo-kopia-' + (Get-Date -Format 'yyyy-MM-dd-HHmm') + '.zip')
    $wolne = (Get-PSDrive -Name $zip.Substring(0, 1) -ErrorAction SilentlyContinue).Free
    if ($wolne -and $wolne -lt $suma + 200MB) { Zapisz ('Za malo miejsca na kopie: potrzeba ok. ' + [math]::Round($suma / 1GB, 2) + ' GB, wolne ' + [math]::Round($wolne / 1GB, 2) + ' GB') Red; return $false }
    Zapisz ('Kopia danych: ' + $pliki.Count + ' plikow, ' + [math]::Round($suma / 1MB) + ' MB -> ' + $zip) Cyan
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $ok = 0; $zle = 0
    try {
        $fs = [System.IO.File]::Open($zip, 'Create'); $za = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
        foreach ($f in $pliki) {
            $rel = $f.FullName.Substring($velivoDir.Length + 1).Replace('\', '/')
            try {
                $in = [System.IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite, Delete')
                try { $e = $za.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::Fastest); $e.LastWriteTime = $f.LastWriteTime; $out = $e.Open(); try { $in.CopyTo($out) } finally { $out.Dispose() } } finally { $in.Dispose() }
                $ok++; if ($ok % 500 -eq 0) { Write-Host ('  ' + $ok + ' / ' + $pliki.Count) }
            } catch { $zle++; Add-Content -Path $log -Value ('  pominiety w kopii: ' + $rel + ' - ' + $_.Exception.Message) -Encoding UTF8 }
        }
        $za.Dispose(); $fs.Dispose()
    } catch { Zapisz ('Kopia nie powiodla sie: ' + $_.Exception.Message) Red; try { $za.Dispose(); $fs.Dispose() } catch { }; return $false }
    # sprawdzenie: plik da się otworzyć i ma tyle wpisów, ile skopiowano
    $r = [System.IO.Compression.ZipFile]::OpenRead($zip)
    $n = $r.Entries.Count; $r.Dispose()
    if ($n -ne $ok) { Zapisz ('Kopia niekompletna: w pliku ' + $n + ' z ' + $ok) Red; return $false }
    $script:kopia = $zip
    Zapisz ('Kopia gotowa: ' + $zip + ' (' + $ok + ' plikow' + $(if ($zle) { ', ' + $zle + ' pominietych - lista w dzienniku' } else { '' }) + ')') Green
    if ($zle -gt 0 -and (Pytaj ($zle.ToString() + " plików nie dało się skopiować (lista w dzienniku na pulpicie).`n`nKontynuować odinstalowanie?") 'YesNo' 'Warning') -ne 'Yes') { return $false }
    return $true
}
if (-not $Proba -and -not $Tak) {
    $lista = ($apps | ForEach-Object { '   • ' + $_.DisplayName }) -join "`n"
    $msg = if ($Porzadki -or $apps.Count -eq 0) { "Sprzątnąć resztki po starych programach Velivo?`n`nZainstalowane programy zostają." }
           else { "Odinstalować wszystkie programy Velivo?`n`n" + $lista + "`n`nKażdy program odinstaluje się sam (Procesor, Audio Balance i Kursory przywracają ustawienia Windows), potem sprzątam resztki.`nTwoje dane zostają: " + $dane }
    if ((Pytaj $msg 'YesNo' 'Warning') -ne 'Yes') { Zapisz 'Przerwano - nic nie zmieniono.'; exit 0 }
    if (-not $Porzadki -and (Test-Path -LiteralPath $velivoDir)) {
        $odp = Pytaj ("Zrobić kopię bezpieczeństwa danych Velivo przed odinstalowaniem? (zalecane)`n`nBiblioteka, kontakty i rozmowy, hasła, ustawienia, nauka klawiatury - plik ZIP na pulpicie.`n`nTak - kopia, potem odinstalowanie`nNie - bez kopii`nAnuluj - nic nie rób") 'YesNoCancel'
        if ($odp -eq 'Cancel') { Zapisz 'Przerwano - nic nie zmieniono.'; exit 0 }
        if ($odp -eq 'Yes' -and -not (Kopia)) { Pytaj "Kopia się nie udała - odinstalowanie przerwane, nic nie zostało usunięte.`n`nSzczegóły: $log" 'OK' 'Error' | Out-Null; exit 1 }
    }
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
    try { & $co; $script:usuniete++; Zapisz ('  usuniete: ' + $opis) } catch { Zapisz ('  nie udalo sie: ' + $opis + ' - ' + $_.Exception.Message) Yellow }
}
$zywe = @(Get-ItemProperty $klucze -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'Velivo*' -and $_.InstallLocation } | ForEach-Object { $_.InstallLocation.TrimEnd('\') })
# 3a. foldery programow w %LOCALAPPDATA%\Programs: Velivo* bez wpisu deinstalacji + stare nazwy (Przegladarka, Tarcza); folder "Dane" w srodku nigdy
$foldery = @(Get-ChildItem -LiteralPath $progs -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'Velivo*' -or $_.Name -eq 'Przegladarka' -or $_.Name -eq 'Tarcza' })
foreach ($f in $foldery) {
    if ($zywe -contains $f.FullName.TrimEnd('\')) { continue }
    if ($f.Name -eq 'Velivo Pakiet' -and $Porzadki) { continue }   # tu mieszka ten skrypt - przy samych porządkach zostaje
    $d = Join-Path $f.FullName 'Dane'
    if (Test-Path -LiteralPath $d) {   # dane ze starej wersji (do 2026-11 w folderze programu) - tylko pliki programu, Dane zostaja
        $f2 = $f
        Usun ($f.FullName + ' (bez folderu Dane)') { Get-ChildItem -LiteralPath $f2.FullName -Force | Where-Object { $_.Name -ne 'Dane' } | Remove-Item -Recurse -Force -ErrorAction Stop }
        Zapisz ('  zostawiony (dane ze starej wersji): ' + $d) Yellow
    } else { $f2 = $f; Usun $f.FullName { Remove-Item -LiteralPath $f2.FullName -Recurse -Force -ErrorAction Stop } }
}
# skrót „Odinstaluj Velivo” - przy pełnym odinstalowaniu znika razem ze skryptem
if (-not $Porzadki) { foreach ($m in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) { $l = Join-Path $m 'Odinstaluj Velivo.lnk'; if (Test-Path -LiteralPath $l) { $l2 = $l; Usun ('skrot ' + $l) { Remove-Item -LiteralPath $l2 -Force -ErrorAction Stop } } } }
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
if (-not $Proba -and -not $Tak) {
    $k = if ($script:kopia) { "`n`nKopia danych: " + $script:kopia } else { '' }
    Pytaj ("Gotowe.`n`nSprzątnięte resztki: " + $script:usuniete + $k + "`n`nTwoje dane: " + $(if ($UsunDane) { 'w Koszu' } else { $dane }) + "`nDziennik: " + $log) 'OK' 'Information' | Out-Null
}
