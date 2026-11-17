# Velivo Monitor - budowa osobno (własny program, własny instalator).
# Użycie: PowerShell w folderze repozytorium ->  .\zbuduj-monitor.ps1
$ErrorActionPreference = 'Stop'
trap { Write-Host "Blad: $_" -ForegroundColor Red; Read-Host 'Nacisnij Enter, aby zamknac'; exit 1 }
$repo = $PSScriptRoot
Set-Location $repo
Write-Host "1/3 Najnowsze zmiany (git pull)..." -ForegroundColor Cyan
$galaz = (git rev-parse --abbrev-ref HEAD).Trim()
git pull origin $galaz
Write-Host "2/3 Kompilacja Velivo Monitor..." -ForegroundColor Cyan
Remove-Item -Recurse -Force build\monitor -ErrorAction SilentlyContinue
dotnet build velivo-monitor\VelivoMonitor.csproj -c Release -o build\monitor   # .NET Framework 4.8 (jest w Windows) - bez dołączania .NET, program ma kilkadziesiąt KB
if ($LASTEXITCODE -ne 0) { Write-Host "Kompilacja Velivo Monitor nie powiodla sie." -ForegroundColor Red; Read-Host 'Enter'; exit 1 }
Write-Host "3/3 Instalator..." -ForegroundColor Cyan
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { Write-Host "Brak Inno Setup: winget install JRSoftware.InnoSetup" -ForegroundColor Red; Read-Host 'Enter'; exit 1 }
$outDir = Join-Path $repo 'build\instalator'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
for ($i = 1; $i -le 6; $i++) { & $iscc "/O$outDir" velivo-monitor\installer-monitor.iss; if ($LASTEXITCODE -eq 0) { break }; if ($i -lt 6) { Write-Host "Antywirus sprawdza plik - ponawiam ($i/6)..." -ForegroundColor Yellow; Start-Sleep ($i * 3) } }
if ($LASTEXITCODE -ne 0) { Write-Host "Budowa instalatora Velivo Monitor nie powiodla sie." -ForegroundColor Red; Read-Host 'Enter'; exit 1 }
Write-Host "Gotowe: build\instalator\Velivo-Monitor-Setup-1.22.exe" -ForegroundColor Green
Start-Process $outDir
