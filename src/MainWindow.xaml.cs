using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Przegladarka
{
    public partial class MainWindow : Window
    {
        string HomeUrl { get { return _settings.Home; } }
        // PRZEGLADARKA_DANE pozwala uruchomic program na osobnym folderze danych (np. do testow)
        static readonly string DataDir = Environment.GetEnvironmentVariable("PRZEGLADARKA_DANE") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Przegladarka");
        static readonly string HistoryFile = Path.Combine(DataDir, "historia.txt");
        static readonly string AppVersionLabel =
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version != null
                ? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(2)
                : "1.122";
        static readonly string AppTitleLabel = "Velivo " + AppVersionLabel;

        static string BuildWindowTitle(string pageTitle)
        {
            return string.IsNullOrWhiteSpace(pageTitle) ? AppTitleLabel : pageTitle + " – " + AppTitleLabel;
        }

        sealed class BrowserTab
        {
            public WebView2 View;
            public Button Header;
            public TextBlock Title;
            public int Blocked;
            public int UbolBlocked;   // z tego: uBlock Origin Lite
            public readonly List<string> BlockedItems = new List<string>();   // co zablokowano na biezacej stronie (wszystkie silniki)
            public int HiddenElements;   // elementy ukryte regulami recznymi (kosmetyka)
            public bool Private;
            public bool Bank;            // karta trybu bankowego (osobny profil; Private=true, zeby nic nie zapisywac)
            public bool Pinned;
            public bool InPip;           // film tej karty gra w okienku "obraz w obrazie"
            public bool Mobile;          // strona w wersji telefonu
            public string DesktopUA;
            public readonly Dictionary<string, int> ThirdParties = new Dictionary<string, int>();   // paragon prywatnosci
            public readonly Dictionary<string, int> Fingerprint = new Dictionary<string, int>();
            public readonly List<string> Pressure = new List<string>();   // sztuczki presji w sklepie
            public string PageSite;
            public double PendingVideoTime;   // karta z innego komputera: film od tej sekundy
            public Button SoundBtn;              // 🔊 / 🔇 na karcie
            public TextBlock RefreshMark, GroupDot;
            public System.Windows.Threading.DispatcherTimer RefreshTimer;
            public int RefreshMinutes;
            public TabGroup Group;
            public string PageScriptId;
            public string HideScriptId;   // ukrywanie chrome.webview - zawsze PO skrypcie stron   // wspolny skrypt stron (ciasteczka, gesty, obraz w obrazie)
            public DateTime NewTabIntentAt;   // ostatni Ctrl+klik / srodkowy klik na linku
            public string PinnedUrl;     // adres zamrozony przy przypieciu - do niego karta wraca po uruchomieniu          // karta przypieta: na poczatku paska, wraca po kazdym uruchomieniu
            public Button CloseBtn;
            public TextBlock PinMark;
            public string StartUrl;      // adres, z ktorym karte otwarto (zanim silnik ruszy)
            public bool ApplyingZoom;    // zmiana powiekszenia robiona przez program, nie przez uzytkownika
            public string LastRequestedUrl;
            public bool QuickAccessRecoveryTried;
        }

        readonly List<BrowserTab> _tabs = new List<BrowserTab>();
        AdBlocker _blocker = new AdBlocker(); // podmieniany w calosci po wczytaniu pelnych list (FilterLists.cs)
        CoreWebView2Environment _env;
        BrowserTab _current;
        int _totalBlocked;
        CoreWebView2Profile _profile;
        bool _cleanedUp;
        bool _extensionsLoaded;
        bool _toolbarCompact;

        readonly string[] _startUrls;

        public MainWindow() : this(new string[0]) { }

        public MainWindow(string[] startUrls)
        {
            _startUrls = startUrls ?? new string[0];
            InitializeComponent();
            Title = AppTitleLabel;
            _settings = AppSettings.Load(DataDir);
            L.Init(_settings.Language);
            try
            {
                // Szybki Dostep (dodatek) czyta jezyk z kod/jezyk-wybor.js
                var langJs = Path.Combine(BundledQuickAccessDir, "kod", "jezyk-wybor.js");
                var want = "var VELIVO_LANG = '" + (L.En ? "en" : "pl") + "';\n";
                if (File.Exists(langJs) && File.ReadAllText(langJs) != want) File.WriteAllText(langJs, want);
            }
            catch (Exception) { }
            L.TranslateTree(this);   // napisy okna z XAML (dymki, przyciski) - gdy wybrano angielski
            BuildAddressMenu();
            InitInnovationsUi();
            UpdateProfileBadge();
            LoadSitePrivacyRules();
            LoadPrivacyLog();
            Closing += TrayOnClosing;                    // tryb zasobnika: chowamy zamiast zamykac
            Closing += GuardAgainstWebViewClosingWindow; // musi byc pierwsze - anuluje zamkniecie okna przez strone
            Closing += ConfirmCloseWithDownloads;        // trwa pobieranie? zapytaj i wstrzymaj
            Closing += (s, e) => { if (!e.Cancel) SaveSession(); }; // karty do przywrocenia przy nastepnym starcie
            Closing += OnClosingCleanup;
            Closed += (s, e) => { _mainClosed = true; StopMost(); StopLanSync(); };
            LoadJobs(); // lista pobran z poprzedniego uruchomienia (przerwane mozna wznowic)
            LoadZoom();
            LoadSiteModes();
            InitBankMode();
            InitZoomMenu();
            UpdateDarkButton();
            ApplyBrowserTheme();
            UpdatePrivacyButton();
            if (Environment.GetEnvironmentVariable("VELIVO_DEBUG") == "1")
                Closing += (s, e) => File.AppendAllText(Path.Combine(DataDir, "debug.log"), DateTime.Now + " ZAMYKANIE OKNA\n" + Environment.StackTrace + "\n\n");
            _blocker.Load(Path.Combine(AppContext.BaseDirectory, "filters.txt"));
            _blocker.Load(Path.Combine(DataDir, "filters.txt")); // wlasne reguly uzytkownika (opcjonalne)
            StartFilterLists(); // pelne listy (EasyList itd.) wczytywane i odswiezane w tle
            SizeChanged += (s, e) => UpdateAdaptiveToolbarLayout();
            Loaded += (s, e) => UpdateAdaptiveToolbarLayout();
            Loaded += async (s, e) =>
            {
                try
                {
                    Directory.CreateDirectory(DataDir);
                    EnsureSejfMostAllowedOrigins();
                    EnsureOneTimeQuickAccessWebViewReset();
                    MigrateQuickAccessProfilePath();
                    // smieci sprzatamy tylko, gdy nie dziala inne okno przegladarki (jego silnik trzyma te pliki)
                    if (System.Diagnostics.Process.GetProcessesByName("Velivo").Length <= 1) CleanJunkAtStartup();
                    _env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDir, "Profil"),
                        new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true, AdditionalBrowserArguments = BrowserArguments() });
                    LoadBookmarks();
                    var pinned = LoadPinnedTabs(); // karty przypiete - zawsze na poczatku
                    foreach (var u in pinned) { AddTab(u); SetTabPinned(_tabs[_tabs.Count - 1], true); }
                    var session = LoadSession(); // karty z poprzedniego uruchomienia
                    foreach (var u in session) AddTab(u);
                    if (session.Count > 0) RestoreTabGroups(_tabs.Skip(pinned.Count).Take(session.Count).ToList());
                    if (session.Count > 0 && _startUrls.Length == 0) SelectTab(_tabs[Math.Min(pinned.Count + LoadSessionActive(), _tabs.Count - 1)]);
                    if (session.Count == 0 && pinned.Count == 0 && _startUrls.Length == 0) AddTab("");
                    if (session.Count == 0 && pinned.Count > 0 && _startUrls.Length == 0) SelectTab(_tabs[0]);   // start od pierwszej przypietej
                    foreach (var u in _startUrls) AddTab(u);
                    _sessionLoaded = true;
                    SaveSessionSoon();
                    StartInstanceServer(); // linki z innych programow -> nowe karty w tym oknie
                    StartLanSync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(L.T("Nie udało się uruchomić WebView2:\n") + ex.Message, "Velivo");
                    Close();
                }
            };
            PreviewKeyDown += OnKeys;
        }

        void EnsureOneTimeQuickAccessWebViewReset()
        {
            try
            {
                if (_settings != null && !_settings.QuickAccessNewTab) return;

                var marker = Path.Combine(DataDir, "quickaccess-webview-reset-v2.done");
                if (File.Exists(marker)) return;

                // Nie resetuj, gdy dziala inne okno procesu - profile moga byc zablokowane.
                if (System.Diagnostics.Process.GetProcessesByName("Velivo").Length > 1) return;

                var profileRoot = Path.Combine(DataDir, "Profil", "EBWebView");
                if (Directory.Exists(profileRoot))
                {
                    try { Directory.Delete(profileRoot, true); }
                    catch (Exception) { return; }
                }

                foreach (var id in QuickAccessKnownIds()) SaveExtPath(id, null);
                var extDir = ExtensionsDir;
                var idFile = Path.Combine(extDir, QuickAccessExtensionId + ".id");
                if (File.Exists(idFile))
                {
                    try { File.Delete(idFile); }
                    catch (Exception) { }
                }

                var mapFile = Path.Combine(extDir, "sciezki.txt");
                if (File.Exists(mapFile))
                {
                    try
                    {
                            var ids = new HashSet<string>(QuickAccessKnownIds(), StringComparer.OrdinalIgnoreCase);
                            var lines = File.ReadAllLines(mapFile)
                                .Where(l =>
                                {
                                    int tab = l.IndexOf('\t');
                                    if (tab <= 0) return true;
                                    var id = l.Substring(0, tab);
                                    return !ids.Contains(id);
                                })
                                .ToArray();
                        File.WriteAllLines(mapFile, lines);
                    }
                    catch (Exception) { }
                }

                Directory.CreateDirectory(DataDir);
                File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        void MigrateQuickAccessProfilePath()
        {
            try
            {
                if (_settings != null && !_settings.QuickAccessNewTab) return;

                var bundled = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, L.T("Dodatki"), "Szybki Dostęp"));
                if (!File.Exists(Path.Combine(bundled, "manifest.json"))) return;

                var profileDir = Path.Combine(DataDir, "Profil", "EBWebView", "Default");
                bool repaired = false;
                foreach (var fileName in new[] { "Secure Preferences", "Preferences" })
                {
                    var filePath = Path.Combine(profileDir, fileName);
                    if (!File.Exists(filePath)) continue;

                    JsonObject root;
                    try
                    {
                        root = JsonNode.Parse(File.ReadAllText(filePath)) as JsonObject;
                    }
                    catch (JsonException)
                    {
                        continue;
                    }
                    if (root == null) continue;

                    var settings = root["extensions"]?["settings"] as JsonObject;
                    bool fileChanged = false;
                    foreach (var id in QuickAccessKnownIds())
                    {
                        var entry = settings != null ? settings[id] as JsonObject : null;
                        if (entry == null) continue;

                        var currentPath = (string)entry["path"];
                        var currentFull = string.IsNullOrWhiteSpace(currentPath) ? "" : Path.GetFullPath(currentPath);
                        var needsPath = !string.Equals(currentFull.TrimEnd(Path.DirectorySeparatorChar), bundled.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
                        int state;
                        var stateRaw = entry["state"];
                        var enabled = stateRaw != null && int.TryParse(stateRaw.ToString(), out state) && state == 1;

                        if (!needsPath && enabled) continue;

                        entry["path"] = bundled;
                        entry["state"] = 1;
                        entry["disable_reasons"] = 0;
                        fileChanged = true;
                    }

                    if (!fileChanged) continue;
                    File.WriteAllText(filePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
                    repaired = true;
                }

                if (repaired) PurgeQuickAccessProfileArtifacts(profileDir);

                foreach (var id in QuickAccessKnownIds()) SaveExtPath(id, bundled);
                Directory.CreateDirectory(ExtensionsDir);
                var idFile = Path.Combine(ExtensionsDir, QuickAccessExtensionId + ".id");
                if (!File.Exists(idFile))
                    File.WriteAllText(idFile, QuickAccessExtensionId);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        void PurgeQuickAccessProfileArtifacts(string profileDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(profileDir) || !Directory.Exists(profileDir)) return;

                var purgeDirs = new List<string>();
                foreach (var id in QuickAccessKnownIds())
                {
                    var suffix = "chrome-extension_" + id + "_0.indexeddb.leveldb";
                    purgeDirs.Add(Path.Combine(profileDir, "Local Extension Settings", id));
                    purgeDirs.Add(Path.Combine(profileDir, "Sync Extension Settings", id));
                    purgeDirs.Add(Path.Combine(profileDir, "IndexedDB", suffix));
                    purgeDirs.Add(Path.Combine(profileDir, "DawnGraphiteCache", id));
                }

                foreach (var dir in purgeDirs)
                {
                    try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                    catch (Exception) { }
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        // ---------- zakladki ----------

        void AddTab(string url) { AddTab(url, false); }

        void AddTab(string url, bool isPrivate) { AddTab(url, isPrivate, null, null); }

        // pending/deferral: karta otwierana na prosbe strony lub dodatku (window.open, chrome.tabs.create) -
        // nowy widok trzeba oddac przez e.NewWindow, inaczej dodatek nie dostanie uchwytu karty.
        void AddTab(string url, bool isPrivate, CoreWebView2NewWindowRequestedEventArgs pending, CoreWebView2Deferral deferral)
        {
            var tab = new BrowserTab { Private = isPrivate || _creatingBank, Bank = _creatingBank, View = new WebView2(), Title = new TextBlock { Text = L.T("Nowa karta"), MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center } };
            var close = new Button { Content = "×", Width = 20, Height = 20, FontSize = 13, Margin = new Thickness(6, 0, 0, 0) };
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            tab.CloseBtn = close;
            tab.PinMark = new TextBlock { Text = "📌", FontSize = 11, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            tab.GroupDot = new TextBlock { Text = "●", FontSize = 12, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            tab.SoundBtn = new Button { Content = "🔊", Width = 22, Height = 20, FontSize = 11, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Visibility = Visibility.Collapsed };
            tab.SoundBtn.Click += (s, e) => { ToggleTabMute(tab); e.Handled = true; };
            var soundMenu = new ContextMenu();
            var pickOut = new MenuItem { Header = L.T("🔊 Wybierz głośniki dla Velivo…") };
            pickOut.Click += (s, e) => OpenAppAudioSettings();
            soundMenu.Items.Add(pickOut);
            tab.SoundBtn.ContextMenu = soundMenu;
            tab.RefreshMark = new TextBlock { Text = "⟳", FontSize = 12, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            panel.Children.Add(tab.GroupDot);
            panel.Children.Add(tab.PinMark);
            panel.Children.Add(tab.SoundBtn);
            panel.Children.Add(tab.Title);
            panel.Children.Add(tab.RefreshMark);
            panel.Children.Add(close);
            tab.Header = new Button { Content = panel, Width = double.NaN, Padding = new Thickness(10, 0, 4, 0), Height = 32, Margin = new Thickness(1, 4, 0, 0) };
            if (isPrivate)
            {
                tab.Title.Foreground = Brushes.White;
                close.Foreground = Brushes.White;
                tab.Title.Text = tab.Bank ? L.T("🏦 Bankowa") : L.T("🕶 Prywatna");
            }
            tab.StartUrl = url;
            tab.Header.ContextMenu = BuildTabMenu(tab);
            tab.Header.Click += (s, e) => SelectTab(tab);
            close.Click += (s, e) => { CloseTab(tab); e.Handled = true; };
            tab.Header.MouseUp += (s, e) => { if (e.ChangedButton == MouseButton.Middle && !tab.Pinned) CloseTab(tab); };

            _tabs.Add(tab);
            TabStrip.Children.Insert(TabStrip.Children.IndexOf(NewTabBtn), tab.Header);
            NewTabBtn.BringIntoView();
            Host.Children.Add(tab.View);
            SelectTab(tab);
            InitView(tab, url, pending, deferral);
        }

        async void InitView(BrowserTab tab, string url, CoreWebView2NewWindowRequestedEventArgs pending, CoreWebView2Deferral deferral)
        {
            try
            {
                var opts = _env.CreateCoreWebView2ControllerOptions();
                opts.IsInPrivateModeEnabled = tab.Private && !tab.Bank;
                if (tab.Bank) opts.ProfileName = BankProfileName;   // osobny, trwaly profil trybu bankowego
                await tab.View.EnsureCoreWebView2Async(_env, opts);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L.T("Nie udało się otworzyć karty.\nJeśli działa jeszcze starsza wersja Velivo, zamknij ją i spróbuj ponownie.\n\n") + ex.Message, "Velivo");
                if (deferral != null) deferral.Complete();
                if (_tabs.Contains(tab)) CloseTab(tab);
                return;
            }
            var core = tab.View.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            if (tab.Bank) await BankAfterInit(core);
            if (!tab.Private && _profile == null) _profile = core.Profile;
            ApplyViewSettings(core);
            await HookAutofill(tab, core);   // przed ukryciem chrome.webview
            HookProtection(tab, core);
            await HookUbolShield(tab, core);
            await HookPasswordVault(tab, core);
            tab.View.ZoomFactorChanged += (s, e) => OnZoomChanged(tab);
            HookTabSound(tab, core);
            ApplyDarkMode(tab);
            // Strony nie musza wiedziec, ze to WebView2 - Google blokuje logowanie w "przegladarkach wbudowanych".
            // chrome.webview jest potrzebny tylko w okienkach dodatkow (osobne widoki), w kartach go wylaczamy.
            core.Settings.IsWebMessageEnabled = true;   // kanal dla Szybkiego Dostepu; wiadomosci z innych stron sa ignorowane
            core.WebMessageReceived += async (s, e) => await HandleQuickAccessWebMessageAsync(core, e);
            // Ctrl+klik / srodkowy klik na linku = swiadomie nowa karta. Silnik nie podaje, jakim klikiem otwarto okno,
            // wiec strona zglasza to przy wcisnieciu przycisku (tylko znacznik, bez danych).
            core.WebMessageReceived += (s, e) => { try { if (e.TryGetWebMessageAsString() == "velivo:nowa-karta") tab.NewTabIntentAt = DateTime.UtcNow; } catch (Exception) { } };
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "(function(){try{if(!window.chrome||!chrome.webview)return;var pm=chrome.webview.postMessage.bind(chrome.webview);document.addEventListener('mousedown',function(e){if(e.button===1||e.ctrlKey||e.shiftKey||e.metaKey){var a=e.target&&e.target.closest&&e.target.closest('a[href]');if(a)pm('velivo:nowa-karta');}},true);}catch(x){}})();");
            core.WebMessageReceived += (s, e) => { try { HandlePageMessage(tab, e.TryGetWebMessageAsString()); } catch (Exception) { } };
            await InstallPageScript(tab, core);   // przed ukryciem chrome.webview - skrypt zapamietuje kanal wiadomosci
            tab.HideScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(HideWebViewBrandScript);
            await EnsureBundledQuickAccessAsync();
            if (!_extensionsLoaded)
            {
                _extensionsLoaded = true;
                await EnsureBundledUbolAsync();
                StartAudioGuard();
                _ = UpdateUbolAsync();
                await RefreshExtensions();
                await SaveExtensionsSyncListAsync();
                await ApplyExtensionsSyncListAsync();
                await RefreshExtensions();
            }
            if (_settings.SendDnt) // navigator.globalPrivacyControl dla skryptow strony
                await core.AddScriptToExecuteOnDocumentCreatedAsync(
                    "try { Object.defineProperty(Navigator.prototype, 'globalPrivacyControl', { get: () => true }); } catch (e) {}");

            // Bez zdjec i czcionek: kazde przechwycone zadanie przechodzi przez glowny watek okna, a strony
            // ze zdjeciami (eBay, Allegro) wczytuja ich setki przy przewijaniu - przycinalo przewijanie i myszke.
            // Reklamy-obrazki i piksele sledzace blokuje i tak uBlock Origin Lite wewnatrz silnika (bez kosztu dla okna).
            foreach (var ctx in new[] {
                CoreWebView2WebResourceContext.Document, CoreWebView2WebResourceContext.Script, CoreWebView2WebResourceContext.Stylesheet,
                CoreWebView2WebResourceContext.XmlHttpRequest, CoreWebView2WebResourceContext.Fetch, CoreWebView2WebResourceContext.Media,
                CoreWebView2WebResourceContext.Websocket, CoreWebView2WebResourceContext.EventSource, CoreWebView2WebResourceContext.Ping,
                CoreWebView2WebResourceContext.Manifest, CoreWebView2WebResourceContext.TextTrack, CoreWebView2WebResourceContext.SignedExchange,
                CoreWebView2WebResourceContext.CspViolationReport, CoreWebView2WebResourceContext.Other })
                core.AddWebResourceRequestedFilter("*", ctx, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (s, e) =>
            {
                if (IsQuickAccessUrl(e.Request.Uri)) return;

                Uri requestUri;
                if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out requestUri) ||
                    (requestUri.Scheme != Uri.UriSchemeHttp && requestUri.Scheme != Uri.UriSchemeHttps))
                    return;

                RecordThirdParty(tab, requestUri);   // paragon prywatnosci - takze proby zablokowane
                ApplyMobileHeaders(tab, e);          // strona w wersji telefonu
                if (ApplyPrivacyRulesToRequest(e, tab)) return;
                // zaufana domena (strona albo zasob) - nic nie blokujemy
                if (IsTrustedUrl(e.Request.Uri) || IsTrustedUrl(tab.View.CoreWebView2 != null ? tab.View.CoreWebView2.Source : null))
                {
                    StripWebViewBrand(e.Request.Headers);
                    return;
                }
                if (_downloadWin != null) NoteMediaRequest(e.Request.Uri, tab, e.ResourceContext);
                if (!_blocker.ShouldBlock(e.Request.Uri))
                {
                    StripWebViewBrand(e.Request.Headers);
                    if (_settings.SendDnt)
                    {
                        e.Request.Headers.SetHeader("DNT", "1");
                        e.Request.Headers.SetHeader("Sec-GPC", "1");
                    }
                    return;
                }
                e.Response = _env.CreateWebResourceResponse(null, 403, "Blocked", "");
                NoteBlocked(tab, "AdBlock", e.Request.Uri);
                AddPrivacyBlock("Tracker zablokowany (AdBlock)", e.Request.Uri, tab);
            };

            core.NewWindowRequested += (s, e) => { if (!OpenLinkInSameTab(tab, e)) { _creatingBank = tab.Bank; try { OnNewWindowRequested(e, tab.Private); } finally { _creatingBank = false; } } };
            core.DocumentTitleChanged += (s, e) =>
            {
                tab.Title.Text = (tab.Bank ? "🏦 " : tab.Private ? "🕶 " : "") + (string.IsNullOrEmpty(core.DocumentTitle) ? core.Source : core.DocumentTitle);
                tab.Header.ToolTip = tab.Title.Text;
                if (tab == _current) Title = BuildWindowTitle(tab.Title.Text);
            };
            core.NavigationStarting += (s, e) =>
            {
                tab.LastRequestedUrl = e.Uri;
                if (!IsQuickAccessUrl(e.Uri)) tab.QuickAccessRecoveryTried = false;
                core.Settings.IsWebMessageEnabled = true;   // zmiana dziala dopiero od nastepnej nawigacji - wiec stale wlaczone; odbiorca sprawdza nadawce (IsQuickAccessUrl)
                core.Settings.IsReputationCheckingRequired = _settings.SmartScreen && ShouldUseReputationCheck(e.Uri) && !IsTrustedUrl(e.Uri);
                if (tab == _current) UpdateTrackingLevel(e.Uri);
                ApplyMobileMode(tab, core, e.Uri);
                // nowa karta przegladarki (np. chrome.tabs.create bez adresu) -> strona nowej karty z dodatku
                if (IsInternalNewTabUrl(e.Uri))
                {
                    var target = NewTabUrl;
                    if (!IsInternalNewTabUrl(target))
                    {
                        e.Cancel = true;
                        Dispatcher.BeginInvoke(new Action(() => core.Navigate(target)));
                        return;
                    }
                }
                // przypieta karta: klikniety link na inna strone otwiera sie w nowej karcie (logowania i przekierowania zostaja)
                if (tab.Pinned && e.IsUserInitiated && !e.IsRedirected && !SameSite(e.Uri, tab.PinnedUrl))
                {
                    e.Cancel = true;
                    var u = e.Uri;
                    Dispatcher.BeginInvoke(new Action(() => AddTab(u)));
                    return;
                }
                if (e.IsRedirected) return;
                tab.Blocked = 0; tab.UbolBlocked = 0;
                tab.HiddenElements = 0;
                tab.BlockedItems.Clear();
                tab.ThirdParties.Clear(); tab.Fingerprint.Clear(); tab.Pressure.Clear();
                { Uri nu; tab.PageSite = Uri.TryCreate(e.Uri, UriKind.Absolute, out nu) && (nu.Scheme == "http" || nu.Scheme == "https") ? RegistrableDomain(nu.Host) : null; }
                if (tab == _current) UpdatePressureButton();
                if (tab == _current) UpdateCounter();
            };
            core.SourceChanged += (s, e) =>
            {
                // karta otwarta przez dodatek bez adresu laduje edge://newtab bez zdarzenia NavigationStarting
                var src = core.Source ?? "";
                if (IsInternalNewTabUrl(src) && !IsInternalNewTabUrl(NewTabUrl))
                {
                    core.Navigate(NewTabUrl);
                    return;
                }
                ApplyZoom(tab);     // powiekszenie zapamietane dla tej strony
                SaveSessionSoon();
                if (tab != _current) return;
                if (_keyHost != null && HostOf(core.Source) != _keyHost) HideKey(); // inna strona - kluczyk znika
                if (!Address.IsKeyboardFocused) Address.Text = core.Source;
                UpdateStar();
                UpdateStoreButton();
                UpdatePrivacyButton();
            };
            core.DownloadStarting += OnDownloadStarting;
            // Velivo ma wlasne okno pobran - wbudowana lista pobran silnika Edge nie powinna wyskakiwac
            core.IsDefaultDownloadDialogOpenChanged += (s, e) => { if (core.IsDefaultDownloadDialogOpen) core.CloseDefaultDownloadDialog(); };
            // pelny ekran zadany przez strone (np. wideo) - WebView2 tylko zglasza, okno musimy powiekszyc sami
            core.ContextMenuRequested += (s, e) => AddSearchToContextMenu(e, tab.Private);
            // Prosba strony o zamkniecie (window.close, pusta karta po starcie pobierania z linku target=_blank)
            // zamyka TYLKO te karte. Domyslnie kontrolka WebView2 zamyka cale okno programu - odpinamy to.
            DetachDefaultWindowClose(tab.View);
            core.DOMContentLoaded += (s, e) => { ApplyElementRules(core, tab); ApplyLiveDarkCss(core); };   // elementy zablokowane recznie (menu kontekstowe)
            core.WindowCloseRequested += (s, e) => Dispatcher.BeginInvoke(new Action(() => { if (_tabs.Contains(tab)) CloseTab(tab); }));
            core.ContainsFullScreenElementChanged += (s, e) =>
            {
                if (tab != _current) return;
                _pageFullScreen = core.ContainsFullScreenElement;
                SetFullScreen(_pageFullScreen);
            };
            core.NavigationCompleted += async (s, e) =>
            {
                bool requestedQuickAccess = IsQuickAccessUrl(tab.LastRequestedUrl);
                bool blockedQuickAccess = false;
                // Przerwane ladowanie (nowa nawigacja, odswiezenie, zamkniecie karty) to nie blokada dodatku.
                if (requestedQuickAccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                    blockedQuickAccess = await IsQuickAccessBlockedAsync(core, e.IsSuccess);

                if (blockedQuickAccess && !tab.QuickAccessRecoveryTried)
                {
                    tab.QuickAccessRecoveryTried = true;
                    _ = Dispatcher.BeginInvoke(new Action(async () => await RecoverQuickAccessFromBlockAsync(core, tab)));
                    return;
                }
                if (blockedQuickAccess) return;
                if (e.IsSuccess && !tab.Private && _settings.SaveHistory) AppendHistory(core.Source, core.DocumentTitle);
                if (e.IsSuccess) CheckBankSiteInNormalTab(tab, core.Source);
                if (e.IsSuccess) _ = ApplyAutoClearRule(tab);
                if (e.IsSuccess) CheckSejfLogins(tab); // pole hasla? -> loginy z Sejfu dla tej strony
                if (e.IsSuccess) LoadVoiceNames(core);  // raz: lista polskich glosow do ustawien
                if (e.IsSuccess) _ = CapturePageThumbAsync(tab);   // miniatura strony dla Szybkiego Dostepu
                if (e.IsSuccess) { _ = RememberPageTextAsync(tab, core); _ = ApplyPendingVideoTime(tab, core); }
            };

            if (pending != null)
            {
                pending.NewWindow = core;
                pending.Handled = true;
                deferral.Complete();
            }
            else Navigate(tab, string.IsNullOrWhiteSpace(url) ? NewTabUrl : url);
        }

        static bool IsInternalNewTabUrl(string uri)
        {
            return uri != null &&
                (uri.StartsWith("chrome://newtab", StringComparison.OrdinalIgnoreCase) ||
                 uri.StartsWith("edge://newtab", StringComparison.OrdinalIgnoreCase) ||
                 uri.StartsWith("chrome-search://local-ntp", StringComparison.OrdinalIgnoreCase));
        }

        static bool ShouldUseReputationCheck(string uri)
        {
            Uri u;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out u)) return false;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
            return !AdBlocker.IsLocalNetworkUri(uri);
        }

        async Task RecoverQuickAccessFromBlockAsync(CoreWebView2 core, BrowserTab tab)
        {
            if (core == null || tab == null) return;
            try
            {
                bool changed = false;
                var exts = await core.Profile.GetBrowserExtensionsAsync();
                CoreWebView2BrowserExtension quickAccessExt = null;
                var paths = LoadExtPaths();
                foreach (var ext in exts)
                {
                    if (BuiltInExtensions.Contains(ext.Id)) continue;
                    string extPath;
                    bool pathMatch = paths.TryGetValue(ext.Id, out extPath) &&
                        string.Equals(Path.GetFullPath(extPath).TrimEnd(Path.DirectorySeparatorChar),
                            Path.GetFullPath(BundledQuickAccessDir).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
                    if (IsQuickAccessExtensionId(ext.Id) || pathMatch)
                        quickAccessExt = ext;
                    // Innych dodatkow uzytkownika nie wylaczamy - nie maja nic wspolnego z problemem Szybkiego Dostepu.
                }

                // Dodatku nie usuwamy (to kasowalo skroty i ustawienia Szybkiego Dostepu) - tylko wlaczamy.
                if (quickAccessExt != null)
                {
                    if (!quickAccessExt.IsEnabled) { await quickAccessExt.EnableAsync(true); changed = true; }
                }

                var bundled = BundledQuickAccessDir;
                if (quickAccessExt == null && File.Exists(Path.Combine(bundled, "manifest.json")))
                {
                    var added = await core.Profile.AddBrowserExtensionAsync(bundled);
                    if (!added.IsEnabled) await added.EnableAsync(true);
                    SaveExtPath(added.Id, bundled);
                    Directory.CreateDirectory(ExtensionsDir);
                    File.WriteAllText(Path.Combine(ExtensionsDir, QuickAccessExtensionId + ".id"), added.Id);
                }

                await RefreshExtensions();
                if (changed) await SaveExtensionsSyncListAsync();

                var target = NewTabUrl;
                if (!IsQuickAccessUrl(target))
                    target = "chrome-extension://" + QuickAccessInstalledId + "/kod/newtab.html";
                core.Settings.IsReputationCheckingRequired = false;
                core.Navigate(target);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        // Wczesniej kazdy tekst "zablokowan"/"blocked by" na stronie Szybkiego Dostepu (np. w nazwie skrotu,
        // grupy albo komunikacie paska) albo chwilowy blad skryptu byl traktowany jak blokada przez ochrone -
        // a "naprawa" wylaczala wszystkie inne dodatki i reinstalowala Szybki Dostep. Teraz: jesli interfejs
        // dodatku jest na stronie, nie ma blokady; blokada to tylko strona bledu przegladarki.
        async Task<bool> IsQuickAccessBlockedAsync(CoreWebView2 core, bool navigationSucceeded)
        {
            if (core == null) return false;
            try
            {
                var result = await core.ExecuteScriptAsync("(() => { try { if (document.getElementById('grupy') && document.getElementById('siatka')) return 'ui'; const text = ((document.body && document.body.innerText) || '').toLowerCase(); return (text.indexOf('err_blocked_by_client') >= 0 || text.indexOf('err_blocked_by_administrator') >= 0 || text.indexOf('err_file_not_found') >= 0) ? 'blocked' : 'other'; } catch (e) { return 'error'; } })();");
                if (result == "\"ui\"") return false;
                if (result == "\"blocked\"") return true;
                return !navigationSucceeded;
            }
            catch (Exception)
            {
                return !navigationSucceeded;
            }
        }

        // Link "w nowej karcie" (target=_blank) otwierany w biezacej karcie - wtedy dziala Wstecz/Dalej.
        // Nie dotyczy: okienek z wymiarami (logowanie, platnosci - potrzebuja okna-rodzica), Ctrl/Shift+klik, kart przypietych.
        bool OpenLinkInSameTab(BrowserTab tab, CoreWebView2NewWindowRequestedEventArgs e)
        {
            try
            {
                if (!_settings.LinksInSameTab || tab.Pinned || !e.IsUserInitiated || tab.View.CoreWebView2 == null) return false;
                if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl) || Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) return false;
                if (Mouse.MiddleButton == MouseButtonState.Pressed) return false;
                if ((DateTime.UtcNow - tab.NewTabIntentAt).TotalSeconds < 2) return false;
                var f = e.WindowFeatures;
                if (f != null && f.HasSize) return false;
                Uri u;
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) return false;
                e.Handled = true;
                tab.View.CoreWebView2.Navigate(e.Uri);
                return true;
            }
            catch (Exception) { return false; }
        }

        void OnNewWindowRequested(CoreWebView2NewWindowRequestedEventArgs e, bool isPrivate)
        {
            if (_settings.BlockThirdPartyPopups && !e.IsUserInitiated) { e.Handled = true; return; } // wyskakujace okno bez klikniecia
            // chrome.tabs.create bez adresu -> strona nowej karty (np. speed dial dodatku), nie edge://newtab
            if (IsInternalNewTabUrl(e.Uri))
            {
                e.Handled = true;
                AddTab(NewTabUrl, isPrivate);
                return;
            }
            var deferral = e.GetDeferral();
            var f = e.WindowFeatures;
            // okno o zadanym rozmiarze (chrome.windows.create type popup, window.open z wymiarami) -> male okno
            if (f != null && f.HasSize && f.Width > 0 && f.Width < 1000)
                OpenPopupWindow(e, deferral, isPrivate);
            else
                AddTab(e.Uri, isPrivate, e, deferral);
        }

        async void OpenPopupWindow(CoreWebView2NewWindowRequestedEventArgs e, CoreWebView2Deferral deferral, bool isPrivate)
        {
            var f = e.WindowFeatures;
            bool bank = _creatingBank;   // okienko otwarte z karty bankowej zostaje w profilu bankowym
            var view = new WebView2();
            var win = new Window { Title = AppTitleLabel, Width = f.Width + 16, Height = f.Height + 39, Content = view, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            if (f.HasPosition) { win.WindowStartupLocation = WindowStartupLocation.Manual; win.Left = f.Left; win.Top = f.Top; }
            win.Closed += (s, a) => view.Dispose();
            win.Show();
            try
            {
                var opts = _env.CreateCoreWebView2ControllerOptions();
                opts.IsInPrivateModeEnabled = isPrivate && !bank;
                if (bank) opts.ProfileName = BankProfileName;
                await view.EnsureCoreWebView2Async(_env, opts);
                var core = view.CoreWebView2;
                ApplyViewSettings(core);
                core.NavigationStarting += (s, a) =>
                {
                    core.Settings.IsWebMessageEnabled = true;
                    core.Settings.IsReputationCheckingRequired = _settings.SmartScreen && ShouldUseReputationCheck(a.Uri) && !IsTrustedUrl(a.Uri);
                };
                core.WindowCloseRequested += (s, a) => win.Close();
                core.DocumentTitleChanged += (s, a) => win.Title = BuildWindowTitle(core.DocumentTitle);
                core.NewWindowRequested += (s, a) => { _creatingBank = bank; try { OnNewWindowRequested(a, isPrivate); } finally { _creatingBank = false; } };
                core.DownloadStarting += OnDownloadStarting;
                e.NewWindow = core;
                e.Handled = true;
            }
            catch (Exception) { win.Close(); }
            finally { deferral.Complete(); }
        }

        void SelectTabColors()
        {
            foreach (var t in _tabs)
            {
                bool on = t == _current;
                t.View.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                // karta w tle moze oddac czesc pamieci (strona dalej dziala: muzyka, czaty, liczniki)
                try
                {
                    var c = t.View.CoreWebView2;
                    var lvl = on ? CoreWebView2MemoryUsageTargetLevel.Normal : CoreWebView2MemoryUsageTargetLevel.Low;
                    if (c != null && c.MemoryUsageTargetLevel != lvl) c.MemoryUsageTargetLevel = lvl;
                }
                catch (Exception) { }
                t.Header.Background = t.Bank
                    ? new SolidColorBrush(on ? Color.FromRgb(0x06, 0x5F, 0x46) : Color.FromRgb(0x05, 0x96, 0x69))
                    : t.Private
                    ? new SolidColorBrush(on ? Color.FromRgb(0x4C, 0x1D, 0x95) : Color.FromRgb(0x6D, 0x28, 0xD9))
                    : (on ? ActiveTabBrush : Brushes.Transparent);
                ModernTabLook(t, on);
            }
        }

        void SelectTab(BrowserTab tab)
        {
            _current = tab;
            SelectTabColors();
            Address.Text = tab.View.CoreWebView2 != null ? tab.View.CoreWebView2.Source : "";
            Title = BuildWindowTitle(tab.Title.Text);
            UpdateCounter();
            UpdateStar();
            UpdateStoreButton();
            UpdatePrivacyButton();
            UpdateZoomButton();
            SaveSessionSoon();
            CheckSejfLogins(tab);
            UpdateAdaptiveToolbarLayout();
            UpdateTrackingLevel(tab.View.CoreWebView2 != null ? tab.View.CoreWebView2.Source : null);
            if (_groups.Count > 0) RefreshGroupsUi();   // zwinieta grupa pokazuje tylko aktywna karte
            UpdatePressureButton();
        }

        void OverflowBtn_Click(object sender, RoutedEventArgs e)
        {
            if (OverflowBtn.ContextMenu == null || OverflowBtn.ContextMenu.Items.Count == 0) return;
            OverflowBtn.ContextMenu.PlacementTarget = OverflowBtn;
            OverflowBtn.ContextMenu.IsOpen = true;
        }

        void UpdateAdaptiveToolbarLayout()
        {
            if (ToolBarPanel == null || Address == null || OverflowBtn == null) return;

            double w = ActualWidth;
            bool compact = (_settings != null && _settings.ToolbarAlwaysCompact) || w < 1480;
            if (_toolbarCompact != compact)
            {
                _toolbarCompact = compact;
                UpdateProfileBadge();
            }

            Address.FontSize = compact ? 14 : 15;
            Address.Margin = compact ? new Thickness(6, 8, 6, 8) : new Thickness(8, 7, 8, 7);

            var overflow = new List<(string Header, Action Click)>();

            void Reset(FrameworkElement el)
            {
                if (el != null) el.Visibility = Visibility.Visible;
            }

            void Push(FrameworkElement el, string header, Action click)
            {
                if (el == null || el.Visibility != Visibility.Visible) return;
                el.Visibility = Visibility.Collapsed;
                overflow.Add((header, click));
            }

            Reset(PrivacyBtn);
            Reset(BookmarksBtn);
            Reset(HistoryBtn);
            Reset(ExtensionsBtn);
            Reset(DownloadsBtn);
            Reset(DarkBtn);
            Reset(ReadBtn);
            Reset(ReaderModeBtn);
            Reset(ShotBtn);

            if (w < 1760) Push(ReaderModeBtn, L.T("Tryb czytania"), () => ReaderMode_Click(null, null));
            if (w < 1680) Push(ShotBtn, L.T("Zrzut ekranu"), () => ShotBtn_Click(null, null));
            if (w < 1600) Push(ReadBtn, L.T("Czytaj na głos"), () => ReadBtn_Click(null, null));
            if (w < 1520) Push(DarkBtn, L.T("Tryb ciemny"), () => DarkBtn_Click(null, null));
            if (w < 1440) Push(DownloadsBtn, L.T("Pobrane pliki"), () => Downloads_Click(null, null));
            if (w < 1360) Push(ExtensionsBtn, L.T("Dodatki"), () => ExtensionsMenu_Click(null, null));
            if (w < 1280) Push(HistoryBtn, L.T("Historia"), () => History_Click(null, null));
            if (w < 1200) Push(BookmarksBtn, L.T("Zakładki"), () => Bookmarks_Click(null, null));
            if (w < 1120) Push(PrivacyBtn, L.T("Prywatność"), () => PrivacyPanel_Click(null, null));

            if (overflow.Count == 0)
            {
                OverflowBtn.Visibility = Visibility.Collapsed;
                OverflowBtn.ContextMenu = null;
            }
            else
            {
                var menu = new ContextMenu();
                foreach (var item in overflow)
                {
                    var mi = new MenuItem { Header = item.Header };
                    mi.Click += (s, e) => item.Click();
                    menu.Items.Add(mi);
                }
                OverflowBtn.ContextMenu = menu;
                OverflowBtn.Visibility = Visibility.Visible;
            }
        }

        // Karty zamkniete w trakcie pobierania przez silnik: niewidoczne, ale zywe az do konca pobierania
        // (zamkniecie widoku WebView2 przerwaloby jego pobierania).
        readonly List<WebView2> _parkedViews = new List<WebView2>();

        void CloseTab(BrowserTab tab)
        {
            StopPasswordCapture(tab.View.CoreWebView2);
            bool busy = HasActiveDownloads(tab.View.CoreWebView2) || tab.InPip;   // okienko obrazu w obrazie gra dalej po zamknieciu karty
            if (tab.Private && busy)
            {
                CancelEngineDownloads(tab.View.CoreWebView2);
                busy = false;
            }
            if (_tabs.Count == 1)
            {
                if (!busy) { Close(); return; } // ostatnia karta: zamknij okno (sprzatanie w OnClosingCleanup)
                AddTab(NewTabUrl); // ostatnia karta pobiera plik - zostaw okno z nowa karta
            }
            if (tab == _readTab) StopReading(); // zamykana karta jest czytana na glos - koniec czytania
            RememberClosed(tab); // do przywrocenia przez Ctrl+Shift+T
            int idx = _tabs.IndexOf(tab);
            _tabs.Remove(tab);
            TabStrip.Children.Remove(tab.Header);
            SaveSessionSoon();
            if (busy)
            {
                tab.View.Visibility = Visibility.Collapsed;
                _parkedViews.Add(tab.View);
            }
            else
            {
                Host.Children.Remove(tab.View);
                tab.View.Dispose();
            }
            if (_tabs.Count == 0) { Close(); return; }
            if (tab.RefreshTimer != null) { tab.RefreshTimer.Stop(); tab.RefreshTimer = null; }
            if (tab.Group != null) RefreshGroupsUi();
            if (tab == _current) SelectTab(_tabs[Math.Min(idx, _tabs.Count - 1)]);
        }

        readonly HashSet<WebView2> _pipViews = new HashSet<WebView2>();

        void ReleaseParkedViews()
        {
            foreach (var v in _parkedViews.ToList())
            {
                if (HasActiveDownloads(v.CoreWebView2) || _pipViews.Contains(v)) continue;
                _parkedViews.Remove(v);
                Host.Children.Remove(v);
                try { v.Dispose(); } catch (Exception) { }
            }
        }

        // ---------- nawigacja ----------

        string ToUrl(string text)
        {
            text = text.Trim();
            if (text.Length == 0) return HomeUrl;
            // adresy z wlasnym schematem (chrome-extension://, edge://, file:, about:, data:, view-source:) zostawiamy bez zmian
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^(https?|file|about|data|edge|chrome|chrome-extension|view-source|blob):", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return text;
            if (text.IndexOf(' ') < 0 && (text.IndexOf('.') > 0 || text.StartsWith("localhost")))
                return "https://" + text;
            return _settings.SearchUrl(text);
        }

        void Navigate(BrowserTab tab, string text)
        {
            if (tab.View.CoreWebView2 == null) return;
            var keyword = ExpandSearchKeyword(text);   // "yt koty" -> wyszukiwanie na YouTube
            if (keyword != null) text = keyword;
            // przypieta karta jest zamrozona - nowy adres (z innej strony) idzie do nowej karty.
            // Nie dotyczy pierwszego wczytania karty przy starcie (wtedy w karcie nie ma jeszcze strony).
            if (tab.Pinned && Restorable(tab.View.CoreWebView2.Source))
            {
                string url;
                try { url = ToUrl(text); } catch (ArgumentException) { url = _settings.SearchUrl(text); }
                if (!SameSite(url, tab.PinnedUrl)) { AddTab(url); return; }
            }
            try
            {
                var target = ToUrl(text);
                tab.View.CoreWebView2.Settings.IsReputationCheckingRequired = _settings.SmartScreen && ShouldUseReputationCheck(target) && !IsTrustedUrl(target);
                tab.View.CoreWebView2.Navigate(target);
            }
            catch (ArgumentException)
            {
                var fallback = _settings.SearchUrl(text);
                tab.View.CoreWebView2.Settings.IsReputationCheckingRequired = _settings.SmartScreen && ShouldUseReputationCheck(fallback) && !IsTrustedUrl(fallback);
                tab.View.CoreWebView2.Navigate(fallback);
            }
        }

        void Address_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || _current == null) return;
            Navigate(_current, Address.Text);
            _current.View.Focus();
        }

        // Menu prawego przycisku w pasku adresu: standardowe pozycje + "Wklej i przejdz" (jak w Chrome/Edge).
        void BuildAddressMenu()
        {
            var menu = new ContextMenu();
            var cut = new MenuItem { Header = L.T("Wytnij"), Command = ApplicationCommands.Cut, InputGestureText = "Ctrl+X", CommandTarget = Address };
            var copy = new MenuItem { Header = L.T("Kopiuj"), Command = ApplicationCommands.Copy, InputGestureText = "Ctrl+C", CommandTarget = Address };
            var paste = new MenuItem { Header = L.T("Wklej"), Command = ApplicationCommands.Paste, InputGestureText = "Ctrl+V", CommandTarget = Address };
            var pasteGo = new MenuItem { Header = L.T("Wklej i przejdź"), FontWeight = FontWeights.SemiBold };
            pasteGo.Click += (s, e) =>
            {
                string text = null;
                try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch (Exception) { }
                if (string.IsNullOrWhiteSpace(text) || _current == null) return;
                text = text.Trim().Replace("\r", "").Replace("\n", " ");
                Address.Text = text;
                Navigate(_current, text);
                _current.View.Focus();
            };
            var all = new MenuItem { Header = L.T("Zaznacz wszystko"), Command = ApplicationCommands.SelectAll, InputGestureText = "Ctrl+A", CommandTarget = Address };
            menu.Opened += (s, e) =>
            {
                bool has = false;
                try { has = Clipboard.ContainsText(); } catch (Exception) { }
                pasteGo.IsEnabled = has;
            };
            foreach (var m in new object[] { cut, copy, paste, pasteGo, new Separator(), all }) menu.Items.Add(m);
            Address.ContextMenu = menu;
        }

        void Address_Focus(object sender, KeyboardFocusChangedEventArgs e) { Dispatcher.BeginInvoke(new Action(Address.SelectAll)); }

        CoreWebView2 Core { get { return _current != null ? _current.View.CoreWebView2 : null; } }
        void Back_Click(object s, RoutedEventArgs e) { if (Core != null && Core.CanGoBack) Core.GoBack(); }
        void Forward_Click(object s, RoutedEventArgs e) { if (Core != null && Core.CanGoForward) Core.GoForward(); }
        void Reload_Click(object s, RoutedEventArgs e) { if (Core != null) Core.Reload(); }
        void Home_Click(object s, RoutedEventArgs e) { if (_current != null) Navigate(_current, HomeUrl); }
        void NewTab_Click(object s, RoutedEventArgs e) { AddTab(NewTabUrl); }
        void PrivateTab_Click(object s, RoutedEventArgs e) { AddTab(HomeUrl, true); }

        async void OnClosingCleanup(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (e.Cancel || _cleanedUp || !_settings.ClearOnExit) return; // zamkniecie anulowane - nic nie sprzatamy
            e.Cancel = true;
            _cleanedUp = true;
            Hide();
            try { await ClearBrowsingDataOnExit(_profile); } catch (Exception) { }
            Close();
        }

        void OnKeys(object sender, KeyEventArgs e)
        {
            // AltGr w polskim ukladzie to dla Windows Ctrl+Alt (AltGr+L = "ł", AltGr+A = "ą" ...),
            // wiec skrot z Ctrl liczy sie tylko bez Alt - inaczej polskie litery uruchamialyby skroty
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (key == Key.F11) SetFullScreen(!_fullScreen);
            else if (key == Key.Escape && _fullScreen && !_pageFullScreen) SetFullScreen(false);
            else if (ctrl && shift && key == Key.N) AddTab(HomeUrl, true);
            else if (ctrl && shift && key == Key.T) ReopenClosedTab();
            else if (ctrl && shift && key == Key.A) ShowTabSearch();
            else if (ctrl && shift && key == Key.F) ShowPageMemorySearch();
            else if (ctrl && shift && key == Key.U) { if (_readTab == null) StartReading(false); else ReadBtn_Click(null, null); }
            else if (ctrl && key == Key.T) AddTab(NewTabUrl);
            else if (ctrl && key == Key.W && _current != null) CloseTab(_current);
            else if (ctrl && key == Key.L) { Address.Focus(); }
            else if (ctrl && key == Key.H) History_Click(null, null);
            else if (ctrl && key == Key.D) Star_Click(null, null);
            else if (ctrl && key == Key.J) Downloads_Click(null, null);
            else if (ctrl && key == Key.Tab && _tabs.Count > 1)
                SelectTab(_tabs[(_tabs.IndexOf(_current) + (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? _tabs.Count - 1 : 1)) % _tabs.Count]);
            else if (key == Key.F5) Reload_Click(null, null);
            else if (Keyboard.Modifiers == ModifierKeys.Alt && key == Key.Left) Back_Click(null, null);
            else if (Keyboard.Modifiers == ModifierKeys.Alt && key == Key.Right) Forward_Click(null, null);
            else return;
            e.Handled = true;
        }

        // ---------- AdBlock ----------

        // Kazdy silnik blokujacy (AdBlock, reguly domen, skrypty JS) zglasza blokade tutaj - licznik i lista na tarczy.
        void NoteBlocked(BrowserTab tab, string engine, string url)
        {
            tab.Blocked++;
            _totalBlocked++;
            if (tab.BlockedItems.Count < 1000) tab.BlockedItems.Add(engine + "\t" + url);
            if (tab == _current) Dispatcher.BeginInvoke(new Action(UpdateCounter));
        }

        void UpdateCounter()
        {
            int here = _current != null ? _current.Blocked + _current.HiddenElements : 0;
            AdCounter.Text = _blocker.Enabled ? here.ToString() : L.T("wyłączony");
            AdIcon.Foreground = _blocker.Enabled ? new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D)) : new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
            AdCounter.Foreground = _blocker.Enabled ? new SolidColorBrush(Color.FromRgb(0x14, 0x53, 0x2D)) : new SolidColorBrush(Color.FromRgb(0x7F, 0x1D, 0x1D));
            AdToggle.Background = _blocker.Enabled ? new SolidColorBrush(Color.FromRgb(0xDC, 0xFC, 0xE7)) : new SolidColorBrush(Color.FromRgb(0xFE, 0xE2, 0xE2));
            ModernShield();
            AdToggle.ToolTip = L.En
                ? "Blocked on this page: " + here + " (uBlock Origin Lite: " + (_current != null ? _current.UbolBlocked : 0) + ", total " + _totalBlocked + "). AdBlock: " + _blocker.RuleCount + " rules. Click to see the list."
                : "Zablokowane na tej stronie: " + here + " (w tym uBlock Origin Lite: " + (_current != null ? _current.UbolBlocked : 0) + ", razem " + _totalBlocked + "). AdBlock: " + _blocker.RuleCount + " reguł. Kliknij, aby zobaczyć listę.";
        }

        void AdToggle_Click(object sender, RoutedEventArgs e)
        {
            var tab = _current;
            var win = new Window { Title = L.T("Zablokowane na tej stronie"), Width = 760, Height = 480, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var root = new DockPanel { Margin = new Thickness(10) };
            var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(bottom, Dock.Bottom);
            var toggle = new Button { Padding = new Thickness(12, 4, 12, 4) };
            var close = new Button { Content = L.T("Zamknij"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            DockPanel.SetDock(close, Dock.Right);
            DockPanel.SetDock(toggle, Dock.Right);
            var summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            bottom.Children.Add(close);
            bottom.Children.Add(toggle);
            bottom.Children.Add(summary);
            var list = new ListBox();
            if (_settings.PrivacyReceipt)
            {
                var receipt = BuildPrivacyReceipt(tab);
                DockPanel.SetDock(receipt, Dock.Top);
                root.Children.Add(receipt);
                win.Height = 640;
            }
            root.Children.Add(bottom);
            root.Children.Add(list);
            win.Content = root;

            Action refresh = () =>
            {
                list.Items.Clear();
                int hidden = tab != null ? tab.HiddenElements : 0;
                if (tab != null)
                {
                    foreach (var g in tab.BlockedItems.GroupBy(x => x).OrderByDescending(g => g.Count()))
                    {
                        var parts = g.Key.Split('\t');
                        list.Items.Add("[" + parts[0] + "]  " + parts[1] + (g.Count() > 1 ? "   ×" + g.Count() : ""));
                    }
                    if (hidden > 0) list.Items.Add("[" + L.T("Elementy") + "]  " + hidden + L.T(" ukrytych elementów (reguły ręczne)"));
                }
                if (list.Items.Count == 0) list.Items.Add(L.T("Nic nie zablokowano na tej stronie."));
                summary.Text = (L.En ? "This page: " : "Ta strona: ") + (tab != null ? tab.Blocked + hidden : 0) + (L.En ? "   ·   total: " : "   ·   razem: ") + _totalBlocked;
                toggle.Content = _blocker.Enabled ? L.T("Wyłącz AdBlock") : L.T("Włącz AdBlock");
            };
            toggle.Click += (s, a) =>
            {
                _blocker.Enabled = !_blocker.Enabled;
                UpdateCounter();
                if (Core != null) Core.Reload();
                refresh();
            };
            close.Click += (s, a) => win.Close();
            refresh();
            win.ShowDialog();
        }

        // ---------- pelny ekran ----------

        bool _fullScreen, _pageFullScreen;
        WindowState _stateBeforeFull;

        void SetFullScreen(bool on)
        {
            if (on == _fullScreen) return;
            _fullScreen = on;
            var bars = new UIElement[] { TabBarPanel, ToolBarPanel, BookmarkBorder };
            if (on)
            {
                _stateBeforeFull = WindowState;
                foreach (var b in bars) b.Visibility = Visibility.Collapsed;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                // Normal -> Maximized, zeby okno bez ramki zakrylo tez pasek zadan
                WindowState = WindowState.Normal;
                WindowState = WindowState.Maximized;
            }
            else
            {
                _pageFullScreen = false;
                TabBarPanel.Visibility = Visibility.Visible;
                ToolBarPanel.Visibility = Visibility.Visible;
                BookmarkBorder.Visibility = Visibility.Visible;
                RenderBookmarkBar(); // sam pasek zakladek widoczny tylko, gdy sa zakladki
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                WindowState = _stateBeforeFull;
            }
        }
    }
}











