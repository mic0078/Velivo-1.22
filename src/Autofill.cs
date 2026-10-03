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

        void HookAutofill(BrowserTab tab, CoreWebView2 core)
        {
            if (tab == null || core == null || tab.Private) return;

            core.NavigationStarting += (s, e) =>
            {
                if (_settings == null || !_settings.Autofill) return;
                _ = CaptureAutofillFromPage(core);
            };

            core.NavigationCompleted += (s, e) =>
            {
                if (!e.IsSuccess || _settings == null || !_settings.Autofill) return;
                _ = ApplyAutofillToPage(core);
            };
        }

        async Task CaptureAutofillFromPage(CoreWebView2 core)
        {
            try
            {
                var host = HostFromUrl(core.Source);
                if (host.Length == 0) return;
                if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var pageUri) || pageUri.Scheme != Uri.UriSchemeHttps) return;

                string js = @"(() => {
  function txt(v){ return (v||'').toString().trim(); }
  function keyFor(i){
    const ac = (i.autocomplete||'').toLowerCase();
    const n = ((i.name||'') + ' ' + (i.id||'') + ' ' + (i.placeholder||'')).toLowerCase();
    if (ac.includes('cc-number') || /card.?number|nr.?karty|numer.?karty|\bcc\b/.test(n)) return ['card','number'];
    if (ac.includes('cc-name') || /name.?on.?card|holder|imie.*nazwisko.*karty/.test(n)) return ['card','holder'];
    if (ac.includes('cc-exp-month') || /exp.?month|month.?exp/.test(n)) return ['card','expMonth'];
    if (ac.includes('cc-exp-year') || /exp.?year|year.?exp/.test(n)) return ['card','expYear'];
    if (ac.includes('cc-exp') || /exp|expiry|ważn|wazn/.test(n)) return ['card','exp'];

    if (ac.includes('email') || /e-?mail/.test(n)) return ['address','email'];
    if (ac.includes('tel') || /telefon|phone/.test(n)) return ['address','phone'];
    if (ac.includes('postal-code') || /kod|zip|postal/.test(n)) return ['address','postal'];
    if (ac.includes('address-line1') || /adres|street|ulic/.test(n)) return ['address','line1'];
    if (ac.includes('address-line2') || /line2|lokal|apt|mieszkan/.test(n)) return ['address','line2'];
    if (ac.includes('address-level2') || /miasto|city/.test(n)) return ['address','city'];
    if (ac.includes('country') || /kraj|country/.test(n)) return ['address','country'];
    if (ac.includes('name') || /imię|imie|nazw|full.?name/.test(n)) return ['address','name'];
    return null;
  }

  const out = { address: {}, card: {}, countA: 0, countC: 0 };
    const inputs = Array.from(document.querySelectorAll('input, textarea, select')).filter(i => {
        const r = i.getBoundingClientRect();
        const s = getComputedStyle(i);
        return !i.disabled && i.type !== 'hidden' && i.type !== 'password' && r.width > 0 && r.height > 0 && s.display !== 'none' && s.visibility !== 'hidden';
    });
  for (const i of inputs) {
    const v = txt(i.value);
    if (!v) continue;
    const k = keyFor(i);
    if (!k) continue;
    if (k[0] === 'card') {
      out.card[k[1]] = k[1] === 'number' ? v.replace(/\s+/g, '') : v;
      out.countC++;
    } else {
      out.address[k[1]] = v;
      out.countA++;
    }
  }
  return JSON.stringify(out);
})();";

                var raw = await core.ExecuteScriptAsync(js);
                var payload = JsonSerializer.Deserialize<string>(raw);
                if (string.IsNullOrWhiteSpace(payload)) return;

                using (var doc = JsonDocument.Parse(payload))
                {
                    var root = doc.RootElement;
                    int countA = root.TryGetProperty("countA", out var aEl) ? aEl.GetInt32() : 0;
                    int countC = root.TryGetProperty("countC", out var cEl) ? cEl.GetInt32() : 0;

                    if (countA >= 2)
                    {
                        var vals = ReadMap(root, "address");
                        MaybePromptAndSave(host, "address", vals, L.T("Wykryto wypełniony formularz adresowy."));
                    }

                    if (countC >= 2 && core.Source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        var vals = ReadMap(root, "card");
                        MaybePromptAndSave(host, "card", vals, L.T("Wykryto dane karty płatniczej."));
                    }
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
                L.T("\nZapisać lokalnie w szyfrowanej bazie offline Velivo?\n\nHasła nadal obsługuje Sejf."),
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
                .FirstOrDefault();
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
            if (string.Equals(key, "number", StringComparison.OrdinalIgnoreCase))
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
                var typeName = string.Equals(e.Type, "card", StringComparison.OrdinalIgnoreCase) ? L.T("Karta") : L.T("Adres");
                var updated = DateTimeOffset.FromUnixTimeSeconds(e.UpdatedUnix).LocalDateTime;
                sb.AppendLine(typeName + " | " + e.Host + " | zapis: " + updated.ToString("yyyy-MM-dd HH:mm"));
                foreach (var kv in e.Values.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var val = string.Equals(e.Type, "card", StringComparison.OrdinalIgnoreCase)
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

        async Task ApplyAutofillToPage(CoreWebView2 core)
        {
            try
            {
                if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var pageUri) || pageUri.Scheme != Uri.UriSchemeHttps) return;
                var host = pageUri.Host.ToLowerInvariant();
                if (host.Length == 0) return;

                var addr = FindAutofillEntry(host, "address");
                var card = FindAutofillEntry(host, "card");
                if ((addr == null || addr.Values.Count == 0) && (card == null || card.Values.Count == 0)) return;

                var payload = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                payload["address"] = addr != null ? addr.Values : new Dictionary<string, string>();
                payload["card"] = card != null ? card.Values : new Dictionary<string, string>();
                var json = JsonSerializer.Serialize(payload);

                string js = @"(() => {
  const data = " + json + @";
  const addr = data.address || {};
  const card = data.card || {};
  function keyFor(i){
    const ac = (i.autocomplete||'').toLowerCase();
    const n = ((i.name||'') + ' ' + (i.id||'') + ' ' + (i.placeholder||'')).toLowerCase();
    if (ac.includes('cc-number') || /card.?number|nr.?karty|numer.?karty|\bcc\b/.test(n)) return ['card','number'];
    if (ac.includes('cc-name') || /name.?on.?card|holder|imie.*nazwisko.*karty/.test(n)) return ['card','holder'];
    if (ac.includes('cc-exp-month') || /exp.?month|month.?exp/.test(n)) return ['card','expMonth'];
    if (ac.includes('cc-exp-year') || /exp.?year|year.?exp/.test(n)) return ['card','expYear'];
    if (ac.includes('cc-exp') || /exp|expiry|ważn|wazn/.test(n)) return ['card','exp'];
    if (ac.includes('email') || /e-?mail/.test(n)) return ['address','email'];
    if (ac.includes('tel') || /telefon|phone/.test(n)) return ['address','phone'];
    if (ac.includes('postal-code') || /kod|zip|postal/.test(n)) return ['address','postal'];
    if (ac.includes('address-line1') || /adres|street|ulic/.test(n)) return ['address','line1'];
    if (ac.includes('address-line2') || /line2|lokal|apt|mieszkan/.test(n)) return ['address','line2'];
    if (ac.includes('address-level2') || /miasto|city/.test(n)) return ['address','city'];
    if (ac.includes('country') || /kraj|country/.test(n)) return ['address','country'];
    if (ac.includes('name') || /imię|imie|nazw|full.?name/.test(n)) return ['address','name'];
    return null;
  }
    function fill(i, v){
        if (v == null || String(v).trim().length === 0 || (i.value||'').trim().length > 0) return;
        v = String(v);
        if (i instanceof HTMLSelectElement) {
            const normalized = v.trim().toLowerCase();
            let option = Array.from(i.options).find(o => o.value.trim().toLowerCase() === normalized || o.textContent.trim().toLowerCase() === normalized);
            if (!option && /^\d+$/.test(normalized)) {
                const number = String(parseInt(normalized, 10));
                option = Array.from(i.options).find(o => /^\d+$/.test(o.value.trim()) && String(parseInt(o.value.trim(), 10)) === number);
            }
            if (!option) return;
            v = option.value;
        }
        const proto = i instanceof HTMLSelectElement ? HTMLSelectElement.prototype : i instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
        const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
        if (setter) setter.call(i, v); else i.value = v;
    i.dispatchEvent(new Event('input', { bubbles: true }));
    i.dispatchEvent(new Event('change', { bubbles: true }));
  }
    for (const i of Array.from(document.querySelectorAll('input, textarea, select'))){
        const r = i.getBoundingClientRect(); const s = getComputedStyle(i);
        if (i.disabled || i.type === 'hidden' || i.type === 'password' || r.width === 0 || r.height === 0 || s.display === 'none' || s.visibility === 'hidden') continue;
    const k = keyFor(i); if (!k) continue;
    fill(i, k[0] === 'card' ? card[k[1]] : addr[k[1]]);
  }
})();";

                await core.ExecuteScriptAsync(js);
            }
            catch (Exception ex) { App.LogError(ex); }
        }
    }
}
