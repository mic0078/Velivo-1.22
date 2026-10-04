using System;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace Przegladarka
{
    // Menu prawego przycisku: "Wyszukaj ..." dla zaznaczonego tekstu (jak w Chrome)
    // oraz "Przejdz do ..." gdy zaznaczenie wyglada jak adres strony.
    public partial class MainWindow
    {
        static readonly Regex LooksLikeAddress = new Regex(@"^(https?://)?[\w-]+(\.[\w-]+)+(:\d+)?(/\S*)?$", RegexOptions.IgnoreCase);

        string SearchEngineName
        {
            get
            {
                string[] e;
                if (!AppSettings.Engines.TryGetValue(_settings.Search, out e)) e = AppSettings.Engines["duckduckgo"];
                int p = e[0].IndexOf('(');
                return (p > 0 ? e[0].Substring(0, p) : e[0]).Trim();
            }
        }

        // Bezposredni adres strony przetlumaczonej przez Tlumacza Google (jak w Chrome):
        // en.wikipedia.org/wiki/X -> en-wikipedia-org.translate.goog/wiki/X?_x_tr_sl=auto&_x_tr_tl=pl...
        // (myslnik w nazwie hosta zapisuje sie podwojnie, kropka jako myslnik)
        static string TranslatedPageUrl(string url, string to = "pl")
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return "https://translate.google.com/translate?sl=auto&tl=" + to + "&u=" + Uri.EscapeDataString(url);
            string host = u.IdnHost.Replace("-", "--").Replace(".", "-") + ".translate.goog";
            string port = u.IsDefaultPort ? "" : ":" + u.Port;
            string q = u.Query.TrimStart('?');
            string tr = "_x_tr_sl=auto&_x_tr_tl=" + to + "&_x_tr_hl=pl&_x_tr_pto=wapp" + (u.Scheme == "http" ? "&_x_tr_sch=http" : "");
            return "https://" + host + port + u.AbsolutePath + "?" + (q.Length > 0 ? q + "&" : "") + tr + u.Fragment;
        }

        // "&" w etykiecie menu oznacza skrot klawiszowy - podwajamy, zeby byl widoczny jako znak
        static string MenuLabel(string s) { return s.Replace("&", "&&"); }

        // Menu prawego przycisku: wyszukiwanie, tlumaczenie na polski (Tlumacz Google) i zrzut ekranu.
        async void AddSearchToContextMenu(CoreWebView2ContextMenuRequestedEventArgs e, bool isPrivate)
        {
            var target = e.ContextMenuTarget;
            var core = Core;
            // jezyk strony sprawdzamy w trakcie otwierania menu - stad odroczenie
            var deferral = e.GetDeferral();
            try
            {
                string lang = "";
                try
                {
                    if (core != null)
                        lang = (await core.ExecuteScriptAsync("(document.documentElement.lang || (document.querySelector('meta[http-equiv=\"content-language\" i]') || {}).content || '').toLowerCase()")).Trim('"');
                }
                catch (Exception) { }
                string pageUrl = target.PageUri ?? "";
                bool web = pageUrl.StartsWith("http://") || pageUrl.StartsWith("https://");
                bool alreadyTranslated = pageUrl.Contains(".translate.goog") || pageUrl.Contains("translate.google.");
                // strona po polsku -> bez tlumaczenia; brak informacji o jezyku -> oferujemy (lepiej zbednie niz wcale)
                bool foreignPage = web && !alreadyTranslated && !lang.StartsWith("pl");

                int pos = 0;
                Action<CoreWebView2ContextMenuItem> add = item => e.MenuItems.Insert(pos++, item);
                Action separator = () => add(_env.CreateContextMenuItem("", null, CoreWebView2ContextMenuItemKind.Separator));

                if (target.HasSelection)
                {
                    string text = Regex.Replace(target.SelectionText ?? "", @"\s+", " ").Trim();
                    if (text.Length > 0)
                    {
                        string shortText = text.Length > 32 ? text.Substring(0, 32).TrimEnd() + "…" : text;
                        var search = _env.CreateContextMenuItem(MenuLabel(L.En ? "Search “" + shortText + "” on " + SearchEngineName : "Wyszukaj „" + shortText + "” w " + SearchEngineName), null, CoreWebView2ContextMenuItemKind.Command);
                        search.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(() => AddTab(_settings.SearchUrl(text), isPrivate));
                        add(search);
                        if (text.Length <= 200 && text.IndexOf(' ') < 0 && LooksLikeAddress.IsMatch(text))
                        {
                            var go = _env.CreateContextMenuItem(MenuLabel((L.En ? "Go to " : "Przejdź do ") + shortText), null, CoreWebView2ContextMenuItemKind.Command);
                            go.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(() => AddTab(ToUrl(text), isPrivate));
                            add(go);
                        }
                        var tr = _env.CreateContextMenuItem(L.T("Przetłumacz zaznaczenie na polski"), null, CoreWebView2ContextMenuItemKind.Command);
                        var q = text.Length > 4500 ? text.Substring(0, 4500) : text;
                        tr.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(() =>
                            AddTab("https://translate.google.com/?sl=auto&tl=pl&op=translate&text=" + Uri.EscapeDataString(q), isPrivate));
                        add(tr);
                        var readSel = _env.CreateContextMenuItem(L.T("Czytaj zaznaczenie na głos"), null, CoreWebView2ContextMenuItemKind.Command);
                        readSel.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(() => StartReading(true));
                        add(readSel);
                        separator();
                    }
                }

                // Tlumaczenie strony zawsze pod reka: obca -> na polski, polska -> na angielski,
                // nieznany jezyk -> obie opcje (wczesniej opcja znikala, gdy strona nie podala jezyka albo podala bledny).
                if (web && !alreadyTranslated)
                {
                    bool polish = lang.StartsWith("pl");
                    bool unknown = lang.Length == 0;
                    Action<string, string> addTranslate = (label, to) =>
                    {
                        var trPage = _env.CreateContextMenuItem(label, null, CoreWebView2ContextMenuItemKind.Command);
                        trPage.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(() =>
                        {
                            var c = Core;
                            if (c != null) c.Navigate(TranslatedPageUrl(pageUrl, to));
                        });
                        add(trPage);
                    };
                    if (!polish || unknown) addTranslate(L.T("Przetłumacz stronę na polski"), "pl");
                    if (polish || unknown) addTranslate(L.T("Przetłumacz stronę na angielski"), "en");
                }

                if (web)
                {
                    var tabForBlock = _current;
                    double px = e.Location.X, py = e.Location.Y;
                    var block = _env.CreateContextMenuItem(L.T("🚫 Blokuj element (reklamę)…"), null, CoreWebView2ContextMenuItemKind.Command);
                    block.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(async () => await StartElementPicker(tabForBlock, px, py));
                    add(block);
                    if (ElementSelectorsFor(pageUrl).Count > 0)
                    {
                        var unblock = _env.CreateContextMenuItem(L.T("Przywróć zablokowane elementy na tej stronie"), null, CoreWebView2ContextMenuItemKind.Command);
                        unblock.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(() => ClearElementRules(tabForBlock));
                        add(unblock);
                    }
                    if (_settings.AutoRejectCookies)
                    {
                        bool rejecting = CookieRejectOn(pageUrl);
                        var ck = _env.CreateContextMenuItem(rejecting ? L.T("🍪 Nie odrzucaj banerów ciasteczek na tej stronie") : L.T("🍪 Odrzucaj banery ciasteczek na tej stronie"), null, CoreWebView2ContextMenuItemKind.Command);
                        ck.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(() => SetCookieException(pageUrl, rejecting));
                        add(ck);
                    }
                    if (target.Kind == CoreWebView2ContextMenuTargetKind.Video)
                    {
                        var pip = _env.CreateContextMenuItem(L.T("⧉ Obraz w obrazie"), null, CoreWebView2ContextMenuItemKind.Command);
                        pip.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(async () => await StartPictureInPicture(tabForBlock));
                        add(pip);
                    }
                    separator();
                    var reader = _env.CreateContextMenuItem(L.T("Tryb czytania i streszczenie"), null, CoreWebView2ContextMenuItemKind.Command);
                    reader.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(OpenReaderMode);
                    add(reader);
                    var readPage = _env.CreateContextMenuItem(_readTab != null ? L.T("Zatrzymaj czytanie") : L.T("Czytaj stronę na głos (Ctrl+Shift+U)"), null, CoreWebView2ContextMenuItemKind.Command);
                    readPage.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(() => { if (_readTab != null) StopReading(); else StartReading(false); });
                    add(readPage);
                    if (!target.HasSelection)
                    {
                        var tabForRead = _current;
                        double rx = e.Location.X, ry = e.Location.Y;
                        var readHere = _env.CreateContextMenuItem(L.T("🔊 Czytaj od tego miejsca"), null, CoreWebView2ContextMenuItemKind.Command);
                        readHere.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(() => StartReadingAt(tabForRead, rx, ry));
                        add(readHere);
                    }
                    var memSearch = _env.CreateContextMenuItem(L.T("🧠 Gdzie ja to czytałem?"), null, CoreWebView2ContextMenuItemKind.Command);
                    memSearch.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(ShowPageMemorySearch);
                    add(memSearch);
                }
                var shots = _env.CreateContextMenuItem(L.T("Zrzut ekranu"), null, CoreWebView2ContextMenuItemKind.Submenu);
                var visible = _env.CreateContextMenuItem(L.T("Widoczna część strony"), null, CoreWebView2ContextMenuItemKind.Command);
                visible.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(async () => await TakeScreenshot(false));
                var full = _env.CreateContextMenuItem(L.T("Cała strona (z przewijaniem)"), null, CoreWebView2ContextMenuItemKind.Command);
                full.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(async () => await TakeScreenshot(true));
                shots.Children.Add(visible); shots.Children.Add(full);
                add(shots);

                var tools = _env.CreateContextMenuItem(L.T("Narzędzia Velivo"), null, CoreWebView2ContextMenuItemKind.Submenu);
                var tPrivacy = _env.CreateContextMenuItem(L.T("Prywatność i antyfingerprinting"), null, CoreWebView2ContextMenuItemKind.Command);
                tPrivacy.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(OpenPrivacyPanel);
                var tMedia = _env.CreateContextMenuItem(L.T("Wykryj media do pobrania"), null, CoreWebView2ContextMenuItemKind.Command);
                tMedia.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(DetectPageMedia);
                var tDl = _env.CreateContextMenuItem(L.T("Menedżer pobrań"), null, CoreWebView2ContextMenuItemKind.Command);
                tDl.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(() => Downloads_Click(null, null));
                var tHist = _env.CreateContextMenuItem(L.T("Historia"), null, CoreWebView2ContextMenuItemKind.Command);
                tHist.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(() => History_Click(null, null));
                var tExt = _env.CreateContextMenuItem(L.T("Dodatki"), null, CoreWebView2ContextMenuItemKind.Command);
                tExt.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(() => Extensions_Click(null, null));
                var tLan = _env.CreateContextMenuItem(L.T("Diagnostyka LAN sync"), null, CoreWebView2ContextMenuItemKind.Command);
                tLan.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(OpenLanDiagnosticsPanel);
                var tProfileWork = _env.CreateContextMenuItem(L.T("Przełącz profil: praca"), null, CoreWebView2ContextMenuItemKind.Command);
                tProfileWork.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(() => SwitchProfile("praca"));
                var tUser = _env.CreateContextMenuItem(L.T("Przełącz użytkownika/profil…"), null, CoreWebView2ContextMenuItemKind.Command);
                tUser.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(OpenProfilesManager);
                var tabForTools = _current;
                var tPip = _env.CreateContextMenuItem(L.T("⧉ Obraz w obrazie"), null, CoreWebView2ContextMenuItemKind.Command);
                tPip.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(async () => await StartPictureInPicture(tabForTools));
                var tTabs = _env.CreateContextMenuItem(L.T("Szukaj w kartach (Ctrl+Shift+A)"), null, CoreWebView2ContextMenuItemKind.Command);
                tTabs.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(ShowTabSearch);
                tools.Children.Add(tPip);
                tools.Children.Add(tTabs);
                var tMem = _env.CreateContextMenuItem(L.T("🧠 Gdzie ja to czytałem? (Ctrl+Shift+F)"), null, CoreWebView2ContextMenuItemKind.Command);
                tMem.CustomItemSelected += (a, b) => Dispatcher.InvokeAsync(ShowPageMemorySearch);
                tools.Children.Add(tMem);
                tools.Children.Add(tPrivacy);
                tools.Children.Add(tMedia);
                tools.Children.Add(tDl);
                tools.Children.Add(tHist);
                tools.Children.Add(tExt);
                tools.Children.Add(tLan);
                tools.Children.Add(tProfileWork);
                tools.Children.Add(tUser);
                add(tools);
                separator();
            }
            catch (Exception ex) { App.LogError(ex); }
            finally { deferral.Complete(); }
        }
    }
}
