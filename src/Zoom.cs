using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Przegladarka
{
    // Powiekszenie stron: domyslne dla wszystkich + zapamietane osobno dla kazdej strony (hosta).
    // Ctrl+kolko / Ctrl+plus / Ctrl+minus / Ctrl+0 obsluguje silnik - my tylko zapisujemy i przywracamy.
    public partial class MainWindow
    {
        readonly Dictionary<string, double> _zoomByHost = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        static string ZoomFile { get { return Path.Combine(DataDir, "powiekszenie.txt"); } }
        static readonly double[] ZoomSteps = { 0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0 };

        double DefaultZoom { get { return Math.Max(0.5, Math.Min(3.0, _settings.DefaultZoom / 100.0)); } }

        void LoadZoom()
        {
            try
            {
                if (!File.Exists(ZoomFile)) return;
                foreach (var line in File.ReadAllLines(ZoomFile))
                {
                    var p = line.Split('\t');
                    double z;
                    if (p.Length == 2 && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) _zoomByHost[p[0]] = z;
                }
            }
            catch (IOException) { }
        }

        void SaveZoom()
        {
            try { File.WriteAllLines(ZoomFile, _zoomByHost.Select(kv => kv.Key + "\t" + kv.Value.ToString(CultureInfo.InvariantCulture))); }
            catch (IOException) { }
        }

        static string HostOf(string url)
        {
            Uri u;
            return Uri.TryCreate(url, UriKind.Absolute, out u) && (u.Scheme == "http" || u.Scheme == "https") ? u.Host : null;
        }

        double ZoomFor(string url)
        {
            var host = HostOf(url);
            double z;
            return host != null && _zoomByHost.TryGetValue(host, out z) ? z : DefaultZoom;
        }

        // Ustawia powiekszenie karty wg strony, ktora jest w niej otwarta.
        void ApplyZoom(BrowserTab tab)
        {
            if (tab.View.CoreWebView2 == null) return;
            double want = ZoomFor(tab.View.CoreWebView2.Source);
            if (Math.Abs(tab.View.ZoomFactor - want) > 0.001)
            {
                tab.ApplyingZoom = true;
                tab.View.ZoomFactor = want;
            }
            if (tab == _current) UpdateZoomButton();
        }

        // Uzytkownik zmienil powiekszenie (Ctrl+kolko itp.) - zapamietaj dla tej strony.
        void OnZoomChanged(BrowserTab tab)
        {
            if (tab.ApplyingZoom) { tab.ApplyingZoom = false; if (tab == _current) UpdateZoomButton(); return; }
            var host = tab.View.CoreWebView2 != null ? HostOf(tab.View.CoreWebView2.Source) : null;
            if (host != null && !tab.Private)
            {
                if (Math.Abs(tab.View.ZoomFactor - DefaultZoom) < 0.001) _zoomByHost.Remove(host);
                else _zoomByHost[host] = Math.Round(tab.View.ZoomFactor, 3);
                SaveZoom();
            }
            // inne otwarte karty z ta sama strona dostaja to samo powiekszenie
            foreach (var t in _tabs) if (t != tab && t.View.CoreWebView2 != null && HostOf(t.View.CoreWebView2.Source) == host) ApplyZoom(t);
            if (tab == _current) UpdateZoomButton();
        }

        void SetZoom(double z)
        {
            if (_current == null) return;
            _current.View.ZoomFactor = Math.Max(0.25, Math.Min(5.0, z)); // zapis zrobi OnZoomChanged
        }

        void StepZoom(int dir)
        {
            if (_current == null) return;
            double z = _current.View.ZoomFactor;
            double next = dir > 0 ? ZoomSteps.FirstOrDefault(s => s > z + 0.001) : ZoomSteps.LastOrDefault(s => s < z - 0.001);
            if (next > 0) SetZoom(next);
        }

        void UpdateZoomButton()
        {
            if (_current == null) return;
            double z = _current.View.ZoomFactor;
            int pct = (int)Math.Round(z * 100);
            ZoomBtn.Content = pct + "%";
            ZoomBtn.Visibility = Visibility.Visible;   // zawsze widoczny - powiekszanie samym kolkiem myszy (np. przy telewizorze)
            ZoomBtn.ToolTip = L.En
                ? "Zoom for this page: " + pct + "%\nMouse wheel over this button: larger / smaller\nClick: restore default (" + _settings.DefaultZoom + "%)\nRight-click: larger / smaller\nCtrl + mouse wheel also works"
                : "Powiększenie tej strony: " + pct + "%\nKółko myszy nad tym przyciskiem: większe / mniejsze\nKliknij: przywróć domyślne (" + _settings.DefaultZoom + "%)\nPrawy klik: większe / mniejsze\nCtrl + kółko myszy także działa";
        }

        void ZoomBtn_Click(object sender, RoutedEventArgs e) { SetZoom(DefaultZoom); }

        void InitZoomMenu()
        {
            var menu = new ContextMenu();
            var plus = new MenuItem { Header = L.T("Powiększ (Ctrl +)") }; plus.Click += (s, e) => StepZoom(1);
            var minus = new MenuItem { Header = L.T("Pomniejsz (Ctrl −)") }; minus.Click += (s, e) => StepZoom(-1);
            var reset = new MenuItem { Header = L.T("Domyślne (Ctrl 0)") }; reset.Click += (s, e) => SetZoom(DefaultZoom);
            menu.Items.Add(plus); menu.Items.Add(minus); menu.Items.Add(reset);
            ZoomBtn.ContextMenu = menu;
            ZoomBtn.PreviewMouseWheel += (s, e) => { e.Handled = true; StepZoom(e.Delta > 0 ? 1 : -1); };
        }
    }
}
