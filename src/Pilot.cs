using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Windows.Threading;

namespace Velivo
{
    // 🎮 Velivo Pilot: telefon (sparowany przez synchronizację LAN – ten sam klucz, ta sama ochrona) steruje odtwarzaczem tego komputera.
    // Telefon wysyła „pilot” (polecenie albo „ping”), komputer odpowiada „pilot-st” (stan odtwarzacza) – wprost na adres telefonu,
    // zaszyfrowane kluczem grupy; dopóki telefon ma otwarty Pilot (ping co kilka sekund), stan idzie co sekundę i od razu po każdej zmianie.
    public partial class MainWindow
    {
        internal sealed class LanPilotCmd { public string to { get; set; } public string op { get; set; } public string v { get; set; } public double n { get; set; } public string q { get; set; } public string g { get; set; } public string s { get; set; } }

        Func<object> _vlcPilotState;                    // stan odtwarzacza (null = odtwarzacz zamknięty) – ustawia VlcPlayer
        Action<string, string, double> _vlcPilotCmd;    // polecenie dla odtwarzacza – ustawia VlcPlayer
        readonly Dictionary<string, (IPEndPoint Ep, DateTime Seen)> _pilotClients = new Dictionary<string, (IPEndPoint, DateTime)>();
        DispatcherTimer _pilotTimer;

        void ReceivePilot(LanStatePacket pkt, LanPilotCmd cmd, IPEndPoint remote)
        {
            if (cmd == null || remote == null || (cmd.to != _lanId && cmd.to != "*")) return;
            _pilotClients[pkt.id] = (remote, DateTime.UtcNow);   // odpowiedź na ten sam adres i port, z którego telefon wysłał
            if (cmd.to == _lanId && (cmd.op == "list" || cmd.op == "play-tv" || cmd.op == "play-film" || cmd.op == "fav-tv" || cmd.op == "fav-film" || cmd.op == "info"))   // 📺🎬 listy i włączanie z telefonu (odtwarzacz nie musi być otwarty)
            {
                try { PilotLists(pkt.id, cmd); } catch (Exception ex) { App.LogError(ex); }
            }
            else if (cmd.op != "ping" && cmd.to == _lanId)   // polecenie tylko do jednego, wybranego komputera
            {
                try { _vlcPilotCmd?.Invoke(cmd.op ?? "", cmd.v, cmd.n); } catch (Exception ex) { App.LogError(ex); }
                var t1 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                t1.Tick += (s, e) => { t1.Stop(); SendPilotState(pkt.id); };
                t1.Start();
            }
            SendPilotState(pkt.id);
            if (_pilotTimer == null)
            {
                _pilotTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _pilotTimer.Tick += (s, e) =>
                {
                    foreach (var k in _pilotClients.Where(c => DateTime.UtcNow - c.Value.Seen > TimeSpan.FromSeconds(30)).Select(c => c.Key).ToList()) _pilotClients.Remove(k);   // pilot zamknięty
                    if (_pilotClients.Count == 0) { _pilotTimer.Stop(); _pilotTimer = null; return; }
                    foreach (var k in _pilotClients.Keys.ToList()) SendPilotState(k);
                };
                _pilotTimer.Start();
            }
        }

        // listy dla Pilota: te same dane co Kanapa (kanały ze znanej listy TV, biblioteka bez powtórek), po 100 pozycji (jeden pakiet UDP)
        const string PilotFavList = "⭐ Ulubione";
        List<IptvChannel> _pilotTv = new List<IptvChannel>();
        List<LibraryVideo> _pilotFilms = new List<LibraryVideo>();

