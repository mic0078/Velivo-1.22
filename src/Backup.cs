using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Przegladarka
{
    // 🧹 Porzadki i kopia: czyszczenie smieci (pamiec stron, miniatury, stary program TV, ikony kanalow, logi)
    // i kopia bezpieczenstwa samych danych uzytkownika (bez smieci, ktore Velivo pobierze samo)
    public partial class MainWindow
    {
        static string DataRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Przegladarka"); } }
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
            if (full)
            {
                foreach (var p in parts.Take(parts.Length - 1).Select(x => x.ToLowerInvariant()))
                    if (p == "velivo-smieci" || p == "cache" || p == "code cache" || p == "gpucache" || p == "grshadercache" || p == "shadercache" || p == "dawncache" || p == "dawngraphitecache" || p == "dawnwebgpucache" || p == "crashpad") return false;
                return true;
            }
            foreach (var p in parts.Take(parts.Length - 1).Select(x => x.ToLowerInvariant()))
                if (p == "piper" || p == "narzedzia" || p == "miniatury stron" || p == "filtry" || p == "logo" || p == "velivo-smieci") return false;   // do pobrania na nowo
            int eb = Array.FindIndex(parts, x => x.Equals("EBWebView", StringComparison.OrdinalIgnoreCase));
            if (eb >= 0)   // dane silnika stron: tylko zalogowania (ciasteczka) i dane stron - bez pamieci podrecznej
                return parts.Skip(eb).Any(x => x.Equals("Network", StringComparison.OrdinalIgnoreCase) || x.Equals("Local Storage", StringComparison.OrdinalIgnoreCase));
            if (name.EndsWith(".epg")) return false;
            return true;
        }

        // kopia: zip z danymi; pliki zajete przez dzialajacy program czytane wspolnie (bez blokowania)
        static int CreateBackup(string zipPath, bool full)
        {
            int n = 0;
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath));
            var tmp = zipPath + ".part";
            using (var fs = new FileStream(tmp, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var f in Directory.EnumerateFiles(DataRoot, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(DataRoot, f);
                    if (!BackupInclude(rel, full)) continue;
                    try
                    {
                        using (var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var dst = zip.CreateEntry(rel.Replace('\\', '/'), CompressionLevel.Optimal).Open())
                            src.CopyTo(dst);
                        n++;
                    }
                    catch (Exception) { }   // plik zablokowany na wylacznosc - pomijamy
                }
            }
            if (File.Exists(zipPath)) File.Delete(zipPath);
            File.Move(tmp, zipPath);
            return n;
        }

        static void TrimBackups(string dir, int keep)
        {
            try
            {
                foreach (var f in new DirectoryInfo(dir).GetFiles(BackupPrefix + "*.zip").OrderByDescending(x => x.LastWriteTimeUtc).Skip(keep))
                    try { f.Delete(); } catch (Exception) { }
            }
            catch (Exception) { }
        }

        string BackupDir { get { var d = GetPref("kopia-folder"); return string.IsNullOrWhiteSpace(d) ? DefaultBackupDir : d; } }
        string NewBackupPath() { return Path.Combine(BackupDir, BackupPrefix + DateTime.Now.ToString("yyyy-MM-dd_HH-mm") + ".zip"); }

        // przywracanie: przy nastepnym starcie, zanim cokolwiek otworzy pliki danych (wolane z App przed oknem)
        internal static void ApplyPendingRestore()
        {
            try
            {
                if (!File.Exists(RestoreMarker)) return;
                var zipPath = File.ReadAllText(RestoreMarker).Trim();
                File.Delete(RestoreMarker);
                if (!File.Exists(zipPath)) return;
                using (var zip = ZipFile.OpenRead(zipPath))
                    foreach (var e in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(e.Name)) continue;
                        var dest = Path.GetFullPath(Path.Combine(DataRoot, e.FullName));
                        if (!dest.StartsWith(Path.GetFullPath(DataRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;   // tylko w folderze danych
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        e.ExtractToFile(dest, true);
                    }
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        // automatyczna kopia w tle (raz dziennie / w tygodniu), zostaje 5 ostatnich
        void AutoBackup()
        {
            int days = int.TryParse(GetPref("kopia-auto"), out var d) ? d : 7;
            if (days <= 0) return;
            DateTime last = DateTime.TryParse(GetPref("kopia-ostatnia"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var l) ? l : DateTime.MinValue;
            if (DateTime.Now - last < TimeSpan.FromDays(days)) return;
            var path = NewBackupPath(); var dir = BackupDir;
            bool full = GetPref("kopia-pelna") != "0";
            Task.Run(() => { CreateBackup(path, full); TrimBackups(dir, 5); }).ContinueWith(t =>
            {
                if (t.IsFaulted) { App.LogError(t.Exception.GetBaseException()); return; }
                Dispatcher.BeginInvoke(new Action(() => SetPref("kopia-ostatnia", DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture))));
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
            auto.Tick += (s, e) => { auto.Stop(); try { AutoBackup(); } catch (Exception ex) { App.LogError(ex); } };
            auto.Start();
        }

        Window _tidyWin;

        void ShowTidy()
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
                if (d.ShowDialog(w) == true) { SetPref("kopia-folder", d.FolderName); dirBox.Text = BackupDir; }
            };
            DockPanel.SetDock(pick, Dock.Right); dirRow.Children.Add(pick); dirRow.Children.Add(dirBox);
            sp.Children.Add(dirRow);
            var autoRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            autoRow.Children.Add(new TextBlock { Text = L.T("Kopia automatyczna: "), VerticalAlignment = VerticalAlignment.Center });
            var autoBox = new ComboBox { Width = 200 };
            var autoKeys = new[] { "1", "7", "0" };
            foreach (var t in new[] { "codziennie", "raz w tygodniu", "wyłączona" }) autoBox.Items.Add(L.T(t));
            autoBox.SelectedIndex = Math.Max(0, Array.IndexOf(autoKeys, GetPref("kopia-auto") ?? "7"));
            autoBox.SelectionChanged += (s, e) => SetPref("kopia-auto", autoKeys[Math.Max(0, autoBox.SelectedIndex)]);
            autoRow.Children.Add(autoBox);
            autoRow.Children.Add(new TextBlock { Text = L.T("  (zostaje 5 ostatnich)"), Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(autoRow);
            // rodzaj kopii: pelna albo same dane
            sp.Children.Add(new TextBlock { Text = L.T("Rodzaj kopii:"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 2) });
            Func<string, string, RadioButton> kind = (t, d) => new RadioButton { GroupName = "rodzaj-kopii", Margin = new Thickness(0, 2, 0, 2),
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Inlines = { new System.Windows.Documents.Run(t) { FontWeight = FontWeights.SemiBold }, new System.Windows.Documents.Run(" – " + d) { Foreground = Brushes.Gray } } } };
            var fullBox = kind(L.T("Pełna"), L.T("wszystko oprócz pamięci podręcznej: dane, program TV, ikony kanałów, głosy lektora, narzędzia, dane stron. Po przywróceniu nic do pobierania. Około 0,7–1 GB."));
            var dataBox = kind(L.T("Same dane"), L.T("hasła, zakładki, historia, ustawienia, profile, listy kanałów, ulubione, biblioteka filmów z okładkami, playlisty, tryb bankowy, logowania. Około 90 MB; resztę Velivo pobierze samo."));
            fullBox.IsChecked = GetPref("kopia-pelna") != "0"; dataBox.IsChecked = !fullBox.IsChecked;
            fullBox.Checked += (s, e) => SetPref("kopia-pelna", "1"); dataBox.Checked += (s, e) => SetPref("kopia-pelna", "0");
            sp.Children.Add(fullBox); sp.Children.Add(dataBox);
            var lastInfo = new TextBlock { Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 6) };
            Action showLast = () =>
            {
                try
                {
                    var f = Directory.Exists(BackupDir) ? new DirectoryInfo(BackupDir).GetFiles(BackupPrefix + "*.zip").OrderByDescending(x => x.LastWriteTime).FirstOrDefault() : null;
                    lastInfo.Text = f == null ? L.T("Jeszcze nie ma żadnej kopii.") : L.T("Ostatnia kopia: ") + f.LastWriteTime.ToString("dd.MM.yyyy HH:mm") + "  ·  " + Mb(f.Length);
                }
                catch (Exception) { lastInfo.Text = ""; }
            };
            sp.Children.Add(lastInfo);
            var bRow = new StackPanel { Orientation = Orientation.Horizontal };
            var make = new Button { Content = L.T("💾 Utwórz kopię teraz"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
            var restore = new Button { Content = L.T("♻ Przywróć z kopii…"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
            var open = new Button { Content = L.T("📁 Otwórz folder kopii"), Padding = new Thickness(14, 6, 14, 6) };
            make.Click += async (s, e) =>
            {
                make.IsEnabled = false; make.Content = L.T("Tworzę kopię…");
                var path = NewBackupPath(); var dir = BackupDir;
                try
                {
                    bool full = fullBox.IsChecked == true;
                    var n = await Task.Run(() => { var c = CreateBackup(path, full); TrimBackups(dir, 5); return c; });
                    SetPref("kopia-ostatnia", DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture));
                    ShowToast(L.T("💾 Kopia gotowa: ") + Path.GetFileName(path) + " (" + n + L.T(" plików, ") + Mb(new FileInfo(path).Length) + ")", path);
                }
                catch (Exception ex) { App.LogError(ex); InfoBox(w, L.T("Nie udało się utworzyć kopii:\n") + ex.Message); }
                make.IsEnabled = true; make.Content = L.T("💾 Utwórz kopię teraz"); showLast();
            };
            restore.Click += (s, e) =>
            {
                var d = new Microsoft.Win32.OpenFileDialog { Title = L.T("Kopia Velivo do przywrócenia"), Filter = L.T("Kopia Velivo") + "|*.zip" };
                if (Directory.Exists(BackupDir)) d.InitialDirectory = BackupDir;
                if (d.ShowDialog(w) != true) return;
                if (!AskYes(w, L.T("Przywrócić dane z kopii\n") + Path.GetFileName(d.FileName) + L.T("?\n\nVelivo uruchomi się ponownie. Dane z kopii zastąpią obecne (pliki, których nie ma w kopii, zostają)."), L.T("Przywróć"), L.T("Anuluj"))) return;
                try { File.WriteAllText(RestoreMarker, d.FileName); RestartVelivo(); }
                catch (Exception ex) { App.LogError(ex); InfoBox(w, ex.Message); }
            };
            open.Click += (s, e) => { try { Directory.CreateDirectory(BackupDir); System.Diagnostics.Process.Start("explorer.exe", "\"" + BackupDir + "\""); } catch (Exception) { } };
            bRow.Children.Add(make); bRow.Children.Add(restore); bRow.Children.Add(open);
            sp.Children.Add(bRow);
            w.Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height - 60 };
            refresh(); showLast();
            w.Show();
        }
    }
}
