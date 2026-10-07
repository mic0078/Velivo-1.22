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
        public string Tracking = "balanced";   // ochrona przed sledzeniem: balanced (domyslna) / strict / none (bez kontrolowania)
        public bool SaveHistory = true;
        public bool ClearOnExit = false;       // przy zamknieciu: historia + cache (bez wylogowywania kont)
        public bool SavePasswords = true;
        public bool Autofill = true;
        public bool SmartScreen = true;        // ostrzezenia o niebezpiecznych stronach
        public bool UbolLite = true;
        public bool StayInTray = false;
        public bool AskedDefaultBrowser = false;   // pytanie o domyslna przegladarke juz zadane (tylko ten komputer)
        public string ReaderTheme = "light";     // czytnik: light / dark / night
        public int ReaderNight = 40;            // natezenie trybu nocnego czytnika (5-100%)
        public string ReaderSize = "";          // ostatni rozmiar okna czytnika "szer;wys"        // po zamknieciu okna zostan w zasobniku (synchronizacja w tle, szybki start)
        public bool AudioGuard = true;         // dzwiek nie ginie, gdy program muzyczny zajmie karte
        public string AudioOut = "";           // wybrane glosniki Velivo ("" = domyslne Windows)           // wbudowany uBlock Origin Lite
        public bool AntiPhishing = true;       // wykrywanie stron-podrobek (offline)
        public bool HttpsFirst = true;         // najpierw HTTPS, strony bez szyfrowania tylko po ostrzezeniu
        public bool SafePayments = true;       // banki i platnosci: okno niewidoczne dla nagrywania ekranu
        public bool AskDownload = true;       // pytaj, gdzie zapisac plik
        public string LastDownloadDir = "";   // ostatnio wybrany folder w okienku zapisu
        public int Connections = 8;            // polaczen na jeden plik w menedzerze pobierania (1-16)
        public int DefaultZoom = 100;          // domyslne powiekszenie stron w %
        public bool DarkPages = false;         // tryb ciemny stron
        public bool LinksInSameTab = true;     // linki otwierane przez strone w nowej karcie (target=_blank) -> w tej samej karcie
        public bool RestoreTabs = false;       // przywracaj karty po ponownym uruchomieniu
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
        public bool SpeedUp = true;            // szybsze wczytywanie: pobieranie strony przy najechaniu na link, laczenie z wyprzedzeniem
        public int CacheMb = 0;                // rozmiar pamieci podrecznej (0 = automatycznie)
        public bool BlockThirdPartyPopups = true;
        public string UiStyle = "modern";
        public int PageFade = 300;
        public int PageEntranceMs = 1000;       // szybkosc efektu wejscia w ms (0 = wg plynnego przejscia)
        public string PageEntrance = "cinema";  // efekt wejscia tresci: slide / blur / zoom / none               // plynne pojawianie sie stron w ms (0 = tylko naturalne przejscie silnika)
        public int NightStrength = 40;          // natezenie trybu nocnego (5-100%)     // wyglad: modern (nowoczesny) / colorful (kolorowy)
        public bool AutoRejectCookies = true;
        public bool PageMemory = true;          // "Gdzie ja to czytalem?" - lokalna pamiec tresci stron
        public bool DarkPatterns = true;        // wykrywacz sztuczek presji w sklepach
        public bool PrivacyReceipt = true;      // paragon prywatnosci na tarczy   // samo klika "Odrzuc" / "Tylko niezbedne" na banerach zgod
        public bool MouseGestures = true;       // prawy przycisk + ruch myszy
        public bool PipButton = true;
        public bool VideoDownloadButton = true;
        public bool VideoPlayer = true;        // filmy z dysku w odtwarzaczu Velivo (offline)
        public bool PlayerAutoplay = true;     // odtwarzaj od razu po otwarciu
        public bool PlayerResume = true;       // wznawiaj od miejsca, w ktorym skonczyles
        public bool PlayerLoop = false;        // powtarzaj film w kolko
        public string PlayerSalt = "";         // losowa sol kluczy "miejsce w filmie" (tylko ten komputer)
        public bool Torrents = false;          // torrenty (magnet, .torrent) przez aria2 - wlaczane recznie
        public string TorrentDir = "";         // osobna strefa torrentow (pusty = Pobrane\Velivo-Torrenty)
        public int TorrentDownKb = 0;          // limit pobierania KB/s (0 = bez limitu)
        public int TorrentUpKb = 1024;         // limit wysylania KB/s (0 = bez limitu)
        public double TorrentRatio = 1.0;      // udostepnianie do wspolczynnika (0 = nie udostepniaj po pobraniu)
        public int TorrentSeedMin = 30;        // najdluzej udostepniaj (minuty)
        public int TorrentPeers = 60;          // najwiecej uczestnikow naraz
        public string VideoDir = "";
        public string FloatBounds = "";
        public int FloatOpacity = 100;
        public bool FloatTopmost = true;       // okienko filmu zawsze na wierzchu         // przezroczystosc okienka "film na wierzchu" (15-100%)      // miejsce i wielkosc okienka "film na wierzchu"          // ostatnio wybrany folder na filmy  // przycisk "Pobierz" nad filmami           // przycisk "obraz w obrazie" nad filmami
        public bool LanSync = true;            // bez sparowania dziala tryb zgodnosci (bez hasel); hasla tylko po sparowaniu
        public string LanSyncKey = "";
        public bool LanSyncSilent = false;     // bez dymkow przy automatycznym sync
        public bool ToolbarAlwaysCompact = false;  // zawsze kompaktowy pasek narzedzi

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
                        case "tracking": if (v == "balanced" || v == "strict" || v == "none") s.Tracking = v; break;
                        case "history": s.SaveHistory = b; break;
                        case "clearOnExit": s.ClearOnExit = b; break;
                        case "passwords": s.SavePasswords = b; break;
                        case "autofill": s.Autofill = b; break;
                        case "smartscreen": s.SmartScreen = b; break;
                        case "ubol": s.UbolLite = b; break;
                        case "audioGuard": s.AudioGuard = b; break;
                        case "tray": s.StayInTray = b; break;
                        case "askedDefault": s.AskedDefaultBrowser = b; break;
                        case "readerTheme": if (v == "light" || v == "dark" || v == "night") s.ReaderTheme = v; break;
                        case "readerNight": { int rn; if (int.TryParse(v, out rn)) s.ReaderNight = Math.Max(5, Math.Min(100, rn)); } break;
                        case "readerSize": s.ReaderSize = v; break;
                        case "audioOut": s.AudioOut = v; break;
                        case "antiPhishing": s.AntiPhishing = b; break;
                        case "httpsFirst": s.HttpsFirst = b; break;
                        case "safePay": s.SafePayments = b; break;
                        case "askDownload": s.AskDownload = b; break;
                        case "lastDlDir": s.LastDownloadDir = v; break;
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
                        case "pageEntranceMs": { int em; if (int.TryParse(v, out em)) s.PageEntranceMs = Math.Max(0, Math.Min(5000, em)); } break;
                        case "pageEntrance": if (v == "slide" || v == "zoom") s.PageEntrance = "blur"; else if (v == "blur" || v == "cinema" || v == "dim" || v == "none") s.PageEntrance = v; break;   // wysuniecie i przyblizenie usuniete
                        case "pageFade": { int pf; if (int.TryParse(v, out pf)) s.PageFade = Math.Max(0, Math.Min(4000, pf)); } break;
                        case "nightStrength": { int ns; if (int.TryParse(v, out ns)) s.NightStrength = Math.Max(5, Math.Min(100, ns)); } break;
                        case "uiStyle": s.UiStyle = v == "colorful" ? "colorful" : "modern"; break;
                        case "pageMemory": s.PageMemory = b; break;
                        case "darkPatterns": s.DarkPatterns = b; break;
                        case "privacyReceipt": s.PrivacyReceipt = b; break;
                        case "gestures": s.MouseGestures = b; break;
                        case "pipBtn": s.PipButton = b; break;
                        case "videoDlBtn": s.VideoDownloadButton = b; break;
                        case "player": s.VideoPlayer = b; break;
                        case "playerAuto": s.PlayerAutoplay = b; break;
                        case "playerResume": s.PlayerResume = b; break;
                        case "playerLoop": s.PlayerLoop = b; break;
                        case "playerSalt": s.PlayerSalt = v; break;
                        case "torrents": s.Torrents = b; break;
                        case "torrentDir": s.TorrentDir = v; break;
                        case "torrentDown": { int x; if (int.TryParse(v, out x)) s.TorrentDownKb = Math.Max(0, Math.Min(1000000, x)); } break;
                        case "torrentUp": { int x; if (int.TryParse(v, out x)) s.TorrentUpKb = Math.Max(0, Math.Min(1000000, x)); } break;
                        case "torrentRatio": { double x; if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x)) s.TorrentRatio = Math.Max(0, Math.Min(10, x)); } break;
                        case "torrentSeed": { int x; if (int.TryParse(v, out x)) s.TorrentSeedMin = Math.Max(1, Math.Min(1440, x)); } break;
                        case "torrentPeers": { int x; if (int.TryParse(v, out x)) s.TorrentPeers = Math.Max(5, Math.Min(200, x)); } break;
                        case "videoDir": s.VideoDir = v; break;
                        case "floatBounds": s.FloatBounds = v; break;
                        case "floatTop": s.FloatTopmost = b; break;
                        case "floatOpacity": { int fo; if (int.TryParse(v, out fo)) s.FloatOpacity = Math.Max(15, Math.Min(100, fo)); } break;
                        case "cacheDir": s.CacheDir = v; break;
                        case "cleanJunk": s.CleanJunkOnStart = b; break;
                        case "speedUp": s.SpeedUp = b; break;
                        case "cacheMb": { int cm; if (int.TryParse(v, out cm)) s.CacheMb = Math.Max(0, Math.Min(8192, cm)); } break;
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
                "search=" + Search, "home=" + Home, "dnt=" + B(SendDnt), "tracking=" + Tracking,
                "history=" + B(SaveHistory), "clearOnExit=" + B(ClearOnExit), "passwords=" + B(SavePasswords),
                "autofill=" + B(Autofill), "smartscreen=" + B(SmartScreen), "ubol=" + B(UbolLite), "audioGuard=" + B(AudioGuard), "tray=" + B(StayInTray), "askedDefault=" + B(AskedDefaultBrowser), "readerTheme=" + (ReaderTheme ?? "light"), "readerNight=" + ReaderNight, "readerSize=" + (ReaderSize ?? ""), "audioOut=" + (AudioOut ?? ""), "antiPhishing=" + B(AntiPhishing), "httpsFirst=" + B(HttpsFirst), "safePay=" + B(SafePayments), "askDownload=" + B(AskDownload), "lastDlDir=" + (LastDownloadDir ?? ""),
                "popups=" + B(BlockThirdPartyPopups), "cookieReject=" + B(AutoRejectCookies), "uiStyle=" + (UiStyle ?? "modern"), "nightStrength=" + NightStrength, "pageFade=" + PageFade, "pageEntrance=" + (PageEntrance ?? "blur"), "pageEntranceMs=" + PageEntranceMs, "pageMemory=" + B(PageMemory), "darkPatterns=" + B(DarkPatterns), "privacyReceipt=" + B(PrivacyReceipt), "gestures=" + B(MouseGestures), "pipBtn=" + B(PipButton), "videoDlBtn=" + B(VideoDownloadButton), "player=" + B(VideoPlayer), "playerAuto=" + B(PlayerAutoplay), "playerResume=" + B(PlayerResume), "playerLoop=" + B(PlayerLoop), "playerSalt=" + (PlayerSalt ?? ""), "torrents=" + B(Torrents), "torrentDir=" + (TorrentDir ?? ""), "torrentDown=" + TorrentDownKb, "torrentUp=" + TorrentUpKb, "torrentRatio=" + TorrentRatio.ToString(System.Globalization.CultureInfo.InvariantCulture), "torrentSeed=" + TorrentSeedMin, "torrentPeers=" + TorrentPeers, "videoDir=" + (VideoDir ?? ""), "floatBounds=" + (FloatBounds ?? ""), "floatOpacity=" + FloatOpacity, "floatTop=" + B(FloatTopmost), "cacheDir=" + (CacheDir ?? ""), "cleanJunk=" + B(CleanJunkOnStart), "speedUp=" + B(SpeedUp), "cacheMb=" + CacheMb, "connections=" + Connections, "zoom=" + DefaultZoom, "dark=" + B(DarkPages),
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

        async System.Threading.Tasks.Task ClearBrowsingDataOnExit(CoreWebView2Profile profile, bool includeHistoryFile = true)
        {
            await ClearBrowsingData(profile,
                CoreWebView2BrowsingDataKinds.DiskCache |
                CoreWebView2BrowsingDataKinds.DownloadHistory |
                CoreWebView2BrowsingDataKinds.BrowsingHistory,
                includeHistoryFile);
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

            // duze, dobrze widoczne przyciski importu - na gorze ustawien
            root.Children.Add(Header(L.T("📥 Import haseł, loginów i zakładek")));
            var importRow = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
            Func<string, Action, Button> BigButton = (t, act) =>
            {
                var b = new Button { Content = t, FontSize = 14, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 6) };
                b.Click += (x, y) => act();
                return b;
            };
            importRow.Children.Add(BigButton(L.T("🔑 Hasła i loginy z pliku (CSV: KeePassXC, Chrome, Edge…)"), () => ImportPasswordsCsvWithDialog(win)));
            importRow.Children.Add(BigButton(L.T("🌐 Z Chrome / Edge / Brave / Opery"), () => ShowBrowserImport(win)));
            root.Children.Add(importRow);

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
            root.Children.Add(new TextBlock { Text = L.T("Efekt wejścia treści:"), Margin = new Thickness(0, 6, 0, 2) });
            var entBox = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var en in new[] { new { k = "blur", t = L.T("Wyostrzenie") }, new { k = "cinema", t = L.T("Z ciemności (kinowe)") }, new { k = "dim", t = L.T("Delikatne przyciemnienie") }, new { k = "none", t = L.T("Brak") } })
                entBox.Items.Add(new ComboBoxItem { Content = en.t, Tag = en.k });
            entBox.SelectedItem = entBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (s.PageEntrance ?? "blur")) ?? entBox.Items[0];
            root.Children.Add(entBox);
            // suwak szybkosci efektu wejscia: 0 = automatycznie (wg plynnego przejscia), dalej 0,2 - 5 s
            var entLbl = new TextBlock { Margin = new Thickness(0, 6, 0, 0) };
            var entSlider = new Slider { Minimum = 0, Maximum = 5000, TickFrequency = 100, IsSnapToTickEnabled = true, Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Value = s.PageEntranceMs };
            Action entText = () => entLbl.Text = L.T("Szybkość efektu wejścia: ") + (entSlider.Value < 100 ? L.T("automatycznie") : (entSlider.Value / 1000.0).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + " s");
            entSlider.ValueChanged += (a, b) => entText(); entText();
            root.Children.Add(entLbl); root.Children.Add(entSlider);
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
            trackBox.Items.Add(new ComboBoxItem { Content = L.T("Wyłączona – bez kontrolowania"), Tag = "none" });
            trackBox.SelectedIndex = s.Tracking == "strict" ? 1 : s.Tracking == "none" ? 2 : 0;
            trackBox.ToolTip = L.T("Zrównoważona: blokuje znane trackery, a osadzone treści (np. wpisy z X, filmy) działają. Ścisła: blokuje też osadzone treści serwisów społecznościowych. Na zaufanych domenach ścisła działa jak zrównoważona, chyba że zaznaczysz dla domeny „Wymuś blokowanie trackerów”. Wyłączona: silnik nie blokuje trackerów (uBlock Origin Lite i reguły dla stron działają dalej, jeśli są włączone).");
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
            var phishBox = Check(L.T("Wykrywaj fałszywe strony banków, sklepów i portali (działa bez internetu)"), L.T("Ostrzega przed adresami typu paypa1.com, ebay-weryfikacja.top i podróbkami stron, do których masz zapisane hasła."), s.AntiPhishing);
            var httpsBox = Check(L.T("Zawsze szyfrowane połączenie (HTTPS) – ostrzegaj przed stronami bez szyfrowania"), null, s.HttpsFirst);
            var payBox = Check(L.T("Bezpieczne płatności – na stronach banków i płatności ukrywaj okno przed programami nagrywającymi ekran"), L.T("Chroni przed złośliwymi programami, które podglądają ekran. Na tych stronach nie zrobisz też zrzutu ekranu."), s.SafePayments);
            var ubolBox = Check(L.T("uBlock Origin Lite – wbudowany bloker reklam (zalecany)"), L.T("Wbudowany w Velivo, wyniki widać na tarczy. Możesz go wyłączyć."), s.UbolLite);
            var ubolOpts = SmallButton(L.T("Ustawienia uBlock Origin Lite…"), () => { _ = OpenUbolSettingsAsync(); win.Close(); });
            ubolOpts.HorizontalAlignment = HorizontalAlignment.Left;
            root.Children.Add(ubolBox); root.Children.Add(ubolOpts);
            root.Children.Add(ss); root.Children.Add(phishBox); root.Children.Add(httpsBox); root.Children.Add(payBox); root.Children.Add(ask);
            root.Children.Add(new TextBlock { Text = L.T("Głośniki Velivo:"), Margin = new Thickness(0, 8, 0, 2) });
            var outBox = new ComboBox { Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
            outBox.Items.Add(new ComboBoxItem { Content = L.T("Domyślne wyjście Windows"), Tag = "" });
            foreach (var d in ListAudioOutputs()) outBox.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d.Id });
            outBox.SelectedItem = outBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (s.AudioOut ?? "")) ?? outBox.Items[0];
            root.Children.Add(outBox);
            var guardBox = Check(L.T("Nie gub dźwięku: gdy program muzyczny (Ableton, Cubase) zajmie głośniki, graj na innym aktywnym wyjściu i wróć po ich zwolnieniu"), null, s.AudioGuard);
            root.Children.Add(guardBox);
            var trayBox = Check(L.T("Po zamknięciu okna zostań w zasobniku (synchronizacja w tle, natychmiastowy start)"), L.T("Ikonka Velivo przy zegarze pulsuje podczas synchronizacji. Prawy przycisk na ikonce: Otwórz, Synchronizuj teraz, Zamknij całkowicie."), s.StayInTray);
            root.Children.Add(trayBox);
            var audioOut = SmallButton(L.T("🔊 Mikser głośności Windows…"), OpenAppAudioSettings);
            audioOut.HorizontalAlignment = HorizontalAlignment.Left;
            root.Children.Add(audioOut);
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

            root.Children.Add(Header(L.T("Szybkość wczytywania stron")));
            var speedBox = Check(L.T("Szybsze otwieranie stron (wczytywanie przy najechaniu na link)"), L.T("Velivo zaczyna pobierać stronę, gdy najedziesz myszką na link, i z wyprzedzeniem łączy się z serwerami widocznych linków. Wyłączone w kartach prywatnych i bankowych. Przy limicie danych lepiej wyłączyć."), s.SpeedUp);
            root.Children.Add(speedBox);
            root.Children.Add(new TextBlock { Text = L.T("Rozmiar pamięci podręcznej (po ponownym uruchomieniu):"), Margin = new Thickness(0, 6, 0, 0) });
            var cacheSize = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 4) };
            int[] cacheOpts = { 0, 512, 1024, 2048, 4096 };
            foreach (var cm in cacheOpts) cacheSize.Items.Add(cm == 0 ? L.T("Automatycznie") : (cm >= 1024 ? (cm / 1024) + " GB" : cm + " MB"));
            cacheSize.SelectedIndex = Math.Max(0, Array.IndexOf(cacheOpts, s.CacheMb));
            root.Children.Add(cacheSize);

            // ---------- odtwarzacz filmow z dysku ----------
            root.Children.Add(Header(L.T("Odtwarzacz filmów")));
            var playerBox = Check(L.T("Otwieraj filmy z dysku w odtwarzaczu Velivo"), L.T("Pliki MP4, WebM, MKV, MOV, M4V i OGV – także dwuklikiem w Windows (skojarzenie z instalatora) i przez Ctrl+O. Na filmie działają ⧉ Obraz w obrazie i ▣ Film na wierzchu."), s.VideoPlayer);
            var playerAutoBox = Check(L.T("Odtwarzaj od razu po otwarciu"), null, s.PlayerAutoplay);
            var playerResumeBox = Check(L.T("Wznawiaj od miejsca, w którym skończyłeś"), L.T("Miejsce w każdym filmie zapamiętywane tylko na tym komputerze."), s.PlayerResume);
            var playerLoopBox = Check(L.T("Powtarzaj film w kółko"), null, s.PlayerLoop);
            foreach (var pb in new[] { playerBox, playerAutoBox, playerResumeBox, playerLoopBox }) root.Children.Add(pb);
            var playerAssoc = SmallButton(L.T("Otwieraj filmy i PDF w Velivo (Windows)…"), OpenVelivoDefaultApps);
            playerAssoc.HorizontalAlignment = HorizontalAlignment.Left; playerAssoc.Margin = new Thickness(0, 4, 0, 4);
            playerAssoc.ToolTip = L.T("Otwiera Aplikacje domyślne Windows na stronie Velivo – tam przy .mp4, .mkv, .pdf… wybierasz Velivo. Na jeden plik: prawy przycisk → Otwórz za pomocą → Velivo.");
            root.Children.Add(playerAssoc);

            // ---------- torrenty: osobna strefa ----------
            root.Children.Add(Header(L.T("Torrenty")));
            var torrBox = Check(L.T("Torrenty (linki magnet i pliki .torrent)"), L.T("Pobiera darmowy program aria2 (doinstalowany przy pierwszym użyciu). Uwaga: w torrentach Twój adres IP widzą inni uczestnicy wymiany. W zasobniku pobierają się dalej po zamknięciu okna; „Zamknij całkowicie” je zatrzymuje."), s.Torrents);
            root.Children.Add(torrBox);
            root.Children.Add(new TextBlock { Text = L.T("Strefa torrentów – osobny folder (puste = Pobrane\\Velivo-Torrenty):"), Margin = new Thickness(0, 6, 0, 0) });
            var torrDirBox = new TextBox { Text = s.TorrentDir, Padding = new Thickness(4), Margin = new Thickness(0, 2, 6, 0) };
            var torrBrowse = SmallButton(L.T("Wybierz…"), null);
            torrBrowse.Click += (a, b) =>
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Strefa torrentów") };
                if (!string.IsNullOrWhiteSpace(torrDirBox.Text) && Directory.Exists(torrDirBox.Text)) dlg.InitialDirectory = torrDirBox.Text;
                if (dlg.ShowDialog(win) == true) torrDirBox.Text = dlg.FolderName;
            };
            var torrDefault = SmallButton(L.T("Domyślny"), () => torrDirBox.Text = "");
            var torrRow = new DockPanel();
            DockPanel.SetDock(torrDefault, Dock.Right); DockPanel.SetDock(torrBrowse, Dock.Right);
            torrRow.Children.Add(torrDefault); torrRow.Children.Add(torrBrowse); torrRow.Children.Add(torrDirBox);
            root.Children.Add(torrRow);
            root.Children.Add(new TextBlock { Text = L.T("Pliki z torrentów są oznaczone jako pobrane z internetu – Windows sprawdzi je przed uruchomieniem. Velivo niczego z nich samo nie otwiera."), TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(0, 2, 0, 4) });
            Func<string, int[], Func<int, string>, int, ComboBox> torrCombo = (label, opts, fmt, cur) =>
            {
                root.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 4, 0, 0) });
                var cb = new ComboBox { Width = 240, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2) };
                foreach (var o in opts) cb.Items.Add(fmt(o));
                cb.SelectedIndex = Math.Max(0, Array.IndexOf(opts, cur));
                root.Children.Add(cb);
                return cb;
            };
            Func<int, string> speed = kb => kb == 0 ? L.T("Bez limitu") : kb >= 1024 ? (kb / 1024) + " MB/s" : kb + " KB/s";
            int[] downOpts = { 0, 512, 1024, 2048, 5120, 10240, 20480 }, upOpts = { 0, 128, 256, 512, 1024, 2048, 5120 };
            int[] ratioOpts = { 0, 50, 100, 150, 200 }, seedOpts = { 15, 30, 60, 120, 240 }, peerOpts = { 20, 40, 60, 100 };
            var torrDown = torrCombo(L.T("Prędkość pobierania:"), downOpts, speed, s.TorrentDownKb);
            var torrUp = torrCombo(L.T("Prędkość wysyłania:"), upOpts, speed, s.TorrentUpKb);
            var torrRatio = torrCombo(L.T("Udostępnianie po pobraniu:"), ratioOpts, r => r == 0 ? L.T("Nie udostępniaj") : L.T("Do współczynnika ") + (r / 100.0).ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("pl-PL")) + ":1", (int)Math.Round(s.TorrentRatio * 100));
            var torrSeed = torrCombo(L.T("Udostępniaj najdłużej:"), seedOpts, m => m >= 60 ? (m / 60) + " h" : m + " min", s.TorrentSeedMin);
            var torrPeers = torrCombo(L.T("Najwięcej uczestników naraz:"), peerOpts, p => p.ToString(), s.TorrentPeers);

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

                if (kinds != 0 && LiveProfile == null) { MessageBox.Show(win, L.T("Otwórz najpierw zwykłą kartę (dotyczy zwykłej przeglądarki, nie trybu bankowego ani prywatnego)."), L.T("Ustawienia")); return; }
                // zwykla przegladarka - nie profil karty bankowej
                try { await ClearBrowsingData(LiveProfile, kinds, clearHistoryFile); if (clearHistoryFile) { RememberHistoryCleared(); ForgetAllPageMemory(); } MessageBox.Show(win, L.T("Wyczyszczono zaznaczone dane."), L.T("Ustawienia")); }
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
                s.SendDnt = dnt.IsChecked == true; s.Tracking = (string)((ComboBoxItem)trackBox.SelectedItem).Tag;
                s.SaveHistory = hist.IsChecked == true; s.ClearOnExit = clear.IsChecked == true;
                s.SavePasswords = pw.IsChecked == true; s.Autofill = af.IsChecked == true;
                s.BlockThirdPartyPopups = pop.IsChecked == true;
                int newFade = s.PageFade;   // lista "Plynne przejscie" zastapiona efektem "Delikatne przyciemnienie"
                var newEnt = (string)((ComboBoxItem)entBox.SelectedItem).Tag;
                int newEntMs = entSlider.Value < 100 ? 0 : (int)entSlider.Value;
                bool scriptsChanged = s.PageFade != newFade || s.PageEntrance != newEnt || s.PageEntranceMs != newEntMs || s.AutoRejectCookies != (cookieRej.IsChecked == true) || s.MouseGestures != (gestures.IsChecked == true) || s.PipButton != (pipBtn.IsChecked == true)
                    || s.DarkPatterns != (darkP.IsChecked == true) || s.VideoDownloadButton != (dlBtnBox.IsChecked == true) || s.PrivacyReceipt != (receiptBox.IsChecked == true);
                s.PageFade = newFade; s.PageEntrance = newEnt; s.PageEntranceMs = newEntMs;
                s.PageMemory = memory.IsChecked == true; s.VideoDownloadButton = dlBtnBox.IsChecked == true; s.Torrents = torrBox.IsChecked == true;
                s.VideoPlayer = playerBox.IsChecked == true; s.PlayerAutoplay = playerAutoBox.IsChecked == true; s.PlayerResume = playerResumeBox.IsChecked == true; s.PlayerLoop = playerLoopBox.IsChecked == true;
                s.TorrentDir = (torrDirBox.Text ?? "").Trim(); s.TorrentDownKb = downOpts[Math.Max(0, torrDown.SelectedIndex)]; s.TorrentUpKb = upOpts[Math.Max(0, torrUp.SelectedIndex)];
                s.TorrentRatio = ratioOpts[Math.Max(0, torrRatio.SelectedIndex)] / 100.0; s.TorrentSeedMin = seedOpts[Math.Max(0, torrSeed.SelectedIndex)]; s.TorrentPeers = peerOpts[Math.Max(0, torrPeers.SelectedIndex)]; s.DarkPatterns = darkP.IsChecked == true; s.PrivacyReceipt = receiptBox.IsChecked == true;
                s.AutoRejectCookies = cookieRej.IsChecked == true; s.MouseGestures = gestures.IsChecked == true; s.PipButton = pipBtn.IsChecked == true;
                if (scriptsChanged) RefreshPageScripts();
                s.SmartScreen = ss.IsChecked == true; s.AskDownload = ask.IsChecked == true;
                bool ubolChanged = s.UbolLite != (ubolBox.IsChecked == true); s.UbolLite = ubolBox.IsChecked == true; if (ubolChanged) _ = EnsureBundledUbolAsync();
                s.AudioGuard = guardBox.IsChecked == true;
                s.StayInTray = trayBox.IsChecked == true; UpdateTrayFromSettings();
                var newOut = (string)((ComboBoxItem)outBox.SelectedItem).Tag ?? "";
                if (newOut != (s.AudioOut ?? "")) { s.AudioOut = newOut; RouteVelivoAudio(newOut); _audioRoutedTo = newOut; }
                s.AntiPhishing = phishBox.IsChecked == true; s.HttpsFirst = httpsBox.IsChecked == true; s.SafePayments = payBox.IsChecked == true;
                s.CleanJunkOnStart = cleanStart.IsChecked == true;
                bool speedChanged = s.SpeedUp != (speedBox.IsChecked == true);
                s.SpeedUp = speedBox.IsChecked == true;
                s.CacheMb = cacheOpts[Math.Max(0, cacheSize.SelectedIndex)];
                if (speedChanged) foreach (var t in _tabs) if (t.View.CoreWebView2 != null) _ = InstallPageScript(t, t.View.CoreWebView2);
                s.Connections = (int)((ComboBoxItem)conns.SelectedItem).Tag;
                int oldZoom = s.DefaultZoom; bool oldDark = s.DarkPages; bool oldFull = s.FullFilterLists; bool oldLan = s.LanSync;
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
                // czytanie w toku - nowy glos / tempo / glosnosc od biezacego zdania
                if (_readTab != null && _tabs.Contains(_readTab) && _readTab.View.CoreWebView2 != null)
                    _ = _readTab.View.CoreWebView2.ExecuteScriptAsync("window.__velivoRead && window.__velivoRead.config(" + Num(s.ReadRate) + "," + System.Text.Json.JsonSerializer.Serialize(s.ReadVoice ?? "") + "," + Num(s.ReadVolume) + ")");
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
                // synchronizacje LAN restartujemy tylko, gdy dotyczy jej zmiana (albo nie dziala, a powinna) - restart liczy
                // klucz od nowa (PBKDF2 na watku okna), zamyka gniazda i zapomina wykryte komputery
                if (oldLan != s.LanSync || (s.LanSync && _lanTx == null)) { StopLanSync(); StartLanSync(); }
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
            if (darkChanged) OfferRestartForDarkMode();   // pelny tryb ciemny (silnik) dziala dopiero po ponownym uruchomieniu - pytamy
        }
    }
}
