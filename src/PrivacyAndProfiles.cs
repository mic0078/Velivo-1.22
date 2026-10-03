using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace Przegladarka
{
    public partial class MainWindow
    {
        sealed class SitePrivacyRule
        {
            public string Domain;
            public bool BlockJs;
            public bool BlockCookies;
            public bool StrictTrackers;
            public bool AutoClearData;
            public bool Trusted;      // zaufana domena: bez AdBlocka, trackerow i SmartScreen

            public override string ToString()
            {
                var tags = new List<string>();
                if (Trusted) tags.Add("zaufana");
                if (BlockJs) tags.Add("JS");
                if (BlockCookies) tags.Add("cookies");
                if (StrictTrackers) tags.Add("trackery");
                if (AutoClearData) tags.Add("autoczyszczenie");
                return Domain + "  [" + (tags.Count == 0 ? "bez reguł" : string.Join(", ", tags)) + "]";
            }
        }

        sealed class PrivacyBlockItem
        {
            public DateTime Time;
            public string Domain;
            public string Url;
            public string Reason;
            public bool PrivateTab;

            public override string ToString()
            {
                return Time.ToString("HH:mm:ss") + "  " + (PrivateTab ? "🕶 " : "") + Domain + "  ·  " + Reason;
            }
        }

        sealed class PrivacyLogRow
        {
            public string Time { get; set; }
            public string Domain { get; set; }
            public string Reason { get; set; }
            public string Mode { get; set; }
            public PrivacyBlockItem Raw { get; set; }
        }

        readonly Dictionary<string, SitePrivacyRule> _privacyRules = new Dictionary<string, SitePrivacyRule>(StringComparer.OrdinalIgnoreCase);
        readonly List<PrivacyBlockItem> _privacyBlocks = new List<PrivacyBlockItem>();
        static string PrivacyRulesFile { get { return Path.Combine(DataDir, "prywatnosc.txt"); } }
        static string PrivacyLogFile { get { return Path.Combine(DataDir, "prywatnosc-log.txt"); } }

        static string ProfileRoot
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Przegladarka"); }
        }

        static string ActiveProfileFile { get { return Path.Combine(ProfileRoot, "active-profile.txt"); } }
        static string ProfilesDir { get { return Path.Combine(ProfileRoot, "Profiles"); } }
        static string ProfilesFile { get { return Path.Combine(ProfileRoot, "profiles.txt"); } }
        static string ProfileIconsFile { get { return Path.Combine(ProfileRoot, "profiles-icons.txt"); } }
        static readonly string[] ProfileIconChoices = { "👤", "🧑", "💼", "📚", "🎯", "🎮", "🚀", "🛡️", "⚡", "🌙", "🐱", "🦊", "🐼", "🦄" };

        static string NormalizeProfileName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "domyslny";
            var src = raw.Trim().ToLowerInvariant();
            var sb = new StringBuilder();
            foreach (var ch in src)
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_') sb.Append(ch);
            }
            return sb.Length == 0 ? "domyslny" : sb.ToString();
        }

        internal static string SelectedProfileName
        {
            get { return NormalizeProfileName(Environment.GetEnvironmentVariable("VELIVO_PROFILE") ?? "domyslny"); }
        }

        static List<string> GetKnownProfiles()
        {
            var list = new List<string> { "domyslny" };
            try
            {
                if (File.Exists(ProfilesFile))
                    foreach (var line in File.ReadAllLines(ProfilesFile))
                    {
                        var n = NormalizeProfileName(line);
                        if (!list.Contains(n)) list.Add(n);
                    }
            }
            catch (Exception) { }
            try
            {
                if (Directory.Exists(ProfilesDir))
                    foreach (var d in Directory.GetDirectories(ProfilesDir))
                    {
                        var n = NormalizeProfileName(Path.GetFileName(d));
                        if (!list.Contains(n)) list.Add(n);
                    }
            }
            catch (Exception) { }
            return list.OrderBy(x => x).ToList();
        }

        static void SaveKnownProfiles(IEnumerable<string> profiles)
        {
            try
            {
                Directory.CreateDirectory(ProfileRoot);
                var list = profiles.Select(NormalizeProfileName)
                    .Where(x => x.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (!list.Contains("domyslny")) list.Insert(0, "domyslny");
                File.WriteAllLines(ProfilesFile, list.OrderBy(x => x));
            }
            catch (Exception) { }
        }

        static string ProfileDisplayName(string profile)
        {
            if (string.IsNullOrWhiteSpace(profile) || profile == "domyslny") return L.T("Domyślny");
            return char.ToUpper(profile[0]) + (profile.Length > 1 ? profile.Substring(1) : "");
        }

        static Dictionary<string, string> LoadProfileIcons()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(ProfileIconsFile)) return map;
                foreach (var line in File.ReadAllLines(ProfileIconsFile))
                {
                    int t = line.IndexOf('\t');
                    if (t <= 0) continue;
                    var profile = NormalizeProfileName(line.Substring(0, t));
                    var icon = line.Substring(t + 1).Trim();
                    if (profile.Length > 0 && icon.Length > 0) map[profile] = icon;
                }
            }
            catch (Exception) { }
            return map;
        }

        static void SaveProfileIcons(Dictionary<string, string> map)
        {
            try
            {
                Directory.CreateDirectory(ProfileRoot);
                File.WriteAllLines(ProfileIconsFile, map.OrderBy(k => k.Key).Select(k => k.Key + "\t" + k.Value));
            }
            catch (Exception) { }
        }

        static string DefaultIconForProfile(string profile)
        {
            int idx = Math.Abs((profile ?? "domyslny").GetHashCode()) % (ProfileIconChoices.Length - 1);
            return ProfileIconChoices[idx + 1];
        }

        static string GetProfileIcon(string profile)
        {
            var p = NormalizeProfileName(profile);
            if (p == "domyslny") return "👤";
            var map = LoadProfileIcons();
            string icon;
            return map.TryGetValue(p, out icon) && icon.Length > 0 ? icon : DefaultIconForProfile(p);
        }

        static void SetProfileIcon(string profile, string icon)
        {
            var p = NormalizeProfileName(profile);
            if (p.Length == 0) return;
            var map = LoadProfileIcons();
            if (string.IsNullOrWhiteSpace(icon)) map.Remove(p);
            else map[p] = icon.Trim();
            SaveProfileIcons(map);
        }

        static void RemoveProfileIcon(string profile)
        {
            var p = NormalizeProfileName(profile);
            var map = LoadProfileIcons();
            if (!map.Remove(p)) return;
            SaveProfileIcons(map);
        }

        void ProfileBadge_Click(object sender, RoutedEventArgs e)
        {
            OpenProfilesManager();
        }

        void UpdateProfileBadge()
        {
            if (ProfileBadgeBtn == null) return;
            var p = SelectedProfileName;
            string label = ProfileDisplayName(p);
            string icon = GetProfileIcon(p);
            string letter = label.Substring(0, 1).ToUpperInvariant();
            ProfileBadgeBtn.Content = _toolbarCompact ? (icon + " " + letter) : (icon + " " + letter + "  " + label);
            ProfileBadgeBtn.MinWidth = _toolbarCompact ? 54 : 86;
            ProfileBadgeBtn.Padding = _toolbarCompact ? new Thickness(8, 0, 8, 0) : new Thickness(10, 0, 10, 0);
            ProfileBadgeBtn.ToolTip = L.T("Aktywny profil: ") + label + L.T("\nKliknij, aby przełączyć użytkownika/profil");

            var palette = new[]
            {
                new { Bg = Color.FromRgb(0xDB, 0xEA, 0xFE), Fg = Color.FromRgb(0x1D, 0x4E, 0xD8) },
                new { Bg = Color.FromRgb(0xDC, 0xFC, 0xE7), Fg = Color.FromRgb(0x15, 0x80, 0x3D) },
                new { Bg = Color.FromRgb(0xFE, 0xF3, 0xC7), Fg = Color.FromRgb(0xB4, 0x53, 0x09) },
                new { Bg = Color.FromRgb(0xF3, 0xE8, 0xFF), Fg = Color.FromRgb(0x7E, 0x22, 0xCE) },
                new { Bg = Color.FromRgb(0xE0, 0xF2, 0xFE), Fg = Color.FromRgb(0x0C, 0x4A, 0x6E) },
            };
            int idx = Math.Abs((p ?? "").GetHashCode()) % palette.Length;
            ProfileBadgeBtn.Background = new SolidColorBrush(palette[idx].Bg);
            ProfileBadgeBtn.Foreground = new SolidColorBrush(palette[idx].Fg);
        }

        SitePrivacyRule RuleForHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return null;
            host = host.ToLowerInvariant();
            while (true)
            {
                SitePrivacyRule r;
                if (_privacyRules.TryGetValue(host, out r)) return r;
                int dot = host.IndexOf('.');
                if (dot < 0) break;
                host = host.Substring(dot + 1);
            }
            return null;
        }

        SitePrivacyRule RuleForUrl(string url)
        {
            Uri u;
            // Reguly domen dotycza tylko stron www - nie dodatkow (chrome-extension://, np. Szybki Dostep).
            if (!Uri.TryCreate(url, UriKind.Absolute, out u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) return null;
            return RuleForHost(u.Host);
        }

        // Poziom ochrony silnika Edge jest wspolny dla wszystkich kart - ustawiamy go wg aktywnej strony:
        // wymuszone trackery -> scisla; zaufana domena -> zrownowazona; reszta -> wg ustawien.
        void UpdateTrackingLevel(string url)
        {
            try
            {
                var core = _current != null ? _current.View.CoreWebView2 : null;
                if (core == null) return;
                var r = RuleForUrl(url);
                bool strict = r != null && r.StrictTrackers ? true
                    : (r != null && r.Trusted) || IsQuickAccessUrl(url) ? false
                    : _settings.StrictTracking;
                var level = strict ? CoreWebView2TrackingPreventionLevel.Strict : CoreWebView2TrackingPreventionLevel.Balanced;
                if (core.Profile.PreferredTrackingPreventionLevel != level) core.Profile.PreferredTrackingPreventionLevel = level;
            }
            catch (Exception) { }
        }

        bool IsTrustedUrl(string url)
        {
            if (IsQuickAccessUrl(url)) return true;   // Szybki Dostep jest zawsze zaufany
            var r = RuleForUrl(url);
            return r != null && r.Trusted;
        }

        // Pozwala wkleic caly adres (https://www.example.com/strona) - zostaje sama domena.
        static string NormalizeRuleDomain(string text)
        {
            var d = (text ?? "").Trim().ToLowerInvariant();
            Uri u;
            if (d.Contains("://") && Uri.TryCreate(d, UriKind.Absolute, out u)) d = u.Host;
            else
            {
                int slash = d.IndexOf('/');
                if (slash >= 0) d = d.Substring(0, slash);
            }
            if (d.StartsWith("*.")) d = d.Substring(2);
            return d.Trim('.');
        }

        void LoadSitePrivacyRules()
        {
            _privacyRules.Clear();
            bool dropped = false;
            try
            {
                if (!File.Exists(PrivacyRulesFile)) return;
                foreach (var line in File.ReadAllLines(PrivacyRulesFile))
                {
                    var p = line.Split('\t');
                    if (p.Length < 5) continue;
                    var d = NormalizeProfileName(p[0]).Replace("_", "-");
                    var rule = new SitePrivacyRule
                    {
                        Domain = p[0].Trim().ToLowerInvariant(),
                        BlockJs = p[1] == "1",
                        BlockCookies = p[2] == "1",
                        StrictTrackers = p[3] == "1",
                        AutoClearData = p[4] == "1",
                        Trusted = p.Length > 5 && p[5] == "1",
                    };
                    // stare wpisy typu "chrome-extension://.../newtab.html" (adres dodatku zamiast domeny) usuwamy -
                    // wygladaly jak blokada Szybkiego Dostepu
                    if (rule.Domain.Contains("://") || rule.Domain.Contains("/") || rule.Domain.IndexOf('.') < 1) { dropped = true; continue; }
                    if (rule.Domain.Length > 0) _privacyRules[rule.Domain] = rule;
                }
            }
            catch (IOException) { }
            if (dropped) SaveSitePrivacyRules();
        }

        void SaveSitePrivacyRules()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllLines(PrivacyRulesFile,
                    _privacyRules.Values.OrderBy(x => x.Domain)
                        .Select(x => x.Domain + "\t" + (x.BlockJs ? "1" : "0") + "\t" + (x.BlockCookies ? "1" : "0") + "\t" + (x.StrictTrackers ? "1" : "0") + "\t" + (x.AutoClearData ? "1" : "0") + "\t" + (x.Trusted ? "1" : "0")));
            }
            catch (IOException) { }
            if (_current != null && _current.View.CoreWebView2 != null) UpdateTrackingLevel(_current.View.CoreWebView2.Source);
            NotifyLanStateChanged();
        }

        void LoadPrivacyLog()
        {
            _privacyBlocks.Clear();
            try
            {
                if (!File.Exists(PrivacyLogFile)) return;
                foreach (var line in File.ReadLines(PrivacyLogFile).TakeLast(400))
                {
                    var p = line.Split('\t');
                    if (p.Length < 5) continue;
                    DateTime t;
                    if (!DateTime.TryParse(p[0], out t)) t = DateTime.Now;
                    _privacyBlocks.Add(new PrivacyBlockItem
                    {
                        Time = t,
                        Domain = p[1],
                        Reason = p[2],
                        Url = p[3],
                        PrivateTab = p[4] == "1",
                    });
                }
            }
            catch (IOException) { }
        }

        void SavePrivacyLog()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                var lines = _privacyBlocks.TakeLast(400).Select(x =>
                    x.Time.ToString("s") + "\t" + (x.Domain ?? "") + "\t" + (x.Reason ?? "") + "\t" + (x.Url ?? "") + "\t" + (x.PrivateTab ? "1" : "0"));
                File.WriteAllLines(PrivacyLogFile, lines);
            }
            catch (IOException) { }
        }

        void AddPrivacyBlock(string reason, string url, BrowserTab tab)
        {
            Uri u;
            string host = Uri.TryCreate(url, UriKind.Absolute, out u) ? u.Host : "?";
            _privacyBlocks.Add(new PrivacyBlockItem
            {
                Time = DateTime.Now,
                Domain = host,
                Url = url,
                Reason = reason,
                PrivateTab = tab != null && tab.Private
            });
            if (_privacyBlocks.Count > 500) _privacyBlocks.RemoveRange(0, _privacyBlocks.Count - 500);
            SavePrivacyLog();
        }

        bool ApplyPrivacyRulesToRequest(CoreWebView2WebResourceRequestedEventArgs e, BrowserTab tab)
        {
            if (AdBlocker.IsLocalNetworkUri(e.Request.Uri)) return false;
            var rule = RuleForUrl(e.Request.Uri);
            if (rule == null) return false;

            if (rule.BlockCookies)
            {
                try { e.Request.Headers.RemoveHeader("Cookie"); } catch (Exception) { }
            }

            if (rule.BlockJs && e.ResourceContext == CoreWebView2WebResourceContext.Script)
            {
                e.Response = _env.CreateWebResourceResponse(null, 403, "Blocked", "");
                NoteBlocked(tab, L.T("JS"), e.Request.Uri);
                AddPrivacyBlock(L.T("Skrypt (JS) zablokowany regułą domeny"), e.Request.Uri, tab);
                return true;
            }

            if (rule.StrictTrackers && _blocker.ShouldBlockForced(e.Request.Uri))
            {
                e.Response = _env.CreateWebResourceResponse(null, 403, "Blocked", "");
                NoteBlocked(tab, L.T("Reguła domeny"), e.Request.Uri);
                AddPrivacyBlock(L.T("Tracker zablokowany (reguła domeny)"), e.Request.Uri, tab);
                return true;
            }

            return false;
        }

        async Task ApplyAutoClearRule(BrowserTab tab)
        {
            if (tab == null || tab.View.CoreWebView2 == null) return;
            Uri u;
            if (!Uri.TryCreate(tab.View.CoreWebView2.Source, UriKind.Absolute, out u)) return;
            var rule = RuleForUrl(u.AbsoluteUri);   // czyszczenie danych dodatku kasowalo skroty Szybkiego Dostepu
            if (rule == null || (!rule.AutoClearData && !rule.BlockCookies)) return;

            try
            {
                var core = tab.View.CoreWebView2;
                if (rule.BlockCookies)
                {
                    var cookies = await core.CookieManager.GetCookiesAsync(u.Scheme + "://" + u.Host + "/");
                    foreach (var c in cookies) core.CookieManager.DeleteCookie(c);
                }
                if (rule.AutoClearData)
                {
                    await core.ExecuteScriptAsync("try { localStorage.clear(); sessionStorage.clear(); if (window.indexedDB && indexedDB.databases) indexedDB.databases().then(db => db.forEach(x => x && x.name && indexedDB.deleteDatabase(x.name))); } catch (e) {} ");
                    AddPrivacyBlock(L.T("Automatyczne czyszczenie danych domeny"), core.Source, tab);
                }
                if (rule.BlockCookies)
                    AddPrivacyBlock(L.T("Cookies usunięte dla domeny (reguła)"), core.Source, tab);
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        void PrivacyPanel_Click(object sender, RoutedEventArgs e)
        {
            OpenPrivacyPanel();
        }

        void OpenPrivacyPanel()
        {
            Uri u;
            string host = (Core != null && Uri.TryCreate(Core.Source, UriKind.Absolute, out u) &&
                (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)) ? u.Host.ToLowerInvariant() : "";
            // Szybki Dostep (i inne strony dodatkow) nie jest domena - jest zaufany na stale, nie pokazujemy go tu wcale

            var win = new Window
            {
                Title = L.T("Prywatność i antyfingerprinting"),
                Width = 1080,
                Height = 700,
                MinWidth = 920,
                MinHeight = 620,
                ResizeMode = ResizeMode.CanResize,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Brushes.White
            };

            var root = new Grid { Margin = new Thickness(12) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.42, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.58, GridUnitType.Star) });

            var left = new Grid { Margin = new Thickness(0, 0, 10, 0) };
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var right = new Grid { Margin = new Thickness(10, 0, 0, 0) };
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var rulesList = new ListBox
            {
                Margin = new Thickness(0, 6, 0, 0),
                MinHeight = 220,
                FontSize = 13,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB))
            };

            var logList = new ListView
            {
                Margin = new Thickness(0, 6, 0, 0),
                FontSize = 12,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB))
            };
            var gv = new GridView();
            gv.Columns.Add(new GridViewColumn { Header = L.T("Godzina"), DisplayMemberBinding = new System.Windows.Data.Binding("Time"), Width = 74 });
            gv.Columns.Add(new GridViewColumn { Header = L.T("Domena"), DisplayMemberBinding = new System.Windows.Data.Binding("Domain"), Width = 190 });
            gv.Columns.Add(new GridViewColumn { Header = L.T("Powód"), DisplayMemberBinding = new System.Windows.Data.Binding("Reason"), Width = 250 });
            gv.Columns.Add(new GridViewColumn { Header = L.T("Tryb"), DisplayMemberBinding = new System.Windows.Data.Binding("Mode"), Width = 66 });
            logList.View = gv;

            var details = new TextBox
            {
                Margin = new Thickness(0, 6, 0, 0),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MinHeight = 92,
                Background = new SolidColorBrush(Color.FromRgb(0xF9, 0xFA, 0xFB)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB))
            };

            var domRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            domRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            domRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var domLbl = new TextBlock { Text = L.T("Domena:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var dom = new TextBox { Text = host, Padding = new Thickness(6, 4, 6, 4), MinHeight = 30 };
            Grid.SetColumn(domLbl, 0);
            Grid.SetColumn(dom, 1);
            domRow.Children.Add(domLbl);
            domRow.Children.Add(dom);

            var js = new CheckBox { Content = L.T("Blokuj JavaScript dla domeny"), Margin = new Thickness(0, 6, 0, 0) };
            var ck = new CheckBox { Content = L.T("Nie wysyłaj cookies dla domeny"), Margin = new Thickness(0, 4, 0, 0) };
            var tr = new CheckBox { Content = L.T("Wymuś blokowanie trackerów dla domeny"), Margin = new Thickness(0, 4, 0, 0) };
            var cl = new CheckBox { Content = L.T("Automatycznie czyść dane po wejściu na domenę"), Margin = new Thickness(0, 4, 0, 0) };
            var trusted = new CheckBox { Content = L.T("Zaufana domena (bez blokowania i SmartScreen)"), Margin = new Thickness(0, 8, 0, 0), FontWeight = FontWeights.SemiBold };
            var info = new TextBlock
            {
                Text = L.T("Reguły działają per domena. Karty prywatne używają osobnego, izolowanego storage WebView2 (InPrivate)."),
                Margin = new Thickness(0, 0, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12
            };

            var optionsBox = new Border
            {
                Margin = new Thickness(0, 8, 0, 0),
                Padding = new Thickness(10, 8, 10, 8),
                Background = new SolidColorBrush(Color.FromRgb(0xF9, 0xFA, 0xFB)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
                BorderThickness = new Thickness(1),
                Child = new StackPanel()
            };
            var optionsPanel = optionsBox.Child as StackPanel;
            optionsPanel.Children.Add(js);
            optionsPanel.Children.Add(ck);
            optionsPanel.Children.Add(tr);
            optionsPanel.Children.Add(cl);
            optionsPanel.Children.Add(trusted);

            Action refresh = () =>
            {
                rulesList.Items.Clear();
                foreach (var r in _privacyRules.Values.OrderBy(x => x.Domain)) rulesList.Items.Add(r);

                logList.Items.Clear();
                foreach (var l in _privacyBlocks.TakeLast(250).Reverse())
                {
                    logList.Items.Add(new PrivacyLogRow
                    {
                        Time = l.Time.ToString("HH:mm:ss"),
                        Domain = l.Domain,
                        Reason = l.Reason,
                        Mode = l.PrivateTab ? L.T("Prywat.") : L.T("Zwykły"),
                        Raw = l
                    });
                }
                UpdatePrivacyButton();
            };

            Action loadCurrentDomain = () =>
            {
                if (IsQuickAccessExtensionId((dom.Text ?? "").Trim()) || (dom.Text ?? "").Contains("chrome-extension"))
                {
                    dom.Text = "";
                    MessageBox.Show(win, L.T("Szybki Dostęp to wbudowany dodatek – jest zaufany na stałe i nic go nie blokuje. Nie trzeba dodawać reguły."), L.T("Prywatność"));
                    return;
                }
                var d = NormalizeRuleDomain(dom.Text);
                SitePrivacyRule r;
                if (!_privacyRules.TryGetValue(d, out r)) r = new SitePrivacyRule { Domain = d };
                js.IsChecked = r.BlockJs;
                ck.IsChecked = r.BlockCookies;
                tr.IsChecked = r.StrictTrackers;
                cl.IsChecked = r.AutoClearData;
                trusted.IsChecked = r.Trusted;
            };

            dom.TextChanged += (s, e) => loadCurrentDomain();

            rulesList.SelectionChanged += (s, e) =>
            {
                var r = rulesList.SelectedItem as SitePrivacyRule;
                if (r == null) return;
                dom.Text = r.Domain;
            };

            logList.SelectionChanged += (s, e) =>
            {
                var row = logList.SelectedItem as PrivacyLogRow;
                if (row == null || row.Raw == null)
                {
                    details.Text = "";
                    return;
                }
                var b = row.Raw;
                details.Text = L.T("Godzina: ") + b.Time.ToString("yyyy-MM-dd HH:mm:ss") +
                               L.T("\nDomena: ") + (b.Domain ?? "") +
                               L.T("\nPowód: ") + (b.Reason ?? "") +
                               L.T("\nTryb: ") + (b.PrivateTab ? L.T("Prywatny") : L.T("Zwykły")) +
                               "\nURL: " + (b.Url ?? "");
            };

            var save = SmallButton(L.T("Zapisz regułę"), () =>
            {
                var d = NormalizeRuleDomain(dom.Text);
                if (d.Length < 3 || d.IndexOf('.') < 1)
                {
                    MessageBox.Show(win, L.T("Podaj poprawną domenę stron www, np. example.com (adresy chrome-extension:// dodatków nie są blokowane regułami)."), L.T("Prywatność"));
                    return;
                }
                bool destructive = (ck.IsChecked == true) || (cl.IsChecked == true);
                if (destructive)
                {
                    var txt = L.T("Ta reguła może powodować utratę logowania i ustawień strony (cookies/sesja/localStorage).\n\n") +
                              L.T("Domena: ") + d + "\n\n" +
                              L.T("Zapisać mimo to?");
                    if (MessageBox.Show(win, txt, L.T("Prywatność"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                        return;
                }
                var r = new SitePrivacyRule
                {
                    Domain = d,
                    BlockJs = js.IsChecked == true,
                    BlockCookies = ck.IsChecked == true,
                    StrictTrackers = tr.IsChecked == true,
                    AutoClearData = cl.IsChecked == true,
                    Trusted = trusted.IsChecked == true,
                };
                _privacyRules[d] = r;
                SaveSitePrivacyRules();
                refresh();
                if (Core != null) Core.Reload();
            });

            var del = SmallButton(L.T("Usuń regułę"), () =>
            {
                var d = NormalizeRuleDomain(dom.Text);
                if (_privacyRules.Remove(d) || _privacyRules.Remove((dom.Text ?? "").Trim().ToLowerInvariant()))
                {
                    SaveSitePrivacyRules();
                    refresh();
                }
            });

            Action<string> addTrusted = (text) =>
            {
                var d = NormalizeRuleDomain(text);
                if (d.Length < 3 || d.IndexOf('.') < 1)
                {
                    MessageBox.Show(win, L.T("Podaj poprawną domenę stron www, np. example.com"), L.T("Zaufane domeny"));
                    return;
                }
                SitePrivacyRule existing;
                if (!_privacyRules.TryGetValue(d, out existing)) existing = new SitePrivacyRule { Domain = d };
                existing.Trusted = true;
                existing.BlockJs = false;
                existing.StrictTrackers = false;
                _privacyRules[d] = existing;
                SaveSitePrivacyRules();
                dom.Text = d;
                refresh();
                if (Core != null) Core.Reload();
            };

            var trustBtn = SmallButton(L.T("✔ Dodaj do zaufanych"), () => addTrusted(dom.Text));
            var trustLogBtn = SmallButton(L.T("✔ Zaznaczoną domenę do zaufanych"), () =>
            {
                var row = logList.SelectedItem as PrivacyLogRow;
                if (row == null || string.IsNullOrWhiteSpace(row.Domain))
                {
                    MessageBox.Show(win, L.T("Najpierw zaznacz wpis na liście blokad."), L.T("Zaufane domeny"));
                    return;
                }
                addTrusted(row.Domain);
            });

            var clearLog = SmallButton(L.T("Wyczyść panel blokad"), () =>
            {
                _privacyBlocks.Clear();
                SavePrivacyLog();
                refresh();
                details.Text = "";
            });

            var prof = new TextBlock
            {
                Text = L.T("Aktywny profil: ") + SelectedProfileName,
                Margin = new Thickness(0, 10, 0, 4),
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27))
            };
            var profBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 0) };
            profBtns.Children.Add(SmallButton(L.T("Praca"), () => SwitchProfile("praca")));
            profBtns.Children.Add(SmallButton(L.T("Prywatny"), () => SwitchProfile("prywatny")));
            profBtns.Children.Add(SmallButton("Dev", () => SwitchProfile("dev")));
            profBtns.Children.Add(SmallButton(L.T("Więcej…"), OpenProfilesManager));

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            actions.Children.Add(save);
            actions.Children.Add(del);
            actions.Children.Add(trustBtn);

            var leftHeader = new TextBlock { Text = L.T("Reguły prywatności dla domen"), FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)) };
            var rulesCaption = new TextBlock { Text = L.T("Zapisane reguły domen"), Margin = new Thickness(0, 10, 0, 2), FontWeight = FontWeights.SemiBold };

            Grid.SetRow(leftHeader, 0);
            Grid.SetRow(info, 1);
            Grid.SetRow(domRow, 2);
            Grid.SetRow(optionsBox, 3);
            Grid.SetRow(actions, 4);

            var rulesPanel = new StackPanel();
            rulesPanel.Children.Add(rulesCaption);
            rulesPanel.Children.Add(rulesList);
            Grid.SetRow(rulesPanel, 5);

            var profilePanel = new StackPanel();
            profilePanel.Children.Add(prof);
            profilePanel.Children.Add(profBtns);
            Grid.SetRow(profilePanel, 6);

            left.Children.Add(leftHeader);
            left.Children.Add(info);
            left.Children.Add(domRow);
            left.Children.Add(optionsBox);
            left.Children.Add(actions);
            left.Children.Add(rulesPanel);
            left.Children.Add(profilePanel);

            var rightHeaderRow = new Grid();
            rightHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rightHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var rightHeader = new TextBlock { Text = L.T("Co zostało zablokowane i dlaczego"), FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(rightHeader, 0);
            var rightButtons = new StackPanel { Orientation = Orientation.Horizontal };
            rightButtons.Children.Add(trustLogBtn);
            rightButtons.Children.Add(clearLog);
            Grid.SetColumn(rightButtons, 1);
            rightHeaderRow.Children.Add(rightHeader);
            rightHeaderRow.Children.Add(rightButtons);

            var rightHint = new TextBlock
            {
                Text = L.T("Wybierz wpis, aby zobaczyć pełny URL i szczegóły blokady."),
                Foreground = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)),
                Margin = new Thickness(0, 6, 0, 0)
            };

            var detailsCaption = new TextBlock { Text = L.T("Szczegóły zaznaczonego wpisu"), Margin = new Thickness(0, 10, 0, 2), FontWeight = FontWeights.SemiBold };
            var detailsPanel = new StackPanel();
            detailsPanel.Children.Add(detailsCaption);
            detailsPanel.Children.Add(details);

            Grid.SetRow(rightHeaderRow, 0);
            Grid.SetRow(rightHint, 1);
            Grid.SetRow(logList, 2);
            Grid.SetRow(detailsPanel, 3);
            right.Children.Add(rightHeaderRow);
            right.Children.Add(rightHint);
            right.Children.Add(logList);
            right.Children.Add(detailsPanel);

            var splitter = new GridSplitter
            {
                Width = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
                ShowsPreview = true
            };

            Grid.SetColumn(left, 0);
            Grid.SetColumn(right, 1);
            root.Children.Add(left);
            root.Children.Add(right);
            Grid.SetColumn(splitter, 0);
            root.Children.Add(splitter);

            win.Content = root;
            refresh();
            loadCurrentDomain();
            win.ShowDialog();
        }

        void UpdatePrivacyButton()
        {
            if (PrivacyBtn == null) return;
            var url = CurrentUrl;
            var r = url == null ? null : RuleForUrl(url);
            PrivacyBtn.Content = r == null ? L.T("Prywatność") : L.T("Prywatność*");
            PrivacyBtn.ToolTip = r == null
                ? L.T("Panel prywatności i antyfingerprinting")
                : L.T("Aktywna reguła prywatności dla tej domeny");
        }

        void SwitchProfile(string profile)
        {
            profile = NormalizeProfileName(profile);
            if (profile == SelectedProfileName) return;
            if (MessageBox.Show(this, L.T("Przełączyć profil na „") + profile + L.T("”?\nAplikacja uruchomi się ponownie z osobnym zestawem kart, historii i dodatków."), L.T("Profil"), MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;
            try
            {
                Directory.CreateDirectory(ProfileRoot);
                File.WriteAllText(ActiveProfileFile, profile);
                var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName) { UseShellExecute = false };
                psi.ArgumentList.Add("--profil");
                psi.ArgumentList.Add(profile);
                psi.ArgumentList.Add("--czekaj-na");
                psi.ArgumentList.Add(Environment.ProcessId.ToString());
                Process.Start(psi);
                Close();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, L.T("Profil")); }
        }

        void OpenProfilesManager()
        {
            var win = new Window
            {
                Title = L.T("Użytkownicy i profile Velivo"),
                Width = 560,
                Height = 460,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            var list = new ListBox { Margin = new Thickness(8) };
            var iconRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 8) };
            var iconLabel = new TextBlock { Text = L.T("Ikona profilu:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var iconPicker = new ComboBox { Width = 170, Height = 28 };
            foreach (var ic in ProfileIconChoices) iconPicker.Items.Add(ic);
            var iconSave = SmallButton(L.T("Zapisz ikonkę"), null);
            iconSave.IsEnabled = false;
            iconRow.Children.Add(iconLabel);
            iconRow.Children.Add(iconPicker);
            iconRow.Children.Add(iconSave);
            Action refresh = () =>
            {
                list.Items.Clear();
                foreach (var p in GetKnownProfiles())
                {
                    var mark = p == SelectedProfileName ? L.T(" (aktywny)") : "";
                    var icon = GetProfileIcon(p);
                    list.Items.Add(new ListBoxItem { Content = icon + "  " + p + mark, Tag = p });
                }
                UpdateProfileBadge();
            };

            void LoadIconSelectionForCurrent()
            {
                var it = list.SelectedItem as ListBoxItem;
                if (it == null)
                {
                    iconPicker.SelectedItem = null;
                    iconSave.IsEnabled = false;
                    return;
                }
                var p = (string)it.Tag;
                var icon = GetProfileIcon(p);
                iconPicker.SelectedItem = icon;
                iconSave.IsEnabled = true;
            }

            var add = SmallButton(L.T("Dodaj użytkownika/profil…"), () =>
            {
                var raw = Prompt(L.T("Nazwa użytkownika/profilu (np. google-konto2):"), "");
                if (raw == null) return;
                var profile = NormalizeProfileName(raw);
                if (profile.Length == 0) { MessageBox.Show(win, L.T("Niepoprawna nazwa."), L.T("Profile")); return; }
                try
                {
                    Directory.CreateDirectory(profile == "domyslny" ? ProfileRoot : Path.Combine(ProfilesDir, profile));
                    SetProfileIcon(profile, DefaultIconForProfile(profile));
                    var all = GetKnownProfiles(); if (!all.Contains(profile)) all.Add(profile); SaveKnownProfiles(all);
                    NotifyLanStateChanged();
                    refresh();
                }
                catch (Exception ex) { MessageBox.Show(win, ex.Message, L.T("Profile")); }
            });

            var use = SmallButton(L.T("Przełącz na zaznaczony"), () =>
            {
                var it = list.SelectedItem as ListBoxItem;
                if (it == null) return;
                SwitchProfile((string)it.Tag);
            });

            iconSave.Click += (s, e) =>
            {
                var it = list.SelectedItem as ListBoxItem;
                if (it == null || iconPicker.SelectedItem == null) return;
                var p = (string)it.Tag;
                SetProfileIcon(p, iconPicker.SelectedItem.ToString());
                refresh();
            };

            var del = SmallButton(L.T("Usuń zaznaczony profil"), () =>
            {
                var it = list.SelectedItem as ListBoxItem;
                if (it == null) return;
                var p = (string)it.Tag;
                if (p == "domyslny" || p == SelectedProfileName)
                {
                    MessageBox.Show(win, L.T("Nie można usunąć aktywnego ani domyślnego profilu."), L.T("Profile"));
                    return;
                }
                if (MessageBox.Show(win, L.T("Usunąć profil „") + p + L.T("” razem z jego lokalnymi danymi?"), L.T("Profile"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                try
                {
                    var dir = Path.Combine(ProfilesDir, p);
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    RemoveProfileIcon(p);
                    SaveKnownProfiles(GetKnownProfiles().Where(x => !string.Equals(x, p, StringComparison.OrdinalIgnoreCase)));
                    NotifyLanStateChanged();
                    refresh();
                }
                catch (Exception ex) { MessageBox.Show(win, ex.Message, L.T("Profile")); }
            });

            var note = new TextBlock
            {
                Text = L.T("Każdy profil ma własne ustawienia, sesje, historię, zakładki i dodatki. Sejf oraz Szybki Dostęp pozostają wspólne i nie są tutaj zmieniane."),
                Margin = new Thickness(8),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray
            };

            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
            bar.Children.Add(add);
            bar.Children.Add(use);
            bar.Children.Add(del);

            var dock = new DockPanel();
            DockPanel.SetDock(note, Dock.Top);
            DockPanel.SetDock(iconRow, Dock.Bottom);
            DockPanel.SetDock(bar, Dock.Bottom);
            dock.Children.Add(note);
            dock.Children.Add(iconRow);
            dock.Children.Add(bar);
            dock.Children.Add(list);
            win.Content = dock;

            list.SelectionChanged += (s, e) => LoadIconSelectionForCurrent();

            refresh();
            if (list.Items.Count > 0) list.SelectedIndex = 0;
            win.ShowDialog();
        }
    }
}
