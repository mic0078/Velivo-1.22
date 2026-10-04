# Wierna kopia MainWindow.CheckLookalike (src/Ochrona.cs) + przypadki testowe. Kod wyjscia 0 = wszystko poprawnie.
import re, sys
src = open(__file__.rsplit('/',1)[0] + '/../../src/Ochrona.cs', encoding='utf-8').read()
brands = {}
for m in re.finditer(r'\{ "(\w+)", new\[\] \{ ([^}]*) \} \}', src):
    brands[m.group(1)] = re.findall(r'"([^"]+)"', m.group(2))
two = re.findall(r'"([a-z]+\.[a-z]+)"', src[src.index('TwoLevelSuffixes'):src.index('static string RegistrableDomain')])
def reg(h):
    p = h.strip('.').lower().split('.')
    if len(p) <= 2: return '.'.join(p)
    l2 = p[-2] + '.' + p[-1]
    return p[-3] + '.' + l2 if l2 in two else l2
def sld(r): return r.split('.')[0]
def deconf(s):
    s = s.lower().replace('rn','m').replace('vv','w').replace('cl','d')
    mp = {'0':'o','1':'l','3':'e','4':'a','5':'s','7':'t','8':'b','9':'g','i':'l'}
    return ''.join(mp.get(c,c) for c in s)
def ed(a,b):
    if abs(len(a)-len(b))>2: return 99
    d=[[0]*(len(b)+1) for _ in range(len(a)+1)]
    for i in range(len(a)+1): d[i][0]=i
    for j in range(len(b)+1): d[0][j]=j
    for i in range(1,len(a)+1):
        for j in range(1,len(b)+1):
            d[i][j]=min(d[i-1][j]+1,d[i][j-1]+1,d[i-1][j-1]+(a[i-1]!=b[j-1]))
            if i>1 and j>1 and a[i-1]==b[j-2] and a[i-2]==b[j-1]: d[i][j]=min(d[i][j],d[i-2][j-2]+1)
    return d[-1][-1]
def check(host, extra=()):
    host=host.lower().strip('.'); r=reg(host)
    official={d for v in brands.values() for d in v}
    if r in official or r in extra: return None
    if any(l.startswith('xn--') for l in host.split('.')): return 'xn'
    toks=[t for t in re.split(r'[.\-]',host) if t]
    for b,v in brands.items():
        if any(t==b or (len(b)>=6 and t.startswith(b)) for t in toks): return v[0]
    lab=sld(r)
    if len(lab)>=4:
        for d in list(official)+list(extra):
            o=sld(d)
            if len(o)<4 or o==lab: continue
            if deconf(lab)==deconf(o): return d
            if len(o)>=6 and len(lab)>=5 and ed(lab,o)==1: return d
    return None
vault=('presonus.com','bitdefender.com','three.co.uk','giffgaff.com')
fake=['paypa1.com','paypal-secure-login.com','www.paypal.com.konto-weryfikacja.top','arnazon.co.uk','amaz0n.com','faceb00k.com','facebook-help.net',
 'ebay-uk.co.uk','mbank-logowanie.pl','ipko-weryfikacja.com','xn--pypal-4ve.com','netfiix.com','santander-uk.help','rnicrosoft.com','microsoft-support.info',
 'royalmail-redelivery.com','evri-parcel.info','hmrc-refund.uk','barclays-security.com','presonnus.com','bitdefendr.com','g1ffgaff.com','revolut-app.net','allegro-pl.shop','inpost-paczka.pl']
real=['paypal.com','www.paypal.com','login.microsoftonline.com','accounts.google.com','www.youtube.com','m.facebook.com','www.ebay.co.uk','www.amazon.pl','allegro.pl',
 'online.mbank.pl','www.ipko.pl','www.gov.uk','www.bbc.co.uk','www.wp.pl','onet.pl','github.com','my.fender.com','www.startpage.com','pineapple.com','www.reddit.com',
 'www.wikipedia.org','www.tesco.com','www.argos.co.uk','www.booking.com','www.ryanair.com','chat.openai.com','claude.ai','www.presonus.com','central.bitdefender.com','www.three.co.uk','www.giffgaff.com','www.olx.pl','www.monzo.com']
f=0
for h in fake:
    r=check(h,vault); good=r is not None; f+=not good; print(('ZALICZONY ' if good else 'NIEZALICZONY ')+'podrobka '+h+' -> '+str(r))
for h in real:
    r=check(h,vault); good=r is None; f+=not good; print(('ZALICZONY ' if good else 'NIEZALICZONY ')+'prawdziwa '+h+('' if good else ' -> FALSZYWY ALARM '+str(r)))
print(f'\nWynik: {len(fake)+len(real)-f} zaliczonych, {f} niezaliczonych'); sys.exit(1 if f else 0)
