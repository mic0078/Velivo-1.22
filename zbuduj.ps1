# Velivo - budowa instalatora na wlasnym komputerze (bez GitHub Actions).
# Uzycie: PowerShell w folderze repozytorium ->  .\zbuduj.ps1        (z testami)
#                                              .\zbuduj.ps1 -BezTestow
# Wymagane raz: winget install Git.Git Microsoft.DotNet.SDK.10 JRSoftware.InnoSetup
param([switch]$BezTestow)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host "1/4 Najnowsze zmiany (git pull)..." -ForegroundColor Cyan
git checkout -- Instalator 2>$null   # poprzednio zbudowany instalator (plik z repozytorium) nie blokuje pobrania zmian
git pull

if (-not $BezTestow) {
    Write-Host "2/4 Testy..." -ForegroundColor Cyan
    dotnet test testy\Velivo.Tests\Velivo.Tests.csproj -c Release --filter "Category!=Vlc"
    if ($LASTEXITCODE -ne 0) { Write-Host "Testy nie przeszly - instalator NIE zostal zbudowany." -ForegroundColor Red; exit 1 }
} else { Write-Host "2/4 Testy pominiete" -ForegroundColor Yellow }

Write-Host "3/4 Kompilacja programu..." -ForegroundColor Cyan
Remove-Item -Recurse -Force build\velivo -ErrorAction SilentlyContinue
dotnet publish src\Przegladarka.csproj -c Release -r win-x64 --self-contained true -o build\velivo
if ($LASTEXITCODE -ne 0) { Write-Host "Kompilacja nie powiodla sie." -ForegroundColor Red; exit 1 }

Write-Host "4/4 Instalator..." -ForegroundColor Cyan
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { Write-Host "Brak Inno Setup: winget install JRSoftware.InnoSetup" -ForegroundColor Red; exit 1 }
& $iscc /DPelny src\installer.iss
if ($LASTEXITCODE -ne 0) { Write-Host "Budowa instalatora nie powiodla sie." -ForegroundColor Red; exit 1 }

Write-Host "Gotowe: Instalator\Velivo-Setup-1.22.exe (zamknij Velivo calkowicie przed instalacja)" -ForegroundColor Green
Start-Process (Join-Path $PSScriptRoot 'Instalator')
