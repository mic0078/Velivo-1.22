using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Velivo
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

        // domeny, do ktorych masz zapisane hasla - ich podrobki tez wykrywamy
        // liczone raz i odswiezane tylko, gdy zmieni sie liczba wpisow - sprawdzanie strony trwa ulamek milisekundy
        List<string> _vaultDomainsCache; int _vaultDomainsCount = -1;
        IEnumerable<string> VaultDomains()
        {
            try
            {
                EnsurePasswordVaultLoaded();
                if (_vaultDomainsCache == null || _vaultDomainsCount != _passwordEntries.Count)
                {
                    _vaultDomainsCache = _passwordEntries.Select(e => ProtRegDomain(GetPasswordEntryHost(e))).Where(d => d.Contains('.')).Distinct().ToList();
                    _vaultDomainsCount = _passwordEntries.Count;
                }
                return _vaultDomainsCache;
            }
            catch (Exception) { return Enumerable.Empty<string>(); }
        }
        static string ProtectionAllowFile { get { return Path.Combine(DataDir, "ochrona-dozwolone.txt"); } }
        HashSet<string> _protectionAllowed;
        readonly HashSet<string> _httpAllowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _httpsTried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // proba https w toku
        readonly Dictionary<ulong, string> _httpsNav = new Dictionary<ulong, string>();               // id nawigacji -> host
        readonly Dictionary<string, string> _httpOriginal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // host -> pierwotny adres http

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
                    if (_settings.HttpsFirst && u.Scheme == Uri.UriSchemeHttps && _httpsTried.Contains(host)) _httpsNav[e.NavigationId] = host;
                    if (_settings.HttpsFirst && u.Scheme == Uri.UriSchemeHttp && !_httpAllowed.Contains(host))
                    {
                        e.Cancel = true;
                        // https przekierowuje z powrotem na http - strona nie ma szyfrowania (bez petli)
                        if (_httpsTried.Contains(host) && e.IsRedirected)
                        {
                            _httpsTried.Remove(host);
                            var plain = e.Uri;
                            Dispatcher.BeginInvoke(new Action(() => AskOpenWithoutHttps(core, host, plain)));
                            return;
                        }
                        _httpsTried.Add(host);
                        _httpOriginal[host] = e.Uri;
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
                    // tylko nasza proba https (po id nawigacji) - nie anulowane przejscia ani inne strony
                    if (!_httpsNav.TryGetValue(e.NavigationId, out var host)) return;
                    _httpsNav.Remove(e.NavigationId);
                    _httpsTried.Remove(host);
                    if (e.IsSuccess || _settings == null || !_settings.HttpsFirst) return;
                    // brak internetu albo strona nie istnieje - to nie jest brak szyfrowania
                    if (e.WebErrorStatus == CoreWebView2WebErrorStatus.Disconnected || e.WebErrorStatus == CoreWebView2WebErrorStatus.HostNameNotResolved ||
                        e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled || e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted) return;
                    if (!_httpOriginal.TryGetValue(host, out var plain)) plain = "http://" + host + "/";
                    Dispatcher.BeginInvoke(new Action(() => AskOpenWithoutHttps(core, host, plain)));
                }
                catch (Exception ex) { App.LogError(ex); }
            };
        }

        // strona nie ma szyfrowania - otwieramy tylko za zgoda (okno poza zdarzeniem silnika)
        void AskOpenWithoutHttps(CoreWebView2 core, string host, string httpUrl)
        {
            if (MessageBox.Show(this,
                    L.T("Ta strona nie obsługuje szyfrowania (HTTPS):") + "\n\n" + host + "\n\n" +
                    L.T("Wszystko, co tu wpiszesz, może zostać podejrzane po drodze. Nie wpisuj haseł, danych karty ani adresu.") + "\n\n" + L.T("Otworzyć mimo to?"),
                    L.T("Brak szyfrowania"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            _httpAllowed.Add(host);
            try { core.Navigate(httpUrl); } catch (Exception ex) { App.LogError(ex); }
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
            var reg = ProtRegDomain(host);
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
                              && u.Scheme == Uri.UriSchemeHttps && IsPaymentHost(u.Host)
                              && !(_current != null && _current.Bank);   // tryb bankowy: bez ukrywania okna (banki chronia sie same, a powodowalo przycinanie)
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
