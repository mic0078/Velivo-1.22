# Velivo - budowa WSZYSTKIEGO na wlasnym komputerze: Windows (testy + instalator) i Android (3 wersje).
# Uzycie: PowerShell w folderze repozytorium ->  .\zbuduj-wszystko.ps1
#                                              .\zbuduj-wszystko.ps1 -TylkoAndroid
# Android: wersja "pelna" (APK dla rodziny) oraz "sklep" i "sklepyt" (AAB do Google Play), podpisane Twoim kluczem.
# Hasla do klucza wpisujesz przy kazdej budowie - nigdzie nie sa zapisywane; zapamietana jest tylko sciezka do pliku klucza.
param([switch]$TylkoAndroid)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $TylkoAndroid) {
    & (Join-Path $PSScriptRoot 'zbuduj.ps1')   # git pull, testy, Velivo na Windows, instalator
    if ($LASTEXITCODE -ne 0) { Write-Host "Windows nie zbudowane - Android pomijam." -ForegroundColor Red; exit 1 }
} else {
    git checkout rozwoj
    git pull origin rozwoj
}

Write-Host "Android: 3 wersje..." -ForegroundColor Cyan
# Java z Android Studio (bez osobnej instalacji)
if (-not $env:JAVA_HOME -or -not (Test-Path $env:JAVA_HOME)) {
    $jbr = @("$env:ProgramFiles\Android\Android Studio\jbr", "$env:LOCALAPPDATA\Programs\Android Studio\jbr", "${env:ProgramFiles(x86)}\Android\Android Studio\jbr") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $jbr) { Write-Host "Nie znaleziono Javy z Android Studio (folder jbr). Zainstaluj Android Studio." -ForegroundColor Red; exit 1 }
    $env:JAVA_HOME = $jbr
}
# SDK Androida: local.properties tworzy Android Studio przy pierwszym otwarciu projektu
$lp = Join-Path $PSScriptRoot 'android\local.properties'
if (-not (Test-Path $lp)) {
    $sdk = "$env:LOCALAPPDATA\Android\Sdk"
    if (-not (Test-Path $sdk)) { Write-Host "Brak Android SDK - otworz raz folder android w Android Studio." -ForegroundColor Red; exit 1 }
    "sdk.dir=" + ($sdk -replace '\\', '\\') | Set-Content $lp -Encoding ASCII
}

# Klucz podpisu: sciezka zapamietana poza repozytorium, hasla tylko w pamieci na czas budowy
$cfgDir = Join-Path $env:LOCALAPPDATA 'Velivo-budowa'
New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
$cfg = Join-Path $cfgDir 'klucz.txt'
$ks = if (Test-Path $cfg) { (Get-Content $cfg -Raw).Trim() } else { '' }
if (-not $ks -or -not (Test-Path $ks)) {
    $ks = (Read-Host "Pelna sciezka do pliku klucza (.jks), np. C:\Users\andro\Velivo-klucz.jks").Trim('"', ' ')
    if (-not (Test-Path $ks)) { Write-Host "Nie ma takiego pliku: $ks" -ForegroundColor Red; exit 1 }
    Set-Content $cfg $ks
}
$alias = Read-Host "Alias klucza (Enter = velivo)"
if (-not $alias) { $alias = 'velivo' }
function Plain($s) { [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)) }
$ksPass = Plain (Read-Host "Haslo do pliku klucza" -AsSecureString)
$keyPass = Plain (Read-Host "Haslo klucza (Enter = to samo)" -AsSecureString)
if (-not $keyPass) { $keyPass = $ksPass }

Push-Location (Join-Path $PSScriptRoot 'android')
try {
    # hasla przez zmienne srodowiska tylko tego procesu (nie widac ich na liscie procesow); po budowie usuwane
    ${env:ORG_GRADLE_PROJECT_android.injected.signing.store.file} = $ks
    ${env:ORG_GRADLE_PROJECT_android.injected.signing.store.password} = $ksPass
    ${env:ORG_GRADLE_PROJECT_android.injected.signing.key.alias} = $alias
    ${env:ORG_GRADLE_PROJECT_android.injected.signing.key.password} = $keyPass
    & .\gradlew.bat --no-daemon assemblePelnaRelease bundleSklepRelease bundleSklepytRelease
    if ($LASTEXITCODE -ne 0) { Write-Host "Budowa Androida nie powiodla sie (zle haslo? brak SDK?)." -ForegroundColor Red; exit 1 }
} finally {
    Pop-Location; $ksPass = $null; $keyPass = $null
    foreach ($n in 'store.file', 'store.password', 'key.alias', 'key.password') { Remove-Item "env:ORG_GRADLE_PROJECT_android.injected.signing.$n" -ErrorAction SilentlyContinue }
}

$out = Join-Path $PSScriptRoot 'build\android'
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $out | Out-Null
$o = Join-Path $PSScriptRoot 'android\app\build\outputs'
Copy-Item "$o\apk\pelna\release\*.apk" (Join-Path $out 'Velivo-pelna.apk')
Copy-Item "$o\bundle\sklepRelease\*.aab" (Join-Path $out 'Velivo-sklep.aab')
Copy-Item "$o\bundle\sklepytRelease\*.aab" (Join-Path $out 'Velivo-sklepyt.aab')
Write-Host "Gotowe:" -ForegroundColor Green
if (-not $TylkoAndroid) { Write-Host "  Instalator\Velivo-Setup-1.22.exe   - Windows" }
Write-Host "  build\android\Velivo-pelna.apk      - telefon (Ty i mama, WhatsApp)"
Write-Host "  build\android\Velivo-sklep.aab      - Google Play (ostrozna)"
Write-Host "  build\android\Velivo-sklepyt.aab    - Google Play (jak Brave)"
Start-Process $out
