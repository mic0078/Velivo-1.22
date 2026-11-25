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
# ⚡ RyzenAdj (moc procesorów AMD) - oficjalne wydanie z GitHub (FlyGoat/RyzenAdj), raz; bez niego program działa (bez sterowania mocą AMD)
$ra = Join-Path $repo 'build\dodatki\RyzenAdj'
# program (ryzenadj.exe) i biblioteka (libryzenadj.dll - silnik AMD czyta pomiary co sekundę bez uruchamiania programu) są w dwóch osobnych archiwach wydania
if (-not (Test-Path (Join-Path $ra 'ryzenadj.exe')) -or -not (Test-Path (Join-Path $ra 'libryzenadj.dll'))) {
    try {
        New-Item -ItemType Directory -Force -Path $ra | Out-Null
        $rel = Invoke-RestMethod 'https://api.github.com/repos/FlyGoat/RyzenAdj/releases/latest' -Headers @{ 'User-Agent' = 'Velivo' }
        foreach ($n in 'ryzenadj-win64.zip', 'libryzenadj-win64.zip') {
            $asset = $rel.assets | Where-Object { $_.name -eq $n } | Select-Object -First 1
            if (-not $asset) { continue }
            $zip = Join-Path $ra $n; Invoke-WebRequest $asset.browser_download_url -OutFile $zip -UseBasicParsing
            Expand-Archive $zip -DestinationPath $ra -Force; Remove-Item $zip -Force
        }
        $exe = Get-ChildItem $ra -Recurse -Filter ryzenadj.exe | Select-Object -First 1
        if ($exe -and $exe.DirectoryName -ne $ra) { Get-ChildItem $exe.DirectoryName | Move-Item -Destination $ra -Force }
        Write-Host ("RyzenAdj " + $rel.tag_name + " pobrany") -ForegroundColor Green
    } catch { Write-Host "RyzenAdj nie pobrany ($_) - program zbuduje sie bez sterowania moca AMD" -ForegroundColor Yellow }
}
if (Test-Path (Join-Path $ra 'ryzenadj.exe')) { Copy-Item -Recurse -Force $ra 'build\procesor\RyzenAdj' }
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
