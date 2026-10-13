using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;

namespace Przegladarka
{
    // Telewizja IPTV (osobny modul wybierany w instalatorze, wymaga odtwarzacza filmow): listy kanalow M3U z pliku albo adresu URL,
    // program TV (EPG, XMLTV - takze .gz). Przycisk 📺 obok 🎬: klik = lista kanalow, prawy przycisk = dodaj liste, odinstaluj modul.
    // Kanaly graja w odtwarzaczu filmow (poprzedni / nastepny = zmiana kanalu). Velivo nie zawiera zadnych list - wczytuje je uzytkownik.
    public partial class MainWindow
    {
        static string IptvMarker { get { return Path.Combine(AppContext.BaseDirectory, "iptv.on"); } }
        static string IptvDataDir { get { return Path.Combine(DataDir, "iptv"); } }
        static string IptvStateFile { get { return Path.Combine(IptvDataDir, "listy.json"); } }

        internal static bool IptvInstalled { get { return VlcInstalled && File.Exists(IptvMarker); } }

        internal sealed class IptvChannel
        {
            public string Name, Url, Group = "", Logo, TvgId;
            public List<string> Opts = new List<string>();   // opcje VLC z listy (#EXTVLCOPT): user-agent, referrer
        }

        internal sealed class IptvPlaylist
        {
            public List<IptvChannel> Channels = new List<IptvChannel>();
            public string EpgUrl;   // adres programu TV z naglowka listy (url-tvg / x-tvg-url)
        }

        internal sealed class EpgItem { public DateTime Start, Stop; public string Title, Desc, Sub, Year; public List<string> Cats = new List<string>(); }

        sealed class IptvSource
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Url { get; set; }   // adres listy albo sciezka pliku
            public string Epg { get; set; }   // adres programu TV (opcjonalnie)
            public long Updated { get; set; }
            public int Hours { get; set; } = 12;   // odswiezanie listy i programu co tyle godzin; 0 = tylko recznie
        }

        sealed class IptvState
        {
            public List<IptvSource> Sources { get; set; } = new List<IptvSource>();
            public List<string> Fav { get; set; } = new List<string>();      // adresy ulubionych kanalow
            public List<string> Recent { get; set; } = new List<string>();   // ostatnio ogladane (najnowsze pierwsze)
            public string Last { get; set; }
            public List<IptvJob> Jobs { get; set; } = new List<IptvJob>();   // zaplanowane nagrania i przelaczenia
            public string RecDir { get; set; }   // folder nagran (puste = Wideo\Velivo TV)
            public string Win { get; set; }
            public string Group { get; set; }    // ostatnio wybrana grupa
            public Dictionary<string, long> Watched { get; set; } = new Dictionary<string, long>();   // kanal -> kiedy ostatnio ogladany (unix ms)
            public int Buffer { get; set; } = 1;
            public bool WatchFromRec { get; set; }   // ogladanie nagrywanego kanalu z pliku nagrania (jedno polaczenie, pauza i cofanie) - opcja
            public bool PauseRelease { get; set; }   // po 5 min pauzy zwolnij polaczenie z dostawca - opcja  // zapas bufora kanalow na zywo (s); 1 = jak VLC
            public bool InfoOnMove { get; set; } = true;   // pasek informacji pokazuje sie przy ruchu myszy (inaczej tylko klawisz I)
            public int InfoSec { get; set; } = 5;        // pasek informacji o programie: znika po tylu sekundach; 0 = tylko na zadanie (klawisz I)
            public int InfoAlpha { get; set; } = 85;     // nieprzezroczystosc paska informacji (%)
            public int InfoScale { get; set; } = 100;
            public int ListAlpha { get; set; } = 85;     // nieprzezroczystosc listy kanalow nad filmem (%)    // wielkosc tekstu paska informacji (%), dodatkowo dopasowana do rozmiaru okna
            public Dictionary<string, string> EpgMap { get; set; } = new Dictionary<string, string>();   // reczne przypisanie programu TV: adres kanalu -> id w EPG
            public string Sort { get; set; }     // sortowanie: "kolumna" albo "kolumna,desc"      // miejsce i rozmiar okna listy: "x,y,szer,wys"
        }

        // ---------- lista M3U ----------

        static readonly Regex M3uAttr = new Regex("([A-Za-z0-9_-]+)=\"([^\"]*)\"", RegexOptions.Compiled);

