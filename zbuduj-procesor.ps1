# Velivo Procesor - budowa osobno (własny program, własny instalator).
# Użycie: PowerShell w folderze repozytorium ->  .\zbuduj-procesor.ps1
$ErrorActionPreference = 'Stop'
trap { Write-Host "Blad: $_" -ForegroundColor Red; Read-Host 'Nacisnij Enter, aby zamknac'; exit 1 }
$repo = $PSScriptRoot
Set-Location $repo
Write-Host "1/3 Najnowsze zmiany (git pull)..." -ForegroundColor Cyan
$galaz = (git rev-parse --abbrev-ref HEAD).Trim()
git pull origin $galaz
Write-Host "2/3 Kompilacja Velivo Procesor..." -ForegroundColor Cyan
Remove-Item -Recurse -Force build\procesor -ErrorAction SilentlyContinue
dotnet build velivo-procesor\VelivoProcesor.csproj -c Release -o build\procesor   # .NET Framework 4.8 (jest w Windows) - bez dołączania .NET, program ma kilkadziesiąt KB
if ($LASTEXITCODE -ne 0) { Write-Host "Kompilacja Velivo Procesor nie powiodla sie." -ForegroundColor Red; Read-Host 'Enter'; exit 1 }
# ⚡ PawnIO (moc procesorów AMD: podpisany sterownik zamiast WinRing0, który Defender blokuje) - oficjalne wydania z GitHub (namazso), przypięte wersje i sumy SHA-256; bez nich program działa (bez sterowania mocą AMD)
$pw = Join-Path $repo 'build\dodatki\PawnIO'
if (-not ((Test-Path (Join-Path $pw 'PawnIO_setup.exe')) -and (Test-Path (Join-Path $pw 'RyzenSMU.bin')))) {
    try {
        New-Item -ItemType Directory -Force -Path $pw | Out-Null
        $setup = Join-Path $pw 'PawnIO_setup.exe'
        Invoke-WebRequest 'https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe' -OutFile $setup -UseBasicParsing
        if ((Get-FileHash $setup -Algorithm SHA256).Hash -ne '1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032') { Remove-Item $setup -Force; throw 'PawnIO_setup.exe: zla suma SHA-256' }
        $zip = Join-Path $pw 'moduly.zip'
        Invoke-WebRequest 'https://github.com/namazso/PawnIO.Modules/releases/download/0.2.12/release_0_2_12.zip' -OutFile $zip -UseBasicParsing
        if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne '09698897CE19B92F1043A2E322CFCF0B1FE823E0B576971B178A65244FB0AF9D') { Remove-Item $zip -Force; throw 'moduly PawnIO: zla suma SHA-256' }
        $tmp = Join-Path $pw 'moduly'; Expand-Archive $zip -DestinationPath $tmp -Force
        $bin = Get-ChildItem $tmp -Recurse -Filter 'RyzenSMU.bin' | Select-Object -First 1
        if (-not $bin) { throw 'w archiwum modulow PawnIO nie ma RyzenSMU.bin' }
        Copy-Item $bin.FullName (Join-Path $pw 'RyzenSMU.bin') -Force
        Remove-Item $zip, $tmp -Recurse -Force
        Write-Host "PawnIO 2.2.0 + RyzenSMU 0.2.12 pobrane" -ForegroundColor Green
    } catch { Write-Host "PawnIO nie pobrany ($_) - program zbuduje sie bez sterowania moca AMD" -ForegroundColor Yellow }
}
if ((Test-Path (Join-Path $pw 'PawnIO_setup.exe')) -and (Test-Path (Join-Path $pw 'RyzenSMU.bin'))) { Copy-Item -Recurse -Force $pw 'build\procesor\PawnIO' }
Write-Host "3/3 Instalator..." -ForegroundColor Cyan
# otwarty instalator z poprzedniej próby trzyma plik - budowa nie może go nadpisać (to nie antywirus)
while (Get-Process -Name "Velivo-Procesor-Setup-1.22", "Velivo-Procesor-Setup-1.22.tmp" -ErrorAction SilentlyContinue) { Write-Host "Otwarte jest okno instalatora Velivo-Procesor-Setup-1.22 z poprzedniej proby - zamknij je (Anuluj / Przerwij instalacje), potem nacisnij Enter." -ForegroundColor Yellow; Read-Host | Out-Null }
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { Write-Host "Brak Inno Setup: winget install JRSoftware.InnoSetup" -ForegroundColor Red; Read-Host 'Enter'; exit 1 }
$outDir = Join-Path $repo 'build\instalator'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
for ($i = 1; $i -le 6; $i++) { & $iscc "/O$outDir" velivo-procesor\installer-procesor.iss; if ($LASTEXITCODE -eq 0) { break }; if ($i -lt 6) { Write-Host "Antywirus sprawdza plik - ponawiam ($i/6)..." -ForegroundColor Yellow; Start-Sleep ($i * 3) } }
if ($LASTEXITCODE -ne 0) { Write-Host "Budowa instalatora Velivo Procesor nie powiodla sie." -ForegroundColor Red; Read-Host 'Enter'; exit 1 }
Write-Host "Gotowe: build\instalator\Velivo-Procesor-Setup-1.22.exe" -ForegroundColor Green
Start-Process $outDir
