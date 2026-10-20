# Velivo - budowa instalatora na wlasnym komputerze (bez GitHub Actions).
# Uzycie: PowerShell w folderze repozytorium ->  .\zbuduj.ps1        (z testami)
#                                              .\zbuduj.ps1 -BezTestow
# Wymagane raz: winget install Git.Git Microsoft.DotNet.SDK.10 JRSoftware.InnoSetup
param([switch]$BezTestow)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host "1/4 Najnowsze zmiany (git pull)..." -ForegroundColor Cyan
git checkout rozwoj   # instalator testowy zawsze z galezi roboczej (main = wersja publiczna)
git pull origin rozwoj
if ($LASTEXITCODE -ne 0) { Write-Host "Nie udalo sie pobrac zmian z galezi rozwoj." -ForegroundColor Red; exit 1 }
Write-Host ("Budowana wersja: " + (git log --oneline -1)) -ForegroundColor Cyan

if (-not $BezTestow) {
    Write-Host "2/4 Testy..." -ForegroundColor Cyan
    dotnet test testy\Velivo.Tests\Velivo.Tests.csproj -c Release --filter "Category!=Vlc"
    if ($LASTEXITCODE -ne 0) { Write-Host "Testy nie przeszly - instalator NIE zostal zbudowany." -ForegroundColor Red; exit 1 }
} else { Write-Host "2/4 Testy pominiete" -ForegroundColor Yellow }

Write-Host "3/4 Kompilacja programu..." -ForegroundColor Cyan
dotnet build-server shutdown 2>$null | Out-Null   # zwalnia pliki trzymane przez poprzednia kompilacje / testy
Remove-Item -Recurse -Force src\bin\Release\net10.0-windows10.0.19041.0\win-x64 -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force build\velivo -ErrorAction SilentlyContinue
dotnet publish src\Velivo.csproj -c Release -r win-x64 --self-contained true -o build\velivo
if ($LASTEXITCODE -ne 0) { Write-Host "Kompilacja nie powiodla sie." -ForegroundColor Red; exit 1 }

Write-Host "4/4 Instalator..." -ForegroundColor Cyan
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { Write-Host "Brak Inno Setup: winget install JRSoftware.InnoSetup" -ForegroundColor Red; exit 1 }
# instalator budowany lokalnie trafia do build\instalator (poza repozytorium) - nigdy nie miesza sie
# ze starym plikiem Instalator\Velivo-Setup-1.22.exe z repozytorium (strona pobierania)
$outDir = Join-Path $PSScriptRoot 'build\instalator'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$old = Join-Path $outDir 'Velivo-Setup-1.22.exe'
for ($i = 0; $i -lt 10 -and (Test-Path $old); $i++) { try { Remove-Item $old -Force -ErrorAction Stop } catch { Start-Sleep 2 } }   # stary instalator (otwarty / sprawdzany przez antywirus) - kilka prob
if (Test-Path $old) { Write-Host "Stary instalator jest w uzyciu - zamknij okno instalacji Velivo i uruchom skrypt ponownie." -ForegroundColor Red; exit 1 }
# antywirus skanuje nowy plik w chwili wstawiania ikony (EndUpdateResource failed) - do 3 prob z przerwa
for ($i = 1; $i -le 3; $i++) { & $iscc /DPelny "/O$outDir" src\installer.iss; if ($LASTEXITCODE -eq 0) { break }; if ($i -lt 3) { Write-Host "Ponawiam budowe instalatora ($i/3)..." -ForegroundColor Yellow; Start-Sleep 5 } }
if ($LASTEXITCODE -ne 0) { Write-Host "Budowa instalatora nie powiodla sie." -ForegroundColor Red; exit 1 }

Write-Host "Gotowe: build\instalator\Velivo-Setup-1.22.exe (zamknij Velivo calkowicie przed instalacja)" -ForegroundColor Green
Start-Process $outDir
