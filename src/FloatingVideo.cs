using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Przegladarka
{
    // "Film na wierzchu": wlasne okienko Velivo z samym filmem, zawsze na wierzchu, dowolnie male (od 160x90).
    // Dziala niezaleznie od kart - zamkniecie karty nie zatrzymuje filmu; zamkniecie okienka - tak.
    public partial class MainWindow
    {
        sealed class FloatRequest { public string page { get; set; } public double time { get; set; } public string title { get; set; } }

        // z karty: biezacy film (strona + miejsce), film w karcie pauzujemy
        async void FloatVideoFromTab(BrowserTab tab)
        {
            var core = tab != null ? tab.View.CoreWebView2 : null;
            if (core == null) return;
            try
            {
                var raw = await core.ExecuteScriptAsync("(function(){var v=Array.prototype.slice.call(document.querySelectorAll('video')).sort(function(a,b){return b.clientWidth*b.clientHeight-a.clientWidth*a.clientHeight;})[0];var t=v?v.currentTime:0;if(v)try{v.pause();}catch(e){}return JSON.stringify({page:location.href,time:t,title:document.title||''});})()");
                var r = JsonSerializer.Deserialize<FloatRequest>(JsonSerializer.Deserialize<string>(raw));
                if (r != null) ShowFloatingVideo(r.page, r.time, r.title, tab.Private);
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        void HandleFloatRequest(BrowserTab tab, string json)
        {
            try
            {
                var r = JsonSerializer.Deserialize<FloatRequest>(json);
                if (r == null) return;
                var core = tab.View.CoreWebView2;
                if (core != null) _ = core.ExecuteScriptAsync("(function(){var v=Array.prototype.slice.call(document.querySelectorAll('video')).sort(function(a,b){return b.clientWidth*b.clientHeight-a.clientWidth*a.clientHeight;})[0];if(v)try{v.pause();}catch(e){}})()");
                ShowFloatingVideo(r.page, r.time, r.title, tab.Private);
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        // Strona z filmem w okienku: film na cala powierzchnie, reszta strony ukryta.
        const string FloatPageScript = @"(function(t){
  function big(){ var vs=Array.prototype.slice.call(document.querySelectorAll('video')); return vs.sort(function(a,b){return (b.videoWidth*b.videoHeight||b.clientWidth*b.clientHeight)-(a.videoWidth*a.videoHeight||a.clientWidth*a.clientHeight);})[0]; }
  var st=document.getElementById('velivo-film'); if(!st){ st=document.createElement('style'); st.id='velivo-film'; (document.head||document.documentElement).appendChild(st); }
  st.textContent='html,body{overflow:hidden!important;background:#000!important}body *{visibility:hidden!important}' +
    'video.velivo-film{visibility:visible!important;position:fixed!important;left:0!important;top:0!important;width:100vw!important;height:100vh!important;max-width:none!important;max-height:none!important;z-index:2147483647!important;background:#000!important;object-fit:contain!important;transform:none!important}';
  var seeked=false;
  function fix(){ var v=big(); if(!v) return; if(!v.classList.contains('velivo-film')){ document.querySelectorAll('video.velivo-film').forEach(function(x){x.classList.remove('velivo-film');}); v.classList.add('velivo-film'); }
    if(!seeked && v.readyState>0){ seeked=true; if(t>3 && Math.abs(v.currentTime-t)>3) try{v.currentTime=t;}catch(e){} }
    if(v.paused && !v.__velivoUserPaused) v.play().catch(function(){}); }
  document.addEventListener('pause', function(e){ if(e.target && e.target.tagName==='VIDEO' && e.isTrusted && document.hasFocus()) e.target.__velivoUserPaused=true; }, true);
  document.addEventListener('play', function(e){ if(e.target && e.target.tagName==='VIDEO') e.target.__velivoUserPaused=false; }, true);
  fix(); setInterval(fix, 1000);
  document.addEventListener('click', function(e){ var v=big(); if(!v) return; e.preventDefault(); e.stopPropagation(); if(v.paused){ v.play(); } else { v.pause(); } }, true);
})";

        void ShowFloatingVideo(string page, double time, string title, bool isPrivate)
        {
            if (string.IsNullOrEmpty(page) || !(page.StartsWith("http://") || page.StartsWith("https://"))) return;
            var view = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Black };

            // gorny pasek: tytul (przeciaganie okienka), zamkniecie; reszta to film
            var titleText = new TextBlock { Text = string.IsNullOrWhiteSpace(title) ? "Velivo" : title, Foreground = Brushes.White, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var close = new Button { Content = "✕", Width = 28, Height = 22, Foreground = Brushes.White, Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontSize = 12, Cursor = Cursors.Hand, ToolTip = L.T("Zamknij film") };
            var back = new Button { Content = "↩", Width = 28, Height = 22, Foreground = Brushes.White, Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontSize = 13, Cursor = Cursors.Hand, ToolTip = L.T("Wróć do karty (otwórz stronę w Velivo)") };
            WindowChrome.SetIsHitTestVisibleInChrome(close, true);
            WindowChrome.SetIsHitTestVisibleInChrome(back, true);
            var bar = new DockPanel { Height = 24, Background = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)) };
            DockPanel.SetDock(close, Dock.Right); DockPanel.SetDock(back, Dock.Right);
            bar.Children.Add(close); bar.Children.Add(back); bar.Children.Add(titleText);
            var root = new DockPanel { Background = Brushes.Black };
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar); root.Children.Add(view);

            var b = _settings.FloatBounds;
            var win = new Window
            {
                Title = (string.IsNullOrWhiteSpace(title) ? "" : title + " – ") + L.T("Film na wierzchu"),
                Topmost = true, ShowInTaskbar = true, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.CanResize,
                MinWidth = 160, MinHeight = 90 + 24, Width = 480, Height = 270 + 24, Background = Brushes.Black, Content = root,
                Icon = Icon,
            };
            // ramka do zmiany rozmiaru i przeciaganie za gorny pasek (bez systemowej belki)
            WindowChrome.SetWindowChrome(win, new WindowChrome { CaptionHeight = 24, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0) });
            if (!string.IsNullOrEmpty(b))
            {
                var p = b.Split(';');
                double l, t, w, h;
                if (p.Length == 4 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out l) && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out t) &&
                    double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out w) && double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out h) &&
                    l > SystemParameters.VirtualScreenLeft - 50 && t > SystemParameters.VirtualScreenTop - 50 && l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80 && t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 60)
                { win.WindowStartupLocation = WindowStartupLocation.Manual; win.Left = l; win.Top = t; win.Width = Math.Max(160, w); win.Height = Math.Max(114, h); }
            }
            else
            {
                win.WindowStartupLocation = WindowStartupLocation.Manual;
                win.Left = SystemParameters.WorkArea.Right - win.Width - 24;
                win.Top = SystemParameters.WorkArea.Bottom - win.Height - 24;
            }
            close.Click += (s, e) => win.Close();
            back.Click += async (s, e) =>
            {
                double now = 0;
                try
                {
                    if (view.CoreWebView2 != null)
                        double.TryParse(await view.CoreWebView2.ExecuteScriptAsync("(function(){var v=document.querySelector('video.velivo-film');return v?v.currentTime:0;})()"), NumberStyles.Float, CultureInfo.InvariantCulture, out now);
                }
                catch (Exception) { }
                var url = view.CoreWebView2 != null ? view.CoreWebView2.Source : page;
                AddTab(url, isPrivate);
                if (now > 0) _tabs[_tabs.Count - 1].PendingVideoTime = now;
                win.Close();
                Activate();
            };
            win.Closed += (s, e) =>
            {
                _settings.FloatBounds = string.Join(";", new[] { win.Left, win.Top, win.Width, win.Height }.Select(x => x.ToString("0", CultureInfo.InvariantCulture)));
                try { _settings.Save(DataDir); } catch (Exception) { }
                try { view.Dispose(); } catch (Exception) { }
            };
            win.Show();
            _ = InitFloatingView(view, page, time, isPrivate, titleText);
        }

        async Task InitFloatingView(WebView2 view, string page, double time, bool isPrivate, TextBlock titleText)
        {
            try
            {
                var opts = _env.CreateCoreWebView2ControllerOptions();
                opts.IsInPrivateModeEnabled = isPrivate;
                await view.EnsureCoreWebView2Async(_env, opts);
                var core = view.CoreWebView2;
                ApplyViewSettings(core);
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.NewWindowRequested += (s, e) => e.Handled = true;   // reklamy i linki z okienka nie otwieraja okien
                core.DocumentTitleChanged += (s, e) => titleText.Text = core.DocumentTitle;
                core.NavigationCompleted += async (s, e) =>
                {
                    try { await core.ExecuteScriptAsync(FloatPageScript + "(" + time.ToString(CultureInfo.InvariantCulture) + ")"); } catch (Exception) { }
                };
                core.Navigate(page);
            }
            catch (Exception ex) { App.LogError(ex); }
        }
    }
}
