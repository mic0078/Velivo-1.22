using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace Przegladarka
{
    // Ustawienia zapisywane w %LOCALAPPDATA%\Przegladarka\ustawienia.txt (klucz=wartosc).
    public sealed class AppSettings
    {
        public string Search = "startpage";
        public string Home = "https://startpage.com";
        public bool SendDnt = true;            // naglowki DNT: 1 i Sec-GPC: 1
        public bool StrictTracking = false;    // ochrona przed sledzeniem: false = zrownowazona (domyslna), true = scisla
        public bool SaveHistory = true;
        public bool ClearOnExit = false;       // przy zamknieciu: historia + cache (bez wylogowywania kont)
        public bool SavePasswords = true;
        public bool Autofill = true;
        public bool SmartScreen = true;        // ostrzezenia o niebezpiecznych stronach
        public bool AskDownload = true;       // pytaj, gdzie zapisac plik
        public int Connections = 8;            // polaczen na jeden plik w menedzerze pobierania (1-16)
        public int DefaultZoom = 100;          // domyslne powiekszenie stron w %
        public bool DarkPages = false;         // tryb ciemny stron
        public bool LinksInSameTab = true;     // linki otwierane przez strone w nowej karcie (target=_blank) -> w tej samej karcie
        public bool RestoreTabs = true;        // przywracaj karty po ponownym uruchomieniu
        public bool FullFilterLists = true;    // pelne listy AdBlocka (EasyList, EasyPrivacy, polska)
        public bool SejfLogins = true;         // kluczyk z loginami z Sejfu na stronach logowania
        public bool QuickAccessNewTab = true;
        public double ReadRate = 1.25;
        public double ReadVolume = 1.0;        // glosnosc czytania na glos (0-1)
        public bool NightLight = false;
        public string Theme = "jasny";
        public string Language = "auto";       // jezyk interfejsu: auto (jak Windows) / pl / en          // motyw przegladarki (Themes.cs)        // tryb nocny: cieplejsze kolory stron (jak Swiatlo nocne w Windows)         // predkosc czytania na glos
        public string ReadVoice = "";          // glos (pusty = pierwszy polski)
        public string CacheDir = "";           // wlasny folder na smieci (pusty = w profilu)
        public bool CleanJunkOnStart = false;  // usuwaj smieci przy kazdym uruchomieniu
        public bool BlockThirdPartyPopups = true;
        public string UiStyle = "modern";
        public int NightStrength = 40;          // natezenie trybu nocnego (5-100%)     // wyglad: modern (nowoczesny) / colorful (kolorowy)
        public bool AutoRejectCookies = true;
        public bool PageMemory = true;          // "Gdzie ja to czytalem?" - lokalna pamiec tresci stron
        public bool DarkPatterns = true;        // wykrywacz sztuczek presji w sklepach
        public bool PrivacyReceipt = true;      // paragon prywatnosci na tarczy   // samo klika "Odrzuc" / "Tylko niezbedne" na banerach zgod
        public bool MouseGestures = true;       // prawy przycisk + ruch myszy
        public bool PipButton = true;
        public bool VideoDownloadButton = true;
        public string VideoDir = "";          // ostatnio wybrany folder na filmy  // przycisk "Pobierz" nad filmami           // przycisk "obraz w obrazie" nad filmami
        public bool LanSync = true;            // bez sparowania dziala tryb zgodnosci (bez hasel); hasla tylko po sparowaniu
        public string LanSyncKey = "";
        public bool LanSyncSilent = false;     // bez dymkow przy automatycznym sync
        public bool ToolbarAlwaysCompact = true;  // zawsze kompaktowy pasek narzedzi

        static readonly byte[] LanSyncKeyEntropy = Encoding.UTF8.GetBytes("Velivo.LanSyncKey.v1");

        public static readonly Dictionary<string, string[]> Engines = new Dictionary<string, string[]>
        {
            { "duckduckgo", new[] { "DuckDuckGo (prywatna)", "https://duckduckgo.com/?q=" } },
            { "startpage",  new[] { "Startpage (prywatna, wyniki Google)", "https://www.startpage.com/do/search?q=" } },
            { "brave",      new[] { "Brave Search (prywatna)", "https://search.brave.com/search?q=" } },
            { "google",     new[] { "Google", "https://www.google.com/search?q=" } },
            { "bing",       new[] { "Bing", "https://www.bing.com/search?q=" } },
        };

        public string SearchUrl(string q)
        {
            string[] e;
            if (!Engines.TryGetValue(Search, out e)) e = Engines["duckduckgo"];
            return e[1] + Uri.EscapeDataString(q);
        }

        static string FilePath(string dir) { return Path.Combine(dir, "ustawienia.txt"); }
        static string LanSyncKeyPath(string dir) { return Path.Combine(dir, "lan-sync-key.dpapi"); }

        public static bool IsLanSyncKeyStrong(string key)
        {
            return !string.IsNullOrWhiteSpace(key) && Encoding.UTF8.GetByteCount(key.Trim()) >= 24;
        }

        static void SaveLanSyncKey(string dir, string key)
        {
            var path = LanSyncKeyPath(dir);
            if (string.IsNullOrEmpty(key))
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            Directory.CreateDirectory(dir);
            var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), LanSyncKeyEntropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, encrypted);
        }

        public static AppSettings Load(string dir)
        {
            var s = new AppSettings();
            string path = FilePath(dir);
            string[] lines = Array.Empty<string>();
            string legacyLanSyncKey = null;
            bool? lanSyncOverride = null;
            try
            {
                if (File.Exists(path)) lines = File.ReadAllLines(path);
                foreach (var line in lines)
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                    bool b = v == "1";
                    switch (k)
                    {
                        case "search": if (Engines.ContainsKey(v)) s.Search = v; break;
                        case "home": if (v.Length > 0) s.Home = v; break;
                        case "dnt": s.SendDnt = b; break;
                        case "tracking": s.StrictTracking = v == "strict"; break;
                        case "history": s.SaveHistory = b; break;
                        case "clearOnExit": s.ClearOnExit = b; break;
                        case "passwords": s.SavePasswords = b; break;
                        case "autofill": s.Autofill = b; break;
                        case "smartscreen": s.SmartScreen = b; break;
                        case "askDownload": s.AskDownload = b; break;
                        case "connections": int c; if (int.TryParse(v, out c)) s.Connections = Math.Max(1, Math.Min(16, c)); break;
                        case "zoom": int z; if (int.TryParse(v, out z)) s.DefaultZoom = Math.Max(50, Math.Min(300, z)); break;
                        case "dark": s.DarkPages = b; break;
                        case "restore": s.RestoreTabs = b; break;
                        case "sameTab": s.LinksInSameTab = b; break;
                        case "fullLists": s.FullFilterLists = b; break;
                        case "sejfLogins": s.SejfLogins = b; break;
                        case "quickAccessTab": s.QuickAccessNewTab = b; break;
                        case "readRate": double rr; if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out rr)) s.ReadRate = Math.Max(0.5, Math.Min(3, rr)); break;
                        case "readVoice": s.ReadVoice = v; break;
                        case "readVolume": double rv; if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out rv)) s.ReadVolume = Math.Max(0, Math.Min(1, rv)); break;
                        case "nightLight": s.NightLight = b; break;
                        case "theme": s.Theme = v; break;
                        case "language": s.Language = v; break;
                        case "popups": s.BlockThirdPartyPopups = b; break;
                        case "cookieReject": s.AutoRejectCookies = b; break;
                        case "nightStrength": { int ns; if (int.TryParse(v, out ns)) s.NightStrength = Math.Max(5, Math.Min(100, ns)); } break;
                        case "uiStyle": s.UiStyle = v == "colorful" ? "colorful" : "modern"; break;
                        case "pageMemory": s.PageMemory = b; break;
                        case "darkPatterns": s.DarkPatterns = b; break;
                        case "privacyReceipt": s.PrivacyReceipt = b; break;
                        case "gestures": s.MouseGestures = b; break;
                        case "pipBtn": s.PipButton = b; break;
                        case "videoDlBtn": s.VideoDownloadButton = b; break;
                        case "videoDir": s.VideoDir = v; break;
                        case "cacheDir": s.CacheDir = v; break;
                        case "cleanJunk": s.CleanJunkOnStart = b; break;
                        case "lanSync": s.LanSync = b; lanSyncOverride = b; break;
                        case "lanSyncKey": legacyLanSyncKey = v; break;
                        case "lanSyncSilent": s.LanSyncSilent = b; break;
                        case "toolbarCompact": s.ToolbarAlwaysCompact = b; break;
                    }
                }
            }
            catch (IOException) { }

            if (!lanSyncOverride.HasValue)
            {
                var profileRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Przegladarka");
                try
                {
                    if (!string.Equals(Path.GetFullPath(dir), Path.GetFullPath(profileRoot), StringComparison.OrdinalIgnoreCase))
                    {
                        var globalSettings = FilePath(profileRoot);
                        if (File.Exists(globalSettings))
                        {
                            var globalLanSetting = File.ReadAllLines(globalSettings)
                                .FirstOrDefault(line => line.StartsWith("lanSync=", StringComparison.OrdinalIgnoreCase));
                            if (globalLanSetting != null)
                                s.LanSync = globalLanSetting.Substring("lanSync=".Length).Trim() == "1";
                        }
                    }
                }
                catch (IOException) { }
            }

            try
            {
                var keyPath = LanSyncKeyPath(dir);
                if (File.Exists(keyPath))
                    s.LanSyncKey = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(keyPath), LanSyncKeyEntropy, DataProtectionScope.CurrentUser));
                else if (!string.IsNullOrWhiteSpace(legacyLanSyncKey))
                {
                    SaveLanSyncKey(dir, legacyLanSyncKey);
                    s.LanSyncKey = legacyLanSyncKey;
                }

                if (legacyLanSyncKey != null && File.Exists(path))
                    File.WriteAllLines(path, lines.Where(line => !line.StartsWith("lanSyncKey=", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex) { App.LogError(ex); }
            return s;
        }

        public void Save(string dir)
        {
            Func<bool, string> B = x => x ? "1" : "0";
            Directory.CreateDirectory(dir);
            SaveLanSyncKey(dir, LanSyncKey);
            File.WriteAllLines(FilePath(dir), new[]
            {
                "search=" + Search, "home=" + Home, "dnt=" + B(SendDnt), "tracking=" + (StrictTracking ? "strict" : "balanced"),
                "history=" + B(SaveHistory), "clearOnExit=" + B(ClearOnExit), "passwords=" + B(SavePasswords),
                "autofill=" + B(Autofill), "smartscreen=" + B(SmartScreen), "askDownload=" + B(AskDownload),
                "popups=" + B(BlockThirdPartyPopups), "cookieReject=" + B(AutoRejectCookies), "uiStyle=" + (UiStyle ?? "modern"), "nightStrength=" + NightStrength, "pageMemory=" + B(PageMemory), "darkPatterns=" + B(DarkPatterns), "privacyReceipt=" + B(PrivacyReceipt), "gestures=" + B(MouseGestures), "pipBtn=" + B(PipButton), "videoDlBtn=" + B(VideoDownloadButton), "videoDir=" + (VideoDir ?? ""), "cacheDir=" + (CacheDir ?? ""), "cleanJunk=" + B(CleanJunkOnStart), "connections=" + Connections, "zoom=" + DefaultZoom, "dark=" + B(DarkPages),
                "restore=" + B(RestoreTabs), "sameTab=" + B(LinksInSameTab), "fullLists=" + B(FullFilterLists), "sejfLogins=" + B(SejfLogins), "quickAccessTab=" + B(QuickAccessNewTab), "readRate=" + ReadRate.ToString(System.Globalization.CultureInfo.InvariantCulture), "readVoice=" + (ReadVoice ?? ""), "readVolume=" + ReadVolume.ToString(System.Globalization.CultureInfo.InvariantCulture), "nightLight=" + B(NightLight), "theme=" + (Theme ?? "jasny"), "language=" + (Language ?? "auto"),
                "lanSync=" + B(LanSync),
                "lanSyncSilent=" + B(LanSyncSilent),
                "toolbarCompact=" + B(ToolbarAlwaysCompact),
            });
        }
    }

    public partial class MainWindow
    {
        AppSettings _settings;

        // Ustawienia dotyczace jednej karty (wywolywane po utworzeniu i po zmianie ustawien).
        void ApplyViewSettings(CoreWebView2 core)
        {
            var st = core.Settings;
            st.IsPasswordAutosaveEnabled = _settings.SavePasswords;
            st.IsGeneralAutofillEnabled = _settings.Autofill;
            st.IsReputationCheckingRequired = false;
            UpdateTrackingLevel(_current != null && _current.View.CoreWebView2 != null ? _current.View.CoreWebView2.Source : core.Source);
        }

        void ApplySettingsToAllTabs()
        {
            foreach (var t in _tabs)
                if (t.View.CoreWebView2 != null) ApplyViewSettings(t.View.CoreWebView2);
        }

        async System.Threading.Tasks.Task ClearBrowsingData(CoreWebView2Profile profile, CoreWebView2BrowsingDataKinds kinds, bool includeHistoryFile)
        {
            if (profile != null)
            {
                if (kinds != 0)
                    await profile.ClearBrowsingDataAsync(kinds);
            }
            if (includeHistoryFile)
                try { File.Delete(HistoryFile); } catch (IOException) { }
        }

        async System.Threading.Tasks.Task ClearBrowsingDataOnExit(CoreWebView2Profile profile)
        {
            await ClearBrowsingData(profile,
                CoreWebView2BrowsingDataKinds.DiskCache |
                CoreWebView2BrowsingDataKinds.DownloadHistory |
                CoreWebView2BrowsingDataKinds.BrowsingHistory,
                true);
        }

        async System.Threading.Tasks.Task ClearBrowsingDataFull(CoreWebView2Profile profile)
        {
            await ClearBrowsingData(profile,
                CoreWebView2BrowsingDataKinds.AllProfile & ~CoreWebView2BrowsingDataKinds.Settings,
                true);
        }

        void Settings_Click(object sender, RoutedEventArgs e)
        {
            var s = _settings;
            bool darkChanged = false;
            var win = new Window
            {
                Title = L.T("Ustawienia"),
                Width = 1160,
                Height = 760,
                MinWidth = 940,
                MinHeight = 620,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResize,
                Background = Brushes.White
            };
            var root = new StackPanel { Margin = new Thickness(16) };
            Func<string, TextBlock> Header = t => new TextBlock { Text = t, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 12, 0, 4) };
            Func<string, string, bool, CheckBox> Check = (t, tip, v) => new CheckBox { Content = t, ToolTip = tip, IsChecked = v, Margin = new Thickness(0, 3, 0, 3) };

            root.Children.Add(Header(L.T("Domyślna przeglądarka")));
            bool isDefault = IsDefaultBrowser();
            var defInfo = new TextBlock
            {
                Text = isDefault ? L.T("✓ Velivo jest domyślną przeglądarką.") : L.T("Velivo nie jest teraz domyślną przeglądarką."),
                Foreground = isDefault ? Brushes.SeaGreen : Brushes.Gray, Margin = new Thickness(0, 0, 0, 4)
            };
            root.Children.Add(defInfo);
            if (!isDefault)
            {
                var makeDefault = SmallButton(L.T("Ustaw Velivo jako domyślną przeglądarkę…"), () => MakeDefaultBrowser(win));
                makeDefault.HorizontalAlignment = HorizontalAlignment.Left; makeDefault.Margin = new Thickness(0);
                root.Children.Add(makeDefault);
            }

            root.Children.Add(Header(L.T("Wygląd i czytelność")));
            root.Children.Add(new TextBlock { Text = L.T("Domyślne powiększenie stron (każdą stronę możesz też powiększyć osobno: Ctrl + kółko myszy):") , TextWrapping = TextWrapping.Wrap });
            var zoom = new ComboBox { Width = 120, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 4) };
            foreach (var zv in new[] { 80, 90, 100, 110, 125, 150, 175, 200 })
            {
                var it = new ComboBoxItem { Content = zv + "%", Tag = zv };
                zoom.Items.Add(it);
                if (zv == s.DefaultZoom) zoom.SelectedItem = it;
            }
            if (zoom.SelectedItem == null) zoom.SelectedIndex = 2;
            root.Children.Add(zoom);
            var dark = Check(L.T("Tryb ciemny stron (przycisk z księżycem na pasku)"), L.T("Strony z własnym ciemnym wyglądem przełączają się na niego, pozostałe jasne strony są przyciemniane."), s.DarkPages);
            root.Children.Add(dark);
            var night = Check(L.T("Tryb nocny – cieplejsze kolory stron (jak Światło nocne w Windows)"), L.T("Mniej niebieskiego światła wieczorem. Przycisk z księżycem przełącza: jasny → ciemny → nocny."), s.NightLight);
            root.Children.Add(night);
            root.Children.Add(new TextBlock { Text = L.T("Motyw przeglądarki (kolory pasków i kart):"), Margin = new Thickness(0, 6, 0, 2) });
            string origTheme = s.Theme; bool themeSaved = false;
            var theme = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var kv in BrowserThemes)
            {
                var it = new ComboBoxItem { Content = L.T(kv.Value.Name), Tag = kv.Key };
                theme.Items.Add(it);
                if (string.Equals(kv.Key, s.Theme, StringComparison.OrdinalIgnoreCase)) theme.SelectedItem = it;
            }
            if (theme.SelectedItem == null) theme.SelectedIndex = 0;
            // podglad na zywo przy wyborze
            theme.SelectionChanged += (a, b) => { if (theme.SelectedItem is ComboBoxItem ci) { _settings.Theme = (string)ci.Tag; ApplyBrowserTheme(); } };
            root.Children.Add(theme);
            root.Children.Add(new TextBlock { Text = L.T("Styl wyglądu:"), Margin = new Thickness(0, 6, 0, 2) });
            var styleBox = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
            styleBox.Items.Add(new ComboBoxItem { Content = L.T("Nowoczesny (spokojny, jak Windows 11)"), Tag = "modern" });
            styleBox.Items.Add(new ComboBoxItem { Content = L.T("Kolorowy (kolorowe przyciski)"), Tag = "colorful" });
            styleBox.SelectedIndex = s.UiStyle == "colorful" ? 1 : 0;
            styleBox.SelectionChanged += (a, b) => { _settings.UiStyle = (string)((ComboBoxItem)styleBox.SelectedItem).Tag; ApplyUiStyle(); };
            root.Children.Add(styleBox);
            root.Children.Add(new TextBlock { Text = L.T("Język interfejsu / Language:"), Margin = new Thickness(0, 6, 0, 2) });
            var langBox = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var opt in new[] { new[] { "auto", L.T("Automatycznie (język z instalatora / Windows)") }, new[] { "pl", "Polski" }, new[] { "en", "English" } })
            {
                var it = new ComboBoxItem { Content = opt[1], Tag = opt[0] };
                langBox.Items.Add(it);
                if (string.Equals(opt[0], s.Language ?? "auto", StringComparison.OrdinalIgnoreCase)) langBox.SelectedItem = it;
            }
            if (langBox.SelectedItem == null) langBox.SelectedIndex = 0;
            root.Children.Add(langBox);
            dark.Checked += (a, b) => night.IsChecked = false;
            night.Checked += (a, b) => dark.IsChecked = false;
            var compactBar = Check(L.T("Zawsze kompaktowy pasek narzędzi"), L.T("Zmniejsza etykiety i przenosi część przycisków do menu „…”, nawet na szerokim oknie."), s.ToolbarAlwaysCompact);
            root.Children.Add(compactBar);
            root.Children.Add(new TextBlock { Text = L.T("Czytanie na głos – głos i prędkość:"), Margin = new Thickness(0, 6, 0, 2) });
            var voiceRow = new StackPanel { Orientation = Orientation.Horizontal };
            var voice = new ComboBox { Width = 340, Margin = new Thickness(0, 0, 8, 0) };
            voice.Items.Add(new ComboBoxItem { Content = L.T("Automatycznie (język strony: polski/angielski)"), Tag = "" });
            foreach (var vn in _voiceNames)
            {
                // wpis: nazwa|jezyk|lokalny(1/0)
                var parts = vn.Split('|');
                string name = parts[0], lang = parts.Length > 1 ? parts[1] : "", local = parts.Length > 2 ? parts[2] : "1";
                string short_ = System.Text.RegularExpressions.Regex.Replace(name.Replace("Microsoft ", ""), @"\s*-\s*.*$", "");
                string label = short_ + (lang.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? L.T(" (angielski") : L.T(" (polski")) +
                               (local == "0" ? L.T(", online – naturalny)") : ")");
                voice.Items.Add(new ComboBoxItem { Content = label, Tag = name });
            }
            // naturalne glosy offline (Piper) - pobieraja sie przy pierwszym czytaniu
            foreach (var pv in PiperVoices)
                voice.Items.Add(new ComboBoxItem { Content = "★ " + L.T(pv.Label) + (PiperVoiceInstalled(pv) ? "" : L.T(" – pobierze ok. 60 MB")), Tag = "piper:" + pv.Id });
            voice.SelectedItem = System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.Cast<ComboBoxItem>(voice.Items), i => (string)i.Tag == (s.ReadVoice ?? "")) ?? voice.Items[0];
            var rate = new ComboBox { Width = 90 };
            foreach (var rv in ReadRates) { var it = new ComboBoxItem { Content = rv.ToString("0.##") + "×", Tag = rv }; rate.Items.Add(it); if (Math.Abs(rv - s.ReadRate) < 0.01) rate.SelectedItem = it; }
            if (rate.SelectedItem == null) rate.SelectedIndex = 1;
            voiceRow.Children.Add(voice); voiceRow.Children.Add(rate);
            root.Children.Add(voiceRow);
            var restore = Check(L.T("Po uruchomieniu przywracaj karty z poprzedniej sesji"), L.T("Karty prywatne nigdy nie są zapisywane. Zamkniętą kartę przywrócisz też skrótem Ctrl+Shift+T."), s.RestoreTabs);
            root.Children.Add(restore);
            var sameTab = Check(L.T("Otwieraj linki w tej samej karcie"), L.T("Linki, które strona chce otworzyć w nowej karcie, otwierają się w bieżącej - działa Wstecz i Dalej. Ctrl+klik dalej otwiera nową kartę. Wyłączone: jak w innych przeglądarkach."), s.LinksInSameTab);
            root.Children.Add(sameTab);

            root.Children.Add(Header(L.T("Wyszukiwanie i start")));
            var kwBtn = SmallButton(L.T("Skróty wyszukiwania (np. „yt koty”)…"), () => EditSearchKeywords(win));
            kwBtn.HorizontalAlignment = HorizontalAlignment.Left; kwBtn.Margin = new Thickness(0, 0, 0, 6);
            var engine = new ComboBox { Margin = new Thickness(0, 2, 0, 6) };
            foreach (var kv in AppSettings.Engines)
            {
                var it = new ComboBoxItem { Content = kv.Value[0], Tag = kv.Key };
                engine.Items.Add(it);
                if (kv.Key == s.Search) engine.SelectedItem = it;
            }
            root.Children.Add(new TextBlock { Text = L.T("Wyszukiwarka w pasku adresu:") });
            root.Children.Add(engine);
            root.Children.Add(kwBtn);
            var gestures = Check(L.T("Gesty myszy (prawy przycisk + ruch)"), L.T("Przytrzymaj prawy przycisk i przesuń: ← wstecz, → dalej, ↑ nowa karta, ↓ zamknij kartę, ↓→ odśwież. Zwykły prawy klik otwiera menu jak zawsze."), s.MouseGestures);
            var pipBtn = Check(L.T("Przycisk „Obraz w obrazie” nad filmami"), L.T("Po najechaniu myszką na film pojawia się przycisk ⧉ – film przechodzi do małego okienka zawsze na wierzchu."), s.PipButton);
            var dlBtnBox = Check(L.T("Przycisk „Pobierz” nad filmami"), L.T("Jak Internet Download Manager: po najechaniu na film pojawia się ⬇ Pobierz. Zwykłe pliki pobiera menedżer Velivo (do 16 połączeń), YouTube i strumienie – darmowe narzędzie yt-dlp."), s.VideoDownloadButton);
            root.Children.Add(gestures); root.Children.Add(pipBtn); root.Children.Add(dlBtnBox);
            root.Children.Add(new TextBlock { Text = L.T("Strona startowa:") });
            var home = new TextBox { Text = s.Home, Padding = new Thickness(4), Margin = new Thickness(0, 2, 0, 0) };
            root.Children.Add(home);
            bool quickAccessPresent = System.Linq.Enumerable.Any(_extInfos, i => IsQuickAccessExtensionId(i.Id) && i.NewTab != null);
            var quickAccess = Check(L.T("Szybki Dostęp jako strona nowej karty"), L.T("Używa osobnych danych Velivo w folderze danych przeglądarki; Szybki Dostęp Sejfu w innych przeglądarkach pozostaje osobny."), s.QuickAccessNewTab);
            quickAccess.IsEnabled = quickAccessPresent;
            root.Children.Add(quickAccess);
            root.Children.Add(new TextBlock { Text = L.T("Folder rozszerzenia do ręcznej instalacji w innych przeglądarkach:"), Margin = new Thickness(0, 4, 0, 2) });
            root.Children.Add(new TextBox
            {
                Text = Path.Combine(AppContext.BaseDirectory, "Dodatki", "Szybki Dostęp"),
                IsReadOnly = true,
                Padding = new Thickness(4),
                ToolTip = L.T("Wybierz ścieżkę i skopiuj ją do okna ładowania rozpakowanego rozszerzenia.")
            });

            root.Children.Add(Header(L.T("Prywatność")));
            var dnt = Check(L.T("Wysyłaj sygnały „Nie śledź” (DNT i Global Privacy Control)"), L.T("Strony dostają prośbę o niesprzedawanie i nieśledzenie Twoich danych."), s.SendDnt);
            var trackPanel = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };
            trackPanel.Children.Add(new TextBlock { Text = L.T("Ochrona przed śledzeniem:"), Margin = new Thickness(0, 0, 0, 2) });
            var trackBox = new ComboBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
            trackBox.Items.Add(new ComboBoxItem { Content = L.T("Ochrona zrównoważona (zalecana)"), Tag = "balanced" });
            trackBox.Items.Add(new ComboBoxItem { Content = L.T("Ścisła ochrona"), Tag = "strict" });
            trackBox.SelectedIndex = s.StrictTracking ? 1 : 0;
            trackBox.ToolTip = L.T("Zrównoważona: blokuje znane trackery, a osadzone treści (np. wpisy z X, filmy) działają. Ścisła: blokuje też osadzone treści serwisów społecznościowych. Na zaufanych domenach ścisła działa jak zrównoważona, chyba że zaznaczysz dla domeny „Wymuś blokowanie trackerów”.");
            trackPanel.Children.Add(trackBox);
            var hist = Check(L.T("Zapisuj historię przeglądania"), null, s.SaveHistory);
            var clear = Check(L.T("Czyść dane przy zamknięciu (historia i pamięć podręczna)"), L.T("Czyści historię i cache przy zamknięciu, ale nie wylogowuje kont ani nie usuwa zapisanych logowań."), s.ClearOnExit);
            bool sejfFound = FindSejfMost() != null;
            var sejf = Check(L.T("Loginy z Sejfu: kluczyk na pasku na stronach logowania") + (sejfFound ? "" : L.T(" (nie znaleziono Sejfu)")),
                L.T("Gdy strona ma pole hasła, Velivo pyta Sejf o loginy dla tej strony. Kliknięcie kluczyka wypełnia formularz."), s.SejfLogins && sejfFound);
            sejf.IsEnabled = sejfFound;
            root.Children.Add(sejf);
            var pw = Check(L.T("Proponuj zapisywanie haseł"), null, s.SavePasswords);
            var af = Check(L.T("Autouzupełnianie formularzy (adresy i karty, lokalna szyfrowana baza)"), L.T("Dane formularzy i kart są zapisywane lokalnie w szyfrowanej bazie offline. Hasła dalej obsługuje Sejf."), s.Autofill);
            var pop = Check(L.T("Blokuj wyskakujące okna otwierane bez kliknięcia"), null, s.BlockThirdPartyPopups);
            var cookieRej = Check(L.T("Automatycznie odrzucaj banery z ciasteczkami (RODO)"), L.T("Velivo samo klika „Odrzuć” albo „Tylko niezbędne”. Gdy baner nie ma takiego przycisku, nic nie jest klikane. Wyjątek dla strony: prawy przycisk na stronie."), s.AutoRejectCookies);
            root.Children.Add(dnt); root.Children.Add(trackPanel);
            var memory = Check(L.T("Zapamiętuj treść przeczytanych stron (szukanie: Ctrl+Shift+F)"), L.T("„Gdzie ja to czytałem?” – tekst stron zostaje tylko na tym komputerze. Pomijane są karty prywatne, banki, płatności, poczta i strony z polem hasła."), s.PageMemory);
            var darkP = Check(L.T("Ostrzegaj przed sztuczkami presji w sklepach"), L.T("Fałszywe liczniki, „ostatnie sztuki”, „X osób ogląda”, zaznaczone z góry dodatki (Velivo je odznacza) i ukryte opłaty."), s.DarkPatterns);
            var receiptBox = Check(L.T("Paragon prywatności na tarczy"), L.T("Po kliknięciu tarczy: z iloma firmami i krajami łączyła się strona, brokerzy danych i próby rozpoznania komputera."), s.PrivacyReceipt);
            foreach (var c in new[] { hist, clear, pw, af, pop, cookieRej, memory, darkP, receiptBox }) root.Children.Add(c);

            var autofillTools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            var afShow = SmallButton(L.T("Pokaż zapisane dane…"), null);
            afShow.Margin = new Thickness(0, 0, 6, 0);
            afShow.Click += (a, b) => OpenAutofillDataViewer(win);
            var afClearCards = SmallButton(L.T("Usuń zapisane karty"), null);
            afClearCards.Margin = new Thickness(0, 0, 6, 0);
            afClearCards.Click += (a, b) => DeleteAutofillCards(win);
            var afClearAddr = SmallButton(L.T("Usuń zapisane adresy"), null);
            afClearAddr.Click += (a, b) => DeleteAutofillAddresses(win);
            autofillTools.Children.Add(afShow);
            autofillTools.Children.Add(afClearCards);
            autofillTools.Children.Add(afClearAddr);
            root.Children.Add(autofillTools);

            var passTools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var passMgr = SmallButton(L.T("Menedżer haseł lokalnych…"), null);
            passMgr.Margin = new Thickness(0, 0, 6, 0);
            passMgr.Click += (a, b) => OpenPasswordsManager(win);
            var passImport = SmallButton(L.T("Import haseł CSV…"), null);
            passImport.Margin = new Thickness(0, 0, 6, 0);
            passImport.Click += (a, b) => ImportPasswordsCsvWithDialog(win);
            var passExport = SmallButton(L.T("Eksport haseł CSV…"), null);
            passExport.Click += (a, b) => ExportPasswordsCsvWithDialog(win);
            passTools.Children.Add(passMgr);
            passTools.Children.Add(passImport);
            passTools.Children.Add(passExport);
            root.Children.Add(passTools);

            root.Children.Add(Header(L.T("Blokowanie reklam")));
            var full = Check(L.T("Pełne listy filtrów (EasyList, EasyPrivacy, polska lista) – ok. 97 tys. reguł"), L.T("Listy pobierają się w tle i odświeżają co 4 dni."), s.FullFilterLists);
            root.Children.Add(full);
            var listInfo = new TextBlock { Text = L.T("Listy: ") + FilterListsInfo() + L.T(". Reguł w użyciu: ") + _blocker.RuleCount.ToString("N0") + (FilterStatus.Length > 0 ? "\n" + FilterStatus : ""), FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(0, 2, 0, 4) };
            root.Children.Add(listInfo);
            var updLists = SmallButton(L.T("Aktualizuj listy teraz"), null);
            updLists.HorizontalAlignment = HorizontalAlignment.Left; updLists.Margin = new Thickness(0);
            updLists.Click += async (a, b) =>
            {
                updLists.IsEnabled = false; listInfo.Text = L.T("Pobieram listy…");
                int ok = await System.Threading.Tasks.Task.Run(DownloadFilterLists);
                await RebuildBlocker();
                listInfo.Text = L.T("Pobrano ") + ok + L.T(" z 3 list. Reguł w użyciu: ") + _blocker.RuleCount.ToString("N0") + (FilterStatus.Length > 0 ? "\n" + FilterStatus : "");
                updLists.IsEnabled = true;
            };
            root.Children.Add(updLists);

            root.Children.Add(Header(L.T("Bezpieczeństwo i pobieranie")));
            var ss = Check(L.T("Ostrzegaj przed niebezpiecznymi stronami i plikami (SmartScreen)"), L.T("Sprawdzanie adresów wysyła je do Microsoft. Zalecane – chroni przed wyłudzeniami."), s.SmartScreen);
            var ask = Check(L.T("Pytaj, gdzie zapisać każdy pobierany plik"), null, s.AskDownload);
            root.Children.Add(ss); root.Children.Add(ask);
            root.Children.Add(new TextBlock { Text = L.T("Połączeń na jeden pobierany plik (więcej = zwykle szybciej):"), Margin = new Thickness(0, 6, 0, 2) });
            var conns = new ComboBox { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var n in new[] { 1, 2, 4, 8, 12, 16 })
            {
                var it = new ComboBoxItem { Content = n == 1 ? L.T("1 (bez dzielenia)") : n.ToString(), Tag = n };
                conns.Items.Add(it);
                if (n == s.Connections) conns.SelectedItem = it;
            }
            if (conns.SelectedItem == null) conns.SelectedIndex = 3;
            root.Children.Add(conns);

            root.Children.Add(Header(L.T("Śmieci przeglądarki (pamięć podręczna)")));
            root.Children.Add(new TextBlock
            {
                Text = L.T("Cache stron, skompilowane skrypty i cache grafiki. Można je usuwać bez utraty logowań i danych dodatków."),
                TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.Gray
            });
            root.Children.Add(new TextBlock { Text = L.T("Folder na śmieci (puste = w profilu przeglądarki):"), Margin = new Thickness(0, 6, 0, 0) });
            var cacheBox = new TextBox { Text = s.CacheDir, Padding = new Thickness(4), Margin = new Thickness(0, 2, 6, 0) };
            var browse = SmallButton(L.T("Wybierz…"), null);
            browse.Click += (a, b) =>
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Folder na śmieci przeglądarki") };
                if (!string.IsNullOrWhiteSpace(cacheBox.Text) && Directory.Exists(cacheBox.Text)) dlg.InitialDirectory = cacheBox.Text;
                if (dlg.ShowDialog(win) == true) cacheBox.Text = dlg.FolderName;
            };
            var reset = SmallButton(L.T("Domyślny"), () => cacheBox.Text = "");
            var cacheRow = new DockPanel();
            DockPanel.SetDock(reset, Dock.Right); DockPanel.SetDock(browse, Dock.Right);
            cacheRow.Children.Add(reset); cacheRow.Children.Add(browse); cacheRow.Children.Add(cacheBox);
            root.Children.Add(cacheRow);
            root.Children.Add(new TextBlock
            {
                Text = L.T("Program tworzy w nim podfolder „") + JunkSubfolder + L.T("” i usuwa tylko jego zawartość. Cache grafiki zostaje w profilu (wymóg silnika)."),
                TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(0, 2, 0, 0)
            });
            var cleanStart = Check(L.T("Usuwaj śmieci przy każdym uruchomieniu przeglądarki"), L.T("Strony wczytają się za pierwszym razem odrobinę wolniej."), s.CleanJunkOnStart);
            root.Children.Add(cleanStart);
            var junkInfo = new TextBlock { Text = L.T("Teraz zajmują: ") + Mb(JunkSize()), Margin = new Thickness(0, 2, 0, 4) };
            var cleanJunk = SmallButton(L.T("Wyczyść śmieci teraz"), null);
            cleanJunk.HorizontalAlignment = HorizontalAlignment.Left; cleanJunk.Margin = new Thickness(0);
            cleanJunk.Click += async (a, b) =>
            {
                try
                {
                    var freed = await CleanJunkNow();
                    junkInfo.Text = L.T("Zwolniono ") + Mb(freed) + L.T(". Teraz zajmują: ") + Mb(JunkSize()) + L.T(" (cache grafiki zniknie przy następnym uruchomieniu).");
                }
                catch (Exception ex) { MessageBox.Show(win, ex.Message, L.T("Ustawienia")); }
            };
            root.Children.Add(junkInfo);
            root.Children.Add(cleanJunk);

            root.Children.Add(Header(L.T("Dane")));
            var clearNow = SmallButton(L.T("Wyczyść dane przeglądania teraz…"), null);
            clearNow.HorizontalAlignment = HorizontalAlignment.Left; clearNow.Margin = new Thickness(0);
            clearNow.Click += async (a, b) =>
            {
                var dlg = new Window
                {
                    Title = L.T("Wyczyść dane przeglądania"),
                    Width = 500,
                    Height = 430,
                    MinWidth = 460,
                    MinHeight = 380,
                    Owner = win,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    ResizeMode = ResizeMode.NoResize
                };

                var panel = new StackPanel { Margin = new Thickness(14) };
                panel.Children.Add(new TextBlock
                {
                    Text = L.T("Wybierz, co usunąć:"),
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 8)
                });

                var cHist = new CheckBox { Content = L.T("Historia przeglądania"), IsChecked = true, Margin = new Thickness(0, 3, 0, 3) };
                var cCache = new CheckBox { Content = L.T("Pamięć podręczna (cache)"), IsChecked = true, Margin = new Thickness(0, 3, 0, 3) };
                var cDl = new CheckBox { Content = L.T("Historia pobrań"), IsChecked = false, Margin = new Thickness(0, 3, 0, 3) };
                var cCookies = new CheckBox { Content = L.T("Cookies i aktywne sesje (wyloguje konta)"), IsChecked = false, Margin = new Thickness(0, 3, 0, 3) };
                var cAutofill = new CheckBox { Content = L.T("Dane formularzy i kart zapisane przez silnik"), IsChecked = false, Margin = new Thickness(0, 3, 0, 3) };
                var cPasswords = new CheckBox { Content = L.T("Zapisane hasła w silniku"), IsChecked = false, Margin = new Thickness(0, 3, 0, 3) };
                panel.Children.Add(cHist);
                panel.Children.Add(cCache);
                panel.Children.Add(cDl);
                panel.Children.Add(cCookies);
                panel.Children.Add(cAutofill);
                panel.Children.Add(cPasswords);

                var warn = new TextBlock
                {
                    Text = L.T("Uwaga: usunięcie cookies/sesji spowoduje wylogowanie ze stron."),
                    Foreground = Brushes.DarkRed,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 10, 0, 0)
                };
                panel.Children.Add(warn);

                bool accepted = false;
                var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
                var okBtn = new Button { Content = L.T("Wyczyść"), Width = 96, Height = 30, Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
                var cancelBtn = new Button { Content = L.T("Anuluj"), Width = 96, Height = 30, IsCancel = true };
                okBtn.Click += (x, y) => { accepted = true; dlg.Close(); };
                cancelBtn.Click += (x, y) => dlg.Close();
                btnRow.Children.Add(okBtn);
                btnRow.Children.Add(cancelBtn);
                panel.Children.Add(btnRow);

                dlg.Content = panel;
                dlg.ShowDialog();
                if (!accepted) return;

                var kinds = (CoreWebView2BrowsingDataKinds)0;
                bool clearHistoryFile = false;
                if (cHist.IsChecked == true)
                {
                    kinds |= CoreWebView2BrowsingDataKinds.BrowsingHistory;
                    clearHistoryFile = true;
                }
                if (cCache.IsChecked == true) kinds |= CoreWebView2BrowsingDataKinds.DiskCache;
                if (cDl.IsChecked == true) kinds |= CoreWebView2BrowsingDataKinds.DownloadHistory;
                if (cCookies.IsChecked == true) kinds |= CoreWebView2BrowsingDataKinds.Cookies;
                if (cAutofill.IsChecked == true) kinds |= CoreWebView2BrowsingDataKinds.GeneralAutofill;
                if (cPasswords.IsChecked == true) kinds |= CoreWebView2BrowsingDataKinds.PasswordAutosave;

                if (kinds == 0 && !clearHistoryFile)
                {
                    MessageBox.Show(win, L.T("Nie zaznaczono żadnych danych do usunięcia."), L.T("Ustawienia"));
                    return;
                }

                try { await ClearBrowsingData(Core != null ? Core.Profile : null, kinds, clearHistoryFile); if (clearHistoryFile) { RememberHistoryCleared(); ForgetAllPageMemory(); } MessageBox.Show(win, L.T("Wyczyszczono zaznaczone dane."), L.T("Ustawienia")); }
                catch (Exception ex) { MessageBox.Show(win, ex.Message, L.T("Ustawienia")); }
            };
            root.Children.Add(clearNow);

            root.Children.Add(Header(L.T("Prywatność per-strona")));
            var privacyPanel = SmallButton(L.T("Panel prywatności i antyfingerprinting…"), null);
            privacyPanel.HorizontalAlignment = HorizontalAlignment.Left; privacyPanel.Margin = new Thickness(0);
            privacyPanel.Click += (a, b) => OpenPrivacyPanel();
            root.Children.Add(privacyPanel);

            root.Children.Add(Header(L.T("Profile użytkownika")));
            root.Children.Add(new TextBlock { Text = L.T("Aktywny profil: ") + SelectedProfileName, Margin = new Thickness(0, 0, 0, 4), Foreground = Brushes.Gray });
            var profileRow = new StackPanel { Orientation = Orientation.Horizontal };
            var pWork = SmallButton(L.T("Praca"), () => SwitchProfile("praca")); pWork.Margin = new Thickness(0, 0, 6, 0);
            var pPrivate = SmallButton(L.T("Prywatny"), () => SwitchProfile("prywatny")); pPrivate.Margin = new Thickness(0, 0, 6, 0);
            var pDev = SmallButton("Dev", () => SwitchProfile("dev"));
            profileRow.Children.Add(pWork); profileRow.Children.Add(pPrivate); profileRow.Children.Add(pDev);
            root.Children.Add(profileRow);
            var profileMgr = SmallButton(L.T("Zarządzaj użytkownikami/profilami…"), OpenProfilesManager);
            profileMgr.HorizontalAlignment = HorizontalAlignment.Left; profileMgr.Margin = new Thickness(0, 6, 0, 0);
            root.Children.Add(profileMgr);

            root.Children.Add(Header(L.T("Synchronizacja E2E")));
            root.Children.Add(new TextBlock
            {
                Text = L.T("Lokalny eksport/import zaszyfrowanej paczki (hasło + AES-GCM). Możesz przenieść plik na inne urządzenie."),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = Brushes.Gray
            });
            var syncRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var syncOut = SmallButton(L.T("Eksportuj paczkę…"), null); syncOut.Margin = new Thickness(0, 0, 6, 0);
            syncOut.Click += SyncExport_Click;
            var syncIn = SmallButton(L.T("Importuj paczkę…"), null);
            syncIn.Click += SyncImport_Click;
            syncRow.Children.Add(syncOut); syncRow.Children.Add(syncIn);
            root.Children.Add(syncRow);

            root.Children.Add(new TextBlock { Text = L.T("Synchronizacja w czasie rzeczywistym (LAN):"), Margin = new Thickness(0, 10, 0, 2) });
            var lanSync = Check(L.T("Włącz synchronizację między uruchomionymi Velivo w tej samej sieci lokalnej"), L.T("Synchronizuje ustawienia, zakładki, hasła, reguły prywatności i Szybki Dostęp dla tego samego profilu. Otwarte karty zostają na każdym komputerze osobno."), s.LanSync);
            _lanSyncSettingCheck = lanSync;
            win.Closed += (sender, args) => { if (ReferenceEquals(_lanSyncSettingCheck, lanSync)) _lanSyncSettingCheck = null; };
            root.Children.Add(lanSync);
            root.Children.Add(new TextBlock
            {
                Text = L.T("Włącz i zapisz synchronizację na obu komputerach. Sparuj je jednorazowo, porównując krótki kod; sekret zostanie zapisany lokalnie."),
                FontSize = 11,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 4)
            });
            var pairLan = SmallButton(L.T("Sparuj urządzenie w sieci…"), BeginLanPairing);
            pairLan.IsEnabled = s.LanSync && _lanTx != null;
            pairLan.HorizontalAlignment = HorizontalAlignment.Left;
            root.Children.Add(pairLan);
            var recoveryRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var exportRecovery = SmallButton(L.T("Zapisz plik odzyskiwania…"), ExportLanPairingRecovery);
            exportRecovery.Margin = new Thickness(0, 0, 6, 0);
            var importRecovery = SmallButton(L.T("Odtwórz parowanie…"), ImportLanPairingRecovery);
            recoveryRow.Children.Add(exportRecovery);
            recoveryRow.Children.Add(importRecovery);
            root.Children.Add(recoveryRow);
            var lanSilent = Check(L.T("Tryb cichy LAN (bez dymków „Zsynchronizowano profil…”)"), L.T("Log i panel diagnostyczny nadal działają, wyłączone są tylko wyskakujące komunikaty."), s.LanSyncSilent);
            root.Children.Add(lanSilent);
            var lanDiag = SmallButton(L.T("Panel diagnostyczny LAN…"), OpenLanDiagnosticsPanel);
            lanDiag.HorizontalAlignment = HorizontalAlignment.Left; lanDiag.Margin = new Thickness(0, 6, 0, 0);
            root.Children.Add(lanDiag);

            var ok = new Button { Content = L.T("Zapisz"), Width = 90, Height = 30, IsDefault = true, Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            ok.Click += (a, b) =>
            {
                s.Search = (string)((ComboBoxItem)engine.SelectedItem).Tag;
                s.Home = string.IsNullOrWhiteSpace(home.Text) ? "https://duckduckgo.com/" : ToUrl(home.Text);
                s.SendDnt = dnt.IsChecked == true; s.StrictTracking = trackBox.SelectedIndex == 1;
                s.SaveHistory = hist.IsChecked == true; s.ClearOnExit = clear.IsChecked == true;
                s.SavePasswords = pw.IsChecked == true; s.Autofill = af.IsChecked == true;
                s.BlockThirdPartyPopups = pop.IsChecked == true;
                bool scriptsChanged = s.AutoRejectCookies != (cookieRej.IsChecked == true) || s.MouseGestures != (gestures.IsChecked == true) || s.PipButton != (pipBtn.IsChecked == true)
                    || s.DarkPatterns != (darkP.IsChecked == true) || s.VideoDownloadButton != (dlBtnBox.IsChecked == true) || s.PrivacyReceipt != (receiptBox.IsChecked == true);
                s.PageMemory = memory.IsChecked == true; s.VideoDownloadButton = dlBtnBox.IsChecked == true; s.DarkPatterns = darkP.IsChecked == true; s.PrivacyReceipt = receiptBox.IsChecked == true;
                s.AutoRejectCookies = cookieRej.IsChecked == true; s.MouseGestures = gestures.IsChecked == true; s.PipButton = pipBtn.IsChecked == true;
                if (scriptsChanged) RefreshPageScripts();
                s.SmartScreen = ss.IsChecked == true; s.AskDownload = ask.IsChecked == true;
                s.CleanJunkOnStart = cleanStart.IsChecked == true;
                s.Connections = (int)((ComboBoxItem)conns.SelectedItem).Tag;
                int oldZoom = s.DefaultZoom; bool oldDark = s.DarkPages; bool oldFull = s.FullFilterLists;
                s.DefaultZoom = (int)((ComboBoxItem)zoom.SelectedItem).Tag;
                s.DarkPages = dark.IsChecked == true;
                s.Theme = (string)((ComboBoxItem)theme.SelectedItem).Tag;
                var newLang = (string)((ComboBoxItem)langBox.SelectedItem).Tag;
                if (!string.Equals(newLang, s.Language ?? "auto", StringComparison.OrdinalIgnoreCase))
                {
                    s.Language = newLang;
                    MessageBox.Show(win, L.T("Zmiana języka zadziała po ponownym uruchomieniu Velivo."), "Velivo");
                }
                bool oldNight = s.NightLight;
                s.NightLight = night.IsChecked == true && !s.DarkPages;
                if (oldNight != s.NightLight) { UpdateDarkButton(); darkChanged = true; }
                s.ToolbarAlwaysCompact = compactBar.IsChecked == true;
                s.RestoreTabs = restore.IsChecked == true; s.LinksInSameTab = sameTab.IsChecked == true;
                if (quickAccess.IsEnabled) s.QuickAccessNewTab = quickAccess.IsChecked == true;
                s.ReadVoice = (string)((ComboBoxItem)voice.SelectedItem).Tag;
                s.ReadRate = (double)((ComboBoxItem)rate.SelectedItem).Tag;
                s.FullFilterLists = full.IsChecked == true;
                s.LanSync = lanSync.IsChecked == true;
                s.LanSyncSilent = lanSilent.IsChecked == true;
                if (sejf.IsEnabled) s.SejfLogins = sejf.IsChecked == true;
                if (!s.SejfLogins) HideKey();
                if (oldZoom != s.DefaultZoom) foreach (var t in _tabs) ApplyZoom(t);
                if (oldDark != s.DarkPages) { foreach (var t in _tabs) ApplyDarkMode(t); UpdateDarkButton(); darkChanged = true; }
                if (oldFull != s.FullFilterLists) { if (s.FullFilterLists) StartFilterLists(); else RebuildBlocker(); }
                string newDir = cacheBox.Text.Trim();
                if (newDir.Length > 0)
                {
                    try
                    {
                        newDir = Path.GetFullPath(newDir);
                        var test = Path.Combine(newDir, JunkSubfolder);
                        Directory.CreateDirectory(test);
                        var probe = Path.Combine(test, ".test");
                        File.WriteAllText(probe, "ok"); File.Delete(probe);
                    }
                    catch (Exception ex) { MessageBox.Show(win, L.T("Nie można używać tego folderu na śmieci:\n") + ex.Message, L.T("Ustawienia")); return; }
                }
                bool dirChanged = !string.Equals(newDir, s.CacheDir ?? "", StringComparison.OrdinalIgnoreCase);
                s.CacheDir = newDir;
                themeSaved = true;
                try { s.Save(DataDir); }
                catch (Exception ex) { MessageBox.Show(win, L.T("Nie zapisano ustawień:\n") + ex.Message, L.T("Ustawienia")); return; }
                ApplySettingsToAllTabs();
                StopLanSync();
                StartLanSync();
                UpdateAdaptiveToolbarLayout();
                win.Close();
                if (dirChanged)
                    MessageBox.Show(this, L.T("Nowy folder na śmieci zacznie działać po ponownym uruchomieniu przeglądarki."), L.T("Ustawienia"));
            };
            root.Children.Add(ok);

            bool IsSectionHeader(TextBlock t)
            {
                return t != null && t.FontSize >= 14 && t.FontWeight == FontWeights.SemiBold;
            }

            var originalChildren = root.Children.Cast<UIElement>().ToList();
            root.Children.Clear();

            var sections = new List<Tuple<string, StackPanel>>();
            string currentTitle = "";
            StackPanel currentBody = null;

            foreach (var child in originalChildren)
            {
                if (ReferenceEquals(child, ok)) continue;
                var tb = child as TextBlock;
                if (IsSectionHeader(tb))
                {
                    currentTitle = tb.Text;
                    currentBody = new StackPanel { Margin = new Thickness(12, 10, 12, 12) };
                    sections.Add(Tuple.Create(currentTitle, currentBody));
                    continue;
                }
                if (currentBody != null) currentBody.Children.Add(child);
            }

            Border MakeCard(string title, StackPanel body)
            {
                var card = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0xF9, 0xFA, 0xFB)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Margin = new Thickness(0, 0, 0, 12)
                };
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock
                {
                    Text = title,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 15,
                    Margin = new Thickness(12, 10, 12, 0),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27))
                });
                panel.Children.Add(body);
                card.Child = panel;
                return card;
            }

            var leftCol = new StackPanel();
            var rightCol = new StackPanel();
            int leftWeight = 0, rightWeight = 0;
            foreach (var sec in sections)
            {
                var card = MakeCard(sec.Item1, sec.Item2);
                int w = Math.Max(1, sec.Item2.Children.Count);
                if (leftWeight <= rightWeight)
                {
                    leftCol.Children.Add(card);
                    leftWeight += w;
                }
                else
                {
                    rightCol.Children.Add(card);
                    rightWeight += w;
                }
            }

            var columns = new Grid { Margin = new Thickness(16, 8, 16, 8) };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(leftCol, 0);
            Grid.SetColumn(rightCol, 2);
            columns.Children.Add(leftCol);
            columns.Children.Add(rightCol);

            var footer = new DockPanel { Margin = new Thickness(16, 8, 16, 12) };
            ok.Margin = new Thickness(0);
            DockPanel.SetDock(ok, Dock.Right);
            footer.Children.Add(ok);

            var frame = new DockPanel();
            DockPanel.SetDock(footer, Dock.Bottom);
            frame.Children.Add(footer);
            frame.Children.Add(new ScrollViewer { Content = columns, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

            win.MaxHeight = SystemParameters.WorkArea.Height - 40;
            win.MaxWidth = SystemParameters.WorkArea.Width - 40;
            win.Content = frame;
            win.ShowDialog();
            if (!themeSaved && _settings.Theme != origTheme) { _settings.Theme = origTheme; ApplyBrowserTheme(); }   // zamknieto bez zapisu
            if (darkChanged) foreach (var t in _tabs) ApplyLiveDarkCss(t.View.CoreWebView2); // od razu, bez restartu
        }
    }
}
