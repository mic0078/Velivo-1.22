using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace Velivo
{
    // Telefon (Velivo na Androida) dodaje zakladki i hasla: wysyla tylko to, czego komputer jeszcze nie ma,
    // zaszyfrowane kluczem z parowania (jak "tab"). Komputer jest glowny - nic nie dodaje bez potwierdzenia.
    public partial class MainWindow
    {
        // 🔄 nauka klawiatury Velivo telefon <-> komputer (zaszyfrowana kluczem z parowania)
        sealed class LanKbSync
        {
            public string to { get; set; }
            public bool reply { get; set; }
            public List<string> own { get; set; }
            public List<string> nofix { get; set; }
            public Dictionary<string, int> pairs { get; set; }
            public Dictionary<string, string> fix { get; set; }
        }
        void ReceiveKbSync(LanStatePacket pkt, LanKbSync k)
        {
            if (k == null || k.to != _lanId || !SwipeTyping.Loaded) return;   // slownik niewczytany - nic nie zapisujemy (bez nadpisania nauki)
            lock (_swLearned)
            {
                foreach (var w0 in (k.own ?? new List<string>()).Take(5000)) { var w = (w0 ?? "").Trim().ToLowerInvariant(); if (w.Length is >= 2 and <= 40 && !w.Contains(' ') && _swOwn.Add(w)) { SwipeTyping.AddUserWord(w); if (!_swLearned.ContainsKey(w)) _swLearned[w] = 1; } }
                SwipeTyping.SyncMerge(k.pairs, k.fix);
                SwipeTyping.SaveLearned(SwipeFile, _swLearned, _swOwn);
            }
            var nf = new HashSet<string>((GetPref("klawiatura-bez-korekty") ?? "").Split('|').Where(x => x.Length > 0));
            foreach (var w in k.nofix ?? new List<string>()) if (w?.Length is >= 2 and <= 40) nf.Add(w);
            SetPref("klawiatura-bez-korekty", string.Join("|", nf.Skip(Math.Max(0, nf.Count - 500))));
            if (k.reply) return;
            LanKbSync mine;   // odpowiedz: nauka tego komputera dla telefonu
            lock (_swLearned) mine = new LanKbSync { to = pkt.id, reply = true, own = _swOwn.ToList(), nofix = nf.ToList(), pairs = SwipeTyping.SyncPairs(), fix = SwipeTyping.SyncFixes() };
            var outPkt = new LanStatePacket { t = "kb", id = _lanId, device = _lanDeviceName, profile = SelectedProfileName, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), hash = "" };
            EncryptLanBlob(outPkt, JsonSerializer.SerializeToUtf8Bytes(mine));
            LanSend(outPkt);
        }

        sealed class LanPhoneAdd
        {
            public string to { get; set; }
            public List<LanPhoneBookmark> bookmarks { get; set; }
            public List<LanPhonePassword> passwords { get; set; }
        }
        sealed class LanPhoneBookmark { public string url { get; set; } public string title { get; set; } }
        sealed class LanPhonePassword { public string host { get; set; } public string user { get; set; } public string pass { get; set; } }

        // paczki od jednego telefonu zbierane przez 2 s - jedno pytanie zamiast kilku
        readonly Dictionary<string, (string Device, List<LanPhoneBookmark> B, List<LanPhonePassword> P)> _phonePending =
            new Dictionary<string, (string, List<LanPhoneBookmark>, List<LanPhonePassword>)>(StringComparer.OrdinalIgnoreCase);
        DispatcherTimer _phoneTimer;
        bool _phoneAsking;

        void ReceivePhoneAdd(LanStatePacket pkt, LanPhoneAdd p)
        {
            if (p == null || p.to != _lanId) return;
            if (!_phonePending.TryGetValue(pkt.id, out var acc))
                acc = (PairDeviceLabel(pkt.device), new List<LanPhoneBookmark>(), new List<LanPhonePassword>());
            foreach (var b in (p.bookmarks ?? new List<LanPhoneBookmark>()).Take(500))
                if (b != null && b.url != null && b.url.Length <= 4000 && (b.url.StartsWith("http://") || b.url.StartsWith("https://"))) acc.B.Add(b);
            foreach (var x in (p.passwords ?? new List<LanPhonePassword>()).Take(500))
                if (x != null && !string.IsNullOrWhiteSpace(x.host) && !string.IsNullOrEmpty(x.pass) && x.host.Length <= 255 && (x.user ?? "").Length <= 500 && x.pass.Length <= 500) acc.P.Add(x);
            _phonePending[pkt.id] = acc;
            if (_phoneTimer == null)
            {
                _phoneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _phoneTimer.Tick += (s, e) => { _phoneTimer.Stop(); AskPhoneAdds(); };
            }
            _phoneTimer.Stop();
            _phoneTimer.Start();
        }

        void AskPhoneAdds()
        {
            if (_phoneAsking) { _phoneTimer.Start(); return; }   // poprzednie pytanie jeszcze otwarte
            foreach (var id in _phonePending.Keys.ToList())
            {
                var acc = _phonePending[id];
                _phonePending.Remove(id);
                // tylko to, czego tu nie ma (zakladka po adresie; haslo po stronie i loginie - inne haslo = zmiana)
                var have = new HashSet<string>(_bookmarks.Select(b => b.Url), StringComparer.Ordinal);
                var newBookmarks = acc.B.Where(b => have.Add(b.url)).ToList();
                EnsurePasswordVaultLoaded();
                var known = _passwordEntries.GroupBy(e => PasswordSyncIdentity(e), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Last().Password, StringComparer.OrdinalIgnoreCase);
                var newPasswords = new List<LanPhonePassword>();
                int changed = 0;
                foreach (var x in acc.P.GroupBy(x => x.host.Trim().ToLowerInvariant() + "\n" + (x.user ?? "").Trim().ToLowerInvariant()).Select(g => g.Last()))
                {
                    var identity = PasswordSyncIdentity(new SavedPasswordEntry { Host = x.host, Username = x.user, Url = "https://" + x.host });
                    if (known.TryGetValue(identity, out var old)) { if (old == x.pass) continue; changed++; }
                    newPasswords.Add(x);
                }
                if (newBookmarks.Count == 0 && newPasswords.Count == 0) continue;

                var list = newBookmarks.Take(15).Select(b => "🔖 " + (string.IsNullOrWhiteSpace(b.title) ? b.url : b.title))
                    .Concat(newPasswords.Take(15).Select(x => "🔑 " + x.host + " – " + (string.IsNullOrEmpty(x.user) ? "—" : x.user)))
                    .ToList();
                int more = newBookmarks.Count + newPasswords.Count - list.Count;
                var text = (L.En ? "The phone " : "Telefon ") + acc.Device + (L.En ? " wants to add:\n\n" : " chce dodać:\n\n") +
                    (L.En ? "Bookmarks: " : "Zakładki: ") + newBookmarks.Count + "\n" +
                    (L.En ? "Passwords: " : "Hasła: ") + newPasswords.Count + (changed > 0 ? (L.En ? " (changed: " : " (zmienione: ") + changed + ")" : "") + "\n\n" +
                    string.Join("\n", list) + (more > 0 ? "\n… " + (L.En ? "and " : "i ") + more + (L.En ? " more" : " więcej") : "") +
                    (L.En ? "\n\nAdd them to this computer?" : "\n\nDodać je na tym komputerze?");
                _phoneAsking = true;
                MessageBoxResult ans;
                try { ans = MessageBox.Show(PairDialogOwner(), text, L.En ? "Velivo – from the phone" : "Velivo – z telefonu", MessageBoxButton.YesNo, MessageBoxImage.Question); }
                finally { _phoneAsking = false; }
                if (ans != MessageBoxResult.Yes) { LanLog(L.T("Odrzucono dane z telefonu ") + acc.Device + "."); continue; }

                if (newBookmarks.Count > 0)
                {
                    foreach (var b in newBookmarks) _bookmarks.Add(new Bookmark { Url = b.url, Title = string.IsNullOrWhiteSpace(b.title) ? b.url : b.title });
                    SaveBookmarks();
                    RenderBookmarkBar();
                }
                if (newPasswords.Count > 0)
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    ImportPasswordsFromSync(JsonSerializer.Serialize(newPasswords.Select(x => new PasswordSyncEntry
                    {
                        Name = x.host, Url = "https://" + x.host, Host = x.host, Username = x.user ?? "", Password = x.pass,
                        Source = "Velivo Android", UpdatedUnix = now
                    }).ToList()), true);
                }
                LanLog(L.T("Dodano z telefonu ") + acc.Device + ": " + newBookmarks.Count + " / " + newPasswords.Count + ".");
                ShowToast((L.En ? "📱 Added from the phone: " : "📱 Dodano z telefonu: ") + newBookmarks.Count + (L.En ? " bookmarks, " : " zakładek, ") + newPasswords.Count + (L.En ? " passwords" : " haseł"), null);
            }
        }
    }
}
