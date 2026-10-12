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

        internal sealed class EpgItem { public DateTime Start, Stop; public string Title, Desc; }

        sealed class IptvSource
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Url { get; set; }   // adres listy albo sciezka pliku
            public string Epg { get; set; }   // adres programu TV (opcjonalnie)
            public long Updated { get; set; }
        }

        sealed class IptvState
        {
            public List<IptvSource> Sources { get; set; } = new List<IptvSource>();
            public List<string> Fav { get; set; } = new List<string>();      // adresy ulubionych kanalow
            public List<string> Recent { get; set; } = new List<string>();   // ostatnio ogladane (najnowsze pierwsze)
            public string Last { get; set; }
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
        internal static Dictionary<string, List<EpgItem>> ParseXmltv(Stream input, DateTime from, DateTime to)
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
                while (r.Read())
                {
                    if (r.NodeType != XmlNodeType.Element) continue;
                    if (r.Name == "channel")
                    {
                        var id = r.GetAttribute("id") ?? "";
                        using (var sub = r.ReadSubtree())
                            while (sub.Read())
                                if (sub.NodeType == XmlNodeType.Element && sub.Name == "display-name") { var dn = sub.ReadElementContentAsString().Trim(); if (dn.Length > 0 && !names.ContainsKey(dn)) names[dn] = id; }
                    }
                    else if (r.Name == "programme")
                    {
                        var start = ParseXmltvTime(r.GetAttribute("start")); var stop = ParseXmltvTime(r.GetAttribute("stop"));
                        var ch = r.GetAttribute("channel") ?? "";
                        if (start == null || stop == null || stop < from || start > to) { r.Skip(); continue; }
                        var it = new EpgItem { Start = start.Value, Stop = stop.Value };
                        using (var sub = r.ReadSubtree())
                            while (sub.Read())
                            {
                                if (sub.NodeType != XmlNodeType.Element) continue;
                                if (sub.Name == "title" && it.Title == null) it.Title = sub.ReadElementContentAsString().Trim();
                                else if (sub.Name == "desc" && it.Desc == null) it.Desc = sub.ReadElementContentAsString().Trim();
                            }
                        if (!byId.TryGetValue(ch, out var l)) byId[ch] = l = new List<EpgItem>();
                        l.Add(it);
                    }
                }
            }
            foreach (var l in byId.Values) l.Sort((a, b) => a.Start.CompareTo(b.Start));
            foreach (var kv in names) if (!byId.ContainsKey(kv.Key) && byId.TryGetValue(kv.Value, out var l)) byId[kv.Key] = l;
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

        internal static List<EpgItem> EpgFor(Dictionary<string, List<EpgItem>> epg, IptvChannel ch)
        {
            if (epg == null) return null;
            if (!string.IsNullOrEmpty(ch.TvgId) && epg.TryGetValue(ch.TvgId, out var l)) return l;
            return epg.TryGetValue(ch.Name ?? "", out l) ? l : null;
        }

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
        static async Task DownloadTo(string url, string file)
        {
            if (File.Exists(url)) { File.Copy(url, file, true); return; }
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Velivo");
                using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    var tmp = file + ".tmp";
                    using (var fs = File.Create(tmp)) await (await resp.Content.ReadAsStreamAsync()).CopyToAsync(fs);
                    File.Copy(tmp, file, true); File.Delete(tmp);
                }
            }
        }

        // ---------- przycisk 📺 ----------

        Button _tvBtn;
        Window _tvWin;
        readonly Dictionary<string, IptvChannel> _iptvPlaying = new Dictionary<string, IptvChannel>();   // kanaly na liscie odtwarzania (adres -> kanal)
        Dictionary<string, List<EpgItem>> _iptvEpg;

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
            _tvBtn.ContextMenu = menu;
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

        // okno listy: zrodlo (lista), grupy, kanaly z "teraz / nastepnie", program wybranego kanalu
        void ShowIptv(Window owner)
        {
            if (_tvWin != null && _tvWin.IsLoaded) { if (_tvWin.Owner != owner) _tvWin.Owner = owner; _tvWin.Topmost = owner.Topmost; _tvWin.Activate(); return; }
            var st = LoadIptv();
            var win = new Window { Title = L.T("📺 Telewizja"), Width = 980, Height = 600, Owner = owner, Topmost = owner.Topmost, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var source = new ComboBox { Width = 200, Margin = new Thickness(8, 8, 0, 4), VerticalContentAlignment = VerticalAlignment.Center };
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
            grid.Columns.Add(new GridViewColumn { Header = L.T("Nr"), Width = 50, DisplayMemberBinding = new System.Windows.Data.Binding("Nr") });
            grid.Columns.Add(new GridViewColumn { Header = L.T("Kanał"), Width = 230, DisplayMemberBinding = new System.Windows.Data.Binding("Name") });
            grid.Columns.Add(new GridViewColumn { Header = L.T("Teraz"), Width = 260, DisplayMemberBinding = new System.Windows.Data.Binding("Now") });
            grid.Columns.Add(new GridViewColumn { Header = L.T("Następnie"), Width = 200, DisplayMemberBinding = new System.Windows.Data.Binding("Next") });
            var list = new ListView { View = grid, Margin = new Thickness(8, 0, 8, 0), SelectionMode = SelectionMode.Single };
            var epgText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 6, 10, 6), Foreground = Brushes.DimGray };
            var epgBox = new ScrollViewer { Content = epgText, Height = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray, Margin = new Thickness(10, 0, 0, 0) };

            IptvPlaylist pl = new IptvPlaylist();
            List<TvRow> rows = new List<TvRow>();
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
                var now = DateTime.Now; var epg = _iptvEpg;
                rows = chans.Select(c =>
                {
                    var l = EpgFor(epg, c); var n0 = EpgAt(l, now); var nx = l?.FirstOrDefault(x => x.Start >= (n0?.Stop ?? now));
                    return new TvRow
                    {
                        Ch = c, Nr = pl.Channels.IndexOf(c) + 1, Name = (st.Fav.Contains(c.Url) ? "⭐ " : "") + c.Name,
                        Now = n0 != null ? n0.Start.ToString("HH:mm") + "  " + n0.Title : "", Next = nx != null ? nx.Start.ToString("HH:mm") + "  " + nx.Title : "",
                    };
                }).ToList();
                list.ItemsSource = rows;
                count.Text = rows.Count + " / " + pl.Channels.Count + L.T(" kanałów") + (epg != null ? "  ·  " + L.T("program TV wczytany") : "");
            };
            Action fillGroups = () =>
            {
                var sel = (groups.SelectedItem as ListBoxItem)?.Tag as string;
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
                if (src == null) { pl = new IptvPlaylist(); _iptvEpg = null; fillGroups(); fillList(); count.Text = L.T("Dodaj listę kanałów (plik .m3u albo adres URL) – przycisk „➕ Dodaj listę”."); return; }
                var m3u = IptvCache(src, ".m3u"); var epgFile = IptvCache(src, ".epg");
                bool stale = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - src.Updated > 12 * 3600 * 1000L;
                try
                {
                    if (File.Exists(m3u)) { var t = await Task.Run(() => ParseM3u(File.ReadAllText(m3u))); if (ver != loadVer) return; pl = t; fillGroups(); fillList(); }
                    if (force || stale || !File.Exists(m3u))
                    {
                        count.Text = L.T("Wczytuję listę kanałów…");
                        await Task.Run(() => DownloadTo(src.Url, m3u));
                        var t = await Task.Run(() => ParseM3u(File.ReadAllText(m3u)));
                        if (ver != loadVer) return;
                        pl = t; fillGroups(); fillList();
                    }
                    var epgUrl = !string.IsNullOrWhiteSpace(src.Epg) ? src.Epg : pl.EpgUrl;
                    if (string.IsNullOrWhiteSpace(epgUrl)) { _iptvEpg = null; fillList(); }
                    else
                    {
                        if (force || stale || !File.Exists(epgFile)) { count.Text = L.T("Wczytuję program TV…"); await Task.Run(() => DownloadTo(epgUrl, epgFile)); }
                        var e = await Task.Run(() => { using (var fs = File.OpenRead(epgFile)) return ParseXmltv(fs, DateTime.Now.AddHours(-6), DateTime.Now.AddHours(30)); });
                        if (ver != loadVer) return;
                        _iptvEpg = e; fillList();
                    }
                    if (force || stale) { src.Updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); SaveIptv(st); }
                }
                catch (Exception ex)
                {
                    App.LogError(ex);
                    if (ver == loadVer) count.Text = L.T("⚠ Nie udało się wczytać: ") + ex.Message;
                }
            };
            Action fillSources = () =>
            {
                source.Items.Clear();
                foreach (var s0 in st.Sources) source.Items.Add(new ComboBoxItem { Content = "📋 " + s0.Name, Tag = s0.Id });
                source.SelectedItem = source.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == st.Last) ?? (source.Items.Count > 0 ? source.Items[0] : null);
            };
            source.SelectionChanged += (s, a) => { var c = cur(); if (c != null && st.Last != c.Id) { st.Last = c.Id; SaveIptv(st); } _ = load(false); };
            groups.SelectionChanged += (s, a) => fillList();
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
                var ok = new Button { Content = "OK", Width = 90, Height = 28, IsDefault = true, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
                ok.Click += (s, a) => d.DialogResult = true;
                p.Children.Add(ok);
                d.Content = p;
                d.Loaded += (s, a) => name.Focus();
                if (d.ShowDialog() != true || url.Text.Trim().Length == 0) return false;
                src.Url = url.Text.Trim(); src.Epg = epg.Text.Trim();
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
                st.Recent.Remove(r.Ch.Url); st.Recent.Insert(0, r.Ch.Url); if (st.Recent.Count > 30) st.Recent.RemoveRange(30, st.Recent.Count - 30);
                SaveIptv(st);
                PlayVideos(rows.Select(x => x.Ch.Url).ToArray(), rows.IndexOf(r));
            };
            Action toggleFav = () =>
            {
                var r = list.SelectedItem as TvRow; if (r == null) return;
                if (!st.Fav.Remove(r.Ch.Url)) st.Fav.Add(r.Ch.Url);
                SaveIptv(st); fillList();
            };
            list.MouseDoubleClick += (s, a) => play();
            list.PreviewKeyDown += (s, a) => { if (a.Key == Key.Enter) { play(); a.Handled = true; } };
            list.SelectionChanged += (s, a) =>
            {
                var r = list.SelectedItem as TvRow; if (r == null) { epgText.Text = ""; return; }
                var l = EpgFor(_iptvEpg, r.Ch);
                if (l == null) { epgText.Text = _iptvEpg == null ? L.T("Brak programu TV – dodaj adres EPG w „✏ Edytuj”.") : L.T("Brak programu dla tego kanału."); return; }
                var now = DateTime.Now;
                epgText.Text = string.Join("\n", l.Where(x => x.Stop > now).Take(12).Select(x => (x.Start <= now ? "▶ " : "    ") + x.Start.ToString("HH:mm") + "–" + x.Stop.ToString("HH:mm") + "  " + x.Title +
                    (x.Start <= now && !string.IsNullOrEmpty(x.Desc) ? "\n        " + (x.Desc.Length > 300 ? x.Desc.Substring(0, 300) + "…" : x.Desc) : "")));
            };
            var menu = new ContextMenu();
            menu.Opened += (s, a) =>
            {
                menu.Items.Clear();
                var r = list.SelectedItem as TvRow;
                Action<string, Action, bool> add = (t, act, on) => { var mi = new MenuItem { Header = t, IsEnabled = on }; mi.Click += (x, y) => act(); menu.Items.Add(mi); };
                add(L.T("▶ Oglądaj"), play, r != null);
                add(r != null && st.Fav.Contains(r.Ch.Url) ? L.T("⭐ Usuń z ulubionych") : L.T("⭐ Dodaj do ulubionych"), toggleFav, r != null);
                add(L.T("📋 Kopiuj adres kanału"), () => { try { Clipboard.SetText(r.Ch.Url); } catch (Exception) { } }, r != null);
                menu.Items.Add(new Separator());
                add(L.T("🔄 Odśwież listę i program TV"), () => _ = load(true), cur() != null);
                add(L.T("➕ Dodaj listę kanałów…"), _iptvAdd, true);
            };
            list.ContextMenu = menu;

            var bar = new DockPanel { Margin = new Thickness(6), LastChildFill = false };
            DockPanel.SetDock(count, Dock.Left); bar.Children.Add(count);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(buttons, Dock.Right); bar.Children.Add(buttons);
            buttons.Children.Add(SmallButton(L.T("▶ Oglądaj"), play));
            buttons.Children.Add(SmallButton(L.T("⭐ Ulubiony"), toggleFav));
            var body = new DockPanel();
            DockPanel.SetDock(top, Dock.Top); body.Children.Add(top);
            DockPanel.SetDock(bar, Dock.Bottom); body.Children.Add(bar);
            DockPanel.SetDock(epgBox, Dock.Bottom); body.Children.Add(epgBox);
            DockPanel.SetDock(groups, Dock.Left); body.Children.Add(groups);
            body.Children.Add(list);
            win.Content = body;
            win.Closed += (s, a) => { _tvWin = null; _iptvAdd = null; };
            _tvWin = win;
            fillSources();
            if (st.Sources.Count == 0) _ = load(false);
            win.Show();
        }

        sealed class TvRow { public int Nr { get; set; } public string Name { get; set; } public string Now { get; set; } public string Next { get; set; } public IptvChannel Ch; }
    }
}
