using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Przegladarka
{
    // Tryb bankowy: osobny, odizolowany profil przegladarki na banki i zakupy.
    //  - wlasne ciasteczka / logowania / pamiec (zwykle karty nic z niego nie widza i odwrotnie),
    //  - bez dodatkow (dodatki sa wgrywane tylko do zwyklego profilu), bez historii i bez zapisu sesji,
    //  - wejscie chronione haslem, a opcjonalnie dodatkowo kluczem sprzetowym (YubiKey, Google Titan...) -
    //    klucz obsluguje sam Windows (WebAuthn), Velivo sprawdza podpis klucza,
    //  - blokuje sie sam po 10 min bezczynnosci i po zamknieciu ostatniej karty bankowej.
    public partial class MainWindow
    {
        const string BankRpId = "velivo.local";
        // Kilka profili bankowych (np. dla innego uzytkownika): kazdy ma wlasny plik, wlasne haslo/klucze,
        // wlasna zaszyfrowana baze i wlasny profil przegladarki (osobne logowania w bankach).
        string _bankProfile = "";   // "" = profil glowny (bank.json)
        static string BankFileFor(string slug) { return Path.Combine(DataDir, string.IsNullOrEmpty(slug) ? "bank.json" : "bank-" + slug + ".json"); }
        string BankFile { get { return BankFileFor(_bankProfile); } }
        string BankProfileName { get { return string.IsNullOrEmpty(_bankProfile) ? "VelivoBank" : "VelivoBank-" + _bankProfile; } }
        static readonly System.Text.RegularExpressions.Regex BankFileRx = new System.Text.RegularExpressions.Regex(@"^bank(-[a-z0-9]{1,24})?\.json$");

        // wszystkie profile bankowe na tym komputerze: slug -> plik
        static List<string> BankProfiles()
        {
            var list = new List<string>();
            try
            {
                foreach (var f in Directory.GetFiles(DataDir, "bank*.json"))
                {
                    var n = Path.GetFileName(f);
                    if (!BankFileRx.IsMatch(n)) continue;
                    list.Add(n == "bank.json" ? "" : n.Substring(5, n.Length - 10));
                }
            }
            catch (Exception) { }
            return list.OrderBy(x => x).ToList();
        }

        static string BankOwnerOf(string slug)
        {
            try { var c = JsonSerializer.Deserialize<BankConfig>(File.ReadAllText(BankFileFor(slug))); if (c != null && !string.IsNullOrWhiteSpace(c.Owner)) return c.Owner; } catch (Exception) { }
            return string.IsNullOrEmpty(slug) ? L.T("Główny") : slug;
        }

        sealed class BankKey
        {
            public string Id { get; set; } public string X { get; set; } public string Y { get; set; } public string Name { get; set; }
            public string WrapK { get; set; }   // czesc klucza kart zaszyfrowana sekretem z tego klucza sprzetowego (null = klucz tylko otwiera tryb)
            public string WrapKp { get; set; }  // czesc z hasla (Kp) zaszyfrowana tym samym sekretem - sam klucz wystarcza do otwarcia
        }
        sealed class BankConfig
        {
            public string Owner { get; set; }      // nazwa profilu bankowego (np. imie uzytkownika)
            public string Salt { get; set; }
            public string Hash { get; set; }
            public int Iter { get; set; }
            public bool UseKey { get; set; }
            public List<BankKey> Keys { get; set; } = new List<BankKey>();
            public string CardSalt { get; set; }   // sol klucza sejfu kart (inna niz hasla)
            public string Cards { get; set; }      // karty zaszyfrowane AES-GCM
            public string Notes { get; set; }      // notatki (loginy, hasla, numery klienta) - tak samo zaszyfrowane
            public string Sites { get; set; }      // strony bankowe / sklepy (nazwa + adres) - zaszyfrowane
            public List<string> SiteHosts { get; set; } = new List<string>();   // skroty SHA-256 hostow (rozpoznanie strony bez ujawniania listy)
            public string WrapP { get; set; }      // czesc hasla klucza kart (Kp) zaszyfrowana kluczem z hasla
            public string WrapPK { get; set; }     // czesc z kluczy (Kk) zaszyfrowana Kp - samo haslo (zapasowo, bez klucza) tez otwiera
            public string HmacSalt { get; set; }   // sol dla sekretu z klucza sprzetowego (hmac-secret)
        }

        bool _bankUnlocked, _creatingBank;
        DateTime _bankLastInput = DateTime.UtcNow;
        System.Windows.Threading.DispatcherTimer _bankTimer;
        int _bankFails;
        Button _bankBtn;
        byte[] _bankKey;   // klucz sejfu kart - tylko w pamieci, gdy tryb jest odblokowany
        // Klucz kart = Kp (z hasla) albo SHA256(Kp + Kk), gdy karty chroni tez klucz sprzetowy (Kk odszyfrowuje
        // tylko sekret z fizycznego klucza - samo haslo wtedy nie wystarczy).
        byte[] _bankKp, _bankKk;

        static byte[] CardKeyFrom(byte[] kp, byte[] kk) { return kk == null ? (byte[])kp.Clone() : SHA256.HashData(kp.Concat(kk).ToArray()); }
        static byte[] KekFromHmac(byte[] hmac) { return HKDF.DeriveKey(HashAlgorithmName.SHA256, hmac, 32, null, Encoding.UTF8.GetBytes("velivo-bank-kk")); }

        static string Wrap(byte[] data, byte[] key)
        {
            var nonce = RandomNumberGenerator.GetBytes(12); var ct = new byte[data.Length]; var tag = new byte[16];
            using (var g = new AesGcm(key, 16)) g.Encrypt(nonce, data, ct, tag);
            return Convert.ToBase64String(nonce.Concat(ct).Concat(tag).ToArray());
        }

        static byte[] Unwrap(string b64, byte[] key)
        {
            var all = Convert.FromBase64String(b64);
            var nonce = all.Take(12).ToArray(); var tag = all.Skip(all.Length - 16).ToArray(); var ct = all.Skip(12).Take(all.Length - 28).ToArray();
            var plain = new byte[ct.Length];
            using (var g = new AesGcm(key, 16)) g.Decrypt(nonce, ct, tag, plain);
            return plain;
        }
        string BankWipeFlag { get { return Path.Combine(DataDir, string.IsNullOrEmpty(_bankProfile) ? "bank.wipe" : "bank-" + _bankProfile + ".wipe"); } }

        sealed class BankCard { public string Label { get; set; } public string Number { get; set; } public string Exp { get; set; } public string Holder { get; set; } }

        sealed class BankNote { public string Title { get; set; } public string Text { get; set; } }
        sealed class BankSite { public string Name { get; set; } public string Url { get; set; } public string Kind { get; set; } }   // Kind: "bank" / "shop"
        static bool IsShop(BankSite x) { return x.Kind == "shop"; }

        List<T> LoadSealed<T>(string sealedText)
        {
            if (_bankKey == null || string.IsNullOrEmpty(sealedText)) return new List<T>();
            var plain = Unwrap(sealedText, _bankKey);
            try { return JsonSerializer.Deserialize<List<T>>(plain) ?? new List<T>(); } finally { CryptographicOperations.ZeroMemory(plain); }
        }

        static string SealList<T>(byte[] key, List<T> items)
        {
            if (items == null || items.Count == 0) return null;
            var plain = JsonSerializer.SerializeToUtf8Bytes(items);
            try { return Wrap(plain, key); } finally { CryptographicOperations.ZeroMemory(plain); }
        }

        static string HostHash(string host) { return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("velivo-bank|" + (host ?? "").ToLowerInvariant().TrimStart('.').Replace("www.", "")))); }

        static byte[] CardKey(string pass, BankConfig c) { return BankHash(pass, Convert.FromBase64String(c.CardSalt), c.Iter); }

        List<BankCard> LoadCards(BankConfig c)
        {
            if (_bankKey == null || string.IsNullOrEmpty(c.Cards)) return new List<BankCard>();
            var plain = Unwrap(c.Cards, _bankKey);
            return JsonSerializer.Deserialize<List<BankCard>>(plain) ?? new List<BankCard>();
        }

        static string SealCards(byte[] key, List<BankCard> cards)
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(cards);
            try { return Wrap(plain, key); } finally { CryptographicOperations.ZeroMemory(plain); }
        }

        BankConfig LoadBank()
        {
            try { if (File.Exists(BankFile)) return JsonSerializer.Deserialize<BankConfig>(File.ReadAllText(BankFile)); }
            catch (Exception) { }
            return null;
        }

        void SaveBank(BankConfig c)
        {
            try { File.WriteAllText(BankFile, JsonSerializer.Serialize(c)); } catch (Exception ex) { App.LogError(ex); }
            try { NotifyLanStateChanged(); } catch (Exception) { }   // wyslij zmiane do sparowanych komputerow
        }

        static byte[] BankHash(string pass, byte[] salt, int iter)
        {
            return Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pass ?? ""), salt, iter, HashAlgorithmName.SHA256, 32);
        }

        // ---------- przycisk na pasku kart ----------
        void InitBankMode()
        {
            _bankBtn = new Button
            {
                Content = "🏦", FontSize = 16, Width = 40, Height = 36, Margin = new Thickness(2, 3, 2, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x06, 0x5F, 0x46)), Background = new SolidColorBrush(Color.FromRgb(0xD1, 0xFA, 0xE5)),
                ToolTip = L.T("Tryb bankowy – odizolowany, chroniony hasłem (i kluczem sprzętowym)\nPrawy klik: ustawienia i blokada")
            };
            DockPanel.SetDock(_bankBtn, Dock.Right);
            _bankBtn.Click += (s, e) => OpenBankTab();
            var menu = new ContextMenu();
            var cfg = new MenuItem { Header = L.T("⚙ Ustawienia trybu bankowego…") };
            cfg.Click += (s, e) => BankSettings();
            var cards = new MenuItem { Header = L.T("💳 Moje karty") };
            cards.Click += (s, e) => BankCards();
            var fill = new MenuItem { Header = L.T("💳 Wypełnij kartę na tej stronie") };
            fill.Click += (s, e) => BankFillMenu();
            var notes = new MenuItem { Header = L.T("📝 Moje notatki (loginy, hasła, numery klienta)") };
            notes.Click += (s, e) => BankNotes();
            var sites = new MenuItem { Header = L.T("🏦 Moje banki") };
            var shops = new MenuItem { Header = L.T("🛒 Moje sklepy online") };
            var addSite = new MenuItem { Header = L.T("➕ Dodaj tę stronę do Moich banków") };
            addSite.Click += (s, e) => BankAddCurrentSite(false);
            var addShop = new MenuItem { Header = L.T("➕ Dodaj tę stronę do Moich sklepów") };
            addShop.Click += (s, e) => BankAddCurrentSite(true);
            var profMenu = new MenuItem { Header = L.T("👤 Profile bankowe") };
            var lockNow = new MenuItem { Header = L.T("🔒 Zablokuj teraz") };
            lockNow.Click += (s, e) => LockBank(null);
            var reset = new MenuItem { Header = L.T("Zapomniałem hasła – wyczyść tryb bankowy…") };
            reset.Click += (s, e) => ResetBank();
            menu.Items.Add(sites); menu.Items.Add(shops); menu.Items.Add(addSite); menu.Items.Add(addShop); menu.Items.Add(new Separator()); menu.Items.Add(cards); menu.Items.Add(fill); menu.Items.Add(notes); menu.Items.Add(new Separator()); menu.Items.Add(cfg); menu.Items.Add(profMenu); menu.Items.Add(lockNow); menu.Items.Add(new Separator()); menu.Items.Add(reset);
            menu.Opened += (s, e) => { lockNow.IsEnabled = _bankUnlocked; fill.IsEnabled = _bankUnlocked && _current != null && _current.Bank; reset.IsEnabled = LoadBank() != null;
                addSite.IsEnabled = _bankUnlocked && _bankKey != null && _current != null && _current.Bank && HostOf(_current.View.CoreWebView2 != null ? _current.View.CoreWebView2.Source : null) != null;
                addShop.IsEnabled = addSite.IsEnabled;
                profMenu.Items.Clear();
                foreach (var p in BankProfiles())
                {
                    var slug = p;
                    var mi = new MenuItem { Header = "👤 " + BankOwnerOf(slug), IsCheckable = true, IsChecked = _bankUnlocked && slug == _bankProfile };
                    mi.Click += (a, b) => SwitchBankProfile(slug);
                    profMenu.Items.Add(mi);
                }
                if (profMenu.Items.Count > 0) profMenu.Items.Add(new Separator());
                var np = new MenuItem { Header = L.T("➕ Nowy profil bankowy (inny użytkownik)…") };
                np.Click += (a, b) => NewBankProfile();
                profMenu.Items.Add(np);
                FillBankSitesMenu(sites, false); FillBankSitesMenu(shops, true); };
            _bankBtn.ContextMenu = menu;
            TabBarPanel.Children.Insert(1, _bankBtn);

            PreviewMouseMove += (s, e) => _bankLastInput = DateTime.UtcNow;
            PreviewKeyDown += (s, e) => _bankLastInput = DateTime.UtcNow;
            _bankTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _bankTimer.Tick += (s, e) =>
            {
                if (!_bankUnlocked) return;
                if (!_tabs.Any(t => t.Bank)) { _bankUnlocked = false; ForgetBankKey(); return; }   // ostatnia karta bankowa zamknieta
                if (DateTime.UtcNow - _bankLastInput > TimeSpan.FromMinutes(10)) LockBank(L.T("🔒 Tryb bankowy zablokowany po 10 minutach bezczynności"));
            };
            _bankTimer.Start();
        }

        void LockBank(string toast)
        {
            _bankUnlocked = false; ForgetBankKey();
            foreach (var t in _tabs.Where(t => t.Bank).ToList()) CloseTab(t);
            ShowToast(toast ?? L.T("🔒 Tryb bankowy zablokowany"), null);
        }

        void ForgetBankKey()
        {
            foreach (var k in new[] { _bankKey, _bankKp, _bankKk }) if (k != null) CryptographicOperations.ZeroMemory(k);
            _bankKey = _bankKp = _bankKk = null;
        }

        void OpenBankTab() { OpenBankTabAt(HomeUrl); }

        async void OpenBankTabAt(string url)
        {
            if (!_bankUnlocked)
            {
                var profs = BankProfiles();
                if (profs.Count > 1)
                {
                    var pick = ChooseBankProfile(profs);
                    if (pick == null) return;
                    _bankProfile = pick;
                }
                else if (profs.Count == 1) _bankProfile = profs[0];
                var c = LoadBank();
                if (c == null) { if (!BankSetup(null)) return; c = LoadBank(); if (c == null) return; }
                else if (!await BankUnlock(c)) return;
            }
            _bankLastInput = DateTime.UtcNow;
            _creatingBank = true;
            try { AddTab(url, true); } finally { _creatingBank = false; }
        }

        // wywolywane z InitView dla karty bankowej - jednorazowe czyszczenie po resecie
        async Task BankAfterInit(Microsoft.Web.WebView2.Core.CoreWebView2 core)
        {
            if (!File.Exists(BankWipeFlag)) return;
            try { await core.Profile.ClearBrowsingDataAsync(); File.Delete(BankWipeFlag); } catch (Exception) { }
        }

        // ---------- odblokowanie ----------
        // Otwieranie: klucz sprzetowy ALBO haslo. Z kluczem (jesli dodany i podlaczony) haslo nie jest potrzebne;
        // haslo jest zapasowe - gdy klucza nie ma pod reka.
        async Task<bool> BankUnlock(BankConfig c)
        {
            var w = BankDialog(L.T("Tryb bankowy"));
            var sp = (StackPanel)w.Content;
            bool hasKeys = c.UseKey && c.Keys.Count > 0;
            Button keyBtn = null;
            TextBlock keyInfo = null;
            if (hasKeys)
            {
                keyBtn = new Button { Content = L.T("🔑 Otwórz kluczem sprzętowym"), Padding = new Thickness(12, 6, 12, 6), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Left };
                sp.Children.Add(keyBtn);
                keyInfo = new TextBlock { Margin = new Thickness(0, 6, 0, 12), TextWrapping = TextWrapping.Wrap, MaxWidth = 340, Text = L.T("Sprawdzam klucz sprzętowy…") };
                sp.Children.Add(keyInfo);
                sp.Children.Add(new TextBlock { Text = L.T("Nie masz klucza? Podaj hasło:"), Margin = new Thickness(0, 0, 0, 6) });
            }
            else sp.Children.Add(new TextBlock { Text = L.T("Podaj hasło trybu bankowego:"), Margin = new Thickness(0, 0, 0, 6) });
            var pass = new PasswordBox { Padding = new Thickness(6), MinWidth = 320 };
            sp.Children.Add(pass);
            var err = new TextBlock { Foreground = Brushes.Firebrick, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 340 };
            sp.Children.Add(err);
            var ok = BankButtons(sp, w, L.T("Otwórz hasłem"));
            bool result = false, busy = false;
            var hmacSalt = string.IsNullOrEmpty(c.HmacSalt) ? null : Convert.FromBase64String(c.HmacSalt);

            Func<Task<WebAuthn.VerifyResult>> touch = async () =>
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                var allowed = c.Keys.ToList();
                var r = await Task.Run(() => WebAuthn.Verify(hwnd, BankRpId, allowed, hmacSalt));
                if (r.Key == null) throw new InvalidOperationException(L.T("Ten klucz nie jest dodany do trybu bankowego."));
                return r;
            };
            Action done = () => { if (string.IsNullOrEmpty(c.HmacSalt)) c.HmacSalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); SaveBank(c); _bankFails = 0; _bankUnlocked = true; result = true; w.Close(); };

            // --- samym kluczem ---
            Func<Task> byKey = async () =>
            {
                if (busy) return; busy = true; if (keyBtn != null) keyBtn.IsEnabled = false; ok.IsEnabled = false;
                err.Text = L.T("Dotknij klucza sprzętowego (okienko Windows)…");
                try
                {
                    var r = await touch();
                    if (r.Key.WrapKp == null || r.Key.WrapK == null || r.Hmac == null || string.IsNullOrEmpty(c.WrapPK))
                    { err.Text = L.T("Ten klucz jeszcze nie otwiera trybu sam – podaj raz hasło (z kluczem), potem wystarczy sam klucz."); return; }
                    var kek = KekFromHmac(r.Hmac);
                    ForgetBankKey();
                    _bankKp = Unwrap(r.Key.WrapKp, kek);
                    _bankKk = Unwrap(r.Key.WrapK, kek);
                    _bankKey = CardKeyFrom(_bankKp, _bankKk);
                    done();
                }
                catch (Exception ex) { err.Text = ex.Message; }
                finally { busy = false; if (keyBtn != null) keyBtn.IsEnabled = true; ok.IsEnabled = true; }
            };
            if (keyBtn != null) keyBtn.Click += async (s, e) => await byKey();

            // --- haslem (zapasowo) ---
            ok.Click += async (s, e) =>
            {
                if (busy) return; busy = true; ok.IsEnabled = false; if (keyBtn != null) keyBtn.IsEnabled = false; err.Text = "";
                try
                {
                    var salt = Convert.FromBase64String(c.Salt);
                    string pw = pass.Password;
                    var h = await Task.Run(() => BankHash(pw, salt, c.Iter));
                    if (!CryptographicOperations.FixedTimeEquals(h, Convert.FromBase64String(c.Hash)))
                    {
                        _bankFails++;
                        int wait = Math.Min(30, _bankFails >= 3 ? (_bankFails - 2) * 5 : 0);   // kolejne bledy = coraz dluzsze czekanie
                        err.Text = L.T("Złe hasło.") + (wait > 0 ? (L.En ? " Wait " : " Odczekaj ") + wait + " s." : "");
                        if (wait > 0) await Task.Delay(wait * 1000);
                        return;
                    }
                    if (string.IsNullOrEmpty(c.CardSalt)) c.CardSalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
                    var pwKey = await Task.Run(() => CardKey(pw, c));
                    ForgetBankKey();
                    if (string.IsNullOrEmpty(c.WrapP))
                    {
                        // najstarszy zapis (karty zaszyfrowane samym kluczem z hasla) - przejscie na nowy uklad
                        _bankKey = pwKey; var old = LoadCards(c);
                        _bankKp = RandomNumberGenerator.GetBytes(32);
                        c.WrapP = Wrap(_bankKp, pwKey);
                        _bankKey = CardKeyFrom(_bankKp, null);
                        c.Cards = old.Count > 0 ? SealCards(_bankKey, old) : null;
                        foreach (var k in c.Keys) { k.WrapK = null; k.WrapKp = null; }
                        c.WrapPK = null;
                    }
                    else _bankKp = Unwrap(c.WrapP, pwKey);
                    bool keyPart = c.UseKey && c.Keys.Any(k => k.WrapK != null);
                    if (!keyPart) _bankKey = CardKeyFrom(_bankKp, null);
                    else if (!string.IsNullOrEmpty(c.WrapPK)) { _bankKk = Unwrap(c.WrapPK, _bankKp); _bankKey = CardKeyFrom(_bankKp, _bankKk); }
                    else
                    {
                        // poprzednia wersja (karty tylko z kluczem): ostatni raz haslo + klucz, potem kazde z osobna wystarczy
                        err.Text = L.T("Dotknij klucza sprzętowego (okienko Windows)…");
                        var r = await touch();
                        if (r.Key.WrapK == null || r.Hmac == null) { err.Text = L.T("Ten klucz otwiera tryb, ale nie odszyfrowuje kart – użyj klucza, który szyfruje karty"); ForgetBankKey(); return; }
                        var kek = KekFromHmac(r.Hmac);
                        _bankKk = Unwrap(r.Key.WrapK, kek);
                        _bankKey = CardKeyFrom(_bankKp, _bankKk);
                        c.WrapPK = Wrap(_bankKk, _bankKp);
                        r.Key.WrapKp = Wrap(_bankKp, kek);
                        ShowToast(L.T("🔑 Od teraz ten klucz sam otwiera tryb bankowy (inne klucze: otwórz nimi raz z hasłem)"), null);
                    }
                    // klucz bez WrapKp, ktory wlasnie uzyto z haslem? (inne klucze uzupelnia sie przy ich uzyciu z haslem)
                    done();
                }
                catch (Exception ex) { err.Text = ex.Message; }
                finally { busy = false; ok.IsEnabled = true; if (keyBtn != null) keyBtn.IsEnabled = true; }
            };

            // klucz juz w porcie -> od razu prosimy o dotkniecie (haslo niepotrzebne)
            w.Loaded += async (s, e) =>
            {
                if (!hasKeys) { pass.Focus(); return; }
                int n = await CountFidoKeys();
                if (n > 0) { keyInfo.Text = L.T("✔ Wykryto podłączony klucz sprzętowy."); await byKey(); }
                else { keyInfo.Text = n == 0 ? L.T("Nie wykryto klucza – włóż go i kliknij przycisk albo podaj hasło.") : L.T("Kliknij przycisk i dotknij klucza albo podaj hasło."); pass.Focus(); }
            };
            w.ShowDialog();
            return result;
        }

        static async Task<int> CountFidoKeys()
        {
            try
            {
                var sel = Windows.Devices.HumanInterfaceDevice.HidDevice.GetDeviceSelector(0xF1D0, 0x0001);
                var found = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(sel);
                return found.Count;
            }
            catch (Exception) { return -1; }
        }

        // ---------- profile bankowe ----------
        string ChooseBankProfile(List<string> profs)
        {
            var w = BankDialog(L.T("Tryb bankowy – wybierz profil"));
            var sp = (StackPanel)w.Content;
            sp.Children.Add(new TextBlock { Text = L.T("Czyj tryb bankowy otworzyć?"), Margin = new Thickness(0, 0, 0, 8) });
            string chosen = null;
            foreach (var p in profs)
            {
                var slug = p;
                var b = new Button { Content = "👤 " + BankOwnerOf(slug), MinWidth = 260, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 6), HorizontalContentAlignment = HorizontalAlignment.Left };
                b.Click += (s, e) => { chosen = slug; w.Close(); };
                sp.Children.Add(b);
            }
            BankButtons(sp, w, L.T("Anuluj")).Click += (s, e) => w.Close();
            w.ShowDialog();
            return chosen;
        }

        void SwitchBankProfile(string slug)
        {
            if (_bankUnlocked) LockBank(null);
            _bankProfile = slug ?? "";
            OpenBankTabAt(HomeUrl);
        }

        void NewBankProfile()
        {
            var w = BankDialog(L.T("Nowy profil bankowy"));
            var sp = (StackPanel)w.Content;
            sp.Children.Add(new TextBlock { Text = L.T("Nazwa profilu (np. imię drugiego użytkownika):"), Margin = new Thickness(0, 0, 0, 4) });
            var name = new TextBox { Padding = new Thickness(4), MinWidth = 300 };
            sp.Children.Add(name);
            sp.Children.Add(new TextBlock { Text = L.T("Każdy profil ma własne hasło lub klucz, osobne logowania w bankach i osobne karty, banki, sklepy i notatki."), TextWrapping = TextWrapping.Wrap, MaxWidth = 340, Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 0) });
            string owner = null;
            BankButtons(sp, w, L.T("Dalej")).Click += (s, e) => { if (!string.IsNullOrWhiteSpace(name.Text)) { owner = name.Text.Trim(); w.Close(); } };
            name.Focus();
            w.ShowDialog();
            if (owner == null) return;
            string slug = new string(owner.ToLowerInvariant().Normalize(NormalizationForm.FormD).Where(ch => (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')).ToArray());
            if (slug.Length == 0) slug = "u" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
            if (slug.Length > 20) slug = slug.Substring(0, 20);
            var existing = BankProfiles(); var baseSlug = slug; int i = 2;
            while (existing.Contains(slug)) slug = baseSlug + i++;
            if (_bankUnlocked) LockBank(null);
            _bankProfile = slug;
            _bankNewOwner = owner;
            if (BankSetup(null)) OpenBankTabAt(HomeUrl);
            else _bankProfile = "";
            _bankNewOwner = null;
        }
        string _bankNewOwner;

        // ---------- pierwsze ustawienie / zmiana ustawien ----------
        void BankSettings()
        {
            var c = LoadBank();
            if (c == null) { BankSetup(null); return; }
            if (!_bankUnlocked) { ShowToast(L.T("Najpierw otwórz tryb bankowy (hasłem / kluczem)"), null); OpenBankTab(); return; }
            BankSetup(c);
        }

        bool BankSetup(BankConfig existing)
        {
            var c = existing ?? new BankConfig { Iter = 600000, Owner = _bankNewOwner };
            var keys = c.Keys.Select(k => new BankKey { Id = k.Id, X = k.X, Y = k.Y, Name = k.Name, WrapK = k.WrapK, WrapKp = k.WrapKp }).ToList();
            if (string.IsNullOrEmpty(c.HmacSalt)) c.HmacSalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            if (existing != null && (!string.IsNullOrEmpty(c.Cards) || !string.IsNullOrEmpty(c.Notes) || !string.IsNullOrEmpty(c.Sites)) && _bankKey == null)
            {
                MessageBox.Show(this, L.T("Karty są zamknięte – otwórz tryb bankowy kluczem, który szyfruje karty, i wtedy zmień ustawienia."), "Velivo");
                return false;
            }
            byte[] kk = _bankKk != null ? (byte[])_bankKk.Clone() : null;   // czesc klucza kart z kluczy sprzetowych
            byte[] kp = _bankKp != null ? (byte[])_bankKp.Clone() : RandomNumberGenerator.GetBytes(32);   // czesc z hasla
            var w = BankDialog(L.T("Tryb bankowy – ustawienia"));
            var sp = (StackPanel)w.Content;
            sp.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Margin = new Thickness(0, 0, 0, 10),
                Text = L.T("Osobny, odizolowany profil na banki i zakupy: własne logowania i ciasteczka, bez dodatków, bez historii. Blokuje się po 10 minutach bezczynności.")
            });
            sp.Children.Add(new TextBlock { Text = existing == null ? L.T("Hasło (min. 8 znaków):") : L.T("Nowe hasło (puste = bez zmiany):") });
            var p1 = new PasswordBox { Padding = new Thickness(6), Margin = new Thickness(0, 2, 0, 6) };
            sp.Children.Add(p1);
            sp.Children.Add(new TextBlock { Text = L.T("Powtórz hasło:") });
            var p2 = new PasswordBox { Padding = new Thickness(6), Margin = new Thickness(0, 2, 0, 10) };
            sp.Children.Add(p2);

            var useKey = new CheckBox { Content = L.T("Dodatkowo wymagaj klucza sprzętowego (YubiKey, Google Titan…)"), IsChecked = c.UseKey, Margin = new Thickness(0, 4, 0, 4) };
            sp.Children.Add(useKey);
            var keyPanel = new StackPanel { Margin = new Thickness(20, 0, 0, 0) };
            var presence = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 360, Margin = new Thickness(0, 2, 0, 4) };
            var list = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 360, Margin = new Thickness(0, 2, 0, 4) };
            var add = new Button { Content = L.T("➕ Dodaj klucz (włóż go i dotknij)"), Padding = new Thickness(10, 4, 10, 4), HorizontalAlignment = HorizontalAlignment.Left };
            var clear = new Button { Content = L.T("Usuń wszystkie klucze"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            row.Children.Add(add); row.Children.Add(clear);
            keyPanel.Children.Add(presence); keyPanel.Children.Add(list); keyPanel.Children.Add(row);
            sp.Children.Add(keyPanel);
            Action refresh = () =>
            {
                keyPanel.IsEnabled = useKey.IsChecked == true;
                keyPanel.Opacity = keyPanel.IsEnabled ? 1 : .5;
                list.Text = keys.Count == 0 ? L.T("Nie dodano jeszcze żadnego klucza.")
                    : (L.En ? "Registered keys: " : "Dodane klucze: ") + string.Join(", ", keys.Select(k => k.Name + (k.WrapK != null ? " 🔐" : ""))) +
                      L.T("\n🔐 = klucz szyfruje karty (bez niego karty się nie otworzą)");
                clear.IsEnabled = keys.Count > 0;
            };
            useKey.Checked += (s, e) => refresh(); useKey.Unchecked += (s, e) => refresh();
            UpdateKeyPresence(presence, false);
            var err = new TextBlock { Foreground = Brushes.Firebrick, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
            sp.Children.Add(err);
            add.Click += async (s, e) =>
            {
                add.IsEnabled = false; err.Text = L.T("Dotknij klucza sprzętowego (okienko Windows)…");
                try
                {
                    var hwnd = new WindowInteropHelper(w).Handle;
                    var k = await Task.Run(() => WebAuthn.Register(hwnd, BankRpId, keys.Select(x => Convert.FromBase64String(x.Id)).ToList()));
                    k.Name = (L.En ? "Key " : "Klucz ") + (keys.Count + 1);
                    // drugie dotkniecie: sekret z klucza (hmac-secret) - z niego powstaje szyfr kart
                    err.Text = L.T("Dotknij klucza jeszcze raz – przygotowanie szyfrowania kart…");
                    byte[] hmac = null;
                    try { var salt = Convert.FromBase64String(c.HmacSalt); var r = await Task.Run(() => WebAuthn.Verify(hwnd, BankRpId, new List<BankKey> { k }, salt)); hmac = r.Key != null ? r.Hmac : null; }
                    catch (Exception) { }
                    if (hmac != null)
                    {
                        if (kk == null) kk = RandomNumberGenerator.GetBytes(32);
                        var kek = KekFromHmac(hmac);
                        k.WrapK = Wrap(kk, kek);
                        k.WrapKp = Wrap(kp, kek);   // sam klucz otworzy tryb (bez hasla)
                        err.Text = "";
                    }
                    else err.Text = L.T("Ten klucz nie potrafi szyfrować – będzie tylko otwierał tryb bankowy.");
                    keys.Add(k); refresh();
                    UpdateKeyPresence(presence, false);
                }
                catch (Exception ex) { err.Text = ex.Message; }
                finally { add.IsEnabled = true; }
            };
            clear.Click += (s, e) => { keys.Clear(); kk = null; refresh(); };
            refresh();

            var ok = BankButtons(sp, w, L.T("Zapisz"));
            bool saved = false;
            ok.Click += (s, e) =>
            {
                string a = p1.Password, b = p2.Password;
                if (existing == null || a.Length > 0)
                {
                    if (a.Length < 8) { err.Text = L.T("Hasło musi mieć co najmniej 8 znaków."); return; }
                    if (a != b) { err.Text = L.T("Hasła się różnią."); return; }
                }
                if (useKey.IsChecked == true && keys.Count == 0) { err.Text = L.T("Dodaj co najmniej jeden klucz albo odznacz opcję klucza."); return; }
                List<BankCard> oldCards = new List<BankCard>(); List<BankNote> oldNotes = new List<BankNote>(); List<BankSite> oldSites = new List<BankSite>();
                if (existing != null) { try { oldCards = LoadCards(c); oldNotes = LoadSealed<BankNote>(c.Notes); oldSites = LoadSealed<BankSite>(c.Sites); } catch (Exception) { err.Text = L.T("Nie udało się odczytać kart."); return; } }
                if (a.Length > 0)
                {
                    var salt = RandomNumberGenerator.GetBytes(16);
                    c.Salt = Convert.ToBase64String(salt); c.Iter = 600000;
                    c.Hash = Convert.ToBase64String(BankHash(a, salt, c.Iter));
                    c.CardSalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
                    c.WrapP = Wrap(kp, CardKey(a, c));
                }
                bool withKey = useKey.IsChecked == true;
                if (!withKey || !keys.Any(k => k.WrapK != null)) { kk = null; foreach (var k in keys) { k.WrapK = null; k.WrapKp = null; } }
                c.WrapPK = kk != null ? Wrap(kk, kp) : null;   // haslo zapasowo tez otwiera karty
                // karty przeszyfrowane nowym kluczem (haslo + ewentualnie klucz sprzetowy)
                ForgetBankKey();
                _bankKp = kp; _bankKk = kk; _bankKey = CardKeyFrom(kp, kk);
                c.Cards = oldCards.Count > 0 ? SealCards(_bankKey, oldCards) : null;
                c.Notes = SealList(_bankKey, oldNotes); c.Sites = SealList(_bankKey, oldSites);
                _bankUnlocked = true;
                c.UseKey = withKey; c.Keys = keys;
                SaveBank(c); saved = true; w.Close();
                ShowToast(L.T("🏦 Tryb bankowy zapisany"), null);
            };
            w.ShowDialog();
            return saved;
        }

        void ResetBank()
        {
            if (MessageBox.Show(this, L.T("Usunąć hasło i klucze trybu bankowego oraz wyczyścić jego dane (logowania, ciasteczka)?\nUstawisz go od nowa."),
                "Velivo", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            LockBank(L.T("Tryb bankowy wyczyszczony"));
            try { File.Delete(BankFile); File.WriteAllText(BankWipeFlag, "1"); } catch (Exception) { }
        }

        // ---------- klucz sprzetowy ----------
        // Czy podlaczony jest jakis klucz sprzetowy (urzadzenie FIDO na USB) - tylko informacja dla uzytkownika.
        async void UpdateKeyPresence(TextBlock target, bool forUnlock)
        {
            int n = -1;
            try
            {
                var sel = Windows.Devices.HumanInterfaceDevice.HidDevice.GetDeviceSelector(0xF1D0, 0x0001);
                var found = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(sel);
                n = found.Count;
            }
            catch (Exception) { }
            if (n > 0) target.Text = L.T("✔ Wykryto podłączony klucz sprzętowy.");
            else if (n == 0) target.Text = forUnlock ? L.T("Włóż klucz sprzętowy – będzie potrzebny po haśle.") : L.T("Nie wykryto klucza na USB (klucz NFC/Bluetooth Windows znajdzie sam po kliknięciu „Dodaj”).");
            else target.Text = forUnlock ? L.T("Po haśle Windows poprosi o klucz sprzętowy.") : "";
        }

        // ---------- karty bankowe (zaszyfrowane haslem trybu bankowego) ----------
        // Kodu CVV/CVC nie zapisujemy celowo - to on chroni karte, gdy ktos pozna jej numer.
        void BankCards()
        {
            if (!_bankUnlocked || _bankKey == null) { ShowToast(L.T("Najpierw otwórz tryb bankowy (hasłem / kluczem)"), null); OpenBankTab(); return; }
            var c = LoadBank(); if (c == null) return;
            List<BankCard> cards;
            try { cards = LoadCards(c); } catch (Exception) { MessageBox.Show(this, L.T("Nie udało się odczytać kart."), "Velivo"); return; }
            var w = BankDialog(L.T("💳 Moje karty"));
            var sp = (StackPanel)w.Content;
            var list = new ListBox { MinWidth = 380, Height = 150, Margin = new Thickness(0, 0, 0, 8) };
            Action fillList = () => { list.Items.Clear(); foreach (var k in cards) list.Items.Add(CardLine(k)); };
            fillList();
            sp.Children.Add(list);
            var del = new Button { Content = L.T("Usuń zaznaczoną"), Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left };
            del.Click += (s, e) => { int i = list.SelectedIndex; if (i >= 0) { cards.RemoveAt(i); fillList(); } };
            sp.Children.Add(del);
            sp.Children.Add(new TextBlock { Text = L.T("Dodaj kartę:"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
            Func<string, TextBox> field = lbl => { sp.Children.Add(new TextBlock { Text = lbl }); var t = new TextBox { Padding = new Thickness(4), Margin = new Thickness(0, 1, 0, 5) }; sp.Children.Add(t); return t; };
            var label = field(L.T("Nazwa (np. Visa PKO):"));
            var number = field(L.T("Numer karty:"));
            var exp = field(L.T("Ważna do (MM/RR):"));
            var holder = field(L.T("Imię i nazwisko na karcie:"));
            sp.Children.Add(new TextBlock { Text = L.T("Kodu CVV nie zapisujemy – wpiszesz go sam przy płatności."), Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 });
            var err = new TextBlock { Foreground = Brushes.Firebrick, Margin = new Thickness(0, 6, 0, 0) };
            sp.Children.Add(err);
            var add = new Button { Content = L.T("➕ Dodaj"), Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            add.Click += (s, e) =>
            {
                string num = new string((number.Text ?? "").Where(char.IsDigit).ToArray());
                if (num.Length < 12 || num.Length > 19 || !Luhn(num)) { err.Text = L.T("Nieprawidłowy numer karty."); return; }
                cards.Add(new BankCard { Label = string.IsNullOrWhiteSpace(label.Text) ? L.T("Karta") : label.Text.Trim(), Number = num, Exp = (exp.Text ?? "").Trim(), Holder = (holder.Text ?? "").Trim() });
                label.Text = number.Text = exp.Text = holder.Text = ""; err.Text = ""; fillList();
            };
            sp.Children.Add(add);
            var ok = BankButtons(sp, w, L.T("Zapisz"));
            ok.Click += (s, e) =>
            {
                c.Cards = cards.Count > 0 ? SealCards(_bankKey, cards) : null;
                SaveBank(c); w.Close(); ShowToast(L.T("💳 Karty zapisane (zaszyfrowane)"), null);
            };
            w.ShowDialog();
        }

        static string CardLine(BankCard k) { return k.Label + "   •••• " + (k.Number.Length >= 4 ? k.Number.Substring(k.Number.Length - 4) : "") + (string.IsNullOrEmpty(k.Exp) ? "" : "   " + k.Exp); }

        static bool Luhn(string n)
        {
            int sum = 0; bool dbl = false;
            for (int i = n.Length - 1; i >= 0; i--) { int d = n[i] - '0'; if (dbl) { d *= 2; if (d > 9) d -= 9; } sum += d; dbl = !dbl; }
            return sum % 10 == 0;
        }

        // Wypelnia formularz platnosci na stronie w karcie bankowej (pola wg autocomplete/nazw).
        void BankFillMenu()
        {
            if (_current == null || !_current.Bank || _bankKey == null) return;
            var c = LoadBank(); if (c == null) return;
            List<BankCard> cards;
            try { cards = LoadCards(c); } catch (Exception) { return; }
            if (cards.Count == 0) { BankCards(); return; }
            var menu = new ContextMenu { PlacementTarget = _bankBtn };
            foreach (var k in cards)
            {
                var card = k;
                var mi = new MenuItem { Header = CardLine(card) };
                mi.Click += async (s, e) =>
                {
                    var core = _current != null && _current.Bank ? _current.View.CoreWebView2 : null;
                    if (core == null) return;
                    var data = JsonSerializer.Serialize(new { n = card.Number, e = card.Exp, h = card.Holder });
                    try { await core.ExecuteScriptAsync(CardFillScript.Replace("__D__", data)); } catch (Exception) { }
                    ShowToast(L.T("💳 Wpisano dane karty – CVV wpisz sam"), null);
                };
                menu.Items.Add(mi);
            }
            menu.IsOpen = true;
        }

        const string CardFillScript = @"(function(d){try{
var docs=[document];document.querySelectorAll('iframe').forEach(function(f){try{if(f.contentDocument)docs.push(f.contentDocument);}catch(x){}});
function set(el,v){if(!el||!v)return;var p=Object.getPrototypeOf(el);var ds=Object.getOwnPropertyDescriptor(p,'value');if(ds&&ds.set)ds.set.call(el,v);else el.value=v;
el.dispatchEvent(new Event('input',{bubbles:true}));el.dispatchEvent(new Event('change',{bubbles:true}));}
var mm=(d.e||'').split(/[\/\-. ]/)[0]||'',yy=(d.e||'').split(/[\/\-. ]/)[1]||'';
docs.forEach(function(doc){doc.querySelectorAll('input,select').forEach(function(el){
var a=((el.getAttribute('autocomplete')||'')+' '+(el.name||'')+' '+(el.id||'')+' '+(el.getAttribute('placeholder')||'')+' '+(el.getAttribute('aria-label')||'')).toLowerCase();
if(/cc-csc|cvv|cvc|csc|security/.test(a))return;
if(/cc-number|cardnumber|card-number|card_number|numer.?karty|ccnum/.test(a))set(el,d.n);
else if(/cc-exp-month|exp.?month|miesi/.test(a))set(el,mm);
else if(/cc-exp-year|exp.?year|rok/.test(a))set(el,el.maxLength==4?'20'+yy:yy);
else if(/cc-exp|expir|wazn|ważn|mm.?\/.?yy|mm.?\/.?rr/.test(a))set(el,d.e);
else if(/cc-name|cardholder|card-holder|holder|imi.+nazw|name.?on.?card/.test(a))set(el,d.h);
});});}catch(x){}})(__D__);";

        // ---------- strony bankowe i sklepy ----------
        void FillBankSitesMenu(MenuItem parent, bool shop)
        {
            parent.Items.Clear();
            var c = LoadBank();
            List<BankSite> list = null;
            if (_bankUnlocked && _bankKey != null && c != null) { try { list = LoadSealed<BankSite>(c.Sites); } catch (Exception) { } }
            if (list == null)
            {
                var info = new MenuItem { Header = L.T("Otwórz tryb bankowy, aby zobaczyć listę") };
                info.Click += (s, e) => OpenBankTab();
                parent.Items.Add(info); return;
            }
            list = list.Where(x => IsShop(x) == shop).ToList();
            var addManual = new MenuItem { Header = shop ? L.T("➕ Dodaj sklep (nazwa i adres)…") : L.T("➕ Dodaj bank (nazwa i adres)…") };
            addManual.Click += (s, e) => BankAddSiteManual(shop);
            parent.Items.Add(addManual);
            parent.Items.Add(new Separator());
            if (list.Count == 0) { parent.Items.Add(new MenuItem { Header = L.T("(pusto)"), IsEnabled = false }); }
            foreach (var site in list)
            {
                var st = site;
                var mi = new MenuItem { Header = st.Name, ToolTip = st.Url };
                mi.Click += (s, e) => OpenBankSite(st.Url);
                parent.Items.Add(mi);
            }
            if (list.Count > 0)
            {
                parent.Items.Add(new Separator());
                var del = new MenuItem { Header = L.T("Usuń stronę z listy") };
                foreach (var site in list)
                {
                    var st = site;
                    var d = new MenuItem { Header = st.Name };
                    d.Click += (s, e) =>
                    {
                        var cc = LoadBank(); if (cc == null || _bankKey == null) return;
                        var l = LoadSealed<BankSite>(cc.Sites); l.RemoveAll(x => x.Url == st.Url);
                        cc.Sites = SealList(_bankKey, l);
                        cc.SiteHosts = l.Select(x => HostHash(HostOf(x.Url))).Distinct().ToList();
                        SaveBank(cc);
                    };
                    del.Items.Add(d);
                }
                parent.Items.Add(del);
            }
        }

        void BankAddSiteManual(bool shop)
        {
            if (!_bankUnlocked || _bankKey == null) return;
            var w = BankDialog(shop ? L.T("🛒 Dodaj sklep online") : L.T("🏦 Dodaj bank"));
            var sp = (StackPanel)w.Content;
            sp.Children.Add(new TextBlock { Text = L.T("Nazwa (np. mBank, Allegro):") });
            var name = new TextBox { Padding = new Thickness(4), MinWidth = 340, Margin = new Thickness(0, 1, 0, 6) };
            sp.Children.Add(name);
            sp.Children.Add(new TextBlock { Text = L.T("Adres strony (np. https://www.mbank.pl):") });
            var addr = new TextBox { Padding = new Thickness(4), Margin = new Thickness(0, 1, 0, 6) };
            sp.Children.Add(addr);
            var err = new TextBlock { Foreground = Brushes.Firebrick };
            sp.Children.Add(err);
            var ok = BankButtons(sp, w, L.T("➕ Dodaj"));
            ok.Click += (s, e) =>
            {
                string u = (addr.Text ?? "").Trim();
                if (u.Length > 0 && !u.Contains("://")) u = "https://" + u;
                var host = HostOf(u);
                if (host == null || !host.Contains('.')) { err.Text = L.T("Nieprawidłowy adres strony."); return; }
                var c = LoadBank(); if (c == null || _bankKey == null) return;
                var list = LoadSealed<BankSite>(c.Sites);
                if (list.Any(x => HostOf(x.Url) == host)) { err.Text = L.T("Ta strona już jest na liście"); return; }
                list.Add(new BankSite { Name = string.IsNullOrWhiteSpace(name.Text) ? host : name.Text.Trim(), Url = u, Kind = shop ? "shop" : "bank" });
                c.Sites = SealList(_bankKey, list);
                c.SiteHosts = list.Select(x => HostHash(HostOf(x.Url))).Distinct().ToList();
                SaveBank(c); w.Close();
                ShowToast((shop ? L.T("🛒 Dodano do Moich sklepów: ") : L.T("🏦 Dodano do Moich banków: ")) + (string.IsNullOrWhiteSpace(name.Text) ? host : name.Text.Trim()), null);
            };
            name.Focus();
            w.ShowDialog();
        }

        void OpenBankSite(string url)
        {
            if (_current != null && _current.Bank) { Navigate(_current, url); return; }
            OpenBankTabAt(url);
        }

        void BankAddCurrentSite(bool shop)
        {
            if (_current == null || !_current.Bank || _bankKey == null) return;
            var core = _current.View.CoreWebView2; if (core == null) return;
            var host = HostOf(core.Source); if (host == null) return;
            var c = LoadBank(); if (c == null) return;
            var list = LoadSealed<BankSite>(c.Sites);
            var u = new Uri(core.Source);
            string url = u.Scheme + "://" + u.Host + "/";
            if (list.Any(x => HostOf(x.Url) == host)) { ShowToast(L.T("Ta strona już jest na liście"), null); return; }
            string name = string.IsNullOrWhiteSpace(core.DocumentTitle) ? host : core.DocumentTitle.Trim();
            if (name.Length > 40) name = name.Substring(0, 40) + "…";
            list.Add(new BankSite { Name = name, Url = url, Kind = shop ? "shop" : "bank" });
            c.Sites = SealList(_bankKey, list);
            c.SiteHosts = list.Select(x => HostHash(HostOf(x.Url))).Distinct().ToList();
            SaveBank(c);
            ShowToast((shop ? L.T("🛒 Dodano do Moich sklepów: ") : L.T("🏦 Dodano do Moich banków: ")) + name, null);
        }

        // Strona z listy bankowej otwarta w ZWYKLEJ karcie -> przypomnienie (raz na strone w tej sesji)
        readonly HashSet<string> _bankWarned = new HashSet<string>();
        void CheckBankSiteInNormalTab(BrowserTab tab, string url)
        {
            try
            {
                if (tab == null || tab.Bank) return;
                var host = HostOf(url); if (host == null) return;
                var hh = HostHash(host);
                bool known = false;
                foreach (var p in BankProfiles())   // strony ze wszystkich profili bankowych
                {
                    try { var c = JsonSerializer.Deserialize<BankConfig>(File.ReadAllText(BankFileFor(p))); if (c != null && c.SiteHosts != null && c.SiteHosts.Contains(hh)) { known = true; break; } } catch (Exception) { }
                }
                if (!known || !_bankWarned.Add(host)) return;
                ShowToast(L.T("🏦 To Twoja strona bankowa – bezpieczniej otworzyć ją w trybie bankowym (przycisk 🏦)"), null);
            }
            catch (Exception) { }
        }

        // ---------- notatki (zaszyfrowane jak karty) ----------
        void BankNotes()
        {
            if (!_bankUnlocked || _bankKey == null) { ShowToast(L.T("Najpierw otwórz tryb bankowy (hasłem / kluczem)"), null); OpenBankTab(); return; }
            var c = LoadBank(); if (c == null) return;
            List<BankNote> notes;
            try { notes = LoadSealed<BankNote>(c.Notes); } catch (Exception) { MessageBox.Show(this, L.T("Nie udało się odczytać kart."), "Velivo"); return; }
            var w = BankDialog(L.T("📝 Notatki trybu bankowego"));
            var sp = (StackPanel)w.Content;
            var list = new ListBox { MinWidth = 420, Height = 130, Margin = new Thickness(0, 0, 0, 8) };
            Action fillList = () => { list.Items.Clear(); foreach (var n in notes) list.Items.Add(n.Title); };
            fillList();
            sp.Children.Add(list);
            sp.Children.Add(new TextBlock { Text = L.T("Tytuł (np. Bank – login):") });
            var title = new TextBox { Padding = new Thickness(4), Margin = new Thickness(0, 1, 0, 6) };
            sp.Children.Add(title);
            sp.Children.Add(new TextBlock { Text = L.T("Treść (login, hasło, numer klienta…):") });
            var text = new TextBox { Padding = new Thickness(4), Height = 110, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 1, 0, 6) };
            sp.Children.Add(text);
            list.SelectionChanged += (s, e) => { int i = list.SelectedIndex; if (i >= 0) { title.Text = notes[i].Title; text.Text = notes[i].Text; } };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var add = new Button { Content = L.T("➕ Dodaj nową"), Padding = new Thickness(10, 3, 10, 3) };
            var upd = new Button { Content = L.T("✔ Zmień zaznaczoną"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
            var del = new Button { Content = L.T("Usuń zaznaczoną"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
            var copy = new Button { Content = L.T("📋 Kopiuj treść"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
            row.Children.Add(add); row.Children.Add(upd); row.Children.Add(del); row.Children.Add(copy);
            sp.Children.Add(row);
            add.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(title.Text) && string.IsNullOrWhiteSpace(text.Text)) return;
                notes.Add(new BankNote { Title = string.IsNullOrWhiteSpace(title.Text) ? L.T("Notatka") : title.Text.Trim(), Text = text.Text ?? "" });
                title.Text = text.Text = ""; fillList();
            };
            upd.Click += (s, e) => { int i = list.SelectedIndex; if (i < 0) return; notes[i].Title = title.Text.Trim(); notes[i].Text = text.Text ?? ""; fillList(); list.SelectedIndex = i; };
            del.Click += (s, e) => { int i = list.SelectedIndex; if (i < 0) return; notes.RemoveAt(i); title.Text = text.Text = ""; fillList(); };
            copy.Click += (s, e) =>
            {
                var t = text.Text ?? ""; if (t.Length == 0) return;
                try { Clipboard.SetText(t); } catch (Exception) { return; }
                ShowToast(L.T("📋 Skopiowano – schowek wyczyści się za 30 s"), null);
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                timer.Tick += (a, b) => { timer.Stop(); try { if (Clipboard.ContainsText() && Clipboard.GetText() == t) Clipboard.Clear(); } catch (Exception) { } };
                timer.Start();
            };
            var ok = BankButtons(sp, w, L.T("Zapisz"));
            ok.Click += (s, e) =>
            {
                var cc = LoadBank(); if (cc == null || _bankKey == null) { w.Close(); return; }
                cc.Notes = SealList(_bankKey, notes);
                SaveBank(cc); w.Close(); ShowToast(L.T("📝 Notatki zapisane (zaszyfrowane)"), null);
            };
            w.ShowDialog();
        }

        // ---------- synchronizacja w sieci (LAN) ----------
        // Przesylany jest tylko bank.json: skrot hasla, klucze publiczne kluczy sprzetowych i karty ZASZYFROWANE
        // (haslem, a przy kluczu sprzetowym takze jego sekretem). Pakiet sieciowy jest dodatkowo szyfrowany parowaniem.
        // Logowania i ciasteczka banku zostaja na kazdym komputerze osobno.
        // wszystkie profile bankowe jako jeden tekst: nazwa pliku -> zawartosc
        static string ExportBanksForSync()
        {
            var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in BankProfiles()) { var f = BankFileFor(p); d[Path.GetFileName(f)] = ReadTextOrEmpty(f); }
            return d.Count == 0 ? "" : JsonSerializer.Serialize(d);
        }

        void ApplySyncedBanks(string all)
        {
            try
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(all);
                if (d == null) return;
                foreach (var kv in d) if (BankFileRx.IsMatch(kv.Key ?? "")) ApplySyncedBank(Path.Combine(DataDir, kv.Key), kv.Value);
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        void ApplySyncedBank(string json) { ApplySyncedBank(BankFileFor(""), json); }

        void ApplySyncedBank(string file, string json)
        {
            try
            {
                var c = JsonSerializer.Deserialize<BankConfig>(json);
                if (c == null || string.IsNullOrEmpty(c.Hash) || string.IsNullOrEmpty(c.Salt)) return;
                if (json == ReadTextOrEmpty(file)) return;
                File.WriteAllText(file, json);
                // haslo / klucze / karty mogly sie zmienic - otwarty tryb zamykamy, otworzysz go ponownie
                if (_bankUnlocked && string.Equals(file, BankFile, StringComparison.OrdinalIgnoreCase)) LockBank(L.T("🔒 Tryb bankowy zmieniony na innym komputerze – otwórz go ponownie"));
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        // ---------- wspolne okienko ----------
        Window BankDialog(string title)
        {
            var w = new Window
            {
                Title = title, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                Content = new StackPanel { Margin = new Thickness(18) }
            };
            w.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) w.Close(); };
            return w;
        }

        static Button BankButtons(StackPanel sp, Window w, string okText)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var ok = new Button { Content = okText, IsDefault = true, MinWidth = 90, Padding = new Thickness(10, 4, 10, 4) };
            var cancel = new Button { Content = L.T("Anuluj"), IsCancel = true, MinWidth = 90, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
            row.Children.Add(ok); row.Children.Add(cancel);
            sp.Children.Add(row);
            return ok;
        }

        // ======================================================================
        //  Windows WebAuthn (webauthn.dll) - rejestracja klucza i sprawdzenie podpisu
        // ======================================================================
        static class WebAuthn
        {
            [StructLayout(LayoutKind.Sequential)] struct RpInfo { public uint Ver; public IntPtr Id; public IntPtr Name; public IntPtr Icon; }
            [StructLayout(LayoutKind.Sequential)] struct UserInfo { public uint Ver; public uint CbId; public IntPtr PbId; public IntPtr Name; public IntPtr Icon; public IntPtr DisplayName; }
            [StructLayout(LayoutKind.Sequential)] struct CoseParam { public uint Ver; public IntPtr Type; public int Alg; }
            [StructLayout(LayoutKind.Sequential)] struct CoseParams { public uint Count; public IntPtr Items; }
            [StructLayout(LayoutKind.Sequential)] struct ClientData { public uint Ver; public uint Cb; public IntPtr Pb; public IntPtr HashAlg; }
            [StructLayout(LayoutKind.Sequential)] struct Credential { public uint Ver; public uint CbId; public IntPtr PbId; public IntPtr Type; }
            [StructLayout(LayoutKind.Sequential)] struct Credentials { public uint Count; public IntPtr Items; }
            [StructLayout(LayoutKind.Sequential)] struct Extensions { public uint Count; public IntPtr Items; }
            [StructLayout(LayoutKind.Sequential)]
            struct MakeOpts { public uint Ver; public uint Timeout; public Credentials Exclude; public Extensions Ext; public uint Attachment; public int RequireResident; public uint UserVerification; public uint Attestation; public uint Flags; }
            // wersja 6 opcji: dochodza m.in. sole hmac-secret (sekret z klucza sprzetowego)
            [StructLayout(LayoutKind.Sequential)]
            struct GetOpts
            {
                public uint Ver; public uint Timeout; public Credentials Allow; public Extensions Ext; public uint Attachment; public uint UserVerification; public uint Flags;
                public IntPtr U2fAppId; public IntPtr U2fAppIdUsed; public IntPtr CancellationId; public IntPtr AllowList;
                public uint LargeBlobOp; public uint CbLargeBlob; public IntPtr PbLargeBlob; public IntPtr HmacSaltValues; public int InPrivate;
            }
            [StructLayout(LayoutKind.Sequential)] struct Extension { public IntPtr Id; public uint Cb; public IntPtr Pv; }
            [StructLayout(LayoutKind.Sequential)] struct HmacSalt { public uint CbFirst; public IntPtr PbFirst; public uint CbSecond; public IntPtr PbSecond; }
            [StructLayout(LayoutKind.Sequential)] struct HmacSaltValues { public IntPtr Global; public uint CredCount; public IntPtr CredList; }
            [StructLayout(LayoutKind.Sequential)]
            struct Attestation { public uint Ver; public IntPtr Format; public uint CbAuthData; public IntPtr PbAuthData; public uint CbAtt; public IntPtr PbAtt; public uint DecodeType; public IntPtr Decode; public uint CbAttObj; public IntPtr PbAttObj; public uint CbCredId; public IntPtr PbCredId; }
            [StructLayout(LayoutKind.Sequential)]
            struct Assertion
            {
                public uint Ver; public uint CbAuthData; public IntPtr PbAuthData; public uint CbSig; public IntPtr PbSig; public Credential Cred;
                public uint CbUserId; public IntPtr PbUserId;
                public Extensions Ext; public uint CbLargeBlob; public IntPtr PbLargeBlob; public uint LargeBlobStatus;   // od wersji 2
                public IntPtr HmacSecret;                                                                                 // od wersji 3
            }

            [DllImport("webauthn.dll")] static extern int WebAuthNAuthenticatorMakeCredential(IntPtr hwnd, ref RpInfo rp, ref UserInfo user, ref CoseParams pubKeyParams, ref ClientData cd, ref MakeOpts opts, out IntPtr attestation);
            [DllImport("webauthn.dll")] static extern int WebAuthNAuthenticatorGetAssertion(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string rpId, ref ClientData cd, ref GetOpts opts, out IntPtr assertion);
            [DllImport("webauthn.dll")] static extern void WebAuthNFreeCredentialAttestation(IntPtr p);
            [DllImport("webauthn.dll")] static extern void WebAuthNFreeAssertion(IntPtr p);
            [DllImport("webauthn.dll")] static extern IntPtr WebAuthNGetErrorName(int hr);

            const uint CrossPlatform = 2, UvDiscouraged = 3, AttestationNone = 1;

            sealed class Mem : IDisposable
            {
                readonly List<IntPtr> _h = new List<IntPtr>();
                public IntPtr Str(string s) { var p = Marshal.StringToHGlobalUni(s); _h.Add(p); return p; }
                public IntPtr Bytes(byte[] b) { var p = Marshal.AllocHGlobal(Math.Max(1, b.Length)); Marshal.Copy(b, 0, p, b.Length); _h.Add(p); return p; }
                public IntPtr Array<T>(IList<T> items) where T : struct
                {
                    int sz = Marshal.SizeOf<T>();
                    var p = Marshal.AllocHGlobal(Math.Max(1, sz * items.Count)); _h.Add(p);
                    for (int i = 0; i < items.Count; i++) Marshal.StructureToPtr(items[i], p + i * sz, false);
                    return p;
                }
                public void Dispose() { foreach (var p in _h) Marshal.FreeHGlobal(p); }
            }

            static Exception Fail(int hr)
            {
                string name = "";
                try { name = Marshal.PtrToStringUni(WebAuthNGetErrorName(hr)); } catch (Exception) { }
                if (name == "NotAllowedError" || hr == unchecked((int)0x800704C7)) return new OperationCanceledException(L.T("Anulowano albo nie dotknięto klucza na czas."));
                if (name == "InvalidStateError") return new InvalidOperationException(L.T("Ten klucz jest już dodany."));
                return new InvalidOperationException(L.T("Klucz sprzętowy: błąd ") + (string.IsNullOrEmpty(name) ? "0x" + hr.ToString("X8") : name));
            }

            static byte[] ClientJson(string type, byte[] challenge)
            {
                string ch = Convert.ToBase64String(challenge).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                return Encoding.UTF8.GetBytes("{\"type\":\"" + type + "\",\"challenge\":\"" + ch + "\",\"origin\":\"https://" + BankRpId + "\",\"crossOrigin\":false}");
            }

            static byte[] Read(IntPtr p, uint n) { var b = new byte[n]; if (n > 0) Marshal.Copy(p, b, 0, (int)n); return b; }

            public static BankKey Register(IntPtr hwnd, string rpId, List<byte[]> exclude)
            {
                using (var m = new Mem())
                {
                    var rp = new RpInfo { Ver = 1, Id = m.Str(rpId), Name = m.Str("Velivo") };
                    var uid = RandomNumberGenerator.GetBytes(16);
                    var user = new UserInfo { Ver = 1, CbId = (uint)uid.Length, PbId = m.Bytes(uid), Name = m.Str("velivo-bank"), DisplayName = m.Str(L.T("Tryb bankowy Velivo")) };
                    var cose = new[] { new CoseParam { Ver = 1, Type = m.Str("public-key"), Alg = -7 } };
                    var cp = new CoseParams { Count = 1, Items = m.Array(cose) };
                    var json = ClientJson("webauthn.create", RandomNumberGenerator.GetBytes(32));
                    var cd = new ClientData { Ver = 1, Cb = (uint)json.Length, Pb = m.Bytes(json), HashAlg = m.Str("SHA-256") };
                    var ex = exclude.Select(id => new Credential { Ver = 1, CbId = (uint)id.Length, PbId = m.Bytes(id), Type = m.Str("public-key") }).ToList();
                    // rozszerzenie hmac-secret: klucz bedzie umial oddac sekret do szyfrowania kart
                    var on = m.Bytes(BitConverter.GetBytes(1));
                    var ext = new[] { new Extension { Id = m.Str("hmac-secret"), Cb = 4, Pv = on } };
                    var opts = new MakeOpts
                    {
                        Ver = 1, Timeout = 60000, Exclude = new Credentials { Count = (uint)ex.Count, Items = ex.Count > 0 ? m.Array(ex) : IntPtr.Zero },
                        Ext = new Extensions { Count = 1, Items = m.Array(ext) },
                        Attachment = CrossPlatform, UserVerification = UvDiscouraged, Attestation = AttestationNone
                    };
                    IntPtr res;
                    int hr = WebAuthNAuthenticatorMakeCredential(hwnd, ref rp, ref user, ref cp, ref cd, ref opts, out res);
                    if (hr == unchecked((int)0x80070057)) { opts.Ext = new Extensions(); hr = WebAuthNAuthenticatorMakeCredential(hwnd, ref rp, ref user, ref cp, ref cd, ref opts, out res); }
                    if (hr != 0) throw Fail(hr);
                    try
                    {
                        var a = Marshal.PtrToStructure<Attestation>(res);
                        var authData = Read(a.PbAuthData, a.CbAuthData);
                        var credId = Read(a.PbCredId, a.CbCredId);
                        byte[] x, y;
                        if (!ParseEs256(authData, out x, out y)) throw new InvalidOperationException(L.T("Ten klucz nie obsługuje wymaganego podpisu (ES256)."));
                        return new BankKey { Id = Convert.ToBase64String(credId), X = Convert.ToBase64String(x), Y = Convert.ToBase64String(y) };
                    }
                    finally { WebAuthNFreeCredentialAttestation(res); }
                }
            }

            // Klucz publiczny P-256 z danych rejestracji (authData -> attestedCredentialData -> COSE).
            static bool ParseEs256(byte[] ad, out byte[] x, out byte[] y)
            {
                x = y = null;
                if (ad.Length < 55 || (ad[32] & 0x40) == 0) return false;   // brak danych klucza
                int credLen = (ad[53] << 8) | ad[54];
                int start = 55 + credLen;
                for (int i = start; i + 35 <= ad.Length; i++)
                {
                    if (x == null && ad[i] == 0x21 && ad[i + 1] == 0x58 && ad[i + 2] == 0x20) { x = ad.Skip(i + 3).Take(32).ToArray(); i += 34; continue; }
                    if (y == null && ad[i] == 0x22 && ad[i + 1] == 0x58 && ad[i + 2] == 0x20) { y = ad.Skip(i + 3).Take(32).ToArray(); i += 34; continue; }
                }
                return x != null && y != null && x.Length == 32 && y.Length == 32;
            }

            public sealed class VerifyResult { public BankKey Key; public byte[] Hmac; }

            // Prosi o dotkniecie klucza i sprawdza podpis jednym z zarejestrowanych kluczy publicznych.
            // Z sola (hmacSalt) prosi tez o sekret hmac-secret - wtedy Hmac = 32 bajty (null, gdy klucz/Windows nie umie).
            public static VerifyResult Verify(IntPtr hwnd, string rpId, List<BankKey> keys, byte[] hmacSalt)
            {
                using (var m = new Mem())
                {
                    var challenge = RandomNumberGenerator.GetBytes(32);
                    var json = ClientJson("webauthn.get", challenge);
                    var cd = new ClientData { Ver = 1, Cb = (uint)json.Length, Pb = m.Bytes(json), HashAlg = m.Str("SHA-256") };
                    var allow = keys.Select(k => { var id = Convert.FromBase64String(k.Id); return new Credential { Ver = 1, CbId = (uint)id.Length, PbId = m.Bytes(id), Type = m.Str("public-key") }; }).ToList();
                    var opts = new GetOpts { Ver = 1, Timeout = 60000, Allow = new Credentials { Count = (uint)allow.Count, Items = m.Array(allow) }, Attachment = CrossPlatform, UserVerification = UvDiscouraged };
                    if (hmacSalt != null && hmacSalt.Length == 32)
                    {
                        var g = new[] { new HmacSalt { CbFirst = 32, PbFirst = m.Bytes(hmacSalt) } };
                        var vals = new[] { new HmacSaltValues { Global = m.Array(g) } };
                        opts.Ver = 6; opts.HmacSaltValues = m.Array(vals);
                    }
                    IntPtr res;
                    int hr = WebAuthNAuthenticatorGetAssertion(hwnd, rpId, ref cd, ref opts, out res);
                    if (hr == unchecked((int)0x80070057) && opts.Ver == 6)
                    {
                        // starszy Windows nie zna wersji 6 - samo sprawdzenie klucza, bez sekretu
                        opts.Ver = 1; opts.HmacSaltValues = IntPtr.Zero;
                        hr = WebAuthNAuthenticatorGetAssertion(hwnd, rpId, ref cd, ref opts, out res);
                    }
                    if (hr != 0) throw Fail(hr);
                    try
                    {
                        var a = Marshal.PtrToStructure<Assertion>(res);
                        var authData = Read(a.PbAuthData, a.CbAuthData);
                        var sig = Read(a.PbSig, a.CbSig);
                        var credId = Convert.ToBase64String(Read(a.Cred.PbId, a.Cred.CbId));
                        var key = keys.FirstOrDefault(k => k.Id == credId);
                        var none = new VerifyResult();
                        if (key == null || authData.Length < 37) return none;
                        // podpis musi dotyczyc tej aplikacji i potwierdzac obecnosc (dotkniecie)
                        if (!authData.Take(32).SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)))) return none;
                        if ((authData[32] & 0x01) == 0) return none;
                        var signed = authData.Concat(SHA256.HashData(json)).ToArray();
                        using (var ec = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = Convert.FromBase64String(key.X), Y = Convert.FromBase64String(key.Y) } }))
                            if (!ec.VerifyData(signed, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) return none;
                        byte[] hmac = null;
                        if (opts.Ver == 6 && a.Ver >= 3 && a.HmacSecret != IntPtr.Zero)
                        {
                            var hs = Marshal.PtrToStructure<HmacSalt>(a.HmacSecret);
                            if (hs.CbFirst == 32 && hs.PbFirst != IntPtr.Zero) hmac = Read(hs.PbFirst, hs.CbFirst);
                        }
                        return new VerifyResult { Key = key, Hmac = hmac };
                    }
                    finally { WebAuthNFreeAssertion(res); }
                }
            }
        }
    }
}
