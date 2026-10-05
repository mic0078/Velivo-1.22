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
        const string BankProfileName = "VelivoBank";
        const string BankRpId = "velivo.local";
        static string BankFile { get { return Path.Combine(DataDir, "bank.json"); } }

        sealed class BankKey
        {
            public string Id { get; set; } public string X { get; set; } public string Y { get; set; } public string Name { get; set; }
            public string WrapK { get; set; }   // czesc klucza kart zaszyfrowana sekretem z tego klucza sprzetowego (null = klucz tylko otwiera tryb)
        }
        sealed class BankConfig
        {
            public string Salt { get; set; }
            public string Hash { get; set; }
            public int Iter { get; set; }
            public bool UseKey { get; set; }
            public List<BankKey> Keys { get; set; } = new List<BankKey>();
            public string CardSalt { get; set; }   // sol klucza sejfu kart (inna niz hasla)
            public string Cards { get; set; }      // karty zaszyfrowane AES-GCM
            public string WrapP { get; set; }      // czesc hasla klucza kart (Kp) zaszyfrowana kluczem z hasla
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
        static string BankWipeFlag { get { return Path.Combine(DataDir, "bank.wipe"); } }

        sealed class BankCard { public string Label { get; set; } public string Number { get; set; } public string Exp { get; set; } public string Holder { get; set; } }

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

        static BankConfig LoadBank()
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
            var cards = new MenuItem { Header = L.T("💳 Moje karty…") };
            cards.Click += (s, e) => BankCards();
            var fill = new MenuItem { Header = L.T("💳 Wypełnij kartę na tej stronie") };
            fill.Click += (s, e) => BankFillMenu();
            var lockNow = new MenuItem { Header = L.T("🔒 Zablokuj teraz") };
            lockNow.Click += (s, e) => LockBank(null);
            var reset = new MenuItem { Header = L.T("Zapomniałem hasła – wyczyść tryb bankowy…") };
            reset.Click += (s, e) => ResetBank();
            menu.Items.Add(cards); menu.Items.Add(fill); menu.Items.Add(cfg); menu.Items.Add(lockNow); menu.Items.Add(new Separator()); menu.Items.Add(reset);
            menu.Opened += (s, e) => { lockNow.IsEnabled = _bankUnlocked; fill.IsEnabled = _bankUnlocked && _current != null && _current.Bank; reset.IsEnabled = LoadBank() != null; };
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

        async void OpenBankTab()
        {
            if (!_bankUnlocked)
            {
                var c = LoadBank();
                if (c == null) { if (!BankSetup(null)) return; c = LoadBank(); if (c == null) return; }
                else if (!await BankUnlock(c)) return;
            }
            _bankLastInput = DateTime.UtcNow;
            _creatingBank = true;
            try { AddTab(HomeUrl, true); } finally { _creatingBank = false; }
        }

        // wywolywane z InitView dla karty bankowej - jednorazowe czyszczenie po resecie
        async Task BankAfterInit(Microsoft.Web.WebView2.Core.CoreWebView2 core)
        {
            if (!File.Exists(BankWipeFlag)) return;
            try { await core.Profile.ClearBrowsingDataAsync(); File.Delete(BankWipeFlag); } catch (Exception) { }
        }

        // ---------- odblokowanie ----------
        async Task<bool> BankUnlock(BankConfig c)
        {
            var w = BankDialog(L.T("Tryb bankowy"));
            var sp = (StackPanel)w.Content;
            sp.Children.Add(new TextBlock { Text = L.T("Podaj hasło trybu bankowego:"), Margin = new Thickness(0, 0, 0, 6) });
            var pass = new PasswordBox { Padding = new Thickness(6), MinWidth = 320 };
            sp.Children.Add(pass);
            bool needKey = c.UseKey && c.Keys.Count > 0;
            TextBlock keyInfo = null;
            if (needKey)
            {
                keyInfo = new TextBlock { Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 340, Text = L.T("Sprawdzam klucz sprzętowy…") };
                sp.Children.Add(keyInfo);
                UpdateKeyPresence(keyInfo, true);
            }
            var err = new TextBlock { Foreground = Brushes.Firebrick, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 340 };
            sp.Children.Add(err);
            var ok = BankButtons(sp, w, L.T("Otwórz"));
            bool result = false;
            ok.Click += async (s, e) =>
            {
                ok.IsEnabled = false; err.Text = "";
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
                    byte[] hmac = null; BankKey used = null;
                    if (needKey)
                    {
                        err.Text = L.T("Dotknij klucza sprzętowego (okienko Windows)…");
                        try
                        {
                            var hwnd = new WindowInteropHelper(w).Handle;
                            var allowed = c.Keys.ToList();
                            var salt2 = string.IsNullOrEmpty(c.HmacSalt) ? null : Convert.FromBase64String(c.HmacSalt);
                            var r = await Task.Run(() => WebAuthn.Verify(hwnd, BankRpId, allowed, salt2));
                            if (r.Key == null) { err.Text = L.T("Ten klucz nie jest dodany do trybu bankowego."); return; }
                            used = r.Key; hmac = r.Hmac;
                        }
                        catch (Exception ex) { err.Text = ex.Message; return; }
                    }
                    if (string.IsNullOrEmpty(c.CardSalt)) c.CardSalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
                    var pwKey = await Task.Run(() => CardKey(pw, c));
                    ForgetBankKey();
                    if (string.IsNullOrEmpty(c.WrapP))
                    {
                        // starszy zapis (karty zaszyfrowane samym kluczem z hasla) - przejscie na nowy uklad
                        _bankKey = pwKey; var old = LoadCards(c);
                        _bankKp = RandomNumberGenerator.GetBytes(32);
                        c.WrapP = Wrap(_bankKp, pwKey);
                        _bankKey = CardKeyFrom(_bankKp, null);
                        c.Cards = old.Count > 0 ? SealCards(_bankKey, old) : null;
                        foreach (var k in c.Keys) k.WrapK = null;
                    }
                    else _bankKp = Unwrap(c.WrapP, pwKey);
                    if (string.IsNullOrEmpty(c.HmacSalt)) c.HmacSalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                    SaveBank(c);
                    bool cardsNeedKey = c.UseKey && c.Keys.Any(k => k.WrapK != null);
                    if (cardsNeedKey)
                    {
                        if (used != null && used.WrapK != null && hmac != null) { try { _bankKk = Unwrap(used.WrapK, KekFromHmac(hmac)); } catch (Exception) { _bankKk = null; } }
                        _bankKey = _bankKk != null ? CardKeyFrom(_bankKp, _bankKk) : null;
                        if (_bankKey == null) ShowToast(L.T("Ten klucz otwiera tryb, ale nie odszyfrowuje kart – użyj klucza, który szyfruje karty"), null);
                    }
                    else _bankKey = CardKeyFrom(_bankKp, null);
                    _bankFails = 0; _bankUnlocked = true; result = true; w.Close();
                }
                catch (Exception ex) { err.Text = ex.Message; }
                finally { ok.IsEnabled = true; }
            };
            pass.Focus();
            w.ShowDialog();
            return result;
        }

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
            var c = existing ?? new BankConfig { Iter = 600000 };
            var keys = c.Keys.Select(k => new BankKey { Id = k.Id, X = k.X, Y = k.Y, Name = k.Name, WrapK = k.WrapK }).ToList();
            if (string.IsNullOrEmpty(c.HmacSalt)) c.HmacSalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            if (existing != null && !string.IsNullOrEmpty(c.Cards) && _bankKey == null)
            {
                MessageBox.Show(this, L.T("Karty są zamknięte – otwórz tryb bankowy kluczem, który szyfruje karty, i wtedy zmień ustawienia."), "Velivo");
                return false;
            }
            byte[] kk = _bankKk != null ? (byte[])_bankKk.Clone() : null;   // czesc klucza kart z kluczy sprzetowych
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
                        k.WrapK = Wrap(kk, KekFromHmac(hmac));
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
                List<BankCard> oldCards = new List<BankCard>();
                if (existing != null) { try { oldCards = LoadCards(c); } catch (Exception) { err.Text = L.T("Nie udało się odczytać kart."); return; } }
                byte[] kp = _bankKp != null ? (byte[])_bankKp.Clone() : RandomNumberGenerator.GetBytes(32);
                if (a.Length > 0)
                {
                    var salt = RandomNumberGenerator.GetBytes(16);
                    c.Salt = Convert.ToBase64String(salt); c.Iter = 600000;
                    c.Hash = Convert.ToBase64String(BankHash(a, salt, c.Iter));
                    c.CardSalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
                    c.WrapP = Wrap(kp, CardKey(a, c));
                }
                bool withKey = useKey.IsChecked == true;
                if (!withKey || !keys.Any(k => k.WrapK != null)) { kk = null; foreach (var k in keys) k.WrapK = null; }
                // karty przeszyfrowane nowym kluczem (haslo + ewentualnie klucz sprzetowy)
                ForgetBankKey();
                _bankKp = kp; _bankKk = kk; _bankKey = CardKeyFrom(kp, kk);
                c.Cards = oldCards.Count > 0 ? SealCards(_bankKey, oldCards) : null;
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

        // ---------- synchronizacja w sieci (LAN) ----------
        // Przesylany jest tylko bank.json: skrot hasla, klucze publiczne kluczy sprzetowych i karty ZASZYFROWANE
        // (haslem, a przy kluczu sprzetowym takze jego sekretem). Pakiet sieciowy jest dodatkowo szyfrowany parowaniem.
        // Logowania i ciasteczka banku zostaja na kazdym komputerze osobno.
        void ApplySyncedBank(string json)
        {
            try
            {
                var c = JsonSerializer.Deserialize<BankConfig>(json);
                if (c == null || string.IsNullOrEmpty(c.Hash) || string.IsNullOrEmpty(c.Salt)) return;
                if (json == ReadTextOrEmpty(BankFile)) return;
                File.WriteAllText(BankFile, json);
                // haslo / klucze / karty mogly sie zmienic - otwarty tryb zamykamy, otworzysz go ponownie
                if (_bankUnlocked) LockBank(L.T("🔒 Tryb bankowy zmieniony na innym komputerze – otwórz go ponownie"));
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
