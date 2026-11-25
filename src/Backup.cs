using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Velivo
{
    // 🧹 Porzadki i kopia: czyszczenie smieci (pamiec stron, miniatury, stary program TV, ikony kanalow, logi)
    // i kopia bezpieczenstwa samych danych uzytkownika (bez smieci, ktore Velivo pobierze samo)
    public partial class MainWindow
    {
        static string DataRoot { get { return VelivoData.Root; } }
        static string RestoreMarker { get { return Path.Combine(DataRoot, "przywroc-kopie.txt"); } }
        static string DefaultBackupDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Velivo - kopie"); } }
        const string BackupPrefix = "Velivo-kopia-";
        Button _tidyBtn;

        // co trafia do kopii (sciezka wzgledem folderu danych, z "/"): dane tak, smieci nie
        // full = pelna kopia: wszystko poza prawdziwymi smieciami (pamiec podreczna stron, logi, pliki tymczasowe) - po przywroceniu nic do pobierania
        internal static bool BackupInclude(string rel, bool full = false)
        {
            var r = rel.Replace('\\', '/');
            var parts = r.Split('/');
            var name = parts[parts.Length - 1].ToLowerInvariant();
            if (name.EndsWith(".tmp") || name.EndsWith(".part") || name == "vlc.log" || name == "dziala.txt" || name == "przywroc-kopie.txt" || name.StartsWith("sesja.txt")) return false;
            if (parts[0].Equals("Komunikator-WebView", StringComparison.OrdinalIgnoreCase)) return false;
            if (parts[0].Equals("Xtts", StringComparison.OrdinalIgnoreCase) && name != "moj-glos.wav") return false;   // lektor XTTS: Python + PyTorch + model 3–6 GB - pobiera się na nowo, w kopii tylko puchł (każda kopia ~8 GB)   // silnik rozmów wideo osobnego Komunikatora - setki plików bez wartości do odtworzenia
            if (full)
            {
                foreach (var p in parts.Take(parts.Length - 1).Select(x => x.ToLowerInvariant()))
                    if (p == "velivo-smieci" || p == "cache" || p == "code cache" || p == "gpucache" || p == "grshadercache" || p == "shadercache" || p == "dawncache" || p == "dawngraphitecache" || p == "dawnwebgpucache" || p == "crashpad") return false;
                return true;
            }
            foreach (var p in parts.Take(parts.Length - 1).Select(x => x.ToLowerInvariant()))
                if (p == "piper" || p == "supertonic" || p == "narzedzia" || p == "miniatury stron" || p == "filtry" || p == "logo" || p == "velivo-smieci") return false;   // do pobrania na nowo
            int eb = Array.FindIndex(parts, x => x.Equals("EBWebView", StringComparison.OrdinalIgnoreCase));
            if (eb >= 0)   // dane silnika stron: tylko zalogowania (ciasteczka) i dane stron - bez pamieci podrecznej
                return parts.Skip(eb).Any(x => x.Equals("Network", StringComparison.OrdinalIgnoreCase) || x.Equals("Local Storage", StringComparison.OrdinalIgnoreCase));
            if (name.EndsWith(".epg")) return false;
            return true;
        }

        // kopia: zip z danymi; pliki zajete przez dzialajacy program czytane wspolnie (bez blokowania)
        // kopia przyrostowa: w kazdej kopii spis plikow (sciezka, rozmiar, data zmiany); przyrost zawiera tylko pliki zmienione / nowe
        // od poprzedniej kopii i liste usunietych; nazwa konczy sie na "-przyrost"
        const string IncSuffix = "-przyrost";
        internal static bool IsIncrement(string zipPath) { return Path.GetFileNameWithoutExtension(zipPath).EndsWith(IncSuffix, StringComparison.OrdinalIgnoreCase); }

        internal static Dictionary<string, string> ReadManifest(string zipPath)
        {
            var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    var e = zip.GetEntry("_manifest.txt"); if (e == null) return null;   // kopia sprzed przyrostow
                    using (var r = new StreamReader(e.Open()))
                        for (string l; (l = r.ReadLine()) != null;) { var t = l.Split('\t'); if (t.Length == 2) m[t[0]] = t[1]; }
                }
            }
            catch (Exception) { return null; }
            return m;
        }

        // lancuch do przywrocenia wybranej kopii: najblizsza wczesniejsza pelna i po kolei przyrosty do wybranej (wlacznie)
        internal static List<string> RestoreChain(IEnumerable<string> allZips, string chosen)
        {
            var sorted = allZips.OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToList();   // nazwy zawieraja date - kolejnosc chronologiczna
            int ci = sorted.FindIndex(f => string.Equals(f, chosen, StringComparison.OrdinalIgnoreCase));
            if (ci < 0 || !IsIncrement(chosen)) return new List<string> { chosen };
            int fi = sorted.FindLastIndex(ci, x => !IsIncrement(x));
            if (fi < 0) return null;   // brak pelnej kopii, od ktorej zaczyna sie ten przyrost
            return sorted.Skip(fi).Take(ci - fi + 1).ToList();
        }

        // już skompresowane (filmy, zdjęcia, głosówki, archiwa - np. skrzynka serwera z filmami) - bez ponownej kompresji: tyle samo miejsca, wielokrotnie szybciej
        static readonly HashSet<string> BackupPackedExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".m4a", ".mp3", ".ogg", ".opus", ".aac", ".zip", ".7z", ".rar", ".gz", ".apk", ".exe", ".bin", ".pdf" };
        internal static bool BackupPacked(string f) => BackupPackedExt.Contains(Path.GetExtension(f)) || (new FileInfo(f) is var fi && fi.Exists && fi.Length > 50L << 20);   // > 50 MB - bez kompresji (inaczej kopia trwa bez końca)

        static int CreateBackup(string zipPath, bool full, Dictionary<string, string> prev = null, Action<int> progress = null)
        {
            int n = 0;
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath));
            var tmp = zipPath + ".part";
            var manifest = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
            using (var fs = new FileStream(tmp, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var przen = PrzenosnaStart(zip);   // hasło przeniesienia ustawione - pliki konta Windows także w postaci do przeniesienia
                var own = Path.GetFullPath(Path.GetDirectoryName(zipPath)).TrimEnd('\\') + "\\";
                foreach (var f in Directory.EnumerateFiles(DataRoot, "*", SearchOption.AllDirectories))
                {
                    // nigdy kopii w kopii: folder kopii w folderze danych (albo stara kopia w danych) - każda kopia zawierała poprzednie i rosła bez końca
                    if (f.StartsWith(own, StringComparison.OrdinalIgnoreCase) || (Path.GetFileName(f).StartsWith(BackupPrefix, StringComparison.OrdinalIgnoreCase) && f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))) continue;
                    var rel = Path.GetRelativePath(DataRoot, f);
                    if (!BackupInclude(rel, full)) continue;
                    var ow = KopiaSkladniki.Wlasciciel(rel); if (ow != null && !KopiaSkladniki.WKopii(ow.Id)) continue;   // program wyłączony z kopii (Centrum / 🧹)
                    var key = rel.Replace('\\', '/'); string stamp;
                    try { var fi = new FileInfo(f); stamp = fi.Length + ":" + fi.LastWriteTimeUtc.Ticks; } catch (Exception) { continue; }
                    manifest.Add(key + "\t" + stamp); seen.Add(key);
                    if (prev != null && prev.TryGetValue(key, out var old) && old == stamp) continue;   // przyrost: bez zmian - pomijamy
                    try
                    {
                        using (var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var dst = zip.CreateEntry(rel.Replace('\\', '/'), BackupPacked(f) ? CompressionLevel.NoCompression : CompressionLevel.Optimal).Open())
                            src.CopyTo(dst);
                        PrzenosnaDodaj(przen, zip, key, f);
                        n++; if (n % 25 == 0) progress?.Invoke(n);
                    }
                    catch (Exception) { }   // plik zablokowany na wylacznosc - pomijamy
                }
                // Audio Balance, Monitor: własne foldery obok danych - w kopii jako _inne/<program>/; pozostałe foldery programów (Procesor, Kursory, każdy nowy moduł) - _folder/<folder>/
                var inne = KopiaSkladniki.Wszystkie.Where(x => x.Inny != null && KopiaSkladniki.WKopii(x.Id)).Select(sk => (Root: KopiaSkladniki.FolderInny(sk), Pre: "_inne/" + sk.Id + "/"))
                    .Concat(KopiaSkladniki.FolderyModulow(DataRoot).Select(d => (Root: d, Pre: "_folder/" + Path.GetFileName(d) + "/"))).ToList();
                foreach (var (root, pre) in inne)
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        if (f.StartsWith(own, StringComparison.OrdinalIgnoreCase) || !KopiaSkladniki.PlikModulu(Path.GetFileName(root), Path.GetRelativePath(root, f))) continue;   // nigdy kopii w kopii; bez dzienników i zapisów tego komputera
                        var key = pre + Path.GetRelativePath(root, f).Replace('\\', '/'); string stamp;
                        try { var fi = new FileInfo(f); stamp = fi.Length + ":" + fi.LastWriteTimeUtc.Ticks; } catch (Exception) { continue; }
                        manifest.Add(key + "\t" + stamp); seen.Add(key);
                        if (prev != null && prev.TryGetValue(key, out var old) && old == stamp) continue;
                        try
                        {
                            using (var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                            using (var dst = zip.CreateEntry(key, CompressionLevel.Optimal).Open())
                                src.CopyTo(dst);
                            n++; if (n % 25 == 0) progress?.Invoke(n);
                        }
                        catch (Exception) { }
                    }
                }
                PrzenosnaKoniec(zip);   // ścieżki tego konta i ustawienia z rejestru - przeniesienie na inny komputer
                using (var w = new StreamWriter(zip.CreateEntry("_manifest.txt").Open())) foreach (var l in manifest) w.WriteLine(l);
                if (prev != null)   // pliki usuniete od poprzedniej kopii - przy przywracaniu tez znikna
                    using (var w = new StreamWriter(zip.CreateEntry("_usuniete.txt").Open())) foreach (var k in prev.Keys.Where(k => !seen.Contains(k))) w.WriteLine(k);
                // do odtworzenia wszystkiego z jednego folderu: instalator Velivo (w pelnej), instrukcja i wlaczone moduly
                var setup = Path.Combine(AppContext.BaseDirectory, "Velivo-instalator.exe");
                if (prev == null && File.Exists(setup)) try { zip.CreateEntryFromFile(setup, "_Instalator/Velivo-instalator.exe", CompressionLevel.NoCompression); } catch (Exception) { }
                using (var w = new StreamWriter(zip.CreateEntry("_Instalator/JAK ODTWORZYC.txt").Open(), new System.Text.UTF8Encoding(true)))
                    w.Write(L.T("Jak odtworzyć Velivo z tej kopii\r\n\r\n1. Rozpakuj z tego pliku ZIP folder _Instalator i uruchom Velivo-instalator.exe (jeśli Velivo nie jest zainstalowane).\r\n2. W Velivo kliknij 🧹 na górnym pasku → „♻ Przywróć z kopii…” i wskaż ten plik ZIP.\r\n3. Velivo uruchomi się ponownie z Twoimi danymi i ustawieniami.\r\n\r\nHasła są zaszyfrowane dla konta Windows – przywracaj na tym samym koncie.\r\n"));
                if (File.Exists(IptvMarker)) zip.CreateEntry("_Instalator/iptv.on");   // modul TV byl wlaczony
            }
            }
            catch (Exception) { try { File.Delete(tmp); } catch (Exception) { } throw; }   // nieudana kopia - bez resztki .part
            if (File.Exists(zipPath)) File.Delete(zipPath);
            File.Move(tmp, zipPath);
            return n;
        }

        // zostaje "keep" ostatnich pelnych kopii razem z ich przyrostami; starsze lancuchy znikaja w calosci (nigdy przyrost bez swojej pelnej)
        static void TrimBackups(string dir, int keep)
        {
            try
            {
                var all = new DirectoryInfo(dir).GetFiles(BackupPrefix + "*.zip").OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var fulls = all.Where(x => !IsIncrement(x.Name)).ToList();
                if (fulls.Count <= keep) { foreach (var x in all.TakeWhile(x => IsIncrement(x.Name))) try { x.Delete(); } catch (Exception) { } return; }   // przyrosty bez pelnej na poczatku
                var oldestKept = fulls[fulls.Count - keep];
                foreach (var f in all.Where(x => string.Compare(x.Name, oldestKept.Name, StringComparison.OrdinalIgnoreCase) < 0))
                    try { f.Delete(); } catch (Exception) { }
            }
            catch (Exception) { }
        }

        // ustawienia kopii: rejestr (wspólny z Velivo Centrum), zapasowo dawne ustawienie Velivo
        static string KP(string k) => KopiaSkladniki.Get(k) ?? GetPref(k);
        static void KPSet(string k, string v) { KopiaSkladniki.Set(k, v); SetPref(k, v); }
        string BackupDir { get { var d = KP("kopia-folder"); return string.IsNullOrWhiteSpace(d) ? DefaultBackupDir : d; } }
        string NewBackupPath(bool inc = false) { return Path.Combine(BackupDir, BackupPrefix + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + (inc ? IncSuffix : "") + ".zip"); }

        // jedna kopia: inc == null - wg ustawien (przyrostowa, gdy wlaczona i jest pelna z ostatnich 7 dni), true - przyrostowa na zyczenie
        // (gdy jest od czego - inaczej pelna), false - pelna na zyczenie; zwraca sciezke i liczbe plikow
        (string Path, int Count) MakeBackupNow(bool? inc = null, Action<int> progress = null)
        {
            bool full = KP("kopia-pelna") != "0", incOn = inc ?? KP("kopia-przyrost") != "0"; var dir = BackupDir;
            if (Directory.Exists(dir))   // resztki przerwanych kopii (program zamknięty w trakcie) - kopia zapisywana teraz jest otwarta i się nie usunie
                foreach (var p in new DirectoryInfo(dir).GetFiles(BackupPrefix + "*.zip.part")) try { p.Delete(); } catch (Exception) { }
            Dictionary<string, string> prev = null;
            if (incOn && Directory.Exists(dir))
            {
                var all = new DirectoryInfo(dir).GetFiles(BackupPrefix + "*.zip").OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var lastFull = all.LastOrDefault(x => !IsIncrement(x.Name));
                var kind = KP("kopia-rodzaj-ostatniej"); bool sameKind = kind == null || kind == (full ? "pelna" : "podstawowa");   // brak zapisu (inny program Velivo) - przyrost, nie nowa pelna   // zmiana rodzaju - nowa pelna kopia
                if (lastFull != null && (DateTime.Now - lastFull.LastWriteTime < TimeSpan.FromDays(7) || inc == true) && sameKind) prev = ReadManifest(all.Last().FullName);
            }
            var path = NewBackupPath(prev != null);
            int n = CreateBackup(path, full, prev, progress);
            TrimBackups(dir, 5);
            KPSet("kopia-rodzaj-ostatniej", full ? "pelna" : "podstawowa");
            return (path, n);
        }

        // gdzie wraca plik z kopii: _inne/<program>/... - folder programu obok danych, reszta - folder danych; poza nimi - nic
        static string RestoreTarget(string key)
        {
            string root = DataRoot, rel = key;
            if (key.StartsWith("_inne/", StringComparison.OrdinalIgnoreCase))
            {
                var p = key.Split(new[] { '/' }, 3); var sk = p.Length == 3 ? KopiaSkladniki.Wszystkie.FirstOrDefault(x => x.Inny != null && x.Id == p[1]) : null;
                if (sk == null) return null;
                root = KopiaSkladniki.FolderInny(sk); rel = p[2];
            }
            else if (key.StartsWith("_folder/", StringComparison.OrdinalIgnoreCase))
            {   // folder programu obok danych (Procesor, Kursory, …) - tylko zwykła nazwa folderu, nigdy ścieżka
                var p = key.Split(new[] { '/' }, 3);
                if (p.Length != 3 || p[1].Length == 0 || p[1] == "." || p[1] == ".." || p[1].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
                root = Path.Combine(KopiaSkladniki.Lokal, p[1]); rel = p[2];
            }
            var dest = Path.GetFullPath(Path.Combine(root, rel));
            return dest.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? dest : null;
        }

        // przywracanie: przy nastepnym starcie, zanim cokolwiek otworzy pliki danych (wolane z App przed oknem)
        internal static void ApplyPendingRestore()
        {
            try
            {
                if (!File.Exists(RestoreMarker)) return;
                var lines = File.ReadAllLines(RestoreMarker).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                var kluczP = KluczZeZnacznika(lines);   // klucz przeniesienia (inne konto / komputer) - z hasła albo klucza sprzętowego
                var chain = lines.Where(x => !x.StartsWith("#")).ToList();   // pelna + przyrosty po kolei
                File.Delete(RestoreMarker);
                var bez = new List<string>(); int przen = 0;
                foreach (var zipPath in chain.Where(File.Exists))
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    var map = SciezkiMapa(zip);   // stare konto / foldery → obecne
                    foreach (var e in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(e.Name) || e.FullName == "_manifest.txt" || e.FullName == "_usuniete.txt" || PrzenosnyWpis(e.FullName)) continue;
                        if (e.FullName.StartsWith("_Instalator/", StringComparison.OrdinalIgnoreCase))
                        {   // nie dane: instalator i instrukcja zostaja w kopii; modul TV - wlaczany, jak byl
                            if (e.Name == "iptv.on" && VlcInstalled && !File.Exists(IptvMarker)) try { File.WriteAllText(IptvMarker, ""); } catch (Exception) { }
                            continue;
                        }
                        var dest = RestoreTarget(e.FullName); if (dest == null) continue;   // tylko w folderze danych (i folderach Audio Balance / Monitora)
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        e.ExtractToFile(dest, true);
                        SciezkiWPliku(dest, map);
                    }
                    przen += PrzenosnaPrzywroc(zip, kluczP, map, bez);
                    RejestrPrzywroc(zip, map);
                    AutostartPrzywroc(zip, map);
                    var del = zip.GetEntry("_usuniete.txt");   // przyrost: pliki usuniete od poprzedniej kopii
                    if (del != null)
                        using (var r = new StreamReader(del.Open()))
                            for (string l; (l = r.ReadLine()) != null;)
                            {
                                var t = RestoreTarget(l);
                                if (t != null) try { File.Delete(t); } catch (Exception) { }
                            }
                }
                bez = bez.Distinct().ToList();
                App.LogInfo("Przywrócono kopię: " + chain.Count + " plik(ów) kopii; pliki konta z hasła przeniesienia: " + przen + (bez.Count > 0 ? "; NIE przeniesione (brak / złe hasło przeniesienia): " + string.Join(", ", bez) : ""));
                if (bez.Count > 0) MessageBox.Show("Velivo przywróciło kopię, ale tych danych nie dało się odszyfrować na tym koncie Windows (brak hasła / klucza przeniesienia):\n\n• " + string.Join("\n• ", bez.Take(12)) + "\n\nPrzywróć kopię jeszcze raz (Velivo Centrum albo 🧹) i otwórz ją hasłem albo kluczem sprzętowym przeniesienia.", "Velivo", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        // 🧩 kopia zlecona przez Velivo Centrum: w tle, postęp w kopia-stan.txt (Centrum go czyta); zakoncz - program uruchomiony tylko do kopii
        static bool _kopiaTrwa;
        internal void KopiaZCentrum(bool? inc, bool zakoncz)   // inc null - wg ustawień (przyrostowa, gdy włączona i jest świeża pełna)
        {
            var stan = Path.Combine(DataRoot, KopiaSkladniki.StanPlik);
            void Pisz(string t) { try { File.WriteAllText(stan, t); } catch (Exception) { } }
            if (_kopiaTrwa) return;   // już trwa - Centrum pokazuje jej postęp
            _kopiaTrwa = true; Pisz("trwa|0");
            Task.Run(() => MakeBackupNow(inc, k => Pisz("trwa|" + k))).ContinueWith(t =>
            {
                _kopiaTrwa = false;
                if (t.IsFaulted) { App.LogError(t.Exception.GetBaseException()); Pisz("blad|" + t.Exception.GetBaseException().Message.Replace('\n', ' ')); }
                else { Pisz("gotowe|" + t.Result.Path + "|" + t.Result.Count); KPSet("kopia-ostatnia", DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture)); }
                if (zakoncz) Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
            });
        }

        // automatyczna kopia w tle (raz dziennie / w tygodniu), zostaje 5 ostatnich
        void AutoBackup()
        {
            foreach (var k in new[] { "kopia-folder", "kopia-auto", "kopia-pelna", "kopia-przyrost" })   // dawne ustawienia Velivo - do rejestru, żeby widziało je Velivo Centrum
                if (KopiaSkladniki.Get(k) == null && GetPref(k) != null) KopiaSkladniki.Set(k, GetPref(k));
            int days = int.TryParse(KP("kopia-auto"), out var d) ? d : 7;
            if (days <= 0) return;
            DateTime last = DateTime.TryParse(KP("kopia-ostatnia"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var l) ? l : DateTime.MinValue;
            try { if (Directory.Exists(BackupDir)) foreach (var z in new DirectoryInfo(BackupDir).GetFiles(BackupPrefix + "*.zip")) if (z.LastWriteTime > last) last = z.LastWriteTime; } catch (Exception) { }   // data z samych kopii: wspólna dla wszystkich programów Velivo (zapis ustawienia mógł przepaść przy zamknięciu w trakcie)
            if (DateTime.Now - last < TimeSpan.FromDays(days)) return;
            Task.Run(() => MakeBackupNow()).ContinueWith(t =>
            {
                if (t.IsFaulted) { App.LogError(t.Exception.GetBaseException()); return; }
                Dispatcher.BeginInvoke(new Action(() => KPSet("kopia-ostatnia", DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture))));
            });
        }

        void InitTidyButton()
        {
            _tidyBtn = new Button { Content = "🧹", FontSize = 16, Width = 40, Height = 36, Margin = new Thickness(2, 3, 2, 0), ToolTip = L.T("Porządki i kopia bezpieczeństwa – czyszczenie śmieci, kopia i przywracanie danych") };
            DockPanel.SetDock(_tidyBtn, Dock.Right);
            _tidyBtn.Click += (s, e) => ShowTidy();
            int bank = TabBarPanel.Children.IndexOf(_bankBtn);
            TabBarPanel.Children.Insert(bank >= 0 ? bank : 1, _tidyBtn);
            var auto = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };   // kopia chwile po starcie, nie w trakcie
            auto.Tick += (s, e) => { auto.Stop(); try { if (VelivoData.Jeden("kopia")) AutoBackup(); } catch (Exception ex) { App.LogError(ex); } };
            auto.Start();
        }

        Window _tidyWin;

        internal void ShowTidy()
        {
            if (_tidyWin != null) { if (_tidyWin.WindowState == WindowState.Minimized) _tidyWin.WindowState = WindowState.Normal; _tidyWin.Activate(); return; }
            var w = new Window { Title = L.T("🧹 Porządki i kopia bezpieczeństwa"), Width = 560, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.CanMinimize, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            _tidyWin = w; w.Closed += (s, e) => _tidyWin = null;
            Independent(w);
            var sp = new StackPanel { Margin = new Thickness(16) };
            Func<string, TextBlock> head = t => new TextBlock { Text = t, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 6) };
            Func<string, TextBlock> note = t => new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 8) };

            // --- czyszczenie ---
            sp.Children.Add(head(L.T("🧹 Czyszczenie śmieci")));
            sp.Children.Add(note(L.T("Usuwa tylko to, co Velivo pobierze lub zbuduje samo. Hasła, zakładki, logowania na stronach, listy kanałów i biblioteka zostają.")));
            var items = new List<(CheckBox Box, Func<long> Size, Func<long> Clean)>();
            Func<string, IEnumerable<string>> files = dir => Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories) : Enumerable.Empty<string>();
            Func<IEnumerable<string>, long> sum = fs => fs.Sum(f => { try { return new FileInfo(f).Length; } catch (Exception) { return 0L; } });
            Func<IEnumerable<string>, long> del = fs => { long n = 0; foreach (var f in fs.ToList()) try { var len = new FileInfo(f).Length; File.Delete(f); n += len; } catch (Exception) { } return n; };
            Action<string, Func<long>, Func<long>, bool> add = (label, size, clean, on) =>
            {
                var cb = new CheckBox { IsChecked = on, Margin = new Thickness(0, 2, 0, 2) };
                items.Add((cb, size, clean));
                sp.Children.Add(cb);
                cb.Tag = label;
            };
            add(L.T("Pamięć podręczna stron (cache, skrypty, grafika)"), JunkSize, () => { var b = JunkSize(); _ = CleanJunkNow(); return b; }, true);
            add(L.T("Miniatury stron (szybki dostęp – odtworzą się)"), () => DirSize(PageThumbsDir), () => del(files(PageThumbsDir)), true);
            add(L.T("Ikony kanałów TV (pobiorą się na nowo)"), () => DirSize(LogoDir), () => { var n = del(files(LogoDir)); _logoMem.Clear(); return n; }, false);
            add(L.T("Stary program TV (wczyta się przy odświeżeniu listy)"), () => sum(files(IptvDataDir).Where(f => f.EndsWith(".epg", StringComparison.OrdinalIgnoreCase))),
                () => del(files(IptvDataDir).Where(f => f.EndsWith(".epg", StringComparison.OrdinalIgnoreCase))), false);
            add(L.T("Dzienniki (vlc.log, logi pobierania)"), () => sum(files(DataRoot).Where(f => f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) && !f.EndsWith("bledy.log", StringComparison.OrdinalIgnoreCase))),
                () => del(files(DataRoot).Where(f => f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) && !f.EndsWith("bledy.log", StringComparison.OrdinalIgnoreCase))), true);
            var total = new TextBlock { Margin = new Thickness(0, 6, 0, 6) };
            Action refresh = () =>
            {
                long all = 0;
                foreach (var it in items) { long s0 = it.Size(); all += s0; it.Box.Content = (string)it.Box.Tag + "  –  " + Mb(s0); }
                total.Text = L.T("Folder danych Velivo: ") + Mb(DirSize(DataRoot)) + L.T("  ·  śmieci z listy: ") + Mb(all);
            };
            sp.Children.Add(total);
            var cleanBtn = new Button { Content = L.T("🧹 Wyczyść zaznaczone"), Padding = new Thickness(14, 6, 14, 6), HorizontalAlignment = HorizontalAlignment.Left };
            cleanBtn.Click += async (s, e) =>
            {
                cleanBtn.IsEnabled = false; long freed = 0;
                foreach (var it in items.Where(x => x.Box.IsChecked == true).ToList()) { var c = it.Clean; freed += await Task.Run(() => { try { return c(); } catch (Exception) { return 0L; } }); }
                refresh(); cleanBtn.IsEnabled = true;
                ShowToast(L.T("🧹 Zwolniono ") + Mb(freed) + L.T(" (część pamięci stron zniknie przy następnym uruchomieniu)"), null);
            };
            sp.Children.Add(cleanBtn);

            // --- kopia ---
            sp.Children.Add(new Separator { Margin = new Thickness(0, 14, 0, 8) });
            sp.Children.Add(head(L.T("💾 Kopia bezpieczeństwa")));
            sp.Children.Add(note(L.T("Hasła zostają w kopii zaszyfrowane – kopię przywrócisz na tym samym koncie Windows.")));
            var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var dirBox = new TextBox { Text = BackupDir, IsReadOnly = true, Padding = new Thickness(4) };
            var pick = new Button { Content = L.T("Zmień folder…"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
            pick.Click += (s, e) =>
            {
                var d = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Folder na kopie Velivo (najlepiej inny dysk albo pendrive)") };
                if (Directory.Exists(BackupDir)) d.InitialDirectory = BackupDir;
                if (d.ShowDialog(w) == true) { KPSet("kopia-folder", d.FolderName); dirBox.Text = BackupDir; }
            };
            DockPanel.SetDock(pick, Dock.Right); dirRow.Children.Add(pick); dirRow.Children.Add(dirBox);
            sp.Children.Add(dirRow);
            var autoRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            autoRow.Children.Add(new TextBlock { Text = L.T("Kopia automatyczna: "), VerticalAlignment = VerticalAlignment.Center });
            var autoBox = new ComboBox { Width = 200 };
            var autoKeys = new[] { "1", "7", "0" };
            foreach (var t in new[] { "codziennie", "raz w tygodniu", "wyłączona" }) autoBox.Items.Add(L.T(t));
            autoBox.SelectedIndex = Math.Max(0, Array.IndexOf(autoKeys, KP("kopia-auto") ?? "7"));
            autoBox.SelectionChanged += (s, e) => KPSet("kopia-auto", autoKeys[Math.Max(0, autoBox.SelectedIndex)]);
            autoRow.Children.Add(autoBox);
            autoRow.Children.Add(new TextBlock { Text = L.T("  (zostaje 5 ostatnich)"), Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(autoRow);
            // rodzaj kopii: pelna albo same dane
            sp.Children.Add(new TextBlock { Text = L.T("Rodzaj kopii:"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 2) });
            Func<string, string, RadioButton> kind = (t, d) => new RadioButton { GroupName = "rodzaj-kopii", Margin = new Thickness(0, 2, 0, 2),
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Inlines = { new System.Windows.Documents.Run(t) { FontWeight = FontWeights.SemiBold }, new System.Windows.Documents.Run(" – " + d) { Foreground = Brushes.Gray } } } };
            var fullBox = kind(L.T("Pełna"), L.T("wszystko bez śmieci: dane, program TV, ikony kanałów, głosy lektora, narzędzia, dane stron. Po przywróceniu nic do pobierania. Około 0,7–1 GB."));
            var dataBox = kind(L.T("Podstawowa"), L.T("same dane bez śmieci: hasła, zakładki, historia, ustawienia, profile, listy kanałów, ulubione, biblioteka filmów z okładkami, playlisty, tryb bankowy, logowania. Około 90 MB; resztę Velivo pobierze samo."));
            fullBox.IsChecked = KP("kopia-pelna") != "0"; dataBox.IsChecked = !fullBox.IsChecked;
            fullBox.Checked += (s, e) => KPSet("kopia-pelna", "1"); dataBox.Checked += (s, e) => KPSet("kopia-pelna", "0");
            sp.Children.Add(dataBox); sp.Children.Add(fullBox);
            var incBox = new CheckBox { Margin = new Thickness(0, 6, 0, 6), IsChecked = KP("kopia-przyrost") != "0",
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = L.T("Kopie przyrostowe – pełna kopia co 7 dni, pozostałe tylko ze zmianami (zwykle kilka MB). Przy przywracaniu Velivo samo składa pełną z przyrostami.") } };
            incBox.Click += (s, e) => KPSet("kopia-przyrost", incBox.IsChecked == true ? "1" : "0");
            sp.Children.Add(incBox);
            var lastInfo = new TextBlock { Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 6) };
            Action showLast = () =>
            {
                try
                {
                    var f = Directory.Exists(BackupDir) ? new DirectoryInfo(BackupDir).GetFiles(BackupPrefix + "*.zip").OrderByDescending(x => x.LastWriteTime).FirstOrDefault() : null;
                    lastInfo.Text = f == null ? L.T("Jeszcze nie ma żadnej kopii.") : L.T("Ostatnia kopia: ") + f.LastWriteTime.ToString("dd.MM.yyyy HH:mm") + "  ·  " + Mb(f.Length) + (IsIncrement(f.Name) ? L.T("  ·  przyrostowa") : L.T("  ·  pełna"));
                }
                catch (Exception) { lastInfo.Text = ""; }
            };
            sp.Children.Add(lastInfo);
            var bRow = new WrapPanel();
            var make = new Button { Content = L.T("💾 Kopia przyrostowa teraz"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 6), ToolTip = L.T("Tylko zmiany od ostatniej kopii (zwykle kilka MB).\nGdy nie ma jeszcze pełnej kopii tego rodzaju, powstanie pełna.") };
            var makeFull = new Button { Content = L.T("💾 Pełna kopia teraz"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 6), ToolTip = L.T("Cała kopia od nowa – np. przed dużą zmianą albo do przeniesienia na inny dysk.") };
            var restore = new Button { Content = L.T("♻ Przywróć z kopii…"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 6) };
            var open = new Button { Content = L.T("📁 Otwórz folder kopii"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 0, 6) };
            RoutedEventHandler doMake = async (s, e) =>
            {
                var b = (Button)s; var label = b.Content; bool inc = b == make;
                make.IsEnabled = makeFull.IsEnabled = false; b.Content = L.T("Tworzę kopię…");
                try
                {
                    var r = await Task.Run(() => MakeBackupNow(inc, k => Dispatcher.BeginInvoke(new Action(() => b.Content = L.T("Tworzę kopię…") + " " + k))));   // postęp - widać, że idzie
                    KPSet("kopia-ostatnia", DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture));
                    ShowToast((IsIncrement(r.Path) ? L.T("💾 Kopia przyrostowa gotowa: ") : L.T("💾 Kopia pełna gotowa: ")) + Path.GetFileName(r.Path) + " (" + r.Count + L.T(" plików, ") + Mb(new FileInfo(r.Path).Length) + ")", r.Path);
                }
                catch (Exception ex) { App.LogError(ex); InfoBox(w, L.T("Nie udało się utworzyć kopii:\n") + ex.Message); }
                make.IsEnabled = makeFull.IsEnabled = true; b.Content = label; showLast();
            };
            make.Click += doMake; makeFull.Click += doMake;
            restore.Click += (s, e) =>
            {
                var d = new Microsoft.Win32.OpenFileDialog { Title = L.T("Kopia Velivo do przywrócenia"), Filter = L.T("Kopia Velivo") + "|*.zip" };
                if (Directory.Exists(BackupDir)) d.InitialDirectory = BackupDir;
                if (d.ShowDialog(w) == true) PrzywrocKopie(d.FileName, w);
            };
            open.Click += (s, e) => { try { Directory.CreateDirectory(BackupDir); System.Diagnostics.Process.Start("explorer.exe", "\"" + BackupDir + "\""); } catch (Exception) { } };
            bRow.Children.Add(make); bRow.Children.Add(makeFull); bRow.Children.Add(restore); bRow.Children.Add(open);
            sp.Children.Add(bRow);
            w.Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height - 60 };
            refresh(); showLast();
            w.Show();
        }
    }
}
