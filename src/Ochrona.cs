using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Przegladarka
{
    // Ochrona przed kradzieza danych (obok SmartScreen):
    //  1. wykrywanie stron-podrobek (offline): marka w adresie na obcej domenie, literowki i podmienione znaki,
    //     adresy xn-- (puny code), podrobki stron, do ktorych masz zapisane hasla;
    //  2. najpierw HTTPS - strony bez szyfrowania tylko po ostrzezeniu;
    //  3. bezpieczne platnosci - na stronach bankow i platnosci okno jest niewidoczne dla programow nagrywajacych ekran.
    public partial class MainWindow
    {
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;

        // marka -> prawdziwe domeny (domena rejestrowana)
        static readonly Dictionary<string, string[]> ProtectedBrands = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "paypal", new[] { "paypal.com", "paypal.me", "paypalobjects.com" } },
            { "ebay", new[] { "ebay.com", "ebay.co.uk", "ebay.pl", "ebay.de", "ebay.fr", "ebay.it", "ebay.es", "ebayimg.com" } },
            { "amazon", new[] { "amazon.com", "amazon.co.uk", "amazon.pl", "amazon.de", "amazon.fr", "amazon.it", "amazon.es", "amazonpay.com", "media-amazon.com" } },
            { "facebook", new[] { "facebook.com", "fb.com", "fbcdn.net", "facebook.net" } },
            { "instagram", new[] { "instagram.com" } },
            { "whatsapp", new[] { "whatsapp.com", "whatsapp.net" } },
            { "google", new[] { "google.com", "google.co.uk", "google.pl", "googleusercontent.com", "googleapis.com", "youtube.com", "gmail.com" } },
            { "gmail", new[] { "gmail.com", "google.com" } },
            { "microsoft", new[] { "microsoft.com", "microsoftonline.com", "live.com", "outlook.com", "office.com", "office365.com", "msn.com", "bing.com", "skype.com", "sharepoint.com", "onedrive.com", "xbox.com" } },
            { "outlook", new[] { "outlook.com", "live.com", "office.com", "microsoft.com" } },
            { "apple", new[] { "apple.com", "icloud.com" } },
            { "icloud", new[] { "icloud.com", "apple.com" } },
            { "twitter", new[] { "twitter.com", "x.com", "twimg.com" } },
            { "netflix", new[] { "netflix.com" } },
            { "spotify", new[] { "spotify.com" } },
            { "allegro", new[] { "allegro.pl", "allegrolokalnie.pl" } },
            { "olx", new[] { "olx.pl", "olx.ua", "olx.ro", "olx.pt", "olx.bg" } },
            { "vinted", new[] { "vinted.pl", "vinted.co.uk", "vinted.com", "vinted.fr", "vinted.de" } },
            { "inpost", new[] { "inpost.pl", "inpost.co.uk", "inpost.eu" } },
            { "pkobp", new[] { "pkobp.pl", "ipko.pl" } },
            { "ipko", new[] { "ipko.pl", "pkobp.pl" } },
            { "mbank", new[] { "mbank.pl" } },
            { "santander", new[] { "santander.pl", "santander.co.uk", "santander.com" } },
            { "pekao", new[] { "pekao.com.pl", "pekao24.pl" } },
            { "millennium", new[] { "bankmillennium.pl" } },
            { "revolut", new[] { "revolut.com", "revolut.me" } },
            { "monzo", new[] { "monzo.com", "monzo.me" } },
            { "starling", new[] { "starlingbank.com" } },
            { "barclays", new[] { "barclays.co.uk", "barclays.com", "barclaycard.co.uk" } },
            { "hsbc", new[] { "hsbc.co.uk", "hsbc.com" } },
            { "lloyds", new[] { "lloydsbank.com", "lloydsbank.co.uk", "lloydsbankinggroup.com" } },
            { "natwest", new[] { "natwest.com" } },
            { "halifax", new[] { "halifax.co.uk", "halifax-online.co.uk" } },
            { "nationwide", new[] { "nationwide.co.uk" } },
            { "royalmail", new[] { "royalmail.com" } },
            { "evri", new[] { "evri.com" } },
            { "hmrc", new[] { "gov.uk" } },
            { "dvla", new[] { "gov.uk" } },
            { "binance", new[] { "binance.com" } },
            { "coinbase", new[] { "coinbase.com" } },
            { "bitdefender", new[] { "bitdefender.com", "bitdefender.co.uk" } },
        };

        // banki i platnosci: tu wlacza sie tryb "bezpieczne platnosci"
        static readonly string[] PaymentDomains =
        {
            "paypal.com", "pkobp.pl", "ipko.pl", "mbank.pl", "santander.pl", "santander.co.uk", "pekao.com.pl", "pekao24.pl", "bankmillennium.pl",
            "ing.pl", "aliorbank.pl", "credit-agricole.pl", "revolut.com", "monzo.com", "starlingbank.com", "barclays.co.uk", "barclaycard.co.uk",
            "hsbc.co.uk", "lloydsbank.co.uk", "lloydsbank.com", "natwest.com", "halifax-online.co.uk", "halifax.co.uk", "nationwide.co.uk",
            "tsb.co.uk", "rbs.co.uk", "bankofscotland.co.uk", "virginmoney.com", "wise.com", "stripe.com", "przelewy24.pl", "payu.com",
            "tpay.com", "blik.com", "klarna.com", "checkout.com", "worldpay.com", "sumup.com", "squareup.com"
        };

        static readonly string[] TwoLevelSuffixes =
        {
            "co.uk", "org.uk", "gov.uk", "ac.uk", "me.uk", "ltd.uk", "plc.uk", "com.pl", "net.pl", "org.pl", "gov.pl", "edu.pl",
            "com.au", "net.au", "co.nz", "co.jp", "com.br", "co.in", "com.tr", "com.ua", "co.za", "com.mx", "com.ar", "com.cn"
        };

        static string RegistrableDomain(string host)
        {
            host = (host ?? "").Trim().TrimEnd('.').ToLowerInvariant();
            var p = host.Split('.');
            if (p.Length <= 2) return host;
            var last2 = p[p.Length - 2] + "." + p[p.Length - 1];
            if (TwoLevelSuffixes.Contains(last2)) return p[p.Length - 3] + "." + last2;
            return last2;
        }

        static string SecondLevelLabel(string registrable)
        {
            var i = registrable.IndexOf('.');
            return i > 0 ? registrable.Substring(0, i) : registrable;
        }

        // 0->o, 1->l, rn->m, vv->w ... - typowe podmiany w podrobkach
        static string Deconfuse(string s)
        {
            s = s.ToLowerInvariant().Replace("rn", "m").Replace("vv", "w").Replace("cl", "d");
            var map = new Dictionary<char, char> { { '0', 'o' }, { '1', 'l' }, { '3', 'e' }, { '4', 'a' }, { '5', 's' }, { '7', 't' }, { '8', 'b' }, { '9', 'g' }, { 'i', 'l' } };
            return new string(s.Select(c => map.TryGetValue(c, out var r) ? r : c).ToArray());
        }

        static int EditDistance(string a, string b)
        {
            if (Math.Abs(a.Length - b.Length) > 2) return 99;
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                {
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            return d[a.Length, b.Length];
        }

        // domeny, do ktorych masz zapisane hasla - ich podrobki tez wykrywamy
        IEnumerable<string> VaultDomains()
        {
            try
            {
                EnsurePasswordVaultLoaded();
                return _passwordEntries.Select(e => RegistrableDomain(GetPasswordEntryHost(e))).Where(d => d.Contains('.')).Distinct().ToList();
            }
            catch (Exception) { return Enumerable.Empty<string>(); }
        }

        // null = strona wyglada w porzadku; inaczej: co podrabia
        internal static string CheckLookalike(string host, IEnumerable<string> extraDomains)
        {
            host = (host ?? "").ToLowerInvariant().TrimEnd('.');
            if (host.Length == 0 || Uri.CheckHostName(host) != UriHostNameType.Dns) return null;
            var reg = RegistrableDomain(host);
            var official = new HashSet<string>(ProtectedBrands.Values.SelectMany(x => x), StringComparer.OrdinalIgnoreCase);
            var extra = (extraDomains ?? Enumerable.Empty<string>()).ToList();
            if (official.Contains(reg) || extra.Contains(reg, StringComparer.OrdinalIgnoreCase)) return null;

            // 1. adres z obcymi znakami udajacymi litery (xn--)
            if (host.Split('.').Any(l => l.StartsWith("xn--", StringComparison.Ordinal)))
                return "xn";

            // 2. marka w adresie, ale domena nie nalezy do tej firmy (paypal-secure-login.com, ebay.co.uk.konto-weryfikacja.top)
            var tokens = host.Split('.', '-').Where(t => t.Length > 0).ToList();
            foreach (var kv in ProtectedBrands)
            {
                var brand = kv.Key;
                bool hit = tokens.Any(t => t == brand || (brand.Length >= 6 && t.StartsWith(brand, StringComparison.Ordinal)));
                if (hit) return kv.Value[0];
            }

            // 3. literowka lub podmieniony znak w nazwie znanej strony (paypa1.com, rnbank.pl, arnazon.co.uk)
            var label = SecondLevelLabel(reg);
            if (label.Length >= 4)
            {
                foreach (var d in official.Concat(extra))
                {
                    var other = SecondLevelLabel(d);
                    if (other.Length < 4 || other == label) continue;
                    if (Deconfuse(label) == Deconfuse(other)) return d;
                    if (other.Length >= 6 && label.Length >= 5 && EditDistance(label, other) == 1) return d;
                }
            }
            return null;
        }

        static string ProtectionAllowFile { get { return Path.Combine(DataDir, "ochrona-dozwolone.txt"); } }
        HashSet<string> _protectionAllowed;
        readonly HashSet<string> _httpAllowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _httpsTried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        HashSet<string> ProtectionAllowed
        {
            get
            {
                if (_protectionAllowed != null) return _protectionAllowed;
                _protectionAllowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try { if (File.Exists(ProtectionAllowFile)) foreach (var l in File.ReadAllLines(ProtectionAllowFile)) if (l.Trim().Length > 0) _protectionAllowed.Add(l.Trim()); }
                catch (Exception ex) { App.LogError(ex); }
                return _protectionAllowed;
            }
        }

        void AllowProtectedHost(string host)
        {
            ProtectionAllowed.Add(host);
            try { Directory.CreateDirectory(DataDir); File.WriteAllLines(ProtectionAllowFile, ProtectionAllowed); } catch (Exception ex) { App.LogError(ex); }
        }

        void HookProtection(BrowserTab tab, CoreWebView2 core)
        {
            if (tab == null || core == null) return;
            StartSafePayments();
            core.NavigationStarting += (s, e) =>
            {
                try
                {
                    if (_settings == null || !Uri.TryCreate(e.Uri, UriKind.Absolute, out var u)) return;
                    if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return;
                    if (AdBlocker.IsLocalNetworkUri(e.Uri) || IsTrustedUrl(e.Uri)) return;
                    var host = u.Host.ToLowerInvariant();

                    // podrobki
                    if (_settings.AntiPhishing && !ProtectionAllowed.Contains(host))
                    {
                        var fake = CheckLookalike(host, VaultDomains());
                        if (fake != null)
                        {
                            e.Cancel = true;
                            var target = e.Uri;
                            Dispatcher.BeginInvoke(new Action(() => WarnLookalike(core, host, fake, target)));
                            return;
                        }
                    }

                    // najpierw HTTPS
                    if (_settings.HttpsFirst && u.Scheme == Uri.UriSchemeHttp && !_httpAllowed.Contains(host))
                    {
                        e.Cancel = true;
                        _httpsTried.Add(host);
                        var b = new UriBuilder(u) { Scheme = Uri.UriSchemeHttps, Port = u.IsDefaultPort ? -1 : u.Port };
                        var https = b.Uri.ToString();
                        Dispatcher.BeginInvoke(new Action(() => { try { core.Navigate(https); } catch (Exception ex) { App.LogError(ex); } }));
                    }
                }
                catch (Exception ex) { App.LogError(ex); }
            };
            core.NavigationCompleted += (s, e) =>
            {
                try
                {
                    if (e.IsSuccess || _settings == null || !_settings.HttpsFirst) return;
                    if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return;
                    var host = u.Host.ToLowerInvariant();
                    if (!_httpsTried.Remove(host)) return;
                    // strona nie ma szyfrowania - tylko za zgoda
                    if (MessageBox.Show(this,
                            L.T("Ta strona nie obsługuje szyfrowania (HTTPS):") + "\n\n" + host + "\n\n" +
                            L.T("Wszystko, co tu wpiszesz, może zostać podejrzane po drodze. Nie wpisuj haseł, danych karty ani adresu.") + "\n\n" + L.T("Otworzyć mimo to?"),
                            L.T("Brak szyfrowania"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                    _httpAllowed.Add(host);
                    var b = new UriBuilder(u) { Scheme = Uri.UriSchemeHttp, Port = -1 };
                    core.Navigate(b.Uri.ToString());
                }
                catch (Exception ex) { App.LogError(ex); }
            };
        }

        void WarnLookalike(CoreWebView2 core, string host, string fake, string target)
        {
            string why = fake == "xn"
                ? L.T("Adres zawiera znaki z innych alfabetów, które udają zwykłe litery.")
                : L.T("Adres udaje stronę:") + " " + fake + "\n" + L.T("ale NIE należy do tej firmy.");
            var text = "⚠ " + L.T("Uwaga – to może być fałszywa strona!") + "\n\n" + host + "\n\n" + why + "\n\n" +
                       L.T("Oszuści podrabiają strony banków, sklepów i portali, żeby ukraść hasło, kartę lub pieniądze. Nie wpisuj tu żadnych danych.") +
                       "\n\n" + L.T("Otworzyć mimo to? (wybierz Nie, jeśli nie masz pewności)");
            if (MessageBox.Show(this, text, L.T("Ochrona przed oszustwem"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            AllowProtectedHost(host);
            try { core.Navigate(target); } catch (Exception ex) { App.LogError(ex); }
        }

        // ---------- bezpieczne platnosci ----------
        DispatcherTimer _safePayTimer;
        bool _safePayOn;

        static bool IsPaymentHost(string host)
        {
            var reg = RegistrableDomain(host);
            return PaymentDomains.Contains(reg, StringComparer.OrdinalIgnoreCase) || PaymentDomains.Contains(host, StringComparer.OrdinalIgnoreCase);
        }

        void StartSafePayments()
        {
            if (_safePayTimer != null) return;
            _safePayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _safePayTimer.Tick += (s, e) =>
            {
                try
                {
                    var src = _current?.View.CoreWebView2?.Source;
                    bool on = _settings != null && _settings.SafePayments && Uri.TryCreate(src ?? "", UriKind.Absolute, out var u)
                              && u.Scheme == Uri.UriSchemeHttps && IsPaymentHost(u.Host);
                    if (on == _safePayOn) return;
                    _safePayOn = on;
                    var h = new WindowInteropHelper(this).Handle;
                    if (h != IntPtr.Zero) SetWindowDisplayAffinity(h, on ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
                    if (on) ShowToast(L.T("🛡 Bezpieczne płatności: okno Velivo jest niewidoczne dla programów nagrywających ekran."), null);
                }
                catch (Exception ex) { App.LogError(ex); }
            };
            _safePayTimer.Start();
        }
    }
}
