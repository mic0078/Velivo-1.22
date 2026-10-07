using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace Przegladarka
{
    // Odtwarzacz filmow z dysku (offline): plik wideo otwiera sie w karcie na prostej stronie Velivo z filmem
    // na cale okno. Na filmie dzialaja zwykle przyciski Velivo (⧉ obraz w obrazie, ▣ Film na wierzchu), a skroty:
    // spacja / klik - pauza, strzalki ← → - 5 s, ↑ ↓ - glosnosc, F - pelny ekran, M - wycisz.
    // Strona-odtwarzacz to plik w danych Velivo; film podaje sie w adresie po "#" i musi byc plikiem lokalnym.
    public partial class MainWindow
    {
        internal static readonly string[] VideoExts = { ".mp4", ".m4v", ".webm", ".mkv", ".mov", ".ogv" };

        internal static bool IsVideoFile(string path)
        {
            var ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            return VideoExts.Contains(ext);
        }

        // argument z Windows (po ArgToUrl: file:///…/film.mp4) -> sciezka filmu; null = to nie film z dysku
        internal static string VideoArg(string a)
        {
            Uri u;
            return Uri.TryCreate(a, UriKind.Absolute, out u) && u.IsFile && IsVideoFile(u.LocalPath) ? u.LocalPath : null;
        }

        static string PlayerFile { get { return Path.Combine(DataDir, "odtwarzacz.html"); } }

        // strona odtwarzacza z filmem: file:///…/odtwarzacz.html#a=1&r=1&l=0&v=file%3A%2F%2F%2F…film.mp4 (ustawienia + film na koncu)
        internal static string PlayerUrlFor(string playerFile, string videoPath, bool autoplay, bool resume, bool loop)
        {
            return new Uri(playerFile).AbsoluteUri + "#a=" + (autoplay ? 1 : 0) + "&r=" + (resume ? 1 : 0) + "&l=" + (loop ? 1 : 0)
                + "&v=" + Uri.EscapeDataString(new Uri(Path.GetFullPath(videoPath)).AbsoluteUri.Replace("#", "%23"));   // "#" w nazwie pliku to nie kotwica
        }

        internal static bool IsPlayerUrl(string playerFile, string url)
        {
            var page = new Uri(playerFile).AbsoluteUri;
            return url != null && (url == page || url.StartsWith(page + "#", StringComparison.OrdinalIgnoreCase));
        }

        string PlayerUrl(string videoPath)
        {
            try
            {
                var html = PlayerHtml.Replace("{ERR}", L.T("Tego filmu nie da się odtworzyć – format albo kodek nie jest obsługiwany (najpewniej działają MP4 z H.264 i WebM)."));
                if (!File.Exists(PlayerFile) || File.ReadAllText(PlayerFile) != html) File.WriteAllText(PlayerFile, html);
            }
            catch (Exception ex) { App.LogError(ex); }
            return PlayerUrlFor(PlayerFile, videoPath, _settings.PlayerAutoplay, _settings.PlayerResume, _settings.PlayerLoop);
        }

        void OpenVideoFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = L.T("Otwórz film z dysku"),
                Filter = L.T("Filmy") + "|" + string.Join(";", VideoExts.Select(x => "*" + x)) + "|" + L.T("Wszystkie pliki") + "|*.*",
            };
            if (dlg.ShowDialog(this) == true) AddTab(PlayerUrl(dlg.FileName));
        }

        const string PlayerHtml = @"<!doctype html>
<html lang=pl><head><meta charset=utf-8><title>Velivo</title>
<style>
html,body{margin:0;height:100%;background:#000;color:#ddd;font:14px Segoe UI,sans-serif;overflow:hidden}
video{width:100vw;height:100vh;object-fit:contain;background:#000;display:block}
#err{display:none;position:fixed;left:0;right:0;top:40%;text-align:center;padding:0 24px}
</style></head>
<body><video id=v controls></video><div id=err>{ERR}</div>
<script>
(function(){
  var v=document.getElementById('v'), o={}, src='';
  location.hash.slice(1).split('&').forEach(function(p){ var i=p.indexOf('='); if(i>0) o[p.slice(0,i)]=p.slice(i+1); });
  try{ src=decodeURIComponent(o.v||''); }catch(e){}
  if(src.indexOf('file:///')!==0){ document.getElementById('err').style.display='block'; return; }   // tylko pliki z dysku
  document.title=decodeURIComponent(src.split('/').pop());
  v.loop=o.l==='1'; v.autoplay=o.a==='1'; v.src=src;
  // wznawianie: miejsce w filmie tylko w pamieci tej strony na tym komputerze; koniec filmu = od poczatku
  var key='poz:'+src, last=0;
  if(o.r==='1') v.addEventListener('loadedmetadata',function(){ var t=+localStorage.getItem(key)||0; if(t>5&&t<v.duration-5) v.currentTime=t; },{once:true});
  function save(){ if(o.r!=='1') return; try{ if(v.ended||v.currentTime<5) localStorage.removeItem(key); else localStorage.setItem(key,String(Math.floor(v.currentTime))); }catch(e){} }
  v.addEventListener('timeupdate',function(){ if(Math.abs(v.currentTime-last)>=5){ last=v.currentTime; save(); } });
  v.addEventListener('pause',save); v.addEventListener('ended',save); addEventListener('pagehide',save);
  v.addEventListener('error',function(){ document.getElementById('err').style.display='block'; });
  v.addEventListener('click',function(e){ if(e.target===v){ e.preventDefault(); v.paused?v.play():v.pause(); } });
  addEventListener('keydown',function(e){
    if(e.ctrlKey||e.altKey||e.metaKey) return;
    var k=e.key, h=true;
    if(k===' '||k==='k') v.paused?v.play():v.pause();
    else if(k==='ArrowRight') v.currentTime=Math.min(v.duration||0,v.currentTime+5);
    else if(k==='ArrowLeft') v.currentTime=Math.max(0,v.currentTime-5);
    else if(k==='ArrowUp') v.volume=Math.min(1,v.volume+0.1);
    else if(k==='ArrowDown') v.volume=Math.max(0,v.volume-0.1);
    else if(k==='m') v.muted=!v.muted;
    else if(k==='f') { if(document.fullscreenElement) document.exitFullscreen(); else v.requestFullscreen(); }
    else h=false;
    if(h) e.preventDefault();
  });
})();
</script></body></html>";
    }
}
