// Minimalny klient CDP bez zaleznosci (WebSocket wbudowany w Node 22).
// Zdarzenia Input.* sa zaufane (isTrusted) - jak prawdziwa mysz i klawiatura.
const [, , cmd, part, ...a] = process.argv;
const list = await (await fetch('http://127.0.0.1:9222/json')).json();
const t = list.find(x => x.type === 'page' && x.url.includes(part));
if (!t) { console.log('NOTARGET'); process.exit(0); }
const ws = new WebSocket(t.webSocketDebuggerUrl);
await new Promise(r => ws.addEventListener('open', r, { once: true }));
let id = 0; const wait = new Map();
ws.addEventListener('message', e => { const m = JSON.parse(e.data); if (m.id && wait.has(m.id)) { wait.get(m.id)(m); wait.delete(m.id); } });
const call = (method, params = {}) => new Promise(r => { const i = ++id; wait.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
const mouse = (type, x, y) => call('Input.dispatchMouseEvent', { type, x: +x, y: +y, button: type === 'mouseMoved' ? 'none' : 'left', clickCount: 1 });
let out = 'ok';
if (cmd === 'eval') { const r = await call('Runtime.evaluate', { expression: a[0], returnByValue: true }); out = String(r.result?.result?.value ?? ''); }
else if (cmd === 'hover') await mouse('mouseMoved', a[0], a[1]);
else if (cmd === 'click') { await mouse('mouseMoved', a[0], a[1]); await mouse('mousePressed', a[0], a[1]); await mouse('mouseReleased', a[0], a[1]); }
else if (cmd === 'text') await call('Input.insertText', { text: a[0] });
else if (cmd === 'enter') { for (const type of ['rawKeyDown', 'char', 'keyUp']) await call('Input.dispatchKeyEvent', { type, key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, text: type === 'char' ? '\r' : undefined }); }
else if (cmd === 'nav') await call('Page.navigate', { url: a[0] });
console.log(out); ws.close(); process.exit(0);
