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
                var bez = LoadLibrary().Where(v => File.Exists(v.Path) && WFolderze(v.Path) && LookupMeta(Meta, v.Path) == null && GetPref("serwer-brak|" + MetaKey(v.Path)) == null).ToList();
                int opis = 0;
                foreach (var v in bez)
                {
                    try { if (await SerwerOpis(v)) opis++; else SetPref("serwer-brak|" + MetaKey(v.Path), "1"); }   // nie znaleziono - nie szukamy co 10 min od nowa
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
                    id = (rok != null ? r.FirstOrDefault(x => x.D.Contains(rok)).Id : null) ?? (rok == null ? r.FirstOrDefault().Id : null);   // z rokiem - tylko pasujący rok (bez zgadywania)
                    if (id != null) break;
                }
            if (id == null) return false;
            var m = await WikiFilm(id, v.Path, null);
            var key = MetaKey(v.Path); var man = LoadManual(); man[key] = m;
            Directory.CreateDirectory(MetaDir); File.WriteAllText(ManualFile, JsonSerializer.Serialize(man));
            Meta[key] = m;
            return true;
        }

        // 📁 foldery, które serwer przegląda sam
        void SerwerFolderyOkno()
        {
            var w = new Window { Title = L.T("📁 Foldery z filmami – Velivo Serwer filmów"), Width = 560, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            Independent(w);
            var list = new ListBox { Margin = new Thickness(12, 0, 12, 0) };
            Action fill = () => { list.Items.Clear(); foreach (var f in SerwerFoldery()) list.Items.Add(f + (Directory.Exists(f) ? "" : L.T("   (niedostępny)"))); };
            var top = new TextBlock { Text = L.T("Serwer co 10 minut przegląda te foldery (z podfolderami): nowe filmy same trafiają do biblioteki i dostają opis, obsadę i plakat. Twoje komputery z Velivo widzą całą bibliotekę serwera."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12), Foreground = Brushes.Gray };
            var add = SmallButton(L.T("➕ Dodaj folder…"), () =>
            {
                var d = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Folder z filmami") };
                if (d.ShowDialog(w) != true) return;
                var l = SerwerFoldery(); if (!l.Contains(d.FolderName, StringComparer.OrdinalIgnoreCase)) l.Add(d.FolderName);
                SetPref("serwer-foldery", string.Join("|", l)); fill(); SerwerSkanuj(true);
            });
            var del = SmallButton(L.T("➖ Usuń zaznaczony"), () =>
            {
                int i = list.SelectedIndex; if (i < 0) return;
                var l = SerwerFoldery(); if (i < l.Count) l.RemoveAt(i);
                SetPref("serwer-foldery", string.Join("|", l)); fill();   // filmy zostają w bibliotece (usuwasz je w niej)
            });
            var now = SmallButton(L.T("🔄 Przejrzyj teraz"), () => SerwerSkanuj(true));
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
            bar.Children.Add(add); bar.Children.Add(del); bar.Children.Add(now);
            var dock = new DockPanel(); DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(bar, Dock.Bottom);
            dock.Children.Add(top); dock.Children.Add(bar); dock.Children.Add(list);
            w.Content = dock; fill(); w.Show(); w.Activate();
        }
    }
}
