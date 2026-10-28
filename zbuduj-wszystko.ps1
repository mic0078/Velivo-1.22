# Velivo - budowa WSZYSTKIEGO na wlasnym komputerze: Windows (testy + instalator) i Android (3 wersje).
# Uzycie: PowerShell w folderze repozytorium ->  .\zbuduj-wszystko.ps1
#                                              .\zbuduj-wszystko.ps1 -TylkoAndroid
# Android: wersja "pelna" (APK dla rodziny) oraz "sklep" i "sklepyt" (AAB do Google Play), podpisane Twoim kluczem.
# Hasla do klucza wpisujesz przy kazdej budowie - nigdzie nie sa zapisywane; zapamietana jest tylko sciezka do pliku klucza.
# Buduje tylko to, co sie zmienilo od ostatniej udanej budowy (zmiana samej dokumentacji - nic):
#   Windows - zmiany w src\, testy\ albo zbuduj.ps1
#   Android - zmiany w android\ i we wspolnym Szybkim Dostepie (src\QuickAccessExtension\kod)
#   -Wszystko  - zbuduj obie wersje mimo braku zmian
param([switch]$TylkoAndroid, [switch]$Wszystko, [switch]$Sklep, [switch]$Przegladarka, [switch]$Klawiatura, [switch]$Komunikator, [switch]$TylkoWindows)   # -Sklep - takze 2 wersje AAB do Google Play; -Przegladarka / -Klawiatura / -Komunikator - tylko te APK
$tylkoApk = $Przegladarka -or $Klawiatura -or $Komunikator
if ($tylkoApk) { $Wszystko = $true; if (-not $TylkoWindows) { $TylkoAndroid = $true } }   # -TylkoWindows -Komunikator = Windows + ten APK
$ErrorActionPreference = 'Stop'
trap { Write-Host "Blad: $_" -ForegroundColor Red; Read-Host 'Nacisnij Enter, aby zamknac'; exit 1 }
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Force build | Out-Null
try { Stop-Transcript | Out-Null } catch { }   # poprzednia budowa w tym samym oknie mogla zostawic zapis otwarty
try { Start-Transcript -Path (Join-Path $PSScriptRoot 'build\budowa.log') -Force | Out-Null } catch { }   # gdyby okno sie zamknelo - przebieg zostaje w build\budowa.log

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
$needWin = (-not $TylkoAndroid) -and (Changed 'windows' @('src/*', 'testy/*', 'zbuduj.ps1', 'komunikator-windows/*'))
$needAnd = ((-not $TylkoWindows) -or $tylkoApk) -and (Changed 'android' @('android/*', 'src/QuickAccessExtension/kod/*', 'src/Rozmowa/*'))
if (-not $needWin -and -not $needAnd) { Write-Host "Brak zmian w programie od ostatniej budowy - nic do budowania (dokumentacja nie wymaga nowej wersji). Wymuszenie: .\zbuduj-wszystko.ps1 -Wszystko" -ForegroundColor Green; exit 0 }
Write-Host ("Do zbudowania: " + (@(if ($needWin) { 'Windows' }; if ($needAnd) { 'Android' }) -join ' + ')) -ForegroundColor Cyan

if ($needWin) {
    & (Join-Path $PSScriptRoot 'zbuduj.ps1')   # testy, Velivo na Windows, instalator
    if ($LASTEXITCODE -ne 0) { Write-Host "Windows nie zbudowane." -ForegroundColor Red; exit 1 }
    Set-Content (Join-Path $cfgDir 'ostatnio-windows.txt') $head
} else { Write-Host "Windows bez zmian - pomijam (instalator z poprzedniej budowy jest aktualny)." -ForegroundColor DarkGray }
if (-not $needAnd) { Write-Host "Android bez zmian - pomijam." -ForegroundColor DarkGray; exit 0 }

