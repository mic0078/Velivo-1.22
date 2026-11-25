# 📦 Velivo – pakiet: jeden instalator z wyborem programów (Pełny / Typowy / Własny).
# Użycie: PowerShell w folderze repozytorium ->  .\zbuduj-pakiet.ps1          (brakujące instalatory programów buduje sam)
#                                              .\zbuduj-pakiet.ps1 -Wszystko (wszystkie programy od nowa, potem pakiet)
param([switch]$Wszystko, [switch]$WspolnyNet)   # -WspolnyNet: pakiet 1.23 - programy na wspolnym .NET 10 (male)
$wer = if ($WspolnyNet) { '1.23' } else { '1.22' }
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
Set-Location $repo
$out = Join-Path $repo 'build\instalator'
function Jest($n, $v = $wer) { return Test-Path (Join-Path $out "$n-Setup-$v.exe") }
$velivo = @('Velivo', 'Velivo-Odtwarzacz', 'Velivo-Przegladarka', 'Velivo-Klawiatura', 'Velivo-Serwer', 'Velivo-Komunikator')
if ($Wszystko -or ($velivo | Where-Object { -not (Jest $_) })) {
    Write-Host "Programy Velivo (Velivo, Odtwarzacz, Przegladarka, Klawiatura, Serwer, Komunikator)..." -ForegroundColor Cyan
    & (Join-Path $repo 'zbuduj.ps1') -WspolnyNet:$WspolnyNet
    if ($LASTEXITCODE -ne 0) { Write-Host "Budowa programow Velivo nie powiodla sie - pakiet nie powstal." -ForegroundColor Red; exit 1 }
}
if ($Wszystko -or -not (Jest 'Velivo-AudioBalance-for-Windows')) {
    Write-Host "Buduje Audio Balance..." -ForegroundColor Cyan
    & (Join-Path $repo 'zbuduj-audiobalance.ps1') -WspolnyNet:$WspolnyNet
    if (-not (Jest 'Velivo-AudioBalance-for-Windows')) { Write-Host "Brak instalatora Audio Balance - pakiet nie powstal." -ForegroundColor Red; exit 1 }
}
foreach ($p in @(@('Velivo-Monitor', 'zbuduj-monitor.ps1'), @('Velivo-Kursory', 'zbuduj-kursory.ps1'), @('Velivo-Centrum', 'zbuduj-centrum.ps1'), @('Velivo-Procesor', 'zbuduj-procesor.ps1')))) {   # .NET Framework z Windows - zawsze 1.22 (Monitor, Kursory, Centrum, Procesor)
    if ($Wszystko -or -not (Jest $p[0] '1.22')) {
        Write-Host ("Buduje " + $p[0] + "...") -ForegroundColor Cyan
        & (Join-Path $repo $p[1])
        if (-not (Jest $p[0] '1.22')) { Write-Host ("Brak " + $p[0] + "-Setup-1.22.exe - pakiet nie powstal.") -ForegroundColor Red; exit 1 }
    }
}
# 💾 Velivo Kopia - wersja 1.23 (właściciel 2026-11-25: nowe zmiany tylko w 1.23), ten sam instalator w obu pakietach
if ($Wszystko -or -not (Jest 'Velivo-Kopia' '1.23')) {
    Write-Host "Buduje Velivo-Kopia..." -ForegroundColor Cyan
    & (Join-Path $repo 'zbuduj-kopia.ps1')
    if (-not (Jest 'Velivo-Kopia' '1.23')) { Write-Host "Brak Velivo-Kopia-Setup-1.23.exe - pakiet nie powstal." -ForegroundColor Red; exit 1 }
}
# 🎞 FFmpeg do pakietu (raz): ten sam plik co pobierany przez Velivo, sprawdzony sumą SHA-256 z listy wydawcy
$ffDir = Join-Path $repo 'build\dodatki'; $ff = Join-Path $ffDir 'ffmpeg.exe'
if (-not (Test-Path $ff)) {
    Write-Host "Pobieram FFmpeg (kodery i dekodery, ok. 140 MB)..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $ffDir | Out-Null
    $zip = Join-Path $ffDir 'ffmpeg.zip'
    Invoke-WebRequest 'https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip' -OutFile $zip -UseBasicParsing
    $sumF = Join-Path $ffDir 'checksums.sha256'
    Invoke-WebRequest 'https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/checksums.sha256' -OutFile $sumF -UseBasicParsing   # do pliku: Windows PowerShell 5.1 daje .Content jako bajty (porównanie zawsze „nie zgadza się”)
    $line = Get-Content $sumF | Where-Object { $_ -match '[\s\*]ffmpeg-master-latest-win64-gpl\.zip\s*$' } | Select-Object -First 1
    Remove-Item $sumF -Force
    $want = if ($line) { ($line.Trim() -split '\s+')[0] } else { $null }
    $have = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
    if (-not $want -or $have -ne $want.ToLower()) { Remove-Item $zip -Force; Write-Host ("FFmpeg: suma kontrolna sie nie zgadza (oczekiwana: " + $want + ", pobrany plik: " + $have + ") - pakiet nie powstal. Uruchom ponownie (wydawca mogl wlasnie podmienic plik).") -ForegroundColor Red; exit 1 }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $a = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try { $e = $a.Entries | Where-Object { $_.FullName -like '*/bin/ffmpeg.exe' } | Select-Object -First 1; [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e, $ff, $true) } finally { $a.Dispose() }
    Remove-Item $zip -Force
}
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { Write-Host "Brak Inno Setup: winget install JRSoftware.InnoSetup" -ForegroundColor Red; exit 1 }
Write-Host "Pakiet..." -ForegroundColor Cyan
for ($i = 1; $i -le 6; $i++) { & $iscc "/DAppVer=$wer" "/O$out" pakiet\installer-pakiet.iss; if ($LASTEXITCODE -eq 0) { break }; if ($i -lt 6) { Write-Host "Antywirus sprawdza plik - ponawiam ($i/6)..." -ForegroundColor Yellow; Start-Sleep (5 * $i) } }
if ($LASTEXITCODE -ne 0) { Write-Host "Budowa pakietu nie powiodla sie." -ForegroundColor Red; exit 1 }
Write-Host "Gotowe: build\instalator\Velivo-Pakiet-Setup-$wer.exe (Pelny / Typowy / Wlasny)" -ForegroundColor Green
Start-Process $out
