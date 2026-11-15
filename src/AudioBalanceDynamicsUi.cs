using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Velivo
{
    public partial class MainWindow
    {
        // 🎚 Kompresor wstępny w oknie Audio Balance (zakładka Korektor): włącznik, presety, wszystkie ustawienia, miernik redukcji
        FrameworkElement AbPrePanel(Brush fg, Brush dim, Func<string, Action, Button> mk)
        {
            var exp = new Expander { Header = L.T("Kompresor wstępny"), Foreground = fg, Margin = new Thickness(0, 10, 0, 0),
                ToolTip = L.T("Działa przed wyrównywaniem głośności: łagodzi różnicę między szeptem a wybuchem, zanim Audio Balance ją zobaczy – mniej „pompowania”. Domyślnie wyłączony.") };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = L.T("Spłaszcza skrajne różnice głośności (szept – wybuch) przed wyrównywaniem Audio Balance. Szum w ciszy łagodnie ścisza zamiast go wzmacniać."), Foreground = dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) });
            bool loading = false;
            var on = new CheckBox { Content = L.T("Kompresor wstępny włączony"), Foreground = fg, Margin = new Thickness(0, 0, 0, 6) };
            panel.Children.Add(on);
            // miernik: ile kompresor teraz ścisza (albo ekspander w ciszy)
            var grText = new TextBlock { Foreground = fg, FontSize = 12, Margin = new Thickness(0, 0, 0, 2) };
            var grBar = new ProgressBar { Minimum = 0, Maximum = 24, Height = 8, Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x5C, 0xFF)), Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x3A)), BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 0, 8),
                ToolTip = L.T("Redukcja wzmocnienia teraz – im dłuższy pasek, tym mocniej kompresor ścisza.") };
            panel.Children.Add(grText); panel.Children.Add(grBar);
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
                sl.ValueChanged += (s, e) => { val.Text = fmt(sl.Value); if (!loading) Apply(); };
                Grid.SetColumn(val, 1); Grid.SetColumn(sl, 2); g.Children.Add(lab); g.Children.Add(val); g.Children.Add(sl);
                panel.Children.Add(g); rows[key] = sl;
            }
            string Db(double v) => v.ToString("+0.0;-0.0;0") + " dB";
            Row("thr", L.T("Próg"), L.T("Od jakiej głośności kompresor zaczyna ściszać (dB)."), -60, 0, 0.5, Db);
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
                    MakeupGainDb = (float)rows["mk"].Value, MixPercent = (float)rows["mix"].Value, ExpanderThresholdDb = (float)rows["et"].Value, ExpanderRatio = (float)rows["er"].Value
                };
                AbProc.Pre.Params = p; SetPref("ab-kompresor", p.Save());
                if (p.IsEnabled && !was) AbNeedsRestartHint();
            }
            on.Click += (s, e) => Apply(); auto.Click += (s, e) => Apply(); mode.SelectionChanged += (s, e) => { if (!loading) Apply(); };
            foreach (var (nm, tip, mkP) in new (string, string, Func<DynParams>)[] {
                (L.T("Film / kino"), L.T("Zbalansowany: RMS, próg -28 dB, 3,5:1, podbicie +4 dB."), DynParams.Film),
                (L.T("Noc / ciche nagrania"), L.T("Mocne wyrównanie: RMS, próg -42 dB, 6:1, podbicie +12 dB, ekspander od -50 dB."), DynParams.Night),
                (L.T("Delikatnie (równoległa)"), L.T("Tylko szczyty: Peak, próg -20 dB, 4:1, Mix 60%."), DynParams.Parallel) })
            { var f = mkP; var b = mk(nm, () => { Show(f()); Apply(); }); b.ToolTip = tip; presets.Children.Add(b); }
            Show(AbProc.Pre.Params);
            var tm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            tm.Tick += (s, e) =>
            {
                bool act = AbProc.Pre.Params.IsEnabled; float gr = act ? AbProc.Pre.CurrentGainReductionDb : 0;
                grText.Text = act ? L.T("Redukcja teraz: ") + gr.ToString("0.0") + " dB" : L.T("Kompresor wyłączony – dźwięk bez zmian");
                grBar.Value = Math.Min(24, Math.Max(0, -gr));
            };
            exp.Loaded += (s, e) => tm.Start(); exp.Unloaded += (s, e) => tm.Stop();
            return exp;
        }
    }
}
