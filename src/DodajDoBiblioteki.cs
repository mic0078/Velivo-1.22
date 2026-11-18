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
    // ➕ grający film do biblioteki: do Jellyfin (plik trafia do folderu biblioteki Jellyfin, Jellyfin skanuje i daje opis i plakat),
    // a gdy ten komputer nie widzi folderu Jellyfin - do własnej biblioteki Velivo 🔒 (z przeniesieniem pliku do jej folderu) i wyszukaniem informacji.
    // Film z adresu (strumień od dostawcy) - najpierw pobranie do wybranego folderu.
    public partial class MainWindow
    {
        // foldery bibliotek Jellyfin, które ten komputer widzi (Jellyfin na tym komputerze albo folder udostępniony w sieci)
        // Dir - folder widziany z tego komputera (null - trzeba raz wskazać folder sieciowy); Loc - folder tak, jak widzi go Jellyfin
        async Task<List<(JellyfinConf S, string Name, string Loc, string Dir)>> JellyfinFoldery()
        {
            var r = new List<(JellyfinConf, string, string, string)>();
            foreach (var s in JfServers(LoadJellyfinConf()))
                try
                {
                    var url = await JellyfinReach(s); if (url == null) continue;
                    using (var http = JellyfinClient(new JellyfinConf { Url = url, Key = s.Key }))
                    {
                        var vf = JsonDocument.Parse(await (await http.GetAsync("Library/VirtualFolders")).Content.ReadAsStringAsync()).RootElement;
                        foreach (var f in vf.EnumerateArray())
                        {
                            if (f.TryGetProperty("CollectionType", out var ct) && ct.GetString() is string t && t != "movies" && t != "homevideos" && t != "mixed") continue;   // seriale, muzyka - nie
                            var nm = f.TryGetProperty("Name", out var n) ? n.GetString() : "";
                            if (f.TryGetProperty("Locations", out var locs))
                                foreach (var l in locs.EnumerateArray())
                                {
                                    var loc = (l.GetString() ?? "").Replace('/', '\\'); if (loc.Length == 0) continue;
                                    // folder z punktu widzenia Jellyfin: na tym komputerze (Jellyfin tutaj) albo ścieżka sieciowa \\… - wprost; inaczej - folder sieciowy wskazany raz przez właściciela
                                    var d = JfTutaj(url) || loc.StartsWith("\\\\") ? loc : GetPref("jf-folder|" + loc);
                                    if (d != null && !await Task.Run(() => { try { return Directory.Exists(d); } catch (Exception) { return false; } }).WaitAsync(TimeSpan.FromSeconds(4)).ContinueWith(x => x.IsCompletedSuccessfully && x.Result)) d = null;
                                    r.Add((new JellyfinConf { Url = url, Key = s.Key }, JfName(s.Url) + " – " + nm, loc, d));
                                }
                        }
                    }
                }
                catch (Exception ex) { App.LogInfo("Jellyfin – foldery biblioteki: " + ex.Message); }
            return r;
        }

        static bool JfTutaj(string url)
        {
            try
            {
                var h = new Uri(url).Host; if (h == "localhost" || h == "127.0.0.1" || string.Equals(h, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return true;
                var moje = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address.ToString());
                return moje.Contains(h);
            }
            catch (Exception) { return false; }
        }

        internal async void DodajDoBiblioteki(string path, string title, Window owner)
        {
            bool zAdresu = path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (!zAdresu && !File.Exists(path)) { InfoBox(owner, L.T("Ten film nie jest plikiem na tym komputerze – dodać można plik z dysku albo film z adresu (zostanie pobrany).")); return; }
            var sp = new StackPanel { Margin = new Thickness(16) };
            sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap });
            sp.Children.Add(new TextBlock { Text = zAdresu ? L.T("Film z adresu – najpierw się pobierze, potem trafi do wybranej biblioteki.") : L.T("Gdzie dodać ten film?"), Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(0, 2, 0, 8), TextWrapping = TextWrapping.Wrap });
            var szukam = new TextBlock { Text = L.T("Szukam folderów biblioteki Jellyfin…"), Foreground = Brushes.Gray, FontSize = 12 }; sp.Children.Add(szukam);
            var opcje = new StackPanel(); sp.Children.Add(opcje);
            var wlasna = new RadioButton { Content = L.T("🔒 Własna biblioteka Velivo"), FontSize = 14, Margin = new Thickness(0, 6, 0, 2), IsChecked = true };
            string folder = !string.IsNullOrWhiteSpace(_settings.VideoDir) && Directory.Exists(_settings.VideoDir) ? _settings.VideoDir : DefaultVideoDir();
            var przenies = new CheckBox { Content = zAdresu ? L.T("Pobierz do folderu:") : L.T("Przenieś plik do folderu biblioteki:"), IsChecked = true, Margin = new Thickness(22, 2, 0, 2) };
            var fRow = new DockPanel { Margin = new Thickness(22, 0, 0, 4) };
            var fTxt = new TextBlock { Text = folder, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
            var fBtn = new Button { Content = L.T("Zmień folder…"), Padding = new Thickness(10, 3, 10, 3) };
            DockPanel.SetDock(fBtn, Dock.Right); fRow.Children.Add(fBtn); fRow.Children.Add(fTxt);
            var link = new CheckBox { Content = L.T("🔗 Plik zostaje na tym komputerze – w Jellyfin tylko link (Jellyfin czyta film przez sieć)"), Margin = new Thickness(0, 6, 0, 0), IsEnabled = !zAdresu,
                ToolTip = L.T("Do biblioteki Jellyfin trafia mały plik .strm ze ścieżką sieciową filmu. Folder z filmem musi być udostępniony w sieci, a ten komputer włączony, gdy oglądasz z Jellyfin.") };
            var info = new CheckBox { Content = L.T("Potem wyszukaj informacje (opis, plakat, ocena)"), IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
            var ok = new Button { Content = L.T("➕ Dodaj"), IsDefault = true, Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
            var cancel = new Button { Content = L.T("Anuluj"), IsCancel = true, Padding = new Thickness(16, 5, 16, 5) };
            var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) }; bs.Children.Add(ok); bs.Children.Add(cancel);
            var stan = new TextBlock { Foreground = Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            sp.Children.Add(link); sp.Children.Add(wlasna); sp.Children.Add(przenies); sp.Children.Add(fRow); sp.Children.Add(info); sp.Children.Add(stan); sp.Children.Add(bs);
            var w = new Window { Title = L.T("➕ Dodaj do biblioteki"), Width = 560, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Owner = owner, Topmost = owner?.Topmost == true, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = sp };
            fBtn.Click += (s, e) => { var d = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = folder, Title = L.T("Folder własnej biblioteki") }; if (d.ShowDialog(w) == true) { folder = d.FolderName; fTxt.Text = folder; } };
            cancel.Click += (s, e) => w.Close();
            var jf = new List<(RadioButton Rb, JellyfinConf S, string Loc, Func<string> Dir)>();
            w.Loaded += async (s, e) =>
            {
                List<(JellyfinConf S, string Name, string Loc, string Dir)> l;
                try { l = await JellyfinFoldery(); } catch (Exception) { l = new List<(JellyfinConf, string, string, string)>(); }
                szukam.Text = l.Count == 0 ? (LoadJellyfinConf() == null ? L.T("Jellyfin nie jest ustawiony – film trafi do własnej biblioteki.") : L.T("Jellyfin nie odpowiada – film trafi do własnej biblioteki."))
                    : L.T("Jellyfin: plik trafi do folderu jego biblioteki, Jellyfin sam pobierze opis i plakat. Gdy Jellyfin jest na innym komputerze, wskaż raz ten folder w sieci (np. \\\\NUCBOX\\Filmy albo dysk sieciowy) – trzeba mieć prawo zapisu.");
                foreach (var x in l)
                {
                    string dir = x.Dir; var xx = x;
                    var rb = new RadioButton { FontSize = 14, Margin = new Thickness(0, 4, 0, 2) };
                    var row = new DockPanel();
                    var txt = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                    Action pokaz = () => txt.Text = "🎞 Jellyfin " + xx.Name + "\n    " + (dir != null ? dir + (dir != xx.Loc ? "  (= " + xx.Loc + L.T(" na serwerze)") : "") : xx.Loc + L.T(" na serwerze – wskaż ten folder w sieci"));
                    var pick = new Button { Content = L.T("Wskaż folder w sieci…"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    pick.Click += (o, a) =>
                    {
                        var d = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Ten sam folder co w Jellyfin: ") + xx.Loc };
                        if (d.ShowDialog(w) != true) return;
                        dir = d.FolderName; SetPref("jf-folder|" + xx.Loc, dir); pokaz(); rb.IsChecked = true;
                    };
                    DockPanel.SetDock(pick, Dock.Right); row.Children.Add(pick); row.Children.Add(txt); rb.Content = row; pokaz();
                    opcje.Children.Add(rb); jf.Add((rb, x.S, x.Loc, () => dir));
                }
                ok.IsEnabled = true;
            };
            ok.Click += async (s, e) =>
            {
                var cel = jf.FirstOrDefault(x => x.Rb.IsChecked == true);
                bool doJf = cel.Rb != null, ruch = doJf || przenies.IsChecked == true;
                if (doJf && cel.Dir() == null) { stan.Text = L.T("Wskaż najpierw ten folder Jellyfin w sieci (przycisk „Wskaż folder w sieci…”)."); return; }
                var nazwa = string.Join("_", title.Split(Path.GetInvalidFileNameChars())).Trim();
                var dir = doJf ? Path.Combine(cel.Dir(), nazwa) : folder;   // Jellyfin: film we własnym folderze „Tytuł (rok)”
                bool szukaj = info.IsChecked == true;
                if (zAdresu)
                {
                    w.Close();
                    ShowUrlDownload(path, owner, dir, f => Dodano(f, doJf ? cel.S : null, szukaj, title, owner));
                    return;
                }
                if (doJf && link.IsChecked == true)
                {   // 🔗 link .strm: film zostaje tutaj, Jellyfin czyta go przez udostępniony folder
                    var unc = SciezkaSieciowa(path);
                    if (unc == null) { stan.Text = L.T("Folder z tym filmem nie jest udostępniony w sieci – udostępnij go (prawy przycisk na folderze → Właściwości → Udostępnianie), żeby Jellyfin mógł go czytać. Albo odznacz „tylko link” – film zostanie przeniesiony."); return; }
                    try
                    {
                        Directory.CreateDirectory(dir);
                        File.WriteAllText(Path.Combine(dir, nazwa + ".strm"), unc, new System.Text.UTF8Encoding(false));
                    }
                    catch (Exception ex) { stan.Text = "❌ " + ex.Message; return; }
                    w.Close();
                    Dodano(path, cel.S, szukaj, title, owner);
                    return;
                }
                ok.IsEnabled = false; stan.Text = ruch ? L.T("Przenoszę plik…") : "";
                string nowa = path;
                if (ruch)
                    try { nowa = await Task.Run(() => Przenies(path, dir)); }
                    catch (Exception ex) { stan.Text = "❌ " + ex.Message; ok.IsEnabled = true; return; }
                w.Close();
                Dodano(nowa, doJf ? cel.S : null, szukaj, title, owner);
                if (nowa != path && File.Exists(path)) InfoBox(owner, L.T("Plik skopiowany do biblioteki. Oryginału nie dało się usunąć, bo właśnie gra – usuń go po obejrzeniu:\n") + path);
            };
            w.ShowDialog();
        }

        // przeniesienie (inny dysk - kopia i usunięcie); grający plik bywa zablokowany - wtedy zostaje kopia, oryginał do usunięcia później
        static string Przenies(string src, string dir)
        {
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, Path.GetFileName(src));
            for (int i = 2; File.Exists(dest); i++) dest = Path.Combine(dir, Path.GetFileNameWithoutExtension(src) + " (" + i + ")" + Path.GetExtension(src));
            if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) return src;
            try { File.Move(src, dest); }
            catch (IOException) { File.Copy(src, dest); try { File.Delete(src); } catch (Exception) { } }
            foreach (var ext in new[] { ".srt", ".pl.srt", ".en.srt", ".ass" })   // napisy obok filmu - razem z nim
            {
                var sub = Path.Combine(Path.GetDirectoryName(src), Path.GetFileNameWithoutExtension(src) + ext);
                if (File.Exists(sub)) try { File.Move(sub, Path.Combine(dir, Path.GetFileNameWithoutExtension(dest) + ext)); } catch (Exception) { }
            }
            return dest;
        }

        // ścieżka sieciowa pliku (\\KOMPUTER\udział\…) z folderów udostępnionych na tym komputerze (rejestr serwera plików Windows); null - folder nieudostępniony
        static string SciezkaSieciowa(string file)
        {
            if (file.StartsWith("\\\\")) return file;
            string best = null; int len = 0; var full = Path.GetFullPath(file);
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LanmanServer\Shares"))
                    foreach (var n in k?.GetValueNames() ?? new string[0])
                    {
                        if (n.EndsWith("$")) continue;
                        var p = (k.GetValue(n) as string[] ?? new string[0]).FirstOrDefault(x => x.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))?.Substring(5).TrimEnd('\\');
                        if (string.IsNullOrEmpty(p) || p.Length <= len || !full.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                        best = "\\\\" + Environment.MachineName + "\\" + n + full.Substring(p.Length); len = p.Length;
                    }
            }
            catch (Exception) { }
            return best;
        }

        void Dodano(string plik, JellyfinConf jf, bool szukaj, string title, Window owner)
        {
            try { var l = LoadLibrary(); if (AddToLibrary(l, new[] { plik }, false) > 0) SaveLibrary(l); _libRefresh?.Invoke(); } catch (Exception ex) { App.LogError(ex); }
            if (jf != null)
            {   // Jellyfin skanuje bibliotekę; po chwili Velivo pobiera od niego opis i plakat
                ShowToast(L.T("🎞 Dodano do Jellyfin – skanuje bibliotekę, opis i plakat pojawią się za chwilę"), null);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using (var http = JellyfinClient(jf)) await http.PostAsync("Library/Refresh", null);
                        await Task.Delay(45000);
                        await Dispatcher.InvokeAsync(async () => { try { await SyncJellyfin(LoadJellyfinConf(), _ => { }); _libRefresh?.Invoke(); } catch (Exception ex) { App.LogInfo("Jellyfin po dodaniu filmu: " + ex.Message); } });
                    }
                    catch (Exception ex) { App.LogInfo("Jellyfin – skanowanie po dodaniu filmu: " + ex.Message); }
                });
                return;
            }
            ShowToast(L.T("🔒 Dodano do własnej biblioteki: ") + Path.GetFileName(plik), plik);
            if (szukaj) ShowMetaSearch(owner, plik, title, () => _libRefresh?.Invoke());
        }
    }
}
