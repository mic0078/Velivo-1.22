// Arkusz rachunkow: strona oddaje dane do Velivo tylko z jednorazowym znacznikiem (tok) nadanym przez Velivo.
// Uruchamia prawdziwy skrypt strony wyciagniety z src/Bank.cs (BillSheetHtml) w atrapie DOM.
const fs = require('fs');
const src = fs.readFileSync(__dirname + '/../../src/Bank.cs', 'utf8');
const html = src.slice(src.indexOf('BillSheetHtml = @"'));
const js = html.slice(html.indexOf('<script>') + 8, html.indexOf('</script>')).replace(/""/g, '"');
const el = () => ({ appendChild() {}, setAttribute() {}, style: {}, textContent: '', set onclick(f) {}, set oninput(f) {}, set onchange(f) {} });
global.document = { getElementById: () => Object.assign(el(), { click() {}, }), createElement: el, querySelector: () => null, querySelectorAll: () => [] };
global.window = global;
let fail = 0; const check = (n, c) => { console.log((c ? 'OK   ' : 'BLAD ') + n); if (!c) fail++; };
const D = { tok: 'ABC123', cur: '£', dec: ',', cols: ['Czynsz'], rows: [{ m: 'maj 2026', v: ['100'] }], next: [], mnames: ['styczeń'] };
(0, eval)(js.replace('__DATA__', JSON.stringify(D)));
window.done(1);
check('dane ze strony maja znacznik nadany przez Velivo', window.__velivoSheet && window.__velivoSheet.tok === 'ABC123');
check('zapis = save:true', window.__velivoSheet.save === true);
const csharp = src.slice(src.indexOf('async void OpenBillSheet'), src.indexOf('const string BillSheetHtml'));
check('Velivo odrzuca dane bez zgodnego znacznika', /tk\.GetString\(\) != sheetToken\) return;/.test(csharp));
check('Velivo konczy (bez zapisu), gdy w karcie jest inna strona', (csharp.match(/core\.Source, sheetSource/g) || []).length >= 2);
check('znacznik losowany kryptograficznie dla kazdego otwarcia', /sheetToken = Convert\.ToHexString\(RandomNumberGenerator\.GetBytes\(16\)\)/.test(csharp));
process.exit(fail ? 1 : 0);
