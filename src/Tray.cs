using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace Velivo
{
    // Tryb "zostan w zasobniku": zamkniecie okna chowa Velivo do ikonki przy zegarze. Synchronizacja LAN dziala dalej,
    // a ponowne otwarcie jest natychmiastowe. Ikonka pulsuje, gdy trwa synchronizacja. Opcja w Ustawieniach.
    public partial class MainWindow
    {
        System.Windows.Forms.NotifyIcon _tray;
        System.Drawing.Icon _trayIcon;
        System.Drawing.Icon[] _trayFrames;   // klatki lagodnego oddechu ikonki
        DispatcherTimer _trayPulse;
        DateTime _trayPulseUntil;
        bool _inTray, _reallyExit;

        // wywolywane z Closing (jako pierwsze): zamiast zamykac - chowamy do zasobnika
        void TrayOnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_reallyExit || _settings == null || !_settings.StayInTray) return;
            e.Cancel = true;
            try { SaveSession(); } catch (Exception) { }
            // strony w tle nie graja (okienko "Film na wierzchu" gra dalej)
            foreach (var t in _tabs.ToList())
                try { _ = t.View.CoreWebView2?.ExecuteScriptAsync("document.querySelectorAll('video,audio').forEach(function(m){try{m.pause();}catch(e){}})"); } catch (Exception) { }
            Hide();
            _inTray = true;
            EnsureTray();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                try { _tray.ShowBalloonTip(3000, "Velivo", L.T("Velivo działa w zasobniku i synchronizuje się w tle. Kliknij ikonkę, aby otworzyć."), System.Windows.Forms.ToolTipIcon.Info); } catch (Exception) { }
            }
        }
        bool _trayHintShown;

        // autostart z Windows: okno schowane w zasobniku, a w tle (niski priorytet) przygotowanie modulow - po kliknieciu wszystko gotowe od razu
        internal bool SilentStart;
        void StartSilently()
        {
            _trayHintShown = true;   // bez dymka - start z Windows ma byc cichy
            EnsureTray(); Hide(); _inTray = true;
            ShowActivated = true;   // przy pokazaniu (ikonka, koniec filmu) okno normalnie aktywne - inaczej Windows nie pokaze okna zmaksymalizowanego
            try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; } catch (Exception) { }
            var warm = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            warm.Tick += (s, e) =>
            {
                warm.Stop();
                try { if (IptvInstalled) LoadIptvInBackground(null); } catch (Exception ex) { App.LogError(ex); }   // lista kanalow i program TV (stare - pobrane po cichu)
                try { if (VlcInstalled) EnsureLibVlc(); } catch (Exception ex) { App.LogError(ex); }   // silnik odtwarzacza filmow i TV
                var normal = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
                normal.Tick += (s2, e2) => { normal.Stop(); try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal; } catch (Exception) { } };
                normal.Start();
            };
            warm.Start();
        }

        void EnsureTray()
        {
            if (_tray != null) { _tray.Visible = true; return; }
            try
            {
                _trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
                // lagodny "oddech": 100% -> 65% -> 100% w 8 krokach (bez migania)
                _trayFrames = new[] { 1f, 0.92f, 0.82f, 0.72f, 0.65f, 0.72f, 0.82f, 0.92f }.Select(a => MakeDimIcon(_trayIcon, a)).ToArray();
                _tray = new System.Windows.Forms.NotifyIcon { Icon = _trayIcon, Text = "Velivo", Visible = true };
                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add(L.T("🌐 Uruchom przeglądarkę"), null, (s, e) => Dispatcher.Invoke(ShowFromTray));
                var filmItem = menu.Items.Add(L.T("🎬 Biblioteka filmów"), null, (s, e) => Dispatcher.Invoke(ShowVideoLibrary));
                var playerItem = menu.Items.Add(L.T("▶ Uruchom odtwarzacz wideo"), null, (s, e) => Dispatcher.Invoke(OpenEmptyPlayer));
                var tvItem = menu.Items.Add(L.T("📺 Uruchom TV"), null, (s, e) => Dispatcher.Invoke(StartTv));
                menu.Items.Add(L.T("🧹 Porządki i kopia bezpieczeństwa"), null, (s, e) => Dispatcher.Invoke(ShowTidy));
                menu.Items.Add(L.T("🕶 Otwórz kartę incognito"), null, (s, e) => Dispatcher.Invoke(() => { ShowFromTray(); PrivateTab_Click(this, new RoutedEventArgs()); }));
                menu.Items.Add(L.T("🏦 Otwórz w trybie bankowym"), null, (s, e) => Dispatcher.Invoke(() => { ShowFromTray(); OpenBankTab(); }));
                // jak przyciski na pasku Velivo: filmy i TV tylko, gdy sa wlaczone / zainstalowane
                menu.Opening += (s, e) => { tvItem.Visible = _tvBtn != null; filmItem.Visible = _filmBtn != null; playerItem.Visible = _playerBtn != null; };
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                menu.Items.Add(TrayMessengerItem());
                menu.Items.Add(TraySendFilesItem());
                menu.Items.Add(L.T("Synchronizuj teraz"), null, (s, e) => Dispatcher.Invoke(() => { TrayPulse(4); try { LanBroadcastState(true); } catch (Exception ex) { App.LogError(ex); } }));
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                menu.Items.Add(L.T("Zamknij Velivo całkowicie"), null, (s, e) => Dispatcher.Invoke(ExitFromTray));
                _tray.ContextMenuStrip = menu;
                _tray.MouseClick += (s, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) Dispatcher.Invoke(ShowFromTray); };
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        static System.Drawing.Icon MakeDimIcon(System.Drawing.Icon src, float alpha)
        {
            try
            {
                using (var bmp = src.ToBitmap())
                using (var dim = new System.Drawing.Bitmap(bmp.Width, bmp.Height))
                {
                    using (var g = System.Drawing.Graphics.FromImage(dim))
                    {
                        var cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha };
                        var ia = new System.Drawing.Imaging.ImageAttributes();
                        ia.SetColorMatrix(cm);
                        g.DrawImage(bmp, new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height), 0, 0, bmp.Width, bmp.Height, System.Drawing.GraphicsUnit.Pixel, ia);
                    }
                    return System.Drawing.Icon.FromHandle(dim.GetHicon());
                }
            }
            catch (Exception) { return src; }
        }

        // pulsowanie ikonki przez podana liczbe sekund (synchronizacja w toku)
        void TrayPulse(int seconds)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => TrayPulse(seconds))); return; }
            if (_tray == null || !_tray.Visible) return;
            // synchronizacja trwa ulamek sekundy - pokazujemy co najmniej 2 pelne oddechy (ok. 2 s), zeby bylo widac
            var until = DateTime.UtcNow.AddSeconds(Math.Max(seconds, 2));
            if (until > _trayPulseUntil) _trayPulseUntil = until;
            _tray.Text = L.T("Velivo – synchronizacja…");
            if (_trayPulse != null) return;
            int frame = 1;
            try { _tray.Icon = _trayFrames[frame]; } catch (Exception) { }   // od razu pierwszy krok - widac natychmiast
            _trayPulse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _trayPulse.Tick += (s, e) =>
            {
                try
                {
                    if (_tray == null) { _trayPulse.Stop(); _trayPulse = null; return; }
                    if (DateTime.UtcNow > _trayPulseUntil && frame == 0) { _tray.Icon = _trayIcon; _tray.Text = "Velivo"; _trayPulse.Stop(); _trayPulse = null; return; }
                    frame = (frame + 1) % _trayFrames.Length;
                    _tray.Icon = _trayFrames[frame];
                }
                catch (Exception) { }
            };
            _trayPulse.Start();
        }

        void ShowFromTray()
        {
            _inTray = false;
            Show();
            if (WindowState == WindowState.Minimized) WindowState = SilentStart ? WindowState.Maximized : WindowState.Normal;
            if (SilentStart) { SilentStart = false; ShowInTaskbar = true; try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal; } catch (Exception) { } }   // otwarte - pelna szybkosc od razu
            Activate();
            Topmost = true; Topmost = false;
        }

        void ExitFromTray()
        {
            _reallyExit = true;
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; } } catch (Exception) { }
            KomTrayRemove();
            Close();
            Application.Current.Shutdown();
        }

        // Po zamknieciu nic nie moze zostac w tle (watki odtwarzacza, synchronizacji, haki klawiatury) - inaczej proces
        // trzymal pliki danych, a kolejne uruchomienie oddawalo adresy niewidocznemu Velivo. Po 5 s konczymy proces na pewno.
        internal static void EnsureProcessEnds()
        {
            var t = new System.Threading.Thread(() => { System.Threading.Thread.Sleep(5000); Environment.Exit(0); }) { IsBackground = true };
            t.Start();
        }

        // przy wylaczeniu opcji w Ustawieniach - ikonka znika
        void UpdateTrayFromSettings()
        {
            if (_settings != null && !_settings.StayInTray && _tray != null && !_inTray) { _tray.Visible = false; }
        }

        const bool KomSamodzielny = false;   // pełne Velivo (osobny Komunikator ma true)
        static string KomVelivoExe => Environment.ProcessPath;
        // 💬 osobny program „Velivo Komunikator” zainstalowany na tym komputerze: on prowadzi rozmowy i serwer (jeden komunikator na komputer -
        // bez podwójnych wiadomości i dwóch programów piszących do tych samych danych); Velivo tylko go uruchamia
        static string _komAppExe; static DateTime _komAppAt;   // sprawdzane najwyżej co 30 s (pytane przy każdym pakiecie z sieci domowej)
        static string KomAppExe
        {
            get
            {
                if (DateTime.UtcNow - _komAppAt < TimeSpan.FromSeconds(30)) return _komAppExe;
                var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Velivo Komunikator", "VelivoKomunikator.exe");
                _komAppExe = File.Exists(p) ? p : null; _komAppAt = DateTime.UtcNow; return _komAppExe;
            }
        }
        static bool KomOddany => KomAppExe != null;
        static bool KomOldPortFree => true;
        void KomSyncIn(byte[] state) => _ = HandleLanPacketAsync(state, null, true);   // 🌍 stan synchronizacji z internetu - sprawdzany jak z sieci domowej (klucz powiązania)
        // osobny Komunikator odebrał stan synchronizacji z internetu - zostawia go w folderze danych, Velivo przyjmuje (także zaległe po starcie)
        System.IO.FileSystemWatcher _komSyncWatch;
        void KomSyncInWatch()
        {
            if (_komSyncWatch != null) return;
            try
            {
                Directory.CreateDirectory(KomSyncInDir);
                void Take() { foreach (var f in Directory.GetFiles(KomSyncInDir, "*.json")) try { var b = File.ReadAllBytes(f); File.Delete(f); KomSyncIn(b); } catch (Exception) { } }
                _komSyncWatch = new System.IO.FileSystemWatcher(KomSyncInDir, "*.json") { EnableRaisingEvents = true };
                _komSyncWatch.Created += (s, e) => Dispatcher.BeginInvoke(new Action(async () => { await System.Threading.Tasks.Task.Delay(300); Take(); }));
                Take();
            }
            catch (Exception ex) { App.LogError(ex); }
        }   // 41919: Velivo bierze go samo (synchronizacja), gdy synchronizacja wyłączona - komunikator
        static void KomAppRun(bool silent)
        {
            try
            {
                if (silent && System.Diagnostics.Process.GetProcessesByName("VelivoKomunikator").Length > 0) return;   // już działa
                System.Diagnostics.Process.Start(KomAppExe, silent ? "--autostart" : "");   // działający - pokaże swoje okno
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        // menu zasobnika: 💬 Komunikator
        System.Windows.Forms.ToolStripMenuItem TrayMessengerItem()
        {
            var it = new System.Windows.Forms.ToolStripMenuItem(L.T("💬 Komunikator"));
            it.Click += (s, e) => Dispatcher.Invoke(() => ShowMessenger());
            return it;
        }

        // 💬 na pasku Velivo obok 📎
        void InitMessengerButton()
        {
            var b = new System.Windows.Controls.Button { Content = "💬", FontSize = 16, Width = 40, Height = 36, Margin = new Thickness(2, 3, 2, 0), ToolTip = L.T("Komunikator Velivo – rozmowy, pliki i wideo w domu i przez internet") };
            System.Windows.Controls.DockPanel.SetDock(b, System.Windows.Controls.Dock.Right);
            b.Click += (s, e) => ShowMessenger();
            int at = _sendFileBtn != null ? TabBarPanel.Children.IndexOf(_sendFileBtn) : -1;
            TabBarPanel.Children.Insert(at >= 0 ? at : 1, b);
        }
    }
}