        internal static IptvPlaylist ParseM3u(string text)
        {
            var pl = new IptvPlaylist();
            IptvChannel pending = null;
            string group = null;
            var opts = new List<string>();
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.Trim().TrimStart('﻿');
                if (line.Length == 0) continue;
                if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (Match m in M3uAttr.Matches(line))
                        if (m.Groups[1].Value.Equals("url-tvg", StringComparison.OrdinalIgnoreCase) || m.Groups[1].Value.Equals("x-tvg-url", StringComparison.OrdinalIgnoreCase))
                            pl.EpgUrl = m.Groups[2].Value.Split(',')[0].Trim();
                }
                else if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
                {
                    pending = new IptvChannel();
                    // nazwa = tekst po pierwszym przecinku poza cudzyslowem
                    bool q = false; int comma = -1;
                    for (int i = 0; i < line.Length; i++) { if (line[i] == '"') q = !q; else if (line[i] == ',' && !q) { comma = i; break; } }
                    pending.Name = comma >= 0 ? line.Substring(comma + 1).Trim() : "";
                    foreach (Match m in M3uAttr.Matches(comma >= 0 ? line.Substring(0, comma) : line))
                    {
                        var k = m.Groups[1].Value.ToLowerInvariant(); var v = m.Groups[2].Value.Trim();
                        if (k == "tvg-id") pending.TvgId = v;
                        else if (k == "tvg-logo") pending.Logo = v;
                        else if (k == "group-title") pending.Group = v;
                        else if (k == "tvg-name" && pending.Name.Length == 0) pending.Name = v;
                    }
                }
                else if (line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase)) group = line.Substring(8).Trim();
                else if (line.StartsWith("#EXTVLCOPT:", StringComparison.OrdinalIgnoreCase))
                {
                    var o = line.Substring(11).Trim();
                    if (o.StartsWith("http-user-agent=") || o.StartsWith("http-referrer=")) opts.Add(":" + o);   // tylko naglowki - inne opcje VLC z obcej listy pomijamy
                }
                else if (!line.StartsWith("#"))
                {
                    if (!line.Contains("://")) { pending = null; opts.Clear(); group = null; continue; }
                    var ch = pending ?? new IptvChannel();
                    ch.Url = line;
                    if (string.IsNullOrEmpty(ch.Name)) ch.Name = line;
                    if (string.IsNullOrEmpty(ch.Group) && group != null) ch.Group = group;
                    ch.Opts.AddRange(opts);
                    pl.Channels.Add(ch);
                    pending = null; opts.Clear(); group = null;
                }
            }
            return pl;
        }

        // ---------- program TV (XMLTV) ----------

        // czas XMLTV: "20261012083000 +0200" (strefa opcjonalna - wtedy UTC)
        internal static DateTime? ParseXmltvTime(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 12) return null;
            var digits = s.Length >= 14 ? s.Substring(0, 14) : s.Substring(0, 12) + "00";
            if (!DateTime.TryParseExact(digits, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return null;
            var off = TimeSpan.Zero;
            var rest = s.Substring(Math.Min(s.Length, 14)).Trim();
            if (rest.Length == 5 && (rest[0] == '+' || rest[0] == '-') && int.TryParse(rest.Substring(1, 2), out var h) && int.TryParse(rest.Substring(3, 2), out var m))
                off = new TimeSpan(h, m, 0) * (rest[0] == '-' ? -1 : 1);
            return new DateTimeOffset(t, off).LocalDateTime;
        }

        // program w oknie czasu [from, to]; klucz: id kanalu i jego nazwy (male litery) - lista bez tvg-id dopasowuje sie po nazwie
        // icons (opcjonalnie): logo kanalu z programu TV (<icon src>) - id i nazwy kanalu -> adres
        internal static Dictionary<string, List<EpgItem>> ParseXmltv(Stream input, DateTime from, DateTime to, Dictionary<string, string> icons = null)
        {
            var buf = new BufferedStream(input);
            var head = new byte[2];
            int n = buf.Read(head, 0, 2);
            Stream s = new ConcatStream(head, n, buf);
            if (n == 2 && head[0] == 0x1f && head[1] == 0x8b) s = new GZipStream(s, CompressionMode.Decompress);
            var byId = new Dictionary<string, List<EpgItem>>(StringComparer.OrdinalIgnoreCase);
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // nazwa -> id
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true };
            using (var r = XmlReader.Create(s, settings))
            {
                // po Skip / ReadElementContentAsString czytnik stoi juz na nastepnym elemencie - nie wolno go przeskoczyc kolejnym Read
                bool next = true;
                while (next ? r.Read() : !r.EOF)
                {
                    next = true;
                    if (r.NodeType != XmlNodeType.Element) continue;
                    if (r.Name == "channel")
                    {
                        var id = r.GetAttribute("id") ?? "";
                        string icon = null; var dns = new List<string>();
                        using (var sub = r.ReadSubtree())
                        {
                            sub.Read();
                            while (!sub.EOF)
                            {
                                if (sub.NodeType == XmlNodeType.Element && sub.Depth > 0 && sub.Name == "display-name") { var dn = sub.ReadElementContentAsString().Trim(); if (dn.Length > 0) { dns.Add(dn); if (!names.ContainsKey(dn)) names[dn] = id; } continue; }
                                if (sub.NodeType == XmlNodeType.Element && sub.Name == "icon" && icon == null) icon = sub.GetAttribute("src");
                                sub.Read();
                            }
                        }
                        if (icons != null && !string.IsNullOrEmpty(icon)) { icons[id] = icon; foreach (var dn in dns) if (!icons.ContainsKey(dn)) icons[dn] = icon; }
                    }
                    else if (r.Name == "programme")
                    {
                        var start = ParseXmltvTime(r.GetAttribute("start")); var stop = ParseXmltvTime(r.GetAttribute("stop"));
                        var ch = r.GetAttribute("channel") ?? "";
                        if (start == null || stop == null || stop < from || start > to) { r.Skip(); next = false; continue; }
                        var it = new EpgItem { Start = start.Value, Stop = stop.Value };
                        using (var sub = r.ReadSubtree())
                        {
                            sub.Read();
                            while (!sub.EOF)
                            {
                                var el = sub.NodeType == XmlNodeType.Element && sub.Depth == 1 ? sub.Name : null;
                                if (el != "title" && el != "desc" && el != "sub-title" && el != "category" && el != "date") { sub.Read(); continue; }
                                string v;
                                try { v = sub.ReadElementContentAsString().Trim(); } catch (XmlException) { sub.Skip(); continue; }   // element z dziecmi - pomijamy
                                if (el == "title") { if (it.Title == null) it.Title = v; }
                                else if (el == "desc") { if (string.IsNullOrEmpty(it.Desc)) it.Desc = v; }
                                else if (el == "sub-title") { if (it.Sub == null) it.Sub = v; }
                                else if (el == "category") { if (v.Length > 0 && it.Cats.Count < 4 && !it.Cats.Contains(v)) it.Cats.Add(v); }
                                else if (it.Year == null) it.Year = v;
                            }
                        }
                        if (!byId.TryGetValue(ch, out var l)) byId[ch] = l = new List<EpgItem>();
                        l.Add(it);
                    }
                }
            }
            foreach (var l in byId.Values) l.Sort((a, b) => a.Start.CompareTo(b.Start));
            foreach (var kv in names) if (!byId.ContainsKey(kv.Key) && byId.TryGetValue(kv.Value, out var l)) byId[kv.Key] = l;
            // nazwy bez dopiskow jakosci: "TVP 1 SD" na liscie = "TVP 1 HD" w programie
            foreach (var kv in byId.ToList()) { var k = EpgKey(kv.Key); if (k.Length > 0 && !byId.ContainsKey(k)) byId[k] = kv.Value; }
            if (icons != null) foreach (var kv in icons.ToList()) { var k = EpgKey(kv.Key); if (k.Length > 0 && !icons.ContainsKey(k)) icons[k] = kv.Value; }
            return byId;
        }

        // pierwsze bajty przeczytane do rozpoznania .gz - oddane z powrotem przed reszta strumienia
        sealed class ConcatStream : Stream
        {
            readonly byte[] _head; int _hn, _hp; readonly Stream _rest;
            public ConcatStream(byte[] head, int n, Stream rest) { _head = head; _hn = n; _rest = rest; }
            public override int Read(byte[] b, int o, int c)
            {
                if (_hp < _hn) { int k = Math.Min(c, _hn - _hp); Array.Copy(_head, _hp, b, o, k); _hp += k; return k; }
                return _rest.Read(b, o, c);
            }
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long a, SeekOrigin b) { throw new NotSupportedException(); }
            public override void SetLength(long a) { throw new NotSupportedException(); }
            public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
        }

        static readonly Regex QualityTag = new Regex(@"\b(uhd|fhd|hd|sd|4k|hevc|h265|raw)\b|\(.*?\)|\[.*?\]|^\|?[a-z]{2}\|\s*|[^a-z0-9]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // klucz zastepczy: male litery, bez dopiskow jakosci, nawiasow, prefiksu "|PL|" i znakow innych niz litery/cyfry; "@"-poczatek odroznia od id
        internal static string EpgKey(string name)
        {
            var k = QualityTag.Replace((name ?? "").ToLowerInvariant().Replace(".pl", ""), "");
            return k.Length == 0 ? "" : "@" + k;
        }

        internal static T ByChannel<T>(Dictionary<string, T> map, IptvChannel ch) where T : class
        {
            if (map == null) return null;
            if (!string.IsNullOrEmpty(ch.TvgId) && map.TryGetValue(ch.TvgId, out var v)) return v;
            if (map.TryGetValue(ch.Name ?? "", out v)) return v;
            var k = EpgKey(ch.Name);
            return k.Length > 0 && map.TryGetValue(k, out v) ? v : null;
        }

        internal static List<EpgItem> EpgFor(Dictionary<string, List<EpgItem>> epg, IptvChannel ch) { return ByChannel(epg, ch); }

        internal static EpgItem EpgAt(List<EpgItem> l, DateTime t) { return l?.FirstOrDefault(x => x.Start <= t && x.Stop > t); }

        // ---------- dane ----------

        IptvState LoadIptv()
        {
            try { if (File.Exists(IptvStateFile)) return JsonSerializer.Deserialize<IptvState>(File.ReadAllText(IptvStateFile)) ?? new IptvState(); }
            catch (Exception ex) { App.LogError(ex); }
            return new IptvState();
        }

        void SaveIptv(IptvState st)
        {
            try { Directory.CreateDirectory(IptvDataDir); File.WriteAllText(IptvStateFile, JsonSerializer.Serialize(st, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { App.LogError(ex); }
        }

        static string IptvCache(IptvSource src, string ext) { return Path.Combine(IptvDataDir, src.Id + ext); }

        // plik z dysku albo pobranie z sieci do pamieci podrecznej (lista ".m3u", program ".epg")
        // progress: pobrane bajty, calosc (0 = nieznana)
        static async Task DownloadTo(string url, string file, Action<long, long> progress = null)
        {
            if (File.Exists(url)) { File.Copy(url, file, true); return; }
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Velivo");
                using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? 0, done = 0, shown = 0;
                    var tmp = file + ".tmp";
                    using (var fs = File.Create(tmp))
                    using (var src = await resp.Content.ReadAsStreamAsync())
                    {
                        var buf = new byte[81920]; int n;
                        while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                        {
                            await fs.WriteAsync(buf, 0, n); done += n;
                            if (progress != null && done - shown > 512 * 1024) { shown = done; progress(done, total); }
                        }
                    }
                    File.Copy(tmp, file, true); File.Delete(tmp);
                }
            }
        }

        // odczyt pliku z postepem (wczytywanie duzego programu TV)
        sealed class ProgressStream : Stream
        {
            readonly Stream _s; readonly Action<double> _p; long _shown;
            public ProgressStream(Stream s, Action<double> p) { _s = s; _p = p; }
            public override int Read(byte[] b, int o, int c)
            {
                int n = _s.Read(b, o, c);
                if (_s.Position - _shown > 1 << 20 || n == 0) { _shown = _s.Position; _p(_s.Length > 0 ? (double)_s.Position / _s.Length : 0); }
                return n;
            }
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => _s.Length;
            public override long Position { get => _s.Position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long a, SeekOrigin b) { throw new NotSupportedException(); }
            public override void SetLength(long a) { throw new NotSupportedException(); }
            public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
            protected override void Dispose(bool d) { if (d) _s.Dispose(); base.Dispose(d); }
        }

        static string EpgMb(long b) { return (b / 1048576.0).ToString(b < 10 * 1048576 ? "0.0" : "0"); }

        // ---------- przycisk 📺 ----------

        Button _tvBtn;
        Window _tvWin;
        readonly Dictionary<string, IptvChannel> _iptvPlaying = new Dictionary<string, IptvChannel>();   // kanaly na liscie odtwarzania (adres -> kanal)
        Dictionary<string, List<EpgItem>> _iptvEpg;
        Dictionary<string, string> _iptvIcons;   // logo kanalow z programu TV

        // ---------- logo kanalow (zapisywane na dysku) ----------

        static string LogoDir { get { return Path.Combine(IptvDataDir, "logo"); } }
        static string LogoFile(string url) { return Path.Combine(LogoDir, FilmId(url) + ".img"); }
        readonly Dictionary<string, ImageSource> _logoMem = new Dictionary<string, ImageSource>();
        readonly HashSet<string> _logoFailed = new HashSet<string>();
        static readonly System.Threading.SemaphoreSlim LogoGate = new System.Threading.SemaphoreSlim(6);
        static readonly HttpClient LogoHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        // height: wysokosc dekodowania (logo kanalu 40 px; plakat w opisie wiekszy - inaczej rozmazany)
        static ImageSource LoadLogo(string file, int height = 40)
        {
            try
            {
                var b = new BitmapImage();
                using (var fs = File.OpenRead(file)) { b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.DecodePixelHeight = height; b.StreamSource = fs; b.EndInit(); }
                b.Freeze();
                return b;
            }
            catch (Exception) { return null; }
        }

        // logo z dysku, a gdy go nie ma - pobranie (tylko http/https, najwyzej 1 MB, musi byc obrazkiem)
        async Task<ImageSource> GetLogo(string url)
        {
            if (string.IsNullOrEmpty(url) || !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) return null;
            if (_logoMem.TryGetValue(url, out var img)) return img;
            if (_logoFailed.Contains(url)) return null;
            var file = LogoFile(url);
            if (!File.Exists(file))
            {
                await LogoGate.WaitAsync();
                try
                {
                    if (!File.Exists(file))
                    {
                        var data = await Task.Run(async () =>
                        {
                            try
                            {
                                using (var resp = await LogoHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                                {
                                    if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentLength > 1 << 20) return null;
                                    var ms = new MemoryStream();
                                    var st = await resp.Content.ReadAsStreamAsync();
                                    var buf = new byte[16384]; int n;
                                    while ((n = await st.ReadAsync(buf, 0, buf.Length)) > 0) { ms.Write(buf, 0, n); if (ms.Length > 1 << 20) return null; }
                                    return ms.ToArray();
                                }
                            }
                            catch (Exception) { return null; }
                        });
                        if (data == null) { _logoFailed.Add(url); return null; }
                        Directory.CreateDirectory(LogoDir);
                        File.WriteAllBytes(file, data);
                    }
                }
                finally { LogoGate.Release(); }
            }
            img = await Task.Run(() => LoadLogo(file));
            if (img == null) { _logoFailed.Add(url); try { File.Delete(file); } catch (Exception) { } return null; }
            _logoMem[url] = img;
            return img;
        }

        void InitIptvFeature()
        {
            if (!IptvInstalled) return;
            _tvBtn = new Button
            {
                Content = "📺", FontSize = 16, Width = 40, Height = 36, Margin = new Thickness(2, 3, 2, 0),
                ToolTip = L.T("Telewizja IPTV – kanały z listy M3U i program TV\nPrawy klik: dodaj listę, odinstaluj moduł")
            };
            DockPanel.SetDock(_tvBtn, Dock.Right);
            _tvBtn.Click += (s, e) => ShowIptv(this);
            var menu = new ContextMenu();
            Action<string, string, Action> item = (glyph, text, act) => { var mi = new MenuItem { Header = text, Icon = MenuGlyph(glyph) }; mi.Click += (s, e) => act(); menu.Items.Add(mi); };
            item("", L.T("Kanały telewizji"), () => ShowIptv(this));
            item("", L.T("Dodaj listę kanałów…"), () => { ShowIptv(this); _iptvAdd?.Invoke(); });
            menu.Items.Add(new Separator());
            item("", L.T("Odinstaluj moduł IPTV…"), UninstallIptv);
            item("\uE787", L.T("📅 Program TV"), () => ShowEpgGuide(this, null));
            item("\uE787", L.T("Zaplanowane nagrania i przełączenia"), () => ShowJobs(this));
            item("\uE8B2", L.T("🎞 Nagrania"), () => ShowRecordings(this));
            item("\uE838", L.T("📁 Folder nagrań"), OpenRecDir);
            item("\uE838", L.T("📁 Zmień folder nagrań…"), () => ChooseRecDir(this));
            _tvBtn.ContextMenu = menu;
            StartIptvScheduler();   // zaplanowane nagrania / przelaczenia dzialaja takze przy zamknietym oknie listy
            var resume = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };   // po uruchomieniu: ostatni program, ktory lecial przy zamykaniu
            resume.Tick += (s, e) => { resume.Stop(); ResumeTv(); };
            resume.Start();
            int film = _filmBtn != null ? TabBarPanel.Children.IndexOf(_filmBtn) : -1;
            TabBarPanel.Children.Insert(film >= 0 ? film + 1 : 1, _tvBtn);   // obok 🎬
        }

        void UninstallIptv()
        {
            if (MessageBox.Show(this, L.T("Odinstalować moduł IPTV?\n\nTwoje listy kanałów zostają – wrócą po ponownej instalacji modułu. Odtwarzacz filmów zostaje."),
                L.T("Telewizja IPTV"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (_tvWin != null) _tvWin.Close();
            try { File.Delete(IptvMarker); } catch (Exception ex) { App.LogError(ex); }
            if (_tvBtn != null) { TabBarPanel.Children.Remove(_tvBtn); _tvBtn = null; }
            ShowToast(L.T("📺 Moduł IPTV odinstalowany"), null);
        }

        Action _iptvAdd;

        // reczne przypisania programu TV (kanal o innej nazwie w EPG) - przed dopasowaniem po tvg-id
        void ApplyEpgMap(IptvPlaylist pl)
        {
            var map = IptvSt.EpgMap; if (map == null || map.Count == 0) return;
            foreach (var c in pl.Channels) if (map.TryGetValue(c.Url, out var id)) c.TvgId = id;
        }

        // wybor kanalu z programu TV dla tego kanalu listy (szukanie po nazwie)
        bool AssignEpg(IptvChannel ch, Window owner)
        {
            var epg = _iptvEpg; if (epg == null) return false;
            var d = new Window { Title = L.T("📅 Przypisz program TV – ") + ch.Name, Width = 460, Height = 520, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var q = new TextBox { Margin = new Thickness(8), Padding = new Thickness(4), Text = Regex.Replace(ch.Name ?? "", @"\b(hd|fhd|sd|uhd|4k)\b|\[.*?\]|\|.*?\|", "", RegexOptions.IgnoreCase).Trim() };
            var lb = new ListBox { Margin = new Thickness(8, 0, 8, 0) };
            var keys = epg.Keys.Where(k => !k.StartsWith("@")).OrderBy(k => k, StringComparer.CurrentCultureIgnoreCase).ToList();
            var now = DateTime.Now;
            Action fill = () =>
            {
                lb.Items.Clear(); var t = q.Text.Trim();
                foreach (var k in keys.Where(k => t.Length == 0 || k.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0).Take(300))
                {
                    var cur = EpgAt(epg[k], now);
                    lb.Items.Add(new ListBoxItem { Content = k + (cur != null ? "   ·   " + L.T("teraz: ") + cur.Title : ""), Tag = k });
                }
            };
            q.TextChanged += (s, a) => fill(); fill();
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
            bool ok = false;
            Action pick = () => { if (lb.SelectedItem is ListBoxItem it) { IptvSt.EpgMap[ch.Url] = (string)it.Tag; ch.TvgId = (string)it.Tag; ok = true; d.Close(); } };
            bar.Children.Add(SmallButton(L.T("✔ Przypisz"), pick));
            if (IptvSt.EpgMap.ContainsKey(ch.Url)) bar.Children.Add(SmallButton(L.T("Usuń przypisanie"), () => { IptvSt.EpgMap.Remove(ch.Url); ch.TvgId = null; ok = true; d.Close(); }));
            lb.MouseDoubleClick += (s, a) => pick();
            var body = new DockPanel();
            DockPanel.SetDock(q, Dock.Top); body.Children.Add(q);
            DockPanel.SetDock(bar, Dock.Bottom); body.Children.Add(bar);
            body.Children.Add(lb);
            d.Content = body;
            d.ShowDialog();
            if (ok) SaveIptv(IptvSt);
            return ok;
        }

        // okno listy: ostatnie miejsce i rozmiar; nie zaslania odtwarzacza (np. w trybie mini) - stawia sie obok niego
        void PlaceIptvWindow(Window win, string saved)
        {
            var wa = SystemParameters.WorkArea;
            var p = (saved ?? "").Split(',');
            if (p.Length == 4 && p.All(x => int.TryParse(x, out _)))
            {
                var r = new Rect(int.Parse(p[0]), int.Parse(p[1]), Math.Max(500, int.Parse(p[2])), Math.Max(300, int.Parse(p[3])));
                if (r.IntersectsWith(new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight)))
                { win.WindowStartupLocation = WindowStartupLocation.Manual; win.Left = r.Left; win.Top = r.Top; win.Width = r.Width; win.Height = r.Height; }
            }
            var v = _vlcWin;
            if (v == null || !v.IsVisible || v.WindowState != WindowState.Normal) return;
            var vr = new Rect(v.Left, v.Top, v.ActualWidth, v.ActualHeight);
            if (win.WindowStartupLocation != WindowStartupLocation.Manual) { win.WindowStartupLocation = WindowStartupLocation.Manual; win.Left = wa.Left + (wa.Width - win.Width) / 2; win.Top = wa.Top + (wa.Height - win.Height) / 2; }
            var me = new Rect(win.Left, win.Top, win.Width, win.Height);
            if (!me.IntersectsWith(vr)) return;
            // obok odtwarzacza: z lewej, z prawej, nad, pod - gdzie sie zmiesci (w razie potrzeby mniejsze)
            if (vr.Left - wa.Left >= 500) { win.Width = Math.Min(win.Width, vr.Left - wa.Left - 8); win.Left = vr.Left - win.Width - 8; }
            else if (wa.Right - vr.Right >= 500) { win.Width = Math.Min(win.Width, wa.Right - vr.Right - 8); win.Left = vr.Right + 8; }
            else if (vr.Top - wa.Top >= 300) { win.Height = Math.Min(win.Height, vr.Top - wa.Top - 8); win.Top = vr.Top - win.Height - 8; }
            else if (wa.Bottom - vr.Bottom >= 300) { win.Height = Math.Min(win.Height, wa.Bottom - vr.Bottom - 8); win.Top = vr.Bottom + 8; }
            win.Top = Math.Max(wa.Top, Math.Min(win.Top, wa.Bottom - win.Height));
            win.Left = Math.Max(wa.Left, Math.Min(win.Left, wa.Right - win.Width));
        }

        // okno listy: zrodlo (lista), grupy, kanaly z "teraz / nastepnie", program wybranego kanalu
        void ShowIptv(Window owner)
        {
            if (_tvWin != null && _tvWin.IsLoaded)   // juz otwarte: na wierzch (takze zminimalizowane / ukryte za odtwarzaczem)
            {
                if (_tvWin.WindowState == WindowState.Minimized) _tvWin.WindowState = WindowState.Normal;
                if (!_tvWin.IsVisible) _tvWin.Show();
                _tvWin.Topmost = true; _tvWin.Topmost = owner.Topmost; _tvWin.Activate();
                return;
            }
            var st = IptvSt;
            var win = new Window { Title = L.T("📺 Telewizja"), Width = 980, Height = 600, Owner = owner, Topmost = owner.Topmost, WindowStartupLocation = WindowStartupLocation.CenterOwner, Tag = "own-bounds" };   // miejsce zapamietuje sam (i omija odtwarzacz)
            PlaceIptvWindow(win, st.Win);
            var source = new ComboBox { Width = 200, Margin = new Thickness(8, 8, 0, 4), VerticalContentAlignment = VerticalAlignment.Center };
            Independent(win);   // zwykle okno Windows - nie przykrywa innych na stale
            var addBtn = SmallButton(L.T("➕ Dodaj listę"), null);
            var editBtn = SmallButton(L.T("✏ Edytuj"), null);
            var delBtn = SmallButton(L.T("🗑 Usuń listę"), null);
            var refBtn = SmallButton(L.T("🔄 Odśwież"), null);
            foreach (var b in new[] { addBtn, editBtn, delBtn, refBtn }) b.Margin = new Thickness(6, 8, 0, 4);
            var search = new TextBox { Margin = new Thickness(8, 8, 8, 4), Padding = new Thickness(4) };
            var hint = new TextBlock { Text = L.T("🔍 Szukaj kanału…"), Foreground = Brushes.Gray, Margin = new Thickness(14, 13, 0, 0), IsHitTestVisible = false };
            search.TextChanged += (s, a) => hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var searchBox = new Grid(); searchBox.Children.Add(search); searchBox.Children.Add(hint);
            var top = new DockPanel();
            foreach (var c in new FrameworkElement[] { source, addBtn, editBtn, delBtn, refBtn }) { DockPanel.SetDock(c, Dock.Left); top.Children.Add(c); }
            top.Children.Add(searchBox);

            var groups = new ListBox { Width = 200, Margin = new Thickness(8, 0, 0, 0) };
            var grid = new GridView();
            // kolumny: naglowek klik = sortuj, drugi klik = odwrotnie
            var cols = new[] { new[] { "nr", "Nr", "50" }, new[] { "name", "Kanał", "270" }, new[] { "now", "Teraz", "300" }, new[] { "next", "Następnie", "260" }, new[] { "watched", "Oglądany", "110" } };
            var sortSaved = (st.Sort ?? "nr").Split(',');
            string sortKey = cols.Any(c => c[0] == sortSaved[0]) ? sortSaved[0] : "nr"; bool sortDesc = sortSaved.Length > 1 && sortSaved[1] == "desc";
            var headers = new Dictionary<string, GridViewColumnHeader>();
            foreach (var c in cols)
            {
                var h = new GridViewColumnHeader { Content = L.T(c[1]), Tag = c[0], Cursor = Cursors.Hand };
                headers[c[0]] = h;
                var col = new GridViewColumn { Header = h, Width = double.Parse(c[2], CultureInfo.InvariantCulture) };
                if (c[0] == "name")   // logo + nazwa
                {
                    var sp = new FrameworkElementFactory(typeof(StackPanel)); sp.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
                    var im = new FrameworkElementFactory(typeof(Image));
                    im.SetValue(FrameworkElement.WidthProperty, 36.0); im.SetValue(FrameworkElement.HeightProperty, 20.0); im.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
                    im.SetValue(Image.StretchProperty, Stretch.Uniform); im.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding("Logo"));
                    var tb = new FrameworkElementFactory(typeof(TextBlock)); tb.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center); tb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name"));
                    sp.AppendChild(im); sp.AppendChild(tb);
                    col.CellTemplate = new DataTemplate { VisualTree = sp };
                }
                else col.DisplayMemberBinding = new System.Windows.Data.Binding(c[0] == "nr" ? "Nr" : c[0] == "now" ? "Now" : c[0] == "watched" ? "Watched" : "Next");
                grid.Columns.Add(col);
            }
            Action markSort = () => { foreach (var c in cols) headers[c[0]].Content = L.T(c[1]) + (c[0] == sortKey ? (sortDesc ? "  ▼" : "  ▲") : ""); };
            markSort();
            var list = new ListView { View = grid, Margin = new Thickness(8, 0, 8, 0), SelectionMode = SelectionMode.Single };
            RememberColumns(win, "telewizja", grid);
            // program kanalu: klik prawym = przelacz o czasie / nagraj ten program
            var epgList = new ListBox { Height = 150, Margin = new Thickness(8, 4, 8, 0) };
            // opis wybranego programu obok listy: tytul, odcinek, gatunek, rok, opis
            var epgDesc = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 6, 10, 6) };
            var descBox = new ScrollViewer { Content = epgDesc, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 150, Margin = new Thickness(0, 4, 8, 0) };
            epgList.Margin = new Thickness(8, 4, 8, 0);
            var epgBox = new Grid();
            epgBox.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            epgBox.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            Grid.SetColumn(descBox, 1); epgBox.Children.Add(epgList); epgBox.Children.Add(descBox);
            var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray, Margin = new Thickness(10, 0, 0, 0) };
            var progress = new ProgressBar { Width = 160, Height = 10, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Maximum = 100, Visibility = Visibility.Collapsed };

            IptvPlaylist pl = new IptvPlaylist();
            List<TvRow> rows = new List<TvRow>();
            Action autoPlay = null;   // klik w 📺: ostatnio ogladany kanal startuje sam (gdy nic nie gra)
            bool autoStart = _vlcWin == null;
            string favKey = "\u0001fav", recentKey = "\u0001recent", allKey = "\u0001all";
            Func<IptvSource> cur = () => st.Sources.FirstOrDefault(x => x.Id == ((source.SelectedItem as ComboBoxItem)?.Tag as string));

            Action fillList = () =>
            {
                var g = (groups.SelectedItem as ListBoxItem)?.Tag as string ?? allKey;
                var q = search.Text.Trim();
                IEnumerable<IptvChannel> chans = pl.Channels;
                if (g == favKey) chans = st.Fav.Select(u => pl.Channels.FirstOrDefault(c => c.Url == u)).Where(c => c != null);
                else if (g == recentKey) chans = st.Recent.Select(u => pl.Channels.FirstOrDefault(c => c.Url == u)).Where(c => c != null);
                else if (g != allKey) chans = chans.Where(c => c.Group == g);
                if (q.Length > 0) chans = chans.Where(c => c.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                var now = DateTime.Now; var epg = _iptvEpg; var icons = _iptvIcons;
                var nrOf = new Dictionary<IptvChannel, int>(); for (int i = 0; i < pl.Channels.Count; i++) nrOf[pl.Channels[i]] = i + 1;
                var all = chans.Select(c =>
                {
                    var l = EpgFor(epg, c); var n0 = EpgAt(l, now); var nx = l?.FirstOrDefault(x => x.Start >= (n0?.Stop ?? now));
                    return new TvRow
                    {
                        Ch = c, Nr = nrOf.TryGetValue(c, out var nr) ? nr : 0, Name = (IsRecording(c.Url) ? "⏺ " : "") + (st.Fav.Contains(c.Url) ? "⭐ " : "") + c.Name,
                        Now = n0 != null ? n0.Start.ToString("HH:mm") + "  " + n0.Title : "", Next = nx != null ? nx.Start.ToString("HH:mm") + "  " + nx.Title : "",
                        NowStart = n0?.Start ?? DateTime.MaxValue, NextStart = nx?.Start ?? DateTime.MaxValue,
                        WatchedMs = st.Watched.TryGetValue(c.Url, out var wm) ? wm : 0, Watched = st.Watched.TryGetValue(c.Url, out var wm2) ? LastPlayedText(wm2, now) : "",
                        LogoUrl = !string.IsNullOrEmpty(c.Logo) ? c.Logo : ByChannel(icons, c),
                    };
                });
                // ulubione i ostatnie - w swojej kolejnosci, dopoki nie wybrano sortowania
                bool keep = g == recentKey || g == favKey && sortKey == "nr" && !sortDesc;   // ostatnio ogladane - zawsze od najnowszego
                if (!keep)
                {
                    Func<TvRow, IComparable> by = sortKey == "watched" ? (Func<TvRow, IComparable>)(r => -r.WatchedMs) : sortKey == "name" ? (Func<TvRow, IComparable>)(r => r.Ch.Name.ToLowerInvariant()) : sortKey == "now" ? r => r.Now.Length > 7 ? r.Now.Substring(7).ToLowerInvariant() : "\uffff" : sortKey == "next" ? r => r.Next.Length > 7 ? r.Next.Substring(7).ToLowerInvariant() : "\uffff" : r => r.Nr;
                    all = sortDesc ? all.OrderByDescending(by) : all.OrderBy(by);
                }
                var keepSel = (list.SelectedItem as TvRow)?.Ch.Url;
                rows = all.ToList();
                list.ItemsSource = rows;
                if (keepSel != null) list.SelectedItem = rows.FirstOrDefault(x => x.Ch.Url == keepSel);
                // logo w tle (z dysku albo pobrane i zapisane)
                foreach (var r0 in rows.Where(x => x.LogoUrl != null).Take(600)) { var r = r0; GetLogo(r.LogoUrl).ContinueWith(t => { if (t.Result != null) r.Logo = t.Result; }, TaskScheduler.FromCurrentSynchronizationContext()); }
                if (autoStart && autoPlay != null && st.Recent.Count > 0 && pl.Channels.Count > 0) { autoStart = false; autoPlay(); }
                count.Text = rows.Count + " / " + pl.Channels.Count + L.T(" kanałów") + (epg != null ? "  ·  " + L.T("program TV wczytany") : "");
            };
            Action fillGroups = () =>
            {
                var sel = (groups.SelectedItem as ListBoxItem)?.Tag as string ?? st.Group;   // przy otwarciu - ostatnia grupa
                groups.Items.Clear();
                groups.Items.Add(new ListBoxItem { Content = L.T("📺 Wszystkie"), Tag = allKey });
                groups.Items.Add(new ListBoxItem { Content = L.T("⭐ Ulubione"), Tag = favKey });
                groups.Items.Add(new ListBoxItem { Content = L.T("🕘 Ostatnio oglądane"), Tag = recentKey });
                foreach (var g in pl.Channels.Select(c => c.Group).Distinct().OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase))
                    groups.Items.Add(new ListBoxItem { Content = g.Length == 0 ? L.T("(bez grupy)") : g, Tag = g });
                groups.SelectedItem = groups.Items.Cast<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == sel) ?? groups.Items[0];
            };
            int loadVer = 0;
            // lista z pamieci podrecznej od razu, odswiezenie z sieci w tle (stara albo na zyczenie)
            Func<bool, Task> load = async force =>
            {
                int ver = ++loadVer;
                var src = cur();
                if (src == null) { pl = new IptvPlaylist(); _iptvEpg = null; _iptvIcons = null; fillGroups(); fillList(); count.Text = L.T("Dodaj listę kanałów (plik .m3u albo adres URL) – przycisk „➕ Dodaj listę”."); return; }
                var m3u = IptvCache(src, ".m3u"); var epgFile = IptvCache(src, ".epg");
                bool stale = src.Hours > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - src.Updated > src.Hours * 3600 * 1000L;
                try
                {
                    if (File.Exists(m3u)) { var t = await Task.Run(() => ParseM3u(File.ReadAllText(m3u))); if (ver != loadVer) return; ApplyEpgMap(t); pl = t; _iptvPl = t; fillGroups(); fillList(); _iptvLoaded?.Invoke(); }
                    if (force || stale || !File.Exists(m3u))
                    {
                        count.Text = L.T("Wczytuję listę kanałów…");
                        await Task.Run(() => DownloadTo(src.Url, m3u));
                        var t = await Task.Run(() => ParseM3u(File.ReadAllText(m3u)));
                        if (ver != loadVer) return;
                        ApplyEpgMap(t); pl = t; _iptvPl = t; fillGroups(); fillList(); _iptvLoaded?.Invoke();
                    }
                    var epgUrl = !string.IsNullOrWhiteSpace(src.Epg) ? src.Epg : pl.EpgUrl;
                    if (string.IsNullOrWhiteSpace(epgUrl)) { _iptvEpg = null; _iptvIcons = null; fillList(); _iptvLoaded?.Invoke(); }
                    else
                    {
                        if (force || stale || !File.Exists(epgFile))
                        {
                            count.Text = L.T("Pobieram program TV…"); progress.Visibility = Visibility.Visible; progress.IsIndeterminate = true;
                            await Task.Run(() => DownloadTo(epgUrl, epgFile, (d, t) => Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (ver != loadVer) return;
                                progress.IsIndeterminate = t <= 0; if (t > 0) progress.Value = 100.0 * d / t;
                                count.Text = L.T("Pobieram program TV… ") + EpgMb(d) + (t > 0 ? " / " + EpgMb(t) + " MB  (" + (int)(100.0 * d / t) + "%)" : " MB");
                            }))));
                        }
                        count.Text = L.T("Wczytuję program TV…"); progress.Visibility = Visibility.Visible; progress.IsIndeterminate = false; progress.Value = 0;
                        var ic = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        var e = await Task.Run(() =>
                        {
                            using (var fs = new ProgressStream(File.OpenRead(epgFile), f => Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (ver != loadVer) return;
                                progress.Value = 100 * f; count.Text = L.T("Wczytuję program TV… ") + (int)(100 * f) + "%";
                            }))))
                                return ParseXmltv(fs, DateTime.Now.AddHours(-6), DateTime.Now.AddHours(30), ic);
                        });
                        progress.Visibility = Visibility.Collapsed;
                        if (ver != loadVer) return;
                        _iptvEpg = e; _iptvIcons = ic; fillList(); _iptvLoaded?.Invoke();
                    }
                    if (force || stale) { src.Updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); SaveIptv(st); }
                }
                catch (Exception ex)
                {
                    App.LogError(ex);
                    if (ver == loadVer) { count.Text = L.T("⚠ Nie udało się wczytać: ") + ex.Message; progress.Visibility = Visibility.Collapsed; }
                }
            };
            Action fillSources = () =>
            {
                source.Items.Clear();
                foreach (var s0 in st.Sources) source.Items.Add(new ComboBoxItem { Content = "📋 " + s0.Name, Tag = s0.Id });
                source.SelectedItem = source.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == st.Last) ?? (source.Items.Count > 0 ? source.Items[0] : null);
            };
            source.SelectionChanged += (s, a) => { _iptvSrc = cur(); var c = cur(); if (c != null && st.Last != c.Id) { st.Last = c.Id; SaveIptv(st); } _ = load(false); };
            groups.SelectionChanged += (s, a) =>
            {
                var g = (groups.SelectedItem as ListBoxItem)?.Tag as string;
                if (g != null && g != st.Group) { st.Group = g; SaveIptv(st); }
                fillList();
            };
            list.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler((s, a) =>
            {
                if (!(a.OriginalSource is GridViewColumnHeader h) || !(h.Tag is string k)) return;
                if (k == sortKey) sortDesc = !sortDesc; else { sortKey = k; sortDesc = false; }
                st.Sort = sortKey + (sortDesc ? ",desc" : ""); SaveIptv(st);
                markSort(); fillList();
            }));
            search.TextChanged += (s, a) => fillList();

            // dodaj / edytuj liste: nazwa, adres albo plik, program TV
            Func<IptvSource, bool> editSource = src =>
            {
                var d = new Window { Title = L.T("Lista kanałów"), Width = 520, SizeToContent = SizeToContent.Height, Owner = win, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
                var p = new StackPanel { Margin = new Thickness(12) };
                Func<string, string, TextBox> field = (label, value) => { p.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) }); var t = new TextBox { Text = value ?? "", Padding = new Thickness(4) }; p.Children.Add(t); return t; };
                var name = field(L.T("Nazwa listy:"), src.Name);
                var url = field(L.T("Adres listy M3U (http://…) albo plik na dysku:"), src.Url);
                var browse = SmallButton(L.T("📁 Wybierz plik…"), () =>
                {
                    var f = new Microsoft.Win32.OpenFileDialog { Filter = L.T("Listy kanałów") + "|*.m3u;*.m3u8|" + L.T("Wszystkie pliki") + "|*.*" };
                    if (f.ShowDialog(d) == true) { url.Text = f.FileName; if (name.Text.Length == 0) name.Text = Path.GetFileNameWithoutExtension(f.FileName); }
                });
                browse.HorizontalAlignment = HorizontalAlignment.Left; browse.Margin = new Thickness(0, 4, 0, 0);
                p.Children.Add(browse);
                var epg = field(L.T("Adres programu TV – EPG XMLTV (opcjonalnie; puste = z listy):"), src.Epg);
                p.Children.Add(new TextBlock { Text = L.T("Odświeżanie listy i programu TV:"), Margin = new Thickness(0, 6, 0, 2) });
                var hoursOpts = new[] { 3, 6, 12, 24, 48, 168, 0 };
                var hours = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, Width = 220 };
                foreach (var t in new[] { "co 3 godziny", "co 6 godzin", "co 12 godzin", "codziennie", "co 2 dni", "co tydzień", "tylko ręcznie (🔄 Odśwież)" }) hours.Items.Add(L.T(t));
                hours.SelectedIndex = Math.Max(0, Array.IndexOf(hoursOpts, src.Hours));
                p.Children.Add(hours);
                var ok = new Button { Content = "OK", Width = 90, Height = 28, IsDefault = true, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
                ok.Click += (s, a) => d.DialogResult = true;
                p.Children.Add(ok);
                d.Content = p;
                d.Loaded += (s, a) => name.Focus();
                if (d.ShowDialog() != true || url.Text.Trim().Length == 0) return false;
                src.Url = url.Text.Trim(); src.Epg = epg.Text.Trim(); src.Hours = hoursOpts[Math.Max(0, hours.SelectedIndex)];
                src.Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : L.T("Lista ") + (st.Sources.Count + 1);
                src.Updated = 0;
                return true;
            };
            _iptvAdd = () =>
            {
                var src = new IptvSource { Id = Guid.NewGuid().ToString("N").Substring(0, 12) };
                if (!editSource(src)) return;
                st.Sources.Add(src); st.Last = src.Id; SaveIptv(st); fillSources();
            };
            addBtn.Click += (s, a) => _iptvAdd();
            editBtn.Click += (s, a) => { var src = cur(); if (src != null && editSource(src)) { SaveIptv(st); fillSources(); _ = load(true); } };
            delBtn.Click += (s, a) =>
            {
                var src = cur(); if (src == null) return;
                if (MessageBox.Show(win, string.Format(L.T("Usunąć listę „{0}” z Velivo?"), src.Name), L.T("Telewizja"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                st.Sources.Remove(src);
                foreach (var ext in new[] { ".m3u", ".epg" }) try { File.Delete(IptvCache(src, ext)); } catch (Exception) { }
                SaveIptv(st); fillSources(); if (st.Sources.Count == 0) _ = load(false);
            };
            refBtn.Click += (s, a) => _ = load(true);

            // ogladanie: lista odtwarzania = widoczne kanaly (poprzedni / nastepny w odtwarzaczu = zmiana kanalu)
            Action play = () =>
            {
                var r = list.SelectedItem as TvRow; if (r == null) return;
                _iptvPlaying.Clear();
                foreach (var x in rows) _iptvPlaying[x.Ch.Url] = x.Ch;
                st.Watched[r.Ch.Url] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                st.Recent.Remove(r.Ch.Url); st.Recent.Insert(0, r.Ch.Url); if (st.Recent.Count > 30) st.Recent.RemoveRange(30, st.Recent.Count - 30);
                SaveIptv(st);
                PlayVideos(rows.Select(x => x.Ch.Url).ToArray(), rows.IndexOf(r));
            };
            autoPlay = () =>
            {
                var r = rows.FirstOrDefault(x => x.Ch.Url == st.Recent[0]);
                if (r != null) { list.SelectedItem = r; list.ScrollIntoView(r); play(); return; }
                var ch = pl.Channels.FirstOrDefault(c => c.Url == st.Recent[0]); if (ch == null) return;   // kanal poza widoczna grupa - sam
                _iptvPlaying[ch.Url] = ch; PlayVideos(new[] { ch.Url }, 0);
            };
            Action toggleFav = () =>
            {
                var r = list.SelectedItem as TvRow; if (r == null) return;
                if (!st.Fav.Remove(r.Ch.Url)) st.Fav.Add(r.Ch.Url);
                SaveIptv(st); fillList();
            };
            list.MouseDoubleClick += (s, a) => play();
            list.PreviewKeyDown += (s, a) => { if (a.Key == Key.Enter) { play(); a.Handled = true; } };
            Action fillEpg = () =>
            {
                epgList.Items.Clear(); epgDesc.Inlines.Clear();
                var r = list.SelectedItem as TvRow; if (r == null) return;
                var l = EpgFor(_iptvEpg, r.Ch);
                if (l == null) { epgDesc.Text = _iptvEpg == null ? L.T("Brak programu TV – dodaj adres EPG w „✏ Edytuj”.") : L.T("Brak programu dla tego kanału."); return; }
                var now = DateTime.Now;
                foreach (var x in l.Where(x => x.Stop > now).Take(40))
                {
                    var mark = JobFor(r.Ch, x, "rec") != null ? "⏺ " : JobFor(r.Ch, x, "switch") != null ? "⏰ " : "";
                    epgList.Items.Add(new ListBoxItem { Content = (x.Start <= now ? "▶ " : "    ") + mark + x.Start.ToString(x.Start.Date == now.Date ? "HH:mm" : "ddd HH:mm") + "–" + x.Stop.ToString("HH:mm") + "  " + x.Title, Tag = x });
                }
                if (epgList.Items.Count > 0) epgList.SelectedIndex = 0;
            };
            list.SelectionChanged += (s, a) => fillEpg();
            epgList.SelectionChanged += (s, a) =>
            {
                epgDesc.Inlines.Clear();
                var x = (epgList.SelectedItem as ListBoxItem)?.Tag as EpgItem; if (x == null) return;
                epgDesc.Inlines.Add(new System.Windows.Documents.Run(x.Title ?? "") { FontWeight = FontWeights.SemiBold, FontSize = 14 });
                if (!string.IsNullOrEmpty(x.Sub)) epgDesc.Inlines.Add(new System.Windows.Documents.Run("\n" + x.Sub) { FontStyle = FontStyles.Italic });
                var meta = x.Start.ToString("HH:mm") + "–" + x.Stop.ToString("HH:mm") + "  ·  " + (int)(x.Stop - x.Start).TotalMinutes + L.T(" min") +
                    (x.Cats.Count > 0 ? "  ·  " + string.Join(", ", x.Cats) : "") + (!string.IsNullOrEmpty(x.Year) ? "  ·  " + (x.Year.Length > 4 ? x.Year.Substring(0, 4) : x.Year) : "");
                epgDesc.Inlines.Add(new System.Windows.Documents.Run("\n" + meta + "\n\n") { Foreground = Brushes.Gray });
                epgDesc.Inlines.Add(new System.Windows.Documents.Run(string.IsNullOrEmpty(x.Desc) ? L.T("Program TV nie zawiera opisu tej audycji.") : x.Desc) { Foreground = string.IsNullOrEmpty(x.Desc) ? Brushes.Gray : epgDesc.Foreground });   // kolor tekstu okna (jasne / ciemne)
            };
            var epgMenu = new ContextMenu();
            epgMenu.Opened += (s, a) =>
            {
                epgMenu.Items.Clear();
                var r = list.SelectedItem as TvRow; var x = (epgList.SelectedItem as ListBoxItem)?.Tag as EpgItem;
                Action<string, Action, bool> add = (t, act, on) => { var mi = new MenuItem { Header = t, IsEnabled = on }; mi.Click += (q, w2) => { act(); fillEpg(); }; epgMenu.Items.Add(mi); };
                bool live = x != null && x.Start <= DateTime.Now;
                add(L.T("▶ Oglądaj ten kanał"), play, r != null);
                var sw = JobFor(r?.Ch, x, "switch"); var rec = JobFor(r?.Ch, x, "rec");
                if (sw != null) add(L.T("❌ Odwołaj przełączenie o ") + x.Start.ToString("HH:mm"), () => CancelJob(sw), true);
                else add(L.T("⏰ Przełącz na ten program o czasie"), () => SwitchAt(r.Ch, x), r != null && x != null && !live);
                if (rec != null) add(_recording.ContainsKey(rec.Id) ? L.T("⏹ Zatrzymaj nagrywanie") : L.T("❌ Odwołaj nagranie"), () => CancelJob(rec), true);
                else add(live ? L.T("⏺ Nagraj ten program (od teraz do końca)") : L.T("⏺ Nagraj ten program"), () => { if (live) { x = new EpgItem { Title = x.Title, Start = DateTime.Now.AddMinutes(2), Stop = x.Stop }; } RecordProgramme(r.Ch, x); }, r != null && x != null);
                add(L.T("📅 Zaplanowane…"), () => ShowJobs(win), true);
            };
            epgList.ContextMenu = epgMenu;
            epgList.MouseDoubleClick += (s, a) => { var r = list.SelectedItem as TvRow; var x = (epgList.SelectedItem as ListBoxItem)?.Tag as EpgItem; if (r != null && x != null) { if (x.Start <= DateTime.Now) play(); else { SwitchAt(r.Ch, x); fillEpg(); } } };
            var menu = new ContextMenu();
            menu.Opened += (s, a) =>
            {
                menu.Items.Clear();
                var r = list.SelectedItem as TvRow;
                Action<string, Action, bool> add = (t, act, on) => { var mi = new MenuItem { Header = t, IsEnabled = on }; mi.Click += (x, y) => act(); menu.Items.Add(mi); };
                add(L.T("▶ Oglądaj"), play, r != null);
                add(r != null && st.Fav.Contains(r.Ch.Url) ? L.T("⭐ Usuń z ulubionych") : L.T("⭐ Dodaj do ulubionych"), toggleFav, r != null);
                add(L.T("📋 Kopiuj adres kanału"), () => { try { Clipboard.SetText(r.Ch.Url); } catch (Exception) { } }, r != null);
                add(L.T("📅 Przypisz program TV…"), () => { if (AssignEpg(r.Ch, win)) { fillList(); fillEpg(); } }, r != null && _iptvEpg != null);
                menu.Items.Add(new Separator());
                if (r != null && IsRecording(r.Ch.Url)) add(L.T("⏹ Zatrzymaj nagrywanie"), () => StopRecording(r.Ch.Url), true);
                else add(L.T("⏺ Nagrywaj teraz"), () => RecordNow(r.Ch, win), r != null);
                add(L.T("⏺ Zaplanuj nagrywanie…"), () => ScheduleDialog(r.Ch, win), r != null);
                add(L.T("📅 Zaplanowane…"), () => ShowJobs(win), true);
                add(L.T("🎞 Nagrania…"), () => ShowRecordings(win), true);
                add(L.T("📅 Pełny program TV…"), () => ShowEpgGuide(win, r?.Ch.Url), true);
                add(L.T("📁 Otwórz folder nagrań"), OpenRecDir, true);
                menu.Items.Add(new Separator());
                add(L.T("🔄 Odśwież listę i program TV"), () => _ = load(true), cur() != null);
                add(L.T("➕ Dodaj listę kanałów…"), _iptvAdd, true);
            };
            list.ContextMenu = menu;

            var bar = new DockPanel { Margin = new Thickness(6), LastChildFill = false };
            DockPanel.SetDock(count, Dock.Left); bar.Children.Add(count);
            var badge = JobsBadge(win); badge.Margin = new Thickness(10, 0, 0, 0);
            DockPanel.SetDock(badge, Dock.Left); bar.Children.Add(badge);
            DockPanel.SetDock(progress, Dock.Left); bar.Children.Add(progress);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(buttons, Dock.Right); bar.Children.Add(buttons);
            buttons.Children.Add(SmallButton(L.T("▶ Oglądaj"), play));
            buttons.Children.Add(SmallButton(L.T("⭐ Ulubiony"), toggleFav));
            buttons.Children.Add(SmallButton(L.T("⏺ Nagrywaj"), () => { var r = list.SelectedItem as TvRow; if (r == null) return; if (IsRecording(r.Ch.Url)) StopRecording(r.Ch.Url); else RecordNow(r.Ch, win); }));
            buttons.Children.Add(SmallButton(L.T("📅 Zaplanowane"), () => ShowJobs(win)));
            buttons.Children.Add(SmallButton(L.T("📅 Program TV"), () => ShowEpgGuide(win, (list.SelectedItem as TvRow)?.Ch.Url)));
            buttons.Children.Add(SmallButton(L.T("🎞 Nagrania"), () => ShowRecordings(win)));
            var recDirBtn = SmallButton(L.T("📁 Folder nagrań"), OpenRecDir);
            recDirBtn.ToolTip = L.T("Klik: otwórz folder nagrań · prawy przycisk: zmień folder");
            var recDirMenu = new ContextMenu(); var chg = new MenuItem { Header = L.T("📁 Zmień folder nagrań…") }; chg.Click += (s, a) => ChooseRecDir(win); recDirMenu.Items.Add(chg);
            recDirBtn.ContextMenu = recDirMenu;
            buttons.Children.Add(recDirBtn);
            buttons.Children.Add(SmallButton(L.T("Zmień…"), () => ChooseRecDir(win)));
            var body = new DockPanel();
            DockPanel.SetDock(top, Dock.Top); body.Children.Add(top);
            DockPanel.SetDock(bar, Dock.Bottom); body.Children.Add(bar);
            DockPanel.SetDock(epgBox, Dock.Bottom); body.Children.Add(epgBox);
            DockPanel.SetDock(groups, Dock.Left); body.Children.Add(groups);
            body.Children.Add(list);
            win.Content = body;
            Action onJobs = () => { fillList(); fillEpg(); };   // ⏺ przy kanale nagrywanym
            _iptvJobsChanged += onJobs;
            win.Closing += (s, a) => { var b = win.RestoreBounds; st.Win = string.Join(",", new[] { b.Left, b.Top, b.Width, b.Height }.Select(v => ((int)v).ToString(CultureInfo.InvariantCulture))); SaveIptv(st); };
            _iptvReload = force => { _iptvSrc = cur(); return load(force); };
            win.Closed += (s, a) => { _tvWin = null; _iptvAdd = null; _iptvJobsChanged -= onJobs; _iptvReload = null; };
            _tvWin = win;
            fillSources();
            if (st.Sources.Count == 0) _ = load(false);
            win.Show();
        }

        sealed class TvRow : System.ComponentModel.INotifyPropertyChanged
        {
            public int Nr { get; set; } public string Name { get; set; } public string Now { get; set; } public string Next { get; set; } public IptvChannel Ch;
            public DateTime NowStart, NextStart;
            public string Watched { get; set; } public long WatchedMs;
            public string LogoUrl;
            ImageSource _logo;
            public ImageSource Logo { get { return _logo; } set { _logo = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs("Logo")); } }
            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        }
    }
}
