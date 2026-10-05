import re, sys, os
from fontTools.ttLib import TTFont
from reportlab.lib.pagesizes import A4
from reportlab.lib.units import mm
from reportlab.lib import colors
from reportlab.lib.styles import ParagraphStyle
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont as RLTTF
from reportlab.platypus import (BaseDocTemplate, PageTemplate, Frame, Paragraph, Spacer, Table, TableStyle,
                                PageBreak, Image)
from reportlab.platypus.tableofcontents import TableOfContents
from reportlab.lib.enums import TA_CENTER

ROOT, MAIN, APPX, OUT = sys.argv[1:5]
D = '/usr/share/fonts/truetype/dejavu/'
pdfmetrics.registerFont(RLTTF('DV', D + 'DejaVuSans.ttf'))
pdfmetrics.registerFont(RLTTF('DVB', D + 'DejaVuSans-Bold.ttf'))
pdfmetrics.registerFont(RLTTF('DVM', D + 'DejaVuSansMono.ttf'))
pdfmetrics.registerFontFamily('DV', normal='DV', bold='DVB', italic='DV', boldItalic='DVB')
cmap = set(TTFont(D + 'DejaVuSans.ttf')['cmap'].getBestCmap().keys())
W = A4[0] - 36*mm

def clean(t):
    t = ''.join(ch for ch in t if ord(ch) in cmap or ch in '\n\t')   # emoji spoza czcionki - bez kwadratow
    return re.sub(r'  +', ' ', t).strip()

def inline(t):
    t = re.sub(r'!\[[^\]]*\]\([^)]*\)', '', t)
    t = re.sub(r'\[([^\]]+)\]\([^)]*\)', r'\1', t)
    t = clean(t).replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;')
    t = re.sub(r'\*\*(.+?)\*\*', r'<b>\1</b>', t)
    t = re.sub(r'(?<![\w*])\*([^*\n]+?)\*(?![\w*])', r'<i>\1</i>', t)
    t = re.sub(r'`([^`]+)`', r'<font face="DVM" size="8.5">\1</font>', t)
    return t

ACC = colors.HexColor('#1d4ed8')
def S(n, **k):
    base = dict(fontName='DV', fontSize=9.5, leading=13.5)
    base.update(k); return ParagraphStyle(n, **base)
st = {
    'title': S('t', fontName='DVB', fontSize=26, leading=32, alignment=TA_CENTER, spaceAfter=12, textColor=ACC),
    'sub': S('s', fontSize=11, leading=16, alignment=TA_CENTER, textColor=colors.HexColor('#444444')),
    'part': S('pt', fontName='DVB', fontSize=22, leading=28, alignment=TA_CENTER, textColor=ACC),
    'h1': S('h1', fontName='DVB', fontSize=16, leading=21, spaceAfter=8, textColor=ACC),
    'h2': S('h2', fontName='DVB', fontSize=12, leading=16, spaceBefore=10, spaceAfter=4, textColor=colors.HexColor('#0f172a')),
    'h3': S('h3', fontName='DVB', fontSize=10.5, leading=14, spaceBefore=8, spaceAfter=3),
    'p': S('p', spaceAfter=4),
    'li0': S('li0', leftIndent=14, bulletIndent=4, spaceAfter=2),
    'li1': S('li1', leftIndent=28, bulletIndent=18, spaceAfter=2),
    'cell': S('c', fontSize=8, leading=10.5),
    'cellb': S('cb', fontName='DVB', fontSize=8, leading=10.5, textColor=colors.white),
    'cap': S('cap', fontSize=8, leading=10, alignment=TA_CENTER, textColor=colors.HexColor('#555555')),
    'toc1': S('toc1', fontName='DVB', fontSize=10, leading=14),
    'toc2': S('toc2', fontSize=8.5, leading=11, leftIndent=14),
}

class Doc(BaseDocTemplate):
    def __init__(self, fn):
        super().__init__(fn, pagesize=A4, leftMargin=18*mm, rightMargin=18*mm, topMargin=18*mm, bottomMargin=18*mm,
                         title='Velivo 1.22 – Instrukcja obsługi', author='Velivo')
        self.addPageTemplates([PageTemplate(id='p', frames=[Frame(self.leftMargin, self.bottomMargin, self.width, self.height)], onPage=self.deco)])
    def deco(self, c, d):
        if d.page == 1: return
        c.saveState(); c.setFont('DV', 8); c.setFillColor(colors.HexColor('#666666'))
        c.drawString(18*mm, 10*mm, 'Velivo 1.22 – Instrukcja obsługi')
        c.drawRightString(A4[0]-18*mm, 10*mm, 'Strona %d' % d.page)
        c.restoreState()
    def afterFlowable(self, fl):
        if isinstance(fl, Paragraph) and getattr(fl, 'toc', None):
            lvl, text, key = fl.toc
            self.canv.bookmarkPage(key); self.canv.addOutlineEntry(text, key, level=lvl)
            self.notify('TOCEntry', (lvl, text, self.page, key))

n = [0]
def heading(text, lvl, style):
    n[0] += 1
    p = Paragraph(inline(text), st[style]); p.toc = (lvl, clean(re.sub(r'`', '', text)), 'k%d' % n[0]); return p