        void PilotLists(string phoneId, LanPilotCmd cmd)
        {
            string q = (cmd.q ?? "").Trim();
            if (cmd.op == "play-tv")
            {
                int i = (int)cmd.n;
                if (i < 0 || i >= _pilotTv.Count || _pilotTv[i].Name != cmd.v) return;
                foreach (var x in _pilotTv) _iptvPlaying[x.Url] = x;
                if (_kanapa != null) _kanapa.Close();
                PlayVideos(_pilotTv.Select(x => x.Url).ToArray(), i);   // poprzedni / następny = kanały tej samej listy
                return;
            }
            if (cmd.op == "info")   // ℹ opis: film (Jellyfin) albo co teraz i co dalej na kanale (program TV)
            {
                int i = (int)cmd.n; string head = "", text = "";
                if (cmd.v == "tv" && i >= 0 && i < _pilotTv.Count)
                {
                    var ch = _pilotTv[i]; head = ch.Name;
                    try
                    {
                        var epg = _iptvEpg == null ? null : EpgFor(_iptvEpg, ch);
                        var now = epg == null ? null : EpgAt(epg, DateTime.Now);
                        var next = now == null || epg == null ? null : EpgAt(epg, now.Stop.AddSeconds(30));
                        text = now == null ? L.T("Brak programu TV dla tego kanału.") :
                            L.T("Teraz") + " " + now.Start.ToString("HH:mm") + "–" + now.Stop.ToString("HH:mm") + ": " + now.Title + (string.IsNullOrWhiteSpace(now.Desc) ? "" : "\n\n" + now.Desc) +
                            (next != null ? "\n\n" + L.T("Potem") + " " + next.Start.ToString("HH:mm") + ": " + next.Title : "");
                    }
                    catch (Exception) { }
                }
                else if (cmd.v == "film" && i >= 0 && i < _pilotFilms.Count)
                {
                    var v = _pilotFilms[i]; var m = MetaFor(v.Path); head = ShownTitle(v, m);
                    var parts = new List<string>();
                    if (m?.Year > 0) parts.Add(m.Year.ToString());
                    if (m?.Rating > 0) parts.Add("★ " + m.Rating.ToString("0.0"));
                    if (m?.Genres?.Count > 0) parts.Add(string.Join(", ", m.Genres.Take(3)));
                    if (v.Len > 0) parts.Add((int)(v.Len / 60000) + L.T(" min"));
                    if (!string.IsNullOrEmpty(m?.Age)) parts.Add(m.Age);
                    var ov = m?.Overview ?? ""; if (ov.Length > 1500) ov = ov.Substring(0, 1500) + "…";
                    text = string.Join(" · ", parts) + "\n\n" + (string.IsNullOrWhiteSpace(ov) ? L.T("Brak opisu – uzupełnij dane filmu w bibliotece (Jellyfin).") : ov);
                }
                if (_lanTx == null || _lanEncryptionKey == null || !_pilotClients.TryGetValue(phoneId, out var ce)) return;
                var pi = new LanStatePacket { t = "pilot-list", id = _lanId, device = _lanDeviceName, profile = SelectedProfileName, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), hash = "" };
                EncryptLanBlob(pi, JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["to"] = phoneId, ["kind"] = "info", ["title"] = head, ["text"] = text }));
                var bi = JsonSerializer.SerializeToUtf8Bytes(pi); _lanTx.Send(bi, bi.Length, ce.Ep);
                return;
            }
            if (cmd.op == "fav-tv")   // ⭐ ulubione kanały - te same co w odtwarzaczu i Kanapie
            {
                int i = (int)cmd.n;
                if (i < 0 || i >= _pilotTv.Count || _pilotTv[i].Name != cmd.v) return;
                var u = _pilotTv[i].Url; if (!IptvSt.Fav.Remove(u)) IptvSt.Fav.Add(u); SaveIptv(IptvSt);
                return;
            }
            if (cmd.op == "fav-film")   // ⭐ ulubione filmy - lista „⭐ Ulubione” jak w odtwarzaczu
            {
                int i = (int)cmd.n;
                if (i < 0 || i >= _pilotFilms.Count || ShownTitle(_pilotFilms[i], MetaFor(_pilotFilms[i].Path)) != cmd.v) return;
                var p = LoadPlaylists(); if (!p.Named.TryGetValue(PilotFavList, out var l)) p.Named[PilotFavList] = l = new List<string>();
                var path = _pilotFilms[i].Path; if (l.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase)) == 0) l.Add(path);
                SavePlaylists(p);
                return;
            }
            if (cmd.op == "play-film")
            {
                int i = (int)cmd.n;
                if (i < 0 || i >= _pilotFilms.Count || ShownTitle(_pilotFilms[i], MetaFor(_pilotFilms[i].Path)) != cmd.v) return;
                if (_kanapa != null) _kanapa.Close();
                PlayVideos(new[] { _pilotFilms[i].Path }, 0);
                return;
            }
            int page = Math.Max(0, (int)cmd.n);
            var items = new List<object[]>(); var groups = new List<string>(); int total;
            if (cmd.v == "tv" || cmd.v == "fav")
            {
                var all = KnownPlaylist()?.Channels ?? new List<IptvChannel>();
                if (_iptvEpg == null) LoadIptvInBackground(() => { });   // „co teraz leci” przy następnym odświeżeniu listy
                if (cmd.v == "fav") _pilotTv = IptvSt.Fav.Select(u => _iptvPlaying.TryGetValue(u, out var c1) ? c1 : all.FirstOrDefault(c => c.Url == u)).Where(c => c != null).ToList();
                else
                {
                    groups = all.Select(c => c.Group ?? "").Where(g => g.Length > 0).Distinct().OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase).ToList();
                    _pilotTv = all.Where(c => (string.IsNullOrEmpty(cmd.g) || c.Group == cmd.g) && (q.Length == 0 || (c.Name ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0)).ToList();
                }
                if (cmd.s == "name") _pilotTv = _pilotTv.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                else if (cmd.s == "name-desc") _pilotTv = _pilotTv.OrderByDescending(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                total = _pilotTv.Count;
                for (int i = page * 100; i < Math.Min(total, page * 100 + 100); i++)
                {
                    var ch = _pilotTv[i]; string now = null;
                    try { now = _iptvEpg == null ? null : EpgAt(EpgFor(_iptvEpg, ch), DateTime.Now)?.Title; } catch (Exception) { }
                    items.Add(new object[] { i, ch.Name ?? "", now ?? ch.Group ?? "", IptvSt.Fav.Contains(ch.Url) });
                }
            }
            else
            {
                var lib = LoadLibrary().Where(v => !string.IsNullOrEmpty(v.Path))
                    .GroupBy(v => { var m = MetaFor(v.Path); return !string.IsNullOrEmpty(m?.Id) ? "j:" + m.Id : "t:" + ShownTitle(v, m).ToLowerInvariant() + "|" + (m?.Year ?? 0); })
                    .Select(g => g.OrderByDescending(v => v.Last).ThenBy(v => v.Peer != null).First()).ToList();   // jeden film raz (jak Kanapa)
                groups = lib.SelectMany(v => MetaFor(v.Path)?.Genres ?? new List<string>()).Distinct().OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase).ToList();
                var favs = new HashSet<string>(LoadPlaylists().Named.TryGetValue(PilotFavList, out var fl) ? fl : new List<string>(), StringComparer.OrdinalIgnoreCase);
                IEnumerable<LibraryVideo> sel = lib;
                if (cmd.v == "cont") sel = lib.Where(v => v.Pos > 0 && !v.Done && v.Last > 0).OrderByDescending(v => v.Last);
                else
                {
                    if (cmd.v == "ffav") sel = sel.Where(v => favs.Contains(v.Path));
                    if (!string.IsNullOrEmpty(cmd.g)) sel = sel.Where(v => MetaFor(v.Path)?.Genres?.Contains(cmd.g) == true);
                    if (q.Length > 0) sel = sel.Where(v => ShownTitle(v, MetaFor(v.Path)).IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0);
                    Func<LibraryVideo, string> t = v => ShownTitle(v, MetaFor(v.Path));
                    sel = cmd.s == "new" ? sel.OrderByDescending(v => v.Added) : cmd.s == "year" ? sel.OrderByDescending(v => MetaFor(v.Path)?.Year ?? 0)
                        : cmd.s == "rating" ? sel.OrderByDescending(v => MetaFor(v.Path)?.Rating ?? 0) : cmd.s == "last" ? sel.OrderByDescending(v => v.Last)
                        : cmd.s == "name-desc" ? sel.OrderByDescending(t, StringComparer.CurrentCultureIgnoreCase) : sel.OrderBy(t, StringComparer.CurrentCultureIgnoreCase);   // sortowanie jak w Kanapie
                }
                _pilotFilms = sel.ToList(); total = _pilotFilms.Count;
                for (int i = page * 100; i < Math.Min(total, page * 100 + 100); i++)
                {
                    var v = _pilotFilms[i]; var m = MetaFor(v.Path);
                    var sub = string.Join(" · ", new[] { m?.Year > 0 ? m.Year.ToString() : null, m?.Rating > 0 ? "★ " + m.Rating.ToString("0.0") : null,
                        v.Done ? "✓" : v.Len > v.Pos && v.Pos > 0 ? "▶ " + (int)((v.Len - v.Pos) / 60000) + " min" : null }.Where(x => x != null));
                    items.Add(new object[] { i, ShownTitle(v, m), sub, favs.Contains(v.Path) });
                }
            }
            if (_lanTx == null || _lanEncryptionKey == null || !_pilotClients.TryGetValue(phoneId, out var c)) return;
            var payload = new Dictionary<string, object> { ["to"] = phoneId, ["kind"] = cmd.v, ["page"] = page, ["total"] = total, ["groups"] = groups, ["items"] = items };
            var pkt = new LanStatePacket { t = "pilot-list", id = _lanId, device = _lanDeviceName, profile = SelectedProfileName, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), hash = "" };
            EncryptLanBlob(pkt, JsonSerializer.SerializeToUtf8Bytes(payload));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(pkt);
            _lanTx.Send(bytes, bytes.Length, c.Ep);
        }

        string PilotNowOnTv()
        {
            try
            {
                if (_playIndex < 0 || _playIndex >= _playlist.Count || !_iptvPlaying.TryGetValue(_playlist[_playIndex], out var ch) || _iptvEpg == null) return "";
                var p = EpgAt(EpgFor(_iptvEpg, ch), DateTime.Now);
                return p == null ? "" : p.Start.ToString("HH:mm") + "–" + p.Stop.ToString("HH:mm") + "  " + p.Title;
            }
            catch (Exception) { return ""; }
        }

        void SendPilotState(string phoneId)
        {
            if (_lanTx == null || _lanEncryptionKey == null || !_pilotClients.TryGetValue(phoneId, out var c)) return;
            object st = null;
            try { st = _vlcPilotState?.Invoke(); } catch (Exception) { }
            var payload = new Dictionary<string, object> { ["to"] = phoneId, ["st"] = st };
            var pkt = new LanStatePacket { t = "pilot-st", id = _lanId, device = _lanDeviceName, profile = SelectedProfileName, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), hash = "" };
            try
            {
                EncryptLanBlob(pkt, JsonSerializer.SerializeToUtf8Bytes(payload));
                var bytes = JsonSerializer.SerializeToUtf8Bytes(pkt);
                _lanTx.Send(bytes, bytes.Length, c.Ep);
            }
            catch (Exception) { }
        }
    }
}
