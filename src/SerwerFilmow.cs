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
            NetSerwerStart();   // 🌍 filmy przez internet (gdy włączone)
            SerwerSygnalStart();   // 🗄 film wysłany na serwer przez inny program Velivo - przegląd folderów od razu
            Dispatcher.BeginInvoke(new Action(() => SerwerSkanuj(false)), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // 📁 porządek sam (właściciel 2026-11-25): film wrzucony luzem do folderu serwera - gdy skończy się kopiować, trafia do własnego folderu o nazwie pliku
        // (razem z napisami, .nfo i plakatem o tej samej nazwie), potem informacje jak zawsze; zmiana nazwy PLIKU w jego folderze - folder też zmienia nazwę,
        // chyba że użytkownik nazwał folder po swojemu (nazwa folderu ≠ dawna nazwa pliku) - wtedy zostaje. Folder z kilkoma filmami - bez zmian.
        readonly List<FileSystemWatcher> _serwerObs = new List<FileSystemWatcher>(); string _serwerObsSig;
        System.Windows.Threading.DispatcherTimer _serwerPorzTm; bool _serwerPorzTrwa;
        readonly Dictionary<string, long> _serwerRozm = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);   // rozmiar przy poprzednim sprawdzeniu - plik jeszcze rośnie?
        void SerwerPilnuj()
        {
            var fol = SerwerFoldery().Where(Directory.Exists).ToList(); var sig = string.Join("|", fol);
            if (sig == _serwerObsSig) return;   // te same foldery - obserwacja już działa
            _serwerObsSig = sig; foreach (var o in _serwerObs) o.Dispose(); _serwerObs.Clear();
            foreach (var f in fol)
                try
                {
                    var o = new FileSystemWatcher(f) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite, InternalBufferSize = 65536 };
                    FileSystemEventHandler zm = (s, e) => { if (IsVlcVideoFile(e.FullPath)) Dispatcher.BeginInvoke(new Action(SerwerPorzadekZaChwile)); };
                    o.Created += zm; o.Changed += zm;
                    o.Renamed += (s, e) => { if (IsVlcVideoFile(e.FullPath)) Dispatcher.BeginInvoke(new Action(() => SerwerZmianaNazwy(e.OldFullPath, e.FullPath))); };
                    o.EnableRaisingEvents = true; _serwerObs.Add(o);
                }
                catch (Exception ex) { App.LogInfo("Serwer filmów – obserwacja folderu " + f + ": " + ex.Message); }
            SerwerPorzadekZaChwile();   // filmy leżące luzem od wcześniej - też
        }
        // sprawdzenie 30 s po ostatniej zmianie w folderach (kopiowanie dużego pliku - wiele zmian, jedno sprawdzenie)
        void SerwerPorzadekZaChwile()
        {
            if (_serwerPorzTm == null) { _serwerPorzTm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) }; _serwerPorzTm.Tick += async (s, e) => { _serwerPorzTm.Stop(); await SerwerPorzadekTeraz(); }; }
            _serwerPorzTm.Stop(); _serwerPorzTm.Start();
        }
        async Task SerwerPorzadekTeraz()
        {
            if (_serwerPorzTrwa) { SerwerPorzadekZaChwile(); return; }
            _serwerPorzTrwa = true; int czeka = 0;
            try
            {
                // skopiowany do końca: ten sam rozmiar co przy poprzednim sprawdzeniu i żaden program go nie trzyma (kopiowanie, odtwarzanie)
                bool Gotowy(string f)
                {
                    long n; try { n = new FileInfo(f).Length; } catch (Exception) { czeka++; return false; }
                    bool ten = _serwerRozm.TryGetValue(f, out var by) && by == n; _serwerRozm[f] = n;
                    if (!ten) { czeka++; return false; }
                    try { using (new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None)) { } } catch (Exception) { czeka++; return false; }
                    _serwerRozm.Remove(f); return true;
                }
                var (ok, _) = await SerwerUporzadkuj(Gotowy);
                if (ok > 0) { ShowToast(L.T("📁 Serwer filmów: nowe filmy w swoich folderach: ") + ok, null); SerwerSkanuj(false); }
            }
            catch (Exception ex) { App.LogError(ex); }
            finally { _serwerPorzTrwa = false; }
            if (czeka > 0) SerwerPorzadekZaChwile();   // jeszcze się kopiuje - sprawdzenie za 30 s
        }
        // zmiana nazwy pliku filmu w jego folderze → folder dostaje tę samą nazwę (gdy nazywał się jak dawny plik); informacje szukane od nowa po nowej nazwie
        void SerwerZmianaNazwy(string stary, string nowy)
        {
            try
            {
                var dir = Path.GetDirectoryName(nowy) ?? "";
                if (!string.Equals(Path.GetDirectoryName(stary), dir, StringComparison.OrdinalIgnoreCase)) return;   // przeniesienie, nie zmiana nazwy
                var fol = SerwerFoldery();
                if (fol.Any(f => string.Equals(f.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) { SerwerPorzadekZaChwile(); return; }   // plik luzem w folderze serwera - zwykły porządek
                var root = fol.FirstOrDefault(f => string.Equals(f.TrimEnd('\\'), (Path.GetDirectoryName(dir) ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)); if (root == null) return;   // tylko foldery filmów bezpośrednio w folderze serwera
                string sn = Path.GetFileNameWithoutExtension(stary).Trim().TrimEnd('.'), nn = Path.GetFileNameWithoutExtension(nowy).Trim().TrimEnd('.');
                if (nn.Length == 0 || sn == nn || !string.Equals(Path.GetFileName(dir), sn, StringComparison.OrdinalIgnoreCase)) return;   // folder nazwany przez użytkownika - zostaje
                if (Directory.EnumerateFiles(dir).Count(f => IsVlcVideoFile(f) && !IsAudioFile(f)) != 1) return;   // kilka filmów w jednym folderze - nie ruszam
                var cel = Path.Combine(root, nn);
                if (string.Equals(cel, dir, StringComparison.OrdinalIgnoreCase)) { var tmp = dir + ".velivo-zmiana"; Directory.Move(dir, tmp); Directory.Move(tmp, cel); }   // sama wielkość liter
                else if (Directory.Exists(cel) || File.Exists(cel)) { App.LogInfo("Serwer filmów – folder " + cel + " już jest, nazwa folderu bez zmian"); return; }
                else Directory.Move(dir, cel);
                var l = LoadLibrary(); bool zm = false;   // biblioteka: nowa ścieżka filmu (postęp oglądania i ulubione zostają)
                foreach (var v in l)
                {
                    var p = string.Equals(v.Path, stary, StringComparison.OrdinalIgnoreCase) ? nowy : v.Path;
                    if (p.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)) { v.Path = cel + p.Substring(dir.Length); zm = true; }
                }
                if (zm) { SaveLibrary(l); _sharedCache = null; _libRefresh?.Invoke(); }
                ShowToast(L.T("📁 Serwer filmów: folder dostał nową nazwę filmu – ") + nn, null);
                SerwerSkanuj(false);
            }
            catch (Exception ex) { App.LogInfo("Serwer filmów – nazwa folderu po zmianie nazwy filmu: " + ex.Message); }   // plik / folder w użyciu - zostaje jak jest
        }

        // nowe pliki z folderów -> biblioteka (wspólna), potem informacje o filmach bez opisu
        async void SerwerSkanuj(bool pokaz)
        {
            if (_serwerSkan) { if (pokaz) ShowToast(L.T("🎞 Serwer już przegląda foldery…"), null); return; }
            if (Wariant.Serwer) SerwerPilnuj();   // 📁 foldery serwera zmienione w oknie - obserwacja od nowa
            _serwerSkan = true;
            try
            {
                var fol = SerwerFoldery().Where(Directory.Exists).ToList();
                int nowe = 0;
                if (fol.Count > 0) { var l = LoadLibrary(); var pom = SerwerPominiete(); nowe = await Task.Run(() => AddToLibrary(l, fol, true)); nowe -= l.RemoveAll(v => pom.Contains(v.Path)); if (nowe > 0) { SaveLibrary(l); _libRefresh?.Invoke(); } }   // usunięte z serwera nie wracają przy przeglądaniu
                var reczne = LoadManual();   // 🎞 własna baza serwera ma pierwszeństwo - film tylko z bazy Jellyfin też dostaje własne informacje
                var bez = LoadLibrary().GroupBy(v => v.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).Where(v => File.Exists(v.Path) && WFolderze(v.Path) && !reczne.ContainsKey(MetaKey(v.Path)) && !(DateTime.TryParse(GetPref("serwer-brak|" + MetaKey(v.Path)), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var bb) && DateTime.Now - bb < TimeSpan.FromDays(7))).ToList();   // nieznaleziony - ponownie po tygodniu
                int opis = 0;
                foreach (var v in bez)
                {
                    try { if (await SerwerOpis(v)) { opis++; _libRefresh?.Invoke(); var mo = LookupMeta(Meta, v.Path); foreach (var o in mo?.People?.Take(12) ?? Enumerable.Empty<string>()) await AktorZdjecie(o); } else SetPref("serwer-brak|" + MetaKey(v.Path), DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture)); }   // nie znaleziono - nie co 10 min od nowa; znaleziono - od razu zdjęcia obsady
                    catch (Exception ex) { App.LogInfo("Serwer filmów – informacje o „" + v.Title + "”: " + ex.Message); break; }   // brak internetu - spróbujemy przy następnym przeglądzie
                    await Task.Delay(1000);   // Wikimedia - grzecznie, po jednym
                }
                // 🎭 filmy z opisem, ale bez obsady (np. nowe - Wikidane jeszcze jej nie mają): obsada z Wikipedii, najwyżej raz na 7 dni dla filmu
                foreach (var v in LoadLibrary().GroupBy(v => v.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).Where(v => File.Exists(v.Path) && WFolderze(v.Path)).ToList())
                {
                    var mi = LookupMeta(Meta, v.Path); var klucz = MetaKey(v.Path);
                    if (mi == null || mi.People != null && mi.People.Count > 0 || DateTime.TryParse(GetPref("serwer-obsada|" + klucz), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var by) && (DateTime.Now - by).TotalDays < 7) continue;
                    SetPref("serwer-obsada|" + klucz, DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture));
                    var ob = SerwerJfInfo ? (await JellyfinFilm(null, null, mi.Name, mi.Year))?.People ?? new List<string>() : new List<string>();   // Jellyfin - tylko za zgodą
                    if (ob.Count == 0) ob = await ObsadaWiki(null, mi.Original ?? mi.Name, mi.Year);
                    if (ob.Count == 0) continue;
                    mi.People = ob; var man = LoadManual(); man[klucz] = mi; Directory.CreateDirectory(MetaDir); File.WriteAllText(ManualFile, JsonSerializer.Serialize(man)); Meta[klucz] = mi; MetaObokFilmu(v.Path, true); opis++;
                    await Task.Delay(1000);
                }
                // 🖼 brak plakatu (np. Wikimedia chwilowo odmówiło - 429): sam plakat od nowa, najwyżej raz na dobę dla filmu; opis i obsada (także wpisane ręcznie) bez zmian
                int plakN = 0; var manP = LoadManual();   // najwyżej 15 na przegląd - lejek Wikimedia zostaje wolny dla wyszukiwania
                foreach (var v in LoadLibrary().GroupBy(v => v.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).Where(v => File.Exists(v.Path) && WFolderze(v.Path)).ToList())
                {
                    var klucz = MetaKey(v.Path);
                    if (plakN >= 15) break;
                    if (!manP.TryGetValue(klucz, out var mi) || mi == null || mi.Poster != null && File.Exists(Path.Combine(PosterDir, mi.Poster))   // tylko własne informacje serwera (Jellyfin - bez zmian)
                        || DateTime.TryParse(GetPref("serwer-plakat|" + klucz), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var bp) && (DateTime.Now - bp).TotalDays < 1) continue;
                    SetPref("serwer-plakat|" + klucz, DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture)); plakN++;
                    MovieInfo nm; try { nm = await SerwerZnajdz(v); } catch (Exception ex) { App.LogInfo("Serwer filmów – plakat „" + v.Title + "”: " + ex.Message); break; }
                    if (nm?.Poster != null && File.Exists(Path.Combine(PosterDir, nm.Poster)))
                    { mi.Poster = nm.Poster; manP = LoadManual(); manP[klucz] = mi; Directory.CreateDirectory(MetaDir); File.WriteAllText(ManualFile, JsonSerializer.Serialize(manP)); Meta[klucz] = mi; MetaObokFilmu(v.Path, true); opis++; }
                    await Task.Delay(1000);
                }
                if (opis > 0) _libRefresh?.Invoke();
                if (pokaz || nowe > 0 || opis > 0) ShowToast(L.T("🎞 Serwer filmów: nowe filmy ") + nowe + L.T(", uzupełnione informacje ") + opis + (bez.Count > opis ? L.T(", bez informacji ") + (bez.Count - opis) : ""), null);
            }
            catch (Exception ex) { App.LogError(ex); }
            finally { _serwerSkan = false; }
        }

        // pierwszy pasujący film z Wikidanych (rok z nazwy pliku - pewniejszy wybór); zapis jak „📝 Uzupełnij informacje”
        // pierwszy pasujący film (opis, obsada, plakat) bez zapisu - albo null
        async Task<MovieInfo> SerwerZnajdz(LibraryVideo v)
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
            if (w == null) return null;
            return await PobierzFilm(w, v.Path, null);
        }
        async Task<bool> SerwerOpis(LibraryVideo v)
        {
            var m = await SerwerZnajdz(v); if (m == null) return false;
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
        // 👥 osoba na serwerze: imię, hasło (zmiana wylogowuje jej urządzenia), foldery, administrator, urządzenia, usunięcie
        void NetOsobaOkno(Window owner, NetUser u, Action done)
        {
            bool nowa = u == null;
            var w = new Window { Title = nowa ? L.T("Nowy użytkownik") : u.Name, Width = 480, SizeToContent = SizeToContent.Height, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = SBg, ResizeMode = ResizeMode.NoResize };
            var sp = new StackPanel { Margin = new Thickness(18) }; w.Content = sp;
            TextBox Pole(string et, string tx) { var l = STxt(et, 13); l.Margin = new Thickness(0, 8, 0, 3); sp.Children.Add(l); var t = new TextBox { Text = tx, Background = SField, Foreground = SFg, CaretBrush = SFg, BorderBrush = SLine, Padding = new Thickness(6, 4, 6, 4) }; sp.Children.Add(t); return t; }
            var imie = Pole(L.T("Imię:"), nowa ? (NetUsers().Count == 0 ? Environment.UserName : "") : u.Name);
            var nowe = nowa ? Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).Replace("/", "x").Replace("+", "y") : "";
            var has = Pole(nowa ? L.T("Hasło (zapisz je – wpisuje się na telefonie i komputerze):") : L.T("Nowe hasło (puste = bez zmian; zmiana wylogowuje urządzenia tej osoby):"), nowe);
            var fl = STxt(L.T("Foldery, które widzi (nic nie zaznaczone = wszystkie):"), 13); fl.Margin = new Thickness(0, 10, 0, 3); sp.Children.Add(fl);
            var boxy = new List<(CheckBox B, string F)>();
            foreach (var f in SerwerFoldery()) { var cb = new CheckBox { Content = STxt(f, 12.5), Foreground = SFg, IsChecked = !nowa && u.Folders != null && u.Folders.Contains(f, StringComparer.OrdinalIgnoreCase), Margin = new Thickness(0, 2, 0, 0) }; boxy.Add((cb, f)); sp.Children.Add(cb); }
            var adm = new CheckBox { Content = STxt(L.T("Administrator (zarządza użytkownikami; ogląda na bibliotece serwera)"), 12.5), Foreground = SFg, IsChecked = nowa ? NetUsers().Count == 0 : u.Admin, Margin = new Thickness(0, 10, 0, 0) }; sp.Children.Add(adm);
            if (!nowa)
            {
                var ul = STxt(L.T("Urządzenia tej osoby:"), 13); ul.Margin = new Thickness(0, 10, 0, 3); sp.Children.Add(ul);
                var dv = NetUrzadzenia().Where(d => d.UserId == u.Id).ToList();
                if (dv.Count == 0) sp.Children.Add(STxt(L.T("Jeszcze żadne."), 12, SDim));
                foreach (var d in dv) { var row = new DockPanel(); var id = d.Id; var od = SBtnMake(L.T("Odłącz"), () => { NetOdlacz(id); row.Visibility = Visibility.Collapsed; }); DockPanel.SetDock(od, Dock.Right); row.Children.Add(od); row.Children.Add(STxt(d.Name + " · " + DateTimeOffset.FromUnixTimeMilliseconds(d.Seen).LocalDateTime.ToString("d.MM HH:mm"), 12.5)); sp.Children.Add(row); }
            }
            var info = STxt("", 12.5, SDim); info.Margin = new Thickness(0, 8, 0, 0); sp.Children.Add(info);
            var rz = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) }; sp.Children.Add(rz);
            rz.Children.Add(SBtnMake(L.T("💾 Zapisz"), () =>
            {
                var n = imie.Text.Trim(); if (n.Length == 0) { info.Text = L.T("Wpisz imię."); return; }
                var inna = NetUserByName(n); if (inna != null && inna != u) { info.Text = L.T("Ta osoba już jest."); return; }
                if (nowa && has.Text.Trim().Length < 4) { info.Text = L.T("Hasło – co najmniej 4 znaki."); return; }
                var lista = NetUsers();
                if (!adm.IsChecked.Value && (nowa ? lista.Count == 0 : u.Admin && lista.Count(x => x.Admin) == 1)) { info.Text = L.T("Musi zostać co najmniej jeden administrator."); return; }
                lock (lista)
                {
                    if (nowa) { u = new NetUser { Id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)) }; lista.Add(u); }
                    u.Name = n; u.Admin = adm.IsChecked == true;
                    u.Folders = boxy.Where(x => x.B.IsChecked == true).Select(x => x.F).ToList();
                    if (has.Text.Trim().Length > 0 && has.Text.Trim() != u.Pass) { if (!nowa) NetOdlaczOsobe(u.Id); u.Pass = has.Text.Trim(); }
                }
                NetUsersSave(); w.Close(); done?.Invoke();
            }, SAccent));
            if (!nowa) rz.Children.Add(SBtnMake(L.T("🗑 Usuń użytkownika"), () =>
            {
                var lista = NetUsers(); if (u.Admin && lista.Count(x => x.Admin) == 1) { info.Text = L.T("Musi zostać co najmniej jeden administrator."); return; }
                if (MessageBox.Show(w, L.T("Usunąć ") + u.Name + L.T("? Jego urządzenia zostaną odłączone, a postęp i ulubione usunięte."), Wariant.Nazwa, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                lock (lista) lista.Remove(u); NetOdlaczOsobe(u.Id); NetUsersSave(); w.Close(); done?.Invoke();
            }));
            w.ShowDialog();
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
            var w = new Window { Title = tytul, Width = 940, Height = m?.People?.Count > 0 ? 720 : 600, WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen, Owner = owner, Topmost = owner?.Topmost ?? false,
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
            var btn = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) }; top.Children.Add(btn);
            Action<string> start = how => { w.Close(); graj(how); };
            bool wznow = pos > 0 && !done && len > 0;
            var b0 = SBtnMake(wznow ? L.T("▶ Wznów od ") + TimeSpan.FromMilliseconds(pos).ToString(pos >= 3600000 ? @"h\:mm\:ss" : @"m\:ss") : L.T("▶ Odtwórz"), () => start(wznow ? "wznow" : null), SAccent); btn.Children.Add(b0);
            if (wznow) btn.Children.Add(SBtnMake(L.T("⏮ Od początku"), () => start("poczatek")));
            foreach (var x in wiecej ?? new List<(string, Action)>()) { var xx = x; btn.Children.Add(SBtnMake(xx.Txt, () => { w.Close(); xx.Act(); })); }
            if (wznow) { var pr = new ProgressBar { Height = 5, Maximum = len, Value = pos, Margin = new Thickness(0, 4, 0, 0), Foreground = SAccent, Background = SLine, BorderThickness = new Thickness(0) }; top.Children.Add(pr); }
            var ov = STxt(string.IsNullOrWhiteSpace(m?.Overview) ? L.T("Brak opisu – „🔎 Wyszukaj informacje” pobierze opis, obsadę i plakat.") : m.Overview, 15, string.IsNullOrWhiteSpace(m?.Overview) ? SDim : SFg);
            ov.VerticalAlignment = VerticalAlignment.Top; ov.LineHeight = 22;
            if (m?.People?.Count > 0)
            {   // 🎭 obsada jak w Jellyfin: karty ze zdjęciem (Wikidane / Wikimedia, pobierane w tle i zapamiętane) oraz imieniem i nazwiskiem
                var ob = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(ob, Dock.Bottom); d.Children.Add(ob);
                var rz = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                var sv = new ScrollViewer { Content = rz, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Cursor = System.Windows.Input.Cursors.Hand };
                // przesuwanie jak w Jellyfin: ‹ › (o 3 karty), kółko myszy, przeciąganie myszą
                void Przesun(double dx) => sv.ScrollToHorizontalOffset(Math.Max(0, Math.Min(sv.ScrollableWidth, sv.HorizontalOffset + dx)));
                var naglowek = new DockPanel(); var lewo = SBtnMake("‹", () => Przesun(-306)); var prawo = SBtnMake("›", () => Przesun(306));
                lewo.ToolTip = L.T("Poprzedni aktorzy"); prawo.ToolTip = L.T("Następni aktorzy"); lewo.Margin = prawo.Margin = new Thickness(6, 0, 0, 0);
                DockPanel.SetDock(prawo, Dock.Right); DockPanel.SetDock(lewo, Dock.Right); naglowek.Children.Add(prawo); naglowek.Children.Add(lewo); naglowek.Children.Add(STxt(L.T("Obsada"), 15, null, true));
                ob.Children.Add(naglowek); ob.Children.Add(sv);
                sv.ScrollChanged += (s0, e0) => { lewo.IsEnabled = sv.HorizontalOffset > 1; prawo.IsEnabled = sv.HorizontalOffset < sv.ScrollableWidth - 1; lewo.Opacity = lewo.IsEnabled ? 1 : 0.35; prawo.Opacity = prawo.IsEnabled ? 1 : 0.35; };
                sv.PreviewMouseWheel += (s0, e0) => { if (sv.ScrollableWidth <= 0) return; Przesun(-e0.Delta); e0.Handled = true; };
                Point? chwyt = null; double odX = 0;
                sv.PreviewMouseLeftButtonDown += (s0, e0) => { chwyt = e0.GetPosition(sv); odX = sv.HorizontalOffset; };
                sv.PreviewMouseMove += (s0, e0) => { if (chwyt == null || e0.LeftButton != System.Windows.Input.MouseButtonState.Pressed) { chwyt = null; return; } var dx = e0.GetPosition(sv).X - chwyt.Value.X; if (Math.Abs(dx) > 4) { if (!sv.IsMouseCaptured) sv.CaptureMouse(); sv.ScrollToHorizontalOffset(Math.Max(0, odX - dx)); } };
                sv.PreviewMouseLeftButtonUp += (s0, e0) => { chwyt = null; if (sv.IsMouseCaptured) sv.ReleaseMouseCapture(); };
                foreach (var osoba in m.People.Take(12))
                {
                    var ramka = new Border { Width = 92, Height = 122, CornerRadius = new CornerRadius(8), Background = SLine, ClipToBounds = true, Child = new TextBlock { Text = "👤", FontSize = 34, Foreground = SDim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                    var karta = new StackPanel { Width = 92, Margin = new Thickness(0, 0, 10, 6), ToolTip = osoba }; karta.Children.Add(ramka);
                    var n0 = STxt(osoba, 12, null, true); n0.Margin = new Thickness(0, 4, 0, 0); n0.TextAlignment = TextAlignment.Center; karta.Children.Add(n0);
                    rz.Children.Add(karta);
                    var os = osoba; _ = Task.Run(async () => { var f = await AktorZdjecie(os); if (f != null) Dispatcher.BeginInvoke(new Action(() => { var img = LoadLogo(f, 244); if (img != null) ramka.Child = new Image { Source = img, Stretch = Stretch.UniformToFill }; })); });
                }
            }
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
            // 🎭 informacje o filmach: serwer sam (Wikidane, Wikipedia); Jellyfin tylko za zgodą (TMDb: opis, plakat, obsada filmów z jego biblioteki)
            var jfInfo = new CheckBox { Content = STxt(L.T("Pobieraj informacje także z Jellyfin (za zgodą)"), 13), IsChecked = SerwerJfInfo, Foreground = SFg, Margin = new Thickness(0, 4, 0, 0),
                ToolTip = L.T("Wyłączone: serwer szuka opisu, plakatu i obsady sam (Wikidane, Wikipedia) – Jellyfin niepotrzebny.\nWłączone: najpierw TMDb przez Twój Jellyfin (więcej danych, obsada filmów z jego biblioteki), potem sam.") };
            jfInfo.Click += (s, e) => { SetPref("serwer-jf-info", jfInfo.IsChecked == true ? "1" : "0"); _meta = null; };
            left.Children.Add(auto); left.Children.Add(jf); left.Children.Add(jfInfo);
            // 🌍 dostęp przez internet - jak Jellyfin: adres domu + hasło serwera
            var net = STxt(L.T("🌍 Dostęp przez internet"), 14.5, null, true); net.Margin = new Thickness(0, 18, 0, 6); left.Children.Add(net);
            left.Children.Add(STxt(L.T("Oglądaj swoje filmy poza domem (komputer, telefon): wpisz tam adres domu z portem ") + NetFilmPort + L.T(", swoje imię i hasło. Każda osoba ma własny postęp, ulubione i dostęp do folderów. Wszystko szyfrowane."), 12, SDim));
            var netOn = new CheckBox { Content = STxt(L.T("Udostępniaj moje filmy przez internet"), 13), IsChecked = NetWlaczony, Foreground = SFg, Margin = new Thickness(0, 6, 0, 4) };
            var netSt = STxt("", 12, SDim); netSt.TextWrapping = TextWrapping.Wrap;
            // 👥 użytkownicy (makieta-serwer-uzytkownicy.html)
            var osoby = new StackPanel(); string osSig = null;
            Action netFill = null;
            netFill = () =>
            {
                netSt.Text = (NetWlaczony ? "● " : "○ ") + (NetStan.Length > 0 ? NetStan : L.T("wyłączony"));
                var us = NetUsers().ToList(); var dv = NetUrzadzenia();
                var sigN = string.Join("|", us.Select(u => u.Id + u.Name + u.Admin + (u.Folders == null ? "" : string.Join(",", u.Folders)) + dv.Count(d => d.UserId == u.Id) + (u.Seen / 60000)));
                if (sigN == osSig) return; osSig = sigN;
                osoby.Children.Clear();
                if (us.Count == 0) osoby.Children.Add(STxt(L.T("Brak osób – dodaj siebie (administrator), potem rodzinę i znajomych."), 12, SDim));
                foreach (var u in us)
                {
                    var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) }; var uu = u;
                    var zm = SBtnMake(L.T("Zmień"), () => NetOsobaOkno(w, uu, () => { osSig = null; netFill(); })); zm.Margin = new Thickness(6, 0, 0, 0); DockPanel.SetDock(zm, Dock.Right); row.Children.Add(zm);
                    var dostep = u.Folders == null || u.Folders.Count == 0 ? L.T("wszystkie foldery") : string.Join(", ", u.Folders.Select(x => Path.GetFileName(x.TrimEnd('\\')) is string n && n.Length > 0 ? n : x));
                    var t = STxt((u.Admin ? "👑 " : "👤 ") + u.Name + "  ·  " + dostep + (u.Admin ? " · " + L.T("administrator") : "") + "  ·  " + L.T("urządzenia: ") + dv.Count(d => d.UserId == u.Id) + (u.Seen > 0 ? "  ·  " + DateTimeOffset.FromUnixTimeMilliseconds(u.Seen).LocalDateTime.ToString("d.MM HH:mm") : ""), 12.5);
                    t.TextWrapping = TextWrapping.Wrap; row.Children.Add(t); osoby.Children.Add(row);
                }
            };
            netOn.Click += (s, e) =>
            {
                SetPref("serwer-net", netOn.IsChecked == true ? "1" : "0"); NetSerwerStart(); netFill();
                if (netOn.IsChecked == true && NetUsers().Count == 0) NetOsobaOkno(w, null, () => { osSig = null; netFill(); });   // pierwsza osoba - Ty (administrator)
            };
            left.Children.Add(netOn); left.Children.Add(netSt);
            var dl = STxt(L.T("👥 Użytkownicy"), 13, null, true); dl.Margin = new Thickness(0, 10, 0, 4); left.Children.Add(dl); left.Children.Add(osoby);
            var dod = SBtnMake(L.T("➕ Dodaj użytkownika"), () => NetOsobaOkno(w, null, () => { osSig = null; netFill(); })); dod.HorizontalAlignment = HorizontalAlignment.Left; dod.Margin = new Thickness(0, 6, 0, 0); left.Children.Add(dod);
            netFill();
            var netTm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) }; netTm.Tick += (s, e) => netFill(); netTm.Start(); w.Closed += (s, e) => netTm.Stop();
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
            dol.Children.Add(SBtnMake(L.T("📁 Uporządkuj w foldery"), async () =>
            {   // filmy leżące luzem w folderze serwera → każdy w swoim folderze (razem z napisami, opisem i plakatem o tej samej nazwie)
                if (MessageBox.Show(w, L.T("Każdy film leżący luzem w folderach serwera trafi do własnego folderu o nazwie filmu – razem z napisami, opisem (.nfo) i plakatem o tej samej nazwie.\n\nPostęp oglądania, ulubione i informacje zostają. Film, który teraz gra, zostanie zatrzymany i wznowiony z nowego miejsca (grany w innym programie Velivo – pominięty).\n\nUporządkować?"), Wariant.Nazwa, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                var (ok, pom) = await SerwerUporzadkuj(); sig = null; fill(); SerwerSkanuj(false);
                ShowToast(L.T("📁 Uporządkowano filmy: ") + ok + (pom > 0 ? L.T(", pominięte (w użyciu): ") + pom : ""), null);
            }));
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
                kto.Text = Environment.MachineName + L.T(" · udostępnia filmów: ") + filmy.Count + " · " + (teraz.Count == 0 ? (_lanLead == null ? L.T("sieć domową prowadzi inny program Velivo na tym komputerze (np. Odtwarzacz) – filmy są udostępniane") : L.T("w sieci teraz: żaden Twój komputer z Velivo")) : L.T("w sieci teraz: ") + string.Join(", ", teraz));
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
