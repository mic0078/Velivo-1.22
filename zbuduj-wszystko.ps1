# Velivo - budowa WSZYSTKIEGO na wlasnym komputerze: Windows (testy + instalator) i Android (3 wersje).
# Uzycie: PowerShell w folderze repozytorium ->  .\zbuduj-wszystko.ps1
#                                              .\zbuduj-wszystko.ps1 -TylkoAndroid
# Android: wersja "pelna" (APK dla rodziny) oraz "sklep" i "sklepyt" (AAB do Google Play), podpisane Twoim kluczem.
# Hasla do klucza wpisujesz przy kazdej budowie - nigdzie nie sa zapisywane; zapamietana jest tylko sciezka do pliku klucza.
# Buduje tylko to, co sie zmienilo od ostatniej udanej budowy (zmiana samej dokumentacji - nic):
#   Windows - zmiany w src\, testy\ albo zbuduj.ps1
#   Android - zmiany w android\ i we wspolnym Szybkim Dostepie (src\QuickAccessExtension\kod)
#   -Wszystko  - zbuduj obie wersje mimo braku zmian
param([switch]$TylkoAndroid, [switch]$Wszystko)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

git checkout -- Instalator 2>$null
git checkout rozwoj
git pull origin rozwoj
$head = (git rev-parse HEAD).Trim()
$cfgDir = Join-Path $env:LOCALAPPDATA 'Velivo-budowa'
New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
function Changed($name, $patterns) {
    if ($Wszystko) { return $true }
    $f = Join-Path $cfgDir "ostatnio-$name.txt"
    if (-not (Test-Path $f)) { return $true }
    $last = (Get-Content $f -Raw).Trim()
    $files = git diff --name-only $last $head 2>$null
    if ($LASTEXITCODE -ne 0) { return $true }   # nieznana zmiana (np. po przepisaniu historii) - budujemy
    return [bool]($files | Where-Object { $p = $_; $patterns | Where-Object { $p -like $_ } })
}
$needWin = (-not $TylkoAndroid) -and (Changed 'windows' @('src/*', 'testy/*', 'zbuduj.ps1'))
$needAnd = Changed 'android' @('android/*', 'src/QuickAccessExtension/kod/*')
if (-not $needWin -and -not $needAnd) { Write-Host "Brak zmian w programie od ostatniej budowy - nic do budowania (dokumentacja nie wymaga nowej wersji). Wymuszenie: .\zbuduj-wszystko.ps1 -Wszystko" -ForegroundColor Green; exit 0 }
Write-Host ("Do zbudowania: " + (@(if ($needWin) { 'Windows' }; if ($needAnd) { 'Android' }) -join ' + ')) -ForegroundColor Cyan

if ($needWin) {
    & (Join-Path $PSScriptRoot 'zbuduj.ps1')   # testy, Velivo na Windows, instalator
    if ($LASTEXITCODE -ne 0) { Write-Host "Windows nie zbudowane." -ForegroundColor Red; exit 1 }
    Set-Content (Join-Path $cfgDir 'ostatnio-windows.txt') $head
} else { Write-Host "Windows bez zmian - pomijam (instalator z poprzedniej budowy jest aktualny)." -ForegroundColor DarkGray }
if (-not $needAnd) { Write-Host "Android bez zmian - pomijam." -ForegroundColor DarkGray; exit 0 }

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
Set-Content (Join-Path $cfgDir 'ostatnio-android.txt') $head
Write-Host "Gotowe:" -ForegroundColor Green
if ($needWin) { Write-Host "  Instalator\Velivo-Setup-1.22.exe   - Windows" }
Write-Host "  build\android\Velivo-pelna.apk      - telefon (Ty i mama, WhatsApp)"
Write-Host "  build\android\Velivo-sklep.aab      - Google Play (ostrozna)"
Write-Host "  build\android\Velivo-sklepyt.aab    - Google Play (jak Brave)"
Start-Process $out
