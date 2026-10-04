import sys
def norm(h): h=h.strip().lower(); return h[4:] if h.startswith("www.") else h
def match(entry, page):
    t=norm(page); e=norm(entry)
    return bool(e) and (e==t or t.endswith("."+e) or e.endswith("."+t))
cases=[("facebook.com","facebook.com",True),("facebook.com","m.facebook.com",True),("login.bitdefender.com","bitdefender.com",True),
("facebook.com","facebook.xyz",False),("facebook.com","faceb00k.com",False),("facebook.com","facebook.com.evil.ru",False),
("facebook.com","evilfacebook.com",False),("x.com","x.com.login-verify.net",False),("ebay.co.uk","ebay.co.uk.secure-pay.top",False),
("paypal.com","paypal-login.com",False),("bitdefender.com","fender.com",False),("presonus.com","my.fender.com",False)]
f=0
for e,p,exp in cases:
    r=match(e,p); good=r==exp; f+=not good
    print(("ZALICZONY " if good else "NIEZALICZONY ")+f"konto {e} na stronie {p}: "+("podaje haslo" if r else "nie podaje"))
print(f"\nWynik: {len(cases)-f} zaliczonych, {f} niezaliczonych"); sys.exit(1 if f else 0)
