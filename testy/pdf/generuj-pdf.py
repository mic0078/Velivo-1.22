# Testowy PDF (2 strony, polski tekst, tytul w metadanych, slowo przeniesione myslnikiem) - dla test-tekst-pdf.mjs
import sys
from reportlab.lib.pagesizes import A4
from reportlab.pdfgen import canvas
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
import glob
font = 'Helvetica'
for p in glob.glob('/usr/share/fonts/**/DejaVuSans.ttf', recursive=True):
    pdfmetrics.registerFont(TTFont('DejaVu', p)); font = 'DejaVu'; break
c = canvas.Canvas(sys.argv[1], pagesize=A4)
c.setTitle('Sprawozdanie roczne Velivo')
c.setFont(font, 12)
y = 800
def wiersz(t, odstep=16):
    global y; c.drawString(60, y, t); y -= odstep
wiersz('Pierwszy akapit opisuje program. Przeglądarka Velivo działa szybko')
wiersz('i chroni prywatność użytkownika.')
wiersz('Nie wysyła treści stron do chmury, a czytnik działa lokalnie na komputerze.')
y -= 14
wiersz('Drugi akapit mówi o ochronie. Tryb bankowy dba o bezpie-')
wiersz('czeństwo przelewów i blokuje rozszerzenia podglądające dane.')
y -= 14
wiersz('Trzeci akapit kończy pierwszą stronę i zawiera jeszcze jedno zdanie.')
c.showPage()
c.setFont(font, 12); y = 800
wiersz('Druga strona zaczyna się tutaj. Tekst płynie dalej po kolei, bez mieszania.')
c.save()
