# Test PasswordVault.cs:1430
#  A. normalny zapis hasla (prawdziwe klikniecia i klawiatura przez CDP Input) -> okno pytania o zapis
#  B. "film na wierzchu" + zamkniecie glownego okna -> po 15 s brak wyjatkow PasswordVault w bledy.log
#  C. brak innych wyjatkow w bledy.log
param([string]$Exe)
$ErrorActionPreference = 'Continue'
$here = $PSScriptRoot
Add-Type @'
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class Okna {
  delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr p);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  public static List<string> Lista(uint pid) {
    var r = new List<string>();
    EnumWindows((h, p) => { uint q; GetWindowThreadProcessId(h, out q);
      if (q == pid && IsWindowVisible(h)) { var t = new StringBuilder(512); var c = new StringBuilder(256); GetWindowText(h, t, 512); GetClassName(h, c, 256); r.Add(h.ToInt64() + "\t" + c + "\t" + t); }
      return true; }, IntPtr.Zero);
    return r;
  }
}
'@
function Okna($procId) { @([Okna]::Lista([uint32]$procId) | ForEach-Object { $f = $_.Split("`t"); [pscustomobject]@{ H = [IntPtr][int64]$f[0]; Klasa = $f[1]; Tytul = $f[2] } }) }
function CDP { node "$here\cdp.mjs" @args }
$wyniki = @()
function Wynik($n, $ok, $info) { $s = if ($ok) { 'PASS' } else { 'FAIL' }; $script:wyniki += "[$s] $n - $info"; Write-Host "[$s] $n - $info" }
$wzor = 'CapturePasswordCandidateAndPrompt|after the WebView2 control is disposed'
function Licz($plik) { if (Test-Path $plik) { @(Select-String -Path $plik -Pattern $wzor).Count } else { 0 } }

$srv = Start-Process python -ArgumentList '-m', 'http.server', '8765', '--bind', '127.0.0.1' -WorkingDirectory "$here\strony" -PassThru -WindowStyle Hidden
$data = Join-Path $env:RUNNER_TEMP 'velivo-pv'; Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory $data -Force | Out-Null
$log = Join-Path $data 'bledy.log'
$env:PRZEGLADARKA_DANE = $data
$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = '--remote-debugging-port=9222'
$base = 'http://127.0.0.1:8765/'
$p = Start-Process $Exe -ArgumentList ($base + 'logowanie.html') -PassThru
Start-Sleep -Seconds 40

# ---- A
$k = (CDP eval 'logowanie.html' '(()=>{const u=document.getElementById("user").getBoundingClientRect(),p=document.getElementById("pass").getBoundingClientRect();return [u.x+u.width/2,u.y+u.height/2,p.x+p.width/2,p.y+p.height/2].map(Math.round).join(" ")})()').Split(' ')
CDP click 'logowanie.html' $k[0] $k[1] | Out-Null; CDP text 'logowanie.html' 'jan' | Out-Null
CDP click 'logowanie.html' $k[2] $k[3] | Out-Null; CDP text 'logowanie.html' 'Tajne123!' | Out-Null
CDP enter 'logowanie.html' | Out-Null
$d = $null; for ($i = 0; $i -lt 15 -and -not $d; $i++) { Start-Sleep -Seconds 1; $d = Okna $p.Id | Where-Object { $_.Klasa -eq '#32770' } | Select-Object -First 1 }
"--- okna procesu po logowaniu:"; Okna $p.Id | ForEach-Object { "$($_.Klasa) | $($_.Tytul)" }
Wynik 'A. Pytanie o zapis hasla po logowaniu' ($null -ne $d) $(if ($d) { "okno dialogowe: '$($d.Tytul)'" } else { 'brak okna dialogowego' })
if ($d) { [Okna]::PostMessage($d.H, 0x0111, [IntPtr]7, [IntPtr]::Zero) | Out-Null; Start-Sleep -Seconds 2 }   # WM_COMMAND IDNO = "Nie"

