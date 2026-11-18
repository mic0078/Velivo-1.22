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

        // 🎞 okno serwera: stan, kto go widzi, foldery, filmy serwera z informacjami (nie cała biblioteka komputera)
        Window _serwerWin;
        internal void SerwerOkno()
        {
            if (_serwerWin != null) { if (_serwerWin.WindowState == WindowState.Minimized) _serwerWin.WindowState = WindowState.Normal; _serwerWin.Activate(); return; }
            var wa = SystemParameters.WorkArea;
            var w = _serwerWin = new Window { Title = L.T("🎞 Velivo Serwer filmów"), Width = Math.Min(1100, wa.Width - 40), Height = Math.Min(720, wa.Height - 40), WindowStartupLocation = WindowStartupLocation.CenterScreen };
            Independent(w); w.Closed += (s, e) => _serwerWin = null;
            var root = new DockPanel { Margin = new Thickness(14) };
            var head = new StackPanel(); DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
            var stan = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            var kto = new TextBlock { Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            head.Children.Add(stan); head.Children.Add(kto);
            head.Children.Add(new TextBlock { Text = L.T("Twoje komputery z Velivo (Odtwarzacz albo pełne Velivo) widzą filmy z folderów serwera w swojej bibliotece – z opisem, obsadą i plakatem – i oglądają je prosto z tego komputera. Inne filmy z biblioteki tego komputera nie są udostępniane."), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(0, 6, 0, 10) });
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
            root.Children.Add(grid);
            // lewa: foldery i ustawienia
            var left = new StackPanel(); grid.Children.Add(left);
            left.Children.Add(new TextBlock { Text = L.T("📁 Foldery serwera"), FontWeight = FontWeights.SemiBold, FontSize = 14 });
            var fol = new ListBox { Height = 160, Margin = new Thickness(0, 6, 0, 6) }; left.Children.Add(fol);
            var fRow = new WrapPanel(); left.Children.Add(fRow);
            var auto = new CheckBox { Content = L.T("Uruchamiaj z Windows"), IsChecked = ReadAutostart() >= 0, Margin = new Thickness(0, 14, 0, 4) };
            auto.Click += (s, e) => WriteAutostart(auto.IsChecked == true, 30);
            var jf = new CheckBox { Content = L.T("Udostępniaj też filmy z Jellyfin"), IsChecked = GetPref("serwer-jellyfin") == "1" };
            jf.Click += (s, e) => { SetPref("serwer-jellyfin", jf.IsChecked == true ? "1" : "0"); _sharedCache = null; };
            left.Children.Add(new TextBlock { Text = L.T("Ustawienia"), FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 16, 0, 0) });
            left.Children.Add(auto); left.Children.Add(jf);
            // prawa: filmy serwera
            var right = new DockPanel(); Grid.SetColumn(right, 2); grid.Children.Add(right);
            var rHead = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 14 }; DockPanel.SetDock(rHead, Dock.Top); right.Children.Add(rHead);
            var skan = SmallButton(L.T("🔄 Przejrzyj foldery i uzupełnij informacje"), () => SerwerSkanuj(true)); skan.HorizontalAlignment = HorizontalAlignment.Left; skan.Margin = new Thickness(0, 8, 0, 0);
            DockPanel.SetDock(skan, Dock.Bottom); right.Children.Add(skan);
            var lista = new StackPanel(); right.Children.Add(new ScrollViewer { Content = lista, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0) });
            string sig = null;
            Action fill = () =>
            {
                var foldery = SerwerFoldery();
                var filmy = LoadLibrary().Where(v => File.Exists(v.Path) && WFolderze(v.Path)).OrderBy(v => ShownTitle(v, MetaFor(v.Path)), StringComparer.CurrentCultureIgnoreCase).ToList();
                var teraz = SendTargets().Select(p => p.Device).ToList();
                stan.Text = L.T("🎞 Serwer działa na ") + Environment.MachineName + L.T(" · udostępnia filmów: ") + filmy.Count;
                kto.Text = teraz.Count == 0 ? L.T("Teraz nie widać w sieci żadnego Twojego komputera z Velivo (włącz Velivo na innym komputerze; komputer musi być sparowany i w tym samym profilu).") : L.T("W sieci teraz: ") + string.Join(", ", teraz);
                var nowy = string.Join("|", foldery) + "#" + string.Join("|", filmy.Select(v => v.Path + (LookupMeta(Meta, v.Path)?.Poster ?? "") + (LookupMeta(Meta, v.Path) != null)));
                if (nowy == sig) return; sig = nowy;   // przebudowa tylko przy zmianie
                fol.Items.Clear();
                foreach (var f in foldery) fol.Items.Add(f + (Directory.Exists(f) ? L.T("   · filmów: ") + filmy.Count(v => v.Path.StartsWith(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) : L.T("   (niedostępny)")));
                rHead.Text = L.T("Filmy na serwerze") + " (" + filmy.Count + ")";
                lista.Children.Clear();
                if (foldery.Count == 0) lista.Children.Add(new TextBlock { Text = L.T("Dodaj folder z filmami (po lewej) – serwer przejrzy go i pobierze informacje o filmach."), Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap });
                foreach (var v in filmy)
                {
                    var mi = LookupMeta(Meta, v.Path); var vv = v;
                    var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                    var pf = mi?.Poster != null ? Path.Combine(PosterDir, mi.Poster) : null;
                    var img = new Image { Width = 48, Height = 70, Stretch = Stretch.UniformToFill, Margin = new Thickness(0, 0, 10, 0), Source = pf != null && File.Exists(pf) ? LoadLogo(pf, 140) : null };
                    DockPanel.SetDock(img, Dock.Left); row.Children.Add(img);
                    var btns = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(btns, Dock.Right);
                    var bi = SmallButton(L.T("🔎 Informacje"), () => ShowMetaSearch(w, vv.Path, ShownTitle(vv, MetaFor(vv.Path)), () => { sig = null; })); bi.Margin = new Thickness(0, 0, 0, 4);
                    var bp = SmallButton(L.T("▶ Odtwórz"), () => PlayVideos(new[] { vv.Path }, 0));
                    btns.Children.Add(bi); btns.Children.Add(bp); row.Children.Add(btns);
                    var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    txt.Children.Add(new TextBlock { Text = ShownTitle(v, mi) + (mi?.Rating > 0 ? "   ★ " + mi.Rating.ToString("0.0") : ""), FontWeight = FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
                    if (mi != null && (mi.Genres?.Count > 0 || !string.IsNullOrEmpty(mi.Director))) txt.Children.Add(new TextBlock { Text = string.Join(", ", mi.Genres ?? new List<string>()) + (string.IsNullOrEmpty(mi.Director) ? "" : L.T(" · reż. ") + mi.Director), Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis });
                    txt.Children.Add(mi != null && !string.IsNullOrWhiteSpace(mi.Overview)
                        ? new TextBlock { Text = L.T("✔ opis") + (mi.People?.Count > 0 ? L.T(", obsada") : "") + (pf != null && File.Exists(pf) ? L.T(", plakat") : ""), Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)) }
                        : new TextBlock { Text = L.T("⚠ brak informacji – kliknij „🔎 Informacje”"), Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)) });
                    row.Children.Add(txt);
                    lista.Children.Add(row);
                }
            };
            fRow.Children.Add(SmallButton(L.T("➕ Dodaj folder…"), () =>
            {
                var d = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Folder z filmami") };
                if (d.ShowDialog(w) != true) return;
                var l = SerwerFoldery(); if (!l.Contains(d.FolderName, StringComparer.OrdinalIgnoreCase)) l.Add(d.FolderName);
                SetPref("serwer-foldery", string.Join("|", l)); _sharedCache = null; fill(); SerwerSkanuj(true);
            }));
            fRow.Children.Add(SmallButton(L.T("➖ Usuń zaznaczony"), () =>
            {
                int i = fol.SelectedIndex; if (i < 0) return;
                var l = SerwerFoldery(); if (i < l.Count) l.RemoveAt(i);
                SetPref("serwer-foldery", string.Join("|", l)); _sharedCache = null; fill();   // filmy zostają w bibliotece komputera - przestają być udostępniane
            }));
            var tm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            tm.Tick += (s, e) => { if (w.IsVisible && w.WindowState != WindowState.Minimized) fill(); };
            w.Closed += (s, e) => tm.Stop();
            w.Content = root; fill(); tm.Start(); w.Show(); w.Activate();
        }
    }
}
