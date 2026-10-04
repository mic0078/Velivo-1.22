using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace Przegladarka
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
                menu.Items.Add(L.T("Otwórz Velivo"), null, (s, e) => Dispatcher.Invoke(ShowFromTray));
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
            _trayPulseUntil = DateTime.UtcNow.AddSeconds(seconds);
            _tray.Text = L.T("Velivo – synchronizacja…");
            if (_trayPulse != null) return;
            int frame = 0;
            _trayPulse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
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
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true; Topmost = false;
        }

        void ExitFromTray()
        {
            _reallyExit = true;
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; } } catch (Exception) { }
            Close();
            Application.Current.Shutdown();
        }

        // przy wylaczeniu opcji w Ustawieniach - ikonka znika
        void UpdateTrayFromSettings()
        {
            if (_settings != null && !_settings.StayInTray && _tray != null && !_inTray) { _tray.Visible = false; }
        }
    }
}
