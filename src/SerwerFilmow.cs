using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Velivo
{
    // 🎞 Velivo Serwer filmów: biblioteka na tym komputerze jako serwer dla moich komputerów (jak Jellyfin, bez Jellyfin):
    // foldery z filmami przeglądane co 10 min (nowe pliki - do biblioteki), informacje o nowych filmach same (Wikidane i Wikipedia, najpierw po polsku),
    // cała biblioteka widoczna na sparowanych komputerach z opisem, obsadą i plakatem (LanVideo). Działa w tle przy zegarze.
    public partial class MainWindow
    {
        static List<string> SerwerFoldery() => (GetPref("serwer-foldery") ?? "").Split('|').Where(x => x.Length > 0).ToList();
        System.Windows.Threading.DispatcherTimer _serwerTm; bool _serwerSkan;
        // co serwer pokazuje innym: tylko filmy z jego folderów (nie cała biblioteka komputera); filmy z Jellyfin - tylko gdy włączone
        static bool WFolderze(string p) => SerwerFoldery().Any(f => p.StartsWith(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        bool SerwerUdostepnia(LibraryVideo v) => File.Exists(v.Path) ? WFolderze(v.Path) : GetPref("serwer-jellyfin") == "1" && JellyfinStreamUrl(v.Path) != null;

        void SerwerStart()
        {
            if (!Wariant.Serwer || _serwerTm != null) return;
            _serwerTm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
            _serwerTm.Tick += (s, e) => SerwerSkanuj(false);
            _serwerTm.Start();
            Dispatcher.BeginInvoke(new Action(() => SerwerSkanuj(false)), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // nowe pliki z folderów -> biblioteka (wspólna), potem informacje o filmach bez opisu
        async void SerwerSkanuj(bool pokaz)
        {
            if (_serwerSkan) { if (pokaz) ShowToast(L.T("🎞 Serwer już przegląda foldery…"), null); return; }
            _serwerSkan = true;
            try
            {
                var fol = SerwerFoldery().Where(Directory.Exists).ToList();
                int nowe = 0;
                if (fol.Count > 0) { var l = LoadLibrary(); nowe = await Task.Run(() => AddToLibrary(l, fol, true)); if (nowe > 0) { SaveLibrary(l); _libRefresh?.Invoke(); } }
                var bez = LoadLibrary().Where(v => File.Exists(v.Path) && WFolderze(v.Path) && LookupMeta(Meta, v.Path) == null && !(DateTime.TryParse(GetPref("serwer-brak|" + MetaKey(v.Path)), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var bb) && DateTime.Now - bb < TimeSpan.FromDays(7))).ToList();   // nieznaleziony - ponownie po tygodniu
                int opis = 0;
                foreach (var v in bez)
                {
                    try { if (await SerwerOpis(v)) opis++; else SetPref("serwer-brak|" + MetaKey(v.Path), DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture)); }   // nie znaleziono - nie co 10 min od nowa
                    catch (Exception ex) { App.LogInfo("Serwer filmów – informacje o „" + v.Title + "”: " + ex.Message); break; }   // brak internetu - spróbujemy przy następnym przeglądzie
                    await Task.Delay(1000);   // Wikimedia - grzecznie, po jednym
                }
                if (opis > 0) _libRefresh?.Invoke();
                if (pokaz || nowe > 0 || opis > 0) ShowToast(L.T("🎞 Serwer filmów: nowe filmy ") + nowe + L.T(", uzupełnione informacje ") + opis + (bez.Count > opis ? L.T(", bez informacji ") + (bez.Count - opis) : ""), null);
            }
            catch (Exception ex) { App.LogError(ex); }
            finally { _serwerSkan = false; }
        }

        // pierwszy pasujący film z Wikidanych (rok z nazwy pliku - pewniejszy wybór); zapis jak „📝 Uzupełnij informacje”
        async Task<bool> SerwerOpis(LibraryVideo v)
        {
            var t = CleanTitle(v.Title ?? Path.GetFileNameWithoutExtension(v.Path));
            var ym = System.Text.RegularExpressions.Regex.Match(t, @"^(.+?)[ .(]+((?:19|20)\d{2})\)?");
            string q = ym.Success ? ym.Groups[1].Value.Replace('.', ' ').Trim() : t, rok = ym.Success ? ym.Groups[2].Value : null;
            string id = null;
            using (var http = WikiHttp())
                foreach (var lang in new[] { "pl", "en" })
                {
                    var js = await http.GetStringAsync("https://www.wikidata.org/w/api.php?action=wbsearchentities&format=json&type=item&limit=15&language=" + lang + "&uselang=pl&search=" + Uri.EscapeDataString(q));
                    var r = JsonDocument.Parse(js).RootElement.GetProperty("search").EnumerateArray()
                        .Select(x => (Id: WdStr(x, "id"), D: WdStr(x, "description") ?? "")).Where(x => x.Id != null && x.D.IndexOf("film", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                    if (rok == null) id = r.FirstOrDefault().Id;
                    else
                    {   // rok z nazwy pliku: w krótkim opisie albo w dacie premiery (P577) - tylko pasujący rok, bez zgadywania
                        id = r.FirstOrDefault(x => x.D.Contains(rok)).Id;
                        var ids = r.Take(6).Select(x => x.Id).ToList();
                        if (id == null && ids.Count > 0)
                        {
                            var ent = JsonDocument.Parse(await http.GetStringAsync("https://www.wikidata.org/w/api.php?action=wbgetentities&format=json&props=claims&ids=" + string.Join("|", ids))).RootElement.GetProperty("entities");
                            id = ids.FirstOrDefault(x => ent.TryGetProperty(x, out var e) && e.TryGetProperty("claims", out var c) && c.TryGetProperty("P577", out var pd)
                                && pd.EnumerateArray().Any(d => (WdStr(d, "mainsnak", "datavalue", "value", "time") ?? "").Substring(Math.Min(1, (WdStr(d, "mainsnak", "datavalue", "value", "time") ?? "").Length)).StartsWith(rok)));
                        }
                    }
                    if (id != null) break;
                }
            if (id == null) return false;
            var m = await WikiFilm(id, v.Path, null);
            var key = MetaKey(v.Path); var man = LoadManual(); man[key] = m;
            Directory.CreateDirectory(MetaDir); File.WriteAllText(ManualFile, JsonSerializer.Serialize(man));
            Meta[key] = m;
            return true;
        }

        // 🎞 okno serwera (wygląd z makiety): ciemne kolory Velivo, karty, stan na górze, foldery po lewej, filmy serwera po prawej
        static readonly SolidColorBrush SBg = Fz(0x14, 0x18, 0x21), SCard = Fz(0x1B, 0x21, 0x30), SBtn = Fz(0x23, 0x2A, 0x37), SFg = Fz(0xE8, 0xEB, 0xF0), SDim = Fz(0x9A, 0xA3, 0xB2),
            SLine = Fz(0x2A, 0x32, 0x40), SField = Fz(0x0F, 0x13, 0x1B), SAccent = Fz(0x6A, 0x3B, 0xE0), SOk = Fz(0x86, 0xEF, 0xAC), SOkBg = Fz(0x14, 0x53, 0x2D), SWarn = Fz(0xFC, 0xA5, 0xA5);
        static SolidColorBrush Fz(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
        static TextBlock STxt(string t, double size = 13, Brush c = null, bool bold = false) => new TextBlock { Text = t, FontSize = size, Foreground = c ?? SFg, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        static Button SBtnMake(string t, Action a, Brush bg = null)
        {
            var b = new Button { Content = t, Template = KomBtnTemplate, Background = bg ?? SBtn, Foreground = SFg, BorderBrush = SLine, BorderThickness = new Thickness(0), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 6, 6), Cursor = System.Windows.Input.Cursors.Hand };
            b.Click += (s, e) => a(); return b;
        }
        static Border SCardMake(UIElement c, Thickness? m = null) => new Border { Background = SCard, CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Margin = m ?? new Thickness(0, 0, 0, 6), Child = c };
        static ImageSource _serwerIco;
        internal static ImageSource SerwerIkona() { try { return _serwerIco ??= new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/serwer.ico")); } catch (Exception) { return null; } }

        Window _serwerWin;
        internal void SerwerOkno()
        {
            if (_serwerWin != null) { if (_serwerWin.WindowState == WindowState.Minimized) _serwerWin.WindowState = WindowState.Normal; _serwerWin.Activate(); return; }
            var wa = SystemParameters.WorkArea;
            var w = _serwerWin = new Window { Title = L.T("🎞 Velivo Serwer filmów"), Width = Math.Min(1100, wa.Width - 40), Height = Math.Min(720, wa.Height - 40), WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = SBg, Foreground = SFg };
            Independent(w); w.Uid = "own-theme"; var ik = SerwerIkona(); if (ik != null) w.Icon = ik;
            w.SourceInitialized += (s, e) => { try { int on = 1; DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(w).Handle, 20, ref on, 4); } catch (Exception) { } };   // ciemny pasek tytułu
            w.Closed += (s, e) => _serwerWin = null;
            var root = new DockPanel { Margin = new Thickness(18) };
            // góra: nazwa, stan, kto widzi
            var head = new DockPanel(); DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
            var nazwa = STxt(L.T("🎞 Velivo Serwer filmów"), 19, null, true);
            var dziala = new Border { Background = SOkBg, CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 2, 9, 2), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = STxt(L.T("● działa"), 12, SOk) };
            var kto = STxt("", 12.5, SDim); kto.TextAlignment = TextAlignment.Right; kto.Margin = new Thickness(16, 0, 0, 0);
            DockPanel.SetDock(nazwa, Dock.Left); DockPanel.SetDock(dziala, Dock.Left); head.Children.Add(nazwa); head.Children.Add(dziala); head.Children.Add(kto);
            var opis = STxt(L.T("Twoje komputery z Velivo (Odtwarzacz albo pełne Velivo) widzą filmy z folderów serwera w swojej bibliotece – z opisem, obsadą i plakatem – i oglądają je prosto z tego komputera. Inne filmy z biblioteki tego komputera nie są udostępniane."), 12, SDim);
            opis.Margin = new Thickness(0, 6, 0, 14); DockPanel.SetDock(opis, Dock.Top); root.Children.Add(opis);
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
            root.Children.Add(grid);
            // lewa: foldery (karty do zaznaczenia) i ustawienia
            var left = new StackPanel(); grid.Children.Add(left);
            left.Children.Add(STxt(L.T("📁 Foldery serwera"), 14.5, null, true));
            var fol = new StackPanel { Margin = new Thickness(0, 8, 0, 4) }; left.Children.Add(fol);
            var fRow = new WrapPanel(); left.Children.Add(fRow);
            string wybrany = null;
            var ust = STxt(L.T("Ustawienia"), 14.5, null, true); ust.Margin = new Thickness(0, 18, 0, 6); left.Children.Add(ust);
            var auto = new CheckBox { Content = STxt(L.T("Uruchamiaj z Windows"), 13), IsChecked = ReadAutostart() >= 0, Foreground = SFg, Margin = new Thickness(0, 2, 0, 4) };
            auto.Click += (s, e) => WriteAutostart(auto.IsChecked == true, 30);
            var jf = new CheckBox { Content = STxt(L.T("Udostępniaj też filmy z Jellyfin"), 13), IsChecked = GetPref("serwer-jellyfin") == "1", Foreground = SFg };
            jf.Click += (s, e) => { SetPref("serwer-jellyfin", jf.IsChecked == true ? "1" : "0"); _sharedCache = null; };
            left.Children.Add(auto); left.Children.Add(jf);
            // prawa: filmy serwera
            var right = new DockPanel(); Grid.SetColumn(right, 2); grid.Children.Add(right);
            var rHead = STxt("", 14.5, null, true); DockPanel.SetDock(rHead, Dock.Top); right.Children.Add(rHead);
            var skan = SBtnMake(L.T("🔄 Przejrzyj foldery i uzupełnij informacje"), () => SerwerSkanuj(true), SAccent); skan.HorizontalAlignment = HorizontalAlignment.Left; skan.Margin = new Thickness(0, 10, 0, 0);
            DockPanel.SetDock(skan, Dock.Bottom); right.Children.Add(skan);
            var lista = new StackPanel(); right.Children.Add(new ScrollViewer { Content = lista, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0) });
            string sig = null;
            Action fill = null;
            fill = () =>
            {
                var foldery = SerwerFoldery();
                var filmy = LoadLibrary().Where(v => File.Exists(v.Path) && WFolderze(v.Path)).OrderBy(v => ShownTitle(v, MetaFor(v.Path)), StringComparer.CurrentCultureIgnoreCase).ToList();
                var teraz = SendTargets().Select(p => p.Device).ToList();
                kto.Text = Environment.MachineName + L.T(" · udostępnia filmów: ") + filmy.Count + " · " + (teraz.Count == 0 ? L.T("w sieci teraz: żaden Twój komputer z Velivo") : L.T("w sieci teraz: ") + string.Join(", ", teraz));
                kto.ToolTip = teraz.Count == 0 ? L.T("Włącz Velivo na innym komputerze; musi być sparowany i w tym samym profilu.") : null;
                var nowy = string.Join("|", foldery) + "#" + wybrany + "#" + string.Join("|", filmy.Select(v => { var m0 = LookupMeta(Meta, v.Path); return v.Path + "?" + m0?.Poster + "?" + (m0 != null); }));
                if (nowy == sig) return; sig = nowy;   // przebudowa tylko przy zmianie
                fol.Children.Clear();
                if (foldery.Count == 0) fol.Children.Add(SCardMake(STxt(L.T("Brak folderów – kliknij „➕ Dodaj folder”."), 12.5, SDim)));
                foreach (var f in foldery)
                {
                    var ff = f; int n = filmy.Count(v => v.Path.StartsWith(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
                    var sp = new StackPanel(); sp.Children.Add(STxt(f, 13)); sp.Children.Add(STxt(Directory.Exists(f) ? L.T("filmów: ") + n + L.T(" · przeglądany co 10 min") : L.T("niedostępny"), 11.5, Directory.Exists(f) ? SDim : SWarn));
                    var card = SCardMake(sp); card.BorderThickness = new Thickness(1); card.BorderBrush = ff == wybrany ? SAccent : SLine; card.Cursor = System.Windows.Input.Cursors.Hand;
                    card.MouseLeftButtonUp += (o, e) => { wybrany = ff == wybrany ? null : ff; fill(); };
                    fol.Children.Add(card);
                }
                rHead.Text = L.T("Filmy na serwerze") + " (" + filmy.Count + ")";
                lista.Children.Clear();
                if (foldery.Count == 0) lista.Children.Add(STxt(L.T("Dodaj folder z filmami (po lewej) – serwer przejrzy go i pobierze informacje o filmach."), 12.5, SDim));
                foreach (var v in filmy)
                {
                    var mi = LookupMeta(Meta, v.Path); var vv = v;
                    var row = new DockPanel();
                    var pf = mi?.Poster != null ? Path.Combine(PosterDir, mi.Poster) : null; bool jestPl = pf != null && File.Exists(pf);
                    var img = new Border { Width = 50, Height = 74, CornerRadius = new CornerRadius(4), Background = SLine, Margin = new Thickness(0, 0, 12, 0), ClipToBounds = true,
                        Child = jestPl ? new Image { Source = LoadLogo(pf, 148), Stretch = Stretch.UniformToFill } : null };
                    DockPanel.SetDock(img, Dock.Left); row.Children.Add(img);
                    var btns = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(btns, Dock.Right);
                    btns.Children.Add(SBtnMake(L.T("🔎 Informacje"), () => ShowMetaSearch(w, vv.Path, ShownTitle(vv, MetaFor(vv.Path)), () => { sig = null; fill(); })));
                    btns.Children.Add(SBtnMake(L.T("▶ Odtwórz"), () => PlayVideos(new[] { vv.Path }, 0)));
                    row.Children.Add(btns);
                    var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    var tl = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
                    tl.Inlines.Add(new System.Windows.Documents.Run(mi?.Name ?? ShownTitle(v, mi)) { FontWeight = FontWeights.SemiBold, FontSize = 14.5, Foreground = SFg });
                    if (mi?.Year > 0) tl.Inlines.Add(new System.Windows.Documents.Run(" (" + mi.Year + ")") { Foreground = SDim, FontSize = 14 });
                    if (mi?.Rating > 0) tl.Inlines.Add(new System.Windows.Documents.Run(" · ★ " + mi.Rating.ToString("0.0")) { Foreground = SDim, FontSize = 14 });
                    txt.Children.Add(tl);
                    if (mi != null && (mi.Genres?.Count > 0 || !string.IsNullOrEmpty(mi.Director))) { var g = STxt(string.Join(", ", mi.Genres ?? new List<string>()) + (string.IsNullOrEmpty(mi.Director) ? "" : L.T(" · reż. ") + mi.Director), 12.5, SDim); g.TextWrapping = TextWrapping.NoWrap; g.TextTrimming = TextTrimming.CharacterEllipsis; txt.Children.Add(g); }
                    if (mi == null) txt.Children.Add(STxt(L.T("⚠ brak informacji – kliknij „🔎 Informacje”"), 12.5, SWarn));
                    else
                    {   // co jest, a czego brakuje - zamiast „brak” przy częściowych danych
                        var jest = new List<string>(); var brak = new List<string>();
                        (string.IsNullOrWhiteSpace(mi.Overview) ? brak : jest).Add(L.T("opis")); (mi.People?.Count > 0 ? jest : brak).Add(L.T("obsada")); (jestPl ? jest : brak).Add(L.T("plakat"));
                        txt.Children.Add(STxt((jest.Count > 0 ? "✔ " + string.Join(", ", jest) : "") + (brak.Count > 0 ? (jest.Count > 0 ? "   " : "") + L.T("⚠ brak: ") + string.Join(", ", brak) : ""), 12.5, brak.Count == 0 ? SOk : SWarn));
                    }
                    row.Children.Add(txt);
                    lista.Children.Add(SCardMake(row));
                }
            };
            fRow.Children.Add(SBtnMake(L.T("➕ Dodaj folder…"), () =>
            {
                var d = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Folder z filmami") };
                if (d.ShowDialog(w) != true) return;
                var l = SerwerFoldery(); if (!l.Contains(d.FolderName, StringComparer.OrdinalIgnoreCase)) l.Add(d.FolderName);
                SetPref("serwer-foldery", string.Join("|", l)); _sharedCache = null; fill(); SerwerSkanuj(true);
            }));
            fRow.Children.Add(SBtnMake(L.T("➖ Usuń zaznaczony"), () =>
            {
                if (wybrany == null) { ShowToast(L.T("Kliknij najpierw folder na liście"), null); return; }
                var l = SerwerFoldery().Where(x => !string.Equals(x, wybrany, StringComparison.OrdinalIgnoreCase)).ToList(); wybrany = null;
                SetPref("serwer-foldery", string.Join("|", l)); _sharedCache = null; fill();   // filmy zostają w bibliotece komputera - przestają być udostępniane
            }));
            var tm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            tm.Tick += (s, e) => { if (w.IsVisible && w.WindowState != WindowState.Minimized) fill(); };
            w.Closed += (s, e) => tm.Stop();
            w.Content = root; fill(); tm.Start(); w.Show(); w.Activate();
        }
    }
}
