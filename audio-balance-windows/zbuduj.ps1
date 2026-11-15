# Velivo Audio Balance - budowa OSOBNO od Velivo (własny program, własny instalator).
# Użycie: PowerShell w folderze repozytorium ->  .\audio-balance-windows\zbuduj.ps1
# Wymagane raz: winget install Git.Git Microsoft.DotNet.SDK.10 JRSoftware.InnoSetup
# VB-Audio Virtual Cable pobiera się sam (raz) z serwera VB-Audio i trafia do instalatora - niczego nie trzeba wrzucać ręcznie.
$ErrorActionPreference = 'Stop'
trap { Write-Host "Blad: $_" -ForegroundColor Red; Read-Host 'Nacisnij Enter, aby zamknac'; exit 1 }
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo

Write-Host "1/4 Najnowsze zmiany (git pull)..." -ForegroundColor Cyan
$galaz = (git rev-parse --abbrev-ref HEAD).Trim()
git pull origin $galaz
Write-Host ("Budowana wersja: " + (git log --oneline -1)) -ForegroundColor Cyan

Write-Host "2/4 Kompilacja Velivo Audio Balance..." -ForegroundColor Cyan
Remove-Item -Recurse -Force build\audiobalance -ErrorAction SilentlyContinue
dotnet publish audio-balance-windows\VelivoAudioBalance.csproj -c Release -r win-x64 --self-contained true -o build\audiobalance
if ($LASTEXITCODE -ne 0) { Write-Host "Kompilacja Velivo Audio Balance nie powiodla sie." -ForegroundColor Red; Read-Host 'Enter'; exit 1 }

Write-Host "3/4 VB-Audio Virtual Cable..." -ForegroundColor Cyan
# pobierany raz do audio-balance-windows\vbcable (poza repozytorium) - przy następnych budowach już jest
$vb = Join-Path $PSScriptRoot 'vbcable'
if (-not (Test-Path (Join-Path $vb 'VBCABLE_Setup_x64.exe'))) {
    $zip = Join-Path $env:TEMP 'VBCABLE_Driver_Pack.zip'; $ok = $false
    foreach ($u in 'https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip', 'https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip') {
        try { Write-Host "Pobieram $u" ; Invoke-WebRequest -Uri $u -OutFile $zip -UseBasicParsing; $ok = $true; break } catch { Write-Host ("  nie udalo sie: " + $_.Exception.Message) -ForegroundColor Yellow }
    }
    if ($ok) { Remove-Item -Recurse -Force $vb -ErrorAction SilentlyContinue; Expand-Archive -LiteralPath $zip -DestinationPath $vb -Force; Remove-Item $zip -Force -ErrorAction SilentlyContinue }
}
if (Test-Path (Join-Path $vb 'VBCABLE_Setup_x64.exe')) { Copy-Item -Recurse -Force $vb 'build\audiobalance\VBCABLE'; Write-Host "VB-Cable w instalatorze: tak" -ForegroundColor Green } else { Write-Host "VB-Cable w instalatorze: nie (nie udalo sie pobrac) - instalator sprobuje pobrac go podczas instalacji" -ForegroundColor Yellow }

Write-Host "4/4 Instalator..." -ForegroundColor Cyan
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { Write-Host "Brak Inno Setup: winget install JRSoftware.InnoSetup" -ForegroundColor Red; Read-Host 'Enter'; exit 1 }
$outDir = Join-Path $repo 'build\instalator'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$old = Join-Path $outDir 'Velivo-AudioBalance-Setup-1.22.exe'
for ($i = 0; $i -lt 10 -and (Test-Path $old); $i++) { try { Remove-Item $old -Force -ErrorAction Stop } catch { Start-Sleep 2 } }
for ($i = 1; $i -le 6; $i++) { & $iscc "/O$outDir" audio-balance-windows\installer-audiobalance.iss; if ($LASTEXITCODE -eq 0) { break }; if ($i -lt 6) { Write-Host "Antywirus sprawdza plik - ponawiam ($i/6)..." -ForegroundColor Yellow; Start-Sleep ($i * 3) } }
if ($LASTEXITCODE -ne 0) { Write-Host "Budowa instalatora Velivo Audio Balance nie powiodla sie." -ForegroundColor Red; Read-Host 'Enter'; exit 1 }
Write-Host "Gotowe: build\instalator\Velivo-AudioBalance-Setup-1.22.exe" -ForegroundColor Green
Start-Process $outDir
