using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Velivo
{
    // 💬 Bąbelki rozmów na pulpicie (jak Messenger): okrągłe zdjęcie kontaktu nad wszystkimi programami, licznik nowych,
    // podskok przy wiadomości, klik - rozmowa w okienku na wierzchu (KomMini), przeciągnięcie na ✕ na dole ekranu - zamyka.
    // Lista bąbelków i miejsca w _komOpt["babelki"] (id -> "x;y") - wracają po ponownym uruchomieniu.
    public partial class MainWindow
    {
        readonly Dictionary<string, Window> _komBubbles = new Dictionary<string, Window>();
        readonly Dictionary<string, int> _komBubbleSeen = new Dictionary<string, int>();
        JsonObject KomBubbleOpt => _komOpt["babelki"] as JsonObject ?? (JsonObject)(_komOpt["babelki"] = new JsonObject());
        bool KomHasBubble(string id) => KomBubbleOpt.ContainsKey(id);

        void KomBubbleToggle(KomContact c)
        {
            if (KomHasBubble(c.Id)) { KomBubbleOpt.Remove(c.Id); KomSave(); if (_komBubbles.TryGetValue(c.Id, out var w)) w.Close(); return; }
            var a = SystemParameters.WorkArea; int n = KomBubbleOpt.Count;
            KomBubbleOpt[c.Id] = (int)(a.Right - 76) + ";" + (int)(a.Top + 120 + n * 72); KomSave();
            KomBubblesUpdate();
        }

        // po każdej zmianie nieprzeczytanych (KomTrayUpdate): brakujące bąbelki tworzy, licznik odświeża, nowa wiadomość - podskok
        void KomBubblesUpdate()
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    foreach (var id in KomBubbleOpt.Select(kv => kv.Key).ToList())
                    {
                        var c = KomContactById(id); if (c == null) { KomBubbleOpt.Remove(id); continue; }
                        if (!_komBubbles.ContainsKey(id)) KomBubbleShow(c);
                    }
                    foreach (var kv in _komBubbles.ToList())
                    {
                        int un = _komUnread.TryGetValue(kv.Key, out var u) ? u : 0;
                        if (kv.Value.Tag is Action<int> set) set(un);
                        bool more = un > (_komBubbleSeen.TryGetValue(kv.Key, out var s) ? s : 0); _komBubbleSeen[kv.Key] = un;
                        if (more) KomBubbleBounce(kv.Value);
                    }
                }
                catch (Exception ex) { App.LogError(ex); }
            }));
        }

        void KomBubbleBounce(Window w)
        {
            if (w.Content is not FrameworkElement el) return;
            var tt = el.RenderTransform as TranslateTransform ?? new TranslateTransform(); el.RenderTransform = tt;
            var an = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(900) };
            foreach (var (t, y) in new[] { (0, 0.0), (150, -14.0), (300, 0.0), (420, -7.0), (540, 0.0), (640, -3.0), (740, 0.0) })
                an.KeyFrames.Add(new EasingDoubleKeyFrame(y, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t)), new QuadraticEase()));
            tt.BeginAnimation(TranslateTransform.YProperty, an);
        }

        void KomBubbleShow(KomContact c)
        {
            var pal = KomCurrentPal();
            const double S = 60;
            var w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
                Topmost = true, ShowInTaskbar = false, ShowActivated = false, Width = S + 16, Height = S + 24, Title = "Velivo – " + c.Name
            };
            var face = KomAvatarEl(c, KomOnline(c.Id), S, pal);   // zdjęcie albo litera, zielona obwódka - w sieci
            face.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, Opacity = 0.45, ShadowDepth = 2 };
            var badgeT = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var badge = new Border { Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), CornerRadius = new CornerRadius(11), MinWidth = 22, Height = 22, Padding = new Thickness(5, 0, 5, 0),
                BorderBrush = Brushes.White, BorderThickness = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Child = badgeT, Visibility = Visibility.Collapsed };
            var grid = new Grid { Margin = new Thickness(8, 12, 8, 12), Cursor = Cursors.Hand, ToolTip = c.Name + "\n" + L.T("Klik – rozmowa · przeciągnij, gdzie chcesz · upuść na ✕ na dole – zamknij") };
            grid.Children.Add(face); grid.Children.Add(badge);
            w.Content = grid;
            w.Tag = (Action<int>)(n => { badgeT.Text = n > 99 ? "99+" : n.ToString(); badge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed; });
            var pos = ((string)KomBubbleOpt[c.Id] ?? "").Split(';');
            var area = SystemParameters.WorkArea;
            w.Left = pos.Length == 2 && double.TryParse(pos[0], out var x) ? x : area.Right - S - 16;
            w.Top = pos.Length == 2 && double.TryParse(pos[1], out var y) ? y : area.Top + 120;
            if (w.Left < SystemParameters.VirtualScreenLeft || w.Left > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 20) w.Left = area.Right - S - 16;   // monitor odłączony - wraca na główny
            if (w.Top < SystemParameters.VirtualScreenTop || w.Top > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 20) w.Top = area.Top + 120;

            // przeciąganie: własne (DragMove nie odróżnia kliknięcia); w trakcie na dole ekranu ✕ do zamknięcia
            Point start = default; bool down = false, moved = false; Window bin = null;
            bool OverBin() => bin != null && w.Left + w.Width / 2 > bin.Left && w.Left + w.Width / 2 < bin.Left + bin.Width && w.Top + w.Height / 2 > bin.Top - 30;
            grid.MouseLeftButtonDown += (s, e) => { down = true; moved = false; start = e.GetPosition(w); grid.CaptureMouse(); e.Handled = true; };
            grid.MouseMove += (s, e) =>
            {
                if (!down) return;
                var p = e.GetPosition(w); var d = p - start;
                if (!moved && Math.Abs(d.X) + Math.Abs(d.Y) < 6) return;
                if (!moved) { moved = true; bin = KomBubbleBin(); }
                w.Left += d.X; w.Top += d.Y;
                if (bin?.Content is Border bb) bb.Background = new SolidColorBrush(OverBin() ? Color.FromRgb(0xEF, 0x44, 0x44) : Color.FromArgb(0xCC, 0x1F, 0x29, 0x37));
            };
            grid.MouseLeftButtonUp += (s, e) =>
            {
                if (!down) return; down = false; grid.ReleaseMouseCapture();
                if (!moved) { KomMini(c); return; }
                bool close = OverBin(); bin?.Close(); bin = null;
                if (close) { KomBubbleOpt.Remove(c.Id); KomSave(); w.Close(); return; }
                // przyciąga do bliższej krawędzi ekranu (jak w telefonie)
                var wa = SystemParameters.WorkArea;
                w.Left = w.Left + w.Width / 2 < (wa.Left + wa.Right) / 2 ? wa.Left + 4 : wa.Right - w.Width - 4;
                w.Top = Math.Max(wa.Top, Math.Min(wa.Bottom - w.Height, w.Top));
                KomBubbleOpt[c.Id] = (int)w.Left + ";" + (int)w.Top; KomSave();
            };
            grid.LostMouseCapture += (s, e) => { if (down && !moved) down = false; };
            var cm = new ContextMenu();
            var mOpen = new MenuItem { Header = L.T("💬 Otwórz rozmowę w Velivo") }; mOpen.Click += (s, e) => ShowMessenger(c.Id);
            var mClose = new MenuItem { Header = L.T("✕ Zamknij bąbelek") }; mClose.Click += (s, e) => { KomBubbleOpt.Remove(c.Id); KomSave(); w.Close(); };
            cm.Items.Add(mOpen); cm.Items.Add(mClose); grid.ContextMenu = cm;
            // zielona obwódka (w sieci) - co 30 s od nowa
            var tm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            bool lastOn = KomOnline(c.Id);
            tm.Tick += (s, e) => { bool on = KomOnline(c.Id); if (on == lastOn) return; lastOn = on; var f2 = KomAvatarEl(c, on, S, pal); f2.Effect = face.Effect; grid.Children.Remove(face); face = f2; grid.Children.Insert(0, face); };
            tm.Start();
            w.Closed += (s, e) => { tm.Stop(); _komBubbles.Remove(c.Id); _komBubbleSeen.Remove(c.Id); };
            _komBubbles[c.Id] = w; _komBubbleSeen[c.Id] = _komUnread.TryGetValue(c.Id, out var u0) ? u0 : 0;
            w.Show();
            ((Action<int>)w.Tag)(_komBubbleSeen[c.Id]);
        }

        // ✕ na dole ekranu, widoczny tylko w czasie przeciągania bąbelka
        Window KomBubbleBin()
        {
            var a = SystemParameters.WorkArea;
            var b = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, Topmost = true,
                ShowInTaskbar = false, ShowActivated = false, Width = 64, Height = 64, Left = (a.Left + a.Right) / 2 - 32, Top = a.Bottom - 84, IsHitTestVisible = false,
                Content = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1F, 0x29, 0x37)), BorderBrush = Brushes.White, BorderThickness = new Thickness(2),
                    Child = new TextBlock { Text = "✕", Foreground = Brushes.White, FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } }
            };
            b.Show(); return b;
        }
    }
}
