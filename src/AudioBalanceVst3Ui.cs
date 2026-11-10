using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Velivo
{
    // 🎛 VST3 w Audio Monitorze: lista wtyczek (kolejność, wł./wył., okno producenta, usuwanie) i zapis łańcucha ze stanem każdej wtyczki (vst3.json)
    public partial class MainWindow
    {
        sealed class VstItem { public string Path { get; set; } public string Cid { get; set; } public bool On { get; set; } = true; public string State { get; set; } }
        sealed class VstConf { public bool Active { get; set; } public List<VstItem> List { get; set; } = new List<VstItem>(); }
        static string VstFile => System.IO.Path.Combine(DataDir, "vst3.json");
        static bool _vstLoaded; static readonly List<string> _vstErrors = new List<string>();

        // przy pierwszym uruchomieniu odtwarzacza z Audio Balance - wczytanie łańcucha (wtyczki z błędem pomijane, powód w Błędach Velivo)
        // onlyIfActive: start odtwarzacza - wtyczki wczytywane tylko, gdy łańcuch jest aktywny (wyłączony VST3 nie ładuje obcych bibliotek)
        static void VstLoad(bool onlyIfActive = false)
        {
            if (_vstLoaded || !Edycja.Premium) return;   // 💎 VST3 - Premium (bezpłatne Velivo nie wczytuje wtyczek)
            VstConf c = null;
            try { if (System.IO.File.Exists(VstFile)) c = JsonSerializer.Deserialize<VstConf>(System.IO.File.ReadAllText(VstFile)); } catch (Exception ex) { App.LogError(ex); }
            if (c == null) { _vstLoaded = true; return; }
            if (onlyIfActive && !c.Active) return;
            _vstLoaded = true;
            // bezpiecznik: wtyczka, przy której Velivo padło w trakcie wczytywania, nie jest wczytywana ponownie (inaczej padałoby przy każdym starcie)
            var mark = System.IO.Path.Combine(DataDir, "vst3-wczytuje.txt"); string crashed = null;
            try { if (System.IO.File.Exists(mark)) { crashed = System.IO.File.ReadAllText(mark).Trim(); System.IO.File.Delete(mark); } } catch (Exception ex) { App.LogError(ex); }
            var list = new List<Vst3.Plugin>(); bool dropped = false;
            foreach (var it in c.List)
            {
                if (crashed != null && string.Equals(it.Path, crashed, StringComparison.OrdinalIgnoreCase))
                {
                    _vstErrors.Add(System.IO.Path.GetFileName(it.Path) + ": " + L.T("zamknęła Velivo przy wczytywaniu – usunięta z łańcucha"));
                    App.LogError(new Exception("VST3: wtyczka zamknęła Velivo przy wczytywaniu - usunięta: " + it.Path)); dropped = true; continue;
                }
                try
                {
                    try { System.IO.File.WriteAllText(mark, it.Path); } catch (Exception) { }
                    var p = Vst3.Plugin.Load(it.Path, it.Cid); p.On = it.On;
                    if (!string.IsNullOrEmpty(it.State)) p.SetState(Convert.FromBase64String(it.State));
                    list.Add(p);
                }
                catch (Exception ex) { _vstErrors.Add(System.IO.Path.GetFileName(it.Path) + ": " + ex.Message); App.LogError(new Exception("VST3: " + it.Path, ex)); }
                finally { try { System.IO.File.Delete(mark); } catch (Exception) { } }
            }
            Vst3.Chain = list.ToArray(); Vst3.Active = c.Active && list.Count > 0;
            if (dropped) VstSave();
        }

        // zapis łańcucha i stanu każdej wtyczki (ustawienia gałek, presety)
        static void VstSave()
        {
            if (!_vstLoaded) return;
            try
            {
                var c = new VstConf { Active = Vst3.Active };
                foreach (var p in Vst3.Chain) { byte[] st = null; try { st = p.GetState(); } catch (Exception ex) { App.LogError(ex); } c.List.Add(new VstItem { Path = p.Path, Cid = p.Cid, On = p.On, State = st == null ? null : Convert.ToBase64String(st) }); }
                AbWriteLatest(VstFile, JsonSerializer.Serialize(c));
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        // zmiana łańcucha w oknie - pod blokadą wątku dźwięku (blok dźwięku nie trafi na wtyczkę w trakcie usuwania)
        static void VstSet(Vst3.Plugin[] chain, Vst3.Plugin removed = null)
        {
            lock (Vst3.Lock) { Vst3.Chain = chain; if (chain.Length == 0) Vst3.Active = false; }
            removed?.Dispose(); VstSave();
        }

        FrameworkElement VstPanel(Window owner, Brush fg, Brush dim, Func<string, Action, Button> mk)
        {
            VstLoad();
            var root = new StackPanel();
            TextBlock T(string s, double size, Brush c) => new TextBlock { Text = s, FontSize = size, Foreground = c, TextWrapping = TextWrapping.Wrap };
            var on = new CheckBox { Content = L.T("VST3 aktywne – Twoje wtyczki przejmują przetwarzanie dźwięku filmu"), Foreground = fg, IsChecked = Vst3.Active, Margin = new Thickness(0, 0, 0, 4) };
            root.Children.Add(on);
            var note = T(L.T("Gdy VST3 jest aktywne: oryginał → Twoje wtyczki po kolei → głośność odtwarzacza. Korektor, dialogi i wyrównywanie Velivo są wtedy pominięte (bez podwójnego przetwarzania). Wtyczka, która się wysypie, może zamknąć Velivo."), 11, dim);
            note.Margin = new Thickness(0, 0, 0, 8); root.Children.Add(note);
            var errs = T("", 11, AbRed); root.Children.Add(errs);
            var list = new StackPanel { Margin = new Thickness(0, 0, 0, 8) }; root.Children.Add(list);
            void Refresh()
            {
                errs.Text = _vstErrors.Count == 0 ? "" : L.T("Nie wczytano: ") + string.Join("; ", _vstErrors); errs.Visibility = _vstErrors.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                list.Children.Clear(); var ch = Vst3.Chain;
                if (ch.Length == 0) list.Children.Add(T(L.T("Brak wtyczek. Dodaj pierwszą przyciskiem poniżej."), 12, dim));
                for (int i = 0; i < ch.Length; i++)
                {
                    var p = ch[i]; int idx = i;
                    var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                    var cb = new CheckBox { IsChecked = p.On, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), ToolTip = L.T("Włącz / wyłącz tę wtyczkę") };
                    cb.Click += (s, e) => { p.On = cb.IsChecked == true; VstSave(); };
                    var btns = new StackPanel { Orientation = Orientation.Horizontal };
                    Button B(string t, string tip, Action a) { var b = mk(t, a); b.Margin = new Thickness(4, 0, 0, 0); b.Padding = new Thickness(8, 2, 8, 2); b.ToolTip = tip; btns.Children.Add(b); return b; }
                    B("▲", L.T("Wyżej w łańcuchu"), () => { if (idx == 0) return; var l = Vst3.Chain.ToList(); (l[idx - 1], l[idx]) = (l[idx], l[idx - 1]); VstSet(l.ToArray()); Refresh(); }).IsEnabled = idx > 0;
                    B("▼", L.T("Niżej w łańcuchu"), () => { var l = Vst3.Chain.ToList(); if (idx >= l.Count - 1) return; (l[idx + 1], l[idx]) = (l[idx], l[idx + 1]); VstSet(l.ToArray()); Refresh(); }).IsEnabled = idx < ch.Length - 1;
                    B(L.T("Okno"), L.T("Otwiera okno wtyczki (ustawienia producenta)"), () => { try { p.ShowEditor(p.Name); } catch (Exception ex) { ShowToast("🎛 " + ex.Message, null); } });
                    B(L.T("Usuń"), L.T("Usuwa wtyczkę z łańcucha"), () => { VstSet(Vst3.Chain.Where(x => x != p).ToArray(), p); Refresh(); on.IsChecked = Vst3.Active; });
                    DockPanel.SetDock(cb, Dock.Left); DockPanel.SetDock(btns, Dock.Right); row.Children.Add(cb); row.Children.Add(btns);
                    row.Children.Add(new TextBlock { Text = (idx + 1) + ". " + p.Name + (p.Latency > 0 ? "  (" + Math.Round(p.Latency * 1000.0 / AbProcessor.Fs) + " ms)" : ""), Foreground = fg, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = p.Path });
                    list.Children.Add(row);
                }
            }
            void Add(string path)
            {
                try
                {
                    var p = Vst3.Plugin.Load(path); VstSet(Vst3.Chain.Concat(new[] { p }).ToArray());
                    if (!Vst3.Active) { Vst3.Active = true; on.IsChecked = true; VstSave(); AbNeedsRestartHint(); }
                    Refresh();
                }
                catch (Exception ex) { App.LogError(new Exception("VST3: " + path, ex)); ShowToast(L.T("🎛 Nie udało się wczytać wtyczki: ") + ex.Message, null); }
            }
            on.Click += (s, e) =>
            {
                if (on.IsChecked == true && Vst3.Chain.Length == 0) { on.IsChecked = false; ShowToast(L.T("🎛 Najpierw dodaj wtyczkę"), null); return; }
                Vst3.Active = on.IsChecked == true; VstSave(); if (Vst3.Active) AbNeedsRestartHint();
            };
            var brow = new WrapPanel();
            var addScan = mk(L.T("Dodaj z folderu VST3…"), () => { });
            addScan.ToolTip = L.T("Wtyczki zainstalowane w Windows (Common Files\\VST3)");
            addScan.Click += (s, e) =>
            {
                var found = Vst3.Scan(); var cm = new ContextMenu { PlacementTarget = addScan };
                if (found.Count == 0) cm.Items.Add(new MenuItem { Header = L.T("Nie znaleziono wtyczek VST3 w folderze Common Files\\VST3"), IsEnabled = false });
                foreach (var f in found) { var ff = f; var mi = new MenuItem { Header = System.IO.Path.GetFileNameWithoutExtension(f), ToolTip = f }; mi.Click += (a, b) => Add(ff); cm.Items.Add(mi); }
                cm.IsOpen = true;
            };
            var addFile = mk(L.T("Dodaj z pliku…"), () =>
            {
                var d = new Microsoft.Win32.OpenFileDialog { Filter = L.T("Wtyczki VST3") + " (*.vst3)|*.vst3", Title = L.T("Wybierz wtyczkę VST3"), InitialDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST3") };
                if (d.ShowDialog(owner) == true) Add(d.FileName);
            });
            brow.Children.Add(addScan); brow.Children.Add(addFile); root.Children.Add(brow);
            Refresh();
            return root;
        }
    }
}
