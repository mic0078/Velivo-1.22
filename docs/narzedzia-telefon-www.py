# Instrukcja Velivo na telefon: strona WWW (PL/EN) i PDF z dokumentacja/instrukcja-telefon(.en).md
# Użycie: python3 docs/narzedzia-telefon-www.py  (PDF przez Chromium/Playwright, gdy jest)
import html, re, sys, os
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

def inline(t):
    t = html.escape(t, quote=False)
    t = re.sub(r'`([^`]+)`', r'<code>\1</code>', t)
    t = re.sub(r'\*\*(.+?)\*\*', r'<b>\1</b>', t)
    return t

def md(text):
    out, lines, i = [], text.split('\n'), 0
    toc = []
    while i < len(lines):
        l = lines[i]
        if l.startswith('|') and i + 1 < len(lines) and re.match(r'^\|\s*-', lines[i + 1]):
            hd = [c.strip() for c in l.strip('|').split('|')]
            out.append('<table><tr>' + ''.join('<th>' + inline(c) + '</th>' for c in hd) + '</tr>')
            i += 2
            while i < len(lines) and lines[i].startswith('|'):
                out.append('<tr>' + ''.join('<td>' + inline(c.strip()) + '</td>' for c in lines[i].strip('|').split('|')) + '</tr>'); i += 1
            out.append('</table>'); continue
        if l.startswith('>'):
            q = []
            while i < len(lines) and lines[i].startswith('>'): q.append(lines[i][1:].strip()); i += 1
            out.append('<aside>' + md('\n'.join(q)) + '</aside>'); continue
        m = re.match(r'^(#{1,3}) (.*)', l)
        if m:
            n = len(m.group(1)); a = 's%d' % len(toc)
            if n == 2: toc.append((a, m.group(2)))
            out.append('<h%d id="%s">%s</h%d>' % (n, a, inline(m.group(2)), n)); i += 1; continue
        if re.match(r'^\s*(- |\d+\. )', l):
            ordered = bool(re.match(r'^\s*\d+\. ', l)); tag = 'ol' if ordered else 'ul'
            out.append('<%s>' % tag)
            while i < len(lines) and re.match(r'^\s*(- |\d+\. )', lines[i]):
                item = re.sub(r'^\s*(- |\d+\. )', '', lines[i]); sub = lines[i].startswith('   ')
                out.append(('<li class="sub">' if sub else '<li>') + inline(item) + '</li>'); i += 1
            out.append('</%s>' % tag); continue
        if l.strip(): out.append('<p>' + inline(l) + '</p>')
        i += 1
    return '\n'.join(out) if not toc else ('<nav><b>Spis / Contents</b><ol>' + ''.join('<li><a href="#%s">%s</a></li>' % (a, inline(t)) for a, t in toc) + '</ol></nav>\n' + '\n'.join(out))

CSS = '''
:root { --bg:#F7F4FF; --card:#FFFFFF; --text:#2A2150; --soft:#4F4380; --acc:#6D3FDB; --line:#E2DBFA; }
@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) { --bg:#15121F; --card:#1B1733; --text:#ECE8FF; --soft:#B9B0E0; --acc:#B3A6FF; --line:#2E2768; } }
body { margin:0; background:var(--bg); color:var(--text); font:17px/1.6 system-ui, "Segoe UI", Roboto, sans-serif; overflow-wrap:anywhere; }
html, body { overflow-x:hidden; }
main { max-width:900px; margin:0 auto; padding:16px; box-sizing:border-box; width:100%; }
.pdf { margin:8px 0 16px; } .pdf a { color:var(--acc); margin-right:16px; }
header { display:flex; align-items:center; gap:12px; flex-wrap:wrap; }
header h1 { font-size:26px; margin:8px 0; }
.lang button { font:inherit; padding:8px 16px; border-radius:12px; border:1px solid var(--line); background:var(--card); color:var(--text); cursor:pointer; }
.lang button.on { background:var(--acc); color:#fff; }
h2 { margin-top:36px; padding-bottom:4px; border-bottom:2px solid var(--line); }
h3 { color:var(--acc); }
table { border-collapse:collapse; width:100%; max-width:100%; margin:12px 0; display:block; overflow-x:auto; }
th, td { border:1px solid var(--line); padding:8px 10px; text-align:left; vertical-align:top; }
th { background:var(--line); }
td { background:var(--card); }
aside { background:var(--card); border-left:5px solid var(--acc); border-radius:12px; padding:4px 16px; margin:16px 0; }
nav { background:var(--card); border-radius:12px; padding:8px 16px; }
nav a { color:var(--acc); }
code { background:var(--line); padding:1px 5px; border-radius:6px; }
li.sub { margin-left:24px; list-style:circle; }
@media print { .lang, nav { display:none; } body { font-size:12pt; } h2 { page-break-after:avoid; } table, aside { page-break-inside:avoid; } }
'''

