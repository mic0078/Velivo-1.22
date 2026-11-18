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
            Meta[key] = m;
            return true;
        }

        // 🎞 okno serwera = biblioteka Odtwarzacza (ta sama lista, opisy, plakaty, wyszukiwanie, napisy) z filmami folderów serwera,
        // na górze pasek: stan serwera, komputery w sieci, foldery (menu: usuń, przejrzyj), filmy z Jellyfin
        internal void SerwerOkno() => ShowVideoLibrary();

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
                int n = LoadLibrary().Count(v => File.Exists(v.Path) && WFolderze(v.Path)); var teraz = SendTargets().Select(p => p.Device).ToList();
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
