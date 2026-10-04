using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Przegladarka
{
    // Import z innych przegladarek (Chrome, Edge, Brave, Opera, Vivaldi): zakladki czytane wprost z ich plikow,
    // hasla - przez eksport CSV (przegladarki szyfruja hasla tak, ze inny program nie moze ich odczytac).
    public partial class MainWindow
    {
        sealed class ImportSource
        {
            public string Browser, Profile, File, ExePath, PasswordsPage;
            public List<Bookmark> Items = new List<Bookmark>();
        }

        static IEnumerable<ImportSource> FindImportSources()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var browsers = new[]
            {
                new { Name = "Google Chrome", Dir = Path.Combine(local, "Google", "Chrome", "User Data"), Exe = "chrome.exe", Pass = "chrome://password-manager/settings" },
                new { Name = "Microsoft Edge", Dir = Path.Combine(local, "Microsoft", "Edge", "User Data"), Exe = "msedge.exe", Pass = "edge://wallet/passwords" },
                new { Name = "Brave", Dir = Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"), Exe = "brave.exe", Pass = "brave://password-manager/settings" },
                new { Name = "Vivaldi", Dir = Path.Combine(local, "Vivaldi", "User Data"), Exe = "vivaldi.exe", Pass = "vivaldi://settings/passwords" },
                new { Name = "Opera", Dir = Path.Combine(roaming, "Opera Software", "Opera Stable"), Exe = "opera.exe", Pass = "opera://settings/passwords" },
            };
            foreach (var b in browsers)
            {
                if (!Directory.Exists(b.Dir)) continue;
                var files = new List<KeyValuePair<string, string>>();
                if (File.Exists(Path.Combine(b.Dir, "Bookmarks"))) files.Add(new KeyValuePair<string, string>("", Path.Combine(b.Dir, "Bookmarks")));   // Opera
                IEnumerable<string> dirs;
                try { dirs = Directory.GetDirectories(b.Dir).Where(d => { var n = Path.GetFileName(d); return n == "Default" || n.StartsWith("Profile "); }); }
                catch (Exception) { dirs = new string[0]; }
                foreach (var d in dirs)
                    if (File.Exists(Path.Combine(d, "Bookmarks"))) files.Add(new KeyValuePair<string, string>(ProfileLabel(d), Path.Combine(d, "Bookmarks")));
                foreach (var f in files)
                {
                    var src = new ImportSource { Browser = b.Name, Profile = f.Key, File = f.Value, ExePath = FindBrowserExe(b.Exe), PasswordsPage = b.Pass };
                    try
                    {
                        using (var doc = JsonDocument.Parse(File.ReadAllText(f.Value)))
                        {
                            JsonElement roots;
                            if (doc.RootElement.TryGetProperty("roots", out roots))
                                foreach (var r in roots.EnumerateObject()) Collect(r.Value, src.Items);
                        }
                    }
                    catch (Exception) { continue; }
                    if (src.Items.Count > 0) yield return src;
                }
            }
        }

        // nazwa profilu z pliku "Preferences" (np. "Osoba 1", "Praca"), inaczej nazwa folderu
        static string ProfileLabel(string dir)
        {
            try
            {
                using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "Preferences"))))
                {
                    JsonElement p, n;
                    if (doc.RootElement.TryGetProperty("profile", out p) && p.TryGetProperty("name", out n) && n.ValueKind == JsonValueKind.String) return n.GetString();
                }
            }
            catch (Exception) { }
            return Path.GetFileName(dir);
        }

        static void Collect(JsonElement node, List<Bookmark> into)
        {
            if (node.ValueKind != JsonValueKind.Object) return;
            JsonElement type, url, name, children;
            if (node.TryGetProperty("type", out type) && type.GetString() == "url" && node.TryGetProperty("url", out url))
            {
                var u = url.GetString() ?? "";
                if (u.StartsWith("http://") || u.StartsWith("https://"))
                    into.Add(new Bookmark { Url = u, Title = node.TryGetProperty("name", out name) && !string.IsNullOrWhiteSpace(name.GetString()) ? name.GetString() : u });
            }
            if (node.TryGetProperty("children", out children) && children.ValueKind == JsonValueKind.Array)
                foreach (var c in children.EnumerateArray()) Collect(c, into);
        }

        static string FindBrowserExe(string exe)
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (var rel in new[] { @"Google\Chrome\Application", @"Microsoft\Edge\Application", @"BraveSoftware\Brave-Browser\Application", @"Vivaldi\Application", @"Programs\Opera" })
                {
                    var p = Path.Combine(root, rel, exe);
                    if (File.Exists(p)) return p;
                }
            }
            return null;
        }

        void ShowBrowserImport(Window owner)
        {
            List<ImportSource> sources;
            try { sources = FindImportSources().ToList(); } catch (Exception ex) { App.LogError(ex); sources = new List<ImportSource>(); }

            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = L.T("Przenieś zakładki i hasła z innej przeglądarki"), FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });

            // 1. zakladki
            panel.Children.Add(new TextBlock { Text = L.T("1. Zakładki – Velivo odczyta je samo"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 4) });
            var checks = new List<KeyValuePair<CheckBox, ImportSource>>();
            if (sources.Count == 0)
                panel.Children.Add(new TextBlock { Text = L.T("Nie znaleziono zakładek Chrome, Edge, Brave, Opery ani Vivaldi na tym komputerze."), Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap });
            foreach (var s in sources)
            {
                var cb = new CheckBox
                {
                    Content = s.Browser + (string.IsNullOrEmpty(s.Profile) ? "" : " – " + s.Profile) + "   (" + s.Items.Select(i => i.Url).Distinct().Count() + (L.En ? " bookmarks)" : " zakładek)"),
                    IsChecked = s.Browser == "Google Chrome" || sources.Count == 1, Margin = new Thickness(0, 3, 0, 3)
                };
                checks.Add(new KeyValuePair<CheckBox, ImportSource>(cb, s));
                panel.Children.Add(cb);
            }
            var importBtn = new Button { Content = L.T("Importuj zaznaczone zakładki"), IsDefault = true, Padding = new Thickness(14, 5, 14, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), IsEnabled = sources.Count > 0 };
            var result = new TextBlock { Foreground = Brushes.SeaGreen, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(importBtn);
            panel.Children.Add(result);
            importBtn.Click += (s, e) =>
            {
                var have = new HashSet<string>(_bookmarks.Select(b => b.Url), StringComparer.Ordinal);
                int added = 0;
                foreach (var kv in checks.Where(k => k.Key.IsChecked == true))
                    foreach (var b in kv.Value.Items)
                        if (have.Add(b.Url)) { _bookmarks.Add(new Bookmark { Url = b.Url, Title = b.Title }); added++; }
                if (added > 0) SaveBookmarks();
                result.Text = added > 0
                    ? (L.En ? "Imported " + added + " bookmarks. All of them are under 📚 All bookmarks on the toolbar." : "Zaimportowano zakładek: " + added + ". Wszystkie są pod 📚 Wszystkie zakładki na pasku.")
                    : L.T("Nic nowego – te zakładki już są w Velivo.");
            };

            // 2. hasla
            panel.Children.Add(new TextBlock { Text = L.T("2. Hasła – przez plik CSV (2 minuty)"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 4) });
            panel.Children.Add(new TextBlock
            {
                Text = L.T("Przeglądarki szyfrują hasła tak, że żaden inny program nie może ich odczytać – to dla Twojego bezpieczeństwa. Dlatego hasła trzeba raz wyeksportować do pliku:"),
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 6)
            });
            panel.Children.Add(new TextBlock
            {
                Text = L.T("a) kliknij „Otwórz eksport haseł” poniżej (otworzy się tamta przeglądarka),\nb) wybierz „Eksportuj hasła” i zapisz plik,\nc) wróć tu i kliknij „Wczytaj plik CSV…”,\nd) usuń plik CSV – hasła są w nim zapisane zwykłym tekstem."),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
            });
            var row = new WrapPanel();
            foreach (var g in sources.Where(x => x.ExePath != null).GroupBy(x => x.Browser))
            {
                var src = g.First();
                var open = new Button { Content = (L.En ? "Open password export: " : "Otwórz eksport haseł: ") + src.Browser, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 6) };
                open.Click += (s, e) =>
                {
                    try { var psi = new ProcessStartInfo(src.ExePath) { UseShellExecute = false }; psi.ArgumentList.Add(src.PasswordsPage); Process.Start(psi); }
                    catch (Exception ex) { MessageBox.Show(owner, ex.Message, "Velivo"); }
                };
                row.Children.Add(open);
            }
            var csv = new Button { Content = L.T("Wczytaj plik CSV…"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 6) };
            csv.Click += (s, e) => ImportPasswordsCsvWithDialog(owner);
            row.Children.Add(csv);
            panel.Children.Add(row);
            panel.Children.Add(new TextBlock
            {
                Text = L.T("Logowania do stron (np. konto Google) nie przenoszą się – zaloguj się raz w Velivo, a Velivo je zapamięta."),
                TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(0, 10, 0, 0)
            });
            var close = new Button { Content = L.T("Zamknij"), IsCancel = true, Padding = new Thickness(14, 5, 14, 5), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            panel.Children.Add(close);

            var w = new Window
            {
                Title = L.T("Import z innej przeglądarki"), Width = 620, SizeToContent = SizeToContent.Height, MaxHeight = 760,
                Owner = owner ?? this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
            };
            close.Click += (s, e) => w.Close();
            w.ShowDialog();
        }
    }
}