def page(body_pl, body_en, title):
    return '''<!doctype html><html lang="pl"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>%s</title><style>%s</style></head><body><main>
<header><img src="data:image/svg+xml,%%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 48 48'%%3E%%3Crect width='48' height='48' rx='12' fill='%%236D3FDB'/%%3E%%3Ctext x='24' y='34' font-size='28' text-anchor='middle' fill='white' font-family='Arial' font-weight='bold'%%3EV%%3C/text%%3E%%3C/svg%%3E" width="48" height="48" alt="">
<h1>Velivo – telefon / phone</h1>
<div class="lang"><button id="bpl" class="on" onclick="lang('pl')">Polski</button> <button id="ben" onclick="lang('en')">English</button></div></header>
<p class="pdf">📄 <a href="Velivo-Telefon-Instrukcja.pdf">Instrukcja PDF (polski)</a> <a href="Velivo-Phone-Manual-EN.pdf">PDF manual (English)</a></p>
<section id="pl">%s</section><section id="en" lang="en" hidden>%s</section>
<script>function lang(l){document.getElementById('pl').hidden=l!='pl';document.getElementById('en').hidden=l!='en';
document.getElementById('bpl').className=l=='pl'?'on':'';document.getElementById('ben').className=l=='en'?'on':'';
try{localStorage.setItem('jezyk',l)}catch(e){}}
try{if(localStorage.getItem('jezyk')=='en'||(!localStorage.getItem('jezyk')&&!navigator.language.startsWith('pl')))lang('en')}catch(e){}</script>
</main></body></html>''' % (title, CSS, body_pl, body_en)

def single(body, title, lang):
    return '<!doctype html><html lang="%s"><head><meta charset="utf-8"><title>%s</title><style>%s :root{--bg:#fff;--card:#fff;--text:#2A2150;--soft:#4F4380;--acc:#6D3FDB;--line:#E2DBFA}</style></head><body><main><h1>%s</h1>%s</main></body></html>' % (lang, title, CSS, title, body)

pl = open(os.path.join(ROOT, 'dokumentacja/instrukcja-telefon.md'), encoding='utf-8').read()
en = open(os.path.join(ROOT, 'dokumentacja/instrukcja-telefon.en.md'), encoding='utf-8').read()
pl_b = md(pl.split('\n', 1)[1]); en_b = md(en.split('\n', 1)[1])
# na górze strony: krótko czym jest Velivo, instalacja i aktualizacje (ten sam tekst co wiadomość dla znajomych)
op = open(os.path.join(ROOT, 'dokumentacja/opis-dla-znajomych.md'), encoding='utf-8').read()
op_pl = op.split('## Po polsku', 1)[1].split('## In English', 1)[0]; op_en = op.split('## In English', 1)[1]
op_pl = op_pl.replace('Cześć! Przesyłam Ci **Velivo** – lekką, prywatną przeglądarkę na telefon (Android 10+), którą robię sam.', '**Velivo** to lekka, prywatna przeglądarka na telefon (Android 10+).').replace('Instrukcja w załączonym PDF.', 'Instrukcja – poniżej i w PDF.')
op_en = op_en.replace('Hi! Here is **Velivo** – a light, private phone browser (Android 10+) that I make myself.', '**Velivo** is a light, private phone browser (Android 10+).').replace('The manual is in the attached PDF.', 'The manual is below and in the PDF.')
def withAbout(body, title, about, policy):
    nav, rest = body.split('</nav>', 1)
    nav = nav.replace('<ol>', '<ol><li><a href="#o">' + title + '</a></li>', 1)
    return nav + '</nav>\n<h2 id="o">' + title + '</h2>' + md(about) + rest + '<p><a href="polityka-prywatnosci.html">' + policy + '</a></p>'
pl_page = withAbout(pl_b, 'O Velivo', op_pl, 'Polityka prywatności')
en_page = withAbout(en_b, 'About Velivo', op_en, 'Privacy policy')
open(os.path.join(ROOT, 'docs/telefon.html'), 'w', encoding='utf-8').write(page(pl_page, en_page, 'Velivo – instrukcja na telefon / phone manual'))
# PDF: Chromium bez okna (Chrome/Edge/Chromium - pierwszy znaleziony), drukowanie strony do A4
import shutil, subprocess, tempfile
chrome = os.environ.get('CHROME') or next((c for c in ['/opt/pw-browsers/chromium-1194/chrome-linux/chrome', shutil.which('chromium') or '', shutil.which('google-chrome') or '',
    r'C:\Program Files\Google\Chrome\Application\chrome.exe', r'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'] if c and os.path.exists(c)), None)
if not chrome: print('Brak Chromium/Chrome/Edge - tylko strona WWW', file=sys.stderr); sys.exit(0)
for body, title, lang, out in [(pl_b, 'Velivo na telefon – instrukcja', 'pl', 'docs/Velivo-Telefon-Instrukcja.pdf'), (en_b, 'Velivo for phones – manual', 'en', 'docs/Velivo-Phone-Manual-EN.pdf')]:
    with tempfile.TemporaryDirectory() as d:
        f = os.path.join(d, 'i.html'); open(f, 'w', encoding='utf-8').write(single(body.split('</nav>', 1)[-1], title, lang))
        subprocess.run([chrome, '--headless', '--no-sandbox', '--disable-gpu', '--no-pdf-header-footer', '--print-to-pdf=' + os.path.join(ROOT, out), 'file://' + f], check=True, capture_output=True, timeout=120)
print('ok')
