using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Velivo
{
    // okno i zasobnik programu Velivo Audio Balance for Windows
    sealed class AbMain : IDisposable
    {
        static readonly string SetFile = Path.Combine(App.DataDir, "ustawienia.json");
        Dictionary<string, string> set = new Dictionary<string, string>();
        internal AbEngine Eng => eng;
        internal string Get(string k) => set.TryGetValue(k, out var v) ? v : null;
        internal void Put(string k, string v) { set[k] = v; try { Directory.CreateDirectory(App.DataDir); File.WriteAllText(SetFile, JsonSerializer.Serialize(set)); } catch (Exception ex) { App.LogError(ex); } }

        readonly AbEngine eng = new AbEngine();
        Window win; System.Windows.Forms.NotifyIcon tray; DispatcherTimer tm; EventWaitHandle showEv;
        static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1F)), Fg = new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xF0)), Dim = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xB4)),
            BtnBg = new SolidColorBrush(Color.FromRgb(0x24, 0x27, 0x34)), Acc = new SolidColorBrush(Color.FromRgb(0x7C, 0x5C, 0xFF)), Ok = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)), Bad = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));

        internal static void SignalShow() { try { using var ev = EventWaitHandle.OpenExisting("VelivoAudioBalance.Pokaz"); ev.Set(); } catch (Exception) { } }

        public AbMain(bool hidden)
        {
            try { if (File.Exists(SetFile)) set = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SetFile)) ?? set; } catch (Exception ex) { App.LogError(ex); }
            Apply();
            BuildWindow(); BuildTray();
            showEv = new EventWaitHandle(false, EventResetMode.AutoReset, "VelivoAudioBalance.Pokaz");
            new Thread(() => { while (showEv != null) { try { if (showEv.WaitOne(1000)) win.Dispatcher.BeginInvoke(new Action(Show)); } catch (Exception) { return; } } }) { IsBackground = true }.Start();
            eng.Stopped += () => win.Dispatcher.BeginInvoke(new Action(() => { Status(); retryAt = DateTime.UtcNow.AddSeconds(3); }));
            if (Get("wlaczony") != "0") StartEngine();
            if (!hidden || !eng.Running) Show();
        }

        DateTime retryAt = DateTime.MaxValue;
        void StartEngine() { eng.Start(Get("wyjscie")); Status(); }

        // ustawienia → silnik
        internal void Toast(string t) { status.Text = t; status.Foreground = Ok; }
        // okienko z polem tekstowym (nazwa ustawienia) - nad oknem, z którego wywołane
        internal string Ask(string text, string def, Window owner)
        {
            var w = new Window { Title = "Velivo Audio Balance", Owner = owner ?? win, Topmost = (owner ?? win)?.Topmost == true, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Background = Bg, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var p = new StackPanel { Margin = new Thickness(14), MinWidth = 300 }; p.Children.Add(T(text));
            var tb = new TextBox { Text = def ?? "", Margin = new Thickness(0, 4, 0, 10) }; p.Children.Add(tb);
            string res = null; var bw = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            bw.Children.Add(Mk("OK", () => { res = tb.Text; w.Close(); })); bw.Children.Add(Mk(L.T("Anuluj"), () => w.Close())); p.Children.Add(bw);
            w.Content = p; w.Loaded += (s, e) => { tb.Focus(); tb.SelectAll(); }; w.ShowDialog(); return res;
        }
        double[] EqDb() { var c = (Get("eq-db") ?? "").Split(','); var g = new double[10]; for (int i = 0; i < 10 && i < c.Length; i++) double.TryParse(c[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out g[i]); return g; }
        // 🎛 korektor 10 pasm (jak w Velivo): włącznik, suwaki ±12 dB, presety
        FrameworkElement EqPanel()
        {
            var p = new StackPanel();
            var on = new CheckBox { Style = AbStyl.Sw, IsChecked = Get("eq") == "1", ToolTip = L.T("Korektor włączony") };
            on.Click += (s, e) => { Put("eq", on.IsChecked == true ? "1" : "0"); eng.Proc.EqOn = on.IsChecked == true; }; p.Children.Add(Head(L.T("🎛 Korektor (EQ)"), on));
            var grid = new Grid(); var cur = EqDb(); var sl = new Slider[10];
            for (int i = 0; i < 10; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                double fq = AbProcessor.EqFreq[i]; var sp = new StackPanel();
                sl[i] = new Slider { Orientation = Orientation.Vertical, Minimum = -12, Maximum = 12, Value = cur[i], Height = 110, HorizontalAlignment = HorizontalAlignment.Center, TickFrequency = 1, IsSnapToTickEnabled = true, ToolTip = fq + " Hz" };
                var vt = T(cur[i].ToString("+0;-0;0"), 10, Fg); vt.HorizontalAlignment = HorizontalAlignment.Center; sp.Children.Add(vt); var sli = sl[i]; sli.ValueChanged += (s2, e2) => vt.Text = sli.Value.ToString("+0;-0;0");
                sp.Children.Add(sl[i]); var lab = T(fq >= 1000 ? (fq / 1000) + "k" : fq.ToString(), 10, Dim); lab.HorizontalAlignment = HorizontalAlignment.Center; sp.Children.Add(lab);
                Grid.SetColumn(sp, i); grid.Children.Add(sp);
                sl[i].ValueChanged += (s, e) => { var g = sl.Select(x => x.Value).ToArray(); eng.Proc.SetEq(g); Put("eq-db", string.Join(",", g.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)))); };
            }
            p.Children.Add(grid);
            var pr = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var (nm, g) in new[] { (L.T("Płasko"), new double[10]), (L.T("Dialogi"), new double[] { -4, -3, -1, 0, 2, 4, 4, 2, 0, -1 }), (L.T("Bas"), new double[] { 6, 5, 3, 1, 0, 0, 0, 0, 0, 0 }), (L.T("Muzyka"), new double[] { 3, 2, 0, -1, -1, 0, 1, 2, 3, 3 }) })
            { var gg = g; pr.Children.Add(Mk(nm, () => { for (int i = 0; i < 10; i++) sl[i].Value = gg[i]; })); }
            p.Children.Add(pr); return p;
        }
        void Apply()
        {
            eng.Latency = int.TryParse(Get("opoznienie"), out var lat) ? lat : 1;
            var p = eng.Proc;
            p.Profile = AbProfile.ByName(Get("profil") ?? "auto", Get("sila") ?? "normal");
            p.Target = float.TryParse(Get("cel"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 0f;
            p.Dialog = int.TryParse(Get("dialogi"), out var d) ? d : 0;
            p.Volume = 1; p.CeilingDb = -1; p.FilmLong = false;   // dźwięk Windows jak kanał TV - poziom z ostatnich ok. 30 s (zmiana programu, reklamy)
            p.Bypass = Get("obejscie") == "1";
            p.EqOn = Get("eq") == "1"; p.SetEq(EqDb());
            var pre = Get("kompresor"); p.Pre.Params = pre == null ? DynParams.Film() : DynParams.Load(pre);
        }

        TextBlock cableTxt; bool cableBusy;
        TextBlock status; AbLive live; ComboBox outBox; Border setupBox; Button defBtn;
        // własny wygląd przycisku: tekst zawsze czytelny (zwykły szablon Windows robi biały przycisk po najechaniu)
        static readonly ControlTemplate BtnTpl = MkTpl();
        static ControlTemplate MkTpl()
        {
            var t = new ControlTemplate(typeof(Button)); var bd = new FrameworkElementFactory(typeof(Border), "bd");
            bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            bd.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter)); cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center); cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp); t.VisualTree = bd;
            var hov = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hov.Setters.Add(new Setter(UIElement.OpacityProperty, 0.82, "bd")); t.Triggers.Add(hov);
            var pr = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true }; pr.Setters.Add(new Setter(UIElement.OpacityProperty, 0.65, "bd")); t.Triggers.Add(pr);
            var dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false }; dis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4, "bd")); t.Triggers.Add(dis);
            return t;
        }
        Button Mk(string text, Action a, string tip = null) { var b = new Button { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Fg }, Template = BtnTpl, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(12, 6, 12, 6), Background = BtnBg, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = tip }; b.Click += (s, e) => a(); return b; }
        static void SetText(Button b, string t) { if (b.Content is TextBlock tb) tb.Text = t; }
        static string GetText(Button b) => (b.Content as TextBlock)?.Text;
        Border Card(UIElement c) => new Border { Background = Card2, CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 10), Child = c };
        static readonly Brush Card2 = new SolidColorBrush(Color.FromRgb(0x1B, 0x1E, 0x2A));
        TextBlock T(string s, double size = 12, Brush c = null, bool bold = false) => new TextBlock { Text = s, FontSize = size, Foreground = c ?? Fg, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        // wybór jako jeden pasek z podświetloną opcją (makieta)
        Grid Choice(string label, (string key, string text, string tip)[] items, Func<string> cur, Action<string> pick)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) }); g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new TextBlock { Text = label, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            var seg = new StackPanel { Orientation = Orientation.Horizontal }; var box = new Border { Background = Seg, CornerRadius = new CornerRadius(8), Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left, Child = seg };
            Grid.SetColumn(box, 1); g.Children.Add(box);
            var bs = new List<(Button b, string k)>();
            void Mark() { foreach (var (b, k) in bs) b.Background = k == cur() ? Acc : Brushes.Transparent; }
            foreach (var (k, t, tip) in items) { var kk = k; var b = Mk(t, () => { pick(kk); Mark(); }, tip); b.Margin = new Thickness(0); b.Padding = new Thickness(9, 4, 9, 4); bs.Add((b, kk)); seg.Children.Add(b); }
            Mark(); return g;
        }
        static readonly Brush Seg = new SolidColorBrush(Color.FromRgb(0x22, 0x26, 0x34));
        Grid Head(string text, UIElement right = null)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 10) }; g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = text, Foreground = Fg, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            if (right is FrameworkElement r) { r.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(r, 1); g.Children.Add(r); }
            return g;
        }

        void BuildWindow()
        {
            win = new Window { Title = "Velivo Audio Balance for Windows", Width = Math.Min(1280, SystemParameters.WorkArea.Width - 40), MinWidth = 1000, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.CanResize, Background = Bg, Foreground = Fg, WindowStartupLocation = WindowStartupLocation.CenterScreen, FontFamily = new FontFamily("Segoe UI") };
            win.Resources.MergedDictionaries.Add(AbStyl.Res);
            try { win.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/audiobalance.ico")); } catch (Exception) { }
            var root = new StackPanel { Margin = new Thickness(14, 10, 14, 12) };
            // górny pasek w jednej linii: nazwa, stan, głośniki, przełącznik „cały dźwięk przez Audio Balance”, zasobnik
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = true };
            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            try { title.Children.Add(new Image { Source = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/audiobalance.ico")), Width = 24, Height = 24, Margin = new Thickness(0, 0, 10, 0) }); } catch (Exception) { }
            title.Children.Add(new TextBlock { Text = "Velivo Audio Balance", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) });
            DockPanel.SetDock(title, Dock.Left); top.Children.Add(title);
            var rightTop = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            outBox = new ComboBox { Width = 230, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = L.T("Głośniki / słuchawki (tu trafia poprawiony dźwięk)") };
            outBox.SelectionChanged += (s, e) => { if (outBox.SelectedItem is ComboBoxItem it && (string)it.Tag != Get("wyjscie")) { Put("wyjscie", (string)it.Tag); StartEngine(); } };
            defBtn = Mk("", ToggleDefault); defBtn.Margin = new Thickness(0, 0, 8, 0);
            rightTop.Children.Add(outBox); rightTop.Children.Add(defBtn);
            var tr = Mk(L.T("Do zasobnika"), () => win.Hide()); tr.Margin = new Thickness(0, 0, 6, 0); rightTop.Children.Add(tr);
            var ex = Mk(L.T("Zakończ"), Exit, L.T("Zamyka program; jeśli CABLE jest domyślny, Windows wraca do Twoich głośników.")); ex.Margin = new Thickness(0); rightTop.Children.Add(ex);
            DockPanel.SetDock(rightTop, Dock.Right); top.Children.Add(rightTop);
            status = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 12, 0) };
            top.Children.Add(status);
            root.Children.Add(top);
            // kreator: brak VB-Audio Virtual Cable
            var sp = new StackPanel();
            sp.Children.Add(T(L.T("Potrzebny jest darmowy VB-Audio Virtual Cable (wirtualna karta dźwiękowa). Windows gra na nią, a Audio Balance przetwarza dźwięk i wysyła go na Twoje głośniki."), 12));
            sp.Children.Add(T(L.T("Kliknij „Zainstaluj VB-Cable automatycznie”. Albo ręcznie: pobierz ze strony producenta, uruchom VBCABLE_Setup_x64.exe jako administrator → Install Driver, uruchom komputer ponownie i kliknij „Sprawdź ponownie”."), 11, Dim));
            var sw = new WrapPanel();
            sw.Children.Add(Mk(L.T("⬇ Zainstaluj VB-Cable automatycznie"), () => _ = InstallCableAsync(), L.T("Pobiera VB-CABLE z serwera VB-Audio i instaluje go. Windows zapyta raz o zgodę (Tak) i o sterownik (Zainstaluj).")));
            sw.Children.Add(Mk(L.T("Pobierz VB-Cable (strona producenta)"), () => Open("https://vb-audio.com/Cable/")));
            sw.Children.Add(Mk(L.T("Sprawdź ponownie"), () => { StartEngine(); }));
            sp.Children.Add(sw);
            cableTxt = T("", 11, Ok); sp.Children.Add(cableTxt);
            sp.Children.Add(T(L.T("VB-CABLE to darmowy program firmy VB-Audio (donationware) – vb-audio.com."), 10, Dim));
            setupBox = new Border { Background = BtnBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 10), Child = sp };
            root.Children.Add(setupBox);
            // dwie kolumny (makieta): lewa – podgląd, pod nim ustawienia i korektor obok siebie; prawa – kompresor, pod nim wtyczki VST3
            var cols = new Grid();
            cols.ColumnDefinitions.Add(new ColumnDefinition()); cols.ColumnDefinitions.Add(new ColumnDefinition());
            live = new AbLive(eng, Fg, Dim);
            var c1 = Card(live.Root); c1.Margin = new Thickness(0, 0, 0, 10);
            var sets = new StackPanel(); sets.Children.Add(Head(L.T("⚙ Ustawienia")));
            sets.Children.Add(Choice(L.T("Wyrównywanie"), new[] { ("off", L.T("Wył."), (string)null), ("auto", "Auto", L.T("Wyrównywanie odczuwalnej głośności.")), ("limit", L.T("Tylko szczyty"), L.T("Tylko ograniczanie głośnych szczytów.")) },
                () => Get("profil") ?? "auto", v => { Put("profil", v); Apply(); }));
            sets.Children.Add(Choice(L.T("Siła wyrównania"), new[] { ("light", L.T("Lekko"), L.T("Wyrównuje głośność delikatnie – zostaje więcej naturalnej dynamiki.")), ("normal", L.T("Normalnie"), L.T("Wyrównuje głośność w zwykłym stopniu (zalecane).")), ("strong", L.T("Mocno"), L.T("Wyrównuje głośność najmocniej – cichy szept i głośny wybuch prawie tak samo głośne.")) }, () => Get("sila") ?? "normal", v => { Put("sila", v); Apply(); }));
            sets.Children.Add(Choice(L.T("Wyraźne dialogi"), new[] { ("0", L.T("Wył."), (string)null), ("1", L.T("Średnio"), L.T("Mowa średnio podbita, gdy przykrywa ją muzyka lub efekty.")), ("2", L.T("Mocno"), L.T("Mowa mocno podbita, gdy przykrywa ją muzyka lub efekty.")) }, () => Get("dialogi") ?? "0", v => { Put("dialogi", v); Apply(); }));
            sets.Children.Add(Choice(L.T("Opóźnienie"), new[] { ("0", "0,1 s", L.T("Do filmów i gier. Na słabszym komputerze może pykać.")), ("1", "0,2 s", (string)null), ("2", "0,35 s", L.T("Najwięcej zapasu – bez pyknięć, dźwięk trochę później.")) },
                () => Get("opoznienie") ?? "1", v => { Put("opoznienie", v); eng.Latency = int.Parse(v); StartEngine(); }));
            var tgt = new ComboBox { ToolTip = L.T("Poziom docelowy – do jakiej głośności wyrównywać.") };
            foreach (var v in new float[] { 0, -14, -16, -18, -20, -23, -24 }) tgt.Items.Add(new ComboBoxItem { Content = v == 0 ? L.T("Auto (zalecane)") : v.ToString("0") + " LUFS", Tag = v });
            var cur = Get("cel") ?? "0"; tgt.SelectedItem = tgt.Items.Cast<ComboBoxItem>().FirstOrDefault(i => ((float)i.Tag).ToString(System.Globalization.CultureInfo.InvariantCulture) == cur) ?? tgt.Items[0];
            tgt.SelectionChanged += (s, e) => { if (tgt.SelectedItem is ComboBoxItem it) { Put("cel", ((float)it.Tag).ToString(System.Globalization.CultureInfo.InvariantCulture)); Apply(); } };
            { var g = new Grid { Margin = new Thickness(0, 0, 0, 8) }; g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) }); g.ColumnDefinitions.Add(new ColumnDefinition());
              g.Children.Add(new TextBlock { Text = L.T("Cel (LUFS)"), Foreground = Dim, VerticalAlignment = VerticalAlignment.Center }); Grid.SetColumn(tgt, 1); g.Children.Add(tgt); sets.Children.Add(g); }
            var byp = new CheckBox { Content = L.T("Bez przetwarzania"), Foreground = Fg, IsChecked = Get("obejscie") == "1", Margin = new Thickness(0, 0, 16, 0), ToolTip = L.T("Dźwięk bez zmian – do porównania.") };
            byp.Click += (s, e) => { Put("obejscie", byp.IsChecked == true ? "1" : "0"); Apply(); };
            var auto = new CheckBox { Content = L.T("Start z Windows"), Foreground = Fg, IsChecked = Autostart, ToolTip = L.T("Uruchamiaj razem z Windows (w zasobniku)") };
            auto.Click += (s, e) => Autostart = auto.IsChecked == true;
            var chk = new WrapPanel { Margin = new Thickness(104, 2, 0, 0) }; chk.Children.Add(byp); chk.Children.Add(auto); sets.Children.Add(chk);
            var lower = new Grid(); lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) }); lower.ColumnDefinitions.Add(new ColumnDefinition());
            var c2 = Card(sets); c2.Margin = new Thickness(0, 0, 10, 0); var c4 = Card(EqPanel()); c4.Margin = new Thickness(0); Grid.SetColumn(c4, 1);
            lower.Children.Add(c2); lower.Children.Add(c4);
            var left = new StackPanel { Margin = new Thickness(0, 0, 10, 0) }; left.Children.Add(c1); left.Children.Add(lower); cols.Children.Add(left);
            // 🎚 kompresor wstępny i 🎛 VST3 z Velivo (ten sam kod)
            MainWindow.Host = this; MainWindow.AbPreNoSrc = true; var mw = new MainWindow();
            var comp = new StackPanel(); var cp = mw.AbPrePanel(Fg, Dim, (t, a2) => Mk(t, a2));
            if (cp is Expander exq) { exq.Header = new TextBlock { Text = L.T("🎚 Kompresor wstępny"), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Fg }; }
            comp.Children.Add(cp);
            var vst = new StackPanel(); vst.Children.Add(Head(L.T("🎛 Wtyczki VST3"))); vst.Children.Add(mw.VstPanel(win, Fg, Dim, (t, a2) => Mk(t, a2)));
            var right = new StackPanel(); var c3 = Card(comp); c3.Margin = new Thickness(0, 0, 0, 10); right.Children.Add(c3); var c5 = Card(vst); c5.Margin = new Thickness(0); right.Children.Add(c5);
            Grid.SetColumn(right, 1); cols.Children.Add(right);
            root.Children.Add(cols);
            win.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height - 40 };
            // po pierwszym ułożeniu stała wysokość: przy SizeToContent każda zmiana liczby na wskaźnikach przeliczała układ całego okna (zacięcia przy przesuwaniu)
            win.ContentRendered += (s, e) => { if (win.SizeToContent != SizeToContent.Manual) { var h = win.ActualHeight; win.SizeToContent = SizeToContent.Manual; win.Height = h; } };
            win.Closing += (s, e) => { if (!exiting) { e.Cancel = true; win.Hide(); } };   // ✕ = do zasobnika (dźwięk dalej przetwarzany)
            tm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };   // 10×/s - ikona w zasobniku płynnie (jak w Velivo)
            tm.Tick += (s, e) => Tick(); tm.Start();
            FillOutputs(); Status();
        }

        void FillOutputs()
        {
            var sel = Get("wyjscie"); outBox.Items.Clear();
            foreach (var (id, name) in AbEngine.Outputs()) outBox.Items.Add(new ComboBoxItem { Content = name, Tag = id });
            outBox.SelectedItem = outBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == sel) ?? (outBox.Items.Count > 0 ? outBox.Items[0] : null);
        }

        void Tick()
        {
            if (!eng.Running && DateTime.UtcNow > retryAt && Get("wlaczony") != "0") { retryAt = DateTime.MaxValue; StartEngine(); }   // urządzenie wróciło / zmiana w Windows
            { var ic = AbIcon.Frame(eng.Running && !eng.Proc.Bypass ? eng.Proc.Out.Momentary : double.NegativeInfinity); if (tray != null && !ReferenceEquals(tray.Icon, ic)) try { tray.Icon = ic; } catch (Exception) { } }   // animowane słupki w zasobniku
            UpdateDefBtn();   // także w zasobniku - znak „przetwarzam” dla Velivo (urządzenia sprawdzane w tle, co 2 s)
        }

        void Status()
        {
            bool cable = AbEngine.CableInstalled();
            setupBox.Visibility = cable ? Visibility.Collapsed : Visibility.Visible;
            if (!cable) { status.Text = L.T("● Brak VB-Audio Virtual Cable – zainstaluj go (niżej)"); status.Foreground = Bad; }
            else if (eng.Running) { status.Text = L.T("● Działa → ") + eng.OutputName; status.Foreground = Ok; }
            else { status.Text = L.T("● Zatrzymany") + (eng.Error == "output" ? L.T(" – nie znaleziono głośników") : eng.Error != null && eng.Error != "cable" ? " – " + eng.Error : Get("wlaczony") == "0" ? "" : L.T(" – ponawiam za chwilę")) + L.T(" (szczegóły: bledy.log w folderze ustawień)"); status.Foreground = Bad; }
            UpdateDefBtn();
            if (tray != null) tray.Text = eng.Running ? "Velivo Audio Balance for Windows – " + L.T("działa") : "Velivo Audio Balance for Windows – " + L.T("zatrzymany");
        }

        // Windows musi grać na „CABLE Input” - wtedy cały dźwięk przechodzi przez Audio Balance
        volatile string cinC, didC, dnameC; volatile bool devBusy; Mutex busyMx; int delayShown = -1; DateTime devAt = DateTime.MinValue;
        void UpdateDefBtn(bool fresh = false)
        {
            // urządzenia Windows sprawdzane najwyżej co 2 s (wcześniej 10×/s w wątku okna - program „chodził wolno”)
            if (fresh) { devAt = DateTime.UtcNow; cinC = AbEngine.CableInputId(); (didC, dnameC) = AbEngine.DefaultOutput(); }
            else if (!devBusy && (DateTime.UtcNow - devAt).TotalSeconds >= 2)
            {   // pytanie Windows o urządzenia trwa długo - w tle, okno i słupki się nie zatrzymują (było co 2 s w wątku okna)
                devAt = DateTime.UtcNow; devBusy = true;
                System.Threading.Tasks.Task.Run(() => { try { var c = AbEngine.CableInputId(); var (d, n) = AbEngine.DefaultOutput(); cinC = c; didC = d; dnameC = n; } catch (Exception ex) { App.LogError(ex); } finally { devBusy = false; } });
            }
            var cin = cinC; var did = didC; var dname = dnameC;
            defBtn.Visibility = cin == null ? Visibility.Collapsed : Visibility.Visible;
            bool on = cin != null && did == cin;
            // znak dla Velivo: gdy cały dźwięk Windows idzie przez ten program, Audio Balance w Velivo się wyłącza (nie poprawia drugi raz)
            bool proc = on && eng.Running && !eng.Proc.Bypass;   // „Bez przetwarzania” - Velivo może wtedy poprawiać sam
            if (proc && busyMx == null) busyMx = new Mutex(true, "VelivoAudioBalance.Przetwarza");
            if (proc && eng.DelayMs != delayShown) { delayShown = eng.DelayMs; try { File.WriteAllText(Path.Combine(App.DataDir, "opoznienie-ms.txt"), delayShown.ToString()); } catch (Exception ex) { App.LogError(ex); } }   // dla Velivo: obraz równo z dźwiękiem
            else if (!proc && busyMx != null) { try { busyMx.ReleaseMutex(); } catch (Exception) { } busyMx.Dispose(); busyMx = null; }
            var txt = on ? L.T("✔ Cały dźwięk przez Audio Balance") : L.T("▶ Kieruj dźwięk Windows przez Audio Balance");
            defBtn.ToolTip = on ? L.T("Kliknij, aby Windows znów grał prosto na głośniki.") : L.T("VB-Cable staje się urządzeniem domyślnym – cały dźwięk Windows przechodzi przez Audio Balance.");
            if (GetText(defBtn) != txt) { SetText(defBtn, txt); defBtn.Background = on ? BtnBg : Acc; }
            if (!on && eng.Running && cin != null && did != null && status.Foreground == Ok) status.Text = L.T("● Działa, ale Windows gra prosto na ") + dname + L.T(" – kliknij przycisk niżej");
        }
        void ToggleDefault()
        {
            var cin = AbEngine.CableInputId(); var (did, _) = AbEngine.DefaultOutput(); if (cin == null) return;
            if (did == cin) { var back = Get("wyjscie") ?? Get("poprzednie"); DefaultDevice.Set(back); }
            else { if (did != null) Put("poprzednie", did); if (Get("wyjscie") == null && did != null) Put("wyjscie", did); DefaultDevice.Set(cin); if (!eng.Running) StartEngine(); }
            UpdateDefBtn(true); FillOutputs(); Status();   // przycisk od razu z nowym stanem
        }

        // ⬇ VB-CABLE: pobranie z serwera VB-Audio (nowy pakiet, zapasowo starszy), rozpakowanie i instalacja po cichu (-i -h) jako administrator; postęp słowami
        async System.Threading.Tasks.Task InstallCableAsync()
        {
            if (cableBusy) return; cableBusy = true;
            void Say(string t, bool bad = false) { cableTxt.Text = t; cableTxt.Foreground = bad ? Bad : Ok; }
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "VelivoAudioBalance-vbcable"); var zip = dir + ".zip"; byte[] data = null; string why = "";
                Say(L.T("Pobieram VB-CABLE z serwera VB-Audio…"));
                using (var h = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(3) })
                    foreach (var u in new[] { "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip", "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip" })
                        try { data = await h.GetByteArrayAsync(u); break; } catch (Exception ex) { why += "\n" + u + ": " + ex.Message; }
                if (data == null) { Say(L.T("Nie udało się pobrać VB-CABLE (internet, zapora albo antywirus blokuje download.vb-audio.com):") + why, true); return; }
                Say(L.T("Rozpakowuję…"));
                await System.Threading.Tasks.Task.Run(() => { File.WriteAllBytes(zip, data); if (Directory.Exists(dir)) Directory.Delete(dir, true); System.IO.Compression.ZipFile.ExtractToDirectory(zip, dir); });
                var setup = Path.Combine(dir, "VBCABLE_Setup_x64.exe");
                if (!File.Exists(setup)) { Say(L.T("W pobranym pakiecie nie ma instalatora VBCABLE_Setup_x64.exe."), true); return; }
                Say(L.T("Instaluję – kliknij „Tak” i „Zainstaluj” w oknach Windows…"));
                System.Diagnostics.Process p;
                try { p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(setup, "-i -h") { UseShellExecute = true, Verb = "runas", WorkingDirectory = dir }); }
                catch (Exception ex) { Say(L.T("Instalacja nie ruszyła (odmowa w oknie Kontrola konta użytkownika?): ") + ex.Message, true); return; }
                await System.Threading.Tasks.Task.Run(() => p?.WaitForExit());
                if (AbEngine.CableInstalled()) { Say(L.T("✔ VB-CABLE zainstalowany.")); StartEngine(); }
                else Say(L.T("✔ Instalacja zakończona – uruchom komputer ponownie, potem kliknij „Sprawdź ponownie”."));
            }
            catch (Exception ex) { App.LogError(ex); Say(ex.Message, true); }
            finally { cableBusy = false; }
        }

        static bool Autostart
        {
            get { try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return k?.GetValue("VelivoAudioBalance") != null; } catch (Exception) { return false; } }
            set { try { using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); if (value) k.SetValue("VelivoAudioBalance", "\"" + Environment.ProcessPath + "\" --zasobnik"); else k.DeleteValue("VelivoAudioBalance", false); } catch (Exception ex) { App.LogError(ex); } }
        }

        void BuildTray()
        {
            tray = new System.Windows.Forms.NotifyIcon { Visible = true, Text = "Velivo Audio Balance for Windows" };
            tray.Icon = AbIcon.Frame(double.NegativeInfinity);   // słupki Audio Balance - rosną z głośnością (Tick)
            var m = new System.Windows.Forms.ContextMenuStrip();
            m.Items.Add(L.T("Pokaż"), null, (s, e) => Show());
            m.Items.Add(L.T("Zakończ"), null, (s, e) => win.Dispatcher.Invoke(Exit));
            tray.ContextMenuStrip = m; tray.MouseClick += (s, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) win.Dispatcher.Invoke(Show); };
        }
        void Show() { FillOutputs(); Status(); win.Show(); if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal; win.Activate(); }
        static void Open(string url) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { } }

        // CABLE Input domyślny bez działającego programu = cisza; przywraca zapisane głośniki (także przy odinstalowaniu)
        internal static void RestoreSpeakers()
        {
            try
            {
                var d = File.Exists(SetFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SetFile)) : null; string back = null;
                if (d != null && !d.TryGetValue("wyjscie", out back)) d.TryGetValue("poprzednie", out back);
                var cin = AbEngine.CableInputId(); var (did, _) = AbEngine.DefaultOutput();
                if (cin != null && did == cin) DefaultDevice.Set(back ?? AbEngine.Outputs().Select(x => x.id).FirstOrDefault());
            }
            catch (Exception ex) { App.LogError(ex); }
        }
        bool exiting;
        void Exit()
        {
            exiting = true;
            // CABLE był domyślny - bez programu dźwięk nie dochodziłby do głośników; Windows wraca do Twoich głośników
            var cin = AbEngine.CableInputId(); var (did, _) = AbEngine.DefaultOutput();
            if (cin != null && did == cin) DefaultDevice.Set(Get("wyjscie") ?? Get("poprzednie") ?? AbEngine.Outputs().Select(x => x.id).FirstOrDefault());
            Application.Current.Shutdown();
        }
        public void Dispose()
        {
            try { tm?.Stop(); } catch (Exception) { }
            eng.Dispose();
            var ev = showEv; showEv = null; try { ev?.Dispose(); } catch (Exception) { }
            try { if (tray != null) { tray.Visible = false; tray.Dispose(); } } catch (Exception) { }
        }
    }
}
