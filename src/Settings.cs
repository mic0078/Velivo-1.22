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
        public string Search = "duckduckgo";
        public string Home = "https://duckduckgo.com/";
        public bool SendDnt = true;            // naglowki DNT: 1 i Sec-GPC: 1
        public bool StrictTracking = true;     // ochrona przed sledzeniem: scisla
        public bool SaveHistory = true;
        public bool ClearOnExit = false;       // przy zamknieciu: historia + cache (bez wylogowywania kont)
        public bool SavePasswords = false;
        public bool Autofill = false;
        public bool SmartScreen = true;        // ostrzezenia o niebezpiecznych stronach
        public bool AskDownload = false;       // pytaj, gdzie zapisac plik
        public int Connections = 8;            // polaczen na jeden plik w menedzerze pobierania (1-16)
        public int DefaultZoom = 100;          // domyslne powiekszenie stron w %
        public bool DarkPages = false;         // tryb ciemny stron
        public bool RestoreTabs = true;        // przywracaj karty po ponownym uruchomieniu
        public bool FullFilterLists = true;    // pelne listy AdBlocka (EasyList, EasyPrivacy, polska)
        public bool SejfLogins = true;         // kluczyk z loginami z Sejfu na stronach logowania
        public bool QuickAccessNewTab = true;
        public double ReadRate = 1.0;          // predkosc czytania na glos
        public string ReadVoice = "";          // glos (pusty = pierwszy polski)
        public string CacheDir = "";           // wlasny folder na smieci (pusty = w profilu)
        public bool CleanJunkOnStart = false;  // usuwaj smieci przy kazdym uruchomieniu
        public bool BlockThirdPartyPopups = true;
        public bool LanSync = false;           // LAN wymaga jawnego skonfigurowania silnego klucza
        public string LanSyncKey = "";
        public bool LanSyncSilent = false;     // bez dymkow przy automatycznym sync
        public bool ToolbarAlwaysCompact = false; // zawsze kompaktowy pasek narzedzi

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
                        case "strict": s.StrictTracking = b; break;
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
                        case "fullLists": s.FullFilterLists = b; break;
                        case "sejfLogins": s.SejfLogins = b; break;
                        case "quickAccessTab": s.QuickAccessNewTab = b; break;
                        case "readRate": double rr; if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out rr)) s.ReadRate = Math.Max(0.5, Math.Min(3, rr)); break;
                        case "readVoice": s.ReadVoice = v; break;
                        case "popups": s.BlockThirdPartyPopups = b; break;
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
                "search=" + Search, "home=" + Home, "dnt=" + B(SendDnt), "strict=" + B(StrictTracking),
                "history=" + B(SaveHistory), "clearOnExit=" + B(ClearOnExit), "passwords=" + B(SavePasswords),
                "autofill=" + B(Autofill), "smartscreen=" + B(SmartScreen), "askDownload=" + B(AskDownload),
                "popups=" + B(BlockThirdPartyPopups), "cacheDir=" + (CacheDir ?? ""), "cleanJunk=" + B(CleanJunkOnStart), "connections=" + Connections, "zoom=" + DefaultZoom, "dark=" + B(DarkPages),
                "restore=" + B(RestoreTabs), "fullLists=" + B(FullFilterLists), "sejfLogins=" + B(SejfLogins), "quickAccessTab=" + B(QuickAccessNewTab), "readRate=" + ReadRate.ToString(System.Globalization.CultureInfo.InvariantCulture), "readVoice=" + (ReadVoice ?? ""),
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
            core.Profile.PreferredTrackingPreventionLevel = _settings.StrictTracking
                ? CoreWebView2TrackingPreventionLevel.Strict : CoreWebView2TrackingPreventionLevel.Balanced;
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
                Title = "Ustawienia",
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

            root.Children.Add(Header("Domyślna przeglądarka"));
            bool isDefault = IsDefaultBrowser();
            var defInfo = new TextBlock
            {
                Text = isDefault ? "✓ Velivo jest domyślną przeglądarką." : "Velivo nie jest teraz domyślną przeglądarką.",
                Foreground = isDefault ? Brushes.SeaGreen : Brushes.Gray, Margin = new Thickness(0, 0, 0, 4)
            };
            root.Children.Add(defInfo);
            if (!isDefault)
            {
                var makeDefault = SmallButton("Ustaw Velivo jako domyślną przeglądarkę…", () => MakeDefaultBrowser(win));
                makeDefault.HorizontalAlignment = HorizontalAlignment.Left; makeDefault.Margin = new Thickness(0);
                root.Children.Add(makeDefault);
            }

            root.Children.Add(Header("Wygląd i czytelność"));
            root.Children.Add(new TextBlock { Text = "Domyślne powiększenie stron (każdą stronę możesz też powiększyć osobno: Ctrl + kółko myszy):" , TextWrapping = TextWrapping.Wrap });
            var zoom = new ComboBox { Width = 120, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 4) };
            foreach (var zv in new[] { 80, 90, 100, 110, 125, 150, 175, 200 })
            {
                var it = new ComboBoxItem { Content = zv + "%", Tag = zv };
                zoom.Items.Add(it);
                if (zv == s.DefaultZoom) zoom.SelectedItem = it;
            }
            if (zoom.SelectedItem == null) zoom.SelectedIndex = 2;
            root.Children.Add(zoom);
            var dark = Check("Tryb ciemny stron (przycisk z księżycem na pasku)", "Strony z własnym ciemnym wyglądem przełączają się na niego, pozostałe jasne strony są przyciemniane.", s.DarkPages);
            root.Children.Add(dark);
            var compactBar = Check("Zawsze kompaktowy pasek narzędzi", "Zmniejsza etykiety i przenosi część przycisków do menu „…”, nawet na szerokim oknie.", s.ToolbarAlwaysCompact);
            root.Children.Add(compactBar);
            root.Children.Add(new TextBlock { Text = "Czytanie na głos – głos i prędkość:", Margin = new Thickness(0, 6, 0, 2) });
            var voiceRow = new StackPanel { Orientation = Orientation.Horizontal };
            var voice = new ComboBox { Width = 260, Margin = new Thickness(0, 0, 8, 0) };
            voice.Items.Add(new ComboBoxItem { Content = "Automatycznie (pierwszy polski)", Tag = "" });
            foreach (var vn in _voiceNames) voice.Items.Add(new ComboBoxItem { Content = vn.Replace("Microsoft ", "").Replace(" - Polish (Poland)", ""), Tag = vn });
            voice.SelectedItem = System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.Cast<ComboBoxItem>(voice.Items), i => (string)i.Tag == (s.ReadVoice ?? "")) ?? voice.Items[0];
            var rate = new ComboBox { Width = 90 };
            foreach (var rv in ReadRates) { var it = new ComboBoxItem { Content = rv.ToString("0.##") + "×", Tag = rv }; rate.Items.Add(it); if (Math.Abs(rv - s.ReadRate) < 0.01) rate.SelectedItem = it; }
            if (rate.SelectedItem == null) rate.SelectedIndex = 1;
            voiceRow.Children.Add(voice); voiceRow.Children.Add(rate);
            root.Children.Add(voiceRow);
            var restore = Check("Po uruchomieniu przywracaj karty z poprzedniej sesji", "Karty prywatne nigdy nie są zapisywane. Zamkniętą kartę przywrócisz też skrótem Ctrl+Shift+T.", s.RestoreTabs);
            root.Children.Add(restore);

            root.Children.Add(Header("Wyszukiwanie i start"));
            var engine = new ComboBox { Margin = new Thickness(0, 2, 0, 6) };
            foreach (var kv in AppSettings.Engines)
            {
                var it = new ComboBoxItem { Content = kv.Value[0], Tag = kv.Key };
                engine.Items.Add(it);
                if (kv.Key == s.Search) engine.SelectedItem = it;
            }
            root.Children.Add(new TextBlock { Text = "Wyszukiwarka w pasku adresu:" });
            root.Children.Add(engine);
            root.Children.Add(new TextBlock { Text = "Strona startowa:" });
            var home = new TextBox { Text = s.Home, Padding = new Thickness(4), Margin = new Thickness(0, 2, 0, 0) };
            root.Children.Add(home);
            bool quickAccessPresent = System.Linq.Enumerable.Any(_extInfos, i => IsQuickAccessExtensionId(i.Id) && i.NewTab != null);
            var quickAccess = Check("Szybki Dostęp jako strona nowej karty", "Używa osobnych danych Velivo w folderze danych przeglądarki; Szybki Dostęp Sejfu w innych przeglądarkach pozostaje osobny.", s.QuickAccessNewTab);
            quickAccess.IsEnabled = quickAccessPresent;
            root.Children.Add(quickAccess);
            root.Children.Add(new TextBlock { Text = "Folder rozszerzenia do ręcznej instalacji w innych przeglądarkach:", Margin = new Thickness(0, 4, 0, 2) });
            root.Children.Add(new TextBox
            {
                Text = Path.Combine(AppContext.BaseDirectory, "Dodatki", "Szybki Dostęp"),
                IsReadOnly = true,
                Padding = new Thickness(4),
                ToolTip = "Wybierz ścieżkę i skopiuj ją do okna ładowania rozpakowanego rozszerzenia."
            });

            root.Children.Add(Header("Prywatność"));
            var dnt = Check("Wysyłaj sygnały „Nie śledź” (DNT i Global Privacy Control)", "Strony dostają prośbę o niesprzedawanie i nieśledzenie Twoich danych.", s.SendDnt);
            var strict = Check("Ścisła ochrona przed śledzeniem", "Blokuje trackery i ciasteczka śledzące między stronami. Rzadko może psuć niektóre strony.", s.StrictTracking);
            var hist = Check("Zapisuj historię przeglądania", null, s.SaveHistory);
            var clear = Check("Czyść dane przy zamknięciu (historia i pamięć podręczna)", "Czyści historię i cache przy zamknięciu, ale nie wylogowuje kont ani nie usuwa zapisanych logowań.", s.ClearOnExit);
            bool sejfFound = FindSejfMost() != null;
            var sejf = Check("Loginy z Sejfu: kluczyk na pasku na stronach logowania" + (sejfFound ? "" : " (nie znaleziono Sejfu)"),
                "Gdy strona ma pole hasła, Velivo pyta Sejf o loginy dla tej strony. Kliknięcie kluczyka wypełnia formularz.", s.SejfLogins && sejfFound);
            sejf.IsEnabled = sejfFound;
            root.Children.Add(sejf);
            var pw = Check("Proponuj zapisywanie haseł", null, s.SavePasswords);
            var af = Check("Autouzupełnianie formularzy (adresy i karty, lokalna szyfrowana baza)", "Dane formularzy i kart są zapisywane lokalnie w szyfrowanej bazie offline. Hasła dalej obsługuje Sejf.", s.Autofill);
            var pop = Check("Blokuj wyskakujące okna otwierane bez kliknięcia", null, s.BlockThirdPartyPopups);
            foreach (var c in new[] { dnt, strict, hist, clear, pw, af, pop }) root.Children.Add(c);

            var autofillTools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            var afShow = SmallButton("Pokaż zapisane dane…", null);
            afShow.Margin = new Thickness(0, 0, 6, 0);
            afShow.Click += (a, b) => OpenAutofillDataViewer(win);
            var afClearCards = SmallButton("Usuń zapisane karty", null);
            afClearCards.Margin = new Thickness(0, 0, 6, 0);
            afClearCards.Click += (a, b) => DeleteAutofillCards(win);
            var afClearAddr = SmallButton("Usuń zapisane adresy", null);
            afClearAddr.Click += (a, b) => DeleteAutofillAddresses(win);
            autofillTools.Children.Add(afShow);
            autofillTools.Children.Add(afClearCards);
            autofillTools.Children.Add(afClearAddr);
            root.Children.Add(autofillTools);

            var passTools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var passMgr = SmallButton("Menedżer haseł lokalnych…", null);
            passMgr.Margin = new Thickness(0, 0, 6, 0);
            passMgr.Click += (a, b) => OpenPasswordsManager(win);
            var passImport = SmallButton("Import haseł CSV…", null);
            passImport.Margin = new Thickness(0, 0, 6, 0);
            passImport.Click += (a, b) => ImportPasswordsCsvWithDialog(win);
            var passExport = SmallButton("Eksport haseł CSV…", null);
            passExport.Click += (a, b) => ExportPasswordsCsvWithDialog(win);
            passTools.Children.Add(passMgr);
            passTools.Children.Add(passImport);
            passTools.Children.Add(passExport);
            root.Children.Add(passTools);

            root.Children.Add(Header("Blokowanie reklam"));
            var full = Check("Pełne listy filtrów (EasyList, EasyPrivacy, polska lista) – ok. 97 tys. reguł", "Listy pobierają się w tle i odświeżają co 4 dni.", s.FullFilterLists);
            root.Children.Add(full);
            var listInfo = new TextBlock { Text = "Listy: " + FilterListsInfo() + ". Reguł w użyciu: " + _blocker.RuleCount.ToString("N0") + (FilterStatus.Length > 0 ? "\n" + FilterStatus : ""), FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(0, 2, 0, 4) };
            root.Children.Add(listInfo);
            var updLists = SmallButton("Aktualizuj listy teraz", null);
            updLists.HorizontalAlignment = HorizontalAlignment.Left; updLists.Margin = new Thickness(0);
            updLists.Click += async (a, b) =>
            {
                updLists.IsEnabled = false; listInfo.Text = "Pobieram listy…";
                int ok = await System.Threading.Tasks.Task.Run(DownloadFilterLists);
                await RebuildBlocker();
                listInfo.Text = "Pobrano " + ok + " z 3 list. Reguł w użyciu: " + _blocker.RuleCount.ToString("N0") + (FilterStatus.Length > 0 ? "\n" + FilterStatus : "");
                updLists.IsEnabled = true;
            };
            root.Children.Add(updLists);

            root.Children.Add(Header("Bezpieczeństwo i pobieranie"));
            var ss = Check("Ostrzegaj przed niebezpiecznymi stronami i plikami (SmartScreen)", "Sprawdzanie adresów wysyła je do Microsoft. Zalecane – chroni przed wyłudzeniami.", s.SmartScreen);
            var ask = Check("Pytaj, gdzie zapisać każdy pobierany plik", null, s.AskDownload);
            root.Children.Add(ss); root.Children.Add(ask);
            root.Children.Add(new TextBlock { Text = "Połączeń na jeden pobierany plik (więcej = zwykle szybciej):", Margin = new Thickness(0, 6, 0, 2) });
            var conns = new ComboBox { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var n in new[] { 1, 2, 4, 8, 12, 16 })
            {
                var it = new ComboBoxItem { Content = n == 1 ? "1 (bez dzielenia)" : n.ToString(), Tag = n };
                conns.Items.Add(it);
                if (n == s.Connections) conns.SelectedItem = it;
            }
            if (conns.SelectedItem == null) conns.SelectedIndex = 3;
            root.Children.Add(conns);

            root.Children.Add(Header("Śmieci przeglądarki (pamięć podręczna)"));
            root.Children.Add(new TextBlock
            {
                Text = "Cache stron, skompilowane skrypty i cache grafiki. Można je usuwać bez utraty logowań i danych dodatków.",
                TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.Gray
            });
            root.Children.Add(new TextBlock { Text = "Folder na śmieci (puste = w profilu przeglądarki):", Margin = new Thickness(0, 6, 0, 0) });
            var cacheBox = new TextBox { Text = s.CacheDir, Padding = new Thickness(4), Margin = new Thickness(0, 2, 6, 0) };
            var browse = SmallButton("Wybierz…", null);
            browse.Click += (a, b) =>
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder na śmieci przeglądarki" };
                if (!string.IsNullOrWhiteSpace(cacheBox.Text) && Directory.Exists(cacheBox.Text)) dlg.InitialDirectory = cacheBox.Text;
                if (dlg.ShowDialog(win) == true) cacheBox.Text = dlg.FolderName;
            };
            var reset = SmallButton("Domyślny", () => cacheBox.Text = "");
            var cacheRow = new DockPanel();
            DockPanel.SetDock(reset, Dock.Right); DockPanel.SetDock(browse, Dock.Right);
            cacheRow.Children.Add(reset); cacheRow.Children.Add(browse); cacheRow.Children.Add(cacheBox);
            root.Children.Add(cacheRow);
            root.Children.Add(new TextBlock
            {
                Text = "Program tworzy w nim podfolder „" + JunkSubfolder + "” i usuwa tylko jego zawartość. Cache grafiki zostaje w profilu (wymóg silnika).",
                TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(0, 2, 0, 0)
            });
            var cleanStart = Check("Usuwaj śmieci przy każdym uruchomieniu przeglądarki", "Strony wczytają się za pierwszym razem odrobinę wolniej.", s.CleanJunkOnStart);
            root.Children.Add(cleanStart);
            var junkInfo = new TextBlock { Text = "Teraz zajmują: " + Mb(JunkSize()), Margin = new Thickness(0, 2, 0, 4) };
            var cleanJunk = SmallButton("Wyczyść śmieci teraz", null);
            cleanJunk.HorizontalAlignment = HorizontalAlignment.Left; cleanJunk.Margin = new Thickness(0);
            cleanJunk.Click += async (a, b) =>
            {
                try
                {
                    var freed = await CleanJunkNow();
                    junkInfo.Text = "Zwolniono " + Mb(freed) + ". Teraz zajmują: " + Mb(JunkSize()) + " (cache grafiki zniknie przy następnym uruchomieniu).";
                }
                catch (Exception ex) { MessageBox.Show(win, ex.Message, "Ustawienia"); }
            };
            root.Children.Add(junkInfo);
            root.Children.Add(cleanJunk);

            root.Children.Add(Header("Dane"));
            var clearNow = SmallButton("Wyczyść dane przeglądania teraz…", null);
            clearNow.HorizontalAlignment = HorizontalAlignment.Left; clearNow.Margin = new Thickness(0);
            clearNow.Click += async (a, b) =>
            {
                var dlg = new Window
                {
                    Title = "Wyczyść dane przeglądania",
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
                    Text = "Wybierz, co usunąć:",
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 8)
                });

                var cHist = new CheckBox { Content = "Historia przeglądania", IsChecked = true, Margin = new Thickness(0, 3, 0, 3) };
                var cCache = new CheckBox { Content = "Pamięć podręczna (cache)", IsChecked = true, Margin = new Thickness(0, 3, 0, 3) };
                var cDl = new CheckBox { Content = "Historia pobrań", IsChecked = false, Margin = new Thickness(0, 3, 0, 3) };
                var cCookies = new CheckBox { Content = "Cookies i aktywne sesje (wyloguje konta)", IsChecked = false, Margin = new Thickness(0, 3, 0, 3) };
                var cAutofill = new CheckBox { Content = "Dane formularzy i kart zapisane przez silnik", IsChecked = false, Margin = new Thickness(0, 3, 0, 3) };
                var cPasswords = new CheckBox { Content = "Zapisane hasła w silniku", IsChecked = false, Margin = new Thickness(0, 3, 0, 3) };
                panel.Children.Add(cHist);
                panel.Children.Add(cCache);
                panel.Children.Add(cDl);
                panel.Children.Add(cCookies);
                panel.Children.Add(cAutofill);
                panel.Children.Add(cPasswords);

                var warn = new TextBlock
                {
                    Text = "Uwaga: usunięcie cookies/sesji spowoduje wylogowanie ze stron.",
                    Foreground = Brushes.DarkRed,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 10, 0, 0)
                };
                panel.Children.Add(warn);

                bool accepted = false;
                var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
                var okBtn = new Button { Content = "Wyczyść", Width = 96, Height = 30, Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
                var cancelBtn = new Button { Content = "Anuluj", Width = 96, Height = 30, IsCancel = true };
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
                    MessageBox.Show(win, "Nie zaznaczono żadnych danych do usunięcia.", "Ustawienia");
                    return;
                }

                try { await ClearBrowsingData(Core != null ? Core.Profile : null, kinds, clearHistoryFile); MessageBox.Show(win, "Wyczyszczono zaznaczone dane.", "Ustawienia"); }
                catch (Exception ex) { MessageBox.Show(win, ex.Message, "Ustawienia"); }
            };
            root.Children.Add(clearNow);

            root.Children.Add(Header("Prywatność per-strona"));
            var privacyPanel = SmallButton("Panel prywatności i antyfingerprinting…", null);
            privacyPanel.HorizontalAlignment = HorizontalAlignment.Left; privacyPanel.Margin = new Thickness(0);
            privacyPanel.Click += (a, b) => OpenPrivacyPanel();
            root.Children.Add(privacyPanel);

            root.Children.Add(Header("Profile użytkownika"));
            root.Children.Add(new TextBlock { Text = "Aktywny profil: " + SelectedProfileName, Margin = new Thickness(0, 0, 0, 4), Foreground = Brushes.Gray });
            var profileRow = new StackPanel { Orientation = Orientation.Horizontal };
            var pWork = SmallButton("Praca", () => SwitchProfile("praca")); pWork.Margin = new Thickness(0, 0, 6, 0);
            var pPrivate = SmallButton("Prywatny", () => SwitchProfile("prywatny")); pPrivate.Margin = new Thickness(0, 0, 6, 0);
            var pDev = SmallButton("Dev", () => SwitchProfile("dev"));
            profileRow.Children.Add(pWork); profileRow.Children.Add(pPrivate); profileRow.Children.Add(pDev);
            root.Children.Add(profileRow);
            var profileMgr = SmallButton("Zarządzaj użytkownikami/profilami…", OpenProfilesManager);
            profileMgr.HorizontalAlignment = HorizontalAlignment.Left; profileMgr.Margin = new Thickness(0, 6, 0, 0);
            root.Children.Add(profileMgr);

            root.Children.Add(Header("Synchronizacja E2E"));
            root.Children.Add(new TextBlock
            {
                Text = "Lokalny eksport/import zaszyfrowanej paczki (hasło + AES-GCM). Możesz przenieść plik na inne urządzenie.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = Brushes.Gray
            });
            var syncRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var syncOut = SmallButton("Eksportuj paczkę…", null); syncOut.Margin = new Thickness(0, 0, 6, 0);
            syncOut.Click += SyncExport_Click;
            var syncIn = SmallButton("Importuj paczkę…", null);
            syncIn.Click += SyncImport_Click;
            syncRow.Children.Add(syncOut); syncRow.Children.Add(syncIn);
            root.Children.Add(syncRow);

            root.Children.Add(new TextBlock { Text = "Synchronizacja w czasie rzeczywistym (LAN):", Margin = new Thickness(0, 10, 0, 2) });
            var lanSync = Check("Włącz synchronizację między uruchomionymi Velivo w tej samej sieci lokalnej", "Synchronizuje ustawienia, zakładki, reguły prywatności i sesję dla tego samego profilu.", s.LanSync);
            _lanSyncSettingCheck = lanSync;
            win.Closed += (sender, args) => { if (ReferenceEquals(_lanSyncSettingCheck, lanSync)) _lanSyncSettingCheck = null; };
            root.Children.Add(lanSync);
            root.Children.Add(new TextBlock
            {
                Text = "Włącz i zapisz synchronizację na obu komputerach. Sparuj je jednorazowo, porównując krótki kod; sekret zostanie zapisany lokalnie.",
                FontSize = 11,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 4)
            });
            var pairLan = SmallButton("Sparuj urządzenie w sieci…", BeginLanPairing);
            pairLan.IsEnabled = s.LanSync && _lanTx != null;
            pairLan.HorizontalAlignment = HorizontalAlignment.Left;
            root.Children.Add(pairLan);
            var recoveryRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var exportRecovery = SmallButton("Zapisz plik odzyskiwania…", ExportLanPairingRecovery);
            exportRecovery.Margin = new Thickness(0, 0, 6, 0);
            var importRecovery = SmallButton("Odtwórz parowanie…", ImportLanPairingRecovery);
            recoveryRow.Children.Add(exportRecovery);
            recoveryRow.Children.Add(importRecovery);
            root.Children.Add(recoveryRow);
            var lanSilent = Check("Tryb cichy LAN (bez dymków „Zsynchronizowano profil…”)", "Log i panel diagnostyczny nadal działają, wyłączone są tylko wyskakujące komunikaty.", s.LanSyncSilent);
            root.Children.Add(lanSilent);
            var lanDiag = SmallButton("Panel diagnostyczny LAN…", OpenLanDiagnosticsPanel);
            lanDiag.HorizontalAlignment = HorizontalAlignment.Left; lanDiag.Margin = new Thickness(0, 6, 0, 0);
            root.Children.Add(lanDiag);

            var ok = new Button { Content = "Zapisz", Width = 90, Height = 30, IsDefault = true, Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            ok.Click += (a, b) =>
            {
                s.Search = (string)((ComboBoxItem)engine.SelectedItem).Tag;
                s.Home = string.IsNullOrWhiteSpace(home.Text) ? "https://duckduckgo.com/" : ToUrl(home.Text);
                s.SendDnt = dnt.IsChecked == true; s.StrictTracking = strict.IsChecked == true;
                s.SaveHistory = hist.IsChecked == true; s.ClearOnExit = clear.IsChecked == true;
                s.SavePasswords = pw.IsChecked == true; s.Autofill = af.IsChecked == true;
                s.BlockThirdPartyPopups = pop.IsChecked == true;
                s.SmartScreen = ss.IsChecked == true; s.AskDownload = ask.IsChecked == true;
                s.CleanJunkOnStart = cleanStart.IsChecked == true;
                s.Connections = (int)((ComboBoxItem)conns.SelectedItem).Tag;
                int oldZoom = s.DefaultZoom; bool oldDark = s.DarkPages; bool oldFull = s.FullFilterLists;
                s.DefaultZoom = (int)((ComboBoxItem)zoom.SelectedItem).Tag;
                s.DarkPages = dark.IsChecked == true;
                s.ToolbarAlwaysCompact = compactBar.IsChecked == true;
                s.RestoreTabs = restore.IsChecked == true;
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
                    catch (Exception ex) { MessageBox.Show(win, "Nie można używać tego folderu na śmieci:\n" + ex.Message, "Ustawienia"); return; }
                }
                bool dirChanged = !string.Equals(newDir, s.CacheDir ?? "", StringComparison.OrdinalIgnoreCase);
                s.CacheDir = newDir;
                try { s.Save(DataDir); }
                catch (Exception ex) { MessageBox.Show(win, "Nie zapisano ustawień:\n" + ex.Message, "Ustawienia"); return; }
                ApplySettingsToAllTabs();
                StopLanSync();
                StartLanSync();
                UpdateAdaptiveToolbarLayout();
                win.Close();
                if (dirChanged)
                    MessageBox.Show(this, "Nowy folder na śmieci zacznie działać po ponownym uruchomieniu przeglądarki.", "Ustawienia");
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
            if (darkChanged) OfferRestartForDarkMode(); // po zamknieciu okna ustawien
        }
    }
}
