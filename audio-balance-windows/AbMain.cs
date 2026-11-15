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
        string Get(string k) => set.TryGetValue(k, out var v) ? v : null;
        void Put(string k, string v) { set[k] = v; try { Directory.CreateDirectory(App.DataDir); File.WriteAllText(SetFile, JsonSerializer.Serialize(set)); } catch (Exception ex) { App.LogError(ex); } }

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
        void Apply()
        {
            var p = eng.Proc;
            p.Profile = AbProfile.ByName(Get("profil") ?? "auto", Get("sila") ?? "normal");
            p.Target = float.TryParse(Get("cel"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 0f;
            p.Dialog = int.TryParse(Get("dialogi"), out var d) ? d : 0;
            p.Volume = 1; p.CeilingDb = -1; p.FilmLong = false;   // dźwięk Windows jak kanał TV - poziom z ostatnich ok. 30 s (zmiana programu, reklamy)
            p.Bypass = Get("obejscie") == "1";
            var pre = Get("kompresor"); p.Pre.Params = pre == null ? DynParams.Film() : DynParams.Load(pre);
        }

        TextBlock cableTxt; bool cableBusy;
        TextBlock status, srcV, chgV, outV, grV; ComboBox outBox; Border setupBox; Button defBtn;
        Button Mk(string text, Action a, string tip = null) { var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 4, 10, 4), Foreground = Fg, Background = BtnBg, BorderThickness = new Thickness(0), ToolTip = tip }; b.Click += (s, e) => a(); return b; }
        TextBlock T(string s, double size = 12, Brush c = null, bool bold = false) => new TextBlock { Text = s, FontSize = size, Foreground = c ?? Fg, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        WrapPanel Choice(string label, (string key, string text, string tip)[] items, Func<string> cur, Action<string> pick)
        {
            var w = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) }; w.Children.Add(new TextBlock { Text = label, Foreground = Fg, Width = 120, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) });
            var bs = new List<(Button b, string k)>();
            void Mark() { foreach (var (b, k) in bs) b.Background = k == cur() ? Acc : BtnBg; }
            foreach (var (k, t, tip) in items) { var kk = k; var b = Mk(t, () => { pick(kk); Mark(); }, tip); bs.Add((b, kk)); w.Children.Add(b); }
            Mark(); return w;
        }

        void BuildWindow()
        {
            win = new Window { Title = "Velivo Audio Balance for Windows", Width = 470, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.CanMinimize, Background = Bg, Foreground = Fg, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            try { win.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/audiobalance.ico")); } catch (Exception) { }
            var root = new StackPanel { Margin = new Thickness(14) };
            root.Children.Add(T("Velivo Audio Balance for Windows", 18, Fg, true));
            root.Children.Add(T(L.T("Cały dźwięk Windows (przeglądarka, serwisy filmowe, gry) wyrównany jak w Velivo: kompresor, poziom LUFS i limiter."), 11, Dim));
            status = T("", 12, Ok, true); root.Children.Add(status);
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
            setupBox = new Border { Background = BtnBg, CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Margin = new Thickness(0, 4, 0, 8), Child = sp };
            root.Children.Add(setupBox);
            // urządzenia
            root.Children.Add(T(L.T("Głośniki / słuchawki (tu trafia poprawiony dźwięk):"), 12));
            outBox = new ComboBox { Margin = new Thickness(0, 0, 0, 6) }; root.Children.Add(outBox);
            outBox.SelectionChanged += (s, e) => { if (outBox.SelectedItem is ComboBoxItem it && (string)it.Tag != Get("wyjscie")) { Put("wyjscie", (string)it.Tag); StartEngine(); } };
            defBtn = Mk("", ToggleDefault); root.Children.Add(defBtn);
            // mierniki
            var meters = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            for (int i = 0; i < 4; i++) meters.ColumnDefinitions.Add(new ColumnDefinition());
            TextBlock Cell(int col, string name) { var p = new StackPanel(); p.Children.Add(T(name, 10, Dim)); var v = T("–", 16, Fg, true); p.Children.Add(v); Grid.SetColumn(p, col); meters.Children.Add(p); return v; }
            srcV = Cell(0, L.T("ŹRÓDŁO (LUFS)")); chgV = Cell(1, L.T("ZMIANA (dB)")); outV = Cell(2, L.T("WYNIK (LUFS)")); grV = Cell(3, L.T("KOMPRESOR (dB)"));
            root.Children.Add(meters);
            // ustawienia (jak w Velivo)
            root.Children.Add(Choice(L.T("Wyrównywanie:"), new[] { ("off", L.T("Wył."), (string)null), ("auto", "Auto Level", L.T("Wyrównywanie odczuwalnej głośności.")), ("limit", "Limit Only", L.T("Tylko ograniczanie głośnych szczytów.")) },
                () => Get("profil") ?? "auto", v => { Put("profil", v); Apply(); }));
            root.Children.Add(Choice(L.T("Intensywność:"), new[] { ("light", "Light", (string)null), ("normal", "Normal", (string)null), ("strong", "Strong", (string)null) }, () => Get("sila") ?? "normal", v => { Put("sila", v); Apply(); }));
            root.Children.Add(Choice(L.T("Wyraźne dialogi:"), new[] { ("0", L.T("Wył."), (string)null), ("1", L.T("Średnio"), (string)null), ("2", L.T("Mocno"), (string)null) }, () => Get("dialogi") ?? "0", v => { Put("dialogi", v); Apply(); }));
            root.Children.Add(Choice(L.T("Kompresor:"), new[] { ("off", L.T("Wył."), (string)null), ("film", L.T("Film / kino"), L.T("Próg automatyczny z poziomu dźwięku.")), ("noc", L.T("Noc"), L.T("Mocne wyrównanie cichych nagrań.")), ("del", L.T("Delikatnie"), L.T("Tylko szczyty, równoległa.")) },
                () => Get("kompresor-preset") ?? "film", v => { Put("kompresor-preset", v); var q = v == "noc" ? DynParams.Night() : v == "del" ? DynParams.Parallel() : DynParams.Film(); if (v == "off") q.IsEnabled = false; Put("kompresor", q.Save()); Apply(); }));
            var tgt = new ComboBox { Margin = new Thickness(0, 2, 0, 6), ToolTip = L.T("Poziom docelowy – do jakiej głośności wyrównywać.") };
            foreach (var v in new float[] { 0, -14, -16, -18, -20, -23, -24 }) tgt.Items.Add(new ComboBoxItem { Content = v == 0 ? L.T("Auto – inteligentnie (odniesienie -23 LUFS, zalecane)") : v.ToString("0") + " LUFS", Tag = v });
            var cur = Get("cel") ?? "0"; tgt.SelectedItem = tgt.Items.Cast<ComboBoxItem>().FirstOrDefault(i => ((float)i.Tag).ToString(System.Globalization.CultureInfo.InvariantCulture) == cur) ?? tgt.Items[0];
            tgt.SelectionChanged += (s, e) => { if (tgt.SelectedItem is ComboBoxItem it) { Put("cel", ((float)it.Tag).ToString(System.Globalization.CultureInfo.InvariantCulture)); Apply(); } };
            root.Children.Add(tgt);
            var byp = new CheckBox { Content = L.T("Bez przetwarzania (dźwięk bez zmian, do porównania)"), Foreground = Fg, IsChecked = Get("obejscie") == "1", Margin = new Thickness(0, 2, 0, 4) };
            byp.Click += (s, e) => { Put("obejscie", byp.IsChecked == true ? "1" : "0"); Apply(); }; root.Children.Add(byp);
            var auto = new CheckBox { Content = L.T("Uruchamiaj razem z Windows (w zasobniku)"), Foreground = Fg, IsChecked = Autostart, Margin = new Thickness(0, 2, 0, 8) };
            auto.Click += (s, e) => Autostart = auto.IsChecked == true; root.Children.Add(auto);
            var bw = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            bw.Children.Add(Mk(L.T("Do zasobnika"), () => win.Hide())); bw.Children.Add(Mk(L.T("Zakończ"), Exit, L.T("Zamyka program; jeśli CABLE jest domyślny, Windows wraca do Twoich głośników.")));
            root.Children.Add(bw);
            win.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height - 60 };
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
            if (!win.IsVisible) return;
            var p = eng.Proc; string F(double x) => double.IsNegativeInfinity(x) || x < -69 || !eng.Running ? "–" : x.ToString("0.0");
            srcV.Text = F(p.Raw.ShortTerm); outV.Text = F(p.Out.ShortTerm);
            chgV.Text = eng.Running ? p.GainDb.ToString("+0.0;-0.0;0") : "–"; grV.Text = eng.Running && p.Pre.Params.IsEnabled ? p.Pre.CurrentGainReductionDb.ToString("0.0") : "–";
            UpdateDefBtn();
        }

        void Status()
        {
            bool cable = AbEngine.CableInstalled();
            setupBox.Visibility = cable ? Visibility.Collapsed : Visibility.Visible;
            if (!cable) { status.Text = L.T("● Brak VB-Audio Virtual Cable – zainstaluj go (niżej)"); status.Foreground = Bad; }
            else if (eng.Running) { status.Text = L.T("● Działa: dźwięk Windows → CABLE → Audio Balance → ") + eng.OutputName; status.Foreground = Ok; }
            else { status.Text = L.T("● Zatrzymany") + (eng.Error == "output" ? L.T(" – nie znaleziono głośników") : eng.Error != null && eng.Error != "cable" ? " – " + eng.Error : Get("wlaczony") == "0" ? "" : L.T(" – ponawiam za chwilę")) + L.T(" (szczegóły: bledy.log w folderze ustawień)"); status.Foreground = Bad; }
            UpdateDefBtn();
            if (tray != null) tray.Text = eng.Running ? "Velivo Audio Balance for Windows – " + L.T("działa") : "Velivo Audio Balance for Windows – " + L.T("zatrzymany");
        }

        // Windows musi grać na „CABLE Input” - wtedy cały dźwięk przechodzi przez Audio Balance
        void UpdateDefBtn()
        {
            var cin = AbEngine.CableInputId(); var (did, dname) = AbEngine.DefaultOutput();
            defBtn.Visibility = cin == null ? Visibility.Collapsed : Visibility.Visible;
            bool on = cin != null && did == cin;
            var txt = on ? L.T("✔ Windows gra przez Audio Balance – przywróć głośniki jako domyślne") : L.T("▶ Kieruj dźwięk Windows przez Audio Balance (VB-Cable jako urządzenie domyślne)");
            if ((string)defBtn.Content != txt) { defBtn.Content = txt; defBtn.Background = on ? BtnBg : Acc; }
            if (!on && eng.Running && cin != null && did != null && status.Foreground == Ok) status.Text = L.T("● Działa, ale Windows gra prosto na ") + dname + L.T(" – kliknij przycisk niżej");
        }
        void ToggleDefault()
        {
            var cin = AbEngine.CableInputId(); var (did, _) = AbEngine.DefaultOutput(); if (cin == null) return;
            if (did == cin) { var back = Get("wyjscie") ?? Get("poprzednie"); DefaultDevice.Set(back); }
            else { if (did != null) Put("poprzednie", did); if (Get("wyjscie") == null && did != null) Put("wyjscie", did); DefaultDevice.Set(cin); if (!eng.Running) StartEngine(); }
            FillOutputs(); Status();
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
