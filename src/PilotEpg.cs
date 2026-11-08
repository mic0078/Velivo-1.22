using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Velivo
{
    // 🎮 Pilot: 📅 Program TV (siatka), przełączenie o godzinie, nagrywanie, archiwum, ⏰ plany i 🎲 „Co dziś obejrzeć”.
    // Telefon tylko pokazuje i wysyła polecenia - robi to komputer tymi samymi funkcjami co okno TV i Kanapa (SwitchAt, RecordProgramme, PlayArchive, losowanie).
    public partial class MainWindow
    {
        List<IptvChannel> _pilotEpgCh = new List<IptvChannel>();

        void PilotSend(string phoneId, Dictionary<string, object> o)
        {
            if (_lanTx == null || _lanEncryptionKey == null || !_pilotClients.TryGetValue(phoneId, out var c)) return;
            o["to"] = phoneId;
            var pkt = new LanStatePacket { t = "pilot-list", id = _lanId, device = _lanDeviceName, profile = SelectedProfileName, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), hash = "" };
            EncryptLanBlob(pkt, JsonSerializer.SerializeToUtf8Bytes(o));
            var b = JsonSerializer.SerializeToUtf8Bytes(pkt);
            _lanTx.Send(b, b.Length, c.Ep);
        }

        static bool PilotCanArchive(IptvChannel ch, EpgItem p) =>
            ch.CatchupDays > 0 && !string.IsNullOrEmpty(ch.Catchup) && ch.Catchup != "disabled" && p.Start < DateTime.Now && p.Start >= DateTime.Now.AddDays(-ch.CatchupDays);

        IptvJob PilotJobFor(IptvChannel ch, EpgItem p) =>
            IptvSt.Jobs.FirstOrDefault(j => j.Url == ch.Url && !j.Film && LocalOf(j.Start) >= p.Start.AddMinutes(-Math.Max(1, IptvSt.RecBefore + 1)) && LocalOf(j.Start) < p.Stop);

        void PilotGuide(string phoneId, LanPilotCmd cmd)
        {
            if (cmd.op == "img") { PilotImg(phoneId, cmd.q ?? ""); return; }
            if (cmd.op == "epg")   // siatka: v = fav / tv, g = grupa, q = dzień (0 dziś, 1 jutro…), n = strona (po 12 kanałów - jeden pakiet UDP)
            {
                var all = KnownPlaylist()?.Channels ?? new List<IptvChannel>();
                if (_iptvEpg == null) { LoadIptvInBackground(() => { }); PilotSend(phoneId, new Dictionary<string, object> { ["kind"] = "epg", ["wait"] = true }); return; }   // program TV wczytuje się - telefon zapyta ponownie
                var groups = all.Select(c => c.Group ?? "").Where(g => g.Length > 0).Distinct().OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase).ToList();
                _pilotEpgCh = cmd.v == "fav"
                    ? IptvSt.Fav.Select(u => all.FirstOrDefault(c => c.Url == u) ?? (_iptvPlaying.TryGetValue(u, out var c1) ? c1 : null)).Where(c => c != null).ToList()
                    : all.Where(c => string.IsNullOrEmpty(cmd.g) || c.Group == cmd.g).ToList();
                _pilotEpgCh = _pilotEpgCh.Where(c => EpgFor(_iptvEpg, c)?.Count > 0).ToList();   // tylko kanały z programem
                if (cmd.s == "name") _pilotEpgCh = _pilotEpgCh.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                else if (cmd.s == "name-desc") _pilotEpgCh = _pilotEpgCh.OrderByDescending(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                int.TryParse(cmd.q ?? "0", out int day);
                var d0 = DateTime.Today.AddDays(day); var d1 = d0.AddDays(1);
                var from = day == 0 ? DateTime.Now.AddHours(-2) : d0;
                int page = Math.Max(0, (int)cmd.n), total = _pilotEpgCh.Count;
                var chans = new List<object[]>();
                for (int i = page * 12; i < Math.Min(total, page * 12 + 12); i++)
                {
                    var ch = _pilotEpgCh[i];
                    var progs = (EpgFor(_iptvEpg, ch) ?? new List<EpgItem>()).Where(p => p.Stop > from && p.Start < d1).Take(30).Select(p =>
                    {
                        var j = PilotJobFor(ch, p);
                        string flag = j == null ? "" : j.Kind == "rec" ? "rec" : "plan";
                        return new object[] { UnixMs(p.Start), UnixMs(p.Stop), p.Title ?? "", flag };
                    }).ToList();
                    chans.Add(new object[] { i, ch.Name ?? "", PilotLogoKey(ch), progs });
                }
                PilotSend(phoneId, new Dictionary<string, object> { ["kind"] = "epg", ["page"] = page, ["total"] = total, ["groups"] = groups, ["t0"] = UnixMs(from), ["t1"] = UnixMs(d1), ["now"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["chans"] = chans });
                return;
            }
            if (cmd.op == "epg-act")   // v = info / play / switch / rec / arch, n = kanał (z siatki), q = początek programu (ms)
            {
                int i = (int)cmd.n; long.TryParse(cmd.q ?? "", out long st);
                if (i < 0 || i >= _pilotEpgCh.Count || _iptvEpg == null) return;
                var ch = _pilotEpgCh[i];
                var p = (EpgFor(_iptvEpg, ch) ?? new List<EpgItem>()).FirstOrDefault(x => UnixMs(x.Start) == st);
                if (cmd.v == "play")
                {
                    foreach (var x in _pilotEpgCh) _iptvPlaying[x.Url] = x;
                    if (_kanapa != null) _kanapa.Close();
                    PlayVideos(_pilotEpgCh.Select(x => x.Url).ToArray(), i);
                    return;
                }
                if (p == null) return;
                if (cmd.v == "info")
                {
                    var job = PilotJobFor(ch, p);
                    PilotSend(phoneId, new Dictionary<string, object> { ["kind"] = "epg-info", ["ch"] = i, ["start"] = st, ["stop"] = UnixMs(p.Stop), ["title"] = p.Title ?? "", ["chName"] = ch.Name ?? "",
                        ["desc"] = string.Join(" · ", new[] { p.Sub, p.Year, p.Cats.Count > 0 ? string.Join(", ", p.Cats.Take(3)) : null }.Where(x => !string.IsNullOrWhiteSpace(x))) + (string.IsNullOrWhiteSpace(p.Desc) ? "" : "\n" + (p.Desc.Length > 800 ? p.Desc.Substring(0, 800) + "…" : p.Desc)),
                        ["arch"] = PilotCanArchive(ch, p), ["job"] = job?.Kind ?? "", ["jobId"] = job?.Id ?? "" });
                    return;
                }
                if (cmd.v == "switch") SwitchAt(ch, p);
                else if (cmd.v == "rec") RecordProgramme(ch, p);
                else if (cmd.v == "arch" && PilotCanArchive(ch, p)) { if (_kanapa != null) _kanapa.Close(); PlayArchive(ch, p); }
                PilotPlans(phoneId);
                return;
            }
            if (cmd.op == "plan-del")   // ✕ usuń zaplanowane przełączenie / nagrywanie (trwające nagrywanie też się kończy - jak w oknie TV)
            {
                var j = IptvSt.Jobs.FirstOrDefault(x => x.Id == cmd.q);
                if (j != null) { IptvSt.Jobs.Remove(j); SaveIptv(IptvSt); _iptvJobsChanged?.Invoke(); IptvTick(); }
            }
            if (cmd.op == "plans" || cmd.op == "plan-del") { PilotPlans(phoneId); return; }
            if (cmd.op == "rand")   // 🎲 g = gatunek, q = "1" tylko nieobejrzane, s = długość (krotki / sredni / dlugi)
            {
                List<LibraryVideo> pool;
                try { pool = LoadLibrary().Where(v => v.Peer == null && !string.IsNullOrEmpty(v.Path)).ToList(); } catch (Exception ex) { App.LogError(ex); pool = new List<LibraryVideo>(); }
                var genres = pool.SelectMany(v => MetaFor(v.Path)?.Genres ?? new List<string>()).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase).ToList();
                var rnd = new Random();
                var cand = pool.Where(v =>
                {
                    var m = MetaFor(v.Path);
                    if (cmd.q == "1" && (v.Done || SeenFraction(v) > 0.9)) return false;
                    if (!string.IsNullOrEmpty(cmd.g) && !(m?.Genres ?? new List<string>()).Contains(cmd.g, StringComparer.CurrentCultureIgnoreCase)) return false;
                    long min = v.Len / 60000;
                    if (cmd.s == "krotki" && (min <= 0 || min > 95)) return false;
                    if (cmd.s == "sredni" && (min <= 95 || min > 130)) return false;
                    if (cmd.s == "dlugi" && min <= 130) return false;
                    return true;
                }).GroupBy(v => { var m = MetaFor(v.Path); return !string.IsNullOrEmpty(m?.Id) ? "j:" + m.Id : "p:" + v.Path.ToLowerInvariant(); }).Select(g => g.First())
                  .OrderBy(_ => rnd.Next()).Take(3).ToList();
                _pilotFilms = cand;   // ▶ Oglądaj i ☆ działają tymi samymi poleceniami co lista filmów
                var favs = LoadPlaylists().Named.TryGetValue(PilotFavList, out var fl) ? new HashSet<string>(fl, StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
                var items = cand.Select((v, i) =>
                {
                    var m = MetaFor(v.Path);
                    var sub = string.Join(" · ", new[] { m?.Year > 0 ? m.Year.ToString() : null, m?.Rating > 0 ? "★ " + m.Rating.ToString("0.0") : null,
                        v.Len > 0 ? (v.Len / 3600000 > 0 ? v.Len / 3600000 + " h " : "") + (v.Len / 60000 % 60) + " min" : null, m?.Genres?.Count > 0 ? string.Join(", ", m.Genres.Take(2)) : null }.Where(x => x != null));
                    var ov = m?.Overview ?? ""; if (ov.Length > 400) ov = ov.Substring(0, 400) + "…";
                    return new object[] { i, ShownTitle(v, m), sub, favs.Contains(v.Path), PilotPosterKey(m) is var pk && pk.Length > 0 ? pk : PilotPoster(m), m?.Id ?? "", m?.Year ?? 0, ResumeAt(v.Pos, v.Len), ov };
                }).ToList();
                if (_jfConfCache == null) try { _jfConfCache = LoadJellyfinConf(); } catch (Exception) { }
                PilotSend(phoneId, new Dictionary<string, object> { ["kind"] = "rand", ["genres"] = genres, ["items"] = items, ["pool"] = pool.Count,
                    ["keys"] = JfServers(_jfConfCache).Select(c => new[] { PilotJfBase(c), c.Key }).ToList() });
            }
        }

        // 🖼 obrazki dla Pilota z dysku komputera (logo kanałów i plakaty są już pobrane) - telefon niczego nie pobiera z internetu
        internal static string PilotLogoKey(IptvChannel ch) => string.IsNullOrWhiteSpace(ch?.Logo) ? "" : "pc:L:" + ch.Logo;
        internal static string PilotPosterKey(MovieInfo m) => m?.Poster != null && System.IO.File.Exists(System.IO.Path.Combine(PosterDir, m.Poster)) ? "pc:P:" + m.Poster : "";
        static byte[] PilotJpeg(string file, int h)
        {
            try
            {
                var b = new BitmapImage();
                using (var fs = System.IO.File.OpenRead(file)) { b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.DecodePixelHeight = h; b.StreamSource = fs; b.EndInit(); }
                b.Freeze();
                var enc = new JpegBitmapEncoder { QualityLevel = 80 }; enc.Frames.Add(BitmapFrame.Create(b));
                using (var ms = new System.IO.MemoryStream()) { enc.Save(ms); return ms.ToArray(); }
            }
            catch (Exception) { return null; }
        }
        async void PilotImg(string phoneId, string key)
        {
            try
            {
                string file = null; int h = 96;
                if (key.StartsWith("L:"))
                {
                    var url = key.Substring(2);
                    if (!url.Contains("://")) file = url;
                    else { await GetLogo(url); file = LogoFile(url); }   // pobiera tylko, gdy komputer jeszcze nie ma
                }
                else if (key.StartsWith("P:")) { var n = System.IO.Path.GetFileName(key.Substring(2)); file = System.IO.Path.Combine(PosterDir, n); h = 300; }
                if (file == null || !System.IO.File.Exists(file)) { PilotSend(phoneId, new Dictionary<string, object> { ["kind"] = "img", ["key"] = "pc:" + key, ["b"] = "" }); return; }
                var jpg = await Task.Run(() => PilotJpeg(file, h));
                if (jpg != null && jpg.Length > 45000) jpg = await Task.Run(() => PilotJpeg(file, h * 2 / 3));   // jeden pakiet UDP
                PilotSend(phoneId, new Dictionary<string, object> { ["kind"] = "img", ["key"] = "pc:" + key, ["b"] = jpg == null ? "" : Convert.ToBase64String(jpg) });
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        // ⏰ plany: przełączenia i nagrania (te same co w oknie TV)
        void PilotPlans(string phoneId)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var items = IptvSt.Jobs.Where(j => j.Kind == "rec" || j.Kind == "switch").OrderBy(j => j.Start)
                .Select(j => new object[] { j.Id, j.Kind, j.Channel ?? "", j.Title ?? "", j.Start, j.Stop, j.Kind == "rec" && _recording.ContainsKey(j.Id) }).ToList();
            PilotSend(phoneId, new Dictionary<string, object> { ["kind"] = "plans", ["items"] = items, ["now"] = now });
        }
    }
}
