using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Przegladarka
{
    // Zrzut ekranu strony: widoczna czesc albo cala strona (przewijana). Zapis do Obrazy\Zrzuty Velivo
    // i kopia w schowku; potem dymek z przyciskami "Otworz" i "Pokaz w folderze".
    public partial class MainWindow
    {
        static string ShotsDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Zrzuty Velivo"); } }

        void ShotBtn_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = ShotBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            var visible = new MenuItem { Header = L.T("Widoczna część strony") }; visible.Click += async (s, a) => await TakeScreenshot(false);
            var full = new MenuItem { Header = L.T("Cała strona (z przewijaniem)") }; full.Click += async (s, a) => await TakeScreenshot(true);
            var folder = new MenuItem { Header = L.T("Otwórz folder ze zrzutami") };
            folder.Click += (s, a) => { Directory.CreateDirectory(ShotsDir); Process.Start("explorer.exe", ShotsDir); };
            menu.Items.Add(visible); menu.Items.Add(full); menu.Items.Add(new Separator()); menu.Items.Add(folder);
            menu.IsOpen = true;
        }

        async Task TakeScreenshot(bool fullPage)
        {
            var core = Core;
            if (core == null) return;
            try
            {
                byte[] png;
                if (fullPage)
                {
                    var metricsJson = await core.CallDevToolsProtocolMethodAsync("Page.getLayoutMetrics", "{}");
                    double w, h;
                    using (var m = JsonDocument.Parse(metricsJson))
                    {
                        var size = m.RootElement.TryGetProperty("cssContentSize", out var cs) ? cs : m.RootElement.GetProperty("contentSize");
                        w = size.GetProperty("width").GetDouble();
                        h = size.GetProperty("height").GetDouble();
                    }
                    h = Math.Min(h, 16000); // bardzo dlugie strony przycinamy (limit karty graficznej)
                    var args = "{\"format\":\"png\",\"captureBeyondViewport\":true,\"clip\":{\"x\":0,\"y\":0,\"width\":" +
                               ((int)Math.Ceiling(w)) + ",\"height\":" + ((int)Math.Ceiling(h)) + ",\"scale\":1}}";
                    var shot = await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", args);
                    using (var d = JsonDocument.Parse(shot)) png = Convert.FromBase64String(d.RootElement.GetProperty("data").GetString());
                }
                else
                {
                    using (var ms = new MemoryStream())
                    {
                        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                        png = ms.ToArray();
                    }
                }

                Directory.CreateDirectory(ShotsDir);
                var title = new string((core.DocumentTitle ?? "strona").Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
                if (title.Length > 60) title = title.Substring(0, 60).Trim();
                var file = Path.Combine(ShotsDir, "Velivo " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + (title.Length > 0 ? " – " + title : "") + ".png");
                File.WriteAllBytes(file, png);

                bool copied = false;
                try
                {
                    var img = new BitmapImage();
                    img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.StreamSource = new MemoryStream(png); img.EndInit();
                    Clipboard.SetImage(img);
                    copied = true;
                }
                catch (Exception) { }
                ShowToast("📷 Zapisano zrzut " + (fullPage ? L.T("całej strony") : L.T("widocznej części")) + (copied ? L.T(" i skopiowano do schowka.") : "."), file);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                MessageBox.Show(this, L.T("Nie udało się zrobić zrzutu ekranu:\n") + ex.Message, "Velivo");
            }
        }

        // Komunikaty Velivo: na srodku okna, tuz NAD paskiem zadan Windows (nigdy na nim) - takze przy oknie zmaksymalizowanym
        void PlaceToast(Window toast)
        {
            try
            {
                var src = PresentationSource.FromVisual(this);
                var tl = PointToScreen(new Point(0, 0)); var br = PointToScreen(new Point(ActualWidth, ActualHeight));
                var wa = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle).WorkingArea;
                Point waTl = new Point(wa.Left, wa.Top), waBr = new Point(wa.Right, wa.Bottom);
                if (src != null && src.CompositionTarget != null)
                {
                    var m = src.CompositionTarget.TransformFromDevice;
                    tl = m.Transform(tl); br = m.Transform(br); waTl = m.Transform(waTl); waBr = m.Transform(waBr);
                }
                double left = Math.Max(tl.X, waTl.X), right = Math.Min(br.X, waBr.X), bottom = Math.Min(br.Y, waBr.Y);
                if (right - left < toast.ActualWidth) { left = waTl.X; right = waBr.X; }
                toast.Left = left + (right - left - toast.ActualWidth) / 2;
                toast.Top = Math.Max(waTl.Y, bottom - toast.ActualHeight - 16);
            }
            catch (Exception) { }
        }

        // Dymek na dole okna, nad paskiem zadan (osobne okienko - nad strona WWW nie da sie nic narysowac).
        void ShowToast(string text, string file)
        {
            var panel = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
            panel.Children.Add(new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 14, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var toast = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Owner = this, Topmost = true,
                SizeToContent = SizeToContent.WidthAndHeight, Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)),
                ShowActivated = false, Content = panel
            };
            if (file != null)
            {
                var open = SmallButton(L.T("Otwórz"), () => { try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); } catch (Exception) { } toast.Close(); });
                var show = SmallButton(L.T("Pokaż w folderze"), () => { Process.Start("explorer.exe", "/select,\"" + file + "\""); toast.Close(); });
                buttons.Children.Add(open); buttons.Children.Add(show);
                panel.Children.Add(buttons);
            }
            toast.Loaded += (s, e) => PlaceToast(toast);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
            timer.Tick += (s, e) => { timer.Stop(); try { toast.Close(); } catch (InvalidOperationException) { } };
            toast.Closed += (s, e) => timer.Stop();
            toast.Show();
            timer.Start();
        }
    }
}
