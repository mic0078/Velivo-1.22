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
        // 🧩 foldery serwera wspólne dla programów Velivo na tym komputerze (Odtwarzacz prowadzący sieć udostępnia filmy Serwera) - z pliku, odświeżane co 10 s
        static List<string> _serwerFol; static DateTime _serwerFolAt;
        static List<string> SerwerFolderyTeraz() { if (Wariant.Serwer) return SerwerFoldery(); if (_serwerFol == null || (DateTime.UtcNow - _serwerFolAt).TotalSeconds > 10) { _serwerFol = SerwerFolderyZPliku(); _serwerFolAt = DateTime.UtcNow; } return _serwerFol; }
        // foldery serwera prosto z pliku ustawień (okna.txt) - Odtwarzacz na tym samym komputerze widzi zmiany Serwera bez ponownego uruchomienia
        static string PrefZPliku(string k)
        {
            try { foreach (var l in File.ReadAllLines(WindowsFile)) if (l.StartsWith("ust:" + k + "\t")) return l.Substring(k.Length + 5); } catch (Exception) { return GetPref(k); }
            return null;
        }
        static List<string> SerwerFolderyZPliku() => (PrefZPliku("serwer-foldery") ?? "").Split('|').Where(x => x.Length > 0).ToList();
        // 🎞 film należy do serwera: plik z folderu serwera albo (gdy włączone w oknie serwera) film z Jellyfin
        static bool _serwerJf; static DateTime _serwerJfAt;
        bool NaSerwerze(LibraryVideo v)
        {
            if (File.Exists(v.Path)) return WFolderze(v.Path);
            if (Wariant.Serwer) _serwerJf = GetPref("serwer-jellyfin") == "1"; else if ((DateTime.UtcNow - _serwerJfAt).TotalSeconds > 10) { _serwerJf = PrefZPliku("serwer-jellyfin") == "1"; _serwerJfAt = DateTime.UtcNow; }
            return _serwerJf && JellyfinStreamUrl(v.Path) != null;
        }
        System.Windows.Threading.DispatcherTimer _serwerTm; bool _serwerSkan;
        // co serwer pokazuje innym: tylko filmy z jego folderów (nie cała biblioteka komputera); filmy z Jellyfin - tylko gdy włączone
        static bool WFolderze(string p) => SerwerFolderyTeraz().Any(f => p.StartsWith(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        bool SerwerUdostepnia(LibraryVideo v) => NaSerwerze(v);

        void SerwerStart()
        {
            if (!Wariant.Serwer || _serwerTm != null) return;
            WymusCiemne = true;   // okna serwera w ciemnym wyglądzie Velivo
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
                if (fol.Count > 0) { var l = LoadLibrary(); var pom = SerwerPominiete(); nowe = await Task.Run(() => AddToLibrary(l, fol, true)); nowe -= l.RemoveAll(v => pom.Contains(v.Path)); if (nowe > 0) { SaveLibrary(l); _libRefresh?.Invoke(); } }   // usunięte z serwera nie wracają przy przeglądaniu
                var bez = LoadLibrary().GroupBy(v => v.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).Where(v => File.Exists(v.Path) && WFolderze(v.Path) && LookupMeta(Meta, v.Path) == null && !(DateTime.TryParse(GetPref("serwer-brak|" + MetaKey(v.Path)), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var bb) && DateTime.Now - bb < TimeSpan.FromDays(7))).ToList();   // nieznaleziony - ponownie po tygodniu
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
            var wyn = await SzukajFilmu(q, rok);
            // z rokiem w nazwie pliku - tylko wynik z tym rokiem (TMDb: rok produkcji, Wikidane: opis albo data premiery); bez roku - pierwszy
            string N(string x) => new string((x ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            FilmWynik w = rok == null ? wyn.FirstOrDefault(x => N(x.Label) == N(q)) :   // bez roku w nazwie - tylko ten sam tytuł (bez zgadywania)
                wyn.FirstOrDefault(x => x.Year.ToString() == rok || x.WdId != null && (x.Desc ?? "").Contains(rok));
            if (w == null && rok != null)
                using (var http = WikiHttp())
                {
                    var ids = wyn.Where(x => x.WdId != null).Take(6).Select(x => x.WdId).ToList();
                    if (ids.Count > 0)
                    {
                        var ent = JsonDocument.Parse(await http.GetStringAsync("https://www.wikidata.org/w/api.php?action=wbgetentities&format=json&props=claims&ids=" + string.Join("|", ids))).RootElement.GetProperty("entities");
                        var id = ids.FirstOrDefault(x => ent.TryGetProperty(x, out var e) && e.TryGetProperty("claims", out var c) && c.TryGetProperty("P577", out var pd)
                            && pd.EnumerateArray().Any(d => { var t = WdStr(d, "mainsnak", "datavalue", "value", "time") ?? ""; return t.Length > 5 && t.Substring(1, 4) == rok; }));
                        w = wyn.FirstOrDefault(x => x.WdId == id && id != null);
                    }
                }
            if (w == null) return false;
            var m = await PobierzFilm(w, v.Path, null);
            var key = MetaKey(v.Path); var man = LoadManual(); man[key] = m;
            Directory.CreateDirectory(MetaDir); File.WriteAllText(ManualFile, JsonSerializer.Serialize(man));
            Meta[key] = m; MetaObokFilmu(v.Path, true);   // 📁 też obok filmu
            return true;
        }

        // 🎞 okno serwera jak makieta: po lewej foldery i ustawienia, po prawej filmy serwera z plakatem i stanem informacji
        static readonly SolidColorBrush SBg = Fz(0x14, 0x18, 0x21), SCard = Fz(0x1B, 0x21, 0x30), SBtn = Fz(0x23, 0x2A, 0x37), SFg = Fz(0xE8, 0xEB, 0xF0), SDim = Fz(0x9A, 0xA3, 0xB2),
            SLine = Fz(0x2A, 0x32, 0x40), SField = Fz(0x0F, 0x13, 0x1B), SAccent = Fz(0x6A, 0x3B, 0xE0), SOk = Fz(0x86, 0xEF, 0xAC), SOkBg = Fz(0x14, 0x53, 0x2D), SWarn = Fz(0xFC, 0xA5, 0xA5);
        static SolidColorBrush Fz(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
        static TextBlock STxt(string t, double size = 13, Brush c = null, bool bold = false) => new TextBlock { Text = t, FontSize = size, Foreground = c ?? SFg, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        // ikony Windows (Segoe Fluent) zamiast emoji - ostre, w kolorze tekstu (WPF nie ma kolorowych emoji)
        static readonly Dictionary<string, string> SIkony = new Dictionary<string, string> { ["🔄"] = "\uE72C", ["🔎"] = "\uE721", ["▶"] = "\uE768", ["➕"] = "\uE710", ["➖"] = "\uE738", ["🎞"] = "\uE8B2", ["📁"] = "\uE8B7" };
        static readonly FontFamily SIkonyFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), STekstFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
        static UIElement SZIkona(string t, double size, Brush c, bool bold)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            int k = t.IndexOf(' ');
            if (k > 0 && SIkony.TryGetValue(t.Substring(0, k), out var g)) { sp.Children.Add(new TextBlock { Text = g, FontFamily = SIkonyFont, FontSize = size, Foreground = c, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0) }); t = t.Substring(k + 1); }
            sp.Children.Add(new TextBlock { Text = t, FontSize = size, Foreground = c, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }
        static Button SBtnMake(string t, Action a, Brush bg = null)
        {
            var b = new Button { Content = SZIkona(t, 13, SFg, false), Template = KomBtnTemplate, Background = bg ?? SBtn, Foreground = SFg, BorderBrush = SLine, BorderThickness = new Thickness(0), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 6, 6), Cursor = System.Windows.Input.Cursors.Hand };
            b.Click += (s, e) => a(); return b;
        }
        static Border SCardMake(UIElement c, Thickness? m = null) => new Border { Background = SCard, CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Margin = m ?? new Thickness(0, 0, 0, 6), Child = c };
        static ImageSource _serwerIco;
        internal static ImageSource SerwerIkona() { try { return _serwerIco ??= new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/serwer.ico")); } catch (Exception) { return null; } }

        // 🎬 szczegóły filmu jak w Jellyfin (serwer i Kanapa - ta sama funkcja): duży plakat, opis, obsada, ▶ Odtwórz / ⏯ Wznów / ⏮ Od początku i dodatkowe czynności
        //    graj("wznow" | "poczatek" | null) - odtwarzanie wybrane przez wołającego (plik, film z serwera)
        internal void FilmSzczegoly(Window owner, string tytul, MovieInfo m, ImageSource plakat, long len, long pos, bool done, Action<string> graj, List<(string Txt, Action Act)> wiecej = null)
        {
            var w = new Window { Title = tytul, Width = 940, Height = 600, WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen, Owner = owner, Topmost = owner?.Topmost ?? false,
                Background = SBg, Foreground = SFg, FontFamily = STekstFont, ShowInTaskbar = false, ResizeMode = ResizeMode.CanResize };
            w.SourceInitialized += (s, e) => { try { int on = 1; DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(w).Handle, 20, ref on, 4); } catch (Exception) { } };   // ciemny pasek tytułu
            w.Uid = "own-theme";   // własne ciemne kolory - motyw Velivo nie przemalowuje tła na białe (biały tekst na białym w Kanapie)
            w.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) w.Close(); };
            var g = new Grid { Margin = new Thickness(22) }; g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) }); g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = SLine, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Top, Height = 480,
                Child = plakat != null ? (UIElement)new Image { Source = plakat, Stretch = Stretch.UniformToFill } : new TextBlock { Text = "🎬", FontSize = 80, Foreground = SDim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
            var d = new DockPanel(); Grid.SetColumn(d, 2); g.Children.Add(d);
            var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); d.Children.Add(top);
            top.Children.Add(STxt(m?.Name ?? tytul, 26, null, true));
            if (!string.IsNullOrEmpty(m?.Original) && m.Original != m.Name) { var o = STxt(m.Original, 14, SDim); o.FontStyle = FontStyles.Italic; top.Children.Add(o); }
            var meta = new[] { m?.Year > 0 ? m.Year.ToString() : null, m?.Rating > 0 ? "★ " + m.Rating.ToString("0.0") : null, len > 0 ? (len / 60000) + L.T(" min") : null, m?.Age,
                m?.Genres?.Count > 0 ? string.Join(", ", m.Genres) : null }.Where(x => !string.IsNullOrEmpty(x));
            var mt = STxt(string.Join("   ·   ", meta), 14, SDim); mt.Margin = new Thickness(0, 8, 0, 0); top.Children.Add(mt);
            if (!string.IsNullOrEmpty(m?.Director)) { var r0 = STxt(L.T("Reżyseria: ") + m.Director, 13.5, SDim); r0.Margin = new Thickness(0, 6, 0, 0); top.Children.Add(r0); }
            if (m?.People?.Count > 0) { var p0 = STxt(L.T("Obsada: ") + string.Join(", ", m.People.Take(12)), 13.5, SDim); p0.Margin = new Thickness(0, 4, 0, 0); top.Children.Add(p0); }
            var btn = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) }; top.Children.Add(btn);
            Action<string> start = how => { w.Close(); graj(how); };
            bool wznow = pos > 0 && !done && len > 0;
            var b0 = SBtnMake(wznow ? L.T("▶ Wznów od ") + TimeSpan.FromMilliseconds(pos).ToString(pos >= 3600000 ? @"h\:mm\:ss" : @"m\:ss") : L.T("▶ Odtwórz"), () => start(wznow ? "wznow" : null), SAccent); btn.Children.Add(b0);
            if (wznow) btn.Children.Add(SBtnMake(L.T("⏮ Od początku"), () => start("poczatek")));
            foreach (var x in wiecej ?? new List<(string, Action)>()) { var xx = x; btn.Children.Add(SBtnMake(xx.Txt, () => { w.Close(); xx.Act(); })); }
            if (wznow) { var pr = new ProgressBar { Height = 5, Maximum = len, Value = pos, Margin = new Thickness(0, 4, 0, 0), Foreground = SAccent, Background = SLine, BorderThickness = new Thickness(0) }; top.Children.Add(pr); }
            var ov = STxt(string.IsNullOrWhiteSpace(m?.Overview) ? L.T("Brak opisu – „🔎 Wyszukaj informacje” pobierze opis, obsadę i plakat.") : m.Overview, 15, string.IsNullOrWhiteSpace(m?.Overview) ? SDim : SFg);
            ov.VerticalAlignment = VerticalAlignment.Top; ov.LineHeight = 22;
            d.Children.Add(new ScrollViewer { Content = ov, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 18, 0, 0) });
            w.Content = g; w.Loaded += (s, e) => b0.Focus(); w.Show(); w.Activate();
        }

        static bool WPrzycisku(DependencyObject d) { for (; d != null; d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d)) if (d is Button) return true; return false; }
        // ➖ usunięte z serwera (plik zostaje) - nie wraca przy przeglądaniu folderów
        static HashSet<string> SerwerPominiete() => new HashSet<string>((GetPref("serwer-pominiete") ?? "").Split('|').Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);
        void SerwerUsun(Window owner, string path, bool dysk)
        {
            var nazwa = Path.GetFileName(path);
            if (MessageBox.Show(owner, dysk ? L.T("Usunąć film z serwera i z dysku (do Kosza)?") + "\n\n" + nazwa : L.T("Usunąć film z serwera? Plik zostaje na dysku, serwer przestaje go udostępniać.") + "\n\n" + nazwa,
                Wariant.Nazwa, MessageBoxButton.YesNo, dysk ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try
            {
                if (dysk && File.Exists(path)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                if (!dysk) { var p = SerwerPominiete(); p.Add(path); SetPref("serwer-pominiete", string.Join("|", p)); }
                var l = LoadLibrary(); if (l.RemoveAll(v => string.Equals(v.Path, path, StringComparison.OrdinalIgnoreCase)) > 0) SaveLibrary(l);
                _sharedCache = null; _libRefresh?.Invoke();
                ShowToast((dysk ? L.T("🗑 Usunięto z serwera i z dysku: ") : L.T("➖ Usunięto z serwera: ")) + nazwa, null);
            }
            catch (Exception ex) { App.LogError(ex); ShowToast(L.T("Nie udało się usunąć: ") + ex.Message, null); }
        }
        // szczegóły filmu serwera (z czynnościami zarządzania)
        void SerwerFilm(Window owner, LibraryVideo v, Action po)
        {
            var m = LookupMeta(Meta, v.Path); var pf = m?.Poster != null ? Path.Combine(PosterDir, m.Poster) : null;
            FilmSzczegoly(owner, ShownTitle(v, m), m, pf != null && File.Exists(pf) ? LoadLogo(pf, 640) : null, v.Len, v.Pos, v.Done, how => { _pilotResume = how; PlayVideos(new[] { v.Path }, 0); },
                new List<(string, Action)> {
                    (L.T("🔎 Wyszukaj informacje"), () => ShowMetaSearch(owner, v.Path, ShownTitle(v, MetaFor(v.Path)), po)),
                    (L.T("📂 Pokaż plik"), () => { try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + v.Path + "\""); } catch (Exception) { } }),
                    (L.T("➖ Usuń z serwera"), () => { SerwerUsun(owner, v.Path, false); po(); }),
                    (L.T("🗑 Usuń z dysku"), () => { SerwerUsun(owner, v.Path, true); po(); }) });
        }

        Window _serwerWin;
        internal void SerwerOkno()
        {
            if (_serwerWin != null) { if (_serwerWin.WindowState == WindowState.Minimized) _serwerWin.WindowState = WindowState.Normal; _serwerWin.Activate(); return; }
            var wa = SystemParameters.WorkArea;
            var w = _serwerWin = new Window { Title = Wariant.Nazwa, Width = Math.Min(1100, wa.Width - 40), Height = Math.Min(720, wa.Height - 40), WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = SBg, Foreground = SFg, FontFamily = STekstFont };
            Independent(w); w.Uid = "own-theme"; var ik = SerwerIkona(); if (ik != null) w.Icon = ik;
            w.SourceInitialized += (s, e) => { try { int on = 1; DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(w).Handle, 20, ref on, 4); } catch (Exception) { } };   // ciemny pasek tytułu
            w.Closed += (s, e) => _serwerWin = null;
            var root = new DockPanel { Margin = new Thickness(18) };
            // góra: nazwa, stan, kto widzi
            var head = new DockPanel(); DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
            var nazwa = SZIkona(L.T("🎞 Velivo Serwer filmów"), 20, SFg, true);
            var dziala = new Border { Background = SOkBg, CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 2, 9, 2), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = STxt(L.T("● działa"), 12, SOk) };
            var kto = STxt("", 12.5, SDim); kto.TextAlignment = TextAlignment.Right; kto.Margin = new Thickness(16, 0, 0, 0);
            DockPanel.SetDock(nazwa, Dock.Left); DockPanel.SetDock(dziala, Dock.Left); head.Children.Add(nazwa); head.Children.Add(dziala); head.Children.Add(kto);
            var opis = STxt(L.T("Twoje komputery z Velivo (Odtwarzacz albo pełne Velivo) widzą filmy z folderów serwera w swojej bibliotece – z opisem, obsadą i plakatem – i oglądają je prosto z tego komputera. Inne filmy z biblioteki tego komputera nie są udostępniane."), 12, SDim);
            opis.Margin = new Thickness(0, 6, 0, 14); DockPanel.SetDock(opis, Dock.Top); root.Children.Add(opis);
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
            root.Children.Add(grid);
            // lewa: foldery (karty do zaznaczenia) i ustawienia
            var left = new StackPanel(); grid.Children.Add(left);
            left.Children.Add(SZIkona(L.T("📁 Foldery serwera"), 14.5, SFg, true));
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
            var szukaj = new TextBox { Background = SField, Foreground = SFg, CaretBrush = SFg, BorderBrush = SLine, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 8, 0, 0), ToolTip = L.T("Szukaj filmu na serwerze (tytuł)") };
            DockPanel.SetDock(szukaj, Dock.Top); right.Children.Add(szukaj);
            Action fill = null; string sig = null;
            szukaj.TextChanged += (s, e) => fill?.Invoke();
            var dol = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(dol, Dock.Bottom); right.Children.Add(dol);
            dol.Children.Add(SBtnMake(L.T("🔄 Przejrzyj foldery i uzupełnij informacje"), () => SerwerSkanuj(true), SAccent));
            dol.Children.Add(SBtnMake(L.T("🔃 Odśwież listę"), () => { sig = null; fill(); }));
            dol.Children.Add(SBtnMake(L.T("🧹 Usuń brakujące pliki"), () =>
            {   // wpisy filmów, których pliku już nie ma w folderach serwera (przeniesione, usunięte poza Velivo)
                var l = LoadLibrary(); int n = l.RemoveAll(v => WFolderze(v.Path) && !File.Exists(v.Path));
                if (n > 0) { SaveLibrary(l); _sharedCache = null; _libRefresh?.Invoke(); }
                ShowToast(n > 0 ? L.T("🧹 Usunięto wpisy bez pliku: ") + n : L.T("🧹 Wszystkie filmy serwera mają pliki – nic do usunięcia"), null); sig = null; fill();
            }));
            if (SerwerPominiete().Count > 0) dol.Children.Add(SBtnMake(L.T("↩ Przywróć usunięte z serwera"), () => { SetPref("serwer-pominiete", ""); SerwerSkanuj(true); }));
            var lista = new StackPanel(); right.Children.Add(new ScrollViewer { Content = lista, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0) });
            fill = () =>
            {
                var foldery = SerwerFoldery();
                var fq = szukaj.Text.Trim();
                var filmy = LoadLibrary().Where(v => File.Exists(v.Path) && WFolderze(v.Path) && (fq.Length == 0 || ShownTitle(v, MetaFor(v.Path)).IndexOf(fq, StringComparison.CurrentCultureIgnoreCase) >= 0)).GroupBy(v => v.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(v => ShownTitle(v, MetaFor(v.Path)), StringComparer.CurrentCultureIgnoreCase).ToList();
                var teraz = SendTargets().Select(p => p.Device).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                kto.Text = Environment.MachineName + L.T(" · udostępnia filmów: ") + filmy.Count + " · " + (teraz.Count == 0 ? L.T("w sieci teraz: żaden Twój komputer z Velivo") : L.T("w sieci teraz: ") + string.Join(", ", teraz));
                kto.ToolTip = teraz.Count == 0 ? L.T("Włącz Velivo na innym komputerze; musi być sparowany i w tym samym profilu.") : null;
                var nowy = string.Join("|", foldery) + "#" + wybrany + "#" + fq + "#" + string.Join("|", filmy.Select(v => { var m0 = LookupMeta(Meta, v.Path); return v.Path + "?" + m0?.Poster + "?" + (m0 != null); }));
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
                    btns.Children.Add(SBtnMake(L.T("▶ Odtwórz"), () => PlayVideos(new[] { vv.Path }, 0)));
                    btns.Children.Add(SBtnMake(L.T("🔎 Informacje"), () => ShowMetaSearch(w, vv.Path, ShownTitle(vv, MetaFor(vv.Path)), () => { sig = null; fill(); })));
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
                    var karta = SCardMake(row); karta.Cursor = System.Windows.Input.Cursors.Hand; karta.ToolTip = L.T("Kliknij – szczegóły filmu (opis, obsada, odtwarzanie, usuwanie)");
                    karta.MouseLeftButtonUp += (o, e) => { if (!(e.OriginalSource is DependencyObject src && WPrzycisku(src))) SerwerFilm(w, vv, () => { sig = null; fill(); }); };   // klik w film = szczegóły (jak Jellyfin), przyciski - swoje czynności
                    lista.Children.Add(karta);
                }
            };
            fRow.Children.Add(SBtnMake(L.T("➕ Dodaj folder…"), () =>
            {
                var d = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Folder z filmami") };
                if (d.ShowDialog(w) != true) return;
                SerwerDodajFolder(d.FolderName); fill();
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

        internal void SerwerDodajFolder(string d)
        {
            var l = SerwerFoldery(); if (!l.Contains(d, StringComparer.OrdinalIgnoreCase)) l.Add(d);
            SetPref("serwer-foldery", string.Join("|", l)); _sharedCache = null; SerwerSkanuj(true);
        }

        FrameworkElement SerwerPasek(Window win)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 8, 6, 4), VerticalAlignment = VerticalAlignment.Center };
            var dot = new TextBlock { Text = "●", Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            var stan = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 10, 0) };
            var fol = SmallButton(L.T("📁 Foldery serwera ▾"), null);
            fol.Click += (s, e) =>
            {
                var cm = new ContextMenu { PlacementTarget = fol };
                foreach (var f in SerwerFoldery())
                {
                    var ff = f; var mi = new MenuItem { Header = f + (Directory.Exists(f) ? "" : L.T("   (niedostępny)")) };
                    var del = new MenuItem { Header = L.T("➖ Usuń z serwera (filmy zostają na dysku)") };
                    del.Click += (o, a) => { SetPref("serwer-foldery", string.Join("|", SerwerFoldery().Where(x => !string.Equals(x, ff, StringComparison.OrdinalIgnoreCase)))); _sharedCache = null; _libRefresh?.Invoke(); };
                    mi.Items.Add(del); cm.Items.Add(mi);
                }
                if (cm.Items.Count > 0) cm.Items.Add(new Separator());
                var add = new MenuItem { Header = L.T("➕ Dodaj folder…") }; add.Click += (o, a) => { var d = PickFolder(win); if (d != null) SerwerDodajFolder(d); }; cm.Items.Add(add);
                var skan = new MenuItem { Header = L.T("🔄 Przejrzyj foldery i uzupełnij informacje") }; skan.Click += (o, a) => SerwerSkanuj(true); cm.Items.Add(skan);
                var jf = new MenuItem { Header = L.T("Udostępniaj też filmy z Jellyfin"), IsCheckable = true, IsChecked = GetPref("serwer-jellyfin") == "1" };
                jf.Click += (o, a) => { SetPref("serwer-jellyfin", jf.IsChecked ? "1" : "0"); _sharedCache = null; }; cm.Items.Add(jf);
                var auto = new MenuItem { Header = L.T("Uruchamiaj z Windows"), IsCheckable = true, IsChecked = ReadAutostart() >= 0 };
                auto.Click += (o, a) => WriteAutostart(auto.IsChecked, 30); cm.Items.Add(auto);
                cm.IsOpen = true;
            };
            Action odsw = () =>
            {
                int n = LoadLibrary().Where(v => File.Exists(v.Path) && WFolderze(v.Path)).Select(v => v.Path.ToLowerInvariant()).Distinct().Count(); var teraz = SendTargets().Select(p => p.Device).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                stan.Text = L.T("Serwer działa · udostępnia filmów: ") + n;
                stan.ToolTip = (teraz.Count == 0 ? L.T("W sieci teraz nie widać żadnego Twojego komputera z Velivo (musi być sparowany i w tym samym profilu).") : L.T("W sieci teraz: ") + string.Join(", ", teraz))
                    + "\n" + L.T("Twoje komputery z Velivo widzą filmy z folderów serwera – z opisem, obsadą i plakatem – i oglądają je prosto z tego komputera.");
                if (SerwerFoldery().Count == 0) stan.Text = L.T("Serwer: dodaj folder z filmami – „📁 Dodaj folder…” na dole");
            };
            var tm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) }; tm.Tick += (s, e) => odsw(); tm.Start(); win.Closed += (s, e) => tm.Stop();
            odsw();
            sp.Children.Add(dot); sp.Children.Add(stan); sp.Children.Add(fol);
            return sp;
        }
    }
}
