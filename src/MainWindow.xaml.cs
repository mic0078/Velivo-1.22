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
            public bool Private;
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
            L.TranslateTree(this);   // napisy okna z XAML (dymki, przyciski) - gdy wybrano angielski
            UpdateProfileBadge();
            LoadSitePrivacyRules();
            LoadPrivacyLog();
            Closing += GuardAgainstWebViewClosingWindow; // musi byc pierwsze - anuluje zamkniecie okna przez strone
            Closing += ConfirmCloseWithDownloads;        // trwa pobieranie? zapytaj i wstrzymaj
            Closing += (s, e) => { if (!e.Cancel) SaveSession(); }; // karty do przywrocenia przy nastepnym starcie
            Closing += OnClosingCleanup;
            Closed += (s, e) => { StopMost(); StopLanSync(); };
            LoadJobs(); // lista pobran z poprzedniego uruchomienia (przerwane mozna wznowic)
            LoadZoom();
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
                    var session = LoadSession(); // karty z poprzedniego uruchomienia
                    foreach (var u in session) AddTab(u);
                    if (session.Count > 0 && _startUrls.Length == 0) SelectTab(_tabs[Math.Min(LoadSessionActive(), _tabs.Count - 1)]);
                    if (session.Count == 0 && _startUrls.Length == 0) AddTab("");
                    foreach (var u in _startUrls) AddTab(u);
                    _sessionLoaded = true;
                    SaveSessionSoon();
                    StartInstanceServer(); // linki z innych programow -> nowe karty w tym oknie
                    StartLanSync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Nie udało się uruchomić WebView2:\n" + ex.Message, "Velivo");
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
            var tab = new BrowserTab { Private = isPrivate, View = new WebView2(), Title = new TextBlock { Text = L.T("Nowa karta"), MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center } };
            var close = new Button { Content = "×", Width = 20, Height = 20, FontSize = 13, Margin = new Thickness(6, 0, 0, 0) };
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(tab.Title);
            panel.Children.Add(close);
            tab.Header = new Button { Content = panel, Width = double.NaN, Padding = new Thickness(10, 0, 4, 0), Height = 32, Margin = new Thickness(1, 4, 0, 0) };
            if (isPrivate)
            {
                tab.Title.Foreground = Brushes.White;
                close.Foreground = Brushes.White;
                tab.Title.Text = L.T("🕶 Prywatna");
            }
            tab.StartUrl = url;
            tab.Header.ContextMenu = BuildTabMenu(tab);
            tab.Header.Click += (s, e) => SelectTab(tab);
            close.Click += (s, e) => { CloseTab(tab); e.Handled = true; };
            tab.Header.MouseUp += (s, e) => { if (e.ChangedButton == MouseButton.Middle) CloseTab(tab); };

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
                opts.IsInPrivateModeEnabled = tab.Private;
                await tab.View.EnsureCoreWebView2Async(_env, opts);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Nie udało się otworzyć karty.\nJeśli działa jeszcze starsza wersja Velivo, zamknij ją i spróbuj ponownie.\n\n" + ex.Message, "Velivo");
                if (deferral != null) deferral.Complete();
                if (_tabs.Contains(tab)) CloseTab(tab);
                return;
            }
            var core = tab.View.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            if (!tab.Private && _profile == null) _profile = core.Profile;
            ApplyViewSettings(core);
            HookAutofill(tab, core);
            await HookPasswordVault(tab, core);
            tab.View.ZoomFactorChanged += (s, e) => OnZoomChanged(tab);
            ApplyDarkMode(tab);
            // Strony nie musza wiedziec, ze to WebView2 - Google blokuje logowanie w "przegladarkach wbudowanych".
            // chrome.webview jest potrzebny tylko w okienkach dodatkow (osobne widoki), w kartach go wylaczamy.
            core.Settings.IsWebMessageEnabled = true;   // kanal dla Szybkiego Dostepu; wiadomosci z innych stron sa ignorowane
            core.WebMessageReceived += async (s, e) => await HandleQuickAccessWebMessageAsync(core, e);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(HideWebViewBrandScript);
            await EnsureBundledQuickAccessAsync();
            if (!_extensionsLoaded)
            {
                _extensionsLoaded = true;
                await RefreshExtensions();
                await SaveExtensionsSyncListAsync();
                await ApplyExtensionsSyncListAsync();
                await RefreshExtensions();
            }
            if (_settings.SendDnt) // navigator.globalPrivacyControl dla skryptow strony
                await core.AddScriptToExecuteOnDocumentCreatedAsync(
                    "try { Object.defineProperty(Navigator.prototype, 'globalPrivacyControl', { get: () => true }); } catch (e) {}");

            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (s, e) =>
            {
                if (IsQuickAccessUrl(e.Request.Uri)) return;

                Uri requestUri;
                if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out requestUri) ||
                    (requestUri.Scheme != Uri.UriSchemeHttp && requestUri.Scheme != Uri.UriSchemeHttps))
                    return;

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
                tab.Blocked++;
                _totalBlocked++;
                AddPrivacyBlock("Tracker zablokowany (AdBlock)", e.Request.Uri, tab);
                Dispatcher.BeginInvoke(new Action(UpdateCounter));
            };

            core.NewWindowRequested += (s, e) => OnNewWindowRequested(e, tab.Private);
            core.DocumentTitleChanged += (s, e) =>
            {
                tab.Title.Text = (tab.Private ? "🕶 " : "") + (string.IsNullOrEmpty(core.DocumentTitle) ? core.Source : core.DocumentTitle);
                tab.Header.ToolTip = tab.Title.Text;
                if (tab == _current) Title = BuildWindowTitle(tab.Title.Text);
            };
            core.NavigationStarting += (s, e) =>
            {
                tab.LastRequestedUrl = e.Uri;
                if (!IsQuickAccessUrl(e.Uri)) tab.QuickAccessRecoveryTried = false;
                core.Settings.IsWebMessageEnabled = true;   // zmiana dziala dopiero od nastepnej nawigacji - wiec stale wlaczone; odbiorca sprawdza nadawce (IsQuickAccessUrl)
                core.Settings.IsReputationCheckingRequired = _settings.SmartScreen && ShouldUseReputationCheck(e.Uri) && !IsTrustedUrl(e.Uri);
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
                if (e.IsRedirected) return;
                tab.Blocked = 0;
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
            core.DOMContentLoaded += (s, e) => { ApplyElementRules(core); ApplyLiveDarkCss(core); };   // elementy zablokowane recznie (menu kontekstowe)
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
                if (e.IsSuccess) _ = ApplyAutoClearRule(tab);
                if (e.IsSuccess) CheckSejfLogins(tab); // pole hasla? -> loginy z Sejfu dla tej strony
                if (e.IsSuccess) LoadVoiceNames(core);  // raz: lista polskich glosow do ustawien
                if (e.IsSuccess) _ = CapturePageThumbAsync(tab);   // miniatura strony dla Szybkiego Dostepu
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
            var view = new WebView2();
            var win = new Window { Title = AppTitleLabel, Width = f.Width + 16, Height = f.Height + 39, Content = view, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            if (f.HasPosition) { win.WindowStartupLocation = WindowStartupLocation.Manual; win.Left = f.Left; win.Top = f.Top; }
            win.Closed += (s, a) => view.Dispose();
            win.Show();
            try
            {
                var opts = _env.CreateCoreWebView2ControllerOptions();
                opts.IsInPrivateModeEnabled = isPrivate;
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
                core.NewWindowRequested += (s, a) => OnNewWindowRequested(a, isPrivate);
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
                t.Header.Background = t.Private
                    ? new SolidColorBrush(on ? Color.FromRgb(0x4C, 0x1D, 0x95) : Color.FromRgb(0x6D, 0x28, 0xD9))
                    : (on ? ActiveTabBrush : Brushes.Transparent);
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
            if (w < 1360) Push(ExtensionsBtn, L.T("Dodatki"), () => Extensions_Click(null, null));
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
            bool busy = HasActiveDownloads(tab.View.CoreWebView2);
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
            if (tab == _current) SelectTab(_tabs[Math.Min(idx, _tabs.Count - 1)]);
        }

        void ReleaseParkedViews()
        {
            foreach (var v in _parkedViews.ToList())
            {
                if (HasActiveDownloads(v.CoreWebView2)) continue;
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

        void UpdateCounter()
        {
            int here = _current != null ? _current.Blocked : 0;
            AdCounter.Text = _blocker.Enabled ? here + (L.En ? "  (total " : "  (razem ") + _totalBlocked + ")" : L.T("wyłączony");
            AdIcon.Foreground = _blocker.Enabled ? new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D)) : new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
            AdCounter.Foreground = _blocker.Enabled ? new SolidColorBrush(Color.FromRgb(0x14, 0x53, 0x2D)) : new SolidColorBrush(Color.FromRgb(0x7F, 0x1D, 0x1D));
            AdToggle.ToolTip = L.En
                ? "AdBlock: " + _blocker.RuleCount + " rules. Click to turn " + (_blocker.Enabled ? "off" : "on") + "."
                : "AdBlock: " + _blocker.RuleCount + " reguł. Kliknij, aby " + (_blocker.Enabled ? "wyłączyć" : "włączyć") + ".";
        }

        void AdToggle_Click(object sender, RoutedEventArgs e)
        {
            _blocker.Enabled = AdToggle.IsChecked == true;
            AdToggle.Background = _blocker.Enabled ? new SolidColorBrush(Color.FromRgb(0xDC, 0xFC, 0xE7)) : new SolidColorBrush(Color.FromRgb(0xFE, 0xE2, 0xE2));
            UpdateCounter();
            if (Core != null) Core.Reload();
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











