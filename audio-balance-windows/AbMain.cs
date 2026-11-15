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
        void Apply()
        {
            eng.Latency = int.TryParse(Get("opoznienie"), out var lat) ? lat : 1;
            var p = eng.Proc;
            p.Profile = AbProfile.ByName(Get("profil") ?? "auto", Get("sila") ?? "normal");
            p.Target = float.TryParse(Get("cel"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 0f;
            p.Dialog = int.TryParse(Get("dialogi"), out var d) ? d : 0;
            p.Volume = 1; p.CeilingDb = -1; p.FilmLong = false;   // dźwięk Windows jak kanał TV - poziom z ostatnich ok. 30 s (zmiana programu, reklamy)
            p.Bypass = Get("obejscie") == "1";
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
            win = new Window { Title = "Velivo Audio Balance for Windows", Width = 600, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.CanMinimize, Background = Bg, Foreground = Fg, WindowStartupLocation = WindowStartupLocation.CenterScreen };
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
            // zakładki: 📊 Podgląd (wskaźniki, widmo, wykres) / ⚙ Ustawienia
            var tabs = new WrapPanel { Margin = new Thickness(0, 6, 0, 8) }; root.Children.Add(tabs);
            var sets = new StackPanel(); Border setsCard = Card(sets), viewCard = null;
            live = new AbLive(eng, Fg, Dim); viewCard = Card(live.Root);
            Button tv = null, ts = null;
            void Tab(bool v) { viewCard.Visibility = v ? Visibility.Visible : Visibility.Collapsed; setsCard.Visibility = v ? Visibility.Collapsed : Visibility.Visible; tv.Background = v ? Acc : BtnBg; ts.Background = v ? BtnBg : Acc; }
            tv = Mk(L.T("📊 Podgląd"), () => Tab(true), L.T("Wskaźniki głośności, widmo i wykres z 30 s.")); ts = Mk(L.T("⚙ Ustawienia"), () => Tab(false), L.T("Wyrównywanie, kompresor, poziom docelowy."));
            tabs.Children.Add(tv); tabs.Children.Add(ts);
            root.Children.Add(viewCard); root.Children.Add(setsCard);
            var settingsRoot = root; root = sets;
            // ustawienia (jak w Velivo)
            root.Children.Add(Choice(L.T("Wyrównywanie:"), new[] { ("off", L.T("Wył."), (string)null), ("auto", "Auto Level", L.T("Wyrównywanie odczuwalnej głośności.")), ("limit", "Limit Only", L.T("Tylko ograniczanie głośnych szczytów.")) },
                () => Get("profil") ?? "auto", v => { Put("profil", v); Apply(); }));
            root.Children.Add(Choice(L.T("Intensywność:"), new[] { ("light", "Light", (string)null), ("normal", "Normal", (string)null), ("strong", "Strong", (string)null) }, () => Get("sila") ?? "normal", v => { Put("sila", v); Apply(); }));
            root.Children.Add(Choice(L.T("Wyraźne dialogi:"), new[] { ("0", L.T("Wył."), (string)null), ("1", L.T("Średnio"), (string)null), ("2", L.T("Mocno"), (string)null) }, () => Get("dialogi") ?? "0", v => { Put("dialogi", v); Apply(); }));
            // 🎚 pełny kompresor wstępny z Velivo (ten sam kod: presety, własne ustawienia, wszystkie suwaki, charakterystyka, mierniki)
            MainWindow.Host = this; MainWindow.AbPreNoSrc = true;
            root.Children.Add(new Border { Background = Card2, CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Margin = new Thickness(0, 6, 0, 6), Child = new MainWindow().AbPrePanel(Fg, Dim, (t, a2) => Mk(t, a2)) });
            root.Children.Add(Choice(L.T("Opóźnienie dźwięku:"), new[] { ("0", L.T("Niskie ~0,1 s"), L.T("Do filmów i gier. Na słabszym komputerze może pykać.")), ("1", L.T("Normalne ~0,2 s"), (string)null), ("2", L.T("Bezpieczne ~0,35 s"), L.T("Najwięcej zapasu – bez pyknięć, dźwięk trochę później.")) },
                () => Get("opoznienie") ?? "1", v => { Put("opoznienie", v); eng.Latency = int.Parse(v); StartEngine(); }));
            var tgt = new ComboBox { Margin = new Thickness(0, 2, 0, 6), ToolTip = L.T("Poziom docelowy – do jakiej głośności wyrównywać.") };
            foreach (var v in new float[] { 0, -14, -16, -18, -20, -23, -24 }) tgt.Items.Add(new ComboBoxItem { Content = v == 0 ? L.T("Auto – inteligentnie (odniesienie -23 LUFS, zalecane)") : v.ToString("0") + " LUFS", Tag = v });
            var cur = Get("cel") ?? "0"; tgt.SelectedItem = tgt.Items.Cast<ComboBoxItem>().FirstOrDefault(i => ((float)i.Tag).ToString(System.Globalization.CultureInfo.InvariantCulture) == cur) ?? tgt.Items[0];
            tgt.SelectionChanged += (s, e) => { if (tgt.SelectedItem is ComboBoxItem it) { Put("cel", ((float)it.Tag).ToString(System.Globalization.CultureInfo.InvariantCulture)); Apply(); } };
            root.Children.Add(tgt);
            var byp = new CheckBox { Content = L.T("Bez przetwarzania (dźwięk bez zmian, do porównania)"), Foreground = Fg, IsChecked = Get("obejscie") == "1", Margin = new Thickness(0, 2, 0, 4) };
            byp.Click += (s, e) => { Put("obejscie", byp.IsChecked == true ? "1" : "0"); Apply(); }; root.Children.Add(byp);
            var auto = new CheckBox { Content = L.T("Uruchamiaj razem z Windows (w zasobniku)"), Foreground = Fg, IsChecked = Autostart, Margin = new Thickness(0, 2, 0, 8) };
            auto.Click += (s, e) => Autostart = auto.IsChecked == true; root.Children.Add(auto);
            root = settingsRoot; Tab(true);
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
        volatile string cinC, didC, dnameC; volatile bool devBusy; Mutex busyMx; DateTime devAt = DateTime.MinValue;
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
            bool proc = on && eng.Running;
            if (proc && busyMx == null) busyMx = new Mutex(true, "VelivoAudioBalance.Przetwarza");
            else if (!proc && busyMx != null) { try { busyMx.ReleaseMutex(); } catch (Exception) { } busyMx.Dispose(); busyMx = null; }
            var txt = on ? L.T("✔ Windows gra przez Audio Balance – przywróć głośniki jako domyślne") : L.T("▶ Kieruj dźwięk Windows przez Audio Balance (VB-Cable jako urządzenie domyślne)");
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
