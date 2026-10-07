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
                if (core != null) _ = core.ExecuteScriptAsync("(function(){var v=Array.prototype.slice.call(document.querySelectorAll('video')).sort(function(a,b){return b.clientWidth*b.clientHeight-a.clientWidth*a.clientHeight;})[0];if(v)try{v.__velivoUserPaused=true;v.pause();}catch(e){}})()");
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
  function fix(){ var v=big(); if(!v) return; if(v.controls) v.controls=false;   // w okienku tylko pasek Velivo (np. film z dysku ma wlasny pasek odtwarzacza)
    if(!v.classList.contains('velivo-film')){ document.querySelectorAll('video.velivo-film').forEach(function(x){x.classList.remove('velivo-film');}); v.classList.add('velivo-film'); }
    if(!seeked && v.readyState>0){ seeked=true; if(t>3 && Math.abs(v.currentTime-t)>3) try{v.currentTime=t;}catch(e){} }
    if(v.paused && !v.__velivoUserPaused && !v.ended) autoPlay(v); }
  // YouTube ma wlasny odtwarzacz - wznawiamy przez niego, inaczej po bledzie dzwieku zostaje zawieszony
  function yt(){ var mp=document.getElementById('movie_player'); return mp && typeof mp.playVideo==='function' ? mp : null; }
  function playIt(v){ var mp=yt(); try{ if(mp){ mp.playVideo(); return; } }catch(e){} v.play().catch(function(){}); }
  // automatyczne wznawianie z hamulcem: najwyzej raz na 2 s i 6 razy na minute - gdy karta dzwiekowa jest zajeta,
  // nie walczymy z nia w petli (to powodowalo zacinanie); spokojnie probujemy dalej co 10 s
  var tries=[];
  function autoPlay(v){ var now=Date.now(); tries=tries.filter(function(x){return now-x<60000;});
    if(tries.length && now-tries[tries.length-1]<(tries.length>=6?10000:2000)) return; tries.push(now); playIt(v); }
  // pauze uzytkownika ustawia TYLKO klik w okienko (nizej) - zatrzymanie przez silnik czy zmiane dzwieku zawsze wznawiamy
  document.addEventListener('pause', function(e){ var v=e.target; if(!v || v.tagName!=='VIDEO' || v.__velivoUserPaused || v.ended) return; setTimeout(function(){ if(v.paused && !v.__velivoUserPaused && !v.ended) autoPlay(v); }, 150); }, true);
  document.addEventListener('play', function(e){ if(e.target && e.target.tagName==='VIDEO') e.target.__velivoUserPaused=false; }, true);
  fix(); setInterval(fix, 1000);
  // pasek czasu: klik/przeciaganie = skok, kolko nad paskiem = +-5 s (kolko nad filmem = przezroczystosc)
  // pasek okienka: pauza, -10 s / +10 s, pasek czasu, glosnosc (wycisz + suwak); kolko nad paskiem czasu = +-5 s, nad glosnoscia = +-5%
  var IC={play:'M8 5v14l11-7z',pause:'M6 5h4v14H6zM14 5h4v14h-4z',back:'M11 18V6l-8.5 6zm.5-6 8.5 6V6z',fwd:'M4 18l8.5-6L4 6zm9-12v12l8.5-6z',
    full:'M4 4h6v2H6v4H4zm10 0h6v6h-2V6h-4zM4 14h2v4h4v2H4zm14 0h2v6h-6v-2h4z',
    vol:'M3 9v6h4l5 5V4L7 9H3zm13.5 3A4.5 4.5 0 0 0 14 8v8a4.5 4.5 0 0 0 2.5-4z',mute:'M3 9v6h4l5 5V4L7 9H3zm13 .4 1.4-1.4 2.1 2.1 2.1-2.1 1.4 1.4-2.1 2.1 2.1 2.1-1.4 1.4-2.1-2.1-2.1 2.1-1.4-1.4 2.1-2.1z'};
  function setIc(el,k){ var p=el.querySelector('path'); if(p && p.getAttribute('d')!==IC[k]) p.setAttribute('d',IC[k]); }
  function vb(a,k,title){ var b=document.createElement('span'); b.className='vb'; b.setAttribute('data-a',a); b.title=title;
    var sv=document.createElementNS('http://www.w3.org/2000/svg','svg'); sv.setAttribute('viewBox','0 0 24 24'); var pa=document.createElementNS('http://www.w3.org/2000/svg','path'); pa.setAttribute('d',IC[k]); sv.appendChild(pa); b.appendChild(sv); return b; }
  var bar=document.getElementById('velivo-seek');
  if(!bar){
    bar=document.createElement('div'); bar.id='velivo-seek';
    var tr=document.createElement('div'), fl=document.createElement('div'), tx=document.createElement('div'), vol=document.createElement('input');
    tr.id='velivo-seek-track'; fl.id='velivo-seek-fill'; tx.id='velivo-seek-time';
    vol.id='velivo-vol'; vol.type='range'; vol.min='0'; vol.max='1'; vol.step='0.05'; vol.title='Głośność';
    tr.appendChild(fl);
    [vb('play','pause','Odtwarzaj / pauza (spacja)'), vb('back','back','10 s wstecz (←)'), vb('fwd','fwd','10 s do przodu (→)'), tr, tx, vb('mute','vol','Wycisz (M)'), vol, vb('full','full','Pełny ekran (F, dwuklik; Esc – wyjście)')].forEach(function(x){ bar.appendChild(x); });
    document.documentElement.appendChild(bar);
    st.textContent+='#velivo-seek,#velivo-seek *{visibility:visible!important}#velivo-seek{position:fixed;left:0;right:0;bottom:0;height:34px;z-index:2147483647;display:flex;align-items:center;gap:6px;padding:0 8px;background:linear-gradient(transparent,rgba(0,0,0,.8));opacity:0;transition:opacity .25s;cursor:pointer;font:600 11px Segoe UI,sans-serif;color:#fff}'+
      '#velivo-seek.on{opacity:1}#velivo-seek-track{flex:1;min-width:30px;height:5px;background:rgba(255,255,255,.3);border-radius:3px;position:relative}#velivo-seek-track:hover{height:8px}#velivo-seek-fill{position:absolute;left:0;top:0;bottom:0;background:#60a5fa;border-radius:3px}#velivo-seek-time{white-space:nowrap;font-variant-numeric:tabular-nums}'+
      '#velivo-seek .vb{display:inline-flex;align-items:center;justify-content:center;width:24px;height:24px;border-radius:5px;flex:none}#velivo-seek .vb:hover{background:rgba(255,255,255,.22)}#velivo-seek .vb svg{width:16px;height:16px;fill:#fff;pointer-events:none}'+
      '#velivo-vol{width:70px;flex:none;margin:0;accent-color:#60a5fa;cursor:pointer}@media (max-width:340px){#velivo-vol,#velivo-seek-time{display:none!important}}@media (max-width:230px){#velivo-seek [data-a=back],#velivo-seek [data-a=fwd]{display:none!important}}';
  }
  function fmt(x){ if(!isFinite(x)) return '--:--'; x=Math.max(0,Math.floor(x)); var h=Math.floor(x/3600),m=Math.floor(x%3600/60),sec=x%60; return (h?h+':'+(m<10?'0':''):'')+m+':'+(sec<10?'0':'')+sec; }
  function upd(){ var v=big(); if(!v) return; var d=v.duration; document.getElementById('velivo-seek-fill').style.width=(isFinite(d)&&d>0?Math.min(100,v.currentTime/d*100):0)+'%'; document.getElementById('velivo-seek-time').textContent=fmt(v.currentTime)+' / '+fmt(d);
    setIc(bar.querySelector('[data-a=play]'), v.paused?'play':'pause'); setIc(bar.querySelector('[data-a=mute]'), v.muted||v.volume===0?'mute':'vol');
    var vr=document.getElementById('velivo-vol'); if(document.activeElement!==vr) vr.value=v.muted?0:v.volume; }
  var hideT=null; function show(){ bar.classList.add('on'); upd(); clearTimeout(hideT); hideT=setTimeout(function hide(){ if(dragging||bar.matches(':hover')){ hideT=setTimeout(hide,1000); return; } bar.classList.remove('on'); },2000); }
  function seekTo(clientX){ var v=big(), r=document.getElementById('velivo-seek-track').getBoundingClientRect(); if(!v||!isFinite(v.duration)||r.width<=0) return; v.currentTime=Math.max(0,Math.min(1,(clientX-r.left)/r.width))*v.duration; upd(); }
  function skip(s){ var v=big(); if(v&&isFinite(v.duration)){ v.currentTime=Math.max(0,Math.min(v.duration,v.currentTime+s)); show(); } }
  function setVol(x){ var v=big(); if(!v) return; v.volume=Math.max(0,Math.min(1,Math.round(x*100)/100)); if(v.volume>0) v.muted=false; show(); }
  function full(on){ if(window.chrome && chrome.webview) chrome.webview.postMessage(on===false ? 'velivo-float-full:0' : 'velivo-float-full'); }
  function toggle(){ var v=big(); if(!v) return; if(v.paused){ v.__velivoUserPaused=false; playIt(v); } else { v.__velivoUserPaused=true; var mp=yt(); try{ if(mp&&mp.pauseVideo){ mp.pauseVideo(); } else v.pause(); }catch(x){ v.pause(); } } setTimeout(upd,50); }
  var dragging=false;
  if(!window.__velivoSeek){ window.__velivoSeek=1;
    setInterval(function(){ if(bar.classList.contains('on')) upd(); },500);
    addEventListener('mousemove', show, true);
    document.getElementById('velivo-seek-track').addEventListener('mousedown', function(e){ if(e.button!==0) return; dragging=true; seekTo(e.clientX); e.preventDefault(); e.stopPropagation(); }, true);
    addEventListener('mousemove', function(e){ if(dragging) seekTo(e.clientX); }, true);
    addEventListener('mouseup', function(){ if(dragging){ dragging=false; show(); } }, true);
    document.getElementById('velivo-vol').addEventListener('input', function(e){ setVol(+e.target.value); });
    bar.addEventListener('click', function(e){ var b=e.target.closest&&e.target.closest('[data-a]'); if(e.target.id==='velivo-vol') return; e.preventDefault(); e.stopPropagation(); if(!b) return;
      var a=b.getAttribute('data-a'), v=big(); if(!v) return;
      if(a==='play') toggle(); else if(a==='back') skip(-10); else if(a==='fwd') skip(10); else if(a==='mute'){ v.muted=!v.muted; if(!v.muted&&v.volume===0) v.volume=0.5; show(); } else if(a==='full') full(); }, true);
    addEventListener('dblclick', function(e){ if(bar.contains(e.target)) return; e.preventDefault(); e.stopPropagation(); full(); }, true);
    // klawiatura (po kliknieciu w okienko): spacja pauza, strzalki ← → 10 s, ↑ ↓ glosnosc, M wycisz
    addEventListener('keydown', function(e){ if(e.ctrlKey||e.altKey||e.metaKey) return; var v=big(); if(!v) return; var k=e.key, h=true;
      if(k===' '||k==='k') toggle(); else if(k==='ArrowRight') skip(10); else if(k==='ArrowLeft') skip(-10);
      else if(k==='ArrowUp') setVol(v.volume+0.05); else if(k==='ArrowDown') setVol(v.volume-0.05); else if(k==='m'||k==='M'){ v.muted=!v.muted; show(); } else if(k==='f'||k==='F') full(); else if(k==='Escape') full(false); else h=false;
      if(h){ e.preventDefault(); e.stopPropagation(); } }, true);
  }
  if (!window.__velivoWheel) { window.__velivoWheel = 1; addEventListener('wheel', function(e){ e.preventDefault(); e.stopPropagation(); var v=big();
    if (bar && v && (e.target.id==='velivo-vol' || (e.target.closest && e.target.closest('[data-a=mute]')))) { setVol(v.volume+(e.deltaY<0?0.05:-0.05)); return; }
    if (bar && bar.contains(e.target)) { skip(e.deltaY<0?5:-5); return; }
    if (window.chrome && chrome.webview) chrome.webview.postMessage('velivo-float-wheel:' + (e.deltaY < 0 ? 1 : -1)); }, { passive: false, capture: true }); }
  document.addEventListener('click', function(e){ if(bar && bar.contains(e.target)) return; if(!big()) return; e.preventDefault(); e.stopPropagation(); toggle(); }, true);
})";

        void ShowFloatingVideo(string page, double time, string title, bool isPrivate)
        {
            if (string.IsNullOrEmpty(page) || !(page.StartsWith("http://") || page.StartsWith("https://") || IsPlayerUrl(PlayerFile, page))) return;   // strony www albo odtwarzacz filmow z dysku
            // kontrolka rysowana w oknie WPF (nie osobne okno systemowe) - dzieki temu dziala przezroczystosc okienka
            var view = new WebView2CompositionControl { DefaultBackgroundColor = System.Drawing.Color.Black };

            // gorny pasek: tytul (przeciaganie okienka), zamkniecie; reszta to film
            var titleText = new TextBlock { Text = string.IsNullOrWhiteSpace(title) ? "Velivo" : title, Foreground = Brushes.White, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var close = new Button { Content = "\uE8BB", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Padding = new Thickness(0), MinHeight = 0, Width = 28, Height = 22, Foreground = Brushes.White, Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontSize = 12, Cursor = Cursors.Hand, ToolTip = L.T("Zamknij film") };
            var back = new Button { Content = "\uE7A7", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Padding = new Thickness(0), MinHeight = 0, Width = 28, Height = 22, Foreground = Brushes.White, Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontSize = 13, Cursor = Cursors.Hand, ToolTip = L.T("Wróć do karty (otwórz stronę w Velivo)") };
            // wlasny, ciemny wyglad przyciskow paska (wspolny styl okien Velivo jest jasny i za szeroki na ten pasek)
            var barStyle = (Style)System.Windows.Markup.XamlReader.Parse(@"<Style TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
    <Border x:Name='Bd' Background='Transparent'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>
    <ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='#33FFFFFF'/></Trigger></ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter></Style>");
            close.Style = barStyle; back.Style = barStyle;
            WindowChrome.SetIsHitTestVisibleInChrome(close, true);
            WindowChrome.SetIsHitTestVisibleInChrome(back, true);
            // przypinka: zawsze na wierzchu albo zwykle okno (pod innymi, gdy klikniesz gdzie indziej)
            var pinBtn = new Button { Padding = new Thickness(0), MinHeight = 0, Width = 28, Height = 22, Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontSize = 12, Cursor = Cursors.Hand,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") };
            pinBtn.Style = barStyle;
            WindowChrome.SetIsHitTestVisibleInChrome(pinBtn, true);
            var bar = new DockPanel { Height = 24, Background = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)) };
            DockPanel.SetDock(close, Dock.Right); DockPanel.SetDock(back, Dock.Right); DockPanel.SetDock(pinBtn, Dock.Right);
            bar.Children.Add(close); bar.Children.Add(back); bar.Children.Add(pinBtn); bar.Children.Add(titleText);
            var root = new DockPanel { Background = Brushes.Black };
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar); root.Children.Add(view);

            var b = _settings.FloatBounds;
            var win = new Window
            {
                Title = (string.IsNullOrWhiteSpace(title) ? "" : title + " – ") + L.T("Film na wierzchu"),
                Topmost = _settings.FloatTopmost, ShowInTaskbar = true, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.CanResize, AllowsTransparency = true,
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
            Action showPin = () =>
            {
                pinBtn.Content = win.Topmost ? "\uE840" : "\uE718";   // przypiete / nieprzypiete
                pinBtn.Foreground = win.Topmost ? new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)) : Brushes.White;
                pinBtn.ToolTip = win.Topmost ? L.T("Zawsze na wierzchu: WŁĄCZONE – kliknij, aby okienko mogło schować się pod inne") : L.T("Zawsze na wierzchu: wyłączone – kliknij, aby przypiąć nad wszystkim");
            };
            showPin();
            pinBtn.Click += (s, e) => { win.Topmost = !win.Topmost; _settings.FloatTopmost = win.Topmost; showPin(); };
            // kolko myszy na gornym pasku = przezroczystosc (15-100%)
            int alpha = Math.Max(15, Math.Min(100, _settings.FloatOpacity));
            win.Opacity = alpha / 100.0;
            bar.ToolTip = L.T("Przeciągnij, aby przesunąć · kółko myszy: przezroczystość");
            System.Windows.Threading.DispatcherTimer label = null;
            Action<int> step = null;
            win.PreviewMouseWheel += (s, e) => { e.Handled = true; step(e.Delta > 0 ? 1 : -1); };   // kolko w calym okienku
            step = dir =>
            {
                alpha = Math.Max(15, Math.Min(100, alpha + (dir > 0 ? 10 : -10)));
                win.Opacity = alpha / 100.0;
                _settings.FloatOpacity = alpha;
                var keep = titleText.Text;
                if (label == null)
                {
                    label = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                    label.Tick += (a, b2) => { label.Stop(); titleText.Text = (string)titleText.Tag ?? titleText.Text; };
                }
                if (!label.IsEnabled) titleText.Tag = keep;
                titleText.Text = (L.En ? "Visibility " : "Widoczność ") + alpha + "%";
                label.Stop(); label.Start();
            };
            back.Click += async (s, e) =>
            {
                double now = 0;
                try
                {
                    if (view.CoreWebView2 != null)
                        double.TryParse(await view.CoreWebView2.ExecuteScriptAsync("(function(){var v=document.querySelector('video.velivo-film');return v?v.currentTime:0;})()"), NumberStyles.Float, CultureInfo.InvariantCulture, out now);
                }
                catch (Exception) { }
                var url = view.CoreWebView2 != null && !string.IsNullOrEmpty(view.CoreWebView2.Source) ? view.CoreWebView2.Source : page;
                // YouTube: miejsce filmu w adresie (dziala tez, gdy Velivo trzeba uruchomic od nowa)
                if (now > 5 && (url.Contains("youtube.com/watch") || url.Contains("youtu.be/")))
                {
                    url = System.Text.RegularExpressions.Regex.Replace(url, @"([?&])t=[^&]*&?", "$1").TrimEnd('&', '?');
                    url += (url.Contains("?") ? "&" : "?") + "t=" + (int)now + "s";
                }
                // film z dysku: miejsce w adresie odtwarzacza (&t=)
                if (now > 1 && IsPlayerUrl(PlayerFile, url))
                    url = System.Text.RegularExpressions.Regex.Replace(url, @"&t=\d+", "").Replace("&v=", "&t=" + (int)now + "&v=");
                // glowne okno zamkniete - najpierw uruchamiamy Velivo od nowa z ta strona, dopiero potem zamykamy okienko
                if (_mainClosed) { OpenFromOutside(new[] { url }); win.Close(); return; }
                win.Close();
                AddTab(url, isPrivate);
                if (now > 5) _tabs[_tabs.Count - 1].PendingVideoTime = now;
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Maximized;
                Show();
                Activate();
                Topmost = true; Topmost = false;   // wyciagnij okno Velivo na wierzch
            };
            win.Closed += (s, e) =>
            {
                var rb = win.WindowState == WindowState.Normal ? new Rect(win.Left, win.Top, win.Width, win.Height) : win.RestoreBounds;   // po pelnym ekranie zapamietujemy zwykly rozmiar
                _settings.FloatBounds = string.Join(";", new[] { rb.Left, rb.Top, rb.Width, rb.Height }.Select(x => x.ToString("0", CultureInfo.InvariantCulture)));
                try { _settings.Save(DataDir); } catch (Exception) { }
                try { view.Dispose(); } catch (Exception) { }
            };
            win.Show();
            _ = InitFloatingView(view, page, time, isPrivate, titleText, d => step(d),
                toggle => { if (toggle) win.WindowState = win.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; else if (win.WindowState == WindowState.Maximized) win.WindowState = WindowState.Normal; });
        }

        const string FloatAdSkipScript = @"(function(){ if (!/(^|\.)youtube\.com$/.test(location.hostname)) return;
  setInterval(function(){ try {
    var p = document.querySelector('.html5-video-player');
    var b = document.querySelector('.ytp-ad-skip-button, .ytp-ad-skip-button-modern, .ytp-skip-ad-button');
    if (b) b.click();
    if (p && p.classList.contains('ad-showing')) { var v = p.querySelector('video'); if (v) { v.muted = true; if (isFinite(v.duration) && v.duration > 0) v.currentTime = v.duration; } }
    else { var v2 = p && p.querySelector('video'); if (v2 && v2.__velivoAdMuted) { v2.muted = false; v2.__velivoAdMuted = false; } }
    if (p && p.classList.contains('ad-showing')) { var v3 = p.querySelector('video'); if (v3) v3.__velivoAdMuted = true; }
    document.querySelectorAll('ytd-ad-slot-renderer, .ytp-ad-overlay-container, #player-ads').forEach(function(x){ x.style.display = 'none'; });
  } catch (e) {} }, 300);
})();";

        async Task InitFloatingView(WebView2CompositionControl view, string page, double time, bool isPrivate, TextBlock titleText, Action<int> wheel, Action<bool> fullScreen)
        {
            try
            {
                var opts = _env.CreateCoreWebView2ControllerOptions();
                opts.IsInPrivateModeEnabled = isPrivate;
                await view.EnsureCoreWebView2Async(_env, opts);
                var core = view.CoreWebView2;
                _floatCores.Add(core);
                view.Unloaded += (s0, e0) => _floatCores.Remove(core);
                ApplyViewSettings(core);
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.NewWindowRequested += (s, e) => e.Handled = true;   // reklamy i linki z okienka nie otwieraja okien
                // blokada reklam i trackerow jak w karcie
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
                core.WebResourceRequested += (s, e) =>
                {
                    try
                    {
                        if (IsTrustedUrl(e.Request.Uri) || IsTrustedUrl(page)) return;
                        if (_blocker.ShouldBlock(e.Request.Uri)) e.Response = _env.CreateWebResourceResponse(null, 403, "Blocked", "");
                    }
                    catch (Exception) { }
                };
                // reklamy wideo YouTube: pominiecie (przycisk "Pomin") albo przewiniecie do konca, bez dzwieku
                await core.AddScriptToExecuteOnDocumentCreatedAsync(FloatAdSkipScript);
                core.DocumentTitleChanged += (s, e) => titleText.Text = core.DocumentTitle;
                core.Settings.IsWebMessageEnabled = true;
                // kolko myszy nad filmem tez zmienia przezroczystosc
                core.WebMessageReceived += (s, e) =>
                {
                    string m = null; try { m = e.TryGetWebMessageAsString(); } catch (Exception) { }
                    if (m == "velivo-float-wheel:1") wheel(1); else if (m == "velivo-float-wheel:-1") wheel(-1);
                    // pelny ekran okienka (przycisk na pasku, F, dwuklik) - Esc wraca; okienko zostaje na wierzchu
                    else if (m == "velivo-float-full") fullScreen(true);
                    else if (m == "velivo-float-full:0") fullScreen(false);
                };
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
