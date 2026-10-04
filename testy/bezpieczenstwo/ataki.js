const { chromium } = require('playwright');
const fs=require('fs');
const PV=fs.readFileSync(__dirname+'/pv.js','utf8'), AF=fs.readFileSync(__dirname+'/af.js','utf8');
let pass=0, fail=0;
const ok=(name,cond)=>{ console.log((cond?'ZALICZONY ':'NIEZALICZONY ')+name); cond?pass++:fail++; };
(async()=>{
 const b=await chromium.launch();
 async function page(body){
   const p=await b.newPage();
   await p.route('https://atak.test/**',r=>r.fulfill({contentType:'text/html',body}));
   await p.addInitScript(()=>{window.__msgs=[];window.chrome={webview:{postMessage:m=>window.__msgs.push(m)}};});
   await p.addInitScript({content:PV}); await p.addInitScript({content:AF});
   await p.addInitScript(()=>{try{delete window.chrome.webview}catch(e){}});   // jak w Velivo
   await p.goto('https://atak.test/'); return p;
 }
 // 1. podmiana pomocnika hasel
 let p=await page(`<input id=u><input id=pw type=password><script>
   window.stolen=[]; try{ window.__velivoPasswordHelper={version:4,setCredentials:c=>stolen.push(c),initialize(){},status(){return{}},takeCandidate(){}} }catch(e){}
   try{ Object.defineProperty(window,'__velivoPasswordHelper',{value:{setCredentials:c=>stolen.push(c)}}) }catch(e){} </script>`);
 await p.evaluate(()=>{ const h=window.__velivoPasswordHelper; h.setCredentials([{u:'ja@x.com',p:'TAJNE-HASLO',n:'x'}]); });
 ok('1. strona nie moze podmienic pomocnika hasel i przechwycic hasel', (await p.evaluate(()=>JSON.stringify(window.stolen))) === '[]');
 // 2. hasla nie da sie odczytac z obiektu pomocnika
 ok('2. hasla nie ma w zadnej wlasciwosci dostepnej dla strony', !(await p.evaluate(()=>JSON.stringify(window.__velivoPasswordHelper)+JSON.stringify(window.__velivoPasswordHelper.status()))).includes('TAJNE'));
 // 3. sztuczne klikniecie Wpisz / konta
 await p.click('#pw');
 await p.evaluate(()=>{ document.querySelector('[data-v=fill]').click(); });
 ok('3a. sztuczne klikniecie "Wpisz" nie otwiera listy kont', await p.evaluate(()=>{const c=document.querySelector('.velivo-pwd-choices');return !c||c.style.display==='none'||c.children.length===0;}));
 await p.click('[data-v=fill]');   // prawdziwe klikniecie uzytkownika pokazuje liste
 await p.evaluate(()=>{ const c=document.querySelector('[data-v=choose]'); if(c) c.click(); });
 ok('3b. sztuczne klikniecie konta nie wpisuje hasla do strony', (await p.evaluate(()=>document.getElementById('pw').value)) === '');
 await p.evaluate(()=>{ document.querySelector('[data-v=fill]').dispatchEvent(new MouseEvent('click',{bubbles:true})); });
 ok('3c. sztuczne zdarzenie click nie wysyla prosby do Velivo', !(await p.evaluate(()=>__msgs.join())).includes('pwpick'));
 ok('3d. prawdziwe klikniecie uzytkownika dziala (lista kont widoczna)', await p.evaluate(()=>document.querySelectorAll('[data-v=choose]').length===1));
 await p.click('[data-v=choose]');
 ok('3e. prawdziwe klikniecie konta wpisuje haslo', (await p.evaluate(()=>document.getElementById('pw').value)) === 'TAJNE-HASLO');
 await p.close();
 // 4. podmiana funkcji wypelniania formularzy
 p=await page(`<input name=cardnumber placeholder="Card number" style="width:200px"><script>window.got=null;try{window.__velivoAutofillFill=(t,d)=>{window.got=d}}catch(e){}</script>`);
 await p.evaluate(()=>window.__velivoAutofillFill('card',{number:'4111111111111111'}));
 ok('4. strona nie moze podmienic funkcji wypelniania i przechwycic karty', (await p.evaluate(()=>window.got)) === null);
 await p.close();
 // 5. ukryte pola (klasyczny atak na autouzupelnianie)
 p=await page(`<input name=imie placeholder=Imię style="width:200px">
  <input name=ulica style="width:1px;height:1px"><input name=telefon style="opacity:0;width:200px"><div style="position:absolute;left:-9999px"><input name=email type=email style="width:200px"></div>
  <input name=cardnumber placeholder="Card number" style="width:1px;height:1px">`);
 await p.click('[name=imie]');
 await p.evaluate(()=>window.__velivoAutofillFill('address',{first:'Jan',line1:'Byres Rd 1',phone:'07123',email:'jan@x.com'}));
 const v=await p.evaluate(()=>({imie:document.querySelector('[name=imie]').value,ulica:document.querySelector('[name=ulica]').value,tel:document.querySelector('[name=telefon]').value,mail:document.querySelector('[name=email]').value}));
 ok('5a. widoczne pole wypelnione (imie)', v.imie==='Jan');
 ok('5b. pole 1x1 piksel NIE wypelnione (adres)', v.ulica==='');
 ok('5c. pole przezroczyste NIE wypelnione (telefon)', v.tel==='');
 ok('5d. pole poza ekranem NIE wypelnione (e-mail)', v.mail==='');
 await p.evaluate(()=>{__msgs.length=0; document.querySelector('[name=cardnumber]').focus();});
 ok('5e. ukryte pole karty nie prosi Velivo o dane karty', !(await p.evaluate(()=>__msgs.join())).includes('affill:card'));
 await p.close();
 // 6. strona nie moze wyslac wiadomosci do Velivo (kanal ukryty, znacznik nieznany)
 p=await page(`<script>window.canPost=!!(window.chrome&&chrome.webview);</script>`);
 ok('6. strona nie ma dostepu do kanalu Velivo (chrome.webview)', (await p.evaluate(()=>window.canPost))===false);
 ok('6b. znacznik bezpieczenstwa nie jest widoczny w kodzie dostepnym dla strony', !(await p.evaluate(()=>String(window.__velivoAutofillFill)+Object.values(window.__velivoPasswordHelper).map(String).join(''))).includes('TOK'));
 await p.close();
 // 7. CVC nigdy
 p=await page(`<form><input autocomplete=cc-number style="width:200px"><input name=cvc style="width:200px"><input autocomplete=cc-exp style="width:200px"><button type=button>Pay</button></form>`);
 await p.fill('[autocomplete=cc-number]','4111 1111 1111 1111'); await p.fill('[name=cvc]','123'); await p.fill('[autocomplete=cc-exp]','12/29'); await p.click('text=Pay');
 ok('7. kod CVC nigdy nie jest wysylany do zapisu', !(await p.evaluate(()=>__msgs.join())).includes('123"'));
 await p.close();
 // 8. http (bez szyfrowania) - brak autouzupelniania formularzy
 const p2=await b.newPage(); await p2.route('http://nieszyfr.test/**',r=>r.fulfill({contentType:'text/html',body:'<input name=imie>'}));
 await p2.addInitScript(()=>{window.chrome={webview:{postMessage:()=>{}}};}); await p2.addInitScript({content:AF}); await p2.goto('http://nieszyfr.test/');
 ok('8. strony bez https nie dostaja autouzupelniania', (await p2.evaluate(()=>'__velivoAutofillFill' in window))===false);
 await b.close();
 console.log(`\nWynik: ${pass} zaliczonych, ${fail} niezaliczonych`);
 process.exit(fail?1:0);
})();