def img(path, maxw, maxh=110*mm):
    path = os.path.join(ROOT, path)
    if not os.path.exists(path): return None
    from reportlab.lib.utils import ImageReader
    iw, ih = ImageReader(path).getSize()
    s = min(maxw / iw, maxh / ih, 1.0 if iw < 500 else 10)
    return Image(path, iw * s, ih * s)

def render(lines, h0, h1, h2, story, skip_sections=()):
    i = 0; para = []; first = True
    def flush():
        if para: story.append(Paragraph(inline(' '.join(para)), st['p'])); para.clear()
    while i < len(lines):
        l = lines[i]
        m = re.match(r'^(#+) (.*)', l)
        if m:
            flush(); lv = len(m.group(1)); text = m.group(2)
            if lv == h0 - 1:   # tytul dokumentu - pomijamy (jest na okladce)
                i += 1; continue
            if text in skip_sections:
                i += 1
                while i < len(lines) and not re.match(r'^#{1,%d} ' % lv, lines[i]): i += 1
                continue
            if lv == h0:
                if not first: story.append(PageBreak())
                first = False; story.append(heading(text, 0, 'h1'))
            elif lv == h1: story.append(heading(text, 1, 'h2'))
            else: story.append(Paragraph(inline(text), st['h3']))
        elif l.startswith('|'):
            flush(); rows = []
            while i < len(lines) and lines[i].startswith('|'):
                cells = [c.strip() for c in lines[i].strip().strip('|').split('|')]
                if not all(re.fullmatch(r':?-+:?', c) for c in cells): rows.append(cells)
                i += 1
            i -= 1
            ncol = max(len(r) for r in rows)
            widths = [W*0.30, W*0.70] if ncol == 2 else ([W*0.22, W*0.40, W*0.18, W*0.07, W*0.06, W*0.07] if ncol == 6 else [W/ncol]*ncol)
            data = []
            for r, row in enumerate(rows):
                row = row + [''] * (ncol - len(row)); out = []
                for c_i, c in enumerate(row):
                    im = re.match(r'^!\[([^\]]*)\]\(([^)]+)\)$', c)
                    if im and r > 0:
                        o = img(im.group(2), widths[c_i] - 8, 45*mm); out.append(o if o else Paragraph(inline(im.group(1)), st['cell']))
                    else:
                        out.append(Paragraph(inline(c), st['cellb' if r == 0 else 'cell']))
                data.append(out)
            t = Table(data, colWidths=widths, repeatRows=1)
            t.setStyle(TableStyle([('BACKGROUND', (0, 0), (-1, 0), ACC), ('GRID', (0, 0), (-1, -1), 0.4, colors.HexColor('#cbd5e1')),
                                   ('VALIGN', (0, 0), (-1, -1), 'TOP'),
                                   ('ROWBACKGROUNDS', (0, 1), (-1, -1), [colors.white, colors.HexColor('#f1f5f9')]),
                                   ('LEFTPADDING', (0, 0), (-1, -1), 3), ('RIGHTPADDING', (0, 0), (-1, -1), 3)]))
            story += [t, Spacer(1, 5)]
        elif re.match(r'^!\[([^\]]*)\]\(([^)]+)\)\s*$', l):
            flush(); m2 = re.match(r'^!\[([^\]]*)\]\(([^)]+)\)', l); o = img(m2.group(2), W)
            if o: story += [o, Paragraph(inline(m2.group(1)), st['cap']), Spacer(1, 4)]
        elif re.match(r'^\s{2,}- ', l):
            flush(); story.append(Paragraph(inline(l.strip()[2:]), st['li1'], bulletText='–'))
        elif re.match(r'^- ', l):
            flush(); story.append(Paragraph(inline(l[2:]), st['li0'], bulletText='•'))
        elif re.match(r'^\s*\d+\. ', l):
            flush(); num, rest = l.strip().split('. ', 1)
            story.append(Paragraph(inline(rest), st['li0'], bulletText=num + '.'))
        elif l.strip() in ('', '---'):
            flush()
        else:
            para.append(l.strip())
        i += 1
    flush()

story = [Spacer(1, 55*mm), Paragraph('Velivo 1.22', st['title']),
         Paragraph('Instrukcja obsługi – pełny opis wszystkich funkcji', st['sub']), Spacer(1, 6*mm),
         Paragraph('Część I – Instrukcja obsługi · Część II – Dodatek techniczny (opis szczegółowy)', st['sub']), Spacer(1, 40*mm),
         Paragraph('Wersja 1.22 · instalator z commita 1f08f31 · dokumentacja 2026-10-05', st['sub']), PageBreak()]
toc = TableOfContents(); toc.levelStyles = [st['toc1'], st['toc2']]
story += [Paragraph('Spis treści', st['h1']), toc, PageBreak()]
story += [Spacer(1, 90*mm), Paragraph('Część I', st['part']), Paragraph('Instrukcja obsługi', st['sub']), PageBreak()]
render(open(MAIN, encoding='utf-8').read().split('\n'), 2, 3, 4, story, skip_sections=('Spis treści',))
story += [PageBreak(), Spacer(1, 90*mm), Paragraph('Część II', st['part']), Paragraph('Dodatek techniczny – opis szczegółowy funkcji krok po kroku', st['sub']), PageBreak()]
render(open(APPX, encoding='utf-8').read().split('\n'), 1, 2, 3, story)
doc = Doc(OUT); doc.multiBuild(story); print('stron:', doc.page)
