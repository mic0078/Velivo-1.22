# Velivo - budowa instalatora na wlasnym komputerze (bez GitHub Actions).
# Uzycie: PowerShell w folderze repozytorium ->  .\zbuduj.ps1        (z testami)
#                                              .\zbuduj.ps1 -BezTestow
# Wymagane raz: winget install Git.Git Microsoft.DotNet.SDK.10 JRSoftware.InnoSetup
param([switch]$BezTestow, [switch]$Podstawowa, [string[]]$Tylko, [switch]$WspolnyNet)   # -WspolnyNet: wersja testowa 1.23 - programy bez wbudowanego .NET (wspólny .NET 10 od Microsoftu, raz)   # -Podstawowa: Velivo bezpłatne (bez Audio Balance, VST3, Supertonic i XTTS)
$ErrorActionPreference = 'Stop'
# -Tylko Velivo,Odtwarzacz,Przegladarka,Klawiatura,Serwer,Komunikator - buduje tylko wybrane programy (bez -Tylko: wszystkie)
$Tylko = @($Tylko | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$wer = if ($WspolnyNet) { '1.23' } else { '1.22' }
function Buduj($n) { return $Tylko.Count -eq 0 -or ($Tylko -contains $n) }
if ($Tylko.Count -gt 0) { Write-Host ("Tylko: " + ($Tylko -join ', ')) -ForegroundColor Cyan }
Set-Location $PSScriptRoot

Write-Host "1/4 Najnowsze zmiany (git pull)..." -ForegroundColor Cyan
$galaz = (git rev-parse --abbrev-ref HEAD).Trim()
if ($galaz -eq "main" -or $galaz -eq "HEAD") { git checkout rozwoj; $galaz = "rozwoj" }   # nigdy z main (wersja publiczna); galaz probna (np. lektor-cache) zostaje
git pull origin $galaz
if ($LASTEXITCODE -ne 0) { Write-Host "Nie udalo sie pobrac zmian z galezi $galaz." -ForegroundColor Red; exit 1 }
Write-Host ("Budowana wersja: " + (git log --oneline -1)) -ForegroundColor Cyan

if (-not $BezTestow) {
    Write-Host "2/4 Testy..." -ForegroundColor Cyan
    dotnet test testy\Velivo.Tests\Velivo.Tests.csproj -c Release --filter "Category!=Vlc"
    if ($LASTEXITCODE -ne 0) { Write-Host "Testy nie przeszly - instalator NIE zostal zbudowany." -ForegroundColor Red; exit 1 }
} else { Write-Host "2/4 Testy pominiete" -ForegroundColor Yellow }

Write-Host "3/4 Kompilacja programu..." -ForegroundColor Cyan
dotnet build-server shutdown 2>$null | Out-Null   # zwalnia pliki trzymane przez poprzednia kompilacje / testy
Remove-Item -Recurse -Force src\bin\Release\net10.0-windows10.0.19041.0\win-x64 -ErrorAction SilentlyContinue
$edycja = if ($Podstawowa) { "-p:Podstawowa=true" } else { "-p:Podstawowa=false" }
# ⚡ wszystkie wybrane programy naraz: każdy we własnym folderze roboczym (build\obj\<program>), dziennik w build\log-<program>.txt
$zadania = @()
foreach ($w in @('Velivo', 'Odtwarzacz', 'Przegladarka', 'Klawiatura', 'Serwer', 'Komunikator') | Where-Object { Buduj $_ }) {
    $d = 'build\' + $w.ToLower()
    Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue
    $proj = if ($w -eq 'Komunikator') { 'komunikator-windows\VelivoKomunikator.csproj' } else { 'src\Velivo.csproj' }
    $ob = (Join-Path $PSScriptRoot "build\obj\$w") + '/'   # poza folderem projektu - kompilacje naraz sobie nie przeszkadzają (ukośnik na końcu: bez \" w cudzysłowie)
    $a = @('publish', $proj, '-c', 'Release', '-r', 'win-x64', '--self-contained', $(if ($WspolnyNet) { 'false' } else { 'true' }), '-o', $d, $edycja, "-p:BaseIntermediateOutputPath=$ob", "-p:MSBuildProjectExtensionsPath=$ob", "-p:OutputPath=$($ob)bin/")
    if ($w -ne 'Velivo' -and $w -ne 'Komunikator') { $a += "-p:Wariant=$w" }
    if ($WspolnyNet) { $a += '-p:TargetLatestRuntimePatch=false' }
    $log = Join-Path $PSScriptRoot "build\log-$w.txt"
    $arg = ($a | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '   # ścieżka repozytorium ze spacjami („Velivo 1.22 z github”)
    $p = Start-Process dotnet -ArgumentList $arg -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError ($log + '.err')
    $zadania += [pscustomobject]@{ Nazwa = $w; Proces = $p; Log = $log }
    Write-Host ("  buduje: " + $w) -ForegroundColor DarkGray
}
$zle = @()
foreach ($z in $zadania) { $z.Proces.WaitForExit(); if ($z.Proces.ExitCode -ne 0) { $zle += $z } else { Write-Host ("  gotowe: " + $z.Nazwa) -ForegroundColor Green } }
# pliki diagnostyczne (pdb, DirectML.Debug) - niepotrzebne do działania, ok. 19 MB na program
foreach ($z in $zadania) { $d = 'build\' + $z.Nazwa.ToLower(); Remove-Item "$d\*.pdb", "$d\DirectML.Debug.*" -Force -ErrorAction SilentlyContinue }
if ($zle.Count -gt 0) {
    foreach ($z in $zle) { Write-Host ("Kompilacja " + $z.Nazwa + " nie powiodla sie:") -ForegroundColor Red; Get-Content $z.Log -ErrorAction SilentlyContinue | Select-String ' error ' | Select-Object -First 10 | ForEach-Object { Write-Host ("  " + $_.Line.Trim()) -ForegroundColor Red } }
    exit 1
}
# darmowe efekty VST3 razem z Velivo i Odtwarzaczem (pelna wersja): z dodatki\VST3 (poza repozytorium)
if (-not $Podstawowa -and (Test-Path "dodatki\VST3")) {
    foreach ($d in @('build\velivo', 'build\odtwarzacz') | Where-Object { Test-Path $_ }) { Copy-Item -Recurse -Force "dodatki\VST3" "$d\VST3" }
    Write-Host ("Wtyczki VST3 w instalatorze: " + ((Get-ChildItem "dodatki\VST3" -Filter *.vst3).Count)) -ForegroundColor Green
}

Write-Host "4/4 Instalator..." -ForegroundColor Cyan
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { Write-Host "Brak Inno Setup: winget install JRSoftware.InnoSetup" -ForegroundColor Red; exit 1 }
# instalator budowany lokalnie trafia do build\instalator (poza repozytorium) - nigdy nie miesza sie
# ze starym plikiem Instalator\Velivo-Setup-1.22.exe z repozytorium (strona pobierania)
$outDir = Join-Path $PSScriptRoot 'build\instalator'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
if (Buduj 'Velivo') {
$nazwa = if ($Podstawowa) { "Velivo-Podstawowa-Setup-$wer" } else { "Velivo-Setup-$wer" }   # 💎 Premium: dotychczasowa nazwa; bezpłatne: osobny plik
$old = Join-Path $outDir ($nazwa + '.exe')
for ($i = 0; $i -lt 10 -and (Test-Path $old); $i++) { try { Remove-Item $old -Force -ErrorAction Stop } catch { Start-Sleep 2 } }   # stary instalator (otwarty / sprawdzany przez antywirus) - kilka prob
# nadal otwarty (instalator w tle / antywirus) - usunac sie nie da, ale zmienic nazwe tak: odkladamy go na bok
if (Test-Path $old) { try { Rename-Item $old ("Velivo-Setup-stary-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".exe") -ErrorAction Stop } catch { } }
if (Test-Path $old) { Write-Host "Stary instalator jest w uzyciu - zamknij okno instalacji Velivo i uruchom skrypt ponownie." -ForegroundColor Red; exit 1 }
# antywirus skanuje nowy plik w chwili wstawiania ikony (EndUpdateResource failed) - do 6 prob, coraz dluzsza przerwa (dziala bez wykluczen w antywirusie)
for ($i = 1; $i -le 6; $i++) { & $iscc $(if ($WspolnyNet) { "/DAppVer=$wer" } else { "/DPelny" }) $(if ($Podstawowa) { "/DBezplatna" } else { "/DPremium" }) "/DOutName=$nazwa" "/O$outDir" src\installer.iss; if ($LASTEXITCODE -eq 0) { break }; if ($i -lt 6) { Write-Host "Antywirus sprawdza plik - ponawiam budowe instalatora ($i/6)..." -ForegroundColor Yellow; Start-Sleep (5 * $i) } }
if ($LASTEXITCODE -ne 0) { Write-Host "Budowa instalatora nie powiodla sie." -ForegroundColor Red; exit 1 }
}
if (Buduj 'Komunikator') {
$oldK = Join-Path $outDir "Velivo-Komunikator-Setup-$wer.exe"
for ($i = 0; $i -lt 10 -and (Test-Path $oldK); $i++) { try { Remove-Item $oldK -Force -ErrorAction Stop } catch { Start-Sleep 2 } }
for ($i = 1; $i -le 6; $i++) { & $iscc $(if ($WspolnyNet) { @("/DWspolnyNet", "/DAppVer=$wer") } else { @() }) "/O$outDir" komunikator-windows\installer-komunikator.iss; if ($LASTEXITCODE -eq 0) { break }; if ($i -lt 6) { Write-Host "Antywirus sprawdza plik - ponawiam budowe instalatora Komunikatora ($i/6)..." -ForegroundColor Yellow; Start-Sleep (5 * $i) } }
if ($LASTEXITCODE -ne 0) { Write-Host "Budowa instalatora Komunikatora nie powiodla sie." -ForegroundColor Red; exit 1 }
}
foreach ($w in @('Odtwarzacz', 'Przegladarka', 'Klawiatura', 'Serwer') | Where-Object { Buduj $_ }) {
    $oldW = Join-Path $outDir ("Velivo-$w-Setup-$wer.exe")
    for ($i = 0; $i -lt 10 -and (Test-Path $oldW); $i++) { try { Remove-Item $oldW -Force -ErrorAction Stop } catch { Start-Sleep 2 } }
    for ($i = 1; $i -le 6; $i++) { & $iscc $(if ($WspolnyNet) { "/DAppVer=$wer" } else { "/DPelny" }) "/D$w" "/O$outDir" src\installer.iss; if ($LASTEXITCODE -eq 0) { break }; if ($i -lt 6) { Write-Host "Antywirus sprawdza plik - ponawiam budowe instalatora $w ($i/6)..." -ForegroundColor Yellow; Start-Sleep (5 * $i) } }
    if ($LASTEXITCODE -ne 0) { Write-Host "Budowa instalatora Velivo $w nie powiodla sie." -ForegroundColor Red; exit 1 }
}

if (Buduj 'Velivo') { Write-Host ("Gotowe: build\instalator\" + $nazwa + ".exe (" + $(if ($Podstawowa) { "Velivo bezplatne" } else { "Velivo Premium" }) + "; zamknij Velivo calkowicie przed instalacja)") -ForegroundColor Green }
if (Buduj 'Komunikator') { Write-Host "Gotowe: build\instalator\Velivo-Komunikator-Setup-$wer.exe (sam komunikator i serwer - bez przegladarki)" -ForegroundColor Green }
if (Buduj 'Odtwarzacz') { Write-Host "Gotowe: build\instalator\Velivo-Odtwarzacz-Setup-$wer.exe (filmy, TV, Kanapa, Pilot, lektor - bez przegladarki)" -ForegroundColor Green }
if (Buduj 'Klawiatura') { Write-Host "Gotowe: build\instalator\Velivo-Klawiatura-Setup-$wer.exe (sama klawiatura ekranowa i poprawianie pisania - w kazdym programie Windows)" -ForegroundColor Green }
if (Buduj 'Przegladarka') { Write-Host "Gotowe: build\instalator\Velivo-Przegladarka-Setup-$wer.exe (sama przegladarka - filmy otwiera Velivo Odtwarzacz)" -ForegroundColor Green }
if (Buduj 'Serwer') { Write-Host "Gotowe: build\instalator\Velivo-Serwer-Setup-$wer.exe (serwer filmow - biblioteka dla Twoich komputerow, w tle)" -ForegroundColor Green }
Start-Process $outDir
