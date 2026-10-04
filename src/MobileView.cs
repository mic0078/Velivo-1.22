using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace Przegladarka
{
    // Wersja telefonu dla wybranych stron: Velivo przedstawia sie stronie jako telefon z Androidem,
    // a strona sama wysyla swoja wersje mobilna. Zapamietywane osobno dla kazdej strony (jak powiekszenie).
    public partial class MainWindow
    {
        static string MobileSitesFile { get { return Path.Combine(DataDir, "widok-telefonu.txt"); } }
        HashSet<string> _mobileSites;

        HashSet<string> MobileSites
        {
            get
            {
                if (_mobileSites == null)
                {
                    _mobileSites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try { if (File.Exists(MobileSitesFile)) foreach (var l in File.ReadAllLines(MobileSitesFile)) if (l.Trim().Length > 0) _mobileSites.Add(l.Trim()); }
                    catch (IOException) { }
                }
                return _mobileSites;
            }
        }

        static string SiteOf(string url)
        {
            Uri u;
            return Uri.TryCreate(url ?? "", UriKind.Absolute, out u) && (u.Scheme == "http" || u.Scheme == "https") ? RegistrableDomain(u.Host) : null;
        }

        bool IsMobileSite(string url) { var s = SiteOf(url); return s != null && MobileSites.Contains(s); }

        static string MobileUserAgent(string desktop)
        {
            var m = Regex.Match(desktop ?? "", @"Chrome/([\d\.]+)");
            var ver = m.Success ? m.Groups[1].Value : "130.0.0.0";
            return "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/" + ver + " Mobile Safari/537.36";
        }

        // Przy kazdej nawigacji: identyfikacja zgodna ze strona docelowa.
        void ApplyMobileMode(BrowserTab tab, CoreWebView2 core, string url)
        {
            try
            {
                if (tab.DesktopUA == null) tab.DesktopUA = core.Settings.UserAgent;
                bool mobile = IsMobileSite(url);
                tab.Mobile = mobile;
                var want = mobile ? MobileUserAgent(tab.DesktopUA) : tab.DesktopUA;
                if (core.Settings.UserAgent != want) core.Settings.UserAgent = want;
            }
            catch (Exception) { }
        }

        // Naglowki zapytan karty w trybie telefonu (takze "client hints", ktore sprawdza np. Google).
        void ApplyMobileHeaders(BrowserTab tab, CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (!tab.Mobile || tab.DesktopUA == null) return;
            try
            {
                var h = e.Request.Headers;
                h.SetHeader("User-Agent", MobileUserAgent(tab.DesktopUA));
                if (h.Contains("Sec-CH-UA-Mobile")) h.SetHeader("Sec-CH-UA-Mobile", "?1");
                if (h.Contains("Sec-CH-UA-Platform")) h.SetHeader("Sec-CH-UA-Platform", "\"Android\"");
            }
            catch (Exception) { }
        }

        void SetMobileSite(BrowserTab tab, string url, bool mobile)
        {
            var site = SiteOf(url);
            if (site == null) return;
            if (mobile) MobileSites.Add(site); else MobileSites.Remove(site);
            try { Directory.CreateDirectory(DataDir); File.WriteAllLines(MobileSitesFile, MobileSites.OrderBy(x => x)); } catch (IOException) { }
            // wszystkie otwarte karty z ta strona przeladowujemy w nowej wersji
            foreach (var t in _tabs.ToList())
            {
                var core = t.View.CoreWebView2;
                if (core == null || SiteOf(core.Source) != site) continue;
                ApplyMobileMode(t, core, core.Source);
                core.Reload();
            }
            ShowToast(mobile
                ? (L.En ? "📱 Phone version for " + site + " – remembered for this site." : "📱 Wersja telefonu dla " + site + " – zapamiętane dla tej strony.")
                : (L.En ? "🖥 Desktop version for " + site + "." : "🖥 Wersja komputerowa dla " + site + "."), null);
        }
    }
}
