# Velivo Audio Balance for Windows - budowa OSOBNO od Velivo (własny program, własny instalator).
# Użycie: PowerShell w folderze repozytorium ->  .\zbuduj-audiobalance.ps1
# Wymagane raz: winget install Git.Git Microsoft.DotNet.SDK.10 JRSoftware.InnoSetup
# VB-Audio Virtual Cable pobiera instalator (podczas instalacji) albo program (przycisk „Zainstaluj VB-Cable automatycznie”).
$ErrorActionPreference = 'Stop'
trap { Write-Host "Blad: $_" -ForegroundColor Red; Read-Host 'Nacisnij Enter, aby zamknac'; exit 1 }
$repo = $PSScriptRoot
Set-Location $repo

Write-Host "1/4 Najnowsze zmiany (git pull)..." -ForegroundColor Cyan
$galaz = (git rev-parse --abbrev-ref HEAD).Trim()
git pull origin $galaz
Write-Host ("Budowana wersja: " + (git log --oneline -1)) -ForegroundColor Cyan

# działający Audio Balance ZOSTAJE włączony na czas budowy (muzyka gra dalej bez zmian) - budowa nie rusza zainstalowanego programu; zamyka go dopiero instalator.
# Wyjątek: program uruchomiony wprost z folderu budowy (build\audiobalance) - trzeba go zamknąć, bo folder jest budowany od nowa (dźwięk wraca wtedy do głośników).
$ab = Get-Process VelivoAudioBalance -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith((Join-Path $repo 'build\audiobalance'), [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
if ($ab) { try { Start-Process $ab.Path -ArgumentList '--aktualizacja' -Wait } catch {}; Stop-Process -Id $ab.Id -Force -ErrorAction SilentlyContinue; Write-Host "Audio Balance zamkniety, dzwiek wrocil do glosnikow (po instalacji wroci przez Audio Balance)" -ForegroundColor Yellow }

Write-Host "2/4 Kompilacja Velivo Audio Balance for Windows..." -ForegroundColor Cyan
Remove-Item -Recurse -Force build\audiobalance -ErrorAction SilentlyContinue
dotnet publish audio-balance-windows\VelivoAudioBalance.csproj -c Release -r win-x64 --self-contained false -p:TargetLatestRuntimePatch=false -o build\audiobalance
if ($LASTEXITCODE -ne 0) { Write-Host "Kompilacja Velivo Audio Balance for Windows nie powiodla sie." -ForegroundColor Red; Read-Host 'Enter'; exit 1 }

Write-Host "3/4 VB-Audio Virtual Cable..." -ForegroundColor Cyan
# skrypt NICZEGO nie pobiera (antywirus brał pobieranie + rozpakowanie za wirusa); VB-Cable pobiera instalator albo program.
# Gdy rozpakowany pakiet jest w audio-balance-windows\vbcable - trafia do instalatora (instalacja bez internetu).
$vb = Join-Path $PSScriptRoot 'audio-balance-windows\vbcable'
if (Test-Path (Join-Path $vb 'VBCABLE_Setup_x64.exe')) { Copy-Item -Recurse -Force $vb 'build\audiobalance\VBCABLE'; Write-Host "VB-Cable w instalatorze: tak" -ForegroundColor Green } else { Write-Host "VB-Cable pobierze instalator albo program (z serwera VB-Audio)" -ForegroundColor Cyan }

Write-Host "4/4 Instalator..." -ForegroundColor Cyan
# otwarty instalator z poprzedniej próby trzyma plik - budowa nie może go nadpisać (to nie antywirus)
while (Get-Process -Name "Velivo-AudioBalance-for-Windows-Setup-1.22", "Velivo-AudioBalance-for-Windows-Setup-1.22.tmp" -ErrorAction SilentlyContinue) { Write-Host "Otwarte jest okno instalatora Velivo-AudioBalance-for-Windows-Setup-1.22 z poprzedniej proby - zamknij je (Anuluj / Przerwij instalacje), potem nacisnij Enter." -ForegroundColor Yellow; Read-Host | Out-Null }
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { Write-Host "Brak Inno Setup: winget install JRSoftware.InnoSetup" -ForegroundColor Red; Read-Host 'Enter'; exit 1 }
$outDir = Join-Path $repo 'build\instalator'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$old = Join-Path $outDir 'Velivo-AudioBalance-for-Windows-Setup-1.22.exe'
for ($i = 0; $i -lt 10 -and (Test-Path $old); $i++) { try { Remove-Item $old -Force -ErrorAction Stop } catch { Start-Sleep 2 } }
for ($i = 1; $i -le 6; $i++) { & $iscc "/O$outDir" audio-balance-windows\installer-audiobalance.iss; if ($LASTEXITCODE -eq 0) { break }; if ($i -lt 6) { Write-Host "Antywirus sprawdza plik - ponawiam ($i/6)..." -ForegroundColor Yellow; Start-Sleep ($i * 3) } }
if ($LASTEXITCODE -ne 0) { Write-Host "Budowa instalatora Velivo Audio Balance nie powiodla sie." -ForegroundColor Red; Read-Host 'Enter'; exit 1 }
Write-Host "Gotowe: build\instalator\Velivo-AudioBalance-for-Windows-Setup-1.22.exe" -ForegroundColor Green
Start-Process $outDir