# ---- B
$czesc = if ((CDP eval 'zalogowano' 'location.href') -ne 'NOTARGET') { 'zalogowano' } else { 'logowanie' }
CDP nav $czesc ($base + 'film.html') | Out-Null; Start-Sleep -Seconds 5
$v = (CDP eval 'film.html' '(()=>{const r=document.getElementById("v").getBoundingClientRect();return [r.x+r.width/2,r.y+r.height/2].map(Math.round).join(" ")})()').Split(' ')
$glowne = Okna $p.Id | Where-Object { $_.Tytul -match 'Velivo 1\.22$' } | Select-Object -First 1
$film = $null
# pasek nad filmem: [Pobierz][Film na wierzchu][Obraz w obrazie] - szukamy srodkowego; okno pobierania zamykamy
foreach ($frac in 0.55, 0.62, 0.5, 0.68, 0.45) {
  CDP hover 'film.html' ([int]$v[0] - 20) $v[1] | Out-Null; Start-Sleep -Milliseconds 300; CDP hover 'film.html' $v[0] $v[1] | Out-Null; Start-Sleep -Milliseconds 600
  $b = CDP eval 'film.html' "(()=>{const h=[...document.documentElement.children].find(e=>e.tagName==='DIV'&&e.style.zIndex==='2147483647');if(!h)return '';const r=h.getBoundingClientRect();return [r.x+r.width*$frac,r.y+r.height/2].map(Math.round).join(' ')})()"
  if (-not $b) { continue }
  $b = $b.Split(' '); CDP click 'film.html' $b[0] $b[1] | Out-Null; Start-Sleep -Seconds 6
  $nowe = @(Okna $p.Id | Where-Object { $_.H -ne $glowne.H -and $_.Tytul -and $_.Klasa -ne '#32770' })
  $film = $nowe | Where-Object { $_.Tytul -notmatch 'Downloads|Pobieran' } | Select-Object -First 1
  $nowe | Where-Object { $_.Tytul -match 'Downloads|Pobieran' } | ForEach-Object { [Okna]::SendMessage($_.H, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
  "  klik ${frac}: $(($nowe | ForEach-Object Tytul) -join ' / ')"
  if ($film) { break }
}
"--- okna procesu przed zamknieciem glownego:"; Okna $p.Id | ForEach-Object { "$($_.Klasa) | $($_.Tytul)" }
$przed = Licz $log
[Okna]::SendMessage($glowne.H, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null   # WM_CLOSE
Start-Sleep -Seconds 15
$p.Refresh(); $zostalo = @(Okna $p.Id | Where-Object { $_.Tytul })
$po = Licz $log
Wynik 'B0. Warunki: okno filmu otwarte, po zamknieciu glownego proces zyje' (($null -ne $film) -and -not $p.HasExited -and $zostalo.Count -ge 1) "film: '$($film.Tytul)', proces dziala: $(-not $p.HasExited), okna: $(($zostalo | ForEach-Object Tytul) -join ' / ')"
Wynik 'B. Brak wyjatku PasswordVault.cs:1430 w bledy.log' ($po -eq 0) "wpisy przed zamknieciem: $przed, po 15 s: $po"

# ---- C
$wszystkie = if (Test-Path $log) { @(Get-Content $log) } else { @() }
$inne = @($wszystkie | Where-Object { $_ -match 'Exception' -and $_ -notmatch $wzor -and $_ -notmatch 'COMException \(0x8007139F\)' })
"=== bledy.log: $($wszystkie.Count) linii (pierwsze 40)"; $wszystkie | Select-Object -First 40
Wynik 'C. Brak innych wyjatkow w bledy.log' ($inne.Count -eq 0) "linii: $($wszystkie.Count), innych wyjatkow: $($inne.Count)"
"=== PODSUMOWANIE"; $wyniki
try { $p.Kill($true) } catch {}; Stop-Process -Name msedgewebview2 -Force -ErrorAction SilentlyContinue; Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue
if ($wyniki -match '^\[FAIL\]') { exit 1 }
