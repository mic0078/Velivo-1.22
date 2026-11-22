# 📦 Velivo – pakiet: jeden instalator z wyborem programów (Pełny / Typowy / Własny).
# Użycie: PowerShell w folderze repozytorium ->  .\zbuduj-pakiet.ps1          (brakujące instalatory programów buduje sam)
#                                              .\zbuduj-pakiet.ps1 -Wszystko (wszystkie programy od nowa, potem pakiet)
param([switch]$Wszystko)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
Set-Location $repo
$out = Join-Path $repo 'build\instalator'
function Jest($n) { return Test-Path (Join-Path $out "$n-Setup-1.22.exe") }
$velivo = @('Velivo', 'Velivo-Odtwarzacz', 'Velivo-Przegladarka', 'Velivo-Klawiatura', 'Velivo-Serwer', 'Velivo-Komunikator')
if ($Wszystko -or ($velivo | Where-Object { -not (Jest $_) })) {
    Write-Host "Programy Velivo (Velivo, Odtwarzacz, Przegladarka, Klawiatura, Serwer, Komunikator)..." -ForegroundColor Cyan
    & (Join-Path $repo 'zbuduj.ps1')
    if ($LASTEXITCODE -ne 0) { Write-Host "Budowa programow Velivo nie powiodla sie - pakiet nie powstal." -ForegroundColor Red; exit 1 }
}
foreach ($p in @(@('Velivo-AudioBalance-for-Windows', 'zbuduj-audiobalance.ps1'), @('Velivo-Monitor', 'zbuduj-monitor.ps1'), @('Velivo-Kursory', 'zbuduj-kursory.ps1'), @('Velivo-Centrum', 'zbuduj-centrum.ps1'))) {
    if ($Wszystko -or -not (Jest $p[0])) {
        Write-Host ("Buduje " + $p[0] + "...") -ForegroundColor Cyan
        & (Join-Path $repo $p[1])
        if (-not (Jest $p[0])) { Write-Host ("Brak " + $p[0] + "-Setup-1.22.exe - pakiet nie powstal.") -ForegroundColor Red; exit 1 }
    }
}
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { Write-Host "Brak Inno Setup: winget install JRSoftware.InnoSetup" -ForegroundColor Red; exit 1 }
Write-Host "Pakiet..." -ForegroundColor Cyan
for ($i = 1; $i -le 6; $i++) { & $iscc "/O$out" pakiet\installer-pakiet.iss; if ($LASTEXITCODE -eq 0) { break }; if ($i -lt 6) { Write-Host "Antywirus sprawdza plik - ponawiam ($i/6)..." -ForegroundColor Yellow; Start-Sleep (5 * $i) } }
if ($LASTEXITCODE -ne 0) { Write-Host "Budowa pakietu nie powiodla sie." -ForegroundColor Red; exit 1 }
Write-Host "Gotowe: build\instalator\Velivo-Pakiet-Setup-1.22.exe (Pelny / Typowy / Wlasny)" -ForegroundColor Green
Start-Process $out
