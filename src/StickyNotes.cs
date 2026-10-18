using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Przegladarka
{
    // Karteczki (sticky notes): zolte karteczki na pulpicie - przesuwane, ze zmiana rozmiaru, opcjonalnie zawsze na wierzchu.
    // Dostepne zawsze (bez trybu bankowego). Zapis lokalny w karteczki.json; po uruchomieniu Velivo wracaja na swoje miejsca.
    // Wzory z ikonka: zwykla, lista zadan, zakupy, zadzwon, wazne, pomysl, na dzis. Listy wypunktowane (•), dwuklik w wiersz
    // odhacza (• -> ✓ -> •); 📅 wstawia date z kalendarza w miejscu kursora.
    public partial class MainWindow
    {
        internal sealed class StickyNote
        {
            public string Id { get; set; }
            public string Kind { get; set; }
            public string Text { get; set; }
            public double X { get; set; }
            public double Y { get; set; }
            public double W { get; set; }
            public double H { get; set; }
            public string Color { get; set; }
            public bool Top { get; set; }
            public bool Free { get; set; }   // odpieta - dowolnie na pulpicie; domyslnie przypieta nad paskiem zadan
        }

        // rodzaj -> ikona, tytul, poczatkowa tresc
        internal static readonly string[][] NoteKinds =
        {
            new[] { "note", "📝", "Karteczka", "" },
            new[] { "todo", "✅", "Do zrobienia", "• \n• \n• " },
            new[] { "shop", "🛒", "Zakupy", "• \n• \n• " },
            new[] { "call", "📞", "Zadzwoń", "" },
            new[] { "important", "❗", "Ważne", "" },
            new[] { "idea", "💡", "Pomysł", "" },
            new[] { "today", "⭐", "Na dziś", "• \n• \n• " },
        };

        static readonly string[] NoteColors = { "FFF59D", "FFCC80", "A5D6A7", "90CAF9", "F48FB1", "E1BEE7" };

        static string NotesFile { get { return Path.Combine(DataDir, "karteczki.json"); } }
        readonly List<StickyNote> _notes = new List<StickyNote>();
        readonly Dictionary<StickyNote, Window> _noteWindows = new Dictionary<StickyNote, Window>();
        DispatcherTimer _notesTimer;
        readonly HashSet<StickyNote> _noteOpen = new HashSet<StickyNote>();   // przypiete karteczki rozwiniete w gore
        bool _notesLayout;

        // przypiete karteczki: jedna obok drugiej tuz nad paskiem zadan (od lewej); zwinieta = sam pasek, rozwinieta - w gore
        void LayoutDockedNotes()
        {
            if (_notesLayout) return;
            _notesLayout = true;
            try
            {
                var wa = SystemParameters.WorkArea; double x = wa.Left + 8;
                foreach (var n in _notes.Where(q => !q.Free))
                {
                    if (!_noteWindows.TryGetValue(n, out var w)) continue;
                    double width = Math.Max(200, n.W), h = _noteOpen.Contains(n) ? Math.Max(120, n.H) : 32;   // zwinieta: caly pasek (30) + ramka
                    w.Width = width; w.Height = h; w.Left = x; w.Top = wa.Bottom - h;
                    x += width + 6;
                }
            }
            finally { _notesLayout = false; }
        }

        internal static string[] NoteKind(string kind) { return NoteKinds.FirstOrDefault(k => k[0] == kind) ?? NoteKinds[0]; }

        internal static List<StickyNote> ParseNotes(string json)
        {
            try
            {
                var list = (JsonSerializer.Deserialize<List<StickyNote>>(json ?? "") ?? new List<StickyNote>()).Where(n => n != null && !string.IsNullOrEmpty(n.Id)).ToList();
                foreach (var n in list) n.Text = (n.Text ?? "").Replace("☐", "•").Replace("☑", "✓");   // dawne kratki (zle wyswietlane) -> kropki
                return list;
            }
            catch (Exception) { return new List<StickyNote>(); }
        }

        // dwuklik w wiersz listy: • <-> ✓ (inne wiersze bez zmian)
        internal static string ToggleCheckLine(string text, int line)
        {
            var lines = (text ?? "").Split('\n');
            if (line < 0 || line >= lines.Length) return text;
            var l = lines[line];
            if (l.StartsWith("•")) lines[line] = "✓" + l.Substring(1);
            else if (l.StartsWith("✓")) lines[line] = "•" + l.Substring(1);
            else return text;
            return string.Join("\n", lines);
        }

        void LoadNotes()
        {
            try { if (File.Exists(NotesFile)) _notes.AddRange(ParseNotes(File.ReadAllText(NotesFile))); } catch (IOException) { }
            foreach (var n in _notes.ToList()) ShowNote(n);
        }

        void SaveNotesSoon()
        {
            if (_notesTimer == null)
            {
                _notesTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                _notesTimer.Tick += (s, e) => { _notesTimer.Stop(); SaveNotes(); };
            }
            _notesTimer.Stop(); _notesTimer.Start();
        }

        void SaveNotes()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                if (_notes.Count == 0) { if (File.Exists(NotesFile)) File.Delete(NotesFile); }
                else File.WriteAllText(NotesFile, JsonSerializer.Serialize(_notes, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (IOException) { }
        }

        void NewNote(string kind)
        {
            var k = NoteKind(kind);
            var wa = SystemParameters.WorkArea;
            int i = _notes.Count % 8;
            var n = new StickyNote
            {
                Id = Guid.NewGuid().ToString("N"), Kind = k[0], Text = k[3], W = 240, H = 220,
                X = wa.Right - 270 - i * 24, Y = wa.Top + 40 + i * 24, Color = NoteColors[0],
            };
            _notes.Add(n);
            SaveNotes();
            ShowNote(n);
        }

        void ShowAllNotes()
        {
            if (_notes.Count == 0) { NewNote("note"); return; }
            foreach (var n in _notes) if (_noteWindows.TryGetValue(n, out var w)) { if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal; w.Activate(); } else ShowNote(n);
        }

        void ShowNote(StickyNote n)
        {
            var k = NoteKind(n.Kind);
            Func<Color> col = () => { try { return (Color)ColorConverter.ConvertFromString("#" + n.Color); } catch (Exception) { return Color.FromRgb(0xFF, 0xF5, 0x9D); } };
            var w = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.CanResizeWithGrip, ShowInTaskbar = false, Topmost = n.Top,
                Width = Math.Max(160, n.W), Height = Math.Max(120, n.H), Left = n.X, Top = n.Y, Title = L.T(k[2]),
                Background = new SolidColorBrush(col()), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), BorderThickness = new Thickness(1),
                Tag = "own-bounds",   // miejsce i rozmiar ustawia karteczka (nie wspolna pamiec okien po tytule)
            };
            // bez bialej ramki Windows u gory (zjadala pol paska i klikniecia); brzegi dalej do zmiany rozmiaru
            System.Windows.Shell.WindowChrome.SetWindowChrome(w, new System.Windows.Shell.WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(5), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });
            // karteczka musi byc widoczna na ktoryms ekranie (np. po odlaczeniu monitora)
            if (w.Left < SystemParameters.VirtualScreenLeft || w.Left > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 60 ||
                w.Top < SystemParameters.VirtualScreenTop || w.Top > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40)
            { w.Left = SystemParameters.WorkArea.Right - w.Width - 30; w.Top = SystemParameters.WorkArea.Top + 40; }

            Func<string, string, Button> headBtn = (text, tip) => new Button
            {
                Content = text, ToolTip = tip, Width = 26, Height = 24, Padding = new Thickness(0), Margin = new Thickness(1, 0, 0, 0),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, FontSize = 13,
            };
            var title = new TextBlock { Text = k[1], ToolTip = L.T(k[2]), FontWeight = FontWeights.Bold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)) };
            var pin = headBtn(n.Top ? "📌" : "📍", L.T("Zawsze na wierzchu (włącz / wyłącz)"));
            var paint = headBtn("🎨", L.T("Kolor karteczki"));
            var add = headBtn("➕", L.T("Nowa karteczka"));
            var del = headBtn("✕", L.T("Usuń karteczkę"));
            var dock = headBtn("📎", L.T("Przypnij nad paskiem zadań / odepnij na pulpit"));
            var open = headBtn("▲", L.T("Rozwiń / zwiń karteczkę"));
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var b in new[] { open, dock, pin, paint, add, del }) btns.Children.Add(b);
            var head = new DockPanel { Height = 30, Background = new SolidColorBrush(Color.FromArgb(28, 0, 0, 0)), Cursor = Cursors.SizeAll };
            DockPanel.SetDock(btns, Dock.Right); head.Children.Add(btns); head.Children.Add(title);
            var box = new TextBox
            {
                Text = n.Text ?? "", AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontSize = 15, Padding = new Thickness(8, 6, 8, 6),
                FontFamily = new FontFamily("Segoe Print, Segoe UI"), Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
            };
            // 📅 ten sam kalendarzyk co przy datach w trybie bankowym - data wstawiana w miejscu kursora
            var date = DatePickerButton(head, () => null, v =>
            {
                int at = box.CaretIndex;
                box.Text = box.Text.Insert(at, v);
                box.Focus(); box.CaretIndex = at + v.Length;
            });
            date.Width = 26; date.MinWidth = 0; date.Height = 24; date.Padding = new Thickness(0); date.Margin = new Thickness(1, 0, 0, 0);
            date.Background = Brushes.Transparent; date.BorderThickness = new Thickness(0); date.Cursor = Cursors.Hand; date.FontSize = 13;
            date.ToolTip = L.T("Wstaw datę z kalendarza");
            btns.Children.Insert(0, date);
            var root = new DockPanel();
            DockPanel.SetDock(head, Dock.Top); root.Children.Add(head); root.Children.Add(box);
            w.Content = root;

            Action toggleOpen = () =>
            {
                if (n.Free) return;
                if (!_noteOpen.Remove(n)) _noteOpen.Add(n);
                open.Content = _noteOpen.Contains(n) ? "▼" : "▲";
                LayoutDockedNotes();
                if (_noteOpen.Contains(n)) { w.Activate(); box.Focus(); box.CaretIndex = box.Text.Length; }
            };
            // przypieta: klik w pasek rozwija / zwija w gore; odpieta: przeciaganie jak dotad
            head.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount != 1) return;
                if (n.Free) { try { w.DragMove(); } catch (InvalidOperationException) { } return; }
                toggleOpen();
            };
            head.ToolTip = n.Free ? null : L.T("Kliknij, aby rozwinąć / zwinąć karteczkę");
            open.Click += (s, e) => toggleOpen();
            open.Visibility = n.Free ? Visibility.Collapsed : Visibility.Visible;
            dock.Click += (s, e) =>
            {
                n.Free = !n.Free; _noteOpen.Remove(n);
                head.Cursor = n.Free ? Cursors.SizeAll : Cursors.Hand; open.Visibility = n.Free ? Visibility.Collapsed : Visibility.Visible; head.ToolTip = n.Free ? null : L.T("Kliknij, aby rozwinąć / zwinąć karteczkę");
                if (n.Free) { var wa = SystemParameters.WorkArea; w.Height = Math.Max(120, n.H); w.Top = Math.Max(wa.Top, wa.Bottom - w.Height - 40); }
                SaveNotes(); LayoutDockedNotes();
            };
            box.TextChanged += (s, e) => { n.Text = box.Text; SaveNotesSoon(); };
            box.PreviewMouseDoubleClick += (s, e) =>
            {
                int line = box.GetLineIndexFromCharacterIndex(box.CaretIndex);
                var t = ToggleCheckLine(box.Text, line);
                if (t == box.Text) return;
                int caret = box.CaretIndex; box.Text = t; box.CaretIndex = Math.Min(caret, t.Length); e.Handled = true;
            };
            w.LocationChanged += (s, e) => { if (!n.Free) return; n.X = w.Left; n.Y = w.Top; SaveNotesSoon(); };
            w.SizeChanged += (s, e) =>
            {
                if (_notesLayout) return;
                n.W = w.ActualWidth; if (n.Free || _noteOpen.Contains(n)) n.H = w.ActualHeight;
                SaveNotesSoon();
                if (!n.Free) LayoutDockedNotes();   // przypieta: dol zostaje przy pasku, sasiednie karteczki sie przesuwaja
            };
            pin.Click += (s, e) => { n.Top = !n.Top; w.Topmost = n.Top; pin.Content = n.Top ? "📌" : "📍"; SaveNotes(); };
            paint.Click += (s, e) =>
            {
                int i = Array.IndexOf(NoteColors, n.Color);
                n.Color = NoteColors[(i + 1) % NoteColors.Length];
                w.Background = new SolidColorBrush(col()); SaveNotes();
            };
            var kinds = new ContextMenu();
            foreach (var kk in NoteKinds) { var kind = kk[0]; var mi = new MenuItem { Header = kk[1] + "  " + L.T(kk[2]) }; mi.Click += (s, e) => NewNote(kind); kinds.Items.Add(mi); }
            add.ContextMenu = kinds;
            add.Click += (s, e) => { kinds.PlacementTarget = add; kinds.IsOpen = true; };
            del.Click += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(n.Text.Replace("•", "").Replace("✓", "")) &&
                    MessageBox.Show(w, L.T("Usunąć tę karteczkę?"), "Velivo", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                _notes.Remove(n); _noteOpen.Remove(n); SaveNotes(); w.Close(); LayoutDockedNotes();
            };
            w.Closed += (s, e) => _noteWindows.Remove(n);
            _noteWindows[n] = w;
            if (!n.Free) head.Cursor = Cursors.Hand;
            w.Show();
            if (n.Top) { w.Topmost = false; w.Topmost = true; }   // od razu nad innymi oknami (bez klikania ikonki)
            LayoutDockedNotes();
            if (!n.Free && (string.IsNullOrEmpty(n.Text) || n.Text == k[3])) { _noteOpen.Add(n); open.Content = "▼"; LayoutDockedNotes(); }   // nowa - od razu rozwinieta do pisania
            if (string.IsNullOrEmpty(n.Text) || n.Text == k[3]) { box.Focus(); box.CaretIndex = k[3].IndexOf('\n') is int nl && nl > 0 ? nl : box.Text.Length; }
        }

        // menu "🗒 Karteczki" (pasek kart, tryb bankowy): nowe karteczki ze wzorow + pokaz wszystkie
        MenuItem BuildNotesMenu()
        {
            var root = new MenuItem { Header = L.T("🗒 Karteczki na pulpit") };
            foreach (var kk in NoteKinds)
            {
                var kind = kk[0];
                var mi = new MenuItem { Header = kk[1] + "  " + L.T(kk[2]) };
                mi.Click += (s, e) => NewNote(kind);
                root.Items.Add(mi);
            }
            root.Items.Add(new Separator());
            var all = new MenuItem { Header = L.T("Pokaż wszystkie karteczki") };
            all.Click += (s, e) => ShowAllNotes();
            root.Items.Add(all);
            return root;
        }
    }
}
