# Test: synchronizacja LAN, gdy zaszyfrowany stan jest wiekszy niz limit UDP (60 000 B), np. z duzym trybem bankowym.
# Test udaje drugi, sparowany komputer (ten sam klucz LAN) pod adresem 127.0.0.2.
#  S. WYSYLANIE: Velivo ma duzy profil bankowy -> po "hello" od sparowanego komputera stan musi do niego dotrzec.
#  R. ODBIOR:    sparowany komputer wysyla stan > 60 000 B -> zakladka z tego stanu musi pojawic sie w Velivo.
#  L. bledy.log bez wyjatkow; Velivo dziala.
param([Parameter(Mandatory)] [string] $Exe)
$ErrorActionPreference = 'Continue'
$wyniki = @()
function Wynik($n, $ok, $info) { $s = if ($ok) { 'PASS' } else { 'FAIL' }; $script:wyniki += "[$s] $n - $info"; Write-Host "[$s] $n - $info" }
function Now-Ms { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }
function Losowy($n) { [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes($n)) }   # nie kompresuje sie (jak szyfrogram)
$port = 41919
$key = 'velivo-test-klucz-lan-1234567890'
$inv = [Globalization.CultureInfo]::InvariantCulture
$mat = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2($key, [Text.Encoding]::UTF8.GetBytes('Velivo LAN sync v1'), 250000, [Security.Cryptography.HashAlgorithmName]::SHA256, 64)
[byte[]]$enc = $mat[0..31]; [byte[]]$auth = $mat[32..63]
function AD($p) { [Text.Encoding]::UTF8.GetBytes((@($p.t, $p.id, $p.device, $p.profile, $p.ts.ToString($inv), $p.hash) -join "`n")) }

$data = Join-Path $env:RUNNER_TEMP 'velivo-lan-duzy'; Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory $data | Out-Null
Set-Content (Join-Path $data 'ustawienia.txt') "lanSync=1`r`nlanSyncKey=$key`r`n" -NoNewline -Encoding utf8
# duzy profil bankowy: ~90 KB zaszyfrowanych (losowych) danych - jak u uzytkownika po dodaniu kart/notatek
$bank = [ordered]@{ Owner = 'test'; Salt = (Losowy 16); Hash = (Losowy 32); Iter = 600000; Cards = (Losowy 68000) }
Set-Content (Join-Path $data 'bank-duzy.json') ($bank | ConvertTo-Json -Compress) -NoNewline -Encoding utf8
$env:PRZEGLADARKA_DANE = $data
$env:VELIVO_PROFILE = 'domyslny'
$proc = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 40
Write-Host "Velivo dziala: $(-not $proc.HasExited)"

$peerId = [guid]::NewGuid().ToString('N')
$peerIp = [Net.IPAddress]::Parse('127.0.0.2')

# ---- S. wysylanie duzego stanu przez Velivo
$tcp = $null
try {
  $tcp = [Net.Sockets.TcpListener]::new($peerIp, $port)
  $tcp.Server.SetSocketOption([Net.Sockets.SocketOptionLevel]::Socket, [Net.Sockets.SocketOptionName]::ReuseAddress, $true)
  $tcp.Start()
} catch { Write-Host "nasluch TCP 127.0.0.2: $($_.Exception.Message)" }
$udp = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new($peerIp, 0))
$hello = [ordered]@{ t = 'hello'; id = $peerId; device = 'SPAROWANY'; profile = 'domyslny'; ts = (Now-Ms); hash = $null }
$h = [Security.Cryptography.HMACSHA256]::new($auth); $hello.key = [Convert]::ToBase64String($h.ComputeHash((AD $hello)))
$b = [Text.Encoding]::UTF8.GetBytes(($hello | ConvertTo-Json -Compress)); [void]$udp.Send($b, $b.Length, '127.0.0.1', $port)
$odebrano = $null
if ($tcp) {
  $task = $tcp.AcceptTcpClientAsync()
  if ($task.Wait(25000)) {
    $c = $task.Result; $s = $c.GetStream(); $s.ReadTimeout = 10000
    $rd = { param($n) $buf = New-Object byte[] $n; $o = 0; while ($o -lt $n) { $r = $s.Read($buf, $o, $n - $o); if ($r -le 0) { break }; $o += $r }; ,$buf }
    $len = [BitConverter]::ToInt32((& $rd 4), 0); $hdr = [Text.Encoding]::UTF8.GetString((& $rd $len)) | ConvertFrom-Json
    $pktBytes = & $rd $hdr.Length; $c.Dispose()
    $p = [Text.Encoding]::UTF8.GetString($pktBytes) | ConvertFrom-Json
    $cipher = [Convert]::FromBase64String($p.data); $plain = New-Object byte[] $cipher.Length
    $g = [Security.Cryptography.AesGcm]::new($enc, 16); $g.Decrypt([Convert]::FromBase64String($p.nonce), $cipher, [Convert]::FromBase64String($p.tag), $plain, (AD $p))
    $ms = [IO.MemoryStream]::new($plain); $gz = [IO.Compression.GZipStream]::new($ms, [IO.Compression.CompressionMode]::Decompress); $sr = [IO.StreamReader]::new($gz)
    $odebrano = $sr.ReadToEnd() | ConvertFrom-Json
    Write-Host "odebrano przez TCP: naglowek=$($hdr.Type), pakiet=$($pktBytes.Length) B, typ=$($p.t)"
  }
  $tcp.Stop()
}
Wynik 'S. Velivo wysyla stan > 60 000 B do sparowanego komputera' (($null -ne $odebrano) -and ($odebrano.banks -match 'bank-duzy\.json')) $(if ($odebrano) { "banks zawiera bank-duzy.json: $($odebrano.banks -match 'bank-duzy\.json')" } else { 'nic nie dotarlo w 25 s' })

