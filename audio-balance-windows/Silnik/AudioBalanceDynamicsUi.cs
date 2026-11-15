using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Velivo
{
    public partial class MainWindow
    {
        // (kopia src/AudioBalanceDynamicsUi.cs dla programu samodzielnego - zmiany przenoś w obie strony)
        // 🎚 ustawienia kompresora: domyślne (ab-kompresor) + własne dla filmu / kanału (ab-kompresor-zrodla: nazwa źródła → ustawienia), stosowane przy starcie źródła
        static string _abPreSrc; static bool _abPreSrcTv, _abPreSrcOwn;
        static Dictionary<string, string> AbPreSrcLoad()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ln in (GetPref("ab-kompresor-zrodla") ?? "").Split('\n')) { var k = ln.IndexOf('\u001F'); if (k > 0) d[ln.Substring(0, k)] = ln.Substring(k + 1); }
            return d;
        }
        static void AbPreSrcSave(Dictionary<string, string> d)
        {
            var l = new List<string>(); foreach (var kv in d) l.Add(kv.Key + "\u001F" + kv.Value);
            SetPref("ab-kompresor-zrodla", string.Join("\n", l)); _abPreAnyOn = null;
        }
        // czy kompresor może być potrzebny (domyślnie albo dla któregoś filmu / kanału) - wtedy dźwięk idzie przez procesor Velivo
        static bool? _abPreAnyOn;   // zapamiętane - AbWanted woła to nawet 50×/s; liczone od nowa po każdym zapisie ustawień
        internal static bool AbPreAnyOn { get { if (_abPreAnyOn == null) { bool any = DynParams.Load(GetPref("ab-kompresor")).IsEnabled; if (!any) foreach (var v in AbPreSrcLoad().Values) if (DynParams.Load(v).IsEnabled) { any = true; break; } _abPreAnyOn = any; } return _abPreAnyOn.Value; } }
        static void AbPreSetDefault(DynParams p) { SetPref("ab-kompresor", p.Save()); _abPreAnyOn = null; }
        internal static void AbPreApplyFor(string src, bool tv)
        {
            _abPreSrc = string.IsNullOrWhiteSpace(src) ? null : src.Trim(); _abPreSrcTv = tv;
            string own = null; _abPreSrcOwn = _abPreSrc != null && AbPreSrcLoad().TryGetValue(_abPreSrc, out own);
            AbProc.Pre.Params = DynParams.Load(_abPreSrcOwn ? own : GetPref("ab-kompresor"));
        }

        // 🎚 Kompresor wstępny w oknie Audio Balance (zakładka Korektor): włącznik, presety, wszystkie ustawienia, miernik redukcji
        internal FrameworkElement AbPrePanel(Brush fg, Brush dim, Func<string, Action, Button> mk)
        {
            var exp = new Expander { Header = L.T("Kompresor wstępny"), Foreground = fg, Margin = new Thickness(0, 0, 0, 0), IsExpanded = true };   // bez podpowiedzi całej sekcji - wyskakiwała ucięta przy każdym ruchu myszy (opis jest pod nagłówkiem)
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = L.T("Spłaszcza skrajne różnice głośności (szept – wybuch) przed wyrównywaniem Audio Balance. Szum w ciszy łagodnie ścisza zamiast go wzmacniać."), Foreground = dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) });
            bool loading = false;
            Action manual = null; CheckBox autoThr = null;   // autoThr - przypisany niżej (czytany przez Apply/Show)   // ręczna zmiana suwaka - ustawienie już nie jest zapisanym presetem
            var on = new CheckBox { Content = L.T("Kompresor wstępny włączony"), Foreground = fg, Margin = new Thickness(0, 0, 0, 6) };
            panel.Children.Add(on);
            // 🎞📺 dla czego są te ustawienia: domyślne albo własne tego filmu / kanału
            var srcText = new TextBlock { Foreground = fg, FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) };
            var ownSrc = new CheckBox { Foreground = fg, Margin = new Thickness(0, 2, 0, 2), ToolTip = L.T("Zaznaczone: te ustawienia zapamiętane tylko dla tego filmu / kanału i wracają same przy następnym włączeniu. Odznaczone: działają ustawienia domyślne.") };
            var srcBtns = new WrapPanel { Margin = new Thickness(0, 2, 0, 6) };
            var toDef = mk(L.T("↺ Domyślne"), () => { }); toDef.ToolTip = L.T("Ten film / kanał wraca do ustawień domyślnych (własne dla niego są usuwane).");
            var setDef = mk(L.T("★ Ustaw jako domyślne"), () => { }); setDef.ToolTip = L.T("Obecne ustawienia stają się domyślnymi – dla filmów i kanałów bez własnych.");
            var listBtn = mk(L.T("📋 Zapisane dla filmów i kanałów…"), () => { }); listBtn.ToolTip = L.T("Lista filmów i kanałów TV z własnymi ustawieniami kompresora – z usuwaniem.");
            srcBtns.Children.Add(toDef); srcBtns.Children.Add(setDef); srcBtns.Children.Add(listBtn);
            if (!AbPreNoSrc) { panel.Children.Add(srcText); panel.Children.Add(ownSrc); panel.Children.Add(srcBtns); }   // program samodzielny: nie ma filmów / kanałów - tylko jedno ustawienie
            // 📈 wskaźniki jak w Ozone: charakterystyka z kropką obecnego poziomu, mierniki Wejście / Redukcja / Wyjście, historia redukcji (6 s)
            var vis = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            vis.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) }); vis.ColumnDefinitions.Add(new ColumnDefinition());
            const double W = 150, H = 150, Lo = -60;   // oś: -60..0 dB
            double X(double db) => (Math.Max(Lo, Math.Min(0, db)) - Lo) / -Lo * W; double Y(double db) => H - (Math.Max(Lo, Math.Min(0, db)) - Lo) / -Lo * H;
            var plot = new Canvas { Width = W, Height = H, Background = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x1A)), ClipToBounds = true,
                ToolTip = L.T("Charakterystyka: poziom wejścia (poziomo) → wyjścia (pionowo). Kropka – obecny dźwięk; przekątna – bez zmian.") };
            var grid = new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x3A));
            for (int k = -50; k < 0; k += 10) { plot.Children.Add(new System.Windows.Shapes.Line { X1 = X(k), X2 = X(k), Y1 = 0, Y2 = H, Stroke = grid, StrokeThickness = 1 }); plot.Children.Add(new System.Windows.Shapes.Line { X1 = 0, X2 = W, Y1 = Y(k), Y2 = Y(k), Stroke = grid, StrokeThickness = 1 }); }
            plot.Children.Add(new System.Windows.Shapes.Line { X1 = 0, Y1 = H, X2 = W, Y2 = 0, Stroke = new SolidColorBrush(Color.FromRgb(0x44, 0x48, 0x58)), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } });
            var thrLine = new System.Windows.Shapes.Line { Y1 = 0, Y2 = H, Stroke = new SolidColorBrush(Color.FromArgb(0x90, 0xFA, 0xCC, 0x15)), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 2 } };
            var expLine = new System.Windows.Shapes.Line { Y1 = 0, Y2 = H, Stroke = new SolidColorBrush(Color.FromArgb(0x90, 0x2E, 0xD3, 0xC6)), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 2 } };
            var curve = new System.Windows.Shapes.Polyline { Stroke = new SolidColorBrush(Color.FromRgb(0x7C, 0x5C, 0xFF)), StrokeThickness = 2 };
            var dot = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0x2E, 0xD3, 0xC6)), Stroke = Brushes.White, StrokeThickness = 1 };
            plot.Children.Add(thrLine); plot.Children.Add(expLine); plot.Children.Add(curve); plot.Children.Add(dot);
            vis.Children.Add(plot);
            void DrawCurve(DynParams p, double thr = double.NaN, double et = double.NaN)
            {
                if (double.IsNaN(et)) et = AbProc.Pre.EffExpanderDb;
                if (double.IsNaN(thr)) thr = p.AutoThreshold ? AbProc.Pre.EffThresholdDb : p.ThresholdDb;
                var pts = new PointCollection(); double mk = p.MakeupGainDb, mix = p.MixPercent / 100.0;
                for (double x = Lo; x <= 0.001; x += 0.5) { double g = VelivoDynamicsProcessor.GainComputer(x, p, thr, et) + mk, y = x + 20 * Math.Log10(Math.Max(1e-6, 1 - mix + mix * Math.Pow(10, g / 20))); pts.Add(new Point(X(x), Y(y))); }
                curve.Points = pts; thrLine.X1 = thrLine.X2 = X(thr); expLine.X1 = expLine.X2 = X(et);
            }
            var right = new StackPanel { Margin = new Thickness(10, 0, 0, 0) }; Grid.SetColumn(right, 1); vis.Children.Add(right);
            var bars = new Grid { Height = 104 };
            (Border bar, TextBlock val) Meter(int col, string name, Color c, string tip)
            {
                bars.ColumnDefinitions.Add(new ColumnDefinition());
                var sp = new DockPanel { Margin = new Thickness(3, 0, 3, 0), ToolTip = tip }; Grid.SetColumn(sp, col);
                var nm = new TextBlock { Text = name, Foreground = dim, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center }; DockPanel.SetDock(nm, Dock.Bottom);
                var v = new TextBlock { Foreground = fg, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center }; DockPanel.SetDock(v, Dock.Top);
                var well = new Border { Background = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x1A)), Width = 14, HorizontalAlignment = HorizontalAlignment.Center };
                var b = new Border { Background = new SolidColorBrush(c), VerticalAlignment = name == L.T("Redukcja") ? VerticalAlignment.Top : VerticalAlignment.Bottom, Height = 0 };
                well.Child = b; sp.Children.Add(nm); sp.Children.Add(v); sp.Children.Add(well); bars.Children.Add(sp);
                return (b, v);
            }
            var mIn = Meter(0, L.T("Wejście"), Color.FromRgb(0x8A, 0x90, 0xA8), L.T("Poziom dźwięku przed kompresorem (dB)."));
            var mGr = Meter(1, L.T("Redukcja"), Color.FromRgb(0xFA, 0xCC, 0x15), L.T("Ile kompresor teraz ścisza (dB) – pasek rośnie w dół."));
            var mOut = Meter(2, L.T("Wyjście"), Color.FromRgb(0x2E, 0xD3, 0xC6), L.T("Poziom po kompresorze, przed wyrównywaniem Audio Balance (dB)."));
            right.Children.Add(bars);
            var hist = new Canvas { Height = 40, Background = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x1A)), ClipToBounds = true, Margin = new Thickness(0, 4, 0, 0),
                ToolTip = L.T("Historia redukcji z ostatnich 6 sekund – im głębiej, tym mocniej kompresor ściszał.") };
            var histLine = new System.Windows.Shapes.Polyline { Stroke = new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15)), StrokeThickness = 1.5 };
            hist.Children.Add(histLine); right.Children.Add(hist);
            var grText = new TextBlock { Foreground = fg, FontSize = 11, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
            right.Children.Add(grText);
            panel.Children.Add(vis);
            var grHist = new double[120]; int grPos = 0; double lastThr = double.NaN, lastExp = double.NaN;
            var presets = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            panel.Children.Add(presets);
            var mode = new ComboBox { Margin = new Thickness(0, 0, 0, 6), ToolTip = L.T("RMS – średnia energia (naturalnie dla dialogów i muzyki); Peak – reaguje na każdy szczyt (strzały, trzaski).") };
            mode.Items.Add(new ComboBoxItem { Content = L.T("Wykrywanie: RMS (dialogi, muzyka)"), Tag = DynDetection.Rms });
            mode.Items.Add(new ComboBoxItem { Content = L.T("Wykrywanie: Peak (szczyty, strzały)"), Tag = DynDetection.Peak });
            panel.Children.Add(mode);
            var auto = new CheckBox { Content = L.T("Automatyczne podbicie (wyrównuje to, co kompresor ściszył)"), Foreground = fg, Margin = new Thickness(0, 2, 0, 6) };
            var rows = new Dictionary<string, Slider>();
            void Row(string key, string name, string tip, double min, double max, double step, Func<double, string> fmt)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 2), ToolTip = tip };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) }); g.ColumnDefinitions.Add(new ColumnDefinition());
                var lab = new TextBlock { Text = name, Foreground = fg, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                var val = new TextBlock { Foreground = dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                var sl = new Slider { Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 5, TickFrequency = step, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
                sl.ValueChanged += (s, e) => { val.Text = fmt(sl.Value); if (!loading) { Apply(); manual?.Invoke(); } };
                Grid.SetColumn(val, 1); Grid.SetColumn(sl, 2); g.Children.Add(lab); g.Children.Add(val); g.Children.Add(sl);
                panel.Children.Add(g); rows[key] = sl;
            }
            string Db(double v) => v.ToString("+0.0;-0.0;0") + " dB";
            Row("thr", L.T("Próg"), L.T("Od jakiej głośności kompresor zaczyna ściszać (dB)."), -60, 0, 0.5, Db);
            autoThr = new CheckBox { Content = L.T("Próg automatyczny (z poziomu filmu)"), Foreground = fg, Margin = new Thickness(0, 2, 0, 2),
                ToolTip = L.T("Kompresor sam mierzy typowy poziom filmu (ok. 10 s, bez ciszy) i ustawia próg ponad nim – ściska tylko to, co wystaje, przy każdym filmie tak samo.") };
            panel.Children.Add(autoThr);
            Row("aoff", L.T("Nad średnią filmu"), L.T("O ile dB ponad typowym poziomem filmu zaczyna działać próg automatyczny."), 0, 20, 0.5, Db);
            Row("ratio", L.T("Stopień"), L.T("Jak mocno ściszać to, co ponad progiem: 4:1 – 4 dB ponad progiem daje 1 dB."), 1, 20, 0.1, v => v.ToString("0.0") + ":1");
            Row("att", L.T("Atak"), L.T("Jak szybko kompresor zaczyna ściszać (ms)."), 0.5, 200, 0.5, v => v.ToString("0.#") + " ms");
            Row("rel", L.T("Powrót"), L.T("Jak szybko wraca po głośnym fragmencie (ms) – dłużej = spokojniej, bez pompowania."), 10, 2000, 10, v => v.ToString("0") + " ms");
            Row("knee", L.T("Miękkie kolano"), L.T("Szerokość płynnego przejścia wokół progu (dB) – im szersze, tym mniej słychać moment zadziałania."), 0, 12, 0.5, v => v.ToString("0.0") + " dB");
            Row("mk", L.T("Podbicie"), L.T("Stałe podbicie po kompresji (dB)."), -12, 24, 0.5, Db);
            panel.Children.Add(auto);
            Row("mix", L.T("Mix (równoległa)"), L.T("Ile sygnału skompresowanego, a ile czystego – poniżej 100% film zachowuje naturalne uderzenia."), 0, 100, 1, v => v.ToString("0") + "%");
            Row("et", L.T("Próg ciszy (ekspander)"), L.T("Poniżej tej głośności szum tła jest łagodnie ściszany zamiast wzmacniany (dB)."), -80, -30, 0.5, Db);
            Row("er", L.T("Stopień ekspandera"), L.T("Jak mocno ściszać szum poniżej progu ciszy (1:1 – wcale)."), 1, 4, 0.1, v => v.ToString("0.0") + ":1");
            exp.Content = panel;

            void Show(DynParams p)
            {
                loading = true;
                on.IsChecked = p.IsEnabled; auto.IsChecked = p.AutoMakeup; mode.SelectedIndex = p.DetectionMode == DynDetection.Peak ? 1 : 0;
                rows["thr"].Value = p.ThresholdDb; rows["ratio"].Value = p.Ratio; rows["att"].Value = p.AttackMs; rows["rel"].Value = p.ReleaseMs; rows["knee"].Value = p.KneeDb;
                autoThr.IsChecked = p.AutoThreshold; rows["aoff"].Value = p.AutoOffsetDb; rows["thr"].IsEnabled = !p.AutoThreshold;
                rows["mk"].Value = p.MakeupGainDb; rows["mix"].Value = p.MixPercent; rows["et"].Value = p.ExpanderThresholdDb; rows["er"].Value = p.ExpanderRatio;
                loading = false;
            }
            void Apply()
            {
                bool was = AbProc.Pre.Params.IsEnabled;
                var p = new DynParams
                {
                    IsEnabled = on.IsChecked == true, AutoMakeup = auto.IsChecked == true, DetectionMode = mode.SelectedIndex == 1 ? DynDetection.Peak : DynDetection.Rms,
                    ThresholdDb = (float)rows["thr"].Value, Ratio = (float)rows["ratio"].Value, AttackMs = (float)rows["att"].Value, ReleaseMs = (float)rows["rel"].Value, KneeDb = (float)rows["knee"].Value,
                    AutoThreshold = autoThr.IsChecked == true, AutoOffsetDb = (float)rows["aoff"].Value,
                    MakeupGainDb = (float)rows["mk"].Value, MixPercent = (float)rows["mix"].Value, ExpanderThresholdDb = (float)rows["et"].Value, ExpanderRatio = (float)rows["er"].Value
                };
                AbProc.Pre.Params = p; if (_abPreSrcOwn && _abPreSrc != null) { var d = AbPreSrcLoad(); d[_abPreSrc] = p.Save(); AbPreSrcSave(d); } else AbPreSetDefault(p); DrawCurve(p); rows["thr"].IsEnabled = !p.AutoThreshold;
                if (p.IsEnabled && !was) AbNeedsRestartHint();
            }
            on.Click += (s, e) => Apply(); autoThr.Click += (s, e) => { Apply(); manual?.Invoke(); }; auto.Click += (s, e) => { Apply(); manual?.Invoke(); }; mode.SelectionChanged += (s, e) => { if (!loading) { Apply(); manual?.Invoke(); } };
            // 💾 własne ustawienia pod własnymi nazwami (zapamiętane na tym komputerze) + nazwa ostatnio wybranego
            var nameText = new TextBlock { Foreground = dim, FontSize = 11, Margin = new Thickness(0, 0, 0, 4) };
            var ownRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var own = new ComboBox { MinWidth = 140, ToolTip = L.T("Twoje zapisane ustawienia kompresora – wybierz, aby włączyć.") };
            var saveBtn = mk(L.T("💾 Zapisz jako…"), () => { }); saveBtn.ToolTip = L.T("Zapisuje obecne ustawienia kompresora pod Twoją nazwą.");
            var delBtn = mk(L.T("🗑 Usuń"), () => { }); delBtn.ToolTip = L.T("Usuwa wybrane własne ustawienie z listy.");
            DockPanel.SetDock(saveBtn, Dock.Right); DockPanel.SetDock(delBtn, Dock.Right); saveBtn.Margin = delBtn.Margin = new Thickness(6, 0, 0, 0);
            ownRow.Children.Add(delBtn); ownRow.Children.Add(saveBtn); ownRow.Children.Add(own);
            int pi = panel.Children.IndexOf(presets); panel.Children.Insert(pi + 1, ownRow); panel.Children.Insert(pi + 1, nameText);
            List<(string n, string p)> Own() { var l = new List<(string, string)>(); foreach (var ln in (GetPref("ab-kompresor-wlasne") ?? "").Split('\n')) { var k = ln.IndexOf('\u001F'); if (k > 0) l.Add((ln.Substring(0, k), ln.Substring(k + 1))); } return l; }
            void SaveOwn(List<(string n, string p)> l) => SetPref("ab-kompresor-wlasne", string.Join("\n", l.ConvertAll(x => x.n + "\u001F" + x.p)));
            bool ownLoading = false;
            void FillOwn(string sel)
            {
                ownLoading = true; own.Items.Clear(); own.Items.Add(new ComboBoxItem { Content = L.T("(własne ustawienia)"), IsEnabled = false });
                foreach (var (n, _) in Own()) own.Items.Add(new ComboBoxItem { Content = n, Tag = n });
                own.SelectedIndex = 0; for (int k = 1; k < own.Items.Count; k++) if ((string)((ComboBoxItem)own.Items[k]).Tag == sel) own.SelectedIndex = k;
                delBtn.IsEnabled = own.SelectedIndex > 0; ownLoading = false;
            }
            void SetName(string n) { SetPref("ab-kompresor-nazwa", n ?? ""); nameText.Text = string.IsNullOrEmpty(n) ? L.T("Ustawienie: własne (zmienione ręcznie)") : L.T("Ustawienie: ") + n; FillOwn(n); }
            own.SelectionChanged += (s, e) =>
            {
                if (ownLoading || !(own.SelectedItem is ComboBoxItem it) || !(it.Tag is string n)) return;
                foreach (var (on2, pp) in Own()) if (on2 == n) { Show(DynParams.Load(pp)); Apply(); SetName(n); return; }
            };
            saveBtn.Click += (s, e) =>
            {
                var cur = GetPref("ab-kompresor-nazwa"); var n = Prompt(L.T("Nazwa ustawienia kompresora:"), string.IsNullOrEmpty(cur) ? L.T("Moje ustawienie") : cur, Window.GetWindow(exp));   // nad oknem Audio Balance (było pod nim i blokowało)
                n = (n ?? "").Replace("\n", " ").Replace("\r", " ").Replace("\u001F", " ").Trim(); if (n.Length == 0) return;
                var l = Own(); l.RemoveAll(x => x.n == n); l.Add((n, AbProc.Pre.Params.Save())); SaveOwn(l); SetName(n);
            };
            delBtn.Click += (s, e) =>
            {
                if (!(own.SelectedItem is ComboBoxItem it) || !(it.Tag is string n)) return;
                var l = Own(); l.RemoveAll(x => x.n == n); SaveOwn(l); SetName(null);
            };
            foreach (var (nm, tip, mkP) in new (string, string, Func<DynParams>)[] {
                (L.T("Film / kino"), L.T("Zbalansowany: RMS, próg -28 dB, 3,5:1, podbicie +4 dB."), DynParams.Film),
                (L.T("Noc / ciche nagrania"), L.T("Mocne wyrównanie: RMS, próg -42 dB, 6:1, podbicie +12 dB, ekspander od -50 dB."), DynParams.Night),
                (L.T("Delikatnie (równoległa)"), L.T("Tylko szczyty: Peak, próg -20 dB, 4:1, Mix 60%."), DynParams.Parallel) })
            { var f = mkP; var n0 = nm; var b = mk(nm, () => { Show(f()); Apply(); SetName(n0); }); b.ToolTip = tip; presets.Children.Add(b); }
            string shownSrc = "\u0000";
            void SrcUi()
            {
                shownSrc = _abPreSrc;
                string kind = _abPreSrcTv ? L.T("Kanał: ") : L.T("Film: ");
                srcText.Text = _abPreSrc == null ? L.T("Ustawienia domyślne (nic nie gra)") : kind + _abPreSrc + " – " + (_abPreSrcOwn ? L.T("ustawienia własne") : L.T("ustawienia domyślne"));
                ownSrc.Content = _abPreSrcTv ? L.T("Własne ustawienia dla tego kanału") : L.T("Własne ustawienia dla tego filmu");
                ownSrc.IsEnabled = _abPreSrc != null; ownSrc.IsChecked = _abPreSrcOwn; toDef.IsEnabled = _abPreSrcOwn;
            }
            ownSrc.Click += (s, e) =>
            {
                if (_abPreSrc == null) return; var d = AbPreSrcLoad();
                if (ownSrc.IsChecked == true) { d[_abPreSrc] = AbProc.Pre.Params.Save(); _abPreSrcOwn = true; }   // od teraz zmiany zapisują się dla tego źródła
                else { d.Remove(_abPreSrc); _abPreSrcOwn = false; AbProc.Pre.Params = DynParams.Load(GetPref("ab-kompresor")); Show(AbProc.Pre.Params); DrawCurve(AbProc.Pre.Params); }
                AbPreSrcSave(d); SrcUi();
            };
            toDef.Click += (s, e) => { if (_abPreSrc == null) return; var d = AbPreSrcLoad(); d.Remove(_abPreSrc); AbPreSrcSave(d); _abPreSrcOwn = false; AbProc.Pre.Params = DynParams.Load(GetPref("ab-kompresor")); Show(AbProc.Pre.Params); DrawCurve(AbProc.Pre.Params); SrcUi(); };
            setDef.Click += (s, e) => { AbPreSetDefault(AbProc.Pre.Params); ShowToast(L.T("★ Ustawienia kompresora zapisane jako domyślne"), null); };
            SrcUi();
            listBtn.Click += (s, e) =>
            {   // 📋 lista zapisanych ustawień dla filmów i kanałów, każda pozycja z 🗑 Usuń
                var own = Window.GetWindow(listBtn);
                var w = new Window { Title = L.T("Kompresor – zapisane dla filmów i kanałów"), Uid = "own-theme", Owner = own, Topmost = own?.Topmost == true, Background = AbDark, Foreground = fg, Width = 520, Height = 460, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResize };
                var root = new DockPanel { Margin = new Thickness(12) };
                var head = new TextBlock { Text = L.T("Filmy i kanały TV z własnymi ustawieniami kompresora. Usunięcie = ten film / kanał wraca do ustawień domyślnych."), Foreground = dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
                DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
                var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
                DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
                var rowsPanel = new StackPanel(); root.Children.Add(new ScrollViewer { Content = rowsPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
                void Fill()
                {
                    rowsPanel.Children.Clear(); var d = AbPreSrcLoad();
                    if (d.Count == 0) { rowsPanel.Children.Add(new TextBlock { Text = L.T("Brak – żaden film ani kanał nie ma własnych ustawień."), Foreground = dim, FontSize = 12 }); return; }
                    foreach (var kv in d.OrderBy(x => x.Key, StringComparer.CurrentCultureIgnoreCase))
                    {
                        var q = DynParams.Load(kv.Value); string key = kv.Key;
                        var g = new Grid { Margin = new Thickness(0, 0, 0, 6) }; g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        var info = new StackPanel();
                        info.Children.Add(new TextBlock { Text = key + (key.Equals(_abPreSrc, StringComparison.OrdinalIgnoreCase) ? L.T("  (gra teraz)") : ""), Foreground = fg, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = key });
                        info.Children.Add(new TextBlock { Foreground = dim, FontSize = 11, Text = !q.IsEnabled ? L.T("kompresor wyłączony") : (q.AutoThreshold ? L.T("próg automatyczny +") + q.AutoOffsetDb.ToString("0.#") + " dB" : L.T("próg ") + q.ThresholdDb.ToString("0.#") + " dB") + ", " + q.Ratio.ToString("0.#") + ":1, " + L.T("podbicie ") + q.MakeupGainDb.ToString("+0.#;-0.#;0") + " dB, Mix " + q.MixPercent.ToString("0") + "%" });
                        var del = mk(L.T("🗑 Usuń"), () =>
                        {
                            var dd = AbPreSrcLoad(); dd.Remove(key); AbPreSrcSave(dd);
                            if (key.Equals(_abPreSrc, StringComparison.OrdinalIgnoreCase)) { _abPreSrcOwn = false; AbProc.Pre.Params = DynParams.Load(GetPref("ab-kompresor")); Show(AbProc.Pre.Params); DrawCurve(AbProc.Pre.Params); SrcUi(); }
                            Fill();
                        });
                        del.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(del, 1);
                        g.Children.Add(info); g.Children.Add(del); rowsPanel.Children.Add(g);
                    }
                }
                var delAll = mk(L.T("Usuń wszystkie"), () =>
                {
                    if (MessageBox.Show(w, L.T("Usunąć własne ustawienia kompresora wszystkich filmów i kanałów? Wrócą do ustawień domyślnych."), "Velivo", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    AbPreSrcSave(new Dictionary<string, string>()); _abPreSrcOwn = false; AbProc.Pre.Params = DynParams.Load(GetPref("ab-kompresor")); Show(AbProc.Pre.Params); DrawCurve(AbProc.Pre.Params); SrcUi(); Fill();
                });
                var close = mk(L.T("Zamknij"), () => w.Close());
                bottom.Children.Add(delAll); bottom.Children.Add(close);
                Fill(); w.Content = root; w.Show();
            };
            Show(AbProc.Pre.Params); DrawCurve(AbProc.Pre.Params);
            manual = () => { if (!string.IsNullOrEmpty(GetPref("ab-kompresor-nazwa"))) SetName(null); };
            var lastName = GetPref("ab-kompresor-nazwa"); SetName(string.IsNullOrEmpty(lastName) ? null : lastName);
            var tm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            tm.Tick += (s, e) =>
            {
                if (!string.Equals(shownSrc, _abPreSrc)) { Show(AbProc.Pre.Params); DrawCurve(AbProc.Pre.Params); SrcUi(); }   // nowy film / kanał - jego ustawienia
                var pr = AbProc.Pre; bool act = pr.Params.IsEnabled && AbFlowing();
                double xi = act ? pr.InputDb : -120, xo = act ? pr.OutputDb : -120, gr = act && xi > -70 ? pr.CurrentGainReductionDb : 0;   // w ciszy (pauza) 0 - nie wielki słupek ekspandera
                Canvas.SetLeft(dot, X(xi) - 5); Canvas.SetTop(dot, Y(xo) - 5); dot.Visibility = act && xi > Lo ? Visibility.Visible : Visibility.Hidden;
                double bh = bars.ActualHeight - 30; if (bh < 10) bh = 74;
                mIn.bar.Height = Math.Max(0, (Math.Max(Lo, xi) - Lo) / -Lo * bh); mIn.val.Text = act ? xi.ToString("0") : "–";
                mOut.bar.Height = Math.Max(0, (Math.Max(Lo, xo) - Lo) / -Lo * bh); mOut.val.Text = act ? xo.ToString("0") : "–";
                mGr.bar.Height = Math.Min(bh, Math.Max(0, -gr) / 24 * bh); mGr.val.Text = act ? gr.ToString("0.0") : "–";
                grHist[grPos++ % grHist.Length] = gr;
                double hw = hist.ActualWidth > 10 ? hist.ActualWidth : 200, hh = 40; var pts = new PointCollection();
                for (int k = 0; k < grHist.Length; k++) { double v = grHist[(grPos + k) % grHist.Length]; pts.Add(new Point(k * hw / (grHist.Length - 1), Math.Min(hh, -v / 24 * hh))); }
                histLine.Points = pts;
                if ((pr.Params.AutoThreshold && Math.Abs(pr.EffThresholdDb - lastThr) > 0.1) || Math.Abs(pr.EffExpanderDb - lastExp) > 0.1) { lastThr = pr.EffThresholdDb; lastExp = pr.EffExpanderDb; DrawCurve(pr.Params, pr.Params.AutoThreshold ? lastThr : pr.Params.ThresholdDb, lastExp); }   // próg i próg ciszy z filmu - linie na żywo   // próg automatyczny - wykres na żywo
                grText.Text = (pr.Params.AutoThreshold && act ? L.T("Próg teraz: ") + pr.EffThresholdDb.ToString("0.0") + " dB · " : "") + (!pr.Params.IsEnabled ? L.T("Kompresor wyłączony – dźwięk bez zmian") : !act ? L.T("Czekam na dźwięk filmu") : L.T("Redukcja teraz: ") + gr.ToString("0.0") + " dB");
            };
            exp.IsVisibleChanged += (s, e) => { if (exp.IsVisible) tm.Start(); else tm.Stop(); };   // tylko gdy zakładka Kompresor jest na ekranie - nie zabiera czasu Audio Monitorowi
            exp.Unloaded += (s, e) => tm.Stop();
            return exp;
        }
    }
}
