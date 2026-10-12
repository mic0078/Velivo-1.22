# Test dodatku "odtwarzacz filmow" (CI, Windows): pobiera libvlc DOKLADNIE tak jak instalator - adres, suma SHA-256 i polecenie
# tar czytane z src/installer.iss (jedno zrodlo) - i tworzy maly film AVI do odtworzenia przez VlcPlaybackTests.
# Uzycie: python -I testy/odtwarzacz-vlc/przygotuj.py <folder roboczy>   -> <folder>/build/x64 (libvlc) i <folder>/test.avi
import hashlib, os, re, struct, subprocess, sys, urllib.request

work = sys.argv[1]
os.makedirs(work, exist_ok=True)
iss = open(os.path.join(os.path.dirname(__file__), '..', '..', 'src', 'installer.iss'), encoding='utf-8-sig').read()
url = re.search(r"VlcUrl = '([^']+)'", iss).group(1)
sha = re.search(r"VlcSha256 = '([0-9a-f]+)'", iss).group(1)
args = ''.join(re.findall(r"'([^']*)'", re.search(r"VlcTarArgs = (.*?);\n", iss, re.S).group(1))).split()

pkg = os.path.join(work, 'libvlc.nupkg')
urllib.request.urlretrieve(url, pkg)
got = hashlib.sha256(open(pkg, 'rb').read()).hexdigest()
if got != sha: sys.exit('zla suma SHA-256: ' + got)
tar = os.path.join(os.environ.get('SystemRoot', r'C:\Windows'), 'System32', 'tar.exe')
subprocess.check_call([tar, '-xf', pkg, '-C', work] + args)
x64 = os.path.join(work, 'build', 'x64')
for must in ['libvlc.dll', 'libvlccore.dll', os.path.join('plugins', 'codec'), os.path.join('plugins', 'demux')]:
    if not os.path.exists(os.path.join(x64, must)): sys.exit('brak po rozpakowaniu: ' + must)
for gone in ['stream_out', 'visualization', 'lua']:
    if os.path.exists(os.path.join(x64, 'plugins', gone)): sys.exit('nie wyciety: ' + gone)
size = sum(os.path.getsize(os.path.join(d, f)) for d, _, fs in os.walk(x64) for f in fs)
print('libvlc rozpakowana: %.1f MB' % (size / 1048576))

# AVI bez kompresji (RGB 24 bit, 64x48, 50 klatek po 25/s = 2 s) - format, ktorego zwykly odtwarzacz w karcie nie odtworzy
W, H, N, FPS = 64, 48, 50, 25
stride = (W * 3 + 3) & ~3
frames = [bytes([(i * 5) % 256, (x * 4) % 256, (y * 5) % 256][c] for y in range(H) for x in range(W) for c in range(3)) for i in range(N)]
def chunk(fcc, data): return fcc + struct.pack('<I', len(data)) + data + (b'\0' if len(data) % 2 else b'')
def lst(typ, data): return chunk(b'LIST', typ + data)
size_img = stride * H
avih = struct.pack('<IIIIIIIIIIIIII', 1000000 // FPS, size_img * FPS, 0, 0x10, N, 0, 1, size_img, W, H, 0, 0, 0, 0)
strh = b'vids' + b'DIB ' + struct.pack('<IHHIIIIIIIIhhhh', 0, 0, 0, 0, 1, FPS, 0, N, size_img, 0xFFFFFFFF, 0, 0, 0, W, H)
strf = struct.pack('<IiiHHIIiiII', 40, W, H, 1, 24, 0, size_img, 0, 0, 0, 0)
hdrl = lst(b'hdrl', chunk(b'avih', avih) + lst(b'strl', chunk(b'strh', strh) + chunk(b'strf', strf)))
movi_body, idx, off = b'', b'', 4
for f in frames:
    c = chunk(b'00db', f)
    idx += b'00db' + struct.pack('<III', 0x10, off, len(f))
    movi_body += c; off += len(c)
riff = b'AVI ' + hdrl + lst(b'movi', movi_body) + chunk(b'idx1', idx)
open(os.path.join(work, 'test.avi'), 'wb').write(b'RIFF' + struct.pack('<I', len(riff)) + riff)
print('test.avi gotowy')
