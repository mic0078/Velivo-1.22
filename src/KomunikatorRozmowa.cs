using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Velivo
{
    // 📹 Rozmowa wideo w komunikatorze: obraz i dźwięk WebRTC prosto między urządzeniami (strona Rozmowa\rozmowa.html,
    // ta sama co w telefonie). Zestawienie połączenia (dzwonię, odbieram, opis połączenia) - zaszyfrowane wiadomości
    // komunikatora z kluczem kontaktu, w domu i przez pośrednika; nie trafiają do rozmowy tekstowej.
    // Kamera i mikrofon tylko w oknie rozmowy, tylko dla tej strony.
    public partial class MainWindow
    {
        const string KomCallTag = "\u0001velivo-rozmowa\n";
        Window _komCallWin; WebView2 _komCallView; string _komCallWith; Window _komRingWin;

        // poza domem: przekaźnik TURN na domowym komputerze (pośredniku), z którym jesteśmy połączeni
        string KomTurnIce()
        {
            var r = _komUpRelay;
            if (_komUp == null || r?.Relay == null) return "[]";
            return new JsonArray { new JsonObject { ["urls"] = "turn:" + r.Relay + "?transport=tcp", ["username"] = _komId, ["credential"] = KomTurnPass(r.Key) } }.ToJsonString();
        }

        void KomSendCall(KomContact c, JsonObject o) =>
            KomDeliver(c, new KomMsg { Mine = true, Text = KomCallTag + o.ToJsonString(), Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mid = Guid.NewGuid().ToString("N") });

        void KomOnCall(KomContact c, string json)
        {
            JsonNode o; try { o = JsonNode.Parse(json); } catch (Exception) { return; }
            var k = (string)o?["k"];
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (k == "ring") { if (_komCallWin != null) KomSendCall(c, new JsonObject { ["k"] = "busy" }); else KomIncoming(c, (bool?)o["a"] == true); return; }
                if (k == "end" && _komCallWin == null) { _komRingWin?.Close(); return; }
                if (_komCallWith == c.Id) try { _komCallView?.CoreWebView2?.PostWebMessageAsString(json); } catch (Exception) { }
            }));
        }

        // ktoś dzwoni: okienko w kolorach komunikatora, nad innymi programami, dzwonek do 45 s
        void KomIncoming(KomContact c, bool audio)
        {
            if (_komRingWin != null) return;
            var pal = KomCurrentPal();
            var w = new Window { Uid = "own-theme", Title = "Velivo", Width = 340, SizeToContent = SizeToContent.Height, Topmost = true, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = new SolidColorBrush(pal.Panel), ShowInTaskbar = true };
            w.SourceInitialized += (s, e) => { try { var h = new System.Windows.Interop.WindowInteropHelper(w).Handle; int on = pal.Dark ? 1 : 0; DwmSetWindowAttribute(h, 20, ref on, 4); } catch (Exception) { } };   // pasek tytułu w kolorze motywu
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = (audio ? "📞 " : "📹 ") + c.Name, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(pal.Fg), HorizontalAlignment = HorizontalAlignment.Center });
            panel.Children.Add(new TextBlock { Text = L.T("dzwoni…"), FontSize = 14, Foreground = new SolidColorBrush(pal.Dim), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 16) });
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            Button Btn(string text, string tip, Color bg) => new Button { Content = text, ToolTip = tip, Width = 120, Height = 40, Margin = new Thickness(8, 0, 8, 0), FontSize = 15,
                Foreground = Brushes.White, Background = new SolidColorBrush(bg), BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
            var no = Btn("✕ " + L.T("Odrzuć"), L.T("Odrzuć rozmowę"), Color.FromRgb(0xE5, 0x39, 0x35));
            var yes = Btn("📞 " + L.T("Odbierz"), L.T("Odbierz rozmowę"), Color.FromRgb(0x22, 0xC5, 0x5E));
            row.Children.Add(no); row.Children.Add(yes); panel.Children.Add(row); w.Content = panel;
            bool answered = false;
            no.Click += (s, e) => w.Close();
            yes.Click += (s, e) => { answered = true; w.Close(); KomOpenCall(c, false, audio); };
            var ring = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            var start = DateTime.UtcNow;
            ring.Tick += (s, e) => { if (DateTime.UtcNow - start > TimeSpan.FromSeconds(45)) { w.Close(); return; } try { System.Media.SystemSounds.Asterisk.Play(); } catch (Exception) { } };
            w.Closed += (s, e) => { ring.Stop(); _komRingWin = null; if (!answered) KomSendCall(c, new JsonObject { ["k"] = "reject" }); };
            _komRingWin = w; w.Show(); w.Activate(); ring.Start();
            try { System.Media.SystemSounds.Asterisk.Play(); } catch (Exception) { }
        }

        async void KomOpenCall(KomContact c, bool caller, bool audio = false)
        {
            if (c == null || _komCallWin != null || _env == null) return;
            var dir = Path.Combine(AppContext.BaseDirectory, "Rozmowa");
            if (!File.Exists(Path.Combine(dir, "rozmowa.html"))) { MessageBox.Show(this, L.T("Brak pliku rozmowy wideo – zainstaluj Velivo ponownie."), "Velivo"); return; }
            var view = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(18, 15, 36) };
            var w = new Window { Uid = "own-theme", Title = (audio ? "📞 " : "📹 ") + c.Name + " – Velivo", Width = audio ? 420 : 900, Height = audio ? 360 : 600, Content = view, Background = new SolidColorBrush(Color.FromRgb(18, 15, 36)), WindowStartupLocation = WindowStartupLocation.CenterScreen };
            _komCallWin = w; _komCallView = view; _komCallWith = c.Id;
            bool ended = false;
            w.Closed += (s, e) =>
            {
                if (!ended) KomSendCall(c, new JsonObject { ["k"] = "end" });
                _komCallWin = null; _komCallView = null; _komCallWith = null; KomCtlOff();
                try { view.Dispose(); } catch (Exception) { }
            };
            w.Show();
            try
            {
                await view.EnsureCoreWebView2Async(_env);
                var core = view.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false; core.Settings.IsStatusBarEnabled = false;
                core.SetVirtualHostNameToFolderMapping("velivo-rozmowa", dir, CoreWebView2HostResourceAccessKind.Deny);
                // kamera i mikrofon - tylko strona rozmowy
                core.PermissionRequested += (s, e) =>
                {
                    bool own = e.Uri.StartsWith("https://velivo-rozmowa/", StringComparison.OrdinalIgnoreCase);
                    if (own && (e.PermissionKind == CoreWebView2PermissionKind.Camera || e.PermissionKind == CoreWebView2PermissionKind.Microphone)) e.State = CoreWebView2PermissionState.Allow;
                    else e.State = CoreWebView2PermissionState.Deny;
                };
                core.NewWindowRequested += (s, e) => e.Handled = true;
                core.NavigationStarting += (s, e) => { if (!e.Uri.StartsWith("https://velivo-rozmowa/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true; };
                core.WebMessageReceived += (s, e) =>
                {
                    string msg; try { msg = e.TryGetWebMessageAsString(); } catch (Exception) { return; }
                    JsonObject o; try { o = JsonNode.Parse(msg) as JsonObject; } catch (Exception) { return; }
                    var k = (string)o?["k"]; if (k == null) return;
                    if (k == "close") { w.Close(); return; }
                    if (k == "in") { KomCtlInput(o); return; }   // 🖱 pomoc zdalna - tylko u siebie, nie do rozmówcy
                    if (k == "ctl-on") { KomCtlOn(c, (int?)o["w"] ?? 0, (int?)o["h"] ?? 0, j => { try { core.PostWebMessageAsString(j); } catch (Exception) { } }); return; }
                    if (k == "ctl-off") { KomCtlOff(); return; }
                    if (k == "end") ended = true;
                    KomSendCall(c, o);
                };
                core.Navigate("https://velivo-rozmowa/rozmowa.html?rola=" + (caller ? "dzwoni" : "odbiera") + "&ice=" + Uri.EscapeDataString(KomTurnIce()) + "&pl=" + (L.En ? "0" : "1") + (audio ? "&audio=1" : ""));
            }
            catch (Exception ex) { App.LogError(ex); w.Close(); }
        }

        // 🎬 wiadomość wideo z kamery Windows (Rozmowa\nagraj.html); nagranie zostaje w Velivo - odtworzenie u siebie
        async void KomRecordVideo(Window owner, Action<string> send, bool photo = false)
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Rozmowa");
            if (_env == null || !File.Exists(Path.Combine(dir, "nagraj.html"))) { MessageBox.Show(owner, L.T("Brak pliku rozmowy wideo – zainstaluj Velivo ponownie."), "Velivo"); return; }
            var view = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(18, 15, 36) };
            var w = new Window { Uid = "own-theme", Title = (photo ? "📷 " + L.T("Zrób zdjęcie") : "🎬 " + L.T("Nagraj wiadomość wideo")) + " – Velivo", Width = 720, Height = 520, Content = view, Owner = owner, Background = new SolidColorBrush(Color.FromRgb(18, 15, 36)), WindowStartupLocation = WindowStartupLocation.CenterOwner };
            w.Closed += (s, e) => { try { view.Dispose(); } catch (Exception) { } };
            w.Show();
            try
            {
                await view.EnsureCoreWebView2Async(_env);
                var core = view.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false; core.Settings.IsStatusBarEnabled = false;
                core.SetVirtualHostNameToFolderMapping("velivo-rozmowa", dir, CoreWebView2HostResourceAccessKind.Deny);
                core.PermissionRequested += (s, e) => e.State = e.Uri.StartsWith("https://velivo-rozmowa/", StringComparison.OrdinalIgnoreCase) && (e.PermissionKind == CoreWebView2PermissionKind.Camera || e.PermissionKind == CoreWebView2PermissionKind.Microphone) ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                core.NewWindowRequested += (s, e) => e.Handled = true;
                core.NavigationStarting += (s, e) => { if (!e.Uri.StartsWith("https://velivo-rozmowa/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true; };
                core.WebMessageReceived += (s, e) =>
                {
                    JsonObject o; try { o = JsonNode.Parse(e.TryGetWebMessageAsString()) as JsonObject; } catch (Exception) { return; }
                    var k = (string)o?["k"];
                    if ((k == "video" || k == "photo") && o["d"] is JsonNode d)
                    {
                        try
                        {
                            Directory.CreateDirectory(KomMediaDir);
                            var p = Path.Combine(KomMediaDir, (k == "photo" ? "Zdjecie-" : "Wideo-") + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + (k == "photo" ? ".jpg" : ".webm"));
                            File.WriteAllBytes(p, Convert.FromBase64String((string)d)); send(p);
                        }
                        catch (Exception ex) { App.LogError(ex); }
                    }
                    if (k == "video" || k == "photo" || k == "close") w.Close();
                };
                core.Navigate("https://velivo-rozmowa/nagraj.html?pl=" + (L.En ? "0" : "1") + (photo ? "&foto=1" : ""));
            }
            catch (Exception ex) { App.LogError(ex); w.Close(); }
        }
    }
}