Write-Host ($(if ($Sklep) { "Android: 3 wersje..." } else { "Android: wersja pelna (APK). Wersje do sklepu: -Sklep" })) -ForegroundColor Cyan
# Java dla Gradle: wersja 17-24 (Gradle 8.x nie dziala na Javie 25+). Szukamy: VELIVO_JAVA, jbr Android Studio, JAVA_HOME, zainstalowane JDK.
$roots = @("$env:ProgramFiles\Android", "$env:LOCALAPPDATA\Programs", "${env:ProgramFiles(x86)}\Android", "$env:LOCALAPPDATA\JetBrains\Toolbox\apps", "$env:ProgramFiles")
$cands = @($env:VELIVO_JAVA)
$cands += $roots | Where-Object { $_ -and (Test-Path $_) } | ForEach-Object { Get-ChildItem $_ -Directory -Filter 'Android Studio*' -ErrorAction SilentlyContinue } |
    ForEach-Object { Get-ChildItem $_.FullName -Directory -Recurse -Depth 3 -Filter 'jbr' -ErrorAction SilentlyContinue } | Select-Object -ExpandProperty FullName
$cands += $env:JAVA_HOME
$cands += @("$env:ProgramFiles\Java", "$env:ProgramFiles\Eclipse Adoptium", "$env:ProgramFiles\Microsoft", "$env:ProgramFiles\Zulu", "$env:ProgramFiles\Amazon Corretto", "$env:USERPROFILE\.jdks") |
    Where-Object { Test-Path $_ } | ForEach-Object { Get-ChildItem $_ -Directory -ErrorAction SilentlyContinue } | Select-Object -ExpandProperty FullName
$cands += Get-ChildItem 'HKLM:\SOFTWARE\JavaSoft\JDK', 'HKLM:\SOFTWARE\Eclipse Adoptium\JDK', 'HKLM:\SOFTWARE\Microsoft\JDK' -Recurse -ErrorAction SilentlyContinue |
    ForEach-Object { $_.GetValue('JavaHome'); $_.GetValue('Path') }
