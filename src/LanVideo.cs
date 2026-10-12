using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Przegladarka
{
    // Filmy w sieci lokalnej (dodatek "odtwarzacz filmow" + sparowana synchronizacja LAN):
    // - "Odtworz na: <komputer>" - film z tej biblioteki gra na drugim Velivo,
    // - "Ogladaj razem z: <komputer>" - oba komputery startuja razem; pauza i przewijanie na jednym dzieje sie tez na drugim,
    // - biblioteka drugiego komputera - lista jego filmow; ogladasz tutaj (strumieniowo albo wprost z dysku sieciowego NAS).
    // Bezpieczenstwo: polecenia ida zaszyfrowanym kanalem LAN (tylko sparowane komputery tego profilu). Film udostepnia maly
    // serwer tylko pod losowym kluczem na ten jeden plik, tylko adresowi sparowanego komputera i najwyzej 12 h - bez listy plikow,
    // bez dostepu do innych sciezek. Serwer startuje dopiero przy pierwszym udostepnieniu (Windows zapyta wtedy o zapore).
    public partial class MainWindow
    {
        const int LanVideoPort = LanPort + 2;

        sealed class LanFilmMsg
        {
            public string to { get; set; }
            public string kind { get; set; }      // play / together / ctl / libreq / lib / prog / want
            public string session { get; set; }   // ogladanie razem
            public string req { get; set; }       // zapytanie o biblioteke
            public string id { get; set; }        // film w bibliotece wlasciciela (skrot sciezki)
            public string url { get; set; }       // strumien z komputera wlasciciela
            public string unc { get; set; }       // sciezka sieciowa (NAS) - drugi komputer otwiera wprost, gdy ja widzi
            public string sub { get; set; }       // napisy (strumien)
            public string title { get; set; }
            public long pos { get; set; }
            public long len { get; set; }
            public bool done { get; set; }
            public long startAt { get; set; }     // wspolny start (unix ms)
            public string act { get; set; }       // ctl: play / pause / seek / leave
            public List<LanFilmItem> items { get; set; }
            public LibraryVideo prefs { get; set; }   // ustawienia filmu: glosnosc, lektor, napisy, sciezka dzwieku
        }

        sealed class LanFilmItem
        {
            public string i { get; set; }   // id
            public string t { get; set; }   // tytul
            public long l { get; set; }     // dlugosc
            public long p { get; set; }     // miejsce
            public bool d { get; set; }     // obejrzany
            public long w { get; set; }     // ostatnio ogladany
            public string u { get; set; }   // strumien
            public string n { get; set; }   // sciezka sieciowa (NAS)
            public string s { get; set; }   // napisy
            public LibraryVideo f { get; set; }   // ustawienia filmu
        }

        // film z innego komputera w playliscie odtwarzacza (klucz = adres strumienia albo sciezka NAS)
        sealed class RemoteFilm { public string PeerId, Id, Title, Sub; public long Pos, Len; public LibraryVideo Prefs; }

        // ustawienia filmu do przeslania (bez sciezki - drugi komputer jej nie potrzebuje)
        internal static LibraryVideo PrefsOf(LibraryVideo v)
        {
            return v == null ? null : new LibraryVideo { Vol = v.Vol, LVol = v.LVol, LRate = v.LRate, LDuck = v.LDuck, LVoice = v.LVoice, Spu = v.Spu, Audio = v.Audio };
        }

        internal static void ApplyPrefs(LibraryVideo target, LibraryVideo p)
        {
            if (target == null || p == null) return;
            if (p.Vol != null) target.Vol = p.Vol; if (p.LVol != null) target.LVol = p.LVol; if (p.LRate != null) target.LRate = p.LRate;
            if (p.LDuck != null) target.LDuck = p.LDuck; if (p.LVoice != null) target.LVoice = p.LVoice;
            if (p.Spu != null) target.Spu = p.Spu; if (p.Audio != null) target.Audio = p.Audio;
        }
        readonly Dictionary<string, RemoteFilm> _remoteFilms = new Dictionary<string, RemoteFilm>(StringComparer.OrdinalIgnoreCase);

        // tekst napisow (plik albo pobrane z drugiego komputera): UTF-8, a gdy to nie UTF-8 - Windows-1250 (stare polskie SRT)
        internal static string SubtitleText(byte[] bytes)
        {
            try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
            catch (DecoderFallbackException) { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(1250).GetString(bytes); }
        }

        internal static string FilmId(string path)
        {
            using (var sha = SHA256.Create())
                return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes((path ?? "").ToLowerInvariant()))).Substring(0, 16);
        }

        // ---------- serwer filmow (tylko udostepnione pliki, tylko sparowany adres) ----------

        sealed class FilmShare { public string Path, PeerIp; public DateTime Until; }
        readonly ConcurrentDictionary<string, FilmShare> _filmShares = new ConcurrentDictionary<string, FilmShare>();
        TcpListener _filmServer;

        string ShareFilm(string path, LanPeerInfo peer)
        {
            if (path == null || !File.Exists(path) || peer == null) return null;
            var peerIp = PeerIp(peer);
            if (peerIp == null) return null;
            if (!EnsureFilmServer()) return null;
            foreach (var old in _filmShares.Where(x => x.Value.Until < DateTime.UtcNow).Select(x => x.Key).ToList()) _filmShares.TryRemove(old, out _);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            _filmShares[token] = new FilmShare { Path = path, PeerIp = peerIp, Until = DateTime.UtcNow.AddHours(12) };
            var me = LocalIpFor(peerIp);
            return me == null ? null : "http://" + me + ":" + LanVideoPort + "/v/" + token + "/" + Uri.EscapeDataString(Path.GetFileName(path));
        }

        static string PeerIp(LanPeerInfo p)
        {
            var a = p?.Address ?? ""; int c = a.LastIndexOf(':');
            var ip = c > 0 ? a.Substring(0, c) : a;
            return IPAddress.TryParse(ip, out _) ? ip : null;
        }

        // adres tego komputera widziany przez drugi (wlasciwa karta sieciowa)
        static string LocalIpFor(string peerIp)
        {
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect(IPAddress.Parse(peerIp), 9);
                    return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
                }
            }
            catch (Exception) { return null; }
        }

        bool EnsureFilmServer()
        {
            if (_filmServer != null) return true;
            try
            {
                var l = new TcpListener(IPAddress.Any, LanVideoPort);
                l.Start();
                _filmServer = l;
                _ = Task.Run(() => FilmServerLoop(l));
                return true;
            }
            catch (Exception ex) { App.LogError(ex); ShowToast(L.T("🎬 Nie udało się udostępnić filmu w sieci (port zajęty albo zablokowany)"), null); return false; }
        }

        async Task FilmServerLoop(TcpListener l)
        {
            while (true)
            {
                TcpClient c;
                try { c = await l.AcceptTcpClientAsync(); } catch (Exception) { return; }
                _ = Task.Run(() => ServeFilm(c));
            }
        }

        // zakres "bytes=a-b" -> (od, do) w granicach pliku; null = caly plik
        internal static (long From, long To)? ParseRange(string header, long size)
        {
            if (string.IsNullOrEmpty(header) || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || size <= 0) return null;
            var r = header.Substring(6).Split(',')[0].Trim().Split('-');
            if (r.Length != 2) return null;
            long a, b;
            if (r[0].Length == 0) { if (!long.TryParse(r[1], out b) || b <= 0) return null; return (Math.Max(0, size - b), size - 1); }   // ostatnie N bajtow
            if (!long.TryParse(r[0], out a) || a >= size) return null;
            if (r[1].Length == 0 || !long.TryParse(r[1], out b) || b >= size) b = size - 1;
            return b < a ? ((long, long)?)null : (a, b);
        }

        async Task ServeFilm(TcpClient c)
        {
            using (c)
            {
                try
                {
                    c.ReceiveTimeout = 15000; c.SendTimeout = 30000;
                    var ns = c.GetStream();
                    var head = new StringBuilder(); var buf = new byte[1];
                    while (head.Length < 8192 && !head.ToString().EndsWith("\r\n\r\n"))
                    {
                        if (await ns.ReadAsync(buf, 0, 1) != 1) return;
                        head.Append((char)buf[0]);
                    }
                    var lines = head.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var first = lines[0].Split(' ');
                    string range = lines.Skip(1).Where(x => x.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)).Select(x => x.Substring(6).Trim()).FirstOrDefault();
                    var remoteIp = ((IPEndPoint)c.Client.RemoteEndPoint).Address.MapToIPv4().ToString();
                    var parts = first.Length >= 2 ? first[1].Split('/') : new string[0];   // /v/<klucz>/<nazwa>
                    FilmShare share = null;
                    bool ok = first.Length >= 2 && (first[0] == "GET" || first[0] == "HEAD") && parts.Length >= 3 && parts[1] == "v"
                        && _filmShares.TryGetValue(parts[2], out share) && share.Until > DateTime.UtcNow && share.PeerIp == remoteIp && File.Exists(share.Path);
                    if (!ok)
                    {
                        var no = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        await ns.WriteAsync(no, 0, no.Length);
                        return;
                    }
                    using (var fs = new FileStream(share.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, true))
                    {
                        long size = fs.Length;
                        var rg = ParseRange(range, size);
                        long from = rg?.From ?? 0, to = rg?.To ?? size - 1, count = Math.Max(0, to - from + 1);
                        var hdr = (rg != null ? "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes " + from + "-" + to + "/" + size + "\r\n" : "HTTP/1.1 200 OK\r\n")
                            + "Content-Type: application/octet-stream\r\nAccept-Ranges: bytes\r\nContent-Length: " + count + "\r\nConnection: close\r\n\r\n";
                        var hb = Encoding.ASCII.GetBytes(hdr);
                        await ns.WriteAsync(hb, 0, hb.Length);
                        if (first[0] == "HEAD") return;
                        fs.Position = from;
                        var data = new byte[1 << 16];
                        while (count > 0)
                        {
                            int n = await fs.ReadAsync(data, 0, (int)Math.Min(data.Length, count));
                            if (n <= 0) break;
                            await ns.WriteAsync(data, 0, n);
                            count -= n;
                        }
                    }
                }
                catch (Exception) { }   // przewiniecie / zamkniecie odtwarzacza zrywa polaczenie - to normalne
            }
        }

        // ---------- dyski sieciowe: Z:\film.mkv -> \\nas\filmy\film.mkv (drugi komputer moze miec inna litere) ----------

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

        static string NetworkPath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return null;
                if (path.StartsWith(@"\\")) return path;
                var root = Path.GetPathRoot(path);
                if (root == null || root.Length < 2 || root[1] != ':') return null;
                var sb = new StringBuilder(512); int len = sb.Capacity;
                if (WNetGetConnection(root.Substring(0, 2), sb, ref len) != 0) return null;   // dysk lokalny
                return sb.ToString().TrimEnd('\\') + "\\" + path.Substring(root.Length);
            }
            catch (Exception) { return null; }
        }

        // ---------- wysylanie ----------

        void SendFilmMsg(LanPeerInfo peer, LanFilmMsg m)
        {
            if (_lanTx == null || _lanEncryptionKey == null || peer == null) return;
            m.to = peer.Id;
            var pkt = new LanStatePacket { t = "film", id = _lanId, device = _lanDeviceName, profile = SelectedProfileName, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), hash = "" };
            EncryptLanBlob(pkt, JsonSerializer.SerializeToUtf8Bytes(m));
            LanSend(pkt);
        }

        LanPeerInfo PeerById(string id) { return SendTargets().FirstOrDefault(p => p.Id == id); }

        // film z biblioteki tego komputera dla drugiego: strumien + sciezka NAS + napisy
        LanFilmMsg FilmFor(LibraryVideo v, LanPeerInfo peer, string kind)
        {
            var sub = FindSubtitleFor(v.Path);
            return new LanFilmMsg
            {
                kind = kind, id = FilmId(v.Path), title = v.Title, pos = v.Done ? 0 : v.Pos, len = v.Len, done = v.Done,
                url = ShareFilm(v.Path, peer), unc = NetworkPath(v.Path), sub = sub != null ? ShareFilm(sub, peer) : null, prefs = PrefsOf(v),
            };
        }

        // "Odtworz na" / "Ogladaj razem" z biblioteki tego komputera
        void PlayFilmOn(LibraryVideo v, LanPeerInfo peer, bool together)
        {
            if (!File.Exists(v.Path)) return;
            var m = FilmFor(v, peer, together ? "together" : "play");
            if (m.url == null && m.unc == null) { ShowToast(L.T("🎬 Nie udało się udostępnić filmu w sieci"), null); return; }
            if (together)
            {
                m.session = Guid.NewGuid().ToString("N");
                m.startAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3000;   // obu komputerom 3 s na wczytanie
                _together = new TogetherSession { PeerId = peer.Id, Session = m.session };
                var path = v.Path;
                StartAt(m.startAt, () => PlayVideos(new[] { path }, 0));   // miejsce startu: wznowienie z biblioteki (to samo, co wyslane)
            }
            SendFilmMsg(peer, m);
            ShowToast((together ? L.T("👥 Oglądacie razem z: ") : L.T("📺 Film gra na: ")) + peer.Device, null);
        }

        void StartAt(long unixMs, Action act)
        {
            var wait = Math.Max(0, unixMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, wait)) };
            t.Tick += (s, e) => { t.Stop(); act(); };
            t.Start();
        }

        // ---------- ogladanie razem ----------

        sealed class TogetherSession { public string PeerId, Session; }
        TogetherSession _together;
        Action<string, long> _vlcRemoteCtl;   // ustawia odtwarzacz (z drugiego komputera), bez odsylania

        // wyjscie ze wspolnego ogladania tylko tutaj - drugi komputer oglada dalej sam
        void LeaveTogether()
        {
            if (_together == null) return;
            TogetherSend("leave", 0);
            _together = null;
        }

        void TogetherSend(string act, long pos)
        {
            var t = _together; if (t == null) return;
            var p = PeerById(t.PeerId); if (p == null) return;
            SendFilmMsg(p, new LanFilmMsg { kind = "ctl", session = t.Session, act = act, pos = pos });
        }

        // ---------- biblioteka drugiego komputera ----------

        readonly Dictionary<string, TaskCompletionSource<List<LanFilmItem>>> _libRequests = new Dictionary<string, TaskCompletionSource<List<LanFilmItem>>>();

        async Task<List<LanFilmItem>> RequestLibrary(LanPeerInfo peer)
        {
            var req = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<List<LanFilmItem>>();
            _libRequests[req] = tcs;
            SendFilmMsg(peer, new LanFilmMsg { kind = "libreq", req = req });
            var done = await Task.WhenAny(tcs.Task, Task.Delay(6000));
            _libRequests.Remove(req);
            return done == tcs.Task ? tcs.Task.Result : null;
        }

        // wspolna biblioteka dla drugiego komputera: ostatnio ogladane na gorze, najwyzej tyle, ile zmiesci sie w pakiecie
        List<LanFilmItem> LibraryFor(LanPeerInfo peer)
        {
            var list = new List<LanFilmItem>();
            int size = 0;   // pakiet LAN ma limit ok. 60 KB - lista najwyzej do ok. 120 KB tekstu przed kompresja
            foreach (var v in LoadLibrary().OrderByDescending(x => x.Last).ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).Take(300))
            {
                if (!IsShared(v) || !File.Exists(v.Path)) continue;   // prywatna biblioteka nie wychodzi z komputera
                size += 260 + v.Title.Length + v.Path.Length * 2;
                if (size > 120000) break;
                var t = v.Title.Length > 90 ? v.Title.Substring(0, 90) : v.Title;
                list.Add(new LanFilmItem { i = FilmId(v.Path), t = t, l = v.Len, p = v.Done ? 0 : v.Pos, d = v.Done, w = v.Last, n = NetworkPath(v.Path), f = PrefsOf(v) });
            }
            return list;
        }

        // wybrany film z biblioteki drugiego komputera: prosimy o strumien (klucz powstaje dopiero teraz, na ten jeden film)
        void WantRemoteFilm(LanPeerInfo peer, LanFilmItem item, bool together)
        {
            if (!together && item.n != null && File.Exists(item.n))   // NAS widoczny tutaj - wprost z dysku sieciowego, z ustawieniami filmu
            {
                _remoteFilms[item.n] = new RemoteFilm { PeerId = peer.Id, Id = item.i, Title = item.t, Pos = item.p, Len = item.l, Prefs = item.f };
                PlayVideos(new[] { item.n }, 0); return;
            }
            SendFilmMsg(peer, new LanFilmMsg { kind = "want", id = item.i, act = together ? "together" : "play" });
            ShowToast(L.T("🎬 Łączę z: ") + peer.Device + "…", null);
        }

        // ---------- odbior ----------

        void ReceiveLanFilm(LanStatePacket pkt, LanFilmMsg m)
        {
            if (m == null || m.to != _lanId) return;
            if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - pkt.ts) > 120000) return;   // stare / powtorzone polecenie
            var peer = PeerById(pkt.id);
            if (peer == null) return;
            switch (m.kind)
            {
                case "libreq":   // lista w tle - wylaczony NAS nie moze zawiesic okna
                    _ = Task.Run(() => { var items = LibraryFor(peer); Dispatcher.BeginInvoke(new Action(() => SendFilmMsg(peer, new LanFilmMsg { kind = "lib", req = m.req, items = items }))); });
                    break;
                case "lib":
                    if (m.req != null && _libRequests.TryGetValue(m.req, out var tcs)) tcs.TrySetResult(m.items ?? new List<LanFilmItem>());
                    break;
                case "want":   // drugi komputer chce obejrzec film z tej biblioteki
                {
                    var v = LoadLibrary().FirstOrDefault(x => FilmId(x.Path) == m.id && File.Exists(x.Path));
                    if (v != null) PlayFilmOn(v, peer, m.act == "together");
                    break;
                }
                case "play":
                case "together":
                    if (!VlcInstalled) { ShowToast("🎬 " + peer.Device + L.T(" wysłał film – zainstaluj dodatek „odtwarzacz filmów”, aby go obejrzeć"), null); return; }
                    var key = m.unc != null && File.Exists(m.unc) ? m.unc : m.url;   // NAS widoczny tutaj - wprost z dysku sieciowego
                    if (key == null || !(key.StartsWith(@"\\") || key.StartsWith("http://"))) return;
                    _remoteFilms[key] = new RemoteFilm { PeerId = peer.Id, Id = m.id, Title = m.title, Sub = key.StartsWith("http") ? m.sub : null, Pos = m.pos, Len = m.len, Prefs = m.prefs };
                    if (m.kind == "together") _together = new TogetherSession { PeerId = peer.Id, Session = m.session };
                    StartAt(m.kind == "together" ? m.startAt : 0, () => PlayVideos(new[] { key }, 0));
                    if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                    ShowToast((m.kind == "together" ? L.T("👥 Oglądacie razem z: ") : L.T("📺 Film z: ")) + peer.Device + " – " + m.title, null);
                    break;
                case "ctl":
                    if (_together == null || _together.Session != m.session || _together.PeerId != peer.Id) return;
                    if (m.act == "leave") _together = null;   // druga osoba wyszla - dalej bez synchronizacji
                    _vlcRemoteCtl?.Invoke(m.act, m.pos);
                    break;
                case "prog":   // postep ogladania filmu z tej biblioteki na drugim komputerze
                {
                    var l = LoadLibrary(); var v = l.FirstOrDefault(x => FilmId(x.Path) == m.id);
                    if (v == null) return;
                    ApplyPrefs(v, m.prefs);   // glosnosc, lektor, napisy ustawione na drugim komputerze
                    if (m.done) { v.Done = true; v.Pos = 0; v.Last = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); if (m.len > 0) v.Len = m.len; }
                    else RecordProgress(v, m.pos, m.len, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    SaveLibrary(l); _libRefresh?.Invoke();
                    break;
                }
            }
        }

        // postep filmu z innego komputera - zapisuje sie w jego bibliotece
        void SendRemoteProgress(RemoteFilm rf, long pos, long len, bool done)
        {
            var p = PeerById(rf.PeerId);
            if (p == null || rf.Id == null) return;
            SendFilmMsg(p, new LanFilmMsg { kind = "prog", id = rf.Id, pos = pos, len = len, done = done, prefs = PrefsOf(rf.Prefs) });
        }
    }
}
