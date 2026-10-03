using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Przegladarka
{
    // Przywracanie kart po ponownym uruchomieniu + ponowne otwieranie zamknietych kart (Ctrl+Shift+T).
    // Karty prywatne nie sa nigdzie zapisywane. Przy "czysc dane przy zamknieciu" sesja tez nie jest zapisywana.
    public partial class MainWindow
    {
        static string SessionFile { get { return Path.Combine(DataDir, "sesja.txt"); } }
        readonly Stack<string> _closedTabs = new Stack<string>();
        DispatcherTimer _sessionTimer;
        bool _sessionLoaded; // nie zapisuj, zanim sesja nie zostanie przywrocona

        static string PinnedFile { get { return Path.Combine(DataDir, "karty-przypiete.txt"); } }

        List<string> LoadPinnedTabs()
        {
            try { if (File.Exists(PinnedFile)) return File.ReadAllLines(PinnedFile).Where(Restorable).Take(30).ToList(); }
            catch (IOException) { }
            return new List<string>();
        }

        void SavePinnedTabs()
        {
            try
            {
                var urls = _tabs.Where(t => t.Pinned).Select(t => t.PinnedUrl).Where(Restorable).ToList();
                if (urls.Count == 0) { if (File.Exists(PinnedFile)) File.Delete(PinnedFile); }
                else File.WriteAllLines(PinnedFile, urls);
            }
            catch (IOException) { }
        }

        // Przypiecie: karta przechodzi na poczatek paska (za inne przypiete), bez krzyzyka, wraca po ponownym uruchomieniu.
        void SetTabPinned(BrowserTab tab, bool pinned)
        {
            if (tab.Private) return;
            tab.Pinned = pinned;
            tab.PinnedUrl = pinned ? (tab.View.CoreWebView2 != null && Restorable(tab.View.CoreWebView2.Source) ? tab.View.CoreWebView2.Source : tab.StartUrl) : null;
            tab.PinMark.Visibility = pinned ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            tab.CloseBtn.Visibility = pinned ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            tab.Title.MaxWidth = pinned ? 90 : 160;
            int target = _tabs.Count(t => t.Pinned && t != tab);
            if (!pinned) target = _tabs.Count(t => t.Pinned);
            _tabs.Remove(tab);
            target = Math.Min(target, _tabs.Count);
            _tabs.Insert(target, tab);
            TabStrip.Children.Remove(tab.Header);
            var before = target + 1 < _tabs.Count ? (System.Windows.UIElement)_tabs[target + 1].Header : NewTabBtn;
            TabStrip.Children.Insert(TabStrip.Children.IndexOf(before), tab.Header);
            if (_sessionLoaded) { SavePinnedTabs(); SaveSessionSoon(); }
        }

        // ta sama strona = ta sama domena glowna (www.x.pl i m.x.pl to jedna strona)
        static bool SameSite(string a, string b)
        {
            Uri ua, ub;
            if (!Uri.TryCreate(a ?? "", UriKind.Absolute, out ua) || !Uri.TryCreate(b ?? "", UriKind.Absolute, out ub)) return true;
            Func<string, string> root = h => { var p = h.ToLowerInvariant().Split('.'); return p.Length >= 2 ? p[p.Length - 2] + "." + p[p.Length - 1] : h; };
            return root(ua.Host) == root(ub.Host);
        }

        static bool Restorable(string url)
        {
            return !string.IsNullOrEmpty(url) && !url.StartsWith("about:") && !url.StartsWith("edge:") && !url.StartsWith("data:");
        }

        // Adresy do otwarcia przy starcie (pusta lista = brak zapisanej sesji).
        List<string> LoadSession()
        {
            var list = new List<string>();
            bool afterRestart = File.Exists(RestartFlag); // ponowne uruchomienie przez Velivo - karty wracaja zawsze
            if (afterRestart) try { File.Delete(RestartFlag); } catch (IOException) { }
            if (!afterRestart && (!_settings.RestoreTabs || _settings.ClearOnExit)) return list;
            try { if (File.Exists(SessionFile)) list = File.ReadAllLines(SessionFile).Where(Restorable).Take(50).ToList(); }
            catch (IOException) { }
            return list;
        }

        int LoadSessionActive()
        {
            try { int n; return File.Exists(SessionFile + ".aktywna") && int.TryParse(File.ReadAllText(SessionFile + ".aktywna"), out n) ? n : 0; }
            catch (IOException) { return 0; }
        }

        void SaveSessionSoon()
        {
            if (!_sessionLoaded) return;
            if (_sessionTimer == null)
            {
                _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _sessionTimer.Tick += (s, e) => { _sessionTimer.Stop(); SaveSession(); };
            }
            _sessionTimer.Stop(); _sessionTimer.Start();
        }

        void SaveSession()
        {
            if (!_sessionLoaded) return;
            SavePinnedTabs();
            try
            {
                if ((!_settings.RestoreTabs || _settings.ClearOnExit) && !File.Exists(RestartFlag))
                {
                    if (File.Exists(SessionFile)) File.Delete(SessionFile);
                    return;
                }
                var normal = _tabs.Where(t => !t.Private && !t.Pinned).ToList();
                var urls = normal.Select(t => t.View.CoreWebView2 != null ? t.View.CoreWebView2.Source : t.StartUrl).Where(Restorable).ToList();
                File.WriteAllLines(SessionFile, urls);
                File.WriteAllText(SessionFile + ".aktywna", Math.Max(0, normal.IndexOf(_current)).ToString());
                NotifyLanStateChanged();
            }
            catch (IOException) { }
        }

        void RememberClosed(BrowserTab tab)
        {
            if (tab.Private) return;
            var url = tab.View.CoreWebView2 != null ? tab.View.CoreWebView2.Source : tab.StartUrl;
            if (!Restorable(url)) return;
            _closedTabs.Push(url);
            if (_closedTabs.Count > 25) { var keep = _closedTabs.Take(25).Reverse().ToList(); _closedTabs.Clear(); foreach (var u in keep) _closedTabs.Push(u); }
        }

        void ReopenClosedTab()
        {
            if (_closedTabs.Count > 0) AddTab(_closedTabs.Pop());
        }

        // Prawy klik na karcie.
        ContextMenu BuildTabMenu(BrowserTab tab)
        {
            var menu = new ContextMenu();
            var reload = new MenuItem { Header = L.T("Odśwież") }; reload.Click += (s, e) => { if (tab.View.CoreWebView2 != null) tab.View.CoreWebView2.Reload(); };
            var dup = new MenuItem { Header = L.T("Duplikuj kartę") }; dup.Click += (s, e) => { if (tab.View.CoreWebView2 != null) AddTab(tab.View.CoreWebView2.Source, tab.Private); };
            var close = new MenuItem { Header = L.T("Zamknij kartę (Ctrl+W)") }; close.Click += (s, e) => CloseTab(tab);
            var others = new MenuItem { Header = L.T("Zamknij inne karty") };
            others.Click += (s, e) => { foreach (var t in _tabs.Where(x => x != tab && !x.Pinned).ToList()) CloseTab(t); SelectTab(tab); };
            var right = new MenuItem { Header = L.T("Zamknij karty po prawej") };
            right.Click += (s, e) => { int i = _tabs.IndexOf(tab); foreach (var t in _tabs.Skip(i + 1).Where(x => !x.Pinned).ToList()) CloseTab(t); };
            var pin = new MenuItem(); pin.Click += (s, e) => SetTabPinned(tab, !tab.Pinned);
            var reopen = new MenuItem { Header = L.T("Przywróć zamkniętą kartę (Ctrl+Shift+T)") }; reopen.Click += (s, e) => ReopenClosedTab();
            menu.Opened += (s, e) =>
            {
                reopen.IsEnabled = _closedTabs.Count > 0; others.IsEnabled = _tabs.Count > 1; right.IsEnabled = _tabs.IndexOf(tab) < _tabs.Count - 1;
                pin.Header = tab.Pinned ? L.T("Odepnij kartę") : L.T("Przypnij kartę");
                pin.IsEnabled = !tab.Private;
            };
            foreach (var m in new object[] { reload, dup, pin, new Separator(), close, others, right, new Separator(), reopen }) menu.Items.Add(m);
            return menu;
        }
    }
}