# ---- R. odbior duzego stanu (TCP)
function Wyslij-Duzy($marker, $zrodlo) {
  $zakl = "$marker`tDUZY STAN`n" + ((1..60 | ForEach-Object { "https://x$_.example/`t$(Losowy 1200)" }) -join "`n")
  $payload = [ordered]@{ settings = "lanSync=1`r`n"; bookmarks = $zakl; session = ''; sessionActive = ''; privacy = ''; profiles = ''; extensions = '[]'; passwords = '[]'
    changed = 1; bookmarksDeleted = ''; pinned = '' }
  $plain = [Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Compress))
  $pk = [ordered]@{ t = 'state'; id = $peerId; device = 'SPAROWANY'; profile = 'domyslny'; ts = (Now-Ms); hash = [guid]::NewGuid().ToString('N') }
  $nonce = [Security.Cryptography.RandomNumberGenerator]::GetBytes(12); $cipher = New-Object byte[] $plain.Length; $tag = New-Object byte[] 16
  $g = [Security.Cryptography.AesGcm]::new($enc, 16); $g.Encrypt($nonce, $plain, $cipher, $tag, (AD $pk))
  $pk.nonce = [Convert]::ToBase64String($nonce); $pk.tag = [Convert]::ToBase64String($tag); $pk.data = [Convert]::ToBase64String($cipher)
  $pb = [Text.Encoding]::UTF8.GetBytes(($pk | ConvertTo-Json -Compress))
  Write-Host "stan do wyslania: $($pb.Length) B z $zrodlo"
  try {
    $c = [Net.Sockets.TcpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Parse($zrodlo), 0)); $c.Connect("127.0.0.1", $port); $s = $c.GetStream()
    $hb = [Text.Encoding]::UTF8.GetBytes((@{ Type = 'lan-state'; Id = $peerId; Length = $pb.Length } | ConvertTo-Json -Compress))
    $s.Write([BitConverter]::GetBytes($hb.Length), 0, 4); $s.Write($hb, 0, $hb.Length); $s.Write($pb, 0, $pb.Length); $s.Flush(); Start-Sleep -Seconds 1; $c.Dispose()
  } catch { Write-Host "wysylanie TCP: $($_.Exception.Message)" }
  Start-Sleep -Seconds 8
  $zf = Join-Path $data 'zakladki.txt'
  (Test-Path $zf) -and ((Get-Content $zf -Raw) -match [regex]::Escape($marker))
}
# sparowany komputer - z adresu, z ktorego sie przedstawil (hello)
$jest = Wyslij-Duzy 'https://duzy-stan-test.example/' '127.0.0.2'
Wynik 'R. Velivo przyjmuje stan > 60 000 B od sparowanego komputera' $jest "zakladka z duzego stanu w zakladki.txt: $jest"
# obcy komputer podszywa sie pod id sparowanego (id jest jawne w pakietach) - inny adres
$obcy = Wyslij-Duzy 'https://obcy-duzy-stan.example/' '127.0.0.3'
Wynik 'R2. Duzy stan z obcego adresu (podszyte id) jest odrzucany' (-not $obcy) "zakladka od obcego w zakladki.txt: $obcy"

# ---- L
$log = Join-Path $data 'bledy.log'
$tekst = if (Test-Path $log) { Get-Content $log -Raw } else { '' }
$proc.Refresh()
Wynik 'L. Velivo dziala, brak wyjatkow w bledy.log' ((-not $proc.HasExited) -and ($tekst -notmatch 'Exception')) "dziala: $(-not $proc.HasExited), bledy.log: $($tekst.Length) znakow"
if ($tekst) { "=== bledy.log"; $tekst.Substring(0, [Math]::Min(3000, $tekst.Length)) }
$udp.Dispose()
"=== PODSUMOWANIE"; $wyniki
try { $proc.Kill($true) } catch {}; Get-Process msedgewebview2 -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
if ($wyniki -match '^\[FAIL\]') { exit 1 }