$cands += (Get-Command java.exe -All -ErrorAction SilentlyContinue | ForEach-Object { Split-Path (Split-Path $_.Source) })
$java = $null; $jv = ''
foreach ($c in ($cands | Where-Object { $_ -and (Test-Path (Join-Path $_ 'bin\java.exe')) } | Select-Object -Unique)) {
    $ErrorActionPreference = 'Continue'   # java -version pisze na stderr - przy 'Stop' PowerShell 5 przerywa skrypt
    $v = (& (Join-Path $c 'bin\java.exe') -version 2>&1 | Select-Object -First 1 | ForEach-Object { "$_" }) -replace '.*"(.*)".*', '$1'
    $ErrorActionPreference = 'Stop'
    $m = [int](($v -split '[.+-]')[0])
    if ($m -ge 17 -and $m -le 24) { $java = $c; $jv = $v; break }
    Write-Host "Pomijam Jave $v ($c) - Gradle potrzebuje 17-24." -ForegroundColor DarkGray
}
if (-not $java) { Write-Host "Brak Javy 17-24. Zainstaluj JDK 21 (np. winget install Microsoft.OpenJDK.21) albo podaj: `$env:VELIVO_JAVA='sciezka'" -ForegroundColor Red; exit 1 }
$env:JAVA_HOME = $java
Write-Host "Java do budowy: $java ($jv)" -ForegroundColor DarkGray
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
if (-not $ks -or -not (Test-Path $ks)) { $def = Join-Path $env:USERPROFILE 'Documents\velivo-klucz.jks'; if (Test-Path $def) { $ks = $def; Set-Content $cfg $ks } }   # domyslnie: Dokumenty\velivo-klucz.jks
if (-not $ks -or -not (Test-Path $ks)) {
    $ks = (Read-Host "Pelna sciezka do pliku klucza (.jks), np. C:\Users\andro\Velivo-klucz.jks").Trim('"', ' ')
    if (-not (Test-Path $ks)) { Write-Host "Nie ma takiego pliku: $ks" -ForegroundColor Red; exit 1 }
    Set-Content $cfg $ks
}
$alias = Read-Host "Alias klucza (Enter = velivo)"
if (-not $alias) { $alias = 'velivo' }
function Plain($s) { [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)) }
# haslo sprawdzane od razu (keytool z Javy Android Studio) - pomylka: ponowne wpisanie zamiast bledu po calej budowie
$keytool = @("$env:JAVA_HOME\bin\keytool.exe", "$env:ProgramFiles\Android\Android Studio\jbr\bin\keytool.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
for ($i = 1; $i -le 3; $i++) {
    $ksPass = Plain (Read-Host "Haslo do pliku klucza" -AsSecureString)
    if (-not $keytool) { break }
    & $keytool -list -keystore $ks -storepass $ksPass -alias $alias *> $null
    if ($LASTEXITCODE -eq 0) { break }
    Write-Host "Zle haslo albo alias klucza - sprobuj jeszcze raz ($i/3)." -ForegroundColor Yellow
    if ($i -eq 3) { exit 1 }
}
$keyPass = Plain (Read-Host "Haslo klucza (Enter = to samo)" -AsSecureString)
if (-not $keyPass) { $keyPass = $ksPass }

Push-Location (Join-Path $PSScriptRoot 'android')
try {
    # hasla przez zmienne srodowiska tylko tego procesu (nie widac ich na liscie procesow); po budowie usuwane
    ${env:ORG_GRADLE_PROJECT_android.injected.signing.store.file} = $ks
    ${env:ORG_GRADLE_PROJECT_android.injected.signing.store.password} = $ksPass
    ${env:ORG_GRADLE_PROJECT_android.injected.signing.key.alias} = $alias
    ${env:ORG_GRADLE_PROJECT_android.injected.signing.key.password} = $keyPass
    $zadania = @('assemblePelnaRelease', ':klawiatura:assembleRelease', ':komunikator:assembleRelease')
    if ($Sklep) { $zadania = @('assemblePelnaRelease', 'bundleSklepRelease', 'bundleSklepytRelease', ':klawiatura:assembleRelease', ':komunikator:assembleRelease') }
    if ($tylkoApk) { $zadania = @(); if ($Przegladarka) { $zadania += 'assemblePelnaRelease' }; if ($Klawiatura) { $zadania += ':klawiatura:assembleRelease' }; if ($Komunikator) { $zadania += ':komunikator:assembleRelease' } }
    & .\gradlew.bat --console=plain @zadania
    if ($LASTEXITCODE -ne 0) { Write-Host "Budowa Androida nie powiodla sie (zle haslo? brak SDK?)." -ForegroundColor Red; exit 1 }
} finally {
    Pop-Location; $ksPass = $null; $keyPass = $null
    foreach ($n in 'store.file', 'store.password', 'key.alias', 'key.password') { Remove-Item "env:ORG_GRADLE_PROJECT_android.injected.signing.$n" -ErrorAction SilentlyContinue }
}

$out = Join-Path $PSScriptRoot 'build\android'
if (-not $tylkoApk) { Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue }   # pojedyncza aplikacja - pozostałe APK zostają
New-Item -ItemType Directory -Force -Path $out | Out-Null
$o = Join-Path $PSScriptRoot 'android\app\build\outputs'
if (-not $tylkoApk -or $Przegladarka) { Copy-Item "$o\apk\pelna\release\*.apk" (Join-Path $out 'Velivo-pelna.apk') }
if (-not $tylkoApk -or $Klawiatura) { Copy-Item (Join-Path $PSScriptRoot 'android\klawiatura\build\outputs\apk\release\*.apk') (Join-Path $out 'Velivo-klawiatura.apk') }
if (-not $tylkoApk -or $Komunikator) { Copy-Item (Join-Path $PSScriptRoot 'android\komunikator\build\outputs\apk\release\*.apk') (Join-Path $out 'Velivo-komunikator.apk') }
if ($Sklep) {
    Copy-Item "$o\bundle\sklepRelease\*.aab" (Join-Path $out 'Velivo-sklep.aab')
    Copy-Item "$o\bundle\sklepytRelease\*.aab" (Join-Path $out 'Velivo-sklepyt.aab')
}
if (-not $tylkoApk) { Set-Content (Join-Path $cfgDir 'ostatnio-android.txt') $head }   # po pojedynczej aplikacji pełna budowa dalej widzi zmiany
Write-Host "Gotowe:" -ForegroundColor Green
if ($needWin) { Write-Host "  build\instalator\Velivo-Setup-1.22.exe - Windows" }
Write-Host "  build\android\Velivo-pelna.apk      - telefon (Ty i mama, WhatsApp)"
Write-Host "  build\android\Velivo-klawiatura.apk - klawiatura Velivo (osobna aplikacja)"
Write-Host "  build\android\Velivo-komunikator.apk - Komunikator Velivo (osobna aplikacja, sam wystarczy)"
if ($Sklep) { Write-Host "  build\android\Velivo-sklep.aab      - Google Play (ostrozna)"
Write-Host "  build\android\Velivo-sklepyt.aab    - Google Play (jak Brave)" }
Start-Process $out
try { Stop-Transcript | Out-Null } catch { }
