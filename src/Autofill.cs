using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace Przegladarka
{
    // Lokalne autouzupełnianie (adresy i karty) z szyfrowaniem DPAPI per użytkownik Windows.
    public partial class MainWindow
    {
        sealed class AutofillEntry
        {
            public string Host { get; set; }
            public string Type { get; set; } // address | card
            public Dictionary<string, string> Values { get; set; }
            public long UpdatedUnix { get; set; }
        }

        sealed class AutofillVault
        {
            public int Version { get; set; }
            public List<AutofillEntry> Entries { get; set; }
        }

        static string AutofillVaultFile { get { return Path.Combine(DataDir, "autouzupelnianie.vault"); } }
        static readonly byte[] AutofillEntropy = Encoding.UTF8.GetBytes("Velivo.Autofill.v1");
        readonly Dictionary<string, DateTime> _autofillPrompted = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        List<AutofillEntry> _autofillEntries;

        void EnsureAutofillLoaded()
        {
            if (_autofillEntries != null) return;
            _autofillEntries = new List<AutofillEntry>();
            try
            {
                if (!File.Exists(AutofillVaultFile)) return;
                var enc = Convert.FromBase64String(File.ReadAllText(AutofillVaultFile));
                var plain = ProtectedData.Unprotect(enc, AutofillEntropy, DataProtectionScope.CurrentUser);
                var vault = JsonSerializer.Deserialize<AutofillVault>(plain);
                if (vault != null && vault.Entries != null)
                {
                    _autofillEntries = vault.Entries.Where(e => e != null && e.Values != null).ToList();
                    bool removedCvc = false;
                    foreach (var entry in _autofillEntries.Where(e => string.Equals(e.Type, "card", StringComparison.OrdinalIgnoreCase)))
                    {
                        foreach (var key in entry.Values.Keys.Where(k => string.Equals(k, "cvc", StringComparison.OrdinalIgnoreCase)).ToList())
                            removedCvc |= entry.Values.Remove(key);
                    }
                    if (removedCvc) SaveAutofillVault();
                }
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        void SaveAutofillVault()
        {
            try
            {
                EnsureAutofillLoaded();
                Directory.CreateDirectory(DataDir);
                var vault = new AutofillVault { Version = 1, Entries = _autofillEntries.OrderByDescending(e => e.UpdatedUnix).ToList() };
                var plain = JsonSerializer.SerializeToUtf8Bytes(vault, new JsonSerializerOptions { WriteIndented = true });
                var enc = ProtectedData.Protect(plain, AutofillEntropy, DataProtectionScope.CurrentUser);
                File.WriteAllText(AutofillVaultFile, Convert.ToBase64String(enc));
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        static string HostFromUrl(string url)
        {
            try
            {
                Uri u;
                if (Uri.TryCreate(url, UriKind.Absolute, out u)) return (u.Host ?? "").ToLowerInvariant();
            }
            catch (Exception) { }
            return "";
        }

        // Skrypt formularzy: rozpoznaje pola adresu, karty i konta bankowego. Gdy klikniesz puste pole - prosi Velivo
        // o dane tego rodzaju (dane nie siedza na stronie na zapas); przy wysylaniu formularza - proponuje zapis.
        // Dziala tez w kartach prywatnych; zapis zawsze dopiero po pytaniu.
        const string AutofillPageScript = @"(() => {
try {
  if (window.__velivoAutofillFill || location.protocol !== 'https:') return;
  const vpm = (window.chrome && chrome.webview && chrome.webview.postMessage) ? chrome.webview.postMessage.bind(chrome.webview) : null;
  if (!vpm) return;
  const send = m => { try { vpm('velivo:__VT_TOKEN__:' + m); } catch (_) {} };
  function keyFor(i) {
    if (!i || !(i instanceof HTMLInputElement || i instanceof HTMLTextAreaElement || i instanceof HTMLSelectElement)) return null;
    const t = (i.type || '').toLowerCase();
    if (t === 'password' || t === 'hidden' || t === 'checkbox' || t === 'radio' || t === 'submit' || t === 'button' || t === 'file' || t === 'search') return null;
    const ac = (i.autocomplete || '').toLowerCase();
    let lab = '';
    try { if (i.labels && i.labels.length) lab = Array.from(i.labels).map(l => l.textContent).join(' '); } catch (_) {}
    const n = ((i.name || '') + ' ' + (i.id || '') + ' ' + (i.placeholder || '') + ' ' + (i.getAttribute('aria-label') || '') + ' ' + lab).toLowerCase();
    if (/cvc|cvv|csc|security.?code|kod.?zabezp/.test(ac + ' ' + n)) return null;   // CVC nigdy
    if (ac.includes('cc-number') || /card.?number|cardnumber|nr.?karty|numer.?karty/.test(n)) return ['card', 'number'];
    if (ac.includes('cc-name') || /name.?on.?card|card.?holder|cardholder|w[lł]a[sś]ciciel.?karty/.test(n)) return ['card', 'holder'];
    if (ac.includes('cc-exp-month') || /exp.{0,6}month|month.{0,6}exp|miesi[aą]c.?wa[zż]n/.test(n)) return ['card', 'expMonth'];
    if (ac.includes('cc-exp-year') || /exp.{0,6}year|year.{0,6}exp|rok.?wa[zż]n/.test(n)) return ['card', 'expYear'];
    if (ac.includes('cc-exp') || /expir|expiry|exp.?date|data.?wa[zż]n|wa[zż]na.?do/.test(n)) return ['card', 'exp'];
    if (/\biban\b/.test(n)) return ['bank', 'iban'];
    if (/sort.?code|sortcode/.test(n)) return ['bank', 'sortCode'];
    if (/account.?number|accountnumber|nr.?konta|numer.?konta|rachunk/.test(n)) return ['bank', 'account'];
    if (/swift|\bbic\b/.test(n)) return ['bank', 'swift'];
    if (ac.includes('email') || t === 'email' || /e-?mail/.test(n)) return ['address', 'email'];
    if (ac.includes('tel') || t === 'tel' || /telefon|phone|mobile|kom[oó]rk/.test(n)) return ['address', 'phone'];
    if (ac.includes('postal-code') || /kod.?poczt|zip|postal|postcode|post.?code/.test(n)) return ['address', 'postal'];
    if (ac.includes('address-line2') || /line.?2|address2|nr.?lokalu|mieszkani|\bapt\b|flat/.test(n)) return ['address', 'line2'];
    if (ac.includes('address-line1') || ac.includes('street-address') || /address.?1|line.?1|adres|street|ulic|address/.test(n)) return ['address', 'line1'];
    if (ac.includes('address-level2') || /miasto|miejscowo|city|town/.test(n)) return ['address', 'city'];
    if (ac.includes('address-level1') || /wojew|county|region|state|province/.test(n)) return ['address', 'region'];
    if (ac.includes('country') || /kraj|country/.test(n)) return ['address', 'country'];
    if (ac.includes('given-name') || /first.?name|firstname|given|imi[eę](?!.*nazw)/.test(n)) return ['address', 'first'];
    if (ac.includes('family-name') || /last.?name|lastname|surname|nazwisk/.test(n)) return ['address', 'last'];
    if (ac === 'name' || /full.?name|fullname|imi[eę].{0,3}i.{0,3}nazw/.test(n)) return ['address', 'name'];
    if (ac.includes('organization') || /firma|company/.test(n)) return ['address', 'company'];
    return null;
  }
  const visible = i => { try { const r = i.getBoundingClientRect(); const cs = getComputedStyle(i); return !i.disabled && !i.readOnly && r.width > 0 && r.height > 0 && cs.display !== 'none' && cs.visibility !== 'hidden'; } catch (_) { return false; } };
  const fields = root => Array.from((root || document).querySelectorAll('input, textarea, select')).filter(visible);
  function setVal(i, v) {
    v = String(v);
    if (i instanceof HTMLSelectElement) {
      const norm = v.trim().toLowerCase();
      let o = Array.from(i.options).find(x => x.value.trim().toLowerCase() === norm || x.textContent.trim().toLowerCase() === norm);
      if (!o && /^\d+$/.test(norm)) { const num = String(parseInt(norm, 10)); o = Array.from(i.options).find(x => /^\d+$/.test(x.value.trim()) && String(parseInt(x.value.trim(), 10)) === num); }
      if (!o) return;
      v = o.value;
    }
    const proto = i instanceof HTMLSelectElement ? HTMLSelectElement.prototype : i instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
    if (setter) setter.call(i, v); else i.value = v;
    i.dispatchEvent(new Event('input', { bubbles: true }));
    i.dispatchEvent(new Event('change', { bubbles: true }));
  }
  function valueFor(type, key, data) {
    if (data[key] != null && String(data[key]).trim()) return data[key];
    if (type === 'address' && key === 'name' && (data.first || data.last)) return ((data.first || '') + ' ' + (data.last || '')).trim();
    if (type === 'address' && (key === 'first' || key === 'last') && data.name) { const p = String(data.name).trim().split(/\s+/); return key === 'first' ? p[0] : p.slice(1).join(' '); }
    if (type === 'card' && key === 'exp' && data.expMonth && data.expYear) return String(data.expMonth).padStart(2, '0') + '/' + String(data.expYear).slice(-2);
    if (type === 'card' && key === 'expMonth' && data.exp) return String(data.exp).split(/[\/\-. ]/)[0];
    if (type === 'card' && key === 'expYear' && data.exp) return String(data.exp).split(/[\/\-. ]/).pop();
    return null;
  }
  // wywolywane przez Velivo z danymi jednego rodzaju; wypelnia tylko puste pola
  window.__velivoAutofillFill = (type, data, anchor) => {
    try {
      const root = (anchor && anchor.form) || document;
      for (const i of fields(root)) {
        const k = keyFor(i);
        if (!k || k[0] !== type || (i.value || '').trim()) continue;
        const v = valueFor(type, k[1], data || {});
        if (v != null && String(v).trim()) setVal(i, v);
      }
    } catch (_) {}
  };
  let lastAsk = '';
  document.addEventListener('focusin', e => {
    const k = keyFor(e.target);
    if (!k || (e.target.value || '').trim()) return;
    const tag = k[0] + '|' + location.pathname;
    if (tag === lastAsk) return;
    lastAsk = tag;
    window.__velivoAutofillAnchor = e.target;
    send('affill:' + k[0]);
  }, true);
  function collect(root) {
    const out = { address: {}, card: {}, bank: {} };
    for (const i of fields(root)) {
      const k = keyFor(i);
      if (!k) continue;
      let v = (i instanceof HTMLSelectElement ? (i.value || '') : (i.value || '')).trim();
      if (!v) continue;
      if (k[0] === 'card' && k[1] === 'number') { v = v.replace(/[\s-]+/g, ''); if (!/^\d{12,19}$/.test(v)) continue; }
      out[k[0]][k[1]] = v;
    }
    return out;
  }
  let lastSent = '';
  function capture(el) {
    try {
      const d = collect((el && el.form) || document);
      const j = JSON.stringify(d);
      if (j === lastSent) return;
      if (Object.keys(d.address).length >= 2 || Object.keys(d.card).length >= 2 || Object.keys(d.bank).length >= 1) { lastSent = j; send('afsave:' + j); }
    } catch (_) {}
  }
  document.addEventListener('submit', e => capture(e.target.querySelector('input,select,textarea')), true);
  document.addEventListener('click', e => {
    const t = e.target && e.target.closest ? e.target.closest('button,input[type=submit],[role=button]') : null;
    if (!t) return;
    const text = (t.innerText || t.value || t.getAttribute('aria-label') || '').trim();
    if (t.type === 'submit' || /zapisz|save|dalej|next|continue|kontynuuj|zap[lł]a[cć]|pay|kup|buy|zam[oó]w|order|wy[sś]lij|submit|potwierd|confirm|checkout/i.test(text)) capture(t);
  }, true);
  document.addEventListener('keydown', e => { if (e.key === 'Enter' && keyFor(e.target)) capture(e.target); }, true);
} catch (_) {}
})();";

        async Task HookAutofill(BrowserTab tab, CoreWebView2 core)
        {
            if (tab == null || core == null) return;
            try { await core.AddScriptToExecuteOnDocumentCreatedAsync(AutofillPageScript.Replace("__VT_TOKEN__", PageToken)); }
            catch (Exception ex) { App.LogError(ex); }
        }

        // strona prosi o dane: wpisy tej domeny, a gdy ich brak - ostatnio uzywane (adres jest ten sam na kazdej stronie)
        async Task HandleAutofillRequest(BrowserTab tab, string type)
        {
            try
            {
                var core = tab?.View.CoreWebView2;
                if (core == null || _settings == null || !_settings.Autofill) return;
                if (type != "address" && type != "card" && type != "bank") return;
                if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return;
                var entry = FindAutofillEntry(u.Host.ToLowerInvariant(), type);
                if (entry == null || entry.Values == null || entry.Values.Count == 0) return;
                var json = JsonSerializer.Serialize(entry.Values);
                await core.ExecuteScriptAsync("window.__velivoAutofillFill && window.__velivoAutofillFill(" + JsonSerializer.Serialize(type) + ", " + json + ", window.__velivoAutofillAnchor);");
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        // strona zglasza wyslany formularz
        void HandleAutofillSave(BrowserTab tab, string payload)
        {
            try
            {
                var core = tab?.View.CoreWebView2;
                if (core == null || _settings == null || !_settings.Autofill) return;   // takze w prywatnych - zapis tylko po Twojej zgodzie
                if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return;
                var host = u.Host.ToLowerInvariant();
                using (var doc = JsonDocument.Parse(payload))
                {
                    var root = doc.RootElement;
                    var a = ReadMap(root, "address");
                    if (a.Count >= 2) MaybePromptAndSave(host, "address", a, L.T("Wykryto wypełniony formularz adresowy."));
                    var c = ReadMap(root, "card");
                    if (c.Count >= 2 && c.ContainsKey("number")) MaybePromptAndSave(host, "card", c, L.T("Wykryto dane karty płatniczej."));
                    var b = ReadMap(root, "bank");
                    if (b.Count >= 1) MaybePromptAndSave(host, "bank", b, L.T("Wykryto dane konta bankowego."));
                }
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        static Dictionary<string, string> ReadMap(JsonElement root, string prop)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            JsonElement e;
            if (!root.TryGetProperty(prop, out e) || e.ValueKind != JsonValueKind.Object) return map;
            foreach (var p in e.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String) continue;
                var v = (p.Value.GetString() ?? "").Trim();
                if (v.Length > 0) map[p.Name] = v;
            }
            return map;
        }

        void MaybePromptAndSave(string host, string type, Dictionary<string, string> vals, string title)
        {
            if (vals == null || vals.Count < 2) return;
            EnsureAutofillLoaded();
            var existing = FindAutofillEntry(host, type);
            if (existing != null && SameValues(existing.Values, vals)) return;

            string key = host + "|" + type;
            DateTime last;
            if (_autofillPrompted.TryGetValue(key, out last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(2)) return;
            _autofillPrompted[key] = DateTime.UtcNow;

            var ans = MessageBox.Show(this,
                title + L.T("\n\nStrona: ") + host +
                L.T("\nZapisać lokalnie w szyfrowanej bazie offline Velivo?\n\nKod CVC nigdy nie jest zapisywany."),
                L.T("Autouzupełnianie"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ans != MessageBoxResult.Yes) return;

            UpsertAutofill(host, type, vals);
            SaveAutofillVault();
        }

        static bool SameValues(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            foreach (var kv in a)
            {
                string v;
                if (!b.TryGetValue(kv.Key, out v)) return false;
                if (!string.Equals((kv.Value ?? "").Trim(), (v ?? "").Trim(), StringComparison.Ordinal)) return false;
            }
            return true;
        }

        AutofillEntry FindAutofillEntry(string host, string type)
        {
            EnsureAutofillLoaded();
            host = (host ?? "").ToLowerInvariant();
            var exact = _autofillEntries.FirstOrDefault(e => string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase) && string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            return _autofillEntries
                .Where(e => string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase) && host.EndsWith("." + e.Host, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.Host.Length)
                .FirstOrDefault()
                // adres/karta/konto sa te same na kazdej stronie - ostatnio zapisane
                ?? _autofillEntries.Where(e => string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase) && e.Values != null && e.Values.Count > 0)
                    .OrderByDescending(e => e.UpdatedUnix).FirstOrDefault();
        }

        void UpsertAutofill(string host, string type, Dictionary<string, string> vals)
        {
            EnsureAutofillLoaded();
            host = (host ?? "").ToLowerInvariant();
            var e = _autofillEntries.FirstOrDefault(x => string.Equals(x.Type, type, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Host, host, StringComparison.OrdinalIgnoreCase));
            if (e == null)
            {
                e = new AutofillEntry { Host = host, Type = type, Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) };
                _autofillEntries.Add(e);
            }
            e.Values = new Dictionary<string, string>(vals, StringComparer.OrdinalIgnoreCase);
            e.UpdatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        static string MaskCardValue(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var v = value.Trim();
            if (string.Equals(key, "number", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "iban", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "account", StringComparison.OrdinalIgnoreCase))
            {
                var digits = new string(v.Where(char.IsDigit).ToArray());
                if (digits.Length <= 4) return digits;
                return new string('*', Math.Max(0, digits.Length - 4)) + digits.Substring(digits.Length - 4);
            }
            if (string.Equals(key, "cvc", StringComparison.OrdinalIgnoreCase)) return "***";
            return v;
        }

        string BuildAutofillSummaryText()
        {
            EnsureAutofillLoaded();
            if (_autofillEntries.Count == 0)
                return L.T("Brak zapisanych danych autouzupelniania.");

            var sb = new StringBuilder();
            sb.AppendLine("Zapisane dane autouzupelniania (lokalnie, szyfrowane):");
            sb.AppendLine();

            var byType = _autofillEntries
                .OrderBy(e => e.Type, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Host, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var e in byType)
            {
                var typeName = string.Equals(e.Type, "card", StringComparison.OrdinalIgnoreCase) ? L.T("Karta") : string.Equals(e.Type, "bank", StringComparison.OrdinalIgnoreCase) ? L.T("Konto bankowe") : L.T("Adres");
                var updated = DateTimeOffset.FromUnixTimeSeconds(e.UpdatedUnix).LocalDateTime;
                sb.AppendLine(typeName + " | " + e.Host + " | zapis: " + updated.ToString("yyyy-MM-dd HH:mm"));
                foreach (var kv in e.Values.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var val = string.Equals(e.Type, "card", StringComparison.OrdinalIgnoreCase) || string.Equals(e.Type, "bank", StringComparison.OrdinalIgnoreCase)
                        ? MaskCardValue(kv.Key, kv.Value)
                        : (kv.Value ?? "").Trim();
                    sb.AppendLine("  - " + kv.Key + ": " + val);
                }
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        int DeleteAutofillEntriesByType(string type)
        {
            EnsureAutofillLoaded();
            int before = _autofillEntries.Count;
            _autofillEntries = _autofillEntries
                .Where(e => !string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase))
                .ToList();
            int removed = before - _autofillEntries.Count;
            if (removed > 0) SaveAutofillVault();
            return removed;
        }

        void OpenAutofillDataViewer(Window owner)
        {
            try
            {
                var txt = new TextBox
                {
                    Text = BuildAutofillSummaryText(),
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    Margin = new Thickness(12),
                    MinHeight = 340,
                    MinWidth = 620
                };

                var w = new Window
                {
                    Title = L.T("Zapisane dane autouzupelniania"),
                    Owner = owner ?? this,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = txt,
                    Width = 760,
                    Height = 520,
                    MinWidth = 640,
                    MinHeight = 420
                };
                w.ShowDialog();
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        void DeleteAutofillAddresses(Window owner)
        {
            if (MessageBox.Show(owner ?? this,
                L.T("Usunac wszystkie zapisane adresy z lokalnej bazy autouzupelniania?"),
                L.T("Autouzupelnianie"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            try
            {
                int removed = DeleteAutofillEntriesByType("address");
                MessageBox.Show(owner ?? this, L.T("Usunieto pozycji adresowych: ") + removed + ".", L.T("Autouzupelnianie"));
            }
            catch (Exception ex) { MessageBox.Show(owner ?? this, ex.Message, L.T("Autouzupelnianie")); }
        }

        void DeleteAutofillCards(Window owner)
        {
            if (MessageBox.Show(owner ?? this,
                L.T("Usunac wszystkie zapisane karty z lokalnej bazy autouzupelniania?"),
                L.T("Autouzupelnianie"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            try
            {
                int removed = DeleteAutofillEntriesByType("card");
                MessageBox.Show(owner ?? this, L.T("Usunieto pozycji kart: ") + removed + ".", L.T("Autouzupelnianie"));
            }
            catch (Exception ex) { MessageBox.Show(owner ?? this, ex.Message, L.T("Autouzupelnianie")); }
        }
    }
}
