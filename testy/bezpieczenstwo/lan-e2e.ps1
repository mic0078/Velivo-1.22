# Test end-to-end synchronizacji LAN na prawdziwym Velivo.exe (Windows, PowerShell 7).
#   -Mode unpaired : Velivo bez sparowania. Atakujacy z sieci wysyla jawny pakiet "state-plain"
#                    (zmiana wyszukiwarki + falszywa zakladka). Nasluchujemy tez, co Velivo samo wysyla.
#   -Mode paired   : Velivo ze wspolnym kluczem. Test udaje sparowany komputer i wysyla prawidlowo
#                    zaszyfrowany i podpisany pakiet "state" - synchronizacja MUSI dalej dzialac.
# Wynik: plik JSON z pomiarami (bez oceny) - ocene robi wywolujacy (ta sama metoda dla wersji przed i po poprawce).
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [ValidateSet('unpaired', 'paired')] [string] $Mode,
    [Parameter(Mandatory)] [string] $Out
)
$ErrorActionPreference = 'Stop'
function Log($m) { Write-Host ((Get-Date -Format 'HH:mm:ss') + " [$Mode] $m") }
$port = 41919
$key = 'velivo-test-klucz-lan-1234567890'          # >= 24 znaki = "silny" klucz (tryb sparowany)
$evilUrl = 'https://evil-velivo-test.example/'
$data = Join-Path ([IO.Path]::GetTempPath()) ('velivo-e2e-' + $Mode + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $data | Out-Null
$settings = "lanSync=1`r`nsearch=startpage`r`n"
if ($Mode -eq 'paired') { $settings += "lanSyncKey=$key`r`n" }
Set-Content -Path (Join-Path $data 'ustawienia.txt') -Value $settings -NoNewline -Encoding utf8

$env:PRZEGLADARKA_DANE = $data
$env:VELIVO_PROFILE = 'domyslny'
$proc = Start-Process -FilePath $Exe -PassThru
Log "Velivo PID $($proc.Id), dane: $data"
Start-Sleep -Seconds 40   # start okna + WebView2 + LAN
Log "po starcie: dziala=$(-not $proc.HasExited)"

$result = [ordered]@{ mode = $Mode; started = -not $proc.HasExited }

function Now-Ms { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }

$payload = [ordered]@{
    settings = "lanSync=1`r`nsearch=brave`r`n"; bookmarks = "$evilUrl`tEVIL"; session = ''; sessionActive = ''
    privacy = ''; profiles = ''; extensions = '[]'; passwords = '[]'
    changed = (Now-Ms) + 86400000; bookmarksDeleted = ''; pinned = ''
}
$plain = [Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Compress))
$senderId = [guid]::NewGuid().ToString('N')

$udp = New-Object Net.Sockets.UdpClient
for ($i = 0; $i -lt 3; $i++) {
    $ts = Now-Ms
    $hash = [guid]::NewGuid().ToString('N')
    if ($Mode -eq 'unpaired') {
        $pkt = [ordered]@{ t = 'state-plain'; id = $senderId; device = 'ATAKUJACY'; profile = 'domyslny'; ts = $ts; hash = $hash
                           data = [Convert]::ToBase64String($plain) }
    } else {
        # dokladnie jak Velivo: PBKDF2(klucz, "Velivo LAN sync v1", 250000, SHA256) -> 32 B szyfrowanie + 32 B uwierzytelnianie
        $mat = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2($key, [Text.Encoding]::UTF8.GetBytes('Velivo LAN sync v1'), 250000, [Security.Cryptography.HashAlgorithmName]::SHA256, 64)
        $enc = $mat[0..31]
        $ad = [Text.Encoding]::UTF8.GetBytes((@('state', $senderId, 'SPAROWANY', 'domyslny', $ts.ToString([Globalization.CultureInfo]::InvariantCulture), $hash) -join "`n"))
        $nonce = [Security.Cryptography.RandomNumberGenerator]::GetBytes(12)
        $cipher = New-Object byte[] $plain.Length; $tag = New-Object byte[] 16
        $gcm = [Security.Cryptography.AesGcm]::new([byte[]]$enc, 16)
        $gcm.Encrypt($nonce, $plain, $cipher, $tag, $ad)
        $pkt = [ordered]@{ t = 'state'; id = $senderId; device = 'SPAROWANY'; profile = 'domyslny'; ts = $ts; hash = $hash
                           nonce = [Convert]::ToBase64String($nonce); tag = [Convert]::ToBase64String($tag); data = [Convert]::ToBase64String($cipher) }
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($pkt | ConvertTo-Json -Compress))
    [void]$udp.Send($bytes, $bytes.Length, '127.0.0.1', $port)
    Start-Sleep -Seconds 2
}
$udp.Dispose()
Log 'wyslano 3 pakiety'
Start-Sleep -Seconds 8

$bm = Join-Path $data 'zakladki.txt'; $st = Join-Path $data 'ustawienia.txt'
$result.injectedBookmark = (Test-Path $bm) -and ((Get-Content $bm -Raw) -match [regex]::Escape($evilUrl))
$result.injectedSettings = (Get-Content $st -Raw) -match 'search=brave'

# co Velivo samo rozglasza w sieci (nasluch na tym samym porcie, SO_REUSEADDR)
$rx = New-Object Net.Sockets.UdpClient
$rx.ExclusiveAddressUse = $false
$rx.Client.SetSocketOption([Net.Sockets.SocketOptionLevel]::Socket, [Net.Sockets.SocketOptionName]::ReuseAddress, $true)
$rx.Client.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Any, $port))
$rx.Client.ReceiveTimeout = 1000
$types = @{}; $plainData = 0; $leak = 0
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline) {
    try {
        $ep = [Net.IPEndPoint]::new([Net.IPAddress]::Any, 0)
        $buf = $rx.Receive([ref]$ep)
        $j = [Text.Encoding]::UTF8.GetString($buf) | ConvertFrom-Json
        $types[$j.t] = 1 + [int]$types[$j.t]
        if ($j.data) {
            # jawne dane = base64 z JSON-em ({"...)
            try { $d = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($j.data)); if ($d.TrimStart().StartsWith('{')) { $plainData++ } } catch {}
        }
        if ([Text.Encoding]::UTF8.GetString($buf) -match 'Startpage|startpage|lanSync=') { $leak++ }
    } catch { if ($_.Exception.InnerException -isnot [Net.Sockets.SocketException] -and $_.Exception -isnot [Net.Sockets.SocketException]) { Log "odbior: $($_.Exception.Message)" } }
}
$rx.Dispose()
Log "nasluch zakonczony: $($types.Keys -join ',')"
$result.sentPacketTypes = $types
$result.sentPlainDataPackets = $plainData
$result.sentPacketsWithReadableSettings = $leak
$result.stillRunning = -not $proc.HasExited
$log = Join-Path $data 'bledy.log'
$result.errorLog = if (Test-Path $log) { (Get-Content $log -Raw).Substring(0, [Math]::Min(1500, (Get-Content $log -Raw).Length)) } else { '' }
try { $proc.Kill($true) } catch {}
Get-Process msedgewebview2 -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3
$result | ConvertTo-Json -Depth 4 | Set-Content $Out -Encoding utf8
Get-Content $Out
