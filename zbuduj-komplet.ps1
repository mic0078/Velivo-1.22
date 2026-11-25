# 🧰 Wszystko naraz: programy Windows i pakiety w obu wersjach (1.23 - wspólny .NET, 1.22 - wbudowany .NET) oraz aplikacje Android.
# Użycie: PowerShell w folderze repozytorium ->  .\zbuduj-komplet.ps1            (Windows 1.23 + 1.22 + Android)
#                                              .\zbuduj-komplet.ps1 -BezAndroida
param([switch]$BezAndroida)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
Set-Location $repo
$galaz = (git rev-parse --abbrev-ref HEAD).Trim()
git pull origin $galaz
function Krok($opis, $blok) {
    Write-Host ""; Write-Host "=== $opis ===" -ForegroundColor Cyan
    & $blok
    if ($LASTEXITCODE -ne 0) { Write-Host "$opis - nie powiodlo sie. Reszta nie zostala zbudowana." -ForegroundColor Red; exit 1 }
}
# 1.23 najpierw; pakiet 1.22 buduje potem od nowa tylko swoje programy (Monitor, Kursory, Centrum, Procesor, Kopia - zawsze 1.23)
Krok "Wersja 1.23 (wspolny .NET): programy i pakiet" { & (Join-Path $repo 'zbuduj-pakiet.ps1') -WspolnyNet -Wszystko }
Krok "Wersja 1.22 (wbudowany .NET): programy i pakiet" { & (Join-Path $repo 'zbuduj-pakiet.ps1') -Wszystko }
if (-not $BezAndroida) { Krok "Aplikacje Android (przegladarka, odtwarzacz, klawiatura, komunikator)" { & (Join-Path $repo 'zbuduj-wszystko.ps1') -TylkoAndroid -Wszystko } }
Write-Host ""
Write-Host "Gotowe - build\instalator:" -ForegroundColor Green
Get-ChildItem (Join-Path $repo 'build\instalator') -Filter *.exe | Sort-Object Name | ForEach-Object { Write-Host ("  " + $_.Name) }
if (-not $BezAndroida) { Write-Host "Gotowe - build\android:" -ForegroundColor Green; Get-ChildItem (Join-Path $repo 'build\android') -Filter *.apk | ForEach-Object { Write-Host ("  " + $_.Name) } }
