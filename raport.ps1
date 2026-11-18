# Raport dla pomocy (Claude): błędy wszystkich programów Velivo, dziennik odtwarzacza, awarie Windows i działające programy - jeden plik na pulpicie.
# Użycie: PowerShell w folderze repozytorium ->  .\raport.ps1
$ErrorActionPreference = 'SilentlyContinue'
$out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'raport-velivo.txt'
$lad = [Environment]::GetFolderPath('LocalApplicationData')
$r = New-Object System.Text.StringBuilder
function Dodaj($t) { [void]$r.AppendLine($t) }
function Ogon($tytul, $plik, $n) {
    Dodaj ""; Dodaj "== $tytul ($plik) =="
    if (Test-Path $plik) { Get-Content $plik -Tail $n -Encoding UTF8 | ForEach-Object { Dodaj $_ } } else { Dodaj "(brak pliku)" }
}
Dodaj "Raport Velivo $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') · $env:COMPUTERNAME · Windows $([Environment]::OSVersion.Version)"
Dodaj ""; Dodaj "== Działające programy Velivo =="
Get-Process | Where-Object { $_.ProcessName -like 'Velivo*' } | Sort-Object ProcessName | ForEach-Object { Dodaj ("{0}  {1} MB  {2}" -f $_.ProcessName, [int]($_.WorkingSet64 / 1MB), $_.MainModule.FileVersionInfo.FileVersion) }
Ogon 'Błędy Velivo' (Join-Path $lad 'Velivo\Poważne błędy\bledy.log') 400
Get-ChildItem (Join-Path $lad 'Velivo') -Recurse -Filter 'vlc.log' | ForEach-Object { Ogon 'Dziennik odtwarzacza' $_.FullName 300 }
Dodaj ""; Dodaj "== Awarie programów Velivo w Windows (48 h) =="
$ev = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000, 1026; StartTime = (Get-Date).AddHours(-48) } | Where-Object { $_.Message -like '*Velivo*' }
if ($ev) { $ev | ForEach-Object { Dodaj "$($_.TimeCreated)  [$($_.Id)]"; Dodaj $_.Message; Dodaj "" } } else { Dodaj "brak – Windows nie zgłosił awarii programów Velivo" }
[IO.File]::WriteAllText($out, $r.ToString(), (New-Object System.Text.UTF8Encoding $true))
Write-Host "Raport zapisany: $out" -ForegroundColor Green
