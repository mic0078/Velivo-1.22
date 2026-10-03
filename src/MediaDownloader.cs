using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Przegladarka
{
    // Wykrywanie URL-i audio/wideo na bieżącej stronie i dodawanie do menedżera pobrań.
    public partial class MainWindow
    {
        readonly List<MediaItem> _sniffedMedia = new List<MediaItem>();
        readonly HashSet<string> _sniffedMediaSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, DateTime> _mediaToastByHost = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        const string MediaScanScript = @"(() => {
  const out = [];
  const add = (u, kind) => { if (!u || typeof u !== 'string') return; try { out.push({ url: new URL(u, location.href).href, kind }); } catch (e) {} };
  for (const v of document.querySelectorAll('video')) {
    add(v.currentSrc || v.src, 'video');
    for (const s of v.querySelectorAll('source')) add(s.src, 'video');
  }
  for (const a of document.querySelectorAll('audio')) {
    add(a.currentSrc || a.src, 'audio');
    for (const s of a.querySelectorAll('source')) add(s.src, 'audio');
  }
  for (const l of document.querySelectorAll('a[href], source[src]')) {
    const u = l.href || l.src || '';
        if (/^(blob:|data:|javascript:)/i.test(u)) continue;
        if (/\/s\/search\/audio\//i.test(u)) continue;
    if (/\.(mp4|webm|mkv|mov|avi|m4v|mp3|m4a|aac|wav|ogg|flac|opus)(\?|#|$)/i.test(u)) add(u, /\.(mp3|m4a|aac|wav|ogg|flac|opus)(\?|#|$)/i.test(u) ? 'audio' : 'video');
    if (/\.m3u8(\?|#|$)/i.test(u)) add(u, 'stream');
  }
  const map = new Map();
  for (const x of out) if (!map.has(x.url)) map.set(x.url, x);
  return JSON.stringify([...map.values()].slice(0, 200));
})();";

        static string MediaHost(string url)
        {
            try
            {
                Uri u;
                if (Uri.TryCreate(url, UriKind.Absolute, out u)) return (u.Host ?? "").ToLowerInvariant();
            }
            catch (Exception) { }
            return "";
        }

        static bool LooksLikeMediaUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) return false;
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
            if (!(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) return false;
            var u = url.ToLowerInvariant();
            if (u.Contains("/s/search/audio/")) return false;
            if (u.Contains(".m3u8") || u.Contains(".mpd") || u.Contains("/videoplayback") || u.Contains("mime=video") || u.Contains("mime=audio")) return true;
            return u.Contains(".mp4") || u.Contains(".webm") || u.Contains(".mkv") || u.Contains(".mov") || u.Contains(".avi") || u.Contains(".m4v") ||
                   u.Contains(".mp3") || u.Contains(".m4a") || u.Contains(".aac") || u.Contains(".wav") || u.Contains(".ogg") || u.Contains(".flac") || u.Contains(".opus");
        }

        static bool IsUsableMediaCandidate(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) return false;
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
            if (!(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) return false;
            var low = url.ToLowerInvariant();
            if (low.Contains("/s/search/audio/")) return false;
            return true;
        }

        static string MediaKindFromUrl(string url)
        {
            var u = (url ?? "").ToLowerInvariant();
            if (u.Contains("mime=audio") || u.Contains(".mp3") || u.Contains(".m4a") || u.Contains(".aac") || u.Contains(".wav") || u.Contains(".ogg") || u.Contains(".flac") || u.Contains(".opus")) return "audio";
            if (u.Contains(".m3u8") || u.Contains(".mpd")) return "stream";
            return "video";
        }

        static bool IsYoutubeLikeHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            return host == "youtube.com" || host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)
                || host == "youtu.be" || host.EndsWith(".youtu.be", StringComparison.OrdinalIgnoreCase)
                || host == "googlevideo.com" || host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsInterestingMediaContext(CoreWebView2WebResourceContext ctx)
        {
            return ctx == CoreWebView2WebResourceContext.Media
                || ctx == CoreWebView2WebResourceContext.Fetch
                || ctx == CoreWebView2WebResourceContext.XmlHttpRequest
                || ctx == CoreWebView2WebResourceContext.Other;
        }

        void NoteMediaRequest(string url, BrowserTab tab, CoreWebView2WebResourceContext ctx)
        {
            try
            {
                if (!IsInterestingMediaContext(ctx)) return;
                if (tab == null || tab.Private) return;
                if (tab != _current) return;
                if (!LooksLikeMediaUrl(url)) return;

                var host = MediaHost(url);
                if (host.Length == 0) return;

                bool added = false;
                lock (_sniffedMedia)
                {
                    if (_sniffedMediaSet.Add(url))
                    {
                        _sniffedMedia.Add(new MediaItem { url = url, kind = MediaKindFromUrl(url) });
                        if (_sniffedMedia.Count > 300)
                        {
                            var remove = _sniffedMedia.Count - 300;
                            for (int i = 0; i < remove; i++) _sniffedMediaSet.Remove(_sniffedMedia[i].url);
                            _sniffedMedia.RemoveRange(0, remove);
                        }
                        added = true;
                    }
                }
                if (!added) return;
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        void ShowMediaDetectedToast(string host)
        {
            var panel = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
            panel.Children.Add(new TextBlock
            {
                Text = "Wykryto media do pobrania na stronie: " + host,
                Foreground = Brushes.White,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 340
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            Window toast = null;
            var open = SmallButton("Pokaż", () =>
            {
                try { DetectPageMedia(); } catch (Exception) { }
                if (toast != null) toast.Close();
            });
            var close = SmallButton("Zamknij", () => { if (toast != null) toast.Close(); });
            buttons.Children.Add(open);
            buttons.Children.Add(close);
            panel.Children.Add(buttons);

            toast = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Owner = this,
                Topmost = true,
                SizeToContent = SizeToContent.WidthAndHeight,
                Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)),
                ShowActivated = false,
                Content = panel
            };

            toast.Loaded += (s, e) =>
            {
                var r = PointToScreen(new Point(ActualWidth, ActualHeight));
                var src = PresentationSource.FromVisual(this);
                if (src != null && src.CompositionTarget != null) r = src.CompositionTarget.TransformFromDevice.Transform(r);
                toast.Left = r.X - toast.ActualWidth - 24;
                toast.Top = r.Y - toast.ActualHeight - 24;
            };

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            timer.Tick += (s, e) => { timer.Stop(); try { toast.Close(); } catch (InvalidOperationException) { } };
            toast.Closed += (s, e) => timer.Stop();
            toast.Show();
            timer.Start();
        }

        async void DetectPageMedia()
        {
            if (Core == null) return;
            try
            {
                var sourceHost = MediaHost(Core.Source);
                if (IsYoutubeLikeHost(sourceHost))
                {
                    MessageBox.Show(this,
                        "Na YouTube Velivo nie udostępnia pobierania wideo/audio.\n\n" +
                        "Na innych stronach (bez DRM) wykrywanie i pobieranie działa normalnie.",
                        "Wykryte media na stronie");
                    return;
                }

                var raw = await Core.ExecuteScriptAsync(MediaScanScript);
                var json = JsonSerializer.Deserialize<string>(raw);
                var list = string.IsNullOrWhiteSpace(json)
                    ? new List<MediaItem>()
                    : JsonSerializer.Deserialize<List<MediaItem>>(json) ?? new List<MediaItem>();

                var pageHost = MediaHost(Core.Source);
                List<MediaItem> sniffed;
                lock (_sniffedMedia)
                {
                    sniffed = _sniffedMedia
                        .Where(m => string.Equals(MediaHost(m.url), pageHost, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                foreach (var m in sniffed)
                    if (!list.Any(x => string.Equals(x.url, m.url, StringComparison.OrdinalIgnoreCase)))
                        list.Add(m);

                list = list.Where(m => m != null && IsUsableMediaCandidate(m.url)).ToList();

                if (list.Count == 0)
                {
                    ShowToast("🎬 Nie wykryto źródeł audio/wideo na tej stronie.", null);
                    return;
                }

                var win = new Window
                {
                    Title = "Wykryte media na stronie",
                    Width = 820,
                    Height = 520,
                    Owner = this,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                var listBox = new ListBox { Margin = new Thickness(8) };
                foreach (var m in list)
                    listBox.Items.Add(new ListBoxItem { Tag = m, Content = "[" + m.kind + "] " + m.url });

                var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
                bar.Children.Add(SmallButton("Pobierz", () =>
                {
                    var it = listBox.SelectedItem as ListBoxItem;
                    if (it == null) return;
                    var media = (MediaItem)it.Tag;
                    _ = QueueMediaDownload(media.url, media.kind == "audio");
                }));
                bar.Children.Add(SmallButton("Pobierz jako audio", () =>
                {
                    var it = listBox.SelectedItem as ListBoxItem;
                    if (it == null) return;
                    var media = (MediaItem)it.Tag;
                    _ = QueueMediaDownload(media.url, true);
                }));

                win.Content = Docked(bar, listBox);
                win.Show();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                MessageBox.Show(this, "Nie udało się wykryć mediów:\n" + ex.Message, "Pobrane");
            }
        }

        sealed class MediaItem
        {
            public string url { get; set; }
            public string kind { get; set; }
        }

        async System.Threading.Tasks.Task QueueMediaDownload(string url, bool audio)
        {
            try
            {
                var core = Core;
                if (core == null || string.IsNullOrWhiteSpace(url)) return;
                if (url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(this,
                        "To źródło jest typu blob (tymczasowe dane w pamięci strony), więc nie da się go pobrać bezpośrednio jako pliku.",
                        "Pobrane");
                    return;
                }
                if (!(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show(this,
                        "To źródło nie jest bezpośrednim adresem HTTP/HTTPS do pliku.",
                        "Pobrane");
                    return;
                }
                var host = MediaHost(url);
                if (IsYoutubeLikeHost(host))
                {
                    MessageBox.Show(this,
                        "Dla YouTube pobieranie nie jest wspierane w Velivo z uwagi na zasady platformy i prawa autorskie.\n\n" +
                        "Na innych stronach (bez DRM) wykrywanie i pobieranie działa normalnie.",
                        "Pobrane");
                    return;
                }
                string dir = core.Profile.DefaultDownloadFolderPath;
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

                string file = GuessMediaFileName(url, audio);
                string target = UniqueFile(Path.Combine(dir, file));
                var job = await ProbeDownload(url, target, audio ? "audio/*" : "video/*", core);
                if (job != null)
                {
                    StartJob(job);
                    ShowToast("🎬 Dodano do pobierania: " + Path.GetFileName(job.File), job.File);
                    return;
                }

                MessageBox.Show(this, "Nie udało się przejąć tego pobierania.\nStrona może wymagać tokenu sesji lub odtwarzacz używa szyfrowanego streamu.", "Pobrane");
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                MessageBox.Show(this, ex.Message, "Pobrane");
            }
        }

        static string GuessMediaFileName(string url, bool audio)
        {
            try
            {
                var u = new Uri(url);
                var name = Path.GetFileName(u.AbsolutePath);
                if (string.IsNullOrWhiteSpace(name)) name = audio ? "audio.bin" : "video.bin";
                if (audio && !name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".aac", StringComparison.OrdinalIgnoreCase))
                    name = Path.GetFileNameWithoutExtension(name) + ".audio" + Path.GetExtension(name);
                return name;
            }
            catch (Exception)
            {
                return audio ? "audio.bin" : "video.bin";
            }
        }
    }
}
