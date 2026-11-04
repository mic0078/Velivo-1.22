using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Velivo
{
    // 🌍 Komunikator poza domem: komputer zawsze wlaczony (np. NUCBOX) jest pośrednikiem - adres No-IP i wlasny port TCP
    // (przekierowany w routerze). Urzadzenie poza domem laczy sie z nim tym samym kluczem co przy dodaniu kontaktu
    // (HMAC + klucz sesji, ramki AES-GCM) i rozmawia tak jak w domu: pakiety p2p-* (wiadomosci, potwierdzenia, obecnosc)
    // i tunele na pliki. W domu (pośrednik widoczny w sieci domowej) - jak dotad, prosto w sieci domowej.
    // Pośrednik przyjmuje tylko swoje kontakty; tresc wiadomosci i plikow jest szyfrowana miedzy rozmowcami.
    public partial class MainWindow
    {
        // --- ustawienia pośrednika (w komunikator.dat) ---
        bool _komRelayOn; string _komRelayHost = ""; int _komRelayPort = 41920;
        TcpListener _komRelayListener; CancellationTokenSource _komRelayCts;

        // urzadzenia poza domem polaczone przez ten komputer (pośrednik)
        sealed class KomRemoteConn { public KomFrameStream Fs; public readonly SemaphoreSlim Lock = new SemaphoreSlim(1, 1); }
        readonly ConcurrentDictionary<string, KomRemoteConn> _komRemote = new ConcurrentDictionary<string, KomRemoteConn>();
        readonly ConcurrentDictionary<string, TaskCompletionSource<KomFrameStream>> _komTunnels = new ConcurrentDictionary<string, TaskCompletionSource<KomFrameStream>>();
        readonly ConcurrentDictionary<string, IPAddress> _komLanAddr = new ConcurrentDictionary<string, IPAddress>();   // ostatni adres w sieci domowej (kazde urzadzenie)
        readonly ConcurrentDictionary<string, DateTime> _komLanSeen = new ConcurrentDictionary<string, DateTime>();
        readonly ConcurrentDictionary<string, DateTime> _komSrvAt = new ConcurrentDictionary<string, DateTime>();   // kiedy kontakt ostatnio ogłosił „jestem serwerem” (brak wpisu po „nie jestem” = nie liczony)
        readonly ConcurrentDictionary<string, JsonNode> _komLanRelays = new ConcurrentDictionary<string, JsonNode>();   // ostatnie „jestem” innych pośredników w domu - dla telefonu, który łączy się z internetu
        readonly ConcurrentDictionary<string, IPAddress> _komViaRelay = new ConcurrentDictionary<string, IPAddress>();   // 🌍 urządzenie poza domem - przez którego pośrednika w domu (adres w sieci domowej; kilka pośredników bez konfliktów)
        IPAddress KomLanFresh(string id) => _komLanAddr.TryGetValue(id, out var a) && _komLanSeen.TryGetValue(id, out var s) && DateTime.UtcNow - s < TimeSpan.FromSeconds(100) ? a : null;   // adres w domu - tylko świeży (stary mógł dostać inny komputer)
        readonly ConcurrentDictionary<string, bool> _komViaNet = new ConcurrentDictionary<string, bool>();   // kontakt widziany przez internet (nie w domu)

        // ten komputer poza domem: polaczenie z pośrednikiem
        volatile KomRemoteConn _komUp; KomContact _komUpRelay; bool _komUpConnecting;
        System.Windows.Threading.DispatcherTimer _komUpTimer;

        // ---------- ramki szyfrowane ----------
        // [4 bajty dlugosci][nonce 12][dane][tag 16] - AES-GCM kluczem sesji; zapis buforowany, Flush wysyla ramke
        internal sealed class KomFrameStream : Stream
        {
            readonly TcpClient _cl; readonly NetworkStream _raw; readonly AesGcm _gcm;
            readonly MemoryStream _w = new MemoryStream(); byte[] _r = Array.Empty<byte>(); int _rp;
            readonly object _wl = new object();
            public KomFrameStream(TcpClient cl, byte[] key) { _cl = cl; cl.NoDelay = true; _raw = cl.GetStream(); _gcm = new AesGcm(key, 16); _raw.ReadTimeout = 150000; _raw.WriteTimeout = 60000; }
            public byte[] ReadFrame()
            {
                var lb = new byte[4]; if (!Fill(lb, 4)) throw new EndOfStreamException();
                int l = BitConverter.ToInt32(lb, 0); if (l < 28 || l > (2 << 20)) throw new InvalidDataException();
                var f = new byte[l]; if (!Fill(f, l)) throw new EndOfStreamException();
                var plain = new byte[l - 28];
                lock (_gcm) _gcm.Decrypt(f.AsSpan(0, 12), f.AsSpan(12, l - 28), f.AsSpan(l - 16, 16), plain);
                return plain;
            }
            public void WriteFrame(byte[] data, int off, int len)
            {
                // jedna ramka = jeden zapis (długość + treść razem) i bez Nagle'a - małe pakiety bez czekania do 200 ms
                var n = RandomNumberGenerator.GetBytes(12); var f = new byte[4 + len + 28];
                BitConverter.TryWriteBytes(f.AsSpan(0, 4), len + 28);
                lock (_gcm) _gcm.Encrypt(n, data.AsSpan(off, len), f.AsSpan(16, len), f.AsSpan(16 + len, 16));
                n.CopyTo(f, 4);
                lock (_wl) { _raw.Write(f, 0, f.Length); _raw.Flush(); }
            }
            bool Fill(byte[] b, int n) { int o = 0; while (o < n) { int r = _raw.Read(b, o, n - o); if (r <= 0) return false; o += r; } return true; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_rp >= _r.Length) { try { _r = ReadFrame(); _rp = 0; } catch (EndOfStreamException) { return 0; } if (_r.Length == 0) return 0; }
                int k = Math.Min(count, _r.Length - _rp); Buffer.BlockCopy(_r, _rp, buffer, offset, k); _rp += k; return k;
            }
            // duzy zapis - po kawalkach 1 MB (ramka nie wieksza niz 2 MB, ktore przyjmuje druga strona)
            public override void Write(byte[] buffer, int offset, int count)
            {
                while (count > 0) { int k = (int)Math.Min(count, (1 << 20) - _w.Length); _w.Write(buffer, offset, k); offset += k; count -= k; if (_w.Length >= (1 << 20)) Flush(); }
            }
            public override void Flush() { if (_w.Length == 0) return; var b = _w.ToArray(); _w.SetLength(0); WriteFrame(b, 0, b.Length); }
            public override Task FlushAsync(CancellationToken t) { Flush(); return Task.CompletedTask; }
            public override Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken t) => Task.Run(() => Read(b, o, c), t);
            public override Task WriteAsync(byte[] b, int o, int c, CancellationToken t) { Write(b, o, c); return Task.CompletedTask; }
            public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
            protected override void Dispose(bool d) { if (d) { try { Flush(); } catch (Exception) { } try { _cl.Dispose(); } catch (Exception) { } _gcm.Dispose(); } base.Dispose(d); }
        }

        internal static byte[] KomSessionKey(byte[] key, byte[] nc, byte[] ns) => SHA256.HashData(key.Concat(Encoding.ASCII.GetBytes("velivo-relay")).Concat(nc).Concat(ns).ToArray());

        // ---------- polaczenie z pośrednikiem (urzadzenie poza domem) ----------
        // mode: "main" (pakiety), "tunnel" (plik do To), "tunnel-in" (plik do nas, Tunnel = id)
        // 🌍 zapasowo port 443 (jak WhatsApp i strony WWW): sieci hoteli, firm i część operatorów w roamingu blokują nietypowe porty;
        // port, który ostatnio zadziałał, próbowany jako pierwszy
        const int KomRelayAltPort = 443;
        readonly ConcurrentDictionary<string, int> _komRelayGoodPort = new ConcurrentDictionary<string, int>();
        async Task<KomFrameStream> KomRelayConnect(KomContact relay, string mode, string tunnel, string to, CancellationToken token)
        {
            var hp = (relay.Relay ?? "").Split(':'); if (hp.Length != 2 || !int.TryParse(hp[1], out var port)) return null;
            var ports = _komRelayGoodPort.TryGetValue(relay.Id, out var good) && good == KomRelayAltPort ? new[] { KomRelayAltPort, port } : new[] { port, KomRelayAltPort };
            foreach (var pt in ports)
            {
                var fs = await KomRelayConnectPort(relay, hp[0], pt, pt == ports[0] ? 7 : 12, mode, tunnel, to, token);
                if (fs != null) { _komRelayGoodPort[relay.Id] = pt; return fs; }
                if (token.IsCancellationRequested) return null;
            }
            return null;
        }
        async Task<KomFrameStream> KomRelayConnectPort(KomContact relay, string host, int port, int timeoutSec, string mode, string tunnel, string to, CancellationToken token)
        {
            var cl = new TcpClient();
            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                    await cl.ConnectAsync(host, port, cts.Token);   // nazwa No-IP sprawdzana przy kazdym polaczeniu - zmiana IP domu nie przeszkadza
                    var s = cl.GetStream();
                    var nc = RandomNumberGenerator.GetBytes(16);
                    var hello = Encoding.UTF8.GetBytes(new JsonObject { ["Type"] = "relay-hello", ["Id"] = _komId, ["Relay"] = relay.Id, ["Nonce"] = Convert.ToBase64String(nc),
                        ["Ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["Mode"] = mode, ["Tunnel"] = tunnel ?? "", ["To"] = to ?? "" }.ToJsonString());
                    await KomWriteBlock(s, hello, cts.Token);
                    await KomWriteBlock(s, KomMacBytes(relay.Key, "relay-hello\n", hello), cts.Token);
                    var rep = await KomReadBlock(s, 4096, cts.Token); var mac = await KomReadBlock(s, 64, cts.Token);
                    if (rep == null || mac == null) { cl.Dispose(); return null; }
                    var exp = KomMacBytes(relay.Key, "relay-ok\n", hello.Concat(rep).ToArray());
                    if (mac.Length != exp.Length || !CryptographicOperations.FixedTimeEquals(mac, exp)) { cl.Dispose(); return null; }   // to nie nasz pośrednik
                    var ns = KomB64((string)JsonNode.Parse(rep)["Nonce"]); if (ns.Length != 16) { cl.Dispose(); return null; }
                    return new KomFrameStream(cl, KomSessionKey(relay.Key, nc, ns));
                }
            }
            catch (Exception) { cl.Dispose(); return null; }
        }

        internal static byte[] KomMacBytes(byte[] key, string label, byte[] data) { using (var h = new HMACSHA256(key)) return h.ComputeHash(Encoding.ASCII.GetBytes(label).Concat(data).ToArray()); }

        // strumien do urzadzenia: poza domem - tunel przez pośrednika; w domu - TCP w sieci domowej
        // tunel do urządzenia poza domem: prośba „relay-open”, urządzenie łączy się z powrotem (do 20 s)
        async Task<KomFrameStream> KomRelayOpenTo(string toId, CancellationToken token)
        {
            if (!_komRemote.TryGetValue(toId, out var rc)) return null;
            var tid = Guid.NewGuid().ToString("N");
            var w = new TaskCompletionSource<KomFrameStream>(TaskCreationOptions.RunContinuationsAsynchronously);
            _komTunnels[tid] = w;
            KomSendFrame(rc, new JsonObject { ["t"] = "relay-open", ["tid"] = tid });
            var done = await Task.WhenAny(w.Task, Task.Delay(20000, token));
            _komTunnels.TryRemove(tid, out _);
            return done == w.Task ? w.Task.Result : null;
        }

        // 📬 skrzynka na pliki: plik do kontaktu, którego nie ma - zaszyfrowany kluczem nadawcy i odbiorcy (pośrednik go nie odczyta),
        // na dysku pośrednika do odebrania (DataDir\skrzynka\<odbiorca>); oddawany, gdy odbiorca się pojawi (internet albo sieć domowa)
        static string KomBoxDir(string id) => Path.Combine(DataDir, "skrzynka", id);
        // 📬 dziennik skrzynki (skrzynka.log w folderze danych): co czeka, komu, próba, wynik - widać, co dzieje się z plikiem, bez pytania odbiorcy
        readonly ConcurrentDictionary<string, int> _komBoxSilent = new ConcurrentDictionary<string, int>();   // plik → ile razy telefon zamknął bez odpowiedzi
        readonly ConcurrentDictionary<string, DateTime> _komBoxWaitLog = new ConcurrentDictionary<string, DateTime>();   // „czeka” najwyżej co 30 min (ponowienie co minutę)
        void KomBoxLog(string id, string what)
        {
            try
            {
                var p = Path.Combine(DataDir, "skrzynka.log");
                if (File.Exists(p) && new FileInfo(p).Length > 2_000_000) File.Delete(p);
                var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [" + Environment.MachineName + "]  " + (KomContactById(id)?.Name ?? id) + "  " + what;
                File.AppendAllText(p, line + Environment.NewLine);
                if (_komRelayListener != null) KomBoxPeers(new JsonObject { ["t"] = "p2p-boxlog", ["id"] = _komId, ["line"] = line });   // wspólny dziennik moich serwerów w domu
            }
            catch (Exception) { }
        }
        // 🔬 DIAGNOSTYKA SYNCHRONIZACJI (serwery-diag.log): każdy etap osobno - PRZYJĘTA (od nadawcy), REPLIKA-WYSŁANA / REPLIKA-PRZYJĘTA (między serwerami),
        // DO-ODBIORCY (przekazana na połączenie odbiorcy), POTWIERDZONA (odbiorca potwierdził odbiór pliku / inny serwer zgłosił doręczenie - kopia usunięta),
        // UZUPEŁNIONO (po przerwie), PORÓWNANIE (odcisk zawartości skrzynek). Identyfikator wiadomości (mid) albo pliku (hid), serwer źródłowy i docelowy, SHA-256 pliku.
        string KomSrvTag(string id) => id == null ? "-" : id == _komId ? Environment.MachineName + "[" + id.Substring(0, 6) + "]" : (KomContactById(id)?.Name ?? "?") + "[" + id.Substring(0, Math.Min(6, id.Length)) + "]";
        void KomDiag(string stage, string kind, string objId, string src, string dst, string result, string sha = null)
        {
            try
            {
                var p = Path.Combine(DataDir, "serwery-diag.log");
                if (File.Exists(p) && new FileInfo(p).Length > 4_000_000) { var old = p + ".1"; try { File.Delete(old); } catch (Exception) { } File.Move(p, old); }
                File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " | " + Environment.MachineName + " | " + stage + " | " + kind + " | " + objId + " | z: " + src + " | do: " + dst + " | " + result + (sha != null ? " | sha256: " + sha : "") + Environment.NewLine);
            }
            catch (Exception) { }
        }
        static string KomSha(string file) { try { if (File.Exists(file + ".sha")) return File.ReadAllText(file + ".sha"); using var fs = File.OpenRead(file); var h = Convert.ToHexString(SHA256.HashData(fs)); return h; } catch (Exception) { return "?"; } }
        // SHA-256 pliku z zapisu obok; brak (plik sprzed tej wersji) - liczony raz w tle i zapisany (bez przycięć okna przy dużych filmach)
        static readonly ConcurrentDictionary<string, bool> _komShaBusy = new ConcurrentDictionary<string, bool>();
        static string KomShaCached(string bin)
        {
            try { if (File.Exists(bin + ".sha")) return File.ReadAllText(bin + ".sha"); } catch (Exception) { }
            if (_komShaBusy.TryAdd(bin, true)) _ = Task.Run(() => { try { var h = KomSha(bin); if (h != "?") File.WriteAllText(bin + ".sha", h); } finally { _komShaBusy.TryRemove(bin, out _); } });
            return "?";
        }
        // odcisk skrzynki: posortowana lista (odbiorca, mid) wiadomości i (odbiorca, hid, sha256 zawartości) plików - ten sam stan na dwóch serwerach = ten sam odcisk
        (int msgs, int files, string fp, JsonObject per) KomBoxFingerprint()
        {
            var lines = new List<string>(); var per = new JsonObject();
            foreach (var kv in _komStore.ToArray()) foreach (var (mid, sha) in KomStoreShas(kv.Key)) lines.Add("m|" + kv.Key + "|" + mid + "|" + sha);   // zawartość, nie tylko identyfikator
            try
            {
                var root = Path.Combine(DataDir, "skrzynka");
                if (Directory.Exists(root))
                    foreach (var d in Directory.GetDirectories(root))
                        foreach (var hdr in Directory.GetFiles(d, "*.hdr"))
                        {
                            var bin = Path.ChangeExtension(hdr, ".bin"); if (!File.Exists(bin)) continue;
                            lines.Add("f|" + Path.GetFileName(d) + "|" + Path.GetFileNameWithoutExtension(hdr).Split('-').Last() + "|" + KomShaCached(bin));
                        }
            }
            catch (Exception) { }
            lines.Sort(StringComparer.Ordinal);
            foreach (var g in lines.GroupBy(l => l.Split('|')[1])) per[g.Key] = g.Count(l => l[0] == 'm') + "/" + g.Count(l => l[0] == 'f') + "/" + KomCanon.ShaList(g).Substring(0, 11);
            return (lines.Count(l => l[0] == 'm'), lines.Count(l => l[0] == 'f'), KomCanon.ShaList(lines).Substring(0, 19), per);   // elementy z długością (bez kolizji sklejania), wersja v1 w skrócie
        }
        // wiadomości w skrzynce dla odbiorcy: mid -> SHA-256 kanonicznego zapisanego obiektu (zaszyfrowana treść + metadane)
        List<(string mid, string sha)> KomStoreShas(string to)
        {
            if (!_komStore.TryGetValue(to, out var l)) return new List<(string, string)>();
            lock (l) return l.Where(x => (string)x["mid"] != null).Select(x => ((string)x["mid"], KomCanon.Sha(x))).ToList();
        }
        // różnica w skrzynce u odbiorcy: prośba o listę (mid, skrót) do serwera, odpowiedź porównywana wiadomość po wiadomości
        void KomFpAsk(string peer, IEnumerable<string> tos, IPAddress addr) => KomSrvReply(new JsonObject { ["t"] = "p2p-fpreq", ["id"] = _komId, ["tos"] = new JsonArray(tos.Select(x => (JsonNode)x).ToArray()) }, addr);
        void KomFpIn(string from, JsonNode p, IPAddress addr)
        {
            if ((string)p["t"] == "p2p-fpreq" && p["tos"] is JsonArray tos)
            {
                foreach (var to in tos.Select(x => (string)x).Where(x => x is { Length: 32 }).Take(20))
                {
                    var files = new JsonObject();
                    try { var d = KomBoxDir(to); if (Directory.Exists(d)) foreach (var hdr in Directory.GetFiles(d, "*.hdr")) { var bin = Path.ChangeExtension(hdr, ".bin"); if (File.Exists(bin)) files[Path.GetFileNameWithoutExtension(hdr).Split('-').Last()] = KomShaCached(bin); } } catch (Exception) { }
                    var ms = new JsonObject(); foreach (var (mid, sha) in KomStoreShas(to).Take(400)) ms[mid] = sha;
                    KomSrvReply(new JsonObject { ["t"] = "p2p-fprep", ["id"] = _komId, ["to"] = to, ["m"] = ms, ["f"] = files }, addr);
                }
                return;
            }
            if ((string)p["t"] != "p2p-fprep" || (string)p["to"] is not { Length: 32 } rto) return;
            var mine = KomStoreShas(rto).ToDictionary(x => x.mid, x => x.sha);
            var theirs = (p["m"] as JsonObject)?.ToDictionary(kv => kv.Key, kv => (string)kv.Value) ?? new Dictionary<string, string>();
            var mf = new Dictionary<string, string>();
            try { var d = KomBoxDir(rto); if (Directory.Exists(d)) foreach (var hdr in Directory.GetFiles(d, "*.hdr")) { var bin = Path.ChangeExtension(hdr, ".bin"); if (File.Exists(bin)) mf[Path.GetFileNameWithoutExtension(hdr).Split('-').Last()] = KomShaCached(bin); } } catch (Exception) { }
            var tf = (p["f"] as JsonObject)?.ToDictionary(kv => kv.Key, kv => (string)kv.Value) ?? new Dictionary<string, string>();
            string me = KomSrvTag(_komId), them = KomSrvTag(from), who = KomSrvTag(rto); int n = 0;
            foreach (var (kind, a, b) in new[] { ("wiadomość", mine, theirs), ("plik", mf, tf) })
                foreach (var id in a.Keys.Union(b.Keys).OrderBy(x => x, StringComparer.Ordinal))
                {
                    a.TryGetValue(id, out var x); b.TryGetValue(id, out var y);
                    if (x == y) continue; n++;
                    KomDiag("RÓŻNICA", kind, id, me, them, "dla " + who + ": " + (x == null ? "brak na " + me + ", jest na " + them : y == null ? "jest na " + me + ", brak na " + them : "inna zawartość – " + me + " sha " + x + ", " + them + " sha " + y));
                }
            if (n == 0) KomDiag("RÓŻNICA", "skrzynka", "-", me, them, "dla " + who + ": przy sprawdzeniu pozycja po pozycji już zgodne (wiadomość była w drodze)");
        }
        readonly ConcurrentDictionary<string, (string fp, int same)> _komFpCmp = new ConcurrentDictionary<string, (string, int)>();
        // porównanie z innym serwerem (z jego stanu co minutę): wpis przy zmianie wyniku; różnica liczy się dopiero 3 razy z rzędu (wiadomości w drodze)
        void KomFpCompare(string peer, JsonObject si, IPAddress addr)
        {
            if ((int?)si["fpV"] != KomCanon.Version)
            {   // inna wersja formatu skrótu (starsza / nowsza wersja Velivo) - nie porównuję, żeby nie dać fałszywej niezgodności
                if (!_komFpCmp.TryGetValue(peer + "|v", out var vv) || vv.fp != "V") { _komFpCmp[peer + "|v"] = ("V", 0); KomDiag("PORÓWNANIE", "skrzynka", "-", KomSrvTag(_komId), KomSrvTag(peer), "nie porównuję – inna wersja formatu skrótu (tu v" + KomCanon.Version + ", tam " + ((int?)si["fpV"] is int tv ? "v" + tv : "brak – starsza wersja Velivo") + ")"); }
                return;
            }
            _komFpCmp.TryRemove(peer + "|v", out _);
            KomEvCompare(peer, si);
            var theirs = (string)si["fp"]; if (theirs == null) return;
            var (m, f, mine, per) = KomBoxFingerprint();
            bool eq = theirs == mine;
            var prev = _komFpCmp.TryGetValue(peer, out var pv) ? pv : (null, 0);
            int streak = eq ? 0 : (prev.fp == "R" ? prev.same : prev.same + 1);
            string state = eq ? "Z" : streak >= 3 ? "R" : "?";
            _komFpCmp[peer] = (state, eq ? 0 : streak);
            if (state == prev.fp || state == "?") return;
            if (eq) { KomDiag("PORÓWNANIE", "skrzynka", mine, KomSrvTag(_komId), KomSrvTag(peer), "SKRZYNKA ZGODNA – aktualna zawartość identyczna: " + m + " wiadomości i " + f + " plików czekających na odbiorców (skróty zapisanych obiektów)"); return; }
            var tp = si["fpPer"] as JsonObject; var diff = new List<string>(); var diffTo = new List<string>();
            foreach (var k in per.Select(x => x.Key).Union(tp?.Select(x => x.Key) ?? Enumerable.Empty<string>()).Distinct())
            { var a = (string)per[k] ?? "0/0/-"; var b = (string)tp?[k] ?? "0/0/-"; if (a != b) diffTo.Add(k); if (a != b) diff.Add((KomContactById(k)?.Name ?? k.Substring(0, 6)) + " tu " + a + ", tam " + b); }
            if (diffTo.Count > 0) KomFpAsk(peer, diffTo, addr);
            KomDiag("PORÓWNANIE", "skrzynka", mine + " ≠ " + theirs, KomSrvTag(_komId), KomSrvTag(peer), "SKRZYNKA RÓŻNA od 3 min – tu " + m + " wiad. / " + f + " plików, tam " + (int?)si["fpM"] + " / " + (int?)si["fpF"] + "; różnice (wiad./pliki/odcisk): " + string.Join("; ", diff));
        }
        // 🗄 historia zdarzeń (dziennik przyjęć i doręczeń) - osobno od skrzynki: skrzynka = co teraz czeka, historia = wszystko, co się wydarzyło
        void KomEvCompare(string peer, JsonObject si)
        {
            var theirs = (string)si["evD"]; if (theirs == null) return;
            var mine = KomEv.Digest(); bool eq = mine == theirs;
            var key = peer + "|ev"; var prev = _komFpCmp.TryGetValue(key, out var pv) ? pv : (null, 0);
            int streak = eq ? 0 : (prev.fp == "R" ? prev.same : prev.same + 1);
            string state = eq ? "Z" : streak >= 3 ? "R" : "?";
            _komFpCmp[key] = (state, eq ? 0 : streak);
            if (state == prev.fp || state == "?") return;
            if (eq) { KomDiag("PORÓWNANIE", "historia zdarzeń", mine, KomSrvTag(_komId), KomSrvTag(peer), "HISTORIA ZGODNA – identyczny dziennik zdarzeń (" + KomEv.Count + " zdarzeń przyjęć i doręczeń, w okresie przechowywania)"); return; }
            var th = si["evHave"] as JsonObject; var me = KomEv.Have(); var d = new List<string>();
            foreach (var o in me.Select(x => x.Key).Union(th?.Select(x => x.Key) ?? Enumerable.Empty<string>()).Distinct())
            { long a = (long?)me[o] ?? 0, b = (long?)th?[o] ?? 0; if (a != b) d.Add("od " + KomSrvTag(o) + ": tu do nr " + a + ", tam do nr " + b); }
            KomDiag("PORÓWNANIE", "historia zdarzeń", mine + " ≠ " + theirs, KomSrvTag(_komId), KomSrvTag(peer), "HISTORIA RÓŻNA od 3 min – tu " + KomEv.Count + " zdarzeń, tam " + (int?)si["evN"] + (d.Count > 0 ? "; braki: " + string.Join("; ", d) : "; numery te same, inna treść albo różne porządki wg „Przechowuj”"));
        }
        // ♻ uzupełnianie po starcie: liczniki replik przyjętych od innych serwerów w pierwszych 15 min
        readonly DateTime _komStartAt = DateTime.UtcNow; int _komFillMsg, _komFillFile, _komFillEv; bool _komFillLogged;
        void KomFillCount(bool file) { if (DateTime.UtcNow - _komStartAt < TimeSpan.FromMinutes(15)) { if (file) Interlocked.Increment(ref _komFillFile); else Interlocked.Increment(ref _komFillMsg); } }
        void KomFillReport()
        {
            if (_komFillLogged || DateTime.UtcNow - _komStartAt < TimeSpan.FromMinutes(15)) return;
            _komFillLogged = true;
            KomDiag("UZUPEŁNIONO", "po starcie", "-", "moje serwery", KomSrvTag(_komId), "w 15 min po uruchomieniu przyjęto od innych serwerów: " + _komFillMsg + " wiadomości, " + _komFillFile + " plików, " + _komFillEv + " zdarzeń dziennika");
        }
        async Task KomBoxStore(JsonNode h, byte[] hb, Stream s, string fromPeer, CancellationToken token)
        {
            var to = (string)h["To"] ?? ""; long size = (long?)h["Size"] ?? -1;
            if (to.Length != 32 || size < 0 || size > LanFile2Limit) return;
            if (!await Dispatcher.InvokeAsync(() => KomContactById(to) != null && ((string)h["Id"] == _komId || KomContactById((string)h["Id"] ?? "") != null))) return;   // tylko między kontaktami tego komputera (albo plik w moim imieniu od mojego innego serwera)
            var dir = KomBoxDir(to); Directory.CreateDirectory(dir);
            var hid = Convert.ToHexString(SHA256.HashData(hb), 0, 16);   // ten sam plik u każdego pośrednika ma tę samą nazwę - kopia kasowana po doręczeniu
            if (fromPeer != null && (_komBoxGone.ContainsKey(hid) || Directory.GetFiles(dir, "*-" + hid + ".hdr").Length > 0)) { KomDiag("REPLIKA-PRZYJĘTA", "plik", hid, KomSrvTag(fromPeer), KomSrvTag(_komId), _komBoxGone.ContainsKey(hid) ? "pominięta – już doręczony" : "pominięta – już jest (bez duplikatu)"); await s.WriteAsync(new byte[] { 0 }, 0, 1, token); return; }   // już doręczony albo już jest
            var f = Path.Combine(dir, (fromPeer != null ? "k" : "") + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + hid);   // k - kopia od innego pośrednika
            await s.WriteAsync(new byte[] { 1 }, 0, 1, token); await s.FlushAsync(token);
            try
            {
                using (var fo = new FileStream(f + ".part", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    int n = ChunkCount(size); var lb = new byte[4]; var buf = new byte[LanChunk + 16];
                    for (int i = 0; i < n; i++)
                    {
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token); idle.CancelAfter(TimeSpan.FromSeconds(60));
                        if (!await ReadExactAsync(s, lb, 4, idle.Token)) return;
                        int l = BitConverter.ToInt32(lb, 0); if (l < 16 || l > LanChunk + 16) return;
                        if (!await ReadExactAsync(s, buf, l, idle.Token)) return;
                        await fo.WriteAsync(lb, 0, 4, token); await fo.WriteAsync(buf, 0, l, token);
                    }
                }
                File.Move(f + ".part", f + ".bin"); if (fromPeer != null) File.WriteAllText(f + ".src", fromPeer);
                var fsha = KomSha(f + ".bin"); File.WriteAllText(f + ".bin.sha", fsha); File.WriteAllBytes(f + ".hdr", hb);   // .src - który serwer ma oryginał; .sha - SHA-256 zawartości (porównanie między serwerami)
                KomDiag(fromPeer == null ? "PRZYJĘTY" : "REPLIKA-PRZYJĘTA", "plik", hid, fromPeer == null ? KomSrvTag((string)h["Id"]) + " (nadawca)" : KomSrvTag(fromPeer) + " (serwer)", KomSrvTag(_komId) + " → dla " + KomSrvTag(to), "zapisany, " + size + " B", fsha);
                if (fromPeer != null) KomFillCount(true);
                var fromName = await Dispatcher.InvokeAsync(() => (string)h["Id"] == _komId ? Environment.MachineName + " (w imieniu tego serwera)" : KomContactById((string)h["Id"] ?? "")?.Name ?? "?"); var peerName = fromPeer == null ? null : await Dispatcher.InvokeAsync(() => KomContactById(fromPeer)?.Name ?? fromPeer);
                KomBoxLog(to, "przyjęty – plik od " + fromName + " (" + KomSize(size) + ") czeka na serwerze, aż odbiorca włączy telefon lub internet" + (peerName != null ? " (zapasowa kopia, oryginał na serwerze " + peerName + ")" : ""));
                await s.WriteAsync(new byte[32], 0, 32, token); await s.FlushAsync(token);   // przechowane
            }
            finally { try { File.Delete(f + ".part"); } catch (Exception) { } }
            _ = Task.Run(async () => { await KomBoxFlush(to); if (fromPeer == null && File.Exists(f + ".hdr")) await KomBoxCopy(f); });   // nie doręczony od razu - kopia u innych pośredników w domu
        }
        // 📬 „doręczone” powtarzane co minutę przez 2 dni - serwer, który był wyłączony, po powrocie skasuje swoją kopię zamiast wysłać drugi raz
        readonly ConcurrentDictionary<string, (string To, DateTime At)> _komBoxDoneOut = new ConcurrentDictionary<string, (string, DateTime)>();
        void KomBoxDoneRepeat()
        {
            foreach (var kv in _komBoxDoneOut) if (DateTime.UtcNow - kv.Value.At > TimeSpan.FromDays(2)) _komBoxDoneOut.TryRemove(kv.Key, out _);
            foreach (var g in _komBoxDoneOut.GroupBy(kv => kv.Value.To))
            {   // pliki i wiadomości (klucz „m:” + mid) - serwer wyłączony w chwili doręczenia dowie się po powrocie i skasuje swoje kopie (wcześniej wiadomości tylko raz - kopie zostawały)
                foreach (var part in g.Where(kv => !kv.Key.StartsWith("m:")).Select(kv => kv.Key).Chunk(200))   // paczki po 200 (pakiet UDP ~7 KB)
                    KomBoxPeers(new JsonObject { ["t"] = "p2p-boxdone", ["id"] = _komId, ["to"] = g.Key, ["files"] = new JsonArray(part.Select(h => (JsonNode)h).ToArray()), ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                foreach (var part in g.Where(kv => kv.Key.StartsWith("m:")).Select(kv => kv.Key.Substring(2)).Chunk(150))
                    KomBoxPeers(new JsonObject { ["t"] = "p2p-boxdone", ["id"] = _komId, ["to"] = g.Key, ["mids"] = new JsonArray(part.Select(h => (JsonNode)h).ToArray()), ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            }
        }
        // 📬 kopia pliku ze skrzynki u innych pośredników w domu (NUCBOX, CHUWI) - gdy ten padnie, inny go odda
        readonly ConcurrentDictionary<string, DateTime> _komBoxGone = new ConcurrentDictionary<string, DateTime>();   // doręczone przez innego pośrednika - spóźniona kopia odrzucana
        async Task KomBoxCopy(string f)
        {
            if (KomSyncOff) return;   // ⏸ synchronizacja wstrzymana przez administratora - kopie po wznowieniu (KomBoxFlush / ponowienie)
            foreach (var a in KomPeerAddrs())
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromHours(2));
                    using var cl = await KomTcpConnect(a, cts.Token); var st = cl.GetStream();
                    await KomWriteBlock(st, File.ReadAllBytes(f + ".hdr"), cts.Token);
                    var b = new byte[1]; if (!await ReadExactAsync(st, b, 1, cts.Token) || b[0] != 1) { KomDiag("REPLIKA-WYSŁANA", "plik", Path.GetFileName(f).Split('-').Last(), KomSrvTag(_komId), KomSrvTag(KomPeerAt(a)), "serwer już ma albo doręczył – nie wysłano"); continue; }
                    using (var fi = File.OpenRead(f + ".bin")) await fi.CopyToAsync(st, 1 << 16, cts.Token);
                    await st.FlushAsync(cts.Token); var okc = await ReadExactAsync(st, new byte[32], 32, cts.Token);
                    KomDiag("REPLIKA-WYSŁANA", "plik", Path.GetFileName(f).Split('-').Last(), KomSrvTag(_komId), KomSrvTag(KomPeerAt(a)) + " (sieć domowa)", okc ? "serwer potwierdził zapis" : "brak potwierdzenia zapisu", KomSha(f + ".bin"));
                }
                catch (Exception ex) { KomDiag("REPLIKA-WYSŁANA", "plik", Path.GetFileName(f).Split('-').Last(), KomSrvTag(_komId), KomSrvTag(KomPeerAt(a)), "błąd: " + ex.Message); }
            // 🔗 moje serwery poza tą siecią domową - ta sama kopia tunelem przez internet (do samego serwera)
            if (_komRelayListener != null)
                foreach (var peer in KomMyServers().Where(c => !KomLanPeer(c.Id)))
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromHours(2));
                        using var fs = await KomRelayConnect(peer, "tunnel", null, peer.Id, cts.Token); if (fs == null) continue;
                        await KomWriteBlock(fs, File.ReadAllBytes(f + ".hdr"), cts.Token);
                        var b = new byte[1]; if (!await ReadExactAsync(fs, b, 1, cts.Token) || b[0] != 1) { KomDiag("REPLIKA-WYSŁANA", "plik", Path.GetFileName(f).Split('-').Last(), KomSrvTag(_komId), KomSrvTag(peer.Id), "serwer już ma albo doręczył – nie wysłano"); continue; }
                        using (var fi = File.OpenRead(f + ".bin")) await fi.CopyToAsync(fs, 1 << 16, cts.Token);
                        await fs.FlushAsync(cts.Token); var okc = await ReadExactAsync(fs, new byte[32], 32, cts.Token);
                        KomDiag("REPLIKA-WYSŁANA", "plik", Path.GetFileName(f).Split('-').Last(), KomSrvTag(_komId), KomSrvTag(peer.Id) + " (internet)", okc ? "serwer potwierdził zapis" : "brak potwierdzenia zapisu", KomSha(f + ".bin"));
                    }
                    catch (Exception ex) { KomDiag("REPLIKA-WYSŁANA", "plik", Path.GetFileName(f).Split('-').Last(), KomSrvTag(_komId), KomSrvTag(peer.Id), "błąd: " + ex.Message); }
        }
        static readonly AsyncLocal<string> _komTunnelSrv = new AsyncLocal<string>();   // tunel otworzył mój serwer (kopia skrzynki przez internet)
        readonly ConcurrentDictionary<string, bool> _komBoxBusy = new ConcurrentDictionary<string, bool>();
        async Task KomBoxFlush(string id)
        {
            var dir = KomBoxDir(id);
            if (!Directory.Exists(dir) || !_komBoxBusy.TryAdd(id, true)) return;
            var gone = new JsonArray();
            try
            {
                // najmniejsze najpierw - duży plik (np. 90 MB przez dane komórkowe) nie blokuje małych
                foreach (var hdr in Directory.GetFiles(dir, "*.hdr").OrderBy(x => { try { return new FileInfo(Path.ChangeExtension(x, ".bin")).Length; } catch (Exception) { return 0L; } }).ThenBy(x => x))
                {
                    // kopia - oddaje serwer z oryginałem, jeśli działa i odbiorca jest z nim połączony albo w domu (bez podwójnej wysyłki 77 MB);
                    // kopia idzie, gdy oryginał nie działa albo odbiorca jest połączony tylko ze mną
                    if (Path.GetFileName(hdr).StartsWith('k') && File.Exists(Path.ChangeExtension(hdr, ".src")) && File.ReadAllText(Path.ChangeExtension(hdr, ".src")) is var src
                        && _komLanSeen.TryGetValue(src, out var srcSeen) && DateTime.UtcNow - srcSeen < TimeSpan.FromSeconds(100)
                        && ((_komViaRelay.TryGetValue(id, out var vr) && _komLanAddr.TryGetValue(src, out var sa) && vr.Equals(sa)) || (_komLanSeen.TryGetValue(id, out var rs) && DateTime.UtcNow - rs < TimeSpan.FromSeconds(100)))) continue;
                    var bin = Path.ChangeExtension(hdr, ".bin");
                    if (!File.Exists(bin)) { File.Delete(hdr); try { File.Delete(Path.ChangeExtension(hdr, ".src")); } catch (Exception) { } continue; }
                    using var cts = new CancellationTokenSource(TimeSpan.FromHours(2));
                    Stream st = null; TcpClient cl = null;
                    try
                    {
                        if (_komRemote.ContainsKey(id)) st = await KomRelayOpenTo(id, cts.Token);
                        else if (_komLanAddr.TryGetValue(id, out var a) && _komLanSeen.TryGetValue(id, out var seen) && DateTime.UtcNow - seen < TimeSpan.FromSeconds(100))
                        { cl = await KomTcpConnect(a, cts.Token); st = cl.GetStream(); }
                        if (st == null) { if (!_komBoxWaitLog.TryGetValue(id, out var wl) || DateTime.UtcNow - wl > TimeSpan.FromMinutes(30)) { _komBoxWaitLog[id] = DateTime.UtcNow; KomBoxLog(id, "czeka – telefonu odbiorcy nie ma teraz w sieci (brak zasięgu, internetu albo jest zgaszony); plików do oddania: " + Directory.GetFiles(dir, "*.hdr").Length + ", serwer spróbuje sam co minutę"); } return; }   // odbiorcy nie ma - zostaje w skrzynce
                        var size = new FileInfo(bin).Length; var t0 = DateTime.UtcNow;
                        string sender = "?"; try { var sid = (string)JsonNode.Parse(File.ReadAllBytes(hdr))?["Id"]; sender = KomContactById(sid ?? "")?.Name ?? sid ?? "?"; } catch (Exception) { }
                        if (!_komBoxSilent.ContainsKey(hdr)) KomBoxLog(id, "wysyłam – odbiorca jest dostępny, przekazuję plik od " + sender + " (" + KomSize(size) + ")");   // ponowienia odrzucanego pliku - bez wpisu co minutę
                        await KomWriteBlock(st, File.ReadAllBytes(hdr), cts.Token);
                        var b = new byte[1]; if (!await ReadExactAsync(st, b, 1, cts.Token))
                        {   // telefon zamknął bez odpowiedzi (nie zna nadawcy / stara wersja): wpis raz, po 10 próbach plik usuwany - nie zaśmieca skrzynki i dziennika w nieskończoność
                            int n = _komBoxSilent.AddOrUpdate(hdr, 1, (_, v) => v + 1);
                            if (n == 1) KomBoxLog(id, "nie przyjmuje – telefon odbiorcy nie przyjął pliku od " + sender + " (" + KomSize(size) + "). Zwykle: nie ma nadawcy w kontaktach albo ma starą wersję Velivo. Serwer spróbuje jeszcze kilka razy");
                            if (n >= 10) { foreach (var ext in new[] { ".hdr", ".bin", ".src" }) try { File.Delete(Path.ChangeExtension(hdr, ext)); } catch (Exception) { } _komBoxSilent.TryRemove(hdr, out _); KomBoxLog(id, "usunięty – po 10 próbach plik od " + sender + " (" + KomSize(size) + ") nie został przyjęty przez telefon odbiorcy. Wyślij go jeszcze raz po aktualizacji Velivo u odbiorcy"); }
                            continue;
                        }
                        if (b[0] == 1)
                        {
                            using (var fi = File.OpenRead(bin)) await fi.CopyToAsync(st, 1 << 16, cts.Token);
                            await st.FlushAsync(cts.Token);
                            if (!await ReadExactAsync(st, new byte[32], 32, cts.Token)) { KomBoxLog(id, "przerwany – połączenie z odbiorcą zerwało się po " + (int)(DateTime.UtcNow - t0).TotalSeconds + " s (słaby zasięg?); serwer spróbuje znowu za minutę"); continue; }   // ten plik przerwany - następne próbują dalej (ponowienie za minutę)
                        }
                        KomBoxLog(id, b[0] == 1 ? "doręczony – odbiorca dostał plik od " + sender + " (" + KomSize(size) + ", " + (int)(DateTime.UtcNow - t0).TotalSeconds + " s)"
                            : b[0] == 2 ? "już doręczony – telefon odbiorcy ma ten plik od " + sender + " (doszedł przez inny serwer); kopia z tego serwera skasowana"
                            : b[0] == 3 ? "odmówił – telefon odbiorcy nie ma nadawcy " + sender + " w kontaktach; plik skasowany z serwera"
                            : "odmówił – telefon odbiorcy nie chciał pliku od " + sender + " (ma go już, nie zna nadawcy albo brak miejsca w telefonie); plik usunięty z serwera");
                        KomDiag(b[0] == 1 ? "POTWIERDZONA" : "DO-ODBIORCY", "plik", Path.GetFileNameWithoutExtension(hdr).Split('-').Last(), KomSrvTag(_komId), KomSrvTag(id), b[0] == 1 ? "odbiorca potwierdził odbiór całego pliku" : b[0] == 2 ? "odbiorca ma już ten plik (inny serwer)" : b[0] == 3 ? "odmowa – nie zna nadawcy" : "odmowa", KomSha(bin));
                        File.Delete(bin); File.Delete(hdr); try { File.Delete(Path.ChangeExtension(hdr, ".src")); } catch (Exception) { } try { File.Delete(bin + ".sha"); } catch (Exception) { }   // odebrany (albo odrzucony przez odbiorcę)
                        var gh = Path.GetFileNameWithoutExtension(hdr).Split('-').Last(); gone.Add(gh); _komBoxDoneOut[gh] = (id, DateTime.UtcNow);
                    }
                    catch (Exception ex) { KomBoxLog(id, "błąd – nie udało się przekazać pliku (" + ex.Message + "); serwer spróbuje znowu za minutę"); if (st == null) return; }   // zerwany przy jednym pliku - pozostałe dalej
                    finally { st?.Dispose(); cl?.Dispose(); }
                }
                if (Directory.GetFiles(dir).Length == 0) Directory.Delete(dir);
            }
            finally { _komBoxBusy.TryRemove(id, out _); if (gone.Count > 0) KomBoxPeers(new JsonObject { ["t"] = "p2p-boxdone", ["id"] = _komId, ["to"] = id, ["files"] = gone, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }); }
        }
        // nadawca: odbiorcy nie ma - plik do skrzynki pośrednika (sam pośrednik: do własnej)
        async Task<string> KomSendViaBox(KomContact to, string path, string mid, KomContact group = null)
        {
            IPAddress addr = IPAddress.Loopback;
            if (_komUp == null)
            {   // w domu: pośrednik w sieci domowej - najpierw ten, z którym odbiorca jest połączony (kilka pośredników: nie do skrzynki, do której odbiorca nie zagląda)
                var via = _komViaRelay.TryGetValue(to.Id, out var v) ? v.ToString() : "";
                var rc = _komContacts.Where(c => !string.IsNullOrEmpty(c.Relay) && _komLanSeen.TryGetValue(c.Id, out var seen) && DateTime.UtcNow - seen < TimeSpan.FromSeconds(100))
                    .OrderByDescending(c => c.Addr == via).FirstOrDefault(c => _komRelayListener == null || c.Addr == via);   // sam pośrednik: własna skrzynka, chyba że odbiorca jest u innego
                if (rc != null && IPAddress.TryParse(rc.Addr ?? "", out var ra)) addr = ra;
                else if (_komRelayListener == null) return "brak";
            }
            return await KomSendFile(addr, path, _ => { }, to, null, mid, box: true, group: group);
        }

        async Task<Stream> KomOpenAsync(TcpClient cl, IPAddress addr, string toId, CancellationToken token)
        {
            var up = _komUp; var relay = _komUpRelay;
            if (up != null && relay != null && toId != null)
            {
                var fs = await KomRelayConnect(relay, "tunnel", null, toId, token);
                if (fs != null) return fs;
            }
            // jesteśmy pośrednikiem, a odbiorca jest poza domem - przez własny odbiornik, który przekazuje tunelem
            if (toId != null && _komRemote.ContainsKey(toId)) { await cl.ConnectAsync(IPAddress.Loopback, KomPort, token); return cl.GetStream(); }
            return (await KomTcpConnect(addr, token)).GetStream();   // strumień zamyka też połączenie
        }

        // pakiet p2p do urzadzenia poza domem (pośrednik) albo przez pośrednika (my poza domem); true - wyslany tunelem
        bool KomRouteRemote(JsonObject p, bool broadcast)
        {
            var up = _komUp;
            if (up != null) { KomSendFrame(up, p); return false; }   // poza domem - przez pośrednika, a sieć domowa i tak dalej (gdy „poza domem” to pomyłka, w domu nic nie znika)
            if (_komRemote.IsEmpty) return false;
            var to = (string)p["to"];
            if (to != null && _komRemote.TryGetValue(to, out var rc)) { KomSendFrame(rc, p); return true; }
            if (broadcast) foreach (var c in _komRemote.Values) KomSendFrame(c, p);   // obecnosc pośrednika takze dla urzadzen poza domem
            return false;
        }

        void KomSendFrame(KomRemoteConn c, JsonNode p)
        {
            var d = Encoding.UTF8.GetBytes(p.ToJsonString());
            _ = Task.Run(async () =>
            {
                if (!await c.Lock.WaitAsync(10000)) return;
                try { c.Fs.WriteFrame(d, 0, d.Length); } catch (Exception) { } finally { c.Lock.Release(); }
            });
        }

        // pakiet z sieci domowej (watek sieci): adres urzadzenia; pośrednik - przekazanie urzadzeniom poza domem
        void KomRelayFromLan(JsonNode p, IPAddress addr)
        {
            var from = (string)p["id"]; var t = (string)p["t"];
            if (from == null || from.Length != 32 || (_komNetPeer == null && IPAddress.IsLoopback(addr))) return;
            if (KomEvIn(p, t, from, addr)) return;   // 🗄 dziennik zdarzeń moich serwerów
            if (t == "p2p-admcmd" || t == "p2p-admack") { if (from != _komId && KomFromRelay(addr)) KomAdmIn(p); return; }   // 🛠 polecenie administratora (podpis sprawdza KomAdmIn)
            if (_komRemote.ContainsKey(from)) return;   // echo pakietu urzadzenia poza domem, ktory sami przekazalismy - nie jest w domu
            if ((bool?)p["fwd"] == true)
            {   // przekazany przez pośrednika - kontakt jest poza domem (🌍), połączony z pośrednikiem pod tym adresem
                _komViaNet[from] = true;
                if ((bool?)p["box"] == true && t == "p2p-msg" && (string)p["to"] is string bto && KomFromRelay(addr)) { var cp = p.DeepClone().AsObject(); cp.Remove("fwd"); cp.Remove("box"); var rsrc = KomSrvTag(_komNetPeer ?? KomPeerAt(addr)); if (_komRemote.TryGetValue(bto, out var brc)) { KomDiag("REPLIKA-PRZYJĘTA", "wiadomość", (string)cp["mid"] ?? "-", rsrc, KomSrvTag(_komId), "odbiorca " + KomSrvTag(bto) + " połączony tutaj – przekazana od razu"); KomSendFrame(brc, cp); if ((string)cp["mid"] is string bm) { _komBoxDoneOut["m:" + bm] = (bto, DateTime.UtcNow); KomEvDone(bto, new[] { bm }); KomBoxPeers(new JsonObject { ["t"] = "p2p-boxdone", ["id"] = _komId, ["to"] = bto, ["mids"] = new JsonArray((JsonNode)bm), ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }); } } else { KomDiag("REPLIKA-PRZYJĘTA", "wiadomość", (string)cp["mid"] ?? "-", rsrc, KomSrvTag(_komId) + " → dla " + KomSrvTag(bto), KomStoreKeep(cp, bto) ? "zapisana w skrzynce" : "nie zapisana (odbiorca w domu albo już doręczona)"); KomFillCount(false); } }   // 📬 kopia od innego pośrednika w domu - gdy tamten padnie, ten doręczy
                if (_komNetPeer != null) { } else if (t == "p2p-bye") _komViaRelay.TryRemove(new KeyValuePair<string, IPAddress>(from, addr)); else _komViaRelay[from] = addr;
                return;
            }
            if (_komNetPeer == null) { _komLanAddr[from] = addr; _komLanSeen[from] = DateTime.UtcNow; _komViaNet.TryRemove(from, out _); }
            else if (t != "p2p-fpreq" && t != "p2p-fprep" && t != "p2p-boxlog" && t != "p2p-srvinfo" && t != "p2p-boxlogclr" && t != "p2p-boxdays" && t != "p2p-boxdone") return;   // łączem serwerów tylko dane serwerów
            if (t == "p2p-boxlog" && KomFromRelay(addr) && (string)p["line"] is string bl && bl.Length < 1000)
            {   // 📬 wpis dziennika z innego mojego serwera - do wspólnego widoku w Centrum serwera
                try { var sp = Path.Combine(DataDir, "skrzynka-serwery.log"); if (File.Exists(sp) && new FileInfo(sp).Length > 4_000_000) File.Delete(sp); File.AppendAllText(sp, bl.Replace("\r", " ").Replace("\n", " ") + Environment.NewLine); } catch (Exception) { }
                return;
            }
            if (t == "p2p-srvinfo" && KomFromRelay(addr) && p is JsonObject si) { _komSrvInfo[from] = ((JsonObject)si.DeepClone(), DateTime.UtcNow); try { KomFpCompare(from, si, addr); } catch (Exception) { } if ((string)si["lan"] is string pl && pl.Length > 0) _komPeerPorts[from] = (pl, (int?)si["port"] ?? 0, ((string)si["ports"] ?? "").Split(',').Select(x => int.TryParse(x, out var n) ? n : 0).Where(n => n > 0).ToArray()); return; }   // 📊 stan innego mojego serwera
            if ((t == "p2p-fpreq" || t == "p2p-fprep") && KomFromRelay(addr)) { try { KomFpIn(from, p, addr); } catch (Exception ex) { App.LogError(ex); } return; }   // 🔬 porównanie pozycja po pozycji
            if (t == "p2p-boxlogclr" && KomFromRelay(addr))
            {   // 🧹 dziennik skrzynki wyczyszczony na innym moim serwerze - tutaj też
                foreach (var lp in new[] { "skrzynka.log", "skrzynka-serwery.log" }) try { File.Delete(Path.Combine(DataDir, lp)); } catch (Exception) { }
                return;
            }
            if (t == "p2p-boxdays" && KomFromRelay(addr) && (int?)p["days"] is int bd && bd > 0 && ((long?)p["at"] ?? 0) > ((long?)_komOpt["boxDaysAt"] ?? 0))
            { _komOpt["boxDays"] = bd; _komOpt["boxDaysAt"] = (long)p["at"]; KomSave(); return; }   // 📬 przechowywanie ustawione na innym moim serwerze - nowsze wygrywa
            if (t == "p2p-boxdone" && (string)p["to"] is string dto && KomFromRelay(addr))
            {   // 📬 inny pośrednik doręczył - kopie tutaj usuwane (bez podwójnej wysyłki)
                if (p["mids"] is JsonArray dm && _komStore.TryGetValue(dto, out var dl)) { var ms = dm.Select(x => (string)x).ToHashSet(); int rm; lock (dl) rm = dl.RemoveAll(x => ms.Contains((string)x["mid"])); KomStoreSave(); if (rm > 0) KomDiag("POTWIERDZONA", "wiadomość", string.Join(",", ms), KomSrvTag(_komNetPeer ?? KomPeerAt(addr)) + " (doręczył)", KomSrvTag(_komId), "inny serwer doręczył odbiorcy " + KomSrvTag(dto) + " – kopia tutaj usunięta (" + rm + ")"); }
                if (p["files"] is JsonArray df && dto.Length == 32 && dto.All(Uri.IsHexDigit))
                {
                    if (_komBoxGone.Count > 1000) foreach (var g in _komBoxGone) if (DateTime.UtcNow - g.Value > TimeSpan.FromDays(1)) _komBoxGone.TryRemove(g.Key, out _);
                    foreach (var x in df) if ((string)x is { Length: 32 } fh && fh.All(Uri.IsHexDigit))
                    {
                        _komBoxGone[fh] = DateTime.UtcNow;
                        try { if (Directory.Exists(KomBoxDir(dto))) foreach (var bf in Directory.GetFiles(KomBoxDir(dto), "*-" + fh + ".*")) File.Delete(bf); } catch (Exception) { }
                    }
                }
                return;
            }
            if (t == "p2p-here" && from != _komId && _komContacts.ToArray().Any(c => c.Id == from && c.Dom))
            {   // mój serwer albo moje urządzenie (telefon, laptop) - serwer oddaje mu kontakty, których nie ma (np. po ponownej instalacji)
                if (p["relay"] != null) _komLanRelays[from] = p.DeepClone();
                if (_komRelayListener != null && (!_komSyncAt.TryGetValue(from, out var sa) || DateTime.UtcNow - sa > TimeSpan.FromMinutes(10))) { _komSyncAt[from] = DateTime.UtcNow; var pf = from; Dispatcher.BeginInvoke(new Action(() => KomRelaySync(pf))); }
            }
            KomStoreFlush(from, null, addr);   // 📬 wrócił do domu - wiadomości, które na niego czekały
            if ((string)p["to"] is string sto && KomStoreKeep(p, sto)) { KomEvMsg(sto, p); return; }
            if (from == _komId || _komRemote.IsEmpty) return;   // nasze - bez echa
            var to = (string)p["to"];
            if (to != null) { if (_komRemote.TryGetValue(to, out var rc)) KomSendFrame(rc, p); return; }
            if (t == "p2p-here" || t == "p2p-who" || t == "p2p-bye") foreach (var c in _komRemote.Values) KomSendFrame(c, p);
        }

        // 📬 skrzynka pośrednika: zaszyfrowane wiadomości (pośrednik ich nie odczyta) do urządzenia, którego nie ma ani przez internet,
        // ani w sieci domowej - doręczane, gdy się pojawi (najwyżej 300 na urządzenie, tylko do kontaktów tego komputera; zapisane na dysku - przetrwają ponowne uruchomienie)
        readonly ConcurrentDictionary<string, List<JsonNode>> _komStore = new ConcurrentDictionary<string, List<JsonNode>>();
        bool KomStoreKeep(JsonNode p, string to)
        {
            if (_komRelayListener == null || (string)p["t"] != "p2p-msg" || to == _komId || _komRemote.ContainsKey(to)) return false;
            if (_komLanSeen.TryGetValue(to, out var seen) && DateTime.UtcNow - seen < TimeSpan.FromSeconds(100)) return false;   // w domu - dojdzie siecią domową
            try { if (!_komContacts.ToArray().Any(c => c.Id == to)) return false; } catch (Exception) { return false; }
            var l = _komStore.GetOrAdd(to, _ => new List<JsonNode>());
            var mid = (string)p["mid"];
            if (mid != null) lock (KomEvDoneSet) if (KomEvDoneSet.Contains(mid)) return true;   // już doręczona przez mój serwer - nie wraca do skrzynki
            lock (l) { if (!l.Any(x => (string)x["mid"] == mid)) { l.Add(p.DeepClone()); if (l.Count > 300) l.RemoveAt(0); } }
            KomStoreSave();
            return true;
        }
        void KomStoreFlush(string id, KomRemoteConn rc, IPAddress addr)
        {
            if (_komRelayListener != null) _ = KomBoxFlush(id);   // 📬 pliki ze skrzynki
            if (!_komStore.TryRemove(id, out var l)) return;
            JsonArray mids;
            lock (l) { foreach (var p in l) { if (rc != null) KomSendFrame(rc, p); else KomLanRaw(p, addr); } mids = new JsonArray(l.Select(x => (JsonNode)(string)x["mid"]).ToArray()); }
            foreach (var m in mids) KomDiag("DO-ODBIORCY", "wiadomość", (string)m ?? "-", KomSrvTag(_komId), KomSrvTag(id), rc != null ? "przekazana na połączenie odbiorcy (internet); potwierdzenie „doręczone” idzie od odbiorcy do nadawcy" : "przekazana w sieci domowej");
            KomStoreSave();
            foreach (var m in mids) if ((string)m is string ms) _komBoxDoneOut["m:" + ms] = (id, DateTime.UtcNow);   // powtarzane przez 2 dni (KomBoxDoneRepeat)
            KomEvDone(id, mids.Select(m => (string)m));
            if (mids.Count > 0) KomBoxPeers(new JsonObject { ["t"] = "p2p-boxdone", ["id"] = _komId, ["to"] = id, ["mids"] = mids, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });   // inni pośrednicy kasują swoje kopie
        }
        // 📬 pośrednicy w domu (NUCBOX, CHUWI, …) dzielą skrzynkę: kopia wiadomości u każdego, po doręczeniu kasowana
        void KomBoxPeers(JsonNode p)
        {
            var pt0 = p["t"]?.GetValue<string>();
            if (KomSyncOff && pt0 != "p2p-admcmd" && pt0 != "p2p-admack" && pt0 != "p2p-srvinfo") return;   // ⏸ synchronizacja wstrzymana przez administratora
            if (pt0 == "p2p-msg") { p = p.DeepClone(); p["box"] = true; }
            foreach (var a in KomPeerAddrs()) KomLanRaw(p, a);
            KomSrvNet(p);   // 🔗 moje serwery poza tą siecią domową (dodane przez internet) - te same dane łączem serwerów
        }
        List<IPAddress> KomPeerAddrs() => _komLanRelays.Keys.Where(k => k != _komId && _komLanSeen.TryGetValue(k, out var ls) && DateTime.UtcNow - ls < TimeSpan.FromMinutes(10))
            .Select(k => _komLanAddr.TryGetValue(k, out var a) ? a : null).Where(a => a != null).ToList();
        // 🔁 pośrednicy w domu uczą się od siebie: każdy mój kontakt poznaje drugiego pośrednika (i on jego) - wiadomości i skrzynka działają
        // przez którykolwiek, nawet gdy kontakt dodano tylko na jednym; raz na parę (zapisane w „intro”), klucz pary losowy
        readonly ConcurrentDictionary<string, DateTime> _komSyncAt = new ConcurrentDictionary<string, DateTime>();
        DateTime _komAltAt;
        DateTime _komForeignLog;   // ostatni zapis połączenia spoza Velivo   // ostatnia próba zapasowych adresów serwerów
        void KomRelaySync(string peerId)
        {
            var peer = KomContactById(peerId); if (peer == null || peer.IsGroup || !peer.Dom) return;   // tylko moje urządzenie z domu (serwer, telefon, laptop) - nigdy znajomego
            if (!string.IsNullOrEmpty(peer.Relay))
            {   // drugi serwer: najpierw wspólny sekret (te same klucze par na każdym serwerze); tylko póki nieuzgodniony i najwyżej 3 razy -
                // starsze Velivo nie zna tego sygnału i pokazywało go jako wiadomość co 10 minut
                bool agreed = _komSekPeer.TryGetValue(peerId, out var po0) && po0 == KomSekOrigin;
                _komSekSent.TryGetValue(peerId, out var sent0);
                if (!agreed && sent0 < 3) { _komSekSent[peerId] = sent0 + 1;
                KomDeliver(peer, new KomMsg { Mine = true, Text = KomSekTag + new JsonObject { ["s"] = KomIntroSec(), ["o"] = KomSekOrigin }.ToJsonString(), Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mid = Guid.NewGuid().ToString("N") }); }
                if (!_komSekPeer.TryGetValue(peerId, out var po) || po != KomSekOrigin) return;
                // 📝 jedna baza na moich serwerach: konta (numer + PIN) i usunięci - drugiemu serwerowi tylko po zmianie (skrót), tą samą drogą co wiadomość (dom albo internet)
                var acc = new JsonObject { ["konta"] = JsonNode.Parse(KomAccounts().ToJsonString()), ["removed"] = JsonNode.Parse((_komOpt["removed"] ?? new JsonArray()).ToJsonString()) }.ToJsonString();
                var ah = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(acc)), 0, 8);
                if (!_komAccSent.TryGetValue(peerId, out var sh) || sh != ah) { _komAccSent[peerId] = ah; KomDeliver(peer, new KomMsg { Mine = true, Text = KomAcctTag + acc, Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mid = Guid.NewGuid().ToString("N") }); }
            }
            if (_komOpt["intro"] is not JsonArray done) _komOpt["intro"] = done = new JsonArray();
            var today = "|" + DateTime.UtcNow.ToString("yyyyMMdd");
            foreach (var old in done.Where(n => (string)n is { } t && t.Length > 9 && t[^9] == '|' && t[^8..].All(char.IsDigit) && !t.EndsWith(today)).ToList()) done.Remove(old);   // wczorajsze znaczniki precz
            var have = done.Select(x => (string)x).ToHashSet(); bool any = false;
            foreach (var x in _komContacts.ToList())
            {
                var tag = x.Id + "|" + peerId + "|" + peer.Relay + "|" + x.Relay;   // nowy adres serwera (włączony później, zmiana portu) - przedstawienie od nowa
                if (x.IsGroup || x.Id == peerId || x.Id == _komId || x.Key == null) continue;
                var key = KomIntroKey(x.Id, peerId);
                string Card(KomContact c) => KomCardTag + new JsonObject { ["id"] = c.Id, ["name"] = c.Name, ["dev"] = c.Device, ["key"] = key, ["relay"] = c.Relay ?? "", ["dom"] = c.Dom }.ToJsonString();
                // serwery wymieniają się WSZYSTKIMI kontaktami od razu (drugi serwer jest w sieci - słychać go): CHUWI zna mamę dodaną przez NUCBOX, także gdy mamy teraz nie ma
                // też co dzień: serwer, który kartę przegapił (starsza wersja, wyłączony), dostaje ją znowu - znany kontakt się nie zmienia
                // + mój klucz z tym kontaktem (rk): drugi mój serwer obsłuży jego telefon w moim imieniu, gdy jestem wyłączony
                string PeerCard(KomContact c) => KomCardTag + new JsonObject { ["id"] = c.Id, ["name"] = c.Name, ["dev"] = c.Device, ["key"] = key, ["relay"] = c.Relay ?? "", ["dom"] = c.Dom, ["rk"] = Convert.ToBase64String(c.Key), ["rv"] = _komId }.ToJsonString();
                if (!have.Contains(tag + "|s" + today)) { KomDeliver(peer, new KomMsg { Mine = true, Text = PeerCard(x), Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mid = Guid.NewGuid().ToString("N") }); done.Add(tag + "|s" + today); any = true; }
                // kontakt poznaje drugi serwer, gdy jest w sieci (karta nie czeka w skrzynce) - nieobecny przy następnym sprawdzeniu (co 10 min)
                // co dzień od nowa: jednorazowa karta przepadała, gdy telefon miał starą wersję albo ją pominął - mama znała tylko NUCBOX i bez niego była poza siecią
                var dtag = tag + "|" + DateTime.UtcNow.ToString("yyyyMMdd");
                if (!have.Contains(dtag) && KomOnline(x.Id) && (x.Dom || !string.IsNullOrEmpty(peer.Relay) || !_komPcs.Contains(peerId))) {   // znajomym (mama) tylko moje serwery i telefony - bez pytań o każdy mój komputer
                    KomDeliver(x, new KomMsg { Mine = true, Text = Card(peer), Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mid = Guid.NewGuid().ToString("N") }); done.Add(dtag); any = true; }
            }
            if (any) KomSave();
        }
        // 🔁 to urządzenie zna dwa serwery w tym samym domu (np. NUCBOX i CHUWI), a one mogą nie znać siebie - przedstawia je sobie raz;
        // tylko serwery widoczne teraz w sieci domowej (moje), nigdy serwer znajomego - kontakty nie wychodzą z domu
        void KomRelayPair()
        {
            var rs = _komContacts.Where(c => !c.IsGroup && c.Dom && !string.IsNullOrEmpty(c.Relay) && c.Key != null && _komLanSeen.TryGetValue(c.Id, out var ls) && DateTime.UtcNow - ls < TimeSpan.FromSeconds(100)).ToList();
            if (rs.Count < 2) return;
            if (_komOpt["intro"] is not JsonArray done) _komOpt["intro"] = done = new JsonArray();
            var have = done.Select(x => (string)x).ToHashSet(); bool any = false;
            for (int i = 0; i < rs.Count; i++)
                for (int j = i + 1; j < rs.Count; j++)
                {
                    var (a, b) = string.CompareOrdinal(rs[i].Id, rs[j].Id) < 0 ? (rs[i], rs[j]) : (rs[j], rs[i]);
                    var tag = a.Id + "|" + b.Id + "|" + a.Relay + "|" + b.Relay;
                    if (have.Contains(tag)) continue;
                    var key = KomIntroKey(a.Id, b.Id);
                    string Card(KomContact c) => KomCardTag + new JsonObject { ["id"] = c.Id, ["name"] = c.Name, ["dev"] = c.Device, ["key"] = key, ["relay"] = c.Relay ?? "", ["dom"] = true }.ToJsonString();
                    KomDeliver(a, new KomMsg { Mine = true, Text = Card(b), Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mid = Guid.NewGuid().ToString("N") });
                    KomDeliver(b, new KomMsg { Mine = true, Text = Card(a), Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mid = Guid.NewGuid().ToString("N") });
                    done.Add(tag); any = true;
                }
            if (any) KomSave();
        }
        // klucz pary przy przedstawianiu: zawsze ten sam dla tej pary (powtórka nie daje stronom różnych kluczy)
        string KomIntroKey(string a, string b) => KomIntroKeyFrom(Convert.FromBase64String(KomIntroSec()), a, b);
        string KomIntroSec() { if ((string)_komOpt["introSec"] is not { Length: 44 } sec) _komOpt["introSec"] = sec = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); return sec; }
        // 🔑 moje serwery mają jeden wspólny sekret przedstawiania (serwera o najmniejszym id) - urządzenie przedstawione przez którykolwiek serwer
        // dostaje ten sam klucz pary, co od pierwszego (wcześniej każdy serwer liczył inny i trzeci serwer odrzucał „inny klucz”)
        const string KomSekTag = "\u0001velivo-sekret\n";
        readonly Dictionary<string, string> _komSekPeer = new Dictionary<string, string>(); readonly Dictionary<string, int> _komSekSent = new Dictionary<string, int>();   // czyj sekret ma mój serwer obok
        string KomSekOrigin => (string)_komOpt["introBy"] ?? _komId;
        void KomSekIn(KomContact from, string body)
        {
            JsonNode j; try { j = JsonNode.Parse(body); } catch (Exception) { return; }
            var s = (string)j?["s"]; var o = (string)j?["o"];
            if (s is not { Length: 44 } || o is not { Length: 32 }) return;
            bool changed = !_komSekPeer.TryGetValue(from.Id, out var was) || was != o;
            _komSekPeer[from.Id] = o;
            if (string.CompareOrdinal(o, KomSekOrigin) < 0) { _komOpt["introSec"] = s; _komOpt["introBy"] = o; KomSave(); changed = true; }
            if (changed) _komSekSent.Remove(from.Id);   // nowa wiadomość od niego - odpowiedź może pójść
            if (changed && _komRelayListener != null) { _komSyncAt[from.Id] = DateTime.UtcNow; KomRelaySync(from.Id); }   // od razu odpowiedź i kontakty (bez czekania 10 min); bez zmian - cisza, więc nie w kółko
        }
        // zapasowe adresy jednego serwera: jego host i hosty moich domowych serwerów, port zapamiętany i każdy 41920–41929 (bez obecnego adresu)
        internal static List<string> KomAltAddrs(string relay, IEnumerable<string> homeHosts)
        {
            int i = (relay ?? "").LastIndexOf(':'); if (i <= 0) return new List<string>();
            var ports = new[] { relay.Substring(i + 1) }.Concat(Enumerable.Range(41920, 10).Select(p => p.ToString())).Distinct();
            return homeHosts.Append(relay.Substring(0, i)).Distinct().SelectMany(h => ports.Select(p => h + ":" + p))
                .Where(a => a != relay && int.TryParse(a.Substring(a.LastIndexOf(':') + 1), out _)).ToList();
        }
        internal static string KomIntroKeyFrom(byte[] sec, string a, string b)
        {
            var ids = string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;   // ta sama para w dowolnej kolejności - ten sam klucz
            return Convert.ToBase64String(HMACSHA256.HashData(sec, Encoding.UTF8.GetBytes(ids)));
        }
        // mój serwer, który DZIAŁA (połączony albo widoczny w sieci domowej), najpierw domowy - wcześniej zawsze pierwszy z listy (NUCBOX), także wyłączony
        KomContact KomLiveRelay() => _komContacts.Where(c => !string.IsNullOrEmpty(c.Relay) && !c.IsGroup)
            .OrderByDescending(c => (c.Dom ? 2 : 0) + ((_komUp != null && _komUpRelay?.Id == c.Id) || (_komLanSeen.TryGetValue(c.Id, out var ls) && DateTime.UtcNow - ls < TimeSpan.FromSeconds(100)) ? 1 : 0)).FirstOrDefault();
        // mój serwer: domowy albo ten, przez który to urządzenie jest teraz połączone z internetu (obcy kontakt nim nie jest)
        bool KomTrustedRelay(KomContact c) => c != null && (c.Dom || (_komUp != null && _komUpRelay?.Id == c.Id));
        bool KomFromRelay(IPAddress addr) => _komNetPeer != null || _komLanRelays.Keys.Any(k => _komLanAddr.TryGetValue(k, out var a) && a.Equals(addr));
        string KomPeerAt(IPAddress addr) => addr == null ? null : _komLanRelays.Keys.FirstOrDefault(k => _komLanAddr.TryGetValue(k, out var a) && a.Equals(addr));
        static string KomStoreFile => Path.Combine(DataDir, "komunikator-skrzynka.json");
        void KomStoreSave()
        {
            try
            {
                var o = new JsonObject();
                foreach (var kv in _komStore) lock (kv.Value) o[kv.Key] = new JsonArray(kv.Value.Select(x => x.DeepClone()).ToArray());
                VelivoData.SafeWrite(KomStoreFile, o.ToJsonString());
            }
            catch (Exception) { }
        }
        void KomStoreLoad()
        {
            try
            {
                if (!_komStore.IsEmpty || !File.Exists(KomStoreFile)) return;
                if (JsonNode.Parse(File.ReadAllText(KomStoreFile)) is JsonObject o)
                    foreach (var kv in o) if (kv.Value is JsonArray a) _komStore[kv.Key] = a.Select(x => x.DeepClone()).ToList();
            }
            catch (Exception) { }
        }

        // pakiet urządzenia poza domem przekazany do domowego Wi-Fi - ze znakiem, żeby w domu było widać 🌍, a nie 🏠
        static JsonNode KomFwd(JsonNode p) { var c = p.DeepClone(); c["fwd"] = true; return c; }
        void KomLanRaw(JsonNode p, IPAddress to)
        {
            var d = Encoding.UTF8.GetBytes(p.ToJsonString());
            var s = _komUdp ?? _lanTx; if (s == null) return;
            foreach (var t in to != null ? new List<IPAddress> { to } : KomBroadcastTargets()) foreach (var pt in new[] { KomPort, LanPort }) try { s.Send(d, d.Length, new IPEndPoint(t, pt)); } catch (Exception) { }
        }

        // ---------- pośrednik (serwer) ----------
        void KomRelayStart()
        {
            KomRelayStop(!_komRelayOn);
            if (!_komRelayOn) { _komRelayAutoTimer?.Stop(); return; }
            KomStoreLoad();   // 📬 wiadomości czekające sprzed ponownego uruchomienia
            _ = KomRelayAutoAsync();
            if (_komRelayAutoTimer == null)
            {
                _komRelayAutoTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(1) };
                _komRelayAutoTimer.Tick += async (s, e) =>
                {   // 🔎 co godzinę: pamięć i wątki pośrednika (szukanie przyczyny zawieszania / zamykania Velivo na NUCBOX)
                    using (var me = System.Diagnostics.Process.GetCurrentProcess())
                        App.LogInfo("Pośrednik: pamięć " + me.PrivateMemorySize64 / 1048576 + " MB, wątki " + me.Threads.Count + ", uchwyty " + me.HandleCount + ", połączenia " + _komRemote.Count);
                    await KomRelayAutoAsync(); KomFirewallCheck();
                };
            }
            _komRelayAutoTimer.Start(); KomNetWatch();
            try
            {
                _komRelayCts = new CancellationTokenSource(); var token = _komRelayCts.Token;
                var l = new TcpListener(IPAddress.Any, _komRelayPort); l.Start(); _komRelayListener = l; KomFirewallCheck();
                _ = Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        try { var c = await l.AcceptTcpClientAsync(token); _komRelayAcceptAt = DateTime.UtcNow; c.NoDelay = true; _ = Task.Run(() => KomRelayServe(c, token)); }   // TURN (dźwięk rozmów) i ramki bez opóźnienia Nagle'a
                        catch (OperationCanceledException) { break; } catch (ObjectDisposedException) { break; } catch (Exception) { await Task.Delay(300); }
                    }
                });
            }
            catch (Exception ex) { App.LogError(ex); KomInfo(L.T("🌍 Pośrednik: port zajęty lub zablokowany – ") + ex.Message); }
        }

        readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _komRejLog = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>();   // ten sam odrzucony co 40 s zaśmiecał Błędy Velivo
        void KomRelayStop(bool bye = false)
        {
            try { if (bye && _komRelayListener != null) KomBoxPeers(new JsonObject { ["t"] = "p2p-srvbye", ["id"] = _komId }); } catch (Exception) { }   // ☕ wyłączam się (zamknięcie Velivo / wyłączenie serwera) - inne serwery przejmują; nie przy ponownym uruchomieniu serwera
            try { _komRelayCts?.Cancel(); } catch (Exception) { }
            try { _komRelayListener?.Stop(); } catch (Exception) { }
            _komRelayListener = null; _komRelayCts = null;
            foreach (var c in _komRemote.Values) try { c.Fs.Dispose(); } catch (Exception) { }
            _komRemote.Clear();
        }

        async Task KomRelayServe(TcpClient cl, CancellationToken token)
        {
            KomFrameStream fs = null; string id = null; KomRemoteConn me = null; var meAt = DateTime.UtcNow;
            try
            {
                var s = cl.GetStream(); s.ReadTimeout = 15000;
                // 📹 ten sam port: TURN dla rozmów wideo (KomunikatorTurn.cs) albo komunikator
                for (int i = 0; i < 150 && cl.Available < 8; i++) await Task.Delay(100, token);
                var peek = new byte[8];
                if (cl.Available >= 8 && cl.Client.Receive(peek, 8, SocketFlags.Peek) == 8 && KomIsStun(peek)) { s.ReadTimeout = Timeout.Infinite; await KomTurnServe(cl, token); return; }
                byte[] hello, mac;
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(15));
                    hello = await KomReadBlock(s, 4096, cts.Token); mac = await KomReadBlock(s, 64, cts.Token);
                }
                if (hello == null || mac == null)
                {   // 🔎 połączenie, które nie jest Velivo (np. test z przeglądarki telefonu) - dowód, że port z internetu dochodzi do tego komputera; najwyżej raz na minutę
                    if (DateTime.UtcNow - _komForeignLog > TimeSpan.FromMinutes(1)) { _komForeignLog = DateTime.UtcNow; App.LogInfo("Pośrednik: połączenie spoza Velivo z " + ((cl.Client.RemoteEndPoint as IPEndPoint)?.Address?.ToString() ?? "?") + " na port " + _komRelayPort + " – port z zewnątrz dochodzi do tego komputera"); }
                    cl.Dispose(); return;
                }
                var h = JsonNode.Parse(hello);
                if ((string)h?["Type"] == "relay-ask") { await KomAskServe(h, mac, s, token); return; }
                if ((string)h?["Type"] == "relay-register") { await KomRegisterServe(h, s, cl, token); return; }   // 📝 nowe konto na serwerze (jak w Signalu): numer + PIN
                if ((string)h?["Type"] == "relay-restore") { await KomRestoreServe(h, s, cl, token); return; }   // ♻ nowy telefon prosi o kopię konta - decyduje właściciel serwera   // 🔗 prośba o kontakt z linku „Mój adres Velivo” (ktoś spoza kontaktów)
                id = (string)h["Id"];
                KomContact c = null; await Dispatcher.InvokeAsync(() => { KomLoad(); c = KomContactById(id ?? ""); });
                // ✉ nowe urzadzenie z zaproszeniem: klucz zaproszenia zamiast klucza kontaktu (jednorazowo, do dolaczenia)
                KomInvite inv = null;
                if ((string)h["Mode"] == "invite" && _komInvites.TryGetValue((string)h["Tunnel"] ?? "", out inv) && inv.Exp > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() && id?.Length == 32 && id != _komId)
                    c = new KomContact { Id = id, Key = inv.K };
                long ts = (long?)h["Ts"] ?? 0;
                // tylko kontakty tego komputera, swiezy podpis kluczem kontaktu; 🔎 każda odrzucona próba w dzienniku (wcześniej po cichu - nie było widać, czemu zapas nie działa)
                var who = (c?.Name ?? "?") + " [" + (id ?? "").Substring(0, Math.Min(6, (id ?? "").Length)) + "] z " + ((cl.Client.RemoteEndPoint as IPEndPoint)?.Address?.ToString() ?? "?");
                // 🔁 zastępstwo: telefon zna tylko inny mój serwer (np. mama - tylko NUCBOX), a trafił tutaj (443, adres domu) - mój serwer przekazał mi jego klucz z tym telefonem, więc obsługuję go w jego imieniu
                var rk = c?.Key; var rel = (string)h["Relay"] ?? "";
                if (c != null && rel != _komId && _komOpt["alias"] is JsonObject al && (string)al[c.Id + "|" + rel] is { } ak) { try { rk = Convert.FromBase64String(ak); } catch (Exception) { } if (rk?.Length == 32) { rel = _komId; who += " (zamiast " + (KomContactById((string)h["Relay"])?.Name ?? "innego serwera") + ")"; } }
                string why = c == null ? "nieznane urządzenie (nie ma go w kontaktach tego serwera)" : (string)h["Type"] != "relay-hello" ? "zły typ" : rel != _komId ? "połączenie do innego serwera: " + (KomContactById(rel)?.Name ?? (rel.Length >= 6 ? rel.Substring(0, 6) : "?")) + " (adres wskazuje ten komputer; brak klucza zastępczego od tamtego serwera)"
                    : Math.Abs(ts - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) > 300000 ? "zły czas zegara (różnica ponad 5 min)" : null;
                if (why != null) { var rejKey = who + why; if (!_komRejLog.TryGetValue(rejKey, out var rejAt) || DateTime.UtcNow - rejAt > TimeSpan.FromMinutes(30)) { _komRejLog[rejKey] = DateTime.UtcNow; App.LogInfo("Pośrednik: odrzucono " + who + " – " + why + " (powtórki przez 30 min nie są zapisywane)"); } _komLastReject = (c?.Name ?? L.T("nieznane urządzenie")) + " – " + why; _ = Dispatcher.BeginInvoke(new Action(KomRelayStatus)); cl.Dispose(); return; }
                var exp = KomMacBytes(rk, "relay-hello\n", hello);
                if (mac.Length != exp.Length || !CryptographicOperations.FixedTimeEquals(mac, exp)) { App.LogInfo("Pośrednik: odrzucono " + who + " – inny klucz (kontakt dodany na nowo tylko po jednej stronie)"); _komLastReject = c.Name + " – " + L.T("inny klucz (ten kontakt trzeba usunąć i dodać na nowo)"); _ = Dispatcher.BeginInvoke(new Action(KomRelayStatus)); cl.Dispose(); return; }
                if ((string)h["Mode"] == "main") App.LogInfo("Pośrednik: połączono " + who);
                var nc = KomB64((string)h["Nonce"]); if (nc.Length != 16) { cl.Dispose(); return; }
                var ns = RandomNumberGenerator.GetBytes(16);
                var rep = Encoding.UTF8.GetBytes(new JsonObject { ["Nonce"] = Convert.ToBase64String(ns) }.ToJsonString());
                await KomWriteBlock(s, rep, token);
                await KomWriteBlock(s, KomMacBytes(rk, "relay-ok\n", hello.Concat(rep).ToArray()), token);
                fs = new KomFrameStream(cl, KomSessionKey(rk, nc, ns));
                var mode = (string)h["Mode"]; var to = (string)h["To"] ?? ""; var tid = (string)h["Tunnel"] ?? "";

                if (mode == "invite")
                {   // dolaczenie do pośrednika: tylko dodanie kontaktu (bez pytania), zaproszenie zuzyte
                    if (inv == null) return;
                    var hb = await KomReadBlock(fs, 4096, token); if (hb == null) return;
                    var ph = JsonNode.Parse(hb); if ((string)ph?["Type"] != "p2p-pair" || (string)ph["Id"] != id) return;
                    _komInvites.TryRemove(KomInviteId(inv.K), out _); await Dispatcher.InvokeAsync(KomSave);
                    await KomReceivePair(ph, fs, IPAddress.Loopback, token, auto: true);
                    return;
                }
                if (mode == "tunnel-in") { if (_komTunnels.TryRemove(tid, out var w)) { w.TrySetResult(fs); fs = null; } return; }   // strumien odbiera czekajacy tunel
                if (mode == "tunnel") { if (KomIsMember(id)) _komTunnelSrv.Value = id; await KomRelayTunnel(fs, to, token); return; }
                if (mode == "backup") { await Task.Run(() => KomBackupReceive(fs, c), token); return; }   // ☁ kopia kontaktów i rozmów urządzenia
                if (mode != "main") return;

                try { cl.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch (Exception) { }   // martwe połączenie z telefonem wykrywa też system
                me = new KomRemoteConn { Fs = fs }; meAt = DateTime.UtcNow;
                if (_komRemote.TryRemove(id, out var old)) try { old.Fs.Dispose(); } catch (Exception) { }
                _komRemote[id] = me;
                _komViaNet[id] = true;
                KomSendFrame(me, KomRelayHere());   // pośrednik jest w sieci
                foreach (var kv in _komLanRelays) if (_komLanSeen.TryGetValue(kv.Key, out var ls) && DateTime.UtcNow - ls < TimeSpan.FromMinutes(10)) { var rh = kv.Value.DeepClone(); rh["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); rh["fwd"] = true; KomSendFrame(me, rh); }   // adresy innych pośredników w domu (np. CHUWI) - telefon zna je, gdy ten zgaśnie
                KomStoreFlush(id, me, null);   // 📬 wiadomości, które czekały, aż się pojawi
                await Dispatcher.InvokeAsync(() => { KomRelayStatus(); _komRefresh?.Invoke(); });
                while (!token.IsCancellationRequested)
                {
                    var data = await Task.Run(() => fs.ReadFrame(), token);
                    var p = JsonNode.Parse(data); var t = (string)p?["t"];
                    if (t == "relay-log") { App.LogInfo(("Pośrednik: " + id.Substring(0, 6) + " – " + ((string)p["why"] ?? "?"))); continue; }   // 🔎 powód rozłączenia podany przez telefon
                    if (t == "relay-ping")
                    {   // sygnał co 20 s - urządzenie poza domem nadal w sieci (inaczej po 95 s „poza siecią”, choć połączone)
                        KomSendFrame(me, new JsonObject { ["t"] = "relay-pong" });
                        var pid = id; _ = Dispatcher.BeginInvoke(new Action(() => { bool was = KomOnline(pid); _komLastSeen[pid] = DateTime.UtcNow; _komSeenAt[pid] = DateTime.Now; if (!was) _komRefresh?.Invoke(); }));
                        continue;
                    }
                    if (t == null || !t.StartsWith("p2p-", StringComparison.Ordinal) || (string)p["id"] != id) continue;   // tylko pakiety tego urzadzenia
                    var pto = (string)p["to"];
                    if (t == "p2p-srvid") { if (KomSrvIdIn(id, p)) KomSendFrame(me, KomSrvIdFrame(id)); continue; }   // 🏛 mój serwer przedstawia się certyfikatem
                    if (t == "p2p-srv") { KomSrvIn(id, p["p"]); continue; }   // 🔗 łącze serwerów: mój serwer połączony ze mną przez internet
                    if (t == "p2p-who") { KomSendFrame(me, KomRelayHere()); KomLanRaw(KomFwd(p), null); foreach (var o in _komRemote) if (o.Key != id) KomSendFrame(o.Value, p); continue; }
                    // do pośrednika (albo do wszystkich) - obsluga tutaj; odpowiedzi ida do urzadzenia przez "to"
                    if (pto == null || pto == _komId) { var pp = p; _ = Dispatcher.BeginInvoke(new Action(() => { try { KomHandle(pp, IPAddress.Loopback); } catch (Exception ex) { App.LogError(ex); } })); }
                    if (pto == _komId) continue;
                    if (pto != null && _komRemote.TryGetValue(pto, out var rc)) { KomSendFrame(rc, p); if ((string)p["t"] == "p2p-msg" && (string)p["mid"] is string lm) { _komBoxDoneOut["m:" + lm] = (pto, DateTime.UtcNow); KomEvDone(pto, new[] { lm }); KomBoxPeers(new JsonObject { ["t"] = "p2p-boxdone", ["id"] = _komId, ["to"] = pto, ["mids"] = new JsonArray((JsonNode)lm), ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }); } continue; }   // doręczone wprost - kopie na innych moich serwerach kasowane   // oba urzadzenia poza domem
                    // do sieci domowej: znany adres albo do wszystkich
                    if (pto != null && KomStoreKeep(p, pto)) { KomDiag("PRZYJĘTA", "wiadomość", (string)p["mid"] ?? "-", KomSrvTag(id) + " (nadawca)", KomSrvTag(_komId) + " → dla " + KomSrvTag(pto), "czeka w skrzynce; replikacja do: " + string.Join(", ", KomPeerAddrs().Select(KomPeerAt).Concat(KomMyServers().Where(c => !KomLanPeer(c.Id) && _komLinkOk.ContainsKey(c.Id)).Select(c => c.Id)).Distinct().Select(KomSrvTag).DefaultIfEmpty("brak innych serwerów"))); KomBoxPeers(KomFwd(p)); KomEvMsg(pto, p); continue; }   // odbiorca niedostępny - pośrednik przechowuje (kopia u innych pośredników w domu)
                    KomLanRaw(KomFwd(p), pto != null && _komLanAddr.TryGetValue(pto, out var a) ? a : null);
                    if (pto == null) foreach (var o in _komRemote) if (o.Key != id) KomSendFrame(o.Value, p);
                }
            }
            catch (Exception ex)
            {   // 🔎 dziennik: dlaczego urzadzenie poza domem sie rozlaczylo (do szukania przyczyny zrywania)
                if (me != null && ex is not IOException && ex is not EndOfStreamException && ex is not ObjectDisposedException) App.LogInfo(("Pośrednik: rozłączono " + (id ?? "").Substring(0, Math.Min(6, (id ?? "").Length)) + " po " + (int)(DateTime.UtcNow - meAt).TotalSeconds + " s: " + ex.GetType().Name + " " + ex.Message));
            }
            finally
            {
                if (me != null && id != null && _komRemote.TryGetValue(id, out var cur) && cur == me)
                {
                    _komRemote.TryRemove(id, out _); _komLinkOk.TryRemove(id, out _);
                    var bye = new JsonObject { ["t"] = "p2p-bye", ["id"] = id, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };   // w domu od razu "poza siecia"
                    KomLanRaw(KomFwd(bye), null);   // przekazane (fwd) - inaczej echo u pośrednika = „w domu” pod jego własnym adresem (pliki do tego urządzenia trafiały do pośrednika)
                    _ = Dispatcher.BeginInvoke(new Action(() => { _komLastSeen.Remove(id); KomRelayStatus(); _komRefresh?.Invoke(); }));
                }
                try { fs?.Dispose(); } catch (Exception) { }
            }
        }

        JsonObject KomRelayHere() { var p = KomPacket("p2p-here"); return p; }

        // tunel od urzadzenia poza domem (plik): do pośrednika, do innego urzadzenia poza domem albo do sieci domowej
        async Task KomRelayTunnel(KomFrameStream fs, string to, CancellationToken token)
        {
            if (to == _komId)
            {   // plik / kontakt dla samego pośrednika
                var hb = await KomReadBlock(fs, 4096, token); if (hb == null) return;
                await KomTcpAsync(hb, fs, IPAddress.Loopback, token);
                return;
            }
            if (_komRemote.ContainsKey(to)) { await KomRelayToRemote(to, null, fs, token); return; }
            var hb0 = await KomReadBlock(fs, 4096, token); if (hb0 == null) return;
            if (Encoding.UTF8.GetString(hb0).Contains("\"p2p-box\"")) { await KomTcpAsync(hb0, fs, IPAddress.Loopback, token); return; }   // 📬 odbiorcy nie ma - skrzynka pośrednika
            // odbiorca w domu (świeży adres) albo połączony z innym pośrednikiem w domu - ten przekaże dalej (KomTcpAsync -> KomRelayToRemote)
            var addr = KomLanFresh(to);
            // przez innego pośrednika w domu - tylko gdy ten teraz działa (wyłączony NUCBOX nie może zatrzymać dostawy przez CHUWI)
            if (addr == null && _komViaRelay.TryGetValue(to, out var va) && _komLanAddr.Any(kv => kv.Value.Equals(va) && _komLanSeen.TryGetValue(kv.Key, out var vs) && DateTime.UtcNow - vs < TimeSpan.FromSeconds(100))) addr = va;
            if (addr == null)
            {   // adresu jeszcze nie znamy - pytanie w sieci domowej
                KomLanRaw(KomPacket("p2p-who"), null);
                for (int i = 0; i < 15 && (addr = KomLanFresh(to)) == null; i++) await Task.Delay(200, token);
            }
            if (addr == null) return;
            using (var lan = await KomTcpConnect(addr, token))
            {
                await KomWriteBlock(lan.GetStream(), hb0, token);
                await KomPipe(fs, lan.GetStream(), token);
            }
        }

        // plik dla urzadzenia poza domem: prosba o tunel, potem przekazywanie w obie strony
        async Task KomRelayToRemote(string toId, byte[] headerBytes, Stream src, CancellationToken token)
        {
            var fs0 = await KomRelayOpenTo(toId, token); if (fs0 == null) return;
            using (var fs = fs0)
            {
                if (headerBytes != null) { await KomWriteBlock(fs, headerBytes, token); }
                await KomPipe(src, fs, token);
            }
        }

        // przekazywanie w obie strony; ramki wysylane od razu po kazdym kawalku
        static async Task KomPipe(Stream a, Stream b, CancellationToken token)
        {
            async Task Copy(Stream x, Stream y)
            {
                var buf = new byte[1 << 18];   // 256 KB na ramkę - mniej ramek i potwierdzeń przy dużych plikach przez serwer (ramka do 2 MB)
                try { while (true) { int r = await x.ReadAsync(buf, 0, buf.Length, token); if (r <= 0) break; await y.WriteAsync(buf, 0, r, token); await y.FlushAsync(token); } }
                catch (Exception) { }
            }
            var t1 = Copy(a, b); var t2 = Copy(b, a);
            await Task.WhenAny(t1, t2);
            await Task.WhenAny(Task.WhenAll(t1, t2), Task.Delay(3000));   // druga strona dokancza (potwierdzenie pliku)
            try { a.Dispose(); } catch (Exception) { } try { b.Dispose(); } catch (Exception) { }
        }

        // ---------- ten komputer poza domem ----------
        // co 20 s: pośrednik nie widoczny w sieci domowej - polaczenie przez internet; widoczny - z powrotem w domu
        void KomUpStart()
        {
            if (_komUpTimer != null) return;
            _komUpTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _komUpTimer.Tick += (s, e) => KomUpCheck();
            _komUpTimer.Start();
            Dispatcher.BeginInvoke(new Action(KomUpCheck), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        readonly DateTime _komUpSince = DateTime.UtcNow;
        void KomUpCheck()
        {
            if (DateTime.UtcNow - _komUpSince < TimeSpan.FromSeconds(30) && !_komUpForce) return;   // po starcie najpierw sprawdzamy sieć domową (pośrednik odpowiada w sekundę)
            // komputery z rozmowami poza domem (kazdy z kontaktami moze nim byc); w domu - ktorykolwiek widoczny w sieci domowej
            var relays = _komContacts.Where(c => !string.IsNullOrEmpty(c.Relay) && c.Relay != _komRelayHost + ":" + _komRelayPort).ToList();   // nie my sami
            var relay = relays.FirstOrDefault();
            // w domu = w sieci domowej widac ktorykolwiek kontakt (wtedy wszystko jak dotad, prosto); poza domem - nikogo
            // pośrednik (działający) zawsze jest „w domu” - nigdy sam nie łączy się z innym pośrednikiem, przekazuje dalej
            // pośrednik z wpisanym adresem domu albo z podłączonymi urządzeniami (np. NUCBOX) zawsze w domu; inny komputer z włączonym pośrednikiem (np. GPD na hotspocie) - w domu tylko, gdy widzi pośrednika
            bool home = relay == null || (_komRelayListener != null && (!_komRemote.IsEmpty || !_komRelayAuto)) || relays.Any(c => _komLanSeen.TryGetValue(c.Id, out var seen) && DateTime.UtcNow - seen < TimeSpan.FromSeconds(100));   // w domu = widać pośrednika, nie dowolny kontakt (np. telefon w tej samej obcej sieci)
            if (home) { if (_komUp != null) { try { _komUp.Fs.Dispose(); } catch (Exception) { } _komUp = null; KomAnnounce(); } return; }
            if (_komUp != null || _komUpConnecting) { if (_komUp != null) KomSendFrame(_komUp, new JsonObject { ["t"] = "relay-ping" }); return; }
            _komUpConnecting = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    KomFrameStream fs = null;
                    // wszystkie serwery naraz, pierwszy, który odpowie (po kolei każdy nieosiągalny kosztował kilkanaście sekund)
                    var tries = relays.Select(c => (c, t: KomRelayConnect(c, "main", null, null, CancellationToken.None))).ToList();
                    var pending = tries.Select(x => x.t).ToList();
                    while (pending.Count > 0 && fs == null)
                    {
                        var done = await Task.WhenAny(pending); pending.Remove(done);
                        var got = await done; if (got != null) { fs = got; relay = tries.First(x => x.t == done).c; }
                    }
                    _ = Task.WhenAll(pending).ContinueWith(t => { foreach (var x in pending) if (x.Status == TaskStatus.RanToCompletion) x.Result?.Dispose(); });   // spóźnione połączenia - zamknięte
                    // serwer ogłosił zły adres (np. wewnętrzny routera): KAŻDY serwer pod adresami moich domowych serwerów (ten sam dom = ten sam adres,
                    // różne porty) - wszystkie próby naraz, co 30 s; próby na kopii kontaktu, zapisany adres zmienia się dopiero po udanym połączeniu
                    if (fs == null && DateTime.UtcNow - _komAltAt > TimeSpan.FromSeconds(30))
                    {
                        _komAltAt = DateTime.UtcNow;
                        var hosts = relays.Where(c => c.Dom && c.Relay.LastIndexOf(':') > 0).Select(c => c.Relay.Substring(0, c.Relay.LastIndexOf(':'))).Distinct().ToList();
                        // każdy port serwerów 41920–41929: serwer przeniesiony na inny port (np. CHUWI 41922 -> 41929) znajduje się sam
                        var alts = relays.SelectMany(c => KomAltAddrs(c.Relay, hosts).Select(alt => (c, alt))).Take(90).ToList();
                        var at = alts.Select(x => (x.c, x.alt, t: KomRelayConnectPort(x.c, x.alt.Substring(0, x.alt.LastIndexOf(':')), int.Parse(x.alt.Substring(x.alt.LastIndexOf(':') + 1)), 7, "main", null, null, CancellationToken.None))).ToList();
                        var left = at.Select(x => x.t).ToList();
                        while (left.Count > 0 && fs == null)
                        {
                            var d = await Task.WhenAny(left); left.Remove(d);
                            var got = await d; if (got != null) { fs = got; var w = at.First(x => x.t == d); relay = w.c; await Dispatcher.InvokeAsync(() => { w.c.Relay = w.alt; KomSave(); }); }
                        }
                        _ = Task.WhenAll(left).ContinueWith(t => { foreach (var x in left) if (x.Status == TaskStatus.RanToCompletion) x.Result?.Dispose(); });
                    }
                    if (fs == null) return;
                    var me = new KomRemoteConn { Fs = fs };
                    _komUpRelay = relay; _komUp = me;
                    await Dispatcher.InvokeAsync(() => { KomAnnounce(); _komRefresh?.Invoke(); });   // jestem (przez internet) + zalegle wiadomosci
                    while (true)
                    {
                        var p = JsonNode.Parse(fs.ReadFrame()); var t = (string)p?["t"];
                        if (t == "relay-open") { var tid = (string)p["tid"]; _ = Task.Run(() => KomUpTunnelIn(relay, tid)); continue; }
                        if (t == null || !t.StartsWith("p2p-", StringComparison.Ordinal)) continue;
                        var from = (string)p["id"]; if (from != null) _komViaNet[from] = true;
                        await Dispatcher.InvokeAsync(() => { try { KomHandle(p, IPAddress.Loopback); } catch (Exception ex) { App.LogError(ex); } });
                    }
                }
                catch (Exception) { }
                finally { _komUpConnecting = false; var u = _komUp; if (u != null) { _komUp = null; try { u.Fs.Dispose(); } catch (Exception) { } } }
            });
        }

        // plik do nas od urzadzenia w domu (przez pośrednika)
        async Task KomUpTunnelIn(KomContact relay, string tid)
        {
            using (var fs = await KomRelayConnect(relay, "tunnel-in", tid, null, CancellationToken.None))
            {
                if (fs == null) return;
                var hb = await KomReadBlock(fs, 4096, CancellationToken.None); if (hb == null) return;
                await KomTcpAsync(hb, fs, IPAddress.Loopback, CancellationToken.None);
            }
        }

        // ---------- ✉ zaproszenia przez internet ----------
        // Kod zaproszenia: adres pośrednika, jego identyfikator, jednorazowy klucz (token), kto zaprasza. Pośrednik zna tylko
        // skrot klucza (K = SHA256(token)) - z nim nowe urzadzenie dolacza do pośrednika, potem prosi zapraszajacego o kontakt
        // (6-cyfrowy kod na obu ekranach i zgoda). Zaproszenie dziala raz i wygasa po 7 dniach.
        sealed class KomInvite { public byte[] K; public string By; public long Exp; }
        readonly ConcurrentDictionary<string, KomInvite> _komInvites = new ConcurrentDictionary<string, KomInvite>();

        // 🔗 „Mój adres Velivo” (jak numer w WhatsApp): ktoś spoza kontaktów, kto ma link, wysyła prośbę; właściciel adresu (np. telefon)
        // decyduje Dodaj / Odrzuć. Pośrednik tylko przekazuje: sekret linku (od właściciela), prośby i odpowiedzi; nowa osoba dostaje wyłącznie komunikator
        // (rozmowy szyfrowane kluczem z wymiany kluczy z właścicielem; sieć domowa, filmy i synchronizacja - nie)
        internal const string KomAddrTag = "\u0001velivo-adres\n", KomAskTag = "\u0001velivo-prosba\n", KomAskOkTag = "\u0001velivo-prosba-ok\n";
        readonly ConcurrentDictionary<string, string> _komAskKeys = new ConcurrentDictionary<string, string>();   // właściciel -> sekret linku (base64)
        readonly ConcurrentDictionary<string, JsonObject> _komAskReq = new ConcurrentDictionary<string, JsonObject>();   // proszący -> {pub, name, owner}
        readonly ConcurrentDictionary<string, JsonObject> _komAskRes = new ConcurrentDictionary<string, JsonObject>();   // proszący -> odpowiedź właściciela
        async Task KomAskServe(JsonNode h, byte[] mac, Stream s, CancellationToken token)
        {
            string rid = (string)h["Id"] ?? "", owner = (string)h["To"] ?? "", pub = (string)h["Pub"] ?? ""; long ts = (long?)h["Ts"] ?? 0;
            if (rid.Length != 32 || pub.Length is 0 or > 400 || !_komAskKeys.TryGetValue(owner, out var sb) || Math.Abs(ts - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) > 300000) return;
            var key = Convert.FromBase64String(sb);
            var exp = KomHmac(key, "velivo-ask\n" + rid + "\n" + owner + "\n" + pub + "\n" + ts);
            if (mac == null || mac.Length != exp.Length || !CryptographicOperations.FixedTimeEquals(mac, exp)) return;   // bez ważnego linku - nic
            JsonObject resp;
            if (_komAskRes.TryGetValue(rid, out var r) && (string)r["owner"] == owner) { resp = r; _komAskRes.TryRemove(rid, out _); await Dispatcher.InvokeAsync(KomSave); }
            else
            {
                resp = new JsonObject { ["wait"] = true };
                bool fresh = !_komAskReq.ContainsKey(rid);
                if (_komAskReq.Count < 200) _komAskReq[rid] = new JsonObject { ["pub"] = pub, ["name"] = KomClean((string)h["Name"]), ["owner"] = owner };
                if (fresh) await Dispatcher.InvokeAsync(() =>
                {   // prośba do właściciela (jak zwykła wiadomość: od razu albo przez skrzynkę 📬); właściciel to ten komputer - pytanie tutaj
                    var ask = new JsonObject { ["id"] = rid, ["name"] = KomClean((string)h["Name"]), ["pub"] = pub }.ToJsonString();
                    if (owner == _komId) { KomAskIn(null, ask); return; }
                    var oc = KomContactById(owner); if (oc == null) return;
                    KomDeliver(oc, new KomMsg { Mine = true, Text = KomAskTag + ask, Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mid = Guid.NewGuid().ToString("N") });
                    KomSave();
                });
            }
            var rb = Encoding.UTF8.GetBytes(resp.ToJsonString());
            await KomWriteBlock(s, rb, token); await KomWriteBlock(s, KomHmac(key, Encoding.UTF8.GetString(rb)), token);
        }
        // odpowiedź właściciela: Dodaj - pośrednik też dodaje proszącego (tylko komunikator), żeby mógł się łączyć przez internet
        void KomAskAnswer(KomContact owner, string body)
        {
            JsonNode j; try { j = JsonNode.Parse(body); } catch (Exception) { return; }
            var rid = (string)j?["id"] ?? "";
            if (!_komAskReq.TryRemove(rid, out var req) || (string)req["owner"] != owner.Id) return;
            if ((bool?)j["ok"] != true) { _komAskRes[rid] = new JsonObject { ["owner"] = owner.Id, ["ok"] = false }; KomSave(); return; }
            var reqPub = Convert.FromBase64String((string)req["pub"]);
            using var me = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var myPub = me.ExportSubjectPublicKeyInfo();
            byte[] k; try { k = KomAgree(me, reqPub, "velivo-prosba", reqPub, myPub); } catch (Exception) { return; }
            if (KomContactById(rid) == null) _komContacts.Add(new KomContact { Id = rid, Name = (string)req["name"] is { Length: > 0 } n ? n : "Velivo", Key = k, Addr = "", Device = (string)req["name"] ?? "", Own = false });
            _komAskRes[rid] = new JsonObject { ["owner"] = owner.Id, ["ok"] = true, ["ownerName"] = (string)j["name"] ?? owner.Name, ["ownerPub"] = (string)j["pub"],
                ["relay"] = _komId, ["relayName"] = _lanDeviceName, ["relayPub"] = Convert.ToBase64String(myPub), ["relayAddr"] = _komRelayHost + ":" + _komRelayPort };
            KomSave(); _komRefresh?.Invoke();
        }
        static string KomInviteId(byte[] k) => Convert.ToHexString(k, 0, 8);

        // 🔗 mój adres na Windows (jak w telefonie): link dla każdego; prośba idzie przez mojego pośrednika albo ten komputer nim jest
        string KomMyAddress(bool reset)
        {
            var r = KomLiveRelay();   // mój domowy, który działa
            // ten komputer jest serwerem - link zawsze do niego (także bez sprawdzonego adresu domu: adres domu od mojego drugiego serwera, ten sam dom),
            // wcześniej link z CHUWI prowadził do NUCBOX i prośby pokazywały się na NUCBOX
            var host = _komRelayHost.Length > 0 ? _komRelayHost : r?.Relay is string rr0 && rr0.LastIndexOf(':') > 0 ? rr0.Substring(0, rr0.LastIndexOf(':')) : "";
            bool self = _komRelayListener != null && host.Length > 0;
            var addr = self ? host + ":" + _komRelayPort : r?.Relay;
            if (addr == null) return null;
            var sk = (string)_komOpt["moj-adres"];
            if (sk == null || reset) { sk = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)); _komOpt["moj-adres"] = sk; }
            if (self) _komAskKeys[_komId] = sk;   // pośrednik zna sekret - wpuszcza tylko prośby z tym linkiem
            var all = _komContacts.Where(c => !string.IsNullOrEmpty(c.Relay) && !c.IsGroup && (c.Dom || (!self && c == r))).ToList();   // główny z linku i każdy mój pośrednik z domu - każdy zna sekret (NUCBOX, CHUWI) zna sekret - prośba dojdzie, gdy jeden nie działa
            foreach (var rr in all) KomDeliver(rr, new KomMsg { Mine = true, Text = KomAddrTag + sk, Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mid = Guid.NewGuid().ToString("N") });
            KomSave();
            var j = new JsonObject { ["a"] = addr, ["o"] = _komId, ["s"] = sk, ["n"] = _lanDeviceName,
                ["r"] = new JsonArray(all.Select(c => c.Relay).Where(x => x != addr).Distinct().Take(4).Select(x => (JsonNode)x).ToArray()) };   // zapasowe adresy pośredników
            return "VELIVO-KONTAKT:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(j.ToJsonString())).Replace('+', '-').Replace('/', '_');
        }
        // prośba od kogoś z moim linkiem (relay == null - ten komputer jest pośrednikiem): zgoda właściciela
        readonly HashSet<string> _komAskOpen = new HashSet<string>();   // pytania na ekranie (wątek okna)
        void KomAskIn(KomContact relay, string body)
        {
            JsonNode j; try { j = JsonNode.Parse(body); } catch (Exception) { return; }
            var id = (string)j?["id"] ?? ""; var pub = (string)j["pub"] ?? ""; var name = KomClean((string)j["name"]); if (name.Length == 0) name = "Velivo";
            if (id.Length != 32 || pub.Length == 0 || KomContactById(id) != null) return;
            KomPlaySound();
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (KomContactById(id) != null || !_komAskOpen.Add(id)) return;   // ta sama prośba przez drugiego pośrednika - jedno pytanie
                bool yes; try { yes = MessageBox.Show((Window)_komWindow ?? PairDialogOwner(), name + L.T(" prosi o kontakt (przez Twój adres Velivo).\n\nDodać? Dostanie tylko komunikator – bez sieci domowej."),
                    "🔗 " + L.T("Prośba o kontakt"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes; }
                finally { _komAskOpen.Remove(id); }
                KomAskReply(relay, id, name, pub, yes);
            }));
        }
        void KomAskReply(KomContact relay, string id, string name, string pub, bool yes)
        {
            if (relay == null && !_komAskReq.TryRemove(id, out _)) return;
            if (!yes)
            {
                if (relay == null) { _komAskRes[id] = new JsonObject { ["owner"] = _komId, ["ok"] = false }; KomSave(); }
                else KomDeliver(relay, new KomMsg { Mine = true, Text = KomAskOkTag + new JsonObject { ["id"] = id, ["ok"] = false }.ToJsonString(), Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mid = Guid.NewGuid().ToString("N") });
                return;
            }
            using var me = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var myPub = me.ExportSubjectPublicKeyInfo(); byte[] reqPub, k;
            try { reqPub = Convert.FromBase64String(pub); k = KomAgree(me, reqPub, "velivo-prosba", reqPub, myPub); } catch (Exception) { return; }
            _komContacts.Add(new KomContact { Id = id, Name = name, Key = k, Addr = "", Device = name, Own = false, Relay = relay?.Relay });
            var mp = Convert.ToBase64String(myPub);
            if (relay == null) _komAskRes[id] = new JsonObject { ["owner"] = _komId, ["ok"] = true, ["ownerName"] = _lanDeviceName, ["ownerPub"] = mp, ["relay"] = _komId, ["relayName"] = _lanDeviceName, ["relayPub"] = mp, ["relayAddr"] = _komRelayHost + ":" + _komRelayPort };
            else KomDeliver(relay, new KomMsg { Mine = true, Text = KomAskOkTag + new JsonObject { ["id"] = id, ["ok"] = true, ["pub"] = mp, ["name"] = _lanDeviceName }.ToJsonString(), Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mid = Guid.NewGuid().ToString("N") });
            KomSave(); _komRefresh?.Invoke(); KomAnnounce();
        }
        // ja proszę: link od kogoś - prośba przez jego pośrednika; odpowiedź sprawdzana co minutę (on może być poza siecią)
        bool KomRequestByLink(string link)
        {
            try
            {
                var s = link.Trim(); var at = s.IndexOf("VELIVO-KONTAKT:", StringComparison.Ordinal); if (at >= 0) s = s.Substring(at + 15).Trim();
                s = s.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";   // adres w środku wiadomości - tylko on
                s = s.Replace('-', '+').Replace('_', '/'); s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
                var j = JsonNode.Parse(Convert.FromBase64String(s)) as JsonObject;
                var o = (string)j?["o"]; var a = (string)j?["a"];
                if (o?.Length != 32 || o == _komId || a == null || !a.Contains(':') || string.IsNullOrEmpty((string)j["s"])) return false;
                using var me = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                j["priv"] = Convert.ToBase64String(me.ExportPkcs8PrivateKey()); j["pub"] = Convert.ToBase64String(me.ExportSubjectPublicKeyInfo());
                if (_komOpt["moje-prosby"] is not JsonArray l) _komOpt["moje-prosby"] = l = new JsonArray();
                l.Add(j); KomSave(); _ = KomPollAsks();
                return true;
            }
            catch (Exception) { return false; }
        }
        bool _komPolling;
        async Task KomPollAsks()
        {
            if (_komOpt["moje-prosby"] is not JsonArray l || l.Count == 0 || _komPolling) return;
            _komPolling = true;
            try
            {
                foreach (var j in l.ToList())
                {
                    bool done = false;
                    try
                    {
                        string hp = (string)j["a"], o = (string)j["o"], pub = (string)j["pub"]; var sk = Convert.FromBase64String((string)j["s"]); long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        TcpClient cl = null;   // pierwszy działający pośrednik z linku (zapasowe „r”, gdy główny nie działa) - zapamiętany jako główny
                        foreach (var cand in new[] { hp }.Concat((j["r"] as JsonArray)?.Select(x => (string)x).Where(x => x != null && x.Contains(':')) ?? Enumerable.Empty<string>()))
                        {
                            var c1 = new TcpClient();
                            try { using var ct = CancellationTokenSource.CreateLinkedTokenSource(cts.Token); ct.CancelAfter(6000); await c1.ConnectAsync(cand.Substring(0, cand.LastIndexOf(':')), int.Parse(cand.Substring(cand.LastIndexOf(':') + 1)), ct.Token); cl = c1; if (cand != hp) { j["a"] = cand; hp = cand; } break; }
                            catch (Exception) { c1.Dispose(); }
                        }
                        if (cl == null) continue;
                        using var _cl = cl;
                        var st = cl.GetStream();
                        var hello = new JsonObject { ["Type"] = "relay-ask", ["Id"] = _komId, ["To"] = o, ["Pub"] = pub, ["Name"] = _lanDeviceName, ["Ts"] = ts };
                        await KomWriteBlock(st, Encoding.UTF8.GetBytes(hello.ToJsonString()), cts.Token);
                        await KomWriteBlock(st, KomHmac(sk, "velivo-ask\n" + _komId + "\n" + o + "\n" + pub + "\n" + ts), cts.Token);
                        var rb = await KomReadBlock(st, 8192, cts.Token); var mac = await KomReadBlock(st, 64, cts.Token);
                        var exp = rb == null ? null : KomHmac(sk, Encoding.UTF8.GetString(rb));
                        if (mac == null || exp == null || mac.Length != exp.Length || !CryptographicOperations.FixedTimeEquals(mac, exp)) continue;
                        var r = JsonNode.Parse(rb);
                        if ((bool?)r?["wait"] == true) continue;
                        done = true;
                        var who = KomClean((string)r["ownerName"]) is { Length: > 0 } wn ? wn : KomClean((string)j["n"]);
                        if ((bool?)r["ok"] != true) { KomInfo("🔗 " + who + L.T(" nie przyjął prośby")); continue; }
                        using var me = ECDiffieHellman.Create(); me.ImportPkcs8PrivateKey(Convert.FromBase64String((string)j["priv"]), out _);
                        var myPub = Convert.FromBase64String(pub);
                        foreach (var (id, nm, pk, rl) in new[] { ((string)r["owner"], who, (string)r["ownerPub"], hp), ((string)r["relay"], KomClean((string)r["relayName"]), (string)r["relayPub"], (string)r["relayAddr"] is string ra && ra.Contains(':') ? ra : hp) })
                        {
                            if (id == null || id.Length != 32 || KomContactById(id) != null || pk == null) continue;
                            var op = Convert.FromBase64String(pk);
                            _komContacts.Add(new KomContact { Id = id, Name = nm.Length > 0 ? nm : "Velivo", Key = KomAgree(me, op, "velivo-prosba", myPub, op), Addr = "", Device = nm, Own = false, Relay = rl });
                        }
                        KomRefreshAfterAsk(who);
                    }
                    catch (Exception) { }
                    finally { if (done) l.Remove(j); }
                }
                KomSave();
            }
            finally { _komPolling = false; }
        }
        void KomRefreshAfterAsk(string who) { KomSave(); _komRefresh?.Invoke(); KomAnnounce(); KomInfo("👤 " + who + L.T(" przyjął prośbę – możecie pisać")); }

        // zaproszenie od kontaktu (pakiet p2p-invite do pośrednika; klucz zaszyfrowany kluczem kontaktu)
        void KomInviteStore(JsonNode p, KomContact c)
        {
            if (!_komRelayOn || c == null || (string)p["to"] != _komId) return;
            var n = KomB64((string)p["n"]); var ek = KomB64((string)p["ek"]); long exp = (long?)p["exp"] ?? 0;
            if (n.Length != 12 || ek.Length != 48 || exp < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() || exp > DateTimeOffset.UtcNow.AddDays(8).ToUnixTimeMilliseconds()) return;
            var k = new byte[32];
            try { using (var g = new AesGcm(c.Key, 16)) g.Decrypt(n, ek.AsSpan(0, 32), ek.AsSpan(32, 16), k, Encoding.ASCII.GetBytes("velivo-invite\n" + exp)); }
            catch (CryptographicException) { return; }
            _komInvites[KomInviteId(k)] = new KomInvite { K = k, By = c.Id, Exp = exp };
            KomSave();
        }


        // dolaczenie z kodem: do pośrednika (bez pytania - zaproszenie), potem prośba o kontakt do zapraszajacego
        async Task<string> KomJoin(string code, Action<string> showCode)
        {
            JsonNode j;
            try
            {
                var b = code.Trim(); var at = b.IndexOf("VELIVO1:", StringComparison.OrdinalIgnoreCase); if (at < 0) return L.T("To nie jest kod zaproszenia Velivo");
                b = b.Substring(at + 8).Trim().Split(' ', '\n', '\r')[0].Replace('-', '+').Replace('_', '/'); b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
                j = JsonNode.Parse(Convert.FromBase64String(b));
            }
            catch (Exception) { return L.T("To nie jest kod zaproszenia Velivo"); }
            string host = (string)j["h"], relayId = (string)j["r"], inviter = (string)j["i"], name = KomClean((string)j["n"]);
            var token = KomB64((string)j["t"]);
            if (host == null || relayId?.Length != 32 || inviter?.Length != 32 || token.Length != 16) return L.T("To nie jest kod zaproszenia Velivo");
            if (inviter == _komId) return L.T("To Twoje własne zaproszenie");
            var k = SHA256.HashData(token);
            // 1) pośrednik - nowy kontakt bez pytania (zaproszenie); z nim rozmowy poza domem
            var tmp = new KomContact { Id = relayId, Key = k, Relay = host, Name = "Velivo" };
            using (var fs = await KomRelayConnect(tmp, "invite", KomInviteId(k), null, CancellationToken.None))
            {
                if (fs == null) return L.T("Nie można połączyć z pośrednikiem – zaproszenie wygasło albo dom jest niedostępny");
                var r1 = await KomPairOn(fs, relayId, "Velivo", "", host, true, _ => { }, CancellationToken.None);
                if (r1 != "ok") return r1;
            }
            if (inviter == relayId) { KomUpCheckNow(); return "ok"; }
            // 2) zapraszajacy - prośba z kodem do porownania, zgoda po jego stronie
            var relay = KomContactById(relayId);
            using (var fs2 = await KomRelayConnect(relay, "tunnel", null, inviter, CancellationToken.None))
            {
                if (fs2 == null) return L.T("Nie można połączyć z pośrednikiem");
                var r2 = await KomPairOn(fs2, inviter, name, "", null, true, showCode, CancellationToken.None);
                KomUpCheckNow();
                return r2 == "ok" ? "ok" : (r2 == L.T("odrzucono") ? r2 : L.T("Zapraszający jest teraz niedostępny – spróbuj, gdy będzie w sieci (") + r2 + ")");
            }
        }

        void KomUpCheckNow() { Dispatcher.BeginInvoke(new Action(() => { _komUpForce = true; KomUpCheck(); })); }
        bool _komUpForce;

        void KomJoinDialog(Window owner)
        {
            var box = new TextBox { MinWidth = 420, MaxWidth = 460, Height = 70, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Margin = new Thickness(0, 8, 0, 8) };
            try { if (Clipboard.ContainsText() && (Clipboard.GetText().Contains("VELIVO1:") || Clipboard.GetText().Contains("VELIVO-KONTAKT:"))) box.Text = Clipboard.GetText().Trim(); } catch (Exception) { }
            var ok = new Button { Content = L.T("Dodaj"), IsDefault = true, Padding = new Thickness(18, 4, 18, 4), HorizontalAlignment = HorizontalAlignment.Right };
            var d = new Window { Title = "🔗 " + L.T("Mam od kogoś adres albo zaproszenie"), SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel { Margin = new Thickness(16), Children = { new TextBlock { Text = L.T("Wklej adres (VELIVO-KONTAKT:…) albo zaproszenie (VELIVO1:…):") }, box, ok } } };
            KomStyle(d);
            ok.Click += (s, e) => d.DialogResult = true;
            if (d.ShowDialog() != true || box.Text.Trim().Length == 0) return;
            var code = box.Text;
            if (code.Contains("VELIVO-KONTAKT:")) { KomInfo(KomRequestByLink(code.Trim()) ? "👤 " + L.T("Prośba wysłana – kontakt pojawi się, gdy ta osoba ją przyjmie") : L.T("To nie jest adres Velivo")); return; }   // 🔗 czyjś adres
            KomInfo(L.T("✉ Łączę z zapraszającym…"), 0);
            _ = Dispatcher.InvokeAsync(async () =>
            {
                Window codeWin = null;
                var r = await KomJoin(code, c => Dispatcher.Invoke(() => codeWin = KomCodeWindow(owner, L.T("✉ Zaproszenie"), c)));
                codeWin?.Close();
                KomInfo(r == "ok" ? L.T("✉ Dodano kontakt – możecie rozmawiać przez internet") : L.T("✉ Nie dodano: ") + r);
                if (r == "ok") _komRefresh?.Invoke();
            });
        }

        // ---------- automatycznie: adres domu i port w routerze (bez wiedzy technicznej) ----------
        // UPnP: router podaje swoj adres w internecie i otwiera port na ten komputer. Gdy router tego nie umie - adres
        // z publicznej uslugi (api.ipify.org), port trzeba wtedy otworzyc recznie. Adres sprawdzany co godzine (zmiana IP).
        bool _komRelayPortFixed;   // port wpisany recznie (inny niz 41920) - Velivo go nie zmienia
        bool _komRelayAuto = true; string _komRelayDns = "";   // ostrzeżenie: wpisana nazwa nie wskazuje na dom   // adres ustalany automatycznie (pusty albo nie wpisany recznie)
        string _komRelayRouter = "";  // co zrobil router (do okna)
        System.Windows.Threading.DispatcherTimer _komRelayAutoTimer;

        DateTime _komRelayMoved;   // zmiana portu najwyzej raz na godzine (bez przeskakiwania 41920 <-> 41921 w kolko)
        bool _komRelayBusy;
        async Task KomRelayAutoAsync()
        {
            if (_komRelayBusy) return;   // trwa inne sprawdzanie - ono ustawi wynik
            if (!_komRelayOn || _komUp != null) { if (_komRelayRouter == L.T("Sprawdzam porty…")) { _komRelayRouter = ""; KomRelayStatus(); } return; }   // nie sprawdzam - bez wiecznego „Sprawdzam”
            _komRelayBusy = true;
            var once = KomRelayAutoOnce();
            try
            {   // router bez odpowiedzi (UPnP) nie może trzymać „Sprawdzam porty…” w nieskończoność (Ryzen: 12 min)
                if (await Task.WhenAny(once, Task.Delay(TimeSpan.FromSeconds(60))) != once)
                {   // router zawieszony - wynik słowami i następne sprawdzenie może ruszyć (zawieszone kończy się samo w tle)
                    _komRelayRouter = L.T("Nie udało się sprawdzić portów – router nie odpowiedział w 60 s"); KomRelayStatus();
                    _ = once.ContinueWith(t => { if (t.Exception != null) App.LogError(t.Exception); }, TaskScheduler.Default);
                    return;
                }
                await once;
            }
            catch (Exception ex) { App.LogError(ex); }
            finally { _komRelayBusy = false; if (_komRelayRouter == L.T("Sprawdzam porty…")) { _komRelayRouter = ""; KomRelayStatus(); } }   // zakończone bez wyniku (np. zmiana portu) - bez wiecznego „Sprawdzam”
        }
        async Task KomRelayAutoOnce()
        {
            // port, na który ktoś już się łączył (przekierowanie w routerze, telefony go znają), zostaje na zawsze - zmiana odcinała zapasowy serwer
            bool canMove = DateTime.UtcNow - _komRelayMoved > TimeSpan.FromHours(1) && _komRelayAcceptAt == default;   // poza domem (laptop) - nie ustalamy adresu obcej sieci
            var port = _komRelayPort;
            var (ext, mapped, note) = await Task.Run(() => KomUpnpAsync(port));
            // dawne porty tego serwera (np. NUCBOX 41920 -> 41921): starsze telefony (mama) znają tylko stary - w routerze stary port też prowadzi tutaj
            if (_komOpt["ports"] is not System.Text.Json.Nodes.JsonArray used) _komOpt["ports"] = used = new System.Text.Json.Nodes.JsonArray();
            if (!used.Any(x => (int?)x == port)) { used.Add(port); KomSave(); }
            foreach (var old in used.Select(x => (int?)x ?? 0).Where(x => x != port && x >= 41920 && x <= 41929).ToList()) await Task.Run(() => KomUpnpAsync(old, port));
            if (ext != null && KomPrivateIp(ext)) ext = null;   // router za routerem operatora podaje swój adres wewnętrzny (np. 192.168.1.x) - z internetu nieosiągalny; prawdziwy z ipify
            // wczesniej przeniesiony na inny port, a 41920 znow prowadzi tu (np. reczne przekierowanie) - wracamy na 41920
            if (port != 41920 && _komRelayAuto && !_komRelayPortFixed && canMove && (await Task.Run(() => KomUpnpAsync(41920))).mapped) { _komRelayMoved = DateTime.UtcNow; _komRelayPort = 41920; KomSave(); KomRelayStart(); KomAnnounce(); return; }
            // port zajety w routerze (np. inny komputer w domu) - nastepny wolny 41920-41929, sam
            if (!mapped && note == "taken" && _komRelayAuto && !_komRelayPortFixed && canMove)
                for (int p2 = 41920; p2 <= 41929 && !mapped; p2++)
                {
                    if (p2 == port) continue;
                    var res = await Task.Run(() => KomUpnpAsync(p2));
                    if (res.mapped) { _komRelayMoved = DateTime.UtcNow; mapped = true; note = ""; ext = res.ext ?? ext; _komRelayPort = p2; KomSave(); KomRelayStart(); KomAnnounce(); return; }
                }
            if (ext == null)
                try { using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) }) ext = (await http.GetStringAsync("https://api.ipify.org")).Trim(); }
                catch (Exception) { }
            if (ext != null && !IPAddress.TryParse(ext, out _)) ext = null;
            if (_komRelayAuto && !IPAddress.TryParse(_komRelayHost, out _)) _komRelayHost = ext ?? "";
            // wpisana nazwa (No-IP itp.) - czy naprawdę wskazuje na obecny adres domu
            _komRelayDns = "";
            if (!_komRelayAuto && _komRelayHost.Length > 0 && !IPAddress.TryParse(_komRelayHost, out _) && ext != null)
                try { var ips = await System.Net.Dns.GetHostAddressesAsync(_komRelayHost); if (!ips.Any(a => a.ToString() == ext)) _komRelayDns = "⚠ " + _komRelayHost + L.T(" wskazuje na ") + (ips.Length > 0 ? ips[0].ToString() : "?") + L.T(", a adres domu to ") + ext + L.T(" – popraw nazwę u dostawcy (np. No-IP) albo zostaw pole puste"); }
                catch (Exception) { _komRelayDns = "⚠ " + _komRelayHost + L.T(" – ta nazwa nie istnieje (sprawdź u dostawcy, np. No-IP) albo zostaw pole puste"); }   // pamiętana stara nazwa z ręcznego wpisu - nie pokazuj jej jako adresu domu
            // 🌍 zapasowe wejście przez port 443 (sieci blokujące nietypowe porty) - w routerze 443 -> port pośrednika na tym komputerze
            var alt = await Task.Run(() => KomUpnpAsync(KomRelayAltPort, port));
            // router bez UPnP (przekierowanie ręczne): prawdziwy test - połączenie z adresem domu na 443 i odpowiedź naszego pośrednika
            _komRelayAltOther = false;
            if (!alt.mapped && _komRelayHost.Length > 0) { alt.mapped = await KomRelayProbe(_komRelayHost, KomRelayAltPort); _komRelayAltOther = _komProbeOther; }
            _komRelayAlt = alt.mapped; _komRelayUpnp = ext != null || mapped || note == "taken";   // router odpowiada na UPnP - umiemy sprawdzić przekierowanie
            // port główny: prawdziwy test z adresu domu (jak 443) - UPnP mówi tylko, co router zapamiętał, nie czy działa przekierowanie ręczne
            var main = _komRelayHost.Length > 0 ? await KomRelayProbe(_komRelayHost, port) : (bool?)null;
            _komRelayRouter = main == true ? L.T("Port ") + port + L.T(": otwarty – sprawdzone połączeniem z adresu domu")
                : main == false ? L.T("Port ") + port + L.T(": z internetu nie odpowiada – przekieruj w routerze TCP ") + port + L.T(" na ") + KomLanIp() + L.T(" i zezwól w zaporze")
                : mapped ? L.T("Router: port otwarty automatycznie") : (note == "taken" ? L.T("Router: port już przekierowany (ręcznie)") : L.T("Router nie otworzył portu sam – przekieruj go w routerze na ten komputer"));
            if (_komRelayAuto && ext != null && ext != _komRelayHost) { _komRelayHost = ext; KomSave(); KomAnnounce(); }   // nowy adres - urzadzenia w domu dostaja go od razu
            KomRelayStatus();
        }

        static bool KomPrivateIp(string ip) => IPAddress.TryParse(ip, out var a) && a.GetAddressBytes() is var b && b.Length == 4 &&
            (b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] < 128) || (b[0] == 169 && b[1] == 254));
        // UPnP (IGD): adres zewnetrzny routera i przekierowanie portu TCP na ten komputer; note: "taken" - port juz zajety (np. recznie)
        static async Task<(string ext, bool mapped, string note)> KomUpnpAsync(int port, int intPort = 0, bool takeOver = false)
        {
            try
            {
                string location = null;
                using (var udp = new UdpClient(AddressFamily.InterNetwork))
                {
                    var req = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n");
                    await udp.SendAsync(req, req.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));
                    var end = DateTime.UtcNow.AddSeconds(3);
                    while (location == null && DateTime.UtcNow < end)
                    {
                        var t = udp.ReceiveAsync();
                        if (await Task.WhenAny(t, Task.Delay(end - DateTime.UtcNow)) != t) { _ = t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted); break; }   // router nie odpowiedzial - bez wpisu w dzienniku
                        var m = System.Text.RegularExpressions.Regex.Match(Encoding.ASCII.GetString(t.Result.Buffer), @"(?im)^location:\s*(\S+)");
                        if (m.Success) location = m.Groups[1].Value;
                    }
                }
                if (location == null) return (null, false, "");
                using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) })
                {
                    var desc = await http.GetStringAsync(location);
                    string svc = null, ctrl = null;
                    foreach (System.Text.RegularExpressions.Match s in System.Text.RegularExpressions.Regex.Matches(desc, @"<service>(.*?)</service>", System.Text.RegularExpressions.RegexOptions.Singleline))
                    {
                        var st = System.Text.RegularExpressions.Regex.Match(s.Value, @"<serviceType>(.*?)</serviceType>").Groups[1].Value.Trim();
                        if (st.Contains("WANIPConnection") || st.Contains("WANPPPConnection")) { svc = st; ctrl = System.Text.RegularExpressions.Regex.Match(s.Value, @"<controlURL>(.*?)</controlURL>").Groups[1].Value.Trim(); break; }
                    }
                    if (svc == null || ctrl.Length == 0) return (null, false, "");
                    var url = new Uri(new Uri(location), ctrl);
                    async Task<(int code, string body)> Soap(string action, string args)
                    {
                        var body = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:" + action + " xmlns:u=\"" + svc + "\">" + args + "</u:" + action + "></s:Body></s:Envelope>";
                        var rq = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, url) { Content = new System.Net.Http.StringContent(body, Encoding.UTF8, "text/xml") };
                        rq.Headers.TryAddWithoutValidation("SOAPAction", "\"" + svc + "#" + action + "\"");
                        using (var rs = await http.SendAsync(rq)) return ((int)rs.StatusCode, await rs.Content.ReadAsStringAsync());
                    }
                    var (c1, b1) = await Soap("GetExternalIPAddress", "");
                    var ext = c1 == 200 ? System.Text.RegularExpressions.Regex.Match(b1, @"<NewExternalIPAddress>(.*?)</NewExternalIPAddress>").Groups[1].Value.Trim() : null;
                    if (string.IsNullOrEmpty(ext) || ext.StartsWith("0.")) ext = null;
                    // adres tego komputera w sieci domowej (ta karta, ktora widzi router)
                    string local;
                    using (var probe = new UdpClient(AddressFamily.InterNetwork)) { probe.Connect(url.Host, url.Port); local = ((IPEndPoint)probe.Client.LocalEndPoint).Address.ToString(); }
                    // port już przekierowany na INNY komputer w domu (np. NUCBOX) - nie nadpisujemy (część routerów nadpisuje bez błędu 718 i drugi pośrednik zabierał porty pierwszemu)
                    var (c0, b0) = await Soap("GetSpecificPortMappingEntry", "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + port + "</NewExternalPort><NewProtocol>TCP</NewProtocol>");
                    var who0 = c0 == 200 ? System.Text.RegularExpressions.Regex.Match(b0, @"<NewInternalClient>(.*?)</NewInternalClient>").Groups[1].Value.Trim() : "";
                    // 🔁 zastępstwo: działający serwer przejmuje port wyłączonego (takeOver), a ten po powrocie od razu go odbiera (opis „Velivo zastępstwo”)
                    if (who0.Length > 0 && who0 != local && (takeOver || b0.Contains(KomTakeDesc))) await Soap("DeletePortMapping", "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + port + "</NewExternalPort><NewProtocol>TCP</NewProtocol>");
                    else if (who0.Length > 0 && who0 != local) return (ext, false, "taken");
                    var (c2, b2) = await Soap("AddPortMapping", "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + port + "</NewExternalPort><NewProtocol>TCP</NewProtocol><NewInternalPort>" + (intPort > 0 ? intPort : port) +
                        "</NewInternalPort><NewInternalClient>" + local + "</NewInternalClient><NewEnabled>1</NewEnabled><NewPortMappingDescription>" + (takeOver ? KomTakeDesc : "Velivo") + "</NewPortMappingDescription><NewLeaseDuration>0</NewLeaseDuration>");
                    if (c2 != 200 && b2.Contains("718"))
                    {
                        // juz przekierowany (np. recznie w routerze) - jesli na ten komputer, to dziala
                        var (c3, b3) = await Soap("GetSpecificPortMappingEntry", "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + port + "</NewExternalPort><NewProtocol>TCP</NewProtocol>");
                        var who = c3 == 200 ? System.Text.RegularExpressions.Regex.Match(b3, @"<NewInternalClient>(.*?)</NewInternalClient>").Groups[1].Value.Trim() : "";
                        var toPort = c3 == 200 ? System.Text.RegularExpressions.Regex.Match(b3, @"<NewInternalPort>(.*?)</NewInternalPort>").Groups[1].Value.Trim() : "";
                        bool ours = who == local && (intPort <= 0 || toPort == intPort.ToString());
                        return (ext, ours, ours ? "" : "taken");
                    }
                    return (ext, c2 == 200, "");
                }
            }
            catch (Exception) { return (null, false, ""); }
        }

        // test przekierowania: połączenie z adresem domu na podanym porcie liczy się tylko wtedy, gdy przyjął je NASZ pośrednik
        // (nie np. panel routera na 443); router bez „pętli” do własnego adresu - wynik „nie wiadomo” (false), napis mówi, jak sprawdzić
        DateTime _komRelayAcceptAt;
        bool _komProbeOther, _komRelayAltOther;   // test portu: połączenie przyjął inny komputer w domu
        async Task<bool> KomRelayProbe(string host, int port)
        {
            _komProbeOther = false;
            var t0 = DateTime.UtcNow;
            try
            {
                using var cl = new TcpClient(); using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await cl.ConnectAsync(host, port, cts.Token);
                await Task.Delay(700);
                if (_komRelayAcceptAt > t0) return true;
                _komProbeOther = true; return false;   // port odpowiada, ale to nie ten komputer (np. 443 na CHUWI)
            }
            catch (Exception) { return false; }
        }

        // ---------- okno ustawien ----------
        TextBlock _komRelayStatusText; bool _komRelayAlt, _komRelayUpnp;
        // 🛡 zapora Windows: serwer ma przyjmować połączenia w każdej sieci (kabel, Wi-Fi, także „publicznej”). Windows sam pyta tylko o jedną sieć,
        // a „Anuluj” w jego okienku zostawia regułę blokującą, która wygrywa z każdą zezwalającą - router przekierowuje, a Windows odrzuca (NUCBOX na Wi-Fi)
        static bool KomFirewallOk(int port)
        {
            try
            {
                var t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2"); if (t == null) return true;
                dynamic pol = Activator.CreateInstance(t); string me = Environment.ProcessPath; bool allow = false;
                foreach (dynamic r in pol.Rules)
                {
                    if ((int)r.Direction != 1 || !(bool)r.Enabled) continue;   // tylko włączone reguły wejściowe
                    string app = r.ApplicationName; bool mine = app != null && string.Equals(Environment.ExpandEnvironmentVariables(app), me, StringComparison.OrdinalIgnoreCase);
                    if (!mine && app != null) continue;
                    int act = r.Action, prof = r.Profiles, proto = r.Protocol;
                    if (mine && act == 0) return false;   // blokada tego programu
                    if (act != 1 || (prof & 7) != 7 || (proto != 6 && proto != 256)) continue;   // zezwolenie we wszystkich sieciach, TCP
                    string lp = r.LocalPorts ?? "*";
                    if (lp == "*" ? mine : KomPortsCover(lp, port)) allow = true;
                }
                return allow;
            }
            catch (Exception) { return true; }   // nie da się sprawdzić - nie pytamy
        }
        // „41920-41929,443” obejmuje port? (porty reguły zapory)
        internal static bool KomPortsCover(string localPorts, int port) =>
            (localPorts ?? "").Split(',').Any(x => x.Trim().Split('-') is var ab && int.TryParse(ab[0], out var a) && int.TryParse(ab[^1], out var b) && port >= a && port <= b);
        DateTime _komFwAsked;
        void KomFirewallCheck()
        {
            if (!_komRelayOn || DateTime.UtcNow - _komFwAsked < TimeSpan.FromHours(6)) return;
            int port = _komRelayPort;
            _ = Task.Run(() =>
            {
                if (KomFirewallOk(port)) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (DateTime.UtcNow - _komFwAsked < TimeSpan.FromHours(6)) return;
                    _komFwAsked = DateTime.UtcNow;
                    KomInfo(L.T("🛡 Otwieram serwer w zaporze Windows (kabel i Wi-Fi) – Windows zapyta o zgodę: kliknij „Tak”"));
                    var me = Environment.ProcessPath.Replace("'", "''"); var name = "Velivo serwer – " + Path.GetFileNameWithoutExtension(me);
                    // blokady tego programu precz, jedna reguła: ten program, TCP, porty serwera (41920–41929) i 443, każda sieć
                    var ps = "$p='" + me + "'; Get-NetFirewallApplicationFilter -Program $p -ErrorAction SilentlyContinue | Get-NetFirewallRule | Where-Object { $_.Direction -eq 'Inbound' -and $_.Action -eq 'Block' } | Remove-NetFirewallRule; " +
                             "Remove-NetFirewallRule -DisplayName '" + name + "' -ErrorAction SilentlyContinue; New-NetFirewallRule -DisplayName '" + name + "' -Direction Inbound -Program $p -Protocol TCP -LocalPort 41920-41929,443 -Action Allow -Profile Any";
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            using var pr = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe", "-NoProfile -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(ps)))
                                { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
                            pr?.WaitForExit(60000);
                            bool ok = KomFirewallOk(port); App.LogInfo("Pośrednik: zapora Windows " + (ok ? "otwarta" : "nadal blokuje"));
                            Dispatcher.BeginInvoke(new Action(() => { KomInfo(ok ? L.T("🛡 Zapora Windows przepuszcza serwer we wszystkich sieciach") : L.T("🛡 Zapora Windows nadal blokuje serwer – połączenia z internetu nie dojdą")); KomRelayStatus(); }));
                        }
                        catch (Exception) { Dispatcher.BeginInvoke(new Action(() => KomInfo(L.T("🛡 Bez zgody Windows zapora może blokować połączenia z internetu (zapytam ponownie za kilka godzin)")))); }
                    });
                }));
            });
        }
        // adres tego komputera w sieci domowej (na niego przekierowanie w routerze); zmiana sieci (kabel ↔ Wi-Fi) - nowy adres
        static string KomLanIp()
        {
            try
            {
                return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    .Select(n => n.GetIPProperties()).Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                    .SelectMany(p => p.UnicastAddresses).Select(u => u.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "";
            }
            catch (Exception) { return ""; }
        }
        // kabel ↔ Wi-Fi: nowy adres w domu - od razu od nowa router (UPnP) i zapora; router bez UPnP - komunikat, na jaki adres przestawić przekierowanie
        bool _komNetWatch;
        void KomNetWatch()
        {
            if (_komNetWatch) return; _komNetWatch = true;
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (s, e) => Dispatcher.BeginInvoke(new Action(async () =>
            {
                await Task.Delay(5000);   // nowe połączenie dostaje adres po chwili
                if (!_komRelayOn) return;
                var ip = KomLanIp(); var old = (string)_komOpt["lanIp"] ?? "";
                if (ip.Length == 0 || ip == old) return;
                _komOpt["lanIp"] = ip; KomSave();
                if (DateTime.UtcNow - _komFwAsked > TimeSpan.FromMinutes(10)) _komFwAsked = default;   // nowa sieć - zapora sprawdzana od razu
                App.LogInfo("Pośrednik: nowy adres w sieci domowej " + ip + (old.Length > 0 ? " (był " + old + ")" : ""));
                await KomRelayAutoAsync(); KomFirewallCheck(); KomRelayStatus();
                if (old.Length > 0 && !_komRelayUpnp) KomInfo(L.T("⚠ Ten komputer ma nowy adres w sieci domowej: ") + ip + L.T(" (był ") + old + L.T("). W routerze przestaw przekierowanie portów ") + _komRelayPort + L.T(" i 443 na ") + ip);
            }));
        }

        TextBlock _komRelaySummary; string _komLastReject;   // 🔎 kogo serwer ostatnio nie wpuścił i dlaczego - widać w oknie, bez szukania w dzienniku
        void KomRelayStatus()
        {
            if (_komRelaySummary != null)
            {   // jedno zdanie i kolor (zielony - działa, pomarańczowy - do zrobienia, szary - wyłączony); emoji w okienkach Windows są szare, więc kolor zamiast ikon
                bool bad = _komRelayOn && _komRelayListener == null;
                _komRelaySummary.Text = !_komRelayOn ? L.T("Wyłączony – ten komputer nie przekazuje rozmów.")
                    : bad ? L.T("Serwer nie działa – port zajęty przez inny program. Rozwiń „Szczegóły” i wybierz inny port (41920–41929).")
                    : !_komRemote.IsEmpty ? L.T("Działa – przez internet połączonych urządzeń: ") + _komRemote.Count
                    : L.T("Działa w domu. Z internetu nikt się teraz nie łączy – to normalne, gdy wszyscy są w domu albo poza siecią.");
                if (_komRelayOn && _komLastReject != null) _komRelaySummary.Text += "\n" + L.T("Ostatnio nie wpuszczono: ") + _komLastReject;
                if (_komRelayOn && !_komRemote.IsEmpty) _komRelaySummary.Text += "\n" + L.T("Połączone teraz: ") + string.Join(", ", _komRemote.Keys.Select(id => KomContactById(id)?.Name ?? "?"));
                _komRelaySummary.Foreground = new System.Windows.Media.SolidColorBrush(!_komRelayOn ? System.Windows.Media.Color.FromRgb(0x9C, 0xA3, 0xAF) : bad ? System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B) : System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E));
            }
            if (_komRelayStatusText == null) return;
            var lan = _komRelayOn ? KomLanIp() : "";
            _komRelayStatusText.Text = !_komRelayOn ? L.T("Wyłączony") : (_komRelayListener != null ? L.T("Działa – urządzenia poza domem: ") + _komRemote.Count : L.T("Nie działa – sprawdź port")) +
                (lan.Length > 0 ? "\n" + L.T("Ten komputer w sieci domowej: ") + lan + L.T(" – na ten adres przekierowanie w routerze (port ") + _komRelayPort + L.T(" i 443)") : "") +
                (_komRelayOn && _komRelayHost.Length > 0 ? "\n" + L.T("Adres domu: ") + _komRelayHost + ":" + _komRelayPort : "") + (_komRelayOn && _komRelayDns.Length > 0 ? "\n" + _komRelayDns : "") + (_komRelayOn && _komRelayRouter.Length > 0 ? "\n" + _komRelayRouter : "") +
                (_komRelayOn && _komRelayListener != null ? "\n" + (_komRelayAlt ? L.T("Zapasowo port 443: otwarty (działa też w sieciach blokujących porty)")
                    : _komRelayAltOther ? L.T("Zapasowo port 443: w routerze prowadzi do innego komputera w domu (np. CHUWI) – tam działa zapasowy dostęp; tutaj nic nie trzeba")
                    : _komRelayUpnp ? L.T("Zapasowo port 443: nieotwarty – w routerze przekieruj 443 na ten komputer, port ") + _komRelayPort
                    : L.T("Zapasowo port 443: router nie pozwala tego sprawdzić – jeśli przekierowałeś w routerze 443 na ten komputer, port ") + _komRelayPort + L.T(", działa")) : "");
        }

        // komputer z kontaktami: rozmowy poza domem wlaczaja sie same (zgoda - polaczenie w domu); adres i port ustawiane same.
        // Wylaczyc mozna w 🎨 → 🌍 (wtedy juz samo sie nie wlacza)
        bool _komRelayAsked;   // uzytkownik sam wylaczyl - nie wlaczamy ponownie
        void KomRelayAskOnce()
        {
            if (_komRelayAsked || _komRelayOn || _komContacts.Count == 0) return;
            _komRelayOn = true; _komRelayAuto = true; KomSave(); KomRelayStart(); KomAnnounce();
        }

        // ☁ kopia urządzeń na serwerze (jak WhatsApp): telefon sam przysyła (co najwyżej co 30 min) skompresowane kontakty, rozmowy i ustawienia;
        // nowy / wyczyszczony telefon prosi o nią (link „Mój adres”), a właściciel serwera wybiera, czyje to konto - bez kodów i haseł
        const string KomRestoreTag = "\u0001velivo-odzysk\n";   // starsza wersja (kod odzyskiwania) - pomijane
        static string KomRestoreDir => Path.Combine(DataDir, "odzysk");
        void KomBackupReceive(KomFrameStream fs, KomContact c)
        {
            try
            {
                var hd = JsonNode.Parse(fs.ReadFrame()); long size = (long?)hd?["Size"] ?? 0;
                if (size <= 0 || size > 64L << 20) return;
                Directory.CreateDirectory(KomRestoreDir);
                var f = Path.Combine(KomRestoreDir, c.Id + ".gz"); var tmp = f + ".tmp";
                using (var o = File.Create(tmp))
                {
                    var buf = new byte[1 << 16]; long left = size;
                    while (left > 0) { int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left)); if (n <= 0) return; o.Write(buf, 0, n); left -= n; }
                }
                File.Move(tmp, f, true);
                var meta = new JsonObject { ["name"] = c.Name, ["dev"] = KomClean((string)hd["Dev"]), ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["size"] = size };
                if ((string)hd["Login"] is { Length: 32 } lg && lg.All(Uri.IsHexDigit) && (string)hd["Pin"] is { Length: 64 } pv && pv.All(Uri.IsHexDigit)) { meta["login"] = lg; meta["pin"] = pv; }   // 🔐 numer (skrót) + sprawdzian PIN-u - nowy telefon tej osoby
                File.WriteAllText(Path.Combine(KomRestoreDir, c.Id + ".json"), meta.ToJsonString());
                var ok = Encoding.UTF8.GetBytes("ok"); fs.WriteFrame(ok, 0, ok.Length);
                App.LogInfo("Pośrednik: kopia od " + c.Name + " (" + (size / 1024) + " KB)");
            }
            catch (Exception ex) { App.LogInfo("Pośrednik: kopia od " + c.Name + " nieudana – " + ex.Message); }
        }
        // kopie na tym serwerze: (id, nazwa, czas) - najnowsze najpierw
        static List<(string Id, string Name, long At)> KomBackups()
        {
            var l = new List<(string, string, long)>();
            try { foreach (var m in Directory.GetFiles(KomRestoreDir, "*.json")) try { var id = Path.GetFileNameWithoutExtension(m); var j = JsonNode.Parse(File.ReadAllText(m));
                if (id.Length == 32 && File.Exists(Path.Combine(KomRestoreDir, id + ".gz"))) l.Add((id, ((string)j?["name"] ?? "?") + ((string)j?["dev"] is { Length: > 0 } d ? " – " + d : ""), (long?)j?["at"] ?? 0)); } catch (Exception) { } }
            catch (Exception) { }
            return l.OrderByDescending(x => x.Item3).ToList();
        }
        sealed class KomRestoreReq { public string Dev, Ip; public DateTime At; public volatile string Choice; }
        static readonly ConcurrentDictionary<string, KomRestoreReq> _komRestoreReq = new ConcurrentDictionary<string, KomRestoreReq>();
        static readonly ConcurrentDictionary<string, (DateTime at, int n)> _komRestoreTries = new ConcurrentDictionary<string, (DateTime, int)>();
        async Task KomRestoreServe(JsonNode h, Stream s, TcpClient cl, CancellationToken token)
        {
            var ip = (cl.Client.RemoteEndPoint as IPEndPoint)?.Address?.ToString() ?? "?";
            var req = (string)h["Req"] ?? "";
            foreach (var old in _komRestoreReq.Where(x => DateTime.UtcNow - x.Value.At > TimeSpan.FromMinutes(6)).ToList()) _komRestoreReq.TryRemove(old.Key, out _);
            byte[] rep = Encoding.UTF8.GetBytes("wait");
            if ((string)h["Login"] is { Length: 32 } lg && lg.All(Uri.IsHexDigit))
            {   // 🔐 użytkownik po numerze i PIN-ie (jak WhatsApp) - konto od razu, bez pytania właściciela; 5 prób na godzinę na numer
                var t0 = _komRestoreTries.AddOrUpdate("l" + lg, (DateTime.UtcNow, 1), (_, o) => DateTime.UtcNow - o.at > TimeSpan.FromHours(1) ? (DateTime.UtcNow, 1) : (o.at, o.n + 1));
                string hit = null, pin = null; long best = -1;
                try { foreach (var m in Directory.GetFiles(KomRestoreDir, "*.json")) try { var j = JsonNode.Parse(File.ReadAllText(m)); if ((string)j?["login"] == lg && ((long?)j["at"] ?? 0) > best) { best = (long?)j["at"] ?? 0; hit = Path.GetFileNameWithoutExtension(m); pin = (string)j["pin"]; } } catch (Exception) { } } catch (Exception) { }
                var pv = (string)h["Pin"] ?? "";
                if (t0.n > 5) rep = Encoding.UTF8.GetBytes("later");
                else if (hit == null) rep = Encoding.UTF8.GetBytes("none");
                else if (pin == null || pv.Length != pin.Length || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(pv), Encoding.ASCII.GetBytes(pin))) rep = Encoding.UTF8.GetBytes("badpin");
                else try { rep = File.ReadAllBytes(Path.Combine(KomRestoreDir, hit + ".gz")); _komRestoreTries.TryRemove("l" + lg, out _); } catch (Exception) { rep = Encoding.UTF8.GetBytes("none"); }
                App.LogInfo("Pośrednik: przywracanie po numerze z " + ip + " – " + (rep.Length > 16 ? "konto oddane" : Encoding.UTF8.GetString(rep)));
                await KomWriteBlock(s, rep, token); return;
            }
            if (req.Length != 32 || !req.All(Uri.IsHexDigit)) rep = Encoding.UTF8.GetBytes("no");
            else if (_komRestoreReq.TryGetValue(req, out var r))
            {
                var ch = r.Choice;
                if (ch == "no") { _komRestoreReq.TryRemove(req, out _); rep = Encoding.UTF8.GetBytes("no"); }
                else if (ch != null)
                {
                    try { rep = File.ReadAllBytes(Path.Combine(KomRestoreDir, ch + ".gz")); _komRestoreReq.TryRemove(req, out _); App.LogInfo("Pośrednik: kopia konta oddana na nowy telefon (" + r.Dev + ", " + ip + ")"); }
                    catch (Exception ex) { App.LogError(ex); rep = Encoding.UTF8.GetBytes("no"); }
                }
            }
            else if (KomBackups().Count == 0) rep = Encoding.UTF8.GetBytes("none");
            else
            {   // nowa prośba: najwyżej 3 z jednego adresu na 10 min i 10 naraz - właściciel nie dostanie zalewu okienek
                var t = _komRestoreTries.AddOrUpdate(ip, (DateTime.UtcNow, 1), (_, o) => DateTime.UtcNow - o.at > TimeSpan.FromMinutes(10) ? (DateTime.UtcNow, 1) : (o.at, o.n + 1));
                if (t.n > 3 || _komRestoreReq.Count >= 10) rep = Encoding.UTF8.GetBytes("no");
                else
                {
                    var nr = new KomRestoreReq { Dev = KomClean((string)h["Dev"]), Ip = ip, At = DateTime.UtcNow };
                    if (_komRestoreReq.TryAdd(req, nr)) { App.LogInfo("Pośrednik: prośba o przywrócenie konta – " + nr.Dev + " z " + ip); _ = Dispatcher.BeginInvoke(new Action(() => KomRestoreAsk(nr))); }
                }
            }
            await KomWriteBlock(s, rep, token);
        }
        // pytanie do właściciela serwera: czyje konto dostaje nowy telefon (okienko na wierzchu, samo znika po 5 min)
        void KomRestoreAsk(KomRestoreReq r)
        {
            KomPlaySound();
            var pal = KomCurrentPal();
            var w = new Window { Uid = "own-theme", Title = "Velivo", Width = 420, SizeToContent = SizeToContent.Height, Topmost = true, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = new SolidColorBrush(pal.Panel), ShowInTaskbar = true };
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = "♻ " + L.T("Nowy telefon chce przywrócić konto"), FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(pal.Fg), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = L.T("Telefon") + ": " + (r.Dev.Length > 0 ? r.Dev : "?") + "\n" + L.T("Czyje to konto? Wybierz osobę tylko, gdy wiesz, że to jej nowy telefon – dostanie jej kontakty, rozmowy i ustawienia. Jeśli nikt nie zmieniał telefonu – kliknij „To nie moje”."),
                FontSize = 14, Foreground = new SolidColorBrush(pal.Dim), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
            Button Btn(string text, string tip, Color bg) => new Button { Content = text, ToolTip = tip, MinHeight = 38, Margin = new Thickness(0, 4, 0, 4), FontSize = 15, Padding = new Thickness(10, 4, 10, 4),
                HorizontalContentAlignment = HorizontalAlignment.Left, Foreground = Brushes.White, Background = new SolidColorBrush(bg), BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
            foreach (var b in KomBackups())
            {
                var id = b.Id;
                var btn = Btn("👤 " + b.Name + "   (" + L.T("kopia z") + " " + DateTimeOffset.FromUnixTimeMilliseconds(b.At).LocalDateTime.ToString("d.MM HH:mm") + ")", L.T("Oddaj nowemu telefonowi konto tej osoby"), Color.FromRgb(0x25, 0x63, 0xEB));
                btn.Click += (s, e) => { r.Choice = id; w.Close(); };
                panel.Children.Add(btn);
            }
            var no = Btn("✕ " + L.T("To nie moje – odrzuć"), L.T("Nowy telefon nic nie dostanie"), Color.FromRgb(0xE5, 0x39, 0x35));
            no.Click += (s, e) => { r.Choice = "no"; w.Close(); };
            panel.Children.Add(no);
            w.Content = panel; KomStyle(w);
            no.Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)); no.Foreground = Brushes.White;   // odrzucenie na czerwono (KomStyle daje kolor motywu)
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            t.Tick += (s, e) => { t.Stop(); w.Close(); };
            w.Closed += (s, e) => t.Stop();
            w.Show(); w.Activate(); t.Start();
        }
        // 🗑 dane osoby na tym serwerze: kopia rozmów, konto (numer + PIN), pliki i wiadomości w skrzynce 📬
        void KomWipeUser(string id)
        {
            if (id is not { Length: 32 } || !id.All(Uri.IsHexDigit)) return;
            foreach (var ext in new[] { ".gz", ".json" }) try { File.Delete(Path.Combine(KomRestoreDir, id + ext)); } catch (Exception) { }
            try { if (Directory.Exists(KomBoxDir(id))) Directory.Delete(KomBoxDir(id), true); } catch (Exception) { }
            KomAccounts().Remove(id);
            App.LogInfo("Pośrednik: usunięto dane konta " + id.Substring(0, 6));
        }
        // 🗑 użytkownik sam usuwa swoje dane i konto z serwera (telefon: 💾 → Usuń moje dane z serwerów) - bez blokady, może założyć konto od nowa
        const string KomWipeMeTag = "\u0001velivo-usun-mnie\n";
        void KomWipeMe(KomContact c)
        {
            if (_komRelayListener == null || c.IsGroup) return;
            KomWipeUser(c.Id);
            _komContacts.RemoveAll(x => x.Id == c.Id); KomSave(); _komRefresh?.Invoke();
        }
        // 📝 konta na serwerze (jak w Signalu): ktoś z internetu instaluje Komunikator, podaje adres serwera, imię, numer i PIN - serwer zakłada mu konto
        // (otwarta - od razu; za zgodą - właściciel klika; wyłączona - nikt). Numer bez SMS-a: tylko nazwa konta, chroni go PIN; pierwszy zajmuje numer.
        string KomRegMode => (string)_komOpt["rejestracja"] is "otwarta" or "wylaczona" ? (string)_komOpt["rejestracja"] : "zgoda";
        JsonObject KomAccounts() { if (_komOpt["konta"] is not JsonObject a) _komOpt["konta"] = a = new JsonObject(); return a; }
        readonly ConcurrentDictionary<string, JsonObject> _komRegRes = new ConcurrentDictionary<string, JsonObject>();
        readonly ConcurrentDictionary<string, bool> _komRegAsk = new ConcurrentDictionary<string, bool>();
        async Task KomRegisterServe(JsonNode h, Stream s, TcpClient cl, CancellationToken token)
        {
            var ip = (cl.Client.RemoteEndPoint as IPEndPoint)?.Address?.ToString() ?? "?";
            string id = (string)h["Id"] ?? "", pub = (string)h["Pub"] ?? "", lg = (string)h["Login"] ?? "", pin = (string)h["Pin"] ?? "";
            var name = KomClean((string)h["Name"]); var nr = KomClean((string)h["Nr"]);
            JsonObject resp;
            if (id.Length != 32 || !id.All(Uri.IsHexDigit) || pub.Length is 0 or > 400 || lg.Length != 32 || !lg.All(Uri.IsHexDigit) || pin.Length != 64 || !pin.All(Uri.IsHexDigit) || name.Length == 0) resp = new JsonObject { ["ok"] = false, ["why"] = "bad" };
            else if (_komRegRes.TryRemove(id, out var done)) resp = done;
            else
            {
                resp = null;
                await Dispatcher.InvokeAsync(() =>
                {
                    var mode = KomRegMode;
                    var taken = KomAccounts().Any(kv => (string)kv.Value?["login"] == lg && kv.Key != id);
                    if (mode == "wylaczona") resp = new JsonObject { ["ok"] = false, ["why"] = "off" };
                    else if (taken) resp = new JsonObject { ["ok"] = false, ["why"] = "taken" };
                    else if (KomForgotten(id) || KomContactById(id) != null) resp = new JsonObject { ["ok"] = false, ["why"] = "no" };
                    else if (mode == "otwarta")
                    {   // najwyżej 3 nowe konta z jednego adresu na dobę - ochrona przed zalewem kont
                        var t = _komRestoreTries.AddOrUpdate("r" + ip, (DateTime.UtcNow, 1), (_, o) => DateTime.UtcNow - o.at > TimeSpan.FromDays(1) ? (DateTime.UtcNow, 1) : (o.at, o.n + 1));
                        resp = t.n > 3 ? new JsonObject { ["ok"] = false, ["why"] = "later" } : KomRegisterCreate(id, name, pub, lg, pin, nr);
                    }
                    else
                    {
                        resp = new JsonObject { ["wait"] = true };
                        if (_komRegAsk.Count < 10 && _komRegAsk.TryAdd(id, true))
                            _ = Dispatcher.BeginInvoke(new Action(() =>
                            {
                                KomPlaySound();
                                bool yes; try { yes = MessageBox.Show((Window)_komWindow ?? PairDialogOwner(), L.T("Nowe konto na Twoim serwerze:") + "\n\n" + name + (nr.Length > 0 ? "\n" + L.T("numer: ") + nr : "") + "\n\n" + L.T("Numer nie jest sprawdzany SMS-em – dodaj tylko, gdy wiesz, kto to jest. Dodać?"),
                                    "📝 " + L.T("Nowe konto"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes; }
                                finally { _komRegAsk.TryRemove(id, out _); }
                                _komRegRes[id] = yes && !KomAccounts().Any(kv => (string)kv.Value?["login"] == lg) ? KomRegisterCreate(id, name, pub, lg, pin, nr) : new JsonObject { ["ok"] = false, ["why"] = "no" };
                            }));
                    }
                });
            }
            if (resp["wait"] == null) App.LogInfo("Pośrednik: nowe konto „" + name + "” z " + ip + " – " + ((bool?)resp["ok"] == true ? "założone" : (string)resp["why"]));
            await KomWriteBlock(s, Encoding.UTF8.GetBytes(resp.ToJsonString()), token);
        }
        JsonObject KomRegisterCreate(string id, string name, string pub, string lg, string pin, string nr)
        {
            using var me = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var myPub = me.ExportSubjectPublicKeyInfo(); byte[] reqPub, k;
            try { reqPub = Convert.FromBase64String(pub); k = KomAgree(me, reqPub, "velivo-prosba", reqPub, myPub); } catch (Exception) { return new JsonObject { ["ok"] = false, ["why"] = "bad" }; }
            _komContacts.Add(new KomContact { Id = id, Name = name, Key = k, Addr = "", Device = name, Own = false });
            KomAccounts()[id] = new JsonObject { ["login"] = lg, ["pin"] = pin, ["nr"] = nr, ["name"] = name, ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            KomSave(); _komRefresh?.Invoke();
            var r = new JsonObject { ["ok"] = true, ["relay"] = _komId, ["relayName"] = _lanDeviceName, ["relayPub"] = Convert.ToBase64String(myPub) };
            if (_komRelayHost.Length > 0) r["relayAddr"] = _komRelayHost + ":" + _komRelayPort;
            return r;
        }
        // 🔎 szukanie po numerze: konto z tym numerem (skrót) dostaje się z pytającym nawzajem jako kontakty (wspólny nowy klucz); najwyżej 20 na godzinę
        const string KomFindTag = "\u0001velivo-szukaj\n";
        void KomFindIn(KomContact c, string lg)
        {
            if (_komRelayListener == null) return;
            var t = _komRestoreTries.AddOrUpdate("s" + c.Id, (DateTime.UtcNow, 1), (_, o) => DateTime.UtcNow - o.at > TimeSpan.FromHours(1) ? (DateTime.UtcNow, 1) : (o.at, o.n + 1));
            string hit = t.n > 20 ? null : KomAccounts().Where(kv => (string)kv.Value?["login"] == lg.Trim() && kv.Key != c.Id).Select(kv => kv.Key).FirstOrDefault();
            if (hit == null && t.n <= 20) try { foreach (var m in Directory.GetFiles(KomRestoreDir, "*.json")) { var j = JsonNode.Parse(File.ReadAllText(m)); var mid = Path.GetFileNameWithoutExtension(m); if ((string)j?["login"] == lg.Trim() && mid != c.Id) { hit = mid; break; } } } catch (Exception) { }   // numer ustawiony w 💾 na telefonie
            var other = hit != null ? KomContactById(hit) : null;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (other == null) { KomDeliver(c, new KomMsg { Mine = true, Text = "🔎 " + L.T("Nie ma konta z tym numerem na tym serwerze."), Ts = ts, Mid = Guid.NewGuid().ToString("N") }); return; }
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            string Card(KomContact x) => KomCardTag + new JsonObject { ["id"] = x.Id, ["name"] = x.Name, ["dev"] = x.Device, ["key"] = key, ["relay"] = x.Relay ?? "" }.ToJsonString();
            KomDeliver(c, new KomMsg { Mine = true, Text = Card(other), Ts = ts, Mid = Guid.NewGuid().ToString("N") });
            KomDeliver(other, new KomMsg { Mine = true, Text = Card(c), Ts = ts, Mid = Guid.NewGuid().ToString("N") });
        }
        Button KomRegisterButton()
        {
            string Label() => "📝 " + L.T("Nowe konta z internetu: ") + (KomRegMode == "otwarta" ? L.T("każdy sam (otwarte)") : KomRegMode == "wylaczona" ? L.T("wyłączone") : L.T("za moją zgodą"));
            var b = new Button { Content = Label(), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = L.T("Czy ktoś z internetu może sam założyć konto na tym serwerze (Komunikator → Załóż konto: imię, numer, PIN). Kliknij, aby zmienić.") };
            b.Click += (s, e) => { _komOpt["rejestracja"] = KomRegMode == "zgoda" ? "otwarta" : KomRegMode == "otwarta" ? "wylaczona" : "zgoda"; KomSave(); b.Content = Label(); };
            return b;
        }
        // 👥 kontakty serwera: kogo znają moje serwery; usunięcie - na wszystkich moich serwerach naraz (i nie wraca przy wymianie kontaktów)
        const string KomForgetTag = "\u0001velivo-zapomnij\n";
        const string KomAcctTag = "\u0001velivo-konta\n";
        readonly ConcurrentDictionary<string, string> _komAccSent = new ConcurrentDictionary<string, string>();
        // 📝 konta i usunięci od mojego drugiego serwera: dopisuje nowsze (at), numer należy do tego, kto zajął go pierwszy; usunięci - usuwani i tutaj
        void KomAcctIn(string json)
        {
            JsonObject o; try { o = JsonNode.Parse(json) as JsonObject; } catch (Exception) { return; }
            if (o == null) return;
            bool ch = false; var mine = KomAccounts();
            if (o["removed"] is JsonArray rr) foreach (var x in rr.Select(n => (string)n).Where(x => x is { Length: 32 }).ToList()) if (!KomForgotten(x) && KomContactById(x) == null) { KomForget(x, false); ch = true; }   // kogoś obecnego tutaj (np. dodanego tu od nowa) nie kasuje - usunięcie z okna i tak idzie od razu na wszystkie serwery
            if (o["konta"] is JsonObject ka) foreach (var kv in ka.ToList())
            {
                if (kv.Key.Length != 32 || kv.Value is not JsonObject v || KomForgotten(kv.Key)) continue;
                long at = (long?)v["at"] ?? 0;
                if (mine[kv.Key] is JsonObject cur && ((long?)cur["at"] ?? 0) >= at) continue;
                var lg = (string)v["login"];
                if (mine.Any(x => x.Key != kv.Key && (string)x.Value?["login"] == lg && ((long?)x.Value?["at"] ?? 0) <= at)) continue;   // numer zajęty wcześniej przez kogoś innego - pierwszy wygrywa
                mine[kv.Key] = JsonNode.Parse(v.ToJsonString()); ch = true;
            }
            if (ch) KomSave();
        }
        bool KomForgotten(string id) => _komOpt["removed"] is JsonArray ra && ra.Any(x => (string)x == id);
        void KomForget(string id, bool spread)
        {
            if (_komOpt["removed"] is not JsonArray ra) _komOpt["removed"] = ra = new JsonArray();
            if (!KomForgotten(id)) ra.Add(id);
            KomWipeUser(id);
            _komContacts.RemoveAll(x => x.Id == id); KomSave(); _komRefresh?.Invoke();
            if (spread) foreach (var r in _komContacts.Where(x => x.Dom && !x.IsGroup && !string.IsNullOrEmpty(x.Relay)).ToList())
                KomDeliver(r, new KomMsg { Mine = true, Text = KomForgetTag + id, Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mid = Guid.NewGuid().ToString("N") });
        }
        static string KomServerContactsHelp => L.T("Twoje serwery pamiętają te osoby i urządzenia i przekazują je sobie nawzajem oraz Twoim urządzeniom – po wyłączeniu jednego serwera albo ponownej instalacji telefonu nikt nie musi się dodawać od nowa. Zielona kropka – w sieci teraz, szara – poza siecią. „Usuń na wszystkich serwerach” – ta osoba nie będzie mogła pisać przez Twoje serwery.");
        Button KomServerContactsButton(Window owner)
        {
            var b = new Button { Content = "👥 " + L.T("Kontakty serwera…"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = L.T("Kogo znają moje serwery – usunięcie działa na wszystkich serwerach naraz") };
            b.Click += (s, e) =>
            {
                var list = new ListBox { MinWidth = 380, MaxHeight = 420 };
                // czytelnie: kolorowa kropka (emoji w okienkach Windows są szare), nazwa, a pod nią kto to i czy jest w sieci
                void Fill()
                {
                    list.Items.Clear();
                    foreach (var c in _komContacts.Where(x => !x.IsGroup).OrderByDescending(x => !string.IsNullOrEmpty(x.Relay)).ThenByDescending(x => x.Dom).ThenBy(x => x.Name))
                    {
                        bool on = KomOnline(c.Id);
                        var kind = !string.IsNullOrEmpty(c.Relay) ? L.T("mój serwer") : c.Dom ? L.T("moje urządzenie") : L.T("znajomy");
                        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 4, 2, 4) };
                        row.Children.Add(new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                            Fill = new System.Windows.Media.SolidColorBrush(on ? System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E) : System.Windows.Media.Color.FromRgb(0x9C, 0xA3, 0xAF)) });
                        row.Children.Add(new StackPanel { Children = {
                            new TextBlock { Text = c.Name, FontWeight = FontWeights.SemiBold, FontSize = 14 },
                            new TextBlock { Text = kind + " · " + (on ? L.T("w sieci") : L.T("poza siecią")), FontSize = 12, Opacity = 0.7 } } });
                        list.Items.Add(new ListBoxItem { Tag = c.Id, Content = row });
                    }
                }
                Fill();
                var head = new TextBlock { Text = L.T("Zapisane na tym serwerze (każdy z listy): ") + _komContacts.Count(x => !x.IsGroup), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
                var del = new Button { Content = "🗑 " + L.T("Usuń na wszystkich serwerach"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                var w = new Window { Title = "👥 " + L.T("Kontakty serwera"), SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = new StackPanel { Margin = new Thickness(16), Children = { head, new TextBlock { Text = KomServerContactsHelp, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Opacity = 0.8, Margin = new Thickness(0, 0, 0, 8) }, list, del } } };
                del.Click += (s2, e2) =>
                {
                    if (list.SelectedItem is not ListBoxItem it || KomContactById((string)it.Tag) is not { } c) return;
                    if (MessageBox.Show(w, L.T("Usunąć „") + c.Name + L.T("” na wszystkich moich serwerach? Nie będzie mógł pisać przez moje serwery."), "🗑", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    KomForget(c.Id, true); Fill(); head.Text = L.T("Zapisane na tym serwerze (każdy z listy): ") + _komContacts.Count(x => !x.IsGroup);
                };
                KomStyle(w); w.ShowDialog();
            };
            return b;
        }
        // 📬 Centrum serwera (pełne okno): skrzynka, osoby, dziennik skrzynki, błędy Velivo, informacje techniczne - tabele z sortowaniem (klik w nagłówek) i szukaniem
        Button KomBoxButton(Window owner)
        {
            var b = new Button { Content = "🛠 " + L.T("Panel administratora…"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = L.T("Wszystko o moich serwerach: stan, sterowanie, skrzynka, osoby, dzienniki, błędy") };
            b.Click += (s, e) => KomServerCenter(owner);
            return b;
        }
        // tabela: kolumny, wiersze (tekst), szukanie i sortowanie; zwraca element i odświeżanie
        (FrameworkElement, Action) KomTable(string[] cols, Func<List<string[]>> rows, TextBox search)
        {
            var gv = new GridView();
            var lv = new ListView { View = gv, Margin = new Thickness(0, 4, 0, 0), SelectionMode = SelectionMode.Extended };
            // własny wiersz: zaznaczony - ciemnoniebieski z białym tekstem (zwykły szablon Windows dawał jasnoniebieski pasek i nieczytelny biały tekst)
            var bd = new FrameworkElementFactory(typeof(Border)); bd.SetValue(Border.PaddingProperty, new Thickness(2, 5, 2, 5));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1)); bd.SetValue(Border.BorderBrushProperty, AdmB(0x283150));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            var gp = new FrameworkElementFactory(typeof(GridViewRowPresenter));
            gp.SetValue(GridViewRowPresenter.ColumnsProperty, new TemplateBindingExtension(GridView.ColumnCollectionProperty));
            gp.SetValue(GridViewRowPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            bd.AppendChild(gp);
            var rowStyle = new Style(typeof(ListViewItem));
            rowStyle.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListViewItem)) { VisualTree = bd }));
            rowStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent)); rowStyle.Setters.Add(new Setter(Control.ForegroundProperty, AdmB(0xe8ecf4)));   // jasny tekst - bez tego szary z motywu Windows, nieczytelny na granacie
            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; over.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x55, 0x6B, 0x7C, 0xC4))));
            var picked = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true }; picked.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2D, 0x4F, 0xB8)))); picked.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            var zebra = new Trigger { Property = ItemsControl.AlternationIndexProperty, Value = 1 }; zebra.Setters.Add(new Setter(Control.BackgroundProperty, AdmB(0x1f2639)));   // co drugi wiersz ciemniejszy - łatwiej czytać
            rowStyle.Triggers.Add(zebra); rowStyle.Triggers.Add(over); rowStyle.Triggers.Add(picked); lv.AlternationCount = 2;
            // nagłówek kolumny: ciemny pasek, pogrubiony, do lewej
            var hb = new FrameworkElementFactory(typeof(Border)); hb.SetValue(Border.BackgroundProperty, AdmB(0x232b42)); hb.SetValue(Border.BorderBrushProperty, AdmB(0x34405e));
            hb.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 1, 1)); hb.SetValue(Border.PaddingProperty, new Thickness(8, 6, 8, 6));
            var hc = new FrameworkElementFactory(typeof(ContentPresenter)); hc.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left); hb.AppendChild(hc);
            var hs = new Style(typeof(GridViewColumnHeader)); hs.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(GridViewColumnHeader)) { VisualTree = hb }));
            hs.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White)); hs.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            gv.ColumnHeaderContainerStyle = hs;
            lv.ItemContainerStyle = rowStyle;
            int sortCol = -1; bool desc = false; List<string[]> data = new List<string[]>();
            int Cmp(string a, string c) => double.TryParse((a ?? "").Split(' ')[0], out var x) && double.TryParse((c ?? "").Split(' ')[0], out var y) ? x.CompareTo(y) : string.Compare(a, c, StringComparison.CurrentCultureIgnoreCase);
            void Show()
            {
                var q = search.Text.Trim();
                IEnumerable<string[]> r = data.Where(x => q.Length == 0 || x.Any(v => (v ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0));
                if (sortCol >= 0) { var l = r.ToList(); l.Sort((x, y) => (desc ? -1 : 1) * Cmp(x[sortCol], y[sortCol])); r = l; }
                var list = r.ToList(); bool first = lv.ItemsSource == null || !((System.Collections.IList)lv.ItemsSource).GetEnumerator().MoveNext();
                var keepSel = new HashSet<string>(lv.SelectedItems.Cast<string[]>().Select(x => string.Join("\u0001", x)));   // zaznaczony wiersz zostaje po odświeżeniu co 5 s (kilka zaznaczonych, np. Ctrl+A - tabela czeka z odświeżeniem)
                lv.ItemsSource = list;
                if (keepSel.Count > 0) foreach (var x in list) if (keepSel.Contains(string.Join("\u0001", x))) lv.SelectedItems.Add(x);
                if (first && list.Count > 0) foreach (var c in gv.Columns) { c.Width = 0; c.Width = double.NaN; }   // szerokość kolumn do zawartości (raz, bez skakania co 5 s)
            }
            for (int k = 0; k < cols.Length; k++)
            {
                int col = k;
                var h = new GridViewColumnHeader { Content = cols[k], Cursor = System.Windows.Input.Cursors.Hand };
                h.Click += (s, e) => { desc = sortCol == col && !desc; sortCol = col; foreach (var c in gv.Columns) ((GridViewColumnHeader)c.Header).Content = cols[gv.Columns.IndexOf(c)]; h.Content = cols[col] + (desc ? " ▼" : " ▲"); Show(); };
                // komórka z kolorem stanu: zielony - doręczone / działa, żółty - w trakcie / czeka, czerwony - przerwane / błąd
                var tb = new FrameworkElementFactory(typeof(TextBlock)); tb.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 6, 0));
                tb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("[" + k + "]"));
                tb.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("[" + k + "]") { Converter = AdmColor.One });
                gv.Columns.Add(new GridViewColumn { Header = h, CellTemplate = new DataTemplate { VisualTree = tb } });
            }
            search.TextChanged += (s, e) => Show();
            return (lv, () => { if (lv.SelectedItems.Count > 1) return; try { data = rows(); } catch (Exception ex) { data = new List<string[]> { new[] { "⚠ " + ex.Message } }; } Show(); });
        }
        // 🛠 wygląd panelu administratora (granat jak w makiecie): tło, kafelki, przyciski, zakładki, kolory stanów
        static SolidColorBrush AdmB(int rgb) => new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        sealed class AdmColor : System.Windows.Data.IValueConverter
        {
            public static readonly AdmColor One = new AdmColor();
            static readonly string[] Bad = { "przerwany", "błąd", "odmówił", "nie przyjmuje", "usunięty", "brak wieści", "wyłączony", "nie nasłuchuje", "różne" };
            static readonly string[] Ok = { "doręczony", "działa", "połączony z tym", "otwarty", "zgodne" };
            static readonly string[] Wait = { "wysyłam", "czeka", "w sieci przez", "sprawdzam", "przyjęty" };
            static readonly string[] Dim = { "poza siecią", "—" };
            public object Convert(object v, Type t, object p, System.Globalization.CultureInfo c)
            {
                var x = (v as string ?? "").ToLowerInvariant();
                if (x.Contains("ręcznie")) return AdmB(0x9aa4bd);   // usunięte przez właściciela (Usuń zaznaczone / wyczyść) - zwykła czynność, nie błąd: szare, nie czerwone
                if (Bad.Any(x.Contains)) return AdmB(0xf87171);
                if (Wait.Any(x.Contains)) return AdmB(0xfbbf24);
                if (Dim.Any(x.Contains)) return AdmB(0x8b93a7);   // poza siecią - szary, jak w makiecie
                if (Ok.Any(x.Contains) || x.Contains("w sieci")) return AdmB(0x4ade80);
                return AdmB(0xe8ecf4);   // zwykły tekst jasny - „brak wartości” dawał domyślną czerń WPF (nie kolor wiersza)
            }
            public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
        }
        int KomBoxDays => (int?)_komOpt["boxDays"] ?? 30;
        // 📊 stan każdego mojego serwera (co minutę między serwerami w domu) - panel administratora pokazuje wszystkie naraz, z wersją (różne wersje = częsta przyczyna błędów)
        readonly ConcurrentDictionary<string, (JsonObject Info, DateTime At)> _komSrvInfo = new ConcurrentDictionary<string, (JsonObject, DateTime)>();
        static string KomBuild => (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "?") + " (" + (File.Exists(Environment.ProcessPath) ? File.GetLastWriteTime(Environment.ProcessPath).ToString("yyyy-MM-dd HH:mm") : "?") + ")";
        JsonObject KomSrvInfoNow()
        {
            var fpv = KomBoxFingerprint();
            int files = 0; try { var root = Path.Combine(DataDir, "skrzynka"); if (Directory.Exists(root)) files = Directory.GetFiles(root, "*.bin", SearchOption.AllDirectories).Length; } catch (Exception) { }
            return new JsonObject { ["t"] = "p2p-srvinfo", ["id"] = _komId, ["name"] = Environment.MachineName, ["ver"] = KomBuild, ["on"] = _komRelayListener != null, ["port"] = _komRelayPort,
                ["host"] = _komRelayHost, ["router"] = _komRelayRouter, ["conns"] = string.Join(", ", _komRemote.Keys.Select(k => KomContactById(k)?.Name ?? "?")), ["connIds"] = string.Join(",", _komRemote.Keys), ["files"] = files,
                ["msgs"] = _komStore.Sum(kv => { lock (kv.Value) return kv.Value.Count; }), ["since"] = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToString("yyyy-MM-dd HH:mm"), ["lan"] = KomLanIp(),
                ["fp"] = fpv.fp, ["fpM"] = fpv.msgs, ["fpF"] = fpv.files, ["fpPer"] = fpv.per, ["fpV"] = KomCanon.Version, ["evN"] = KomEv.Count, ["evD"] = KomEv.Digest(), ["evHave"] = KomEv.Have(),
                ["ports"] = string.Join(",", ((_komOpt["ports"] as System.Text.Json.Nodes.JsonArray)?.Select(x => (int?)x ?? 0) ?? Enumerable.Empty<int>()).Append(_komRelayPort).Concat(_komRelayAlt && !_komRelayAltOther ? new[] { KomRelayAltPort } : new int[0]).Where(x => x > 0).Distinct()) };
        }
        // 🌍 w sieci przez którykolwiek mój serwer (stan od innych serwerów, co minutę) - Ryzen widzi mamę połączoną z NUCBOX
        string KomPeerWith(string id) => _komSrvInfo.Where(kv => DateTime.UtcNow - kv.Value.At < TimeSpan.FromMinutes(3) && ((string)kv.Value.Info["connIds"] ?? "").Split(',').Contains(id)).Select(kv => kv.Key).FirstOrDefault();
        bool KomOnlineAny(string id) => KomOnline(id) || KomPeerWith(id) != null;
        // 🔁 zastępstwo serwerów: starszy telefon (mama) zna tylko port jednego serwera (NUCBOX 41921). Gdy ten serwer nie działa
        // (brak wieści 3 min i nie odpowiada w sieci domowej), JEDEN działający serwer (najmniejsze id) przekierowuje w routerze jego porty na siebie
        // i obsługuje jego ludzi (alias); po powrocie właściciel portu sam go odbiera (opis KomTakeDesc), a my oddajemy bez walki.
        // Działa tylko przez UPnP - ręczne przekierowania w routerze wygrywają z UPnP.
        const string KomTakeDesc = "Velivo zastepstwo";
        readonly ConcurrentDictionary<string, (string Lan, int Port, int[] Ports)> _komPeerPorts = new ConcurrentDictionary<string, (string, int, int[])>();
        readonly ConcurrentDictionary<int, string> _komTaken = new ConcurrentDictionary<int, string>();
        int _komTakeBusy;
        async Task KomTakeover()
        {
            if (Interlocked.Exchange(ref _komTakeBusy, 1) == 1) return;
            try
            {
                bool Fresh(string id) => _komSrvInfo.TryGetValue(id, out var v) && DateTime.UtcNow - v.At < TimeSpan.FromMinutes(3) && (bool?)v.Info["on"] == true;
                foreach (var kv in _komPeerPorts.ToList())
                {
                    var (lan, mainPort, ports) = kv.Value;
                    bool alive = Fresh(kv.Key);
                    if (!alive && mainPort > 0) try { using var cl = new TcpClient(); using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await cl.ConnectAsync(lan, mainPort, cts.Token); alive = true; } catch (Exception) { }
                    if (alive) { foreach (var p in _komTaken.Where(x => x.Value == kv.Key).Select(x => x.Key).ToList()) if (_komTaken.TryRemove(p, out _)) App.LogInfo("Zastępstwo: serwer " + KomSrvName(kv.Key) + " znowu działa – oddaję mu port " + p); continue; }
                    var boss = _komSrvInfo.Keys.Where(Fresh).Append(_komId).OrderBy(x => x, StringComparer.Ordinal).First();
                    if (boss != _komId) continue;   // przejmuje tylko jeden działający serwer - bez przepychanki
                    foreach (var p in ports.Where(p => p != _komRelayPort && !_komTaken.ContainsKey(p)))
                        if ((await KomUpnpAsync(p, _komRelayPort, true)).mapped && _komTaken.TryAdd(p, kv.Key)) App.LogInfo("Zastępstwo: serwer " + KomSrvName(kv.Key) + " nie działa – port " + p + " w routerze prowadzi teraz do tego komputera");
                }
            }
            catch (Exception ex) { App.LogError(ex); }
            finally { _komTakeBusy = 0; }
        }
        string KomSrvName(string id) => _komSrvInfo.TryGetValue(id, out var v) ? (string)v.Info["name"] ?? id : KomContactById(id)?.Name ?? id;
        // 🔗 ŁĄCZE SERWERÓW: moje serwery są równorzędne bez względu na to, jak je dodano (dom / internet). W domu wymieniają dane pakietami w sieci domowej;
        // serwer, którego nie widać w tej sieci (inny dom, dodany przez internet), dostaje te same pakiety (kopie skrzynki, „doręczone”, dziennik zdarzeń,
        // stan, przechowywanie) przez stałe połączenie z jego serwerem (jak telefon poza domem), ponawiane co minutę. Pakiet w „kopercie” p2p-srv -
        // nadawcą jest serwer (połączenie sprawdzone kluczem), w środku oryginał; po stronie odbiorcy ta sama obsługa co z sieci domowej (idempotentna: mid / id zdarzenia).
        [ThreadStatic] static string _komNetPeer;   // obsługuję pakiet od mojego serwera przez internet (zaufany jak serwer w domu)
        readonly ConcurrentDictionary<string, KomRemoteConn> _komPeerLink = new ConcurrentDictionary<string, KomRemoteConn>();
        readonly ConcurrentDictionary<string, bool> _komPeerDial = new ConcurrentDictionary<string, bool>();
        IEnumerable<KomContact> KomMyServers() => _komContacts.Where(c => !c.IsGroup && !string.IsNullOrEmpty(c.Relay) && c.Id != _komId && KomIsMember(c.Id)).ToList();   // członkowie mojej infrastruktury (certyfikat), nie „moje urządzenie”
        bool KomLanPeer(string id) => _komLanSeen.TryGetValue(id, out var ls) && DateTime.UtcNow - ls < TimeSpan.FromSeconds(100) && _komLanRelays.ContainsKey(id);
        void KomSrvNet(JsonNode p)
        {
            if (_komRelayListener == null || p == null) return;
            var pt1 = (string)p["t"]; if (KomSyncOff && pt1 != "p2p-admcmd" && pt1 != "p2p-admack" && pt1 != "p2p-srvinfo") return;   // ⏸ wstrzymana
            foreach (var c in KomMyServers())
            {
                if (KomLanPeer(c.Id)) continue;   // w tej samej sieci domowej - już dostał pakietem
                var link = _komRemote.TryGetValue(c.Id, out var r) ? r : _komPeerLink.TryGetValue(c.Id, out var l) ? l : null;
                if (link != null && _komLinkOk.ContainsKey(c.Id)) KomSendFrame(link, new JsonObject { ["t"] = "p2p-srv", ["id"] = _komId, ["to"] = c.Id, ["p"] = p.DeepClone() });
            }
        }
        // pakiet od mojego serwera łączem: to, co w domu przyszłoby pakietem z sieci domowej
        void KomSrvIn(string peerId, JsonNode inner)
        {
            if (inner == null || !_komLinkOk.ContainsKey(peerId) || !KomIsMember(peerId)) return;   // tylko członek z dowodem tożsamości na tym połączeniu   // tylko moje serwery
            _komNetPeer = peerId;
            try { KomRelayFromLan(inner, IPAddress.None); } catch (Exception ex) { App.LogError(ex); } finally { _komNetPeer = null; }
        }
        // co minutę: stałe połączenie z każdym moim serwerem spoza tej sieci domowej (gdy on nie połączył się już ze mną)
        void KomPeerLinks()
        {
            if (_komRelayListener == null) return;
            foreach (var c in KomMyServers())
            {
                if (KomLanPeer(c.Id) || _komRemote.ContainsKey(c.Id) || _komPeerLink.ContainsKey(c.Id) || !_komPeerDial.TryAdd(c.Id, true)) continue;
                var peer = c;
                _ = Task.Run(async () =>
                {
                    KomRemoteConn me = null;
                    try
                    {
                        var fs = await KomRelayConnect(peer, "main", null, null, CancellationToken.None); if (fs == null) return;
                        me = new KomRemoteConn { Fs = fs }; _komPeerLink[peer.Id] = me;
                        App.LogInfo("Łącze serwerów: połączono z " + peer.Name + " przez internet");
                        KomSendFrame(me, KomSrvIdFrame(peer.Id));   // 🏛 dowód członkostwa - dane serwerów dopiero po sprawdzeniu po obu stronach
                        while (true)
                        {
                            var p = JsonNode.Parse(fs.ReadFrame()); var t = (string)p?["t"];
                            if (t == "p2p-srvid" && (string)p["id"] == peer.Id) { if (KomSrvIdIn(peer.Id, p)) { KomSendFrame(me, new JsonObject { ["t"] = "p2p-srv", ["id"] = _komId, ["to"] = peer.Id, ["p"] = KomSrvInfoNow() }); KomEvHave(); } continue; }
                            if (t == "p2p-srv" && (string)p["id"] == peer.Id) { KomSrvIn(peer.Id, p["p"]); continue; }
                            if (t == null || !t.StartsWith("p2p-", StringComparison.Ordinal)) continue;
                            if ((string)p["to"] is string pt && pt != _komId) continue;   // tylko do mnie (wiadomości od ludzi do tego komputera)
                            await Dispatcher.InvokeAsync(() => { try { KomHandle(p, IPAddress.Loopback); } catch (Exception ex) { App.LogError(ex); } });
                        }
                    }
                    catch (Exception) { }
                    finally
                    {
                        if (me != null) { _komPeerLink.TryRemove(new KeyValuePair<string, KomRemoteConn>(peer.Id, me)); _komLinkOk.TryRemove(peer.Id, out _); try { me.Fs.Dispose(); } catch (Exception) { } App.LogInfo("Łącze serwerów: rozłączono z " + peer.Name + " – ponowię za minutę"); }
                        _komPeerDial.TryRemove(peer.Id, out _);
                    }
                });
            }
            foreach (var kv in _komPeerLink) KomSendFrame(kv.Value, new JsonObject { ["t"] = "relay-ping" });   // utrzymanie połączenia (jak telefon)
        }
        // 🤝 DOŁĄCZANIE SERWERA: serwer dodany zaproszeniem albo „Mój adres” jest zwykłym kontaktem (znajomym) - dostępu do moich danych nie ma.
        // Gdy ogłasza się jako serwer Velivo (p2p-here srv), ten serwer raz pyta właściciela: „to mój serwer?”. Tak = uprawnienie „moje urządzenie” (Dom):
        // wspólny sekret kluczy, wymiana kontaktów, skrzynki, dziennika i łącze serwerów (tożsamość = klucz pary sprawdzany przy każdym połączeniu).
        // Drugi serwer pyta swojego właściciela tak samo - zaufanie zawsze po obu stronach, więc serwer znajomego nigdy nie wejdzie sam. Nie = pamiętane, bez ponownych pytań.
        bool _komSrvAsking;
        void KomSrvAsk()
        {
            if (_komRelayListener == null || _komSrvAsking || KomOddany) return;
            var no = _komOpt["srvNo"] as JsonArray;
            KomClInit(); KomClMigrate();
            var c = _komContacts.FirstOrDefault(x => !x.IsGroup && x.Id != _komId && !KomIsMember(x.Id) && !KomClMine(x.Id) && _komSrvAt.TryGetValue(x.Id, out var sa) && sa != DateTime.MinValue && DateTime.UtcNow - sa < TimeSpan.FromMinutes(10)
                && (no == null || !no.Any(n => (string)n == x.Id)));
            if (c == null) return;
            _komSrvAsking = true;
            try
            {
                var yes = MessageBox.Show(PairDialogOwner(), "„" + c.Name + L.T("” to serwer Velivo.\n\nCzy to Twój serwer?\n\nTak – będzie działał razem z Twoimi serwerami: te same wiadomości, pliki, kontakty i kopie; gdy jeden serwer się wyłączy, drugi przejmie pracę. Na tamtym serwerze zatwierdź to samo.\n\nNie – zostaje zwykłym kontaktem (np. serwer znajomego) i nie dostanie Twoich danych."),
                    L.T("Twój serwer?"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                if (yes) { App.LogInfo("Serwery: " + c.Name + " zatwierdzony jako mój serwer (czekam na zatwierdzenie po tamtej stronie)"); KomClApprove(c); }
                else { if (no == null) _komOpt["srvNo"] = no = new JsonArray(); no.Add(c.Id); KomSave(); }
            }
            finally { _komSrvAsking = false; }
        }
        void KomSrvInfoSend() { _ = Task.Run(() => { try { KomBoxPeers(KomSrvInfoNow()); } catch (Exception) { } }); }   // w tle - odcisk skrzynki czyta pliki
        static void KomLogKeep(string p, Func<string, bool> keep)
        {
            try { if (File.Exists(p)) { var l = File.ReadAllLines(p).Where(x => !string.IsNullOrWhiteSpace(x)).Where(keep).ToList(); VelivoData.SafeWrite(p, string.Join(Environment.NewLine, l) + (l.Count > 0 ? Environment.NewLine : "")); } } catch (Exception ex) { App.LogError(ex); }
        }
        // przechowywanie (ustawia właściciel): pliki i wiadomości w skrzynce oraz wpisy dziennika starsze niż N dni - usuwane; raz na godzinę
        DateTime _komExpireAt;
        void KomBoxExpire()
        {
            if (DateTime.UtcNow - _komExpireAt < TimeSpan.FromHours(1)) return; _komExpireAt = DateTime.UtcNow;
            try { KomEv.Prune(DateTimeOffset.UtcNow.AddDays(-KomBoxDays).ToUnixTimeMilliseconds()); } catch (Exception ex) { App.LogError(ex); }   // 🗄 dziennik zdarzeń - tylko z okresu przechowywania
            var limit = DateTime.Now.AddDays(-KomBoxDays);
            try
            {
                var root = Path.Combine(DataDir, "skrzynka");
                if (Directory.Exists(root)) foreach (var d in Directory.GetDirectories(root))
                    foreach (var hdr in Directory.GetFiles(d, "*.hdr").Where(x => File.GetLastWriteTime(x) < limit))
                    {
                        foreach (var ext in new[] { ".hdr", ".bin", ".src" }) try { File.Delete(Path.ChangeExtension(hdr, ext)); } catch (Exception) { }
                        KomBoxLog(Path.GetFileName(d), "usunięty – odbiorca nie odebrał pliku przez " + KomBoxDays + " dni (ustawienie „Przechowuj”)");
                    }
                long lim = DateTimeOffset.UtcNow.AddDays(-KomBoxDays).ToUnixTimeMilliseconds(); bool ch = false;
                foreach (var kv in _komStore) lock (kv.Value) ch |= kv.Value.RemoveAll(x => (long?)x["ts"] is long t && t < lim) > 0;
                if (ch) KomStoreSave();
                foreach (var p in new[] { Path.Combine(DataDir, "skrzynka.log"), Path.Combine(DataDir, "skrzynka-serwery.log") })
                if (File.Exists(p)) { var keep = File.ReadLines(p).Where(l => !(l.Length > 19 && DateTime.TryParse(l.Substring(0, 19), out var t) && t < limit)).ToList(); VelivoData.SafeWrite(p, string.Join(Environment.NewLine, keep) + Environment.NewLine); }
            }
            catch (Exception ex) { App.LogError(ex); }
            KomBoxPeers(new JsonObject { ["t"] = "p2p-boxdays", ["id"] = _komId, ["days"] = KomBoxDays, ["at"] = (long?)_komOpt["boxDaysAt"] ?? 0 });   // moje serwery - to samo przechowywanie (nowsze ustawienie wygrywa)
        }
        async void KomServerCenter(Window owner)
        {
            if (!await KomAdmLogin(owner)) return;   // 🔐 klucz sprzętowy albo hasło administratora
            var w = new Window { Title = "🛠 " + L.T("Panel administratora") + " – " + Environment.MachineName, Width = 1100, Height = 760, WindowState = WindowState.Maximized, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            string Ago(DateTime t) => t.ToString("yyyy-MM-dd HH:mm");
            Action rOps = null, rInfra = null, rFiles = null, rDiag = null, rAcc = null, rKeys = null, AttnFill = null; Border attn = null; TextBlock cProb = null; string attnSig = null; TabControl tabs = null;   // używane w All() (przyciski tworzone wcześniej)
            // kafelki
            var cards = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Margin = new Thickness(0, 0, 0, 8) };
            TextBlock Card(string sub) { var v = new TextBlock { FontSize = 22, FontWeight = FontWeights.SemiBold }; cards.Children.Add(new Border { BorderThickness = new Thickness(1), BorderBrush = AdmB(0x34405e), Background = AdmB(0x232b42), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 8, 0),
                Child = new StackPanel { Children = { v, new TextBlock { Text = sub, Opacity = 0.75 } } } }); return v; }
            var cSrv = Card(L.T("serwer")); var cFiles = Card(L.T("pliki czekają")); var cMsg = Card(L.T("wiadomości czekają")); var cOn = Card(L.T("osób w sieci")); var cVer = Card(L.T("wersje serwerów"));
            var search = new TextBox { Padding = new Thickness(4), ToolTip = L.T("Szukaj w otwartej tabeli (osoba, zdarzenie, adres…)") };
            // stan słowami z tego, co serwer naprawdę widzi: odbiorca połączony z TYM serwerem (wysyła), z innym (tamten oddaje), poza siecią
            string Why(string id, bool copy) =>
                _komRemote.ContainsKey(id) || (_komLanSeen.TryGetValue(id, out var ls) && DateTime.UtcNow - ls < TimeSpan.FromSeconds(100)) ? L.T("połączony z tym serwerem – wysyłam (próba co minutę)")
                : KomOnlineAny(id) ? (copy ? L.T("w sieci przez inny serwer – oddaje go tamten serwer, tu zapasowa kopia") : L.T("w sieci przez inny serwer – ten serwer czeka, aż odbiorca połączy się z nim"))
                : L.T("poza siecią – wyślę, gdy się pojawi");
            // 1. skrzynka
            var (tBox, rBox) = KomTable(new[] { L.T("Osoba"), L.T("Plik (rozmiar KB)"), L.T("Czeka od"), L.T("Stan") }, () =>
            {
                var l = new List<string[]>(); var root = Path.Combine(DataDir, "skrzynka");
                foreach (var d in Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>())
                {
                    var id = Path.GetFileName(d); var name = KomContactById(id)?.Name ?? id;
                    foreach (var bin in Directory.GetFiles(d, "*.bin").Select(x => new FileInfo(x)))
                        l.Add(new[] { name, (bin.Length / 1024) + " KB" + (Path.GetFileName(bin.FullName).StartsWith('k') ? L.T(" (kopia od innego serwera)") : ""), Ago(bin.LastWriteTime), Why(id, Path.GetFileName(bin.FullName).StartsWith('k')), id + "|" + bin.FullName });   // ostatnie pole (niewidoczne) - do usuwania
                }
                foreach (var kv in _komStore) { List<JsonNode> ms; lock (kv.Value) ms = kv.Value.ToList(); foreach (var m in ms)
                    l.Add(new[] { KomContactById(kv.Key)?.Name ?? kv.Key, L.T("wiadomość (tekst) od ") + (KomContactById((string)m["id"] ?? "")?.Name ?? "?"),
                        (long?)m["ts"] is long ts ? Ago(DateTimeOffset.FromUnixTimeMilliseconds(ts).LocalDateTime) : "", Why(kv.Key, false), kv.Key + "|m:" + (string)m["mid"] }); }
                return l;
            }, search);
            // 2. osoby i urządzenia
            var (tPeople, rPeople) = KomTable(new[] { L.T("Stan"), L.T("Osoba"), L.T("Rodzaj"), L.T("Połączenie"), L.T("Adres"), L.T("Ostatnio") }, () =>
                _komContacts.Where(x => !x.IsGroup).Select(c =>
                {
                    var way = new List<string>();
                    if (_komRemote.ContainsKey(c.Id)) way.Add(L.T("przez internet z tym serwerem"));
                    bool lan = _komLanSeen.TryGetValue(c.Id, out var ls) && DateTime.UtcNow - ls < TimeSpan.FromSeconds(100); if (lan) way.Add(L.T("sieć domowa"));
                    if (_komViaRelay.TryGetValue(c.Id, out var vr)) way.Add(L.T("przez serwer ") + (_komContacts.FirstOrDefault(r => _komLanAddr.TryGetValue(r.Id, out var ra) && ra.Equals(vr))?.Name ?? vr.ToString()));
                    return new[] { KomOnlineAny(c.Id) ? L.T("● w sieci") : L.T("○ poza siecią"), c.Name, !string.IsNullOrEmpty(c.Relay) ? L.T("mój serwer") + " (" + c.Relay + ")" : c.Dom ? L.T("moje urządzenie") : L.T("znajomy"),
                        way.Count > 0 ? string.Join(", ", way) : "—", _komLanAddr.TryGetValue(c.Id, out var la) ? la.ToString() : "", _komSeenAt.TryGetValue(c.Id, out var sa) ? Ago(sa) : "", c.Id };
                }).ToList(), search);
            // 3. dziennik skrzynki, 4. błędy Velivo - z plików (najnowsze na górze)
            List<string[]> LogRows(string file, int n)
            {
                var p = Path.Combine(DataDir, file); if (!File.Exists(p)) return new List<string[]>();
                return File.ReadLines(p).Where(l => !string.IsNullOrWhiteSpace(l)).Reverse().Take(n).Select(l => l.Length > 21 && DateTime.TryParse(l.Substring(0, 19), out _) ? new[] { l.Substring(0, 19), l.Substring(19).Trim() } : new[] { "", l }).ToList();
            }
            // dziennik wszystkich moich serwerów: własny + wpisy przysłane przez inne serwery w domu, razem po czasie
            var (tLog, rLog) = KomTable(new[] { L.T("Czas"), L.T("Serwer"), L.T("Zdarzenie (osoba i co się stało)") }, () =>
                LogRows("skrzynka.log", 3000).Concat(LogRows("skrzynka-serwery.log", 3000)).Select(r =>
                {
                    var m = System.Text.RegularExpressions.Regex.Match(r[1], @"^\[([^\]]+)\]\s+(.*)$");
                    return m.Success ? new[] { r[0], m.Groups[1].Value, m.Groups[2].Value } : new[] { r[0], Environment.MachineName, r[1] };
                }).GroupBy(r => r[0] + "|" + r[1] + "|" + r[2]).Select(g => g.First()).OrderByDescending(r => r[0]).Take(5000).ToList(), search);   // ten sam wpis od innego serwera przychodzi dwoma portami - pokazywany raz
            // bledy.log: wpis = linia z czasem, treść (bywa wielowierszowa), pusta linia - jeden wiersz tabeli na wpis (wcześniej czas i treść w osobnych wierszach)
            List<string[]> ErrRows()
            {
                var p = Path.Combine(DataDir, "bledy.log"); if (!File.Exists(p)) return new List<string[]>();
                string txt; try { txt = File.ReadAllText(p).Replace("\r\n", "\n"); } catch (Exception) { return new List<string[]>(); }
                return txt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Reverse().Take(3000).Select(b =>
                {
                    var ls = b.Trim('\n').Split('\n'); bool t = ls[0].Length >= 19 && DateTime.TryParse(ls[0].Substring(0, 19), out _);
                    var msg = string.Join("  ⏎ ", (t ? ls.Skip(1) : ls).Select(x => x.Trim()).Where(x => x.Length > 0));
                    return new[] { t ? ls[0].Substring(0, 19) : "", msg.Length > 500 ? msg.Substring(0, 500) + "…" : msg };
                }).Where(r => r[1].Length > 0).ToList();
            }
            var (tErr, rErr) = KomTable(new[] { L.T("Czas"), L.T("Błąd Velivo") }, ErrRows, search);
            // 5. informacje techniczne
            var (tInfo, rInfo) = KomTable(new[] { L.T("Co"), L.T("Wartość") }, () =>
            {
                long boxSize = 0; try { var root = Path.Combine(DataDir, "skrzynka"); if (Directory.Exists(root)) boxSize = Directory.GetFiles(root, "*.bin", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length); } catch (Exception) { }
                return new List<string[]> {
                    new[] { L.T("Komputer"), Environment.MachineName }, new[] { L.T("Windows"), Environment.OSVersion.VersionString },
                    new[] { L.T("Wersja Velivo"), System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "" },
                    new[] { L.T("Folder danych"), DataDir }, new[] { L.T("Serwer (pośrednik)"), _komRelayListener != null ? L.T("działa, port ") + _komRelayPort : L.T("wyłączony") },
                    new[] { L.T("Adres domu"), _komRelayHost }, new[] { L.T("Router / porty"), _komRelayRouter },
                    new[] { L.T("Ten komputer w internecie"), _komUp != null ? L.T("połączony z serwerem ") + _komUpRelay?.Name : L.T("w domu") },
                    new[] { L.T("Kontakty"), _komContacts.Count(x => !x.IsGroup).ToString() }, new[] { L.T("Moje serwery widoczne w domu"), string.Join(", ", _komLanRelays.Keys.Select(k => KomContactById(k)?.Name ?? k)) },
                    new[] { L.T("Skrzynka na dysku"), (boxSize / 1048576.0).ToString("0.0") + " MB" }, new[] { L.T("Przechowywanie"), KomBoxDays + L.T(" dni") },
                    new[] { L.T("Czas działania Velivo"), (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).ToString(@"d\d\ hh\:mm") } };
            }, search);
            // 📊 Przegląd: wszystkie moje serwery naraz (ten + wieści od innych co minutę)
            var (tOver, rOver) = KomTable(new[] { L.T("Serwer"), L.T("Stan"), L.T("Wersja (kompilacja)"), L.T("Port"), L.T("Adres domu"), L.T("Router / porty"), L.T("Połączeni przez internet"), L.T("Pliki"), L.T("Wiadomości"), L.T("Działa od"), L.T("Ostatnia wieść") }, () =>
            {
                var l = new List<string[]>();
                string[] Row(JsonObject i, string state, string last) => new[] { (string)i["name"], state, (string)i["ver"], ((int?)i["port"])?.ToString() ?? "", (string)i["host"], (string)i["router"], (string)i["conns"], ((int?)i["files"])?.ToString() ?? "", ((int?)i["msgs"])?.ToString() ?? "", (string)i["since"], last };
                var me = KomSrvInfoNow(); l.Add(Row(me, (bool?)me["on"] == true ? L.T("● działa (ten komputer)") : L.T("○ serwer wyłączony (ten komputer)"), L.T("teraz")));
                foreach (var kv in _komSrvInfo.Where(kv => DateTime.UtcNow - kv.Value.At < TimeSpan.FromHours(12))) l.Add(Row(kv.Value.Info, DateTime.UtcNow - kv.Value.At < TimeSpan.FromMinutes(3) ? ((bool?)kv.Value.Info["on"] == true ? L.T("● działa") : L.T("○ serwer wyłączony")) : L.T("○ brak wieści – wyłączony albo poza siecią domową"), Ago(kv.Value.At.ToLocalTime())));
                // serwery bez wieści od 12 h (odłączone: CHUWI, HP) - nie zaśmiecają listy; wracają same, gdy się odezwą
                foreach (var c in _komContacts.Where(x => !x.IsGroup && !string.IsNullOrEmpty(x.Relay) && !_komSrvInfo.ContainsKey(x.Id) && (KomOnline(x.Id) || (_komSeenAt.TryGetValue(x.Id, out var xs) && DateTime.Now - xs < TimeSpan.FromHours(12)))))
                    l.Add(new[] { c.Name, KomOnline(c.Id) ? L.T("● w sieci – starsza wersja (nie podaje stanu)") : L.T("○ brak wieści – wyłączony albo starsza wersja"), "?", "", c.Relay, "", "", "", "", "", _komSeenAt.TryGetValue(c.Id, out var sa) ? Ago(sa) : "" });
                return l;
            }, search);
            // 🖥 Serwery: sterowanie tym serwerem
            var srvState = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 0, 0, 10) };
            Button Act(string text, string tip, Action a) { var bt = new Button { Content = text, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 8), ToolTip = tip }; bt.Click += (s, e) => { try { a(); } catch (Exception ex) { App.LogError(ex); MessageBox.Show(w, ex.Message, "⚠"); } }; return bt; }
            Button onOff = null;   // przypisany niżej - All() (w przycisku) czyta go przez SrvFill
            onOff = Act("", L.T("Włącza albo wyłącza serwer (pośrednika rozmów) na tym komputerze"), () =>
            {
                if (_komRelayOn && MessageBox.Show(w, L.T("Wyłączyć serwer na tym komputerze? Osoby połączone z nim przejdą na inny mój serwer."), "🖥", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                _komRelayOn = !_komRelayOn; if (!_komRelayOn) _komRelayAsked = true; KomSave(); KomRelayStart(); if (_komRelayOn) KomAnnounce(); All();
            });
            var acts = new WrapPanel { Children = {
                onOff,
                Act("🔄 " + L.T("Uruchom serwer od nowa"), L.T("Zamyka połączenia i uruchamia serwer ponownie (telefony połączą się same w ciągu kilkudziesięciu sekund)"), () => { if (!_komRelayOn) return; KomRelayStart(); KomAnnounce(); KomBoxLog(_komId, "serwer uruchomiony ponownie (przycisk w panelu administratora)"); All(); }),
                Act("🔍 " + L.T("Sprawdź porty i router teraz"), L.T("Sprawdza połączeniem z adresu domu, czy port serwera i 443 są otwarte, i odnawia przekierowanie w routerze (UPnP)"), () => { _komRelayRouter = L.T("Sprawdzam porty…"); All(); _ = Task.Run(async () => { await KomRelayAutoAsync(); _ = Dispatcher.BeginInvoke(new Action(All)); }); }),
                Act("🛡 " + L.T("Otwórz zaporę Windows"), L.T("Sprawdza zaporę Windows i w razie potrzeby otwiera port serwera (Windows zapyta o zgodę)"), () => { int pt = _komRelayPort; _ = Task.Run(() => { bool ok = KomFirewallOk(pt); _ = Dispatcher.BeginInvoke(new Action(() => { if (ok) MessageBox.Show(w, L.T("Zapora Windows jest otwarta dla serwera."), "🛡"); else { _komFwAsked = DateTime.MinValue; KomFirewallCheck(); } })); }); }),
                Act("⚙ " + L.T("Adres domu i port…"), L.T("Okno ustawień serwera: adres domu, port, konta i kontakty"), () => KomRelayDialog(w)),
                Act("📤 " + L.T("Wyślij stan do moich serwerów"), L.T("Od razu wymienia stan z innymi moimi serwerami w domu (normalnie co minutę)"), () => { KomSrvInfoSend(); KomAnnounce(); }) } };
            var pServers = new StackPanel { Margin = new Thickness(8), Children = { srvState, acts,
                new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Text = L.T("Te przyciski działają na tym komputerze. Stan wszystkich moich serwerów naraz – zakładka Przegląd. Zmiana na jednym serwerze nie wyłącza pozostałych: osoby połączone z wyłączonym serwerem przechodzą na inny.") } } };
            void SrvFill()
            {
                onOff.Content = _komRelayOn ? "⏻ " + L.T("Wyłącz serwer") : "⏻ " + L.T("Włącz serwer");
                srvState.Text = (_komRelayListener != null ? L.T("● Serwer działa na porcie ") + _komRelayPort : _komRelayOn ? L.T("⚠ Serwer włączony, ale nie nasłuchuje (port zajęty?)") : L.T("○ Serwer wyłączony"))
                    + "\n" + L.T("Adres domu: ") + (string.IsNullOrEmpty(_komRelayHost) ? L.T("ustalam…") : _komRelayHost) + "\n" + L.T("Router / porty: ") + _komRelayRouter
                    + "\n" + L.T("Połączeni przez internet: ") + (_komRemote.IsEmpty ? "—" : string.Join(", ", _komRemote.Keys.Select(k => KomContactById(k)?.Name ?? "?")))
                    + (_komLastReject != null ? "\n" + L.T("Ostatnio nie wpuszczono: ") + _komLastReject : "") + "\n" + L.T("Wersja: ") + KomBuild;
            }
            // zakładka Serwery - tło jak okno (bez tego biała z jasnym, nieczytelnym tekstem)
            ScrollViewer SrvView() { var sv = new ScrollViewer { Content = pServers, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; sv.SetBinding(Control.BackgroundProperty, new System.Windows.Data.Binding("Background") { Source = w }); return sv; }
            // ===== 🛠 PANEL ADMINISTRATORA – pełne zarządzanie (makieta dokumentacja/makieta-panel-administratora.html) =====
            ListView Lv(FrameworkElement t) => t as ListView;
            List<string[]> Sel(FrameworkElement t) => Lv(t)?.SelectedItems.Cast<string[]>().ToList() ?? new List<string[]>();
            FrameworkElement Page(string help, FrameworkElement body, params Button[] bs)
            {
                var dp = new DockPanel();
                var hb = new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 4, 0, 6) }; DockPanel.SetDock(hb, Dock.Top); dp.Children.Add(hb);
                if (bs.Length > 0) { var wp = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) }; foreach (var b in bs) wp.Children.Add(b); DockPanel.SetDock(wp, Dock.Top); dp.Children.Add(wp); }
                dp.Children.Add(body); return dp;
            }
            string Short(string x) => string.IsNullOrEmpty(x) ? "-" : x.Length > 8 ? x.Substring(0, 8) : x;
            // 🏛 Infrastruktura: moje serwery (certyfikaty), kandydaci, odłączeni
            FrameworkElement tInfraT; (tInfraT, rInfra) = KomTable(new[] { L.T("Serwer"), L.T("Rola"), L.T("Certyfikat"), L.T("Połączenie"), L.T("Wersja"), L.T("Identyfikator serwera") }, () =>
            {
                var l = new List<string[]>(); KomClInit();
                lock (_komClLock)
                {
                    if (_komCl == null) return l;
                    foreach (var cert in _komCl.Certs.Values.Where(_komCl.Valid))
                    {
                        var d = (string)cert["d"]; bool me = d == _komId; var cc = KomContactById(d);
                        var info = _komSrvInfo.TryGetValue(d, out var si) ? si : default;
                        string conn = me ? L.T("ten komputer") : KomLanPeer(d) ? L.T("● sieć domowa") : _komLinkOk.ContainsKey(d) ? L.T("● łącze przez internet – certyfikat sprawdzony") : L.T("○ brak połączenia");
                        l.Add(new[] { me ? Environment.MachineName + L.T(" (ten)") : cc?.Name ?? Short(d), L.T("członek infrastruktury"), L.T("ważny od ") + DateTimeOffset.FromUnixTimeMilliseconds((long?)cert["at"] ?? 0).LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                            conn, me ? KomBuild : (string)info.Info?["ver"] ?? "?", Short((string)cert["s"]), "m|" + d });
                    }
                    foreach (var r in _komCl.Revoked.Values) l.Add(new[] { Short((string)r["s"]), L.T("odłączony"), L.T("odwołany ") + DateTimeOffset.FromUnixTimeMilliseconds((long?)r["at"] ?? 0).LocalDateTime.ToString("yyyy-MM-dd HH:mm"), "-", "-", Short((string)r["s"]), "r|" });
                }
                foreach (var c in _komContacts.Where(x => !x.IsGroup && x.Id != _komId && !KomIsMember(x.Id) && _komSrvAt.TryGetValue(x.Id, out var sa) && sa != DateTime.MinValue && DateTime.UtcNow - sa < TimeSpan.FromDays(2)).ToList())
                    l.Add(new[] { c.Name, KomClMine(c.Id) ? L.T("czeka na „Tak” po tamtej stronie") : (_komOpt["srvNo"] as JsonArray)?.Any(n => (string)n == c.Id) == true ? L.T("serwer znajomego (odmówiono)") : L.T("serwer Velivo – nie należy do moich"), "-", KomOnline(c.Id) ? L.T("● w sieci") : L.T("○ poza siecią"), "-", "-", "k|" + c.Id });
                return l;
            }, search);
            // 🛠 polecenia dla serwerów: wykonuje backend wybranego serwera, wynik w „Operacje administratora”
            void AdmDo(IEnumerable<string> targets, string op)
            {
                var ts = targets.ToList(); if (ts.Count == 0) { MessageBox.Show(w, L.T("Zaznacz serwer (członka) na liście Infrastruktura."), "🛠"); return; }
                var name = KomAdmOps.First(x => x.Op == op).Name;
                if ((op == "off" || op == "syncoff") && MessageBox.Show(w, L.T(name) + ": " + string.Join(", ", ts.Select(x => x == "*" ? L.T("wszystkie serwery") : KomSrvTag(x))) + "?", "🛠", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                foreach (var x in ts) KomAdmSend(x, op);
                tabs.SelectedIndex = 13; All();
            }
            List<string> SelSrv() => Sel(tInfraT).Where(r => r[6].StartsWith("m|")).Select(r => r[6].Substring(2)).ToList();
            Button OpsButton(string text, string tip, Func<List<string>> targets)
            {
                var b = Act(text, tip, () => { }); var cm = new ContextMenu();
                foreach (var (op, nm) in KomAdmOps) { var o2 = op; var mi = new MenuItem { Header = L.T(nm) }; mi.Click += (s, e) => AdmDo(targets(), o2); cm.Items.Add(mi); }
                b.ContextMenu = cm; b.Click += (s, e) => { cm.PlacementTarget = b; cm.IsOpen = true; }; return b;
            }
            FrameworkElement tOpsT; (tOpsT, rOps) = KomTable(new[] { L.T("Czas"), L.T("Serwer"), L.T("Kierunek"), L.T("Polecenie"), L.T("Czynność"), L.T("Od → do"), L.T("Wynik") }, () =>
            {
                var l = new List<string[]>(); var p = Path.Combine(DataDir, "adm.log");
                try { if (File.Exists(p)) foreach (var line in File.ReadLines(p)) { var a = line.Split(" | "); if (a.Length >= 7) { var n = KomAdmOps.FirstOrDefault(x => x.Op == a[4]).Name; a[4] = n != null ? L.T(n) : a[4]; l.Add(a); } } } catch (Exception) { }
                l.Reverse(); return l.Take(3000).ToList();
            }, search);
            var tOps = Page(L.T("Dziennik operacji administratora: każde polecenie (kto, komu, kiedy) i wynik zwrócony przez serwer, który je wykonał. WYSŁANE → WYKONANE / BŁĄD / ODRZUCONE (zły podpis, nie mój serwer, starsze niż doba)."), tOpsT,
                OpsButton("🛠 " + L.T("Polecenie dla wszystkich serwerów ▾"), L.T("Wykonują wszystkie moje serwery (także ten)"), () => new List<string> { "*" }));
            var tInfra = Page(L.T("Moje serwery tworzą jedną infrastrukturę: każdy ma własny klucz i certyfikat podpisany kluczem infrastruktury, sprawdzany przy każdym połączeniu. Serwer znajomego bez Twojego „Tak” nie dostaje żadnych danych. Odłączenie = nowy klucz dla pozostałych."), tInfraT,
                Act("🤝 " + L.T("Dołącz zaznaczony serwer"), L.T("Zaznaczony serwer Velivo zostanie Twoim serwerem (na tamtym serwerze też potwierdź „Tak”)"), () => { foreach (var r in Sel(tInfraT).Where(r => r[6].StartsWith("k|"))) if (KomContactById(r[6].Substring(2)) is { } c) { if (_komOpt["srvNo"] is JsonArray no) foreach (var x in no.Where(x => (string)x == c.Id).ToList()) no.Remove(x); KomClApprove(c); } All(); }),
                Act("⛔ " + L.T("Odłącz zaznaczony serwer"), L.T("Przestanie dostawać Twoje dane; pozostałe serwery dostaną nowy klucz"), () => { var ids = Sel(tInfraT).Where(r => r[6].StartsWith("m|") && r[6] != "m|" + _komId).Select(r => r[6].Substring(2)).ToList(); if (ids.Count == 0) { MessageBox.Show(w, L.T("Zaznacz serwer (członka) na liście."), "⛔"); return; } if (MessageBox.Show(w, L.T("Odłączyć od Twoich serwerów: ") + string.Join(", ", ids.Select(x => KomContactById(x)?.Name ?? x)) + "?", "⛔", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; foreach (var x in ids) if (KomContactById(x) is { } c) KomClRevoke(c); All(); }),
                Act("🔄 " + L.T("Wyślij stan infrastruktury teraz"), L.T("Od razu wymienia certyfikaty i odwołania z moimi serwerami (normalnie co 10 min)"), () => { KomClSyncNow(); KomPeerLinks(); }),
                OpsButton("🛠 " + L.T("Polecenie dla zaznaczonych ▾"), L.T("Uruchom od nowa, włącz / wyłącz, porty, zapora, kolejki, synchronizacja – wykonuje zaznaczony serwer"), SelSrv),
                OpsButton("🛠 " + L.T("Polecenie dla wszystkich ▾"), L.T("Wykonują wszystkie moje serwery (także ten)"), () => new List<string> { "*" }));
            // 📎 Pliki: los każdego pliku w jednym wierszu (z diagnostyki wszystkich etapów)
            List<string[]> DiagRows() { var l = new List<string[]>(); foreach (var f in new[] { "serwery-diag.log.1", "serwery-diag.log" }) { var p = Path.Combine(DataDir, f); if (!File.Exists(p)) continue; try { foreach (var line in File.ReadLines(p)) { if (line.Trim().Length == 0) continue; var a = line.Split(" | "); if (a.Length >= 8) l.Add(a); } } catch (Exception) { } } return l; }
            FrameworkElement tFilesT; (tFilesT, rFiles) = KomTable(new[] { L.T("Czas"), L.T("Od → do"), L.T("Etapy (wszystkie serwery)"), L.T("Stan"), L.T("SHA-256"), L.T("Plik") }, () =>
                DiagRows().Where(a => a[3] == "plik").GroupBy(a => a[4]).Select(g =>
                {
                    var first = g.FirstOrDefault(a => a[2] == "PRZYJĘTY") ?? g.First(); var last = g.Last();
                    string st = g.Any(a => a[2] == "POTWIERDZONA" && a[7].Contains("potwierdził")) ? L.T("✓ doręczony") : g.Any(a => a[7].Contains("odmowa")) ? L.T("✗ odmowa") : L.T("… czeka");
                    var steps = string.Join(" · ", g.Select(a => a[2].ToLower() + " (" + a[1] + ")").Distinct());
                    var sha = g.Select(a => a.Length > 8 ? a[8].Replace("sha256: ", "") : null).FirstOrDefault(x => x != null) ?? "-";
                    return new[] { first[0].Substring(0, 16), first[5].Replace("z: ", "") + " → " + first[6].Replace("do: ", ""), steps, st, Short(sha), g.Key };
                }).OrderByDescending(r => r[0]).Take(3000).ToList(), search);
            var tFiles = Page(L.T("Każdy plik w jednym wierszu: od kogo, do kogo, przez które serwery przeszedł (przyjęty, kopia na drugim serwerze, wysłany, potwierdzony) i czy doszedł. SHA-256 – ten sam na każdym serwerze = te same dane."), tFilesT);
            // 🔬 Diagnostyka: pełny zapis etapów i porównań
            FrameworkElement tDiagT; (tDiagT, rDiag) = KomTable(new[] { L.T("Czas"), L.T("Serwer"), L.T("Etap"), L.T("Rodzaj"), L.T("Identyfikator"), L.T("Z"), L.T("Do"), L.T("Wynik") }, () =>
                DiagRows().Select(a => new[] { a[0], a[1], a[2], a[3], a[4], a[5].Replace("z: ", ""), a[6].Replace("do: ", ""), a[7] + (a.Length > 8 ? " | " + a[8] : "") }).Reverse().Take(5000).ToList(), search);
            var tDiag = Page(L.T("Diagnostyka synchronizacji (serwery-diag.log): każdy etap wiadomości i pliku, porównania SKRZYNKA i HISTORIA z innymi serwerami (ZGODNA / RÓŻNA) i wpisy RÓŻNICA dla konkretnej pozycji. Wpisz w 🔍 np. RÓŻN, PORÓWNANIE albo identyfikator."), tDiagT,
                Act("🔍 " + L.T("Tylko porównania"), L.T("Pokaż wyniki porównań z innymi serwerami"), () => search.Text = "PORÓWNANIE"),
                Act("⚠ " + L.T("Tylko różnice"), L.T("Pokaż niezgodności"), () => search.Text = "RÓŻN"),
                Act("🧹 " + L.T("Wyczyść diagnostykę"), L.T("Usuwa serwery-diag.log na tym komputerze"), () => { if (MessageBox.Show(w, L.T("Wyczyścić diagnostykę na tym komputerze?"), "🧹", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; foreach (var f in new[] { "serwery-diag.log", "serwery-diag.log.1" }) try { File.Delete(Path.Combine(DataDir, f)); } catch (Exception) { } All(); }));
            // 📝 Konta (numer + PIN) na moich serwerach
            FrameworkElement tAccT; (tAccT, rAcc) = KomTable(new[] { L.T("Imię"), L.T("Numer"), L.T("Założone"), L.T("W sieci"), L.T("Id") }, () =>
                KomAccounts().Select(kv => new[] { (string)kv.Value?["name"] ?? "?", (string)kv.Value?["nr"] ?? "-", DateTimeOffset.FromUnixTimeMilliseconds((long?)kv.Value?["at"] ?? 0).LocalDateTime.ToString("yyyy-MM-dd HH:mm"), KomOnlineAny(kv.Key) ? L.T("● tak") : L.T("○ nie"), Short(kv.Key), kv.Key }).ToList(), search);
            var tAcc = Page(L.T("Konta założone w Komunikatorze (imię, numer, PIN) – wspólne dla moich serwerów. Usunięcie działa na wszystkich serwerach: osoba nie może pisać przez moje serwery."), tAccT,
                Act("🗑 " + L.T("Usuń zaznaczone konta"), L.T("Usuwa konto i osobę na wszystkich moich serwerach"), () => { var ids = Sel(tAccT).Select(r => r[5]).ToList(); if (ids.Count == 0) return; if (MessageBox.Show(w, L.T("Usunąć konta: ") + string.Join(", ", Sel(tAccT).Select(r => r[0])) + "?", "🗑", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; foreach (var x in ids) { KomAccounts().Remove(x); _komStore.TryRemove(x, out _); try { Directory.Delete(KomBoxDir(x), true); } catch (Exception) { } KomForget(x, true); } KomSave(); All(); }),
                KomRegisterButton());
            // 🔑 Klucze sprzętowe
            FrameworkElement tKeysT; (tKeysT, rKeys) = KomTable(new[] { L.T("Klucz"), L.T("Identyfikator"), L.T("Chroni") }, () =>
                KomHwKeys().Select(k => new[] { string.IsNullOrEmpty(k.Name) ? L.T("Klucz sprzętowy") : k.Name, Short(k.Id), L.T("otwieranie komunikatora, kopia zapasowa (zamiast hasła)") }).ToList(), search);
            var tKeys = Page(L.T("Klucze sprzętowe (YubiKey i inne FIDO2): otwierają komunikator dotknięciem i odblokowują kopię zapasową bez hasła. Miej co najmniej dwa (zapasowy w bezpiecznym miejscu). Ostatnie sprawdzenie: ") + ((long?)_komOpt["klucze-test"] is long kt && kt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(kt).LocalDateTime.ToString("yyyy-MM-dd") : L.T("nigdy")), tKeysT,
                Act("➕ " + L.T("Dodaj klucz"), L.T("Windows Hello / PIN, potem dotknij nowy klucz"), () => { KomHwKeyAdd(w); }),
                Act("✔ " + L.T("Sprawdź klucze"), L.T("Dotknij po kolei każdy klucz – sprawdzenie, czy działa"), () => { KomHwKeyTest(w); }),
                Act("⚙ " + L.T("Usuń / wymień klucz…"), L.T("Usunięcie zgubionego albo zepsutego klucza (po Windows Hello)"), () => { KomHwKeySetup(w); All(); }),
                Act("🔒 " + L.T("Blokada komunikatora: ") + (_komLock ? L.T("wł.") : L.T("wył.")), L.T("Otwieranie komunikatora po Windows Hello albo kluczu"), () => { _komLock = !_komLock; KomSave(); MessageBox.Show(w, L.T("Blokada komunikatora: ") + (_komLock ? L.T("wł.") : L.T("wył.")), "🔒"); }));
            // ⚙ Ustawienia serwera: wszystko, co dotąd było w różnych oknach
            var tSet = Page(L.T("Ustawienia tego serwera i moich serwerów w jednym miejscu."), new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85, Margin = new Thickness(0, 10, 0, 0),
                    Text = L.T("NIE ZAIMPLEMENTOWANE (jeszcze nie działa – nie udaję): blokowanie / odblokowanie użytkownika, tworzenie konta przez administratora, urządzenia osoby i rozłączanie sesji, backup i przywracanie serwera z panelu, role i priorytety serwerów, zasoby (procesor, pamięć, dysk) innych serwerów, historia awarii, wspólny dokument konfiguracji dla wszystkich serwerów, zmiana portu / adresu innego serwera zdalnie, historia rozmów i połączeń na serwerach. Kolejność: dokumentacja/architektura-panel-administratora.md.") },
                Act("🌍 " + L.T("Adres domu, port, rozmowy poza domem…"), L.T("Okno serwera: adres domu, port, 443, sprawdzanie portów"), () => KomRelayDialog(w)),
                KomServerContactsButton(w),
                Act("🔗 " + L.T("Mój adres / zaproszenie…"), L.T("Link, przez który ktoś (albo Twój nowy serwer) dodaje ten serwer"), () => KomMyAddressDialog(w)),
                Act("👥 " + L.T("Połącz znajomych…"), L.T("Przedstaw sobie dwie osoby"), () => KomIntroduceDialog(w)),
                Act("🧹 " + L.T("Porządki na dysku…"), L.T("Pliki komunikatora spoza rozmów, resztki – do Kosza po podsumowaniu"), () => KomCleanup(w)),
                Act("📂 " + L.T("Folder danych"), L.T("Otwórz folder danych Velivo (dzienniki, skrzynka)"), () => { try { System.Diagnostics.Process.Start("explorer.exe", DataDir); } catch (Exception) { } }),
                Act("🔍 " + L.T("Sprawdź porty i router teraz"), L.T("Połączenie z adresem domu na porcie serwera i 443, odnowienie UPnP"), () => { _komRelayRouter = L.T("Sprawdzam porty…"); _ = Task.Run(async () => { await KomRelayAutoAsync(); _ = Dispatcher.BeginInvoke(new Action(() => All())); }); }),
                Act("🛡 " + L.T("Otwórz zaporę Windows teraz"), L.T("Reguła dla Velivo i portów serwera (jedno „Tak” w Windows)"), () => { _komFwAsked = DateTime.MinValue; KomFirewallCheck(); }),
                Act("🔁 " + L.T("Uruchom serwer od nowa"), L.T("Zamyka i otwiera nasłuch serwera (połączenia wrócą same)"), () => { KomRelayStart(); All(); }),
                Act("🔐 " + L.T("Zmień hasło administratora"), L.T("Hasło do panelu administratora na tym serwerze (logowanie także kluczem sprzętowym)"), () => KomAdmChangePass(w)),
                Act("🚪 " + L.T("Wyloguj"), L.T("Zamyka panel; następne otwarcie wymaga logowania"), () => { _komAdmUntil = DateTime.MinValue; w.Close(); }));
            // ⚠ Wymaga uwagi (na górze) + kafelek „problemy”
            cProb = Card(L.T("problemy teraz"));
            attn = new Border { Background = AdmB(0x2a2133), BorderBrush = AdmB(0x6b3a4a), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8) };
            AttnFill = () =>
            {
                var ps = new List<(string Text, (string Label, Action Run)[] Acts)>();
                try
                {
                    var root = Path.Combine(DataDir, "skrzynka");
                    foreach (var d in Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>())
                    {
                        var old = Directory.GetFiles(d, "*.bin").Select(x => new FileInfo(x)).Where(x => DateTime.Now - x.LastWriteTime > TimeSpan.FromHours(24)).ToList(); if (old.Count == 0) continue;
                        var id = Path.GetFileName(d); var nm = KomContactById(id)?.Name ?? Short(id); var days = (int)(DateTime.Now - old.Min(x => x.LastWriteTime)).TotalDays;
                        ps.Add(("📎 " + old.Count + L.T(" plików do ") + nm + L.T(" czeka od ") + Math.Max(1, days) + L.T(" dni") + (KomOnlineAny(id) ? L.T(" – osoba jest w sieci, a pliki nie schodzą (sprawdź Dziennik skrzynki)") : L.T(" – osoba nie łączy się z żadnym moim serwerem")),
                            new (string, Action)[] { (L.T("Pokaż pliki"), () => { tabs.SelectedIndex = 2; search.Text = nm; }), (L.T("Usuń te pliki"), () => { if (MessageBox.Show(w, L.T("Usunąć czekające pliki do ") + nm + "?", "🗑", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; var hs = new JsonArray(); foreach (var f in old) { var b = Path.ChangeExtension(f.FullName, null); foreach (var e in new[] { ".hdr", ".bin", ".src", ".bin.sha" }) try { File.Delete(b + e); } catch (Exception) { } hs.Add(Path.GetFileName(b).Split('-').Last()); KomBoxLog(id, "usunięty ręcznie przez właściciela: " + (f.Length / 1024) + " KB"); } KomBoxPeers(new JsonObject { ["t"] = "p2p-boxdone", ["id"] = _komId, ["to"] = id, ["files"] = hs, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }); All(); }) }));
                    }
                }
                catch (Exception) { }
                foreach (var kv in _komSrvInfo.Where(kv => DateTime.UtcNow - kv.Value.At < TimeSpan.FromMinutes(3)))
                    if (((string)kv.Value.Info["ver"] ?? "").Split(' ')[0] != KomBuild.Split(' ')[0] || ((string)kv.Value.Info["ver"] ?? "").Length > 0 && (string)kv.Value.Info["ver"] != KomBuild && false)
                        ps.Add(("🖥 " + L.T("Serwer ") + ((string)kv.Value.Info["name"] ?? "?") + L.T(" ma inną wersję Velivo (") + (string)kv.Value.Info["ver"] + L.T(") – zaktualizuj, by działały te same funkcje"), new (string, Action)[] { (L.T("Serwery"), () => tabs.SelectedIndex = 1) }));
                foreach (var c in KomMyServers().Where(c => !KomLanPeer(c.Id) && !_komLinkOk.ContainsKey(c.Id)))
                    ps.Add(("🔗 " + L.T("Brak połączenia z moim serwerem ") + c.Name + L.T(" (ani w domu, ani przez internet) – wyłączony albo bez dostępu"), new (string, Action)[] { (L.T("Infrastruktura"), () => tabs.SelectedIndex = 7) }));
                foreach (var c in _komContacts.Where(x => !x.IsGroup && x.Id != _komId && !KomIsMember(x.Id) && !KomClMine(x.Id) && (_komOpt["srvNo"] as JsonArray)?.Any(n => (string)n == x.Id) != true && _komSrvAt.TryGetValue(x.Id, out var sa) && sa != DateTime.MinValue && DateTime.UtcNow - sa < TimeSpan.FromMinutes(10)).ToList())
                {
                    var cc = c;
                    ps.Add(("🖥 " + L.T("Serwer Velivo ") + c.Name + L.T(" działa, ale nie należy do Twoich serwerów"), new (string, Action)[] { (L.T("To mój serwer"), () => { KomClApprove(cc); All(); }), (L.T("Nie (znajomy)"), () => { KomClAdd(_komOpt, "srvNo", cc.Id); KomSave(); All(); }) }));
                }
                foreach (var kv in _komFpCmp.Where(kv => kv.Value.fp == "R"))
                    ps.Add(("🔬 " + (kv.Key.EndsWith("|ev") ? L.T("Historia zdarzeń") : L.T("Skrzynka")) + L.T(" różni się od serwera ") + KomSrvTag(kv.Key.Split('|')[0]) + L.T(" od ponad 3 min"), new (string, Action)[] { (L.T("Diagnostyka"), () => { tabs.SelectedIndex = 9; search.Text = "RÓŻN"; }) }));
                if (_komRelayRouter.Contains(L.T("nie odpowiada"))) ps.Add(("🌍 " + _komRelayRouter, new (string, Action)[] { (L.T("Serwery"), () => tabs.SelectedIndex = 1) }));
                if (KomSyncOff) ps.Add(("⏸ " + L.T("Synchronizacja tego serwera jest WSTRZYMANA – nie wysyła kopii ani zdarzeń do innych serwerów"), new (string, Action)[] { (L.T("Wznów"), () => { KomAdmSend(_komId, "syncon"); All(); }) }));
                if (KomHwKeys().Count == 1) ps.Add(("🔑 " + L.T("Masz tylko jeden klucz sprzętowy – dodaj zapasowy (zgubienie jedynego = tylko hasło)"), new (string, Action)[] { (L.T("Klucze"), () => tabs.SelectedIndex = 11) }));
                cProb.Text = ps.Count.ToString(); cProb.Foreground = AdmB(ps.Count > 0 ? 0xf87171 : 0x4ade80);
                var sig = string.Join("\n", ps.Select(p => p.Text)); if (sig == attnSig) return; attnSig = sig;
                if (ps.Count == 0) { attn.Visibility = Visibility.Collapsed; return; }
                attn.Visibility = Visibility.Visible;
                var sp = new StackPanel(); sp.Children.Add(new TextBlock { Text = "⚠ " + L.T("Wymaga uwagi") + " (" + ps.Count + ")", FontWeight = FontWeights.SemiBold, FontSize = 15, Foreground = AdmB(0xfbbf24), Margin = new Thickness(0, 0, 0, 4) });
                foreach (var p in ps)
                {
                    var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                    var bp = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(bp, Dock.Right);
                    foreach (var (lab, run) in p.Acts) { var bb = new Button { Content = lab, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) }; bb.Click += (s, e) => { try { run(); } catch (Exception ex) { App.LogError(ex); } }; bp.Children.Add(bb); }
                    row.Children.Add(bp); row.Children.Add(new TextBlock { Text = p.Text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = AdmB(0xe8ecf4) });
                    sp.Children.Add(row);
                }
                attn.Child = sp; KomStyle(w); AdmLook(w, tabs);
            };
            tabs = new TabControl();
            foreach (var (h, el) in new (string, FrameworkElement)[] { ("📊 " + L.T("Przegląd"), tOver), ("🖥 " + L.T("Serwery"), SrvView()), ("📬 " + L.T("Skrzynka"), tBox), ("👥 " + L.T("Osoby i urządzenia"), tPeople), ("📜 " + L.T("Dziennik skrzynki"), tLog), ("⚠ " + L.T("Błędy Velivo"), tErr), ("ℹ " + L.T("Informacje techniczne"), tInfo), ("🏛 " + L.T("Infrastruktura"), tInfra), ("📎 " + L.T("Pliki"), tFiles), ("🔬 " + L.T("Diagnostyka"), tDiag), ("📝 " + L.T("Konta"), tAcc), ("🔑 " + L.T("Klucze sprzętowe"), tKeys), ("⚙ " + L.T("Ustawienia"), tSet), ("📋 " + L.T("Operacje administratora"), tOps) })
                tabs.Items.Add(new TabItem { Header = h, Content = el });
            // 👆 kafelki prowadzą do szczegółów: klik = właściwa zakładka z odfiltrowaną listą (tam zaznaczasz i usuwasz / sprawdzasz)
            void Go(TextBlock card, int tab, string q, string tip)
            {
                if (card.Parent is not StackPanel sp || sp.Parent is not Border b) return;
                b.Cursor = System.Windows.Input.Cursors.Hand; b.ToolTip = tip;
                b.MouseEnter += (s, e) => b.BorderBrush = AdmB(0x6d7cff); b.MouseLeave += (s, e) => b.BorderBrush = AdmB(0x34405e);
                b.PreviewMouseLeftButtonDown += (s, e) => { e.Handled = true; tabs.SelectedIndex = tab; search.Text = q; search.Focus(); };   // Preview - klik działa też na napisach w kafelku
                sp.Children.Add(new TextBlock { Text = "▸ " + L.T("pokaż szczegóły"), FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 2, 0, 0) });
            }
            Go(cSrv, 1, "", L.T("Kliknij – zakładka Serwery: stan, włącz / wyłącz, uruchom od nowa, sprawdź porty, zapora, adres i port"));
            Go(cFiles, 2, "KB", L.T("Kliknij – zakładka Skrzynka z samymi plikami: komu, od kiedy czeka i dlaczego; zaznacz i „Usuń zaznaczone”, a co doręczono – Dziennik skrzynki"));
            Go(cMsg, 2, L.T("wiadomość (tekst)"), L.T("Kliknij – zakładka Skrzynka z samymi wiadomościami: komu, od kiedy czeka i dlaczego; zaznacz i „Usuń zaznaczone”"));
            Go(cOn, 3, "", L.T("Kliknij – zakładka Osoby i urządzenia: kto jest w sieci, przez który serwer, ostatnio widziany"));
            Go(cVer, 0, "", L.T("Kliknij – zakładka Przegląd: wersja i stan każdego mojego serwera"));
            Go(cProb, 9, "RÓŻN", L.T("Kliknij – problemy są opisane w ramce „Wymaga uwagi” nad zakładkami; tu Diagnostyka z różnicami między serwerami"));
            void All()
            {
                int files = 0; try { var root = Path.Combine(DataDir, "skrzynka"); if (Directory.Exists(root)) files = Directory.GetFiles(root, "*.bin", SearchOption.AllDirectories).Length; } catch (Exception) { }
                cSrv.Text = _komRelayListener != null ? L.T("Działa") : L.T("Wyłączony"); cFiles.Text = files.ToString(); cMsg.Text = _komStore.Sum(kv => { lock (kv.Value) return kv.Value.Count; }).ToString();
                cOn.Text = _komContacts.Count(x => !x.IsGroup && KomOnlineAny(x.Id)) + " / " + _komContacts.Count(x => !x.IsGroup);
                var vers = _komSrvInfo.Where(kv => DateTime.UtcNow - kv.Value.At < TimeSpan.FromMinutes(3)).Select(kv => ((string)kv.Value.Info["ver"] ?? "").Split(' ')[0]).Append(KomBuild.Split(' ')[0]).Distinct().Count();
                cVer.Text = vers <= 1 ? L.T("zgodne") : L.T("RÓŻNE – zaktualizuj");
                cSrv.Foreground = AdmB(_komRelayListener != null ? 0x4ade80 : 0xf87171); cVer.Foreground = AdmB(vers <= 1 ? 0x4ade80 : 0xf87171);
                cFiles.Foreground = AdmB(files > 0 ? 0xfbbf24 : 0xe8ecf4); cMsg.Foreground = AdmB(cMsg.Text != "0" ? 0xfbbf24 : 0xe8ecf4);
                rOver(); SrvFill(); rBox(); rPeople(); rLog(); rErr(); rInfo(); rInfra?.Invoke(); rFiles?.Invoke(); rDiag?.Invoke(); rAcc?.Invoke(); rKeys?.Invoke(); rOps?.Invoke(); AttnFill?.Invoke();
            }
            // przechowywanie: ustawia właściciel, idzie do moich serwerów
            var days = new[] { 7, 14, 30, 90, 365 };
            var keep = new ComboBox { Width = 130, ItemsSource = days.Select(d => d + L.T(" dni")).ToList(), SelectedIndex = Math.Max(0, Array.IndexOf(days, KomBoxDays)),
                ToolTip = L.T("Jak długo serwer trzyma niedoręczone pliki i wiadomości oraz wpisy dziennika – to samo ustawienie dostają moje inne serwery") };
            keep.SelectionChanged += (s, e) => { _komOpt["boxDays"] = days[Math.Max(0, keep.SelectedIndex)]; _komOpt["boxDaysAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); KomSave(); _komExpireAt = DateTime.MinValue; KomBoxExpire(); All(); };
            var copy = new Button { Content = "📋 " + L.T("Kopiuj raport"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0),
                ToolTip = L.T("Kopiuje otwartą tabelę i informacje techniczne (bez kluczy i treści wiadomości) – wklej, zgłaszając problem") };
            copy.Click += (s, e) =>
            {
                string Tab(FrameworkElement el) => el is ListView lv && lv.ItemsSource is IEnumerable<string[]> r ? string.Join(Environment.NewLine, r.Select(x => string.Join("  |  ", x))) : "";
                try { Clipboard.SetText(DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "  " + Environment.MachineName + Environment.NewLine + "== " + ((TabItem)tabs.SelectedItem).Header + " ==" + Environment.NewLine
                    + Tab((FrameworkElement)((TabItem)tabs.SelectedItem).Content) + Environment.NewLine + "== " + L.T("Informacje techniczne") + " ==" + Environment.NewLine + Tab(tInfo)); copy.Content = "✅ " + L.T("Skopiowano"); } catch (Exception) { }
            };
            // 🗑 usuń zaznaczone pliki ze skrzynki (np. nie do doręczenia) - na tym serwerze, z wpisem w dzienniku
            // 🗑 pełna kontrola: zaznaczone w otwartej zakładce - na WSZYSTKICH moich serwerach (Skrzynka: pliki i wiadomości, Osoby: kontakt, Dziennik: czyszczenie)
            var del = new Button { Content = "🗑 " + L.T("Usuń zaznaczone"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0),
                ToolTip = L.T("Skrzynka: usuwa zaznaczone pliki i wiadomości; Osoby: usuwa osobę; Dziennik: czyści dziennik – na wszystkich moich serwerach. Ctrl+A – wszystko, Ctrl/Shift – kilka.") };
            del.Click += (s, e) =>
            {
                var tab = (FrameworkElement)((TabItem)tabs.SelectedItem).Content;
                var sel = tab is ListView lv ? lv.SelectedItems.Cast<string[]>().ToList() : new List<string[]>();
                if (tab is ListView lvu) lvu.UnselectAll();   // po usunięciu tabela ma się odświeżyć (przy kilku zaznaczonych odświeżanie czeka)
                if (tab == tLog || tab == tErr)
                {   // 📜 / ⚠ wpisy dziennika: zaznaczone - usuwane z plików na tym serwerze (wiersz = czas + treść)
                    if (sel.Count == 0) { MessageBox.Show(w, L.T("Zaznacz wpisy (Ctrl+A – wszystkie). Cały dziennik czyści przycisk „🧹 Wyczyść dziennik”."), "🗑"); return; }
                    if (MessageBox.Show(w, L.T("Usunąć zaznaczone wpisy dziennika: ") + sel.Count + "?", "🗑", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    if (tab == tErr)
                    {   // ⚠ bledy.log - wpisy blokami (czas, treść, pusta linia): usuwane całe wpisy o zaznaczonym czasie
                        var times = new HashSet<string>(sel.Select(r => r[0]).Where(x => x.Length > 0)); var ep = Path.Combine(DataDir, "bledy.log");
                        try { if (File.Exists(ep)) { var bl = File.ReadAllText(ep).Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Where(b => { var f = b.Trim('\n'); return !(f.Length >= 19 && times.Contains(f.Substring(0, 19))); }).ToList();
                            VelivoData.SafeWrite(ep, bl.Count == 0 ? "" : string.Join("\n\n", bl.Select(b => b.Trim('\n'))) + "\n\n"); } } catch (Exception ex) { App.LogError(ex); }
                        All(); return;
                    }
                    var gone = new HashSet<string>(sel.Select(r => r[0] + "|" + r[r.Length - 1]));
                    foreach (var p in tab == tLog ? new[] { "skrzynka.log", "skrzynka-serwery.log" } : new[] { "bledy.log" })
                        KomLogKeep(Path.Combine(DataDir, p), l => !(l.Length > 19 && gone.Contains(l.Substring(0, 19) + "|" + System.Text.RegularExpressions.Regex.Replace(l.Substring(19).Trim(), @"^\[[^\]]+\]\s+", ""))) && !gone.Contains("|" + l));
                    All(); return;
                }
                if (tab == tPeople)
                {
                    var ids = sel.Where(r => r.Length > 6).Select(r => r[6]).ToList();
                    if (ids.Count == 0) { MessageBox.Show(w, L.T("Zaznacz osoby w zakładce Osoby i urządzenia."), "🗑"); return; }
                    if (MessageBox.Show(w, L.T("Usunąć na wszystkich moich serwerach: ") + string.Join(", ", ids.Select(x => KomContactById(x)?.Name ?? x)) + L.T("? Nie będą mogli pisać przez moje serwery; ich czekające pliki i wiadomości znikną."), "🗑", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                    foreach (var x in ids) { KomBoxLog(x, "osoba usunięta ręcznie przez właściciela – nie może już pisać przez moje serwery"); _komStore.TryRemove(x, out _); try { Directory.Delete(KomBoxDir(x), true); } catch (Exception) { } KomForget(x, true); }
                    KomStoreSave(); All(); return;
                }
                if (tab != tBox) { MessageBox.Show(w, L.T("W tej zakładce nie ma czego usuwać."), "🗑"); return; }
                var rows = sel.Where(r => r.Length > 4 && r[4].Contains('|')).ToList();
                if (rows.Count == 0) { MessageBox.Show(w, L.T("Zaznacz pliki albo wiadomości w zakładce Skrzynka (Ctrl+A – wszystko)."), "🗑"); return; }
                if (MessageBox.Show(w, L.T("Usunąć ze skrzynki na wszystkich moich serwerach: ") + rows.Count + L.T(" pozycji? Odbiorca ich nie dostanie."), "🗑", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                foreach (var g in rows.GroupBy(r => r[4].Substring(0, r[4].IndexOf('|'))))
                {
                    var files = new JsonArray(); var mids = new JsonArray();
                    foreach (var r in g)
                    {
                        var what = r[4].Substring(r[4].IndexOf('|') + 1);
                        if (what.StartsWith("m:")) { var mid = what.Substring(2); if (_komStore.TryGetValue(g.Key, out var l2)) lock (l2) l2.RemoveAll(x => (string)x["mid"] == mid); mids.Add(mid); }
                        else { foreach (var ext in new[] { ".hdr", ".bin", ".src" }) try { File.Delete(Path.ChangeExtension(what, ext)); } catch (Exception) { } files.Add(Path.GetFileNameWithoutExtension(what).Split('-').Last()); }
                        KomBoxLog(g.Key, "usunięty ręcznie przez właściciela: " + r[1]);
                    }
                    KomStoreSave();
                    // inne moje serwery kasują swoje kopie (to samo „doręczone”, które już rozumieją; powtarzane 2 dni - wyłączony też skasuje)
                    foreach (var h in files) if ((string)h is string hs) _komBoxDoneOut[hs] = (g.Key, DateTime.UtcNow);
                    foreach (var m in mids) if ((string)m is string ms2) _komBoxDoneOut["m:" + ms2] = (g.Key, DateTime.UtcNow);
                    if (files.Count > 0) KomBoxPeers(new JsonObject { ["t"] = "p2p-boxdone", ["id"] = _komId, ["to"] = g.Key, ["files"] = files, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                    if (mids.Count > 0) KomBoxPeers(new JsonObject { ["t"] = "p2p-boxdone", ["id"] = _komId, ["to"] = g.Key, ["mids"] = mids, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                }
                All();
            };
            // 🧹 cały dziennik otwartej zakładki (📜 skrzynka – na wszystkich moich serwerach, ⚠ błędy – ten komputer); 💾 zapis tabeli do pliku
            var clear = new Button { Content = "🧹 " + L.T("Wyczyść dziennik"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0),
                ToolTip = L.T("Dziennik skrzynki – czyści na wszystkich moich serwerach; Błędy Velivo – czyści na tym komputerze") };
            clear.Click += (s, e) =>
            {
                var tab = (FrameworkElement)((TabItem)tabs.SelectedItem).Content;
                if (tab != tLog && tab != tErr) { MessageBox.Show(w, L.T("Otwórz zakładkę Dziennik skrzynki albo Błędy Velivo."), "🧹"); return; }
                if (MessageBox.Show(w, tab == tLog ? L.T("Wyczyścić dziennik skrzynki na wszystkich moich serwerach?") : L.T("Wyczyścić dziennik błędów Velivo na tym komputerze?"), "🧹", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                foreach (var p in tab == tLog ? new[] { "skrzynka.log", "skrzynka-serwery.log" } : new[] { "bledy.log" }) try { File.Delete(Path.Combine(DataDir, p)); } catch (Exception) { }
                if (tab == tLog) KomBoxPeers(new JsonObject { ["t"] = "p2p-boxlogclr", ["id"] = _komId, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                All();
            };
            var save = new Button { Content = "💾 " + L.T("Zapisz do pliku"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0), ToolTip = L.T("Zapisuje otwartą tabelę (po szukaniu – to, co widać) do pliku tekstowego") };
            save.Click += (s, e) =>
            {
                var tab = (FrameworkElement)((TabItem)tabs.SelectedItem).Content;
                var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "velivo-" + Environment.MachineName + "-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".txt", Filter = L.T("Pliki tekstowe") + "|*.txt" };
                if (dlg.ShowDialog(w) != true) return;
                try { File.WriteAllLines(dlg.FileName, (tab is ListView lv && lv.ItemsSource is IEnumerable<string[]> r ? r : Enumerable.Empty<string[]>()).Select(x => string.Join("\t", x))); } catch (Exception ex) { MessageBox.Show(w, ex.Message, "💾"); }
            };
            var folder = new Button { Content = "📂 " + L.T("Folder danych"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
            folder.Click += (s, e) => { try { System.Diagnostics.Process.Start("explorer.exe", DataDir); } catch (Exception) { } };
            var refresh = new Button { Content = "🔄 " + L.T("Odśwież"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) }; refresh.Click += (s, e) => All();
            var bar = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };   // pole szukania w osobnym wierszu na całą szerokość, przyciski pod nim zawijane (wąskie okno)
            bar.Children.Add(new DockPanel { Margin = new Thickness(0, 0, 0, 6), Children = { new TextBlock { Text = "🔍 ", VerticalAlignment = VerticalAlignment.Center }, search } });
            var right = new WrapPanel { Children = { new TextBlock { Text = L.T("Przechowuj: "), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) }, keep, refresh, del, clear, save, copy, folder } };
            foreach (FrameworkElement el in right.Children) el.Margin = new Thickness(el.Margin.Left, 2, el.Margin.Right, 2);
            bar.Children.Add(right);
            var help = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 0, 0, 8),
                Text = L.T("Wszystko o tym serwerze w jednym miejscu. Klik w nagłówek kolumny – sortowanie (drugi klik – odwrotnie), pole 🔍 – szukanie w otwartej tabeli. Niedoręczone pliki czekają na serwerze, a serwer próbuje co minutę; po czasie z „Przechowuj” są usuwane. Odświeża się samo co 5 s.") };
            // 🖱 menu kontekstowe (prawy przycisk) na każdej tabeli: czynności dla zaznaczonych wierszy + kopiuj / odśwież
            void Ctx(FrameworkElement t, params (string Label, Action Run)[] items)
            {
                if (t is not ListView lv) return;
                var cm = new ContextMenu();
                foreach (var (lab, run) in items) { if (lab == "-") { cm.Items.Add(new Separator()); continue; } var mi = new MenuItem { Header = lab }; mi.Click += (s, e) => { try { run(); } catch (Exception ex) { App.LogError(ex); } }; cm.Items.Add(mi); }
                if (cm.Items.Count > 0) cm.Items.Add(new Separator());
                var cp = new MenuItem { Header = "📋 " + L.T("Kopiuj zaznaczone wiersze") }; cp.Click += (s, e) => { try { Clipboard.SetText(string.Join(Environment.NewLine, lv.SelectedItems.Cast<string[]>().Select(r => string.Join("\t", r)))); } catch (Exception) { } }; cm.Items.Add(cp);
                var rf = new MenuItem { Header = "🔄 " + L.T("Odśwież") }; rf.Click += (s, e) => All(); cm.Items.Add(rf);
                lv.ContextMenu = cm;
            }
            void Del() => del.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            string SelName(FrameworkElement t, int col) => Sel(t).Select(r => r.Length > col ? r[col] : "").FirstOrDefault() ?? "";
            Ctx(tBox, ("🗑 " + L.T("Usuń zaznaczone (na wszystkich moich serwerach)"), Del), ("👤 " + L.T("Pokaż osobę w Osobach i urządzeniach"), () => { var n = SelName(tBox, 0); tabs.SelectedIndex = 3; search.Text = n; }), ("📜 " + L.T("Pokaż w Dzienniku skrzynki"), () => { var n = SelName(tBox, 0); tabs.SelectedIndex = 4; search.Text = n; }));
            Ctx(tPeople, ("🗑 " + L.T("Usuń osobę na wszystkich moich serwerach"), Del), ("📬 " + L.T("Pokaż jej czekające pliki"), () => { var n = SelName(tPeople, 1); tabs.SelectedIndex = 2; search.Text = n; }), ("📜 " + L.T("Pokaż w Dzienniku skrzynki"), () => { var n = SelName(tPeople, 1); tabs.SelectedIndex = 4; search.Text = n; }), ("📎 " + L.T("Pokaż jej pliki (los pliku)"), () => { var n = SelName(tPeople, 1); tabs.SelectedIndex = 8; search.Text = n; }));
            Ctx(tLog, ("🗑 " + L.T("Usuń zaznaczone wpisy"), Del), ("👤 " + L.T("Pokaż osobę"), () => { var n = (SelName(tLog, 2).Split("  ")[0]); tabs.SelectedIndex = 3; search.Text = n; }));
            Ctx(tErr, ("🗑 " + L.T("Usuń zaznaczone wpisy"), Del));
            Ctx(tInfraT, ("🤝 " + L.T("Dołącz jako mój serwer"), () => { foreach (var r in Sel(tInfraT).Where(r => r[6].StartsWith("k|"))) if (KomContactById(r[6].Substring(2)) is { } c) KomClApprove(c); All(); }),
                ("⛔ " + L.T("Odłącz od moich serwerów"), () => { foreach (var r in Sel(tInfraT).Where(r => r[6].StartsWith("m|") && r[6] != "m|" + _komId)) if (KomContactById(r[6].Substring(2)) is { } c && MessageBox.Show(w, L.T("Odłączyć ") + c.Name + "?", "⛔", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) KomClRevoke(c); All(); }),
                ("🚫 " + L.T("To serwer znajomego – nie pytaj"), () => { foreach (var r in Sel(tInfraT).Where(r => r[6].StartsWith("k|"))) KomClAdd(_komOpt, "srvNo", r[6].Substring(2)); KomSave(); All(); }),
                ("-", null), ("🔁 " + L.T("Uruchom serwer od nowa"), () => AdmDo(SelSrv(), "restart")), ("▶ " + L.T("Włącz serwer"), () => AdmDo(SelSrv(), "on")), ("⏹ " + L.T("Wyłącz serwer"), () => AdmDo(SelSrv(), "off")),
                ("🔍 " + L.T("Sprawdź porty i router"), () => AdmDo(SelSrv(), "ports")), ("🛡 " + L.T("Otwórz zaporę Windows"), () => AdmDo(SelSrv(), "firewall")), ("📬 " + L.T("Wyślij ponownie kolejki (skrzynka)"), () => AdmDo(SelSrv(), "flush")),
                ("🔄 " + L.T("Synchronizuj teraz"), () => AdmDo(SelSrv(), "sync")), ("⏸ " + L.T("Wstrzymaj synchronizację"), () => AdmDo(SelSrv(), "syncoff")), ("▶ " + L.T("Wznów synchronizację"), () => AdmDo(SelSrv(), "syncon")), ("-", null),
                ("🔄 " + L.T("Wyślij stan infrastruktury teraz"), () => { KomClSyncNow(); KomPeerLinks(); }), ("🔬 " + L.T("Diagnostyka tego serwera"), () => { var n = SelName(tInfraT, 0); tabs.SelectedIndex = 9; search.Text = n; }));
            Ctx(tFilesT, ("🔬 " + L.T("Wszystkie etapy tego pliku (Diagnostyka)"), () => { var id = SelName(tFilesT, 5); tabs.SelectedIndex = 9; search.Text = id; }), ("📋 " + L.T("Kopiuj SHA-256 i identyfikator"), () => { try { Clipboard.SetText(string.Join(Environment.NewLine, Sel(tFilesT).Select(r => r[5] + "  " + r[4]))); } catch (Exception) { } }));
            Ctx(tDiagT, ("🔎 " + L.T("Pokaż tylko ten identyfikator"), () => search.Text = SelName(tDiagT, 4)), ("⚠ " + L.T("Tylko różnice"), () => search.Text = "RÓŻN"), ("📊 " + L.T("Tylko porównania"), () => search.Text = "PORÓWNANIE"));
            Ctx(tAccT, ("🗑 " + L.T("Usuń konto i osobę na wszystkich serwerach"), () => { var ids = Sel(tAccT).Select(r => r[5]).ToList(); if (ids.Count == 0 || MessageBox.Show(w, L.T("Usunąć konta: ") + string.Join(", ", Sel(tAccT).Select(r => r[0])) + "?", "🗑", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; foreach (var x in ids) { KomAccounts().Remove(x); _komStore.TryRemove(x, out _); try { Directory.Delete(KomBoxDir(x), true); } catch (Exception) { } KomForget(x, true); } KomSave(); All(); }),
                ("👤 " + L.T("Pokaż w Osobach i urządzeniach"), () => { var n = SelName(tAccT, 0); tabs.SelectedIndex = 3; search.Text = n; }));
            Ctx(tKeysT, ("➕ " + L.T("Dodaj klucz"), () => KomHwKeyAdd(w)), ("✔ " + L.T("Sprawdź klucze"), () => KomHwKeyTest(w)), ("⚙ " + L.T("Usuń / wymień klucz…"), () => KomHwKeySetup(w)));
            Ctx(tOver, ("🌍 " + L.T("Adres domu, port, 443…"), () => KomRelayDialog(w)), ("🔍 " + L.T("Sprawdź porty i router teraz"), () => { _ = Task.Run(async () => { await KomRelayAutoAsync(); _ = Dispatcher.BeginInvoke(new Action(() => All())); }); }),
                ("🛡 " + L.T("Otwórz zaporę Windows teraz"), () => { _komFwAsked = DateTime.MinValue; KomFirewallCheck(); }), ("🔁 " + L.T("Uruchom serwer od nowa"), () => { KomRelayStart(); All(); }), ("🏛 " + L.T("Infrastruktura"), () => tabs.SelectedIndex = 7));
            var root2 = new DockPanel { Margin = new Thickness(16) };
            foreach (var el in new FrameworkElement[] { cards, attn, help, bar }) { DockPanel.SetDock(el, Dock.Top); root2.Children.Add(el); }
            root2.Children.Add(tabs);
            w.Content = root2;
            All();
            var tm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) }; tm.Tick += (s, e) => { if (w.IsActive || w.IsVisible) All(); }; tm.Start();
            w.Closed += (s, e) => tm.Stop();
            KomStyle(w); AdmLook(w, tabs); w.Show();
        }
        // po KomStyle (kolory motywu) - stały granatowy wygląd panelu, czytelny niezależnie od motywu
        static void AdmLook(Window w, TabControl tabs)
        {
            w.Background = AdmB(0x1b2133); w.Foreground = AdmB(0xe8ecf4);
            void Walk(object o)
            {
                if (o is Button b) { b.Background = AdmB(0x2a3350); b.BorderBrush = AdmB(0x44507a); b.Foreground = AdmB(0xe8ecf4); }
                else if (o is ListView lv) { lv.Background = AdmB(0x1b2133); lv.Foreground = AdmB(0xe8ecf4); lv.BorderBrush = AdmB(0x34405e); }
                else if (o is TextBox t) { t.Background = AdmB(0x232b42); t.Foreground = AdmB(0xe8ecf4); t.BorderBrush = AdmB(0x44507a); t.CaretBrush = AdmB(0xe8ecf4); }
                if (o is DependencyObject d) foreach (var c in LogicalTreeHelper.GetChildren(d)) Walk(c);
            }
            Walk(w);
            tabs.Background = AdmB(0x1b2133); tabs.BorderBrush = AdmB(0x34405e);
            var bd = new FrameworkElementFactory(typeof(Border)); bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6, 6, 0, 0)); bd.SetValue(Border.PaddingProperty, new Thickness(14, 7, 14, 7)); bd.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter)); cp.SetValue(ContentPresenter.ContentSourceProperty, "Header"); bd.AppendChild(cp);
            var ts = new Style(typeof(TabItem)); ts.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(TabItem)) { VisualTree = bd }));
            ts.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent)); ts.Setters.Add(new Setter(Control.ForegroundProperty, AdmB(0xaab3c8)));
            var ov = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; ov.Setters.Add(new Setter(Control.BackgroundProperty, AdmB(0x232b42)));
            var sel = new Trigger { Property = TabItem.IsSelectedProperty, Value = true }; sel.Setters.Add(new Setter(Control.BackgroundProperty, AdmB(0x2a3350))); sel.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            ts.Triggers.Add(ov); ts.Triggers.Add(sel);
            foreach (TabItem ti in tabs.Items) ti.Style = ts;
        }
        void KomRelayDialog(Window owner)
        {
            var on = new CheckBox { Content = L.T("Ten komputer jest pośrednikiem dla rozmów poza domem (musi być zawsze włączony)"), IsChecked = _komRelayOn, Margin = new Thickness(0, 0, 0, 10) };
            var host = new TextBox { Text = _komRelayAuto ? "" : _komRelayHost, MinWidth = 300, ToolTip = L.T("Puste – Velivo ustali adres domu samo. Możesz wpisać własny, np. z No-IP: mojdom.ddns.net") };
            var port = new TextBox { Text = _komRelayPort.ToString(), Width = 90, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = L.T("Port TCP przekierowany w routerze na ten komputer") };
            _komRelayStatusText = new TextBlock { Margin = new Thickness(0, 10, 0, 0), FontWeight = FontWeights.SemiBold };
            var info = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(0, 10, 0, 0), Opacity = 0.85,
                Text = L.T("Wystarczy zaznaczyć – Velivo samo ustali adres domu i spróbuje otworzyć port w routerze. Gdy router tego nie umie, przekieruj ten port TCP na ten komputer (jak dla Jellyfin). Pozostałe urządzenia dostaną adres same w sieci domowej. Korzystać mogą tylko Twoje kontakty; wiadomości i pliki są szyfrowane.") };
            var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            // najpierw jedno zdanie, co się dzieje (dla każdego); adres, port i szczegóły techniczne - schowane dla zaawansowanych
            _komRelaySummary = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(0, 4, 0, 8) };
            var more = new Expander { Header = L.T("Szczegóły i ustawienia (dla zaawansowanych)"), Margin = new Thickness(0, 10, 0, 0), Content = new StackPanel { Margin = new Thickness(0, 6, 0, 0), Children = {
                new TextBlock { Text = L.T("Adres domu (puste – ustalany automatycznie):") }, host,
                new TextBlock { Text = L.T("Port:"), Margin = new Thickness(0, 8, 0, 0) }, port,
                _komRelayStatusText, info } } };
            var grid = new StackPanel { Margin = new Thickness(16), Children = {
                on, _komRelaySummary,
                new TextBlock { Text = L.T("Serwer przekazuje Twoje rozmowy, gdy Ty albo Twoje kontakty jesteście poza domem (dane komórkowe, inny kraj). Działa sam – nic nie trzeba ustawiać."), TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Opacity = 0.8 },
                KomServerContactsButton(owner), KomBoxButton(owner), KomRegisterButton(), more, ok } };
            var d = new Window { Title = L.T("🌍 Rozmowy poza domem"), SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = grid };
            KomStyle(d);
            KomRelayStatus();
            if (_komRelayOn && _komRelayListener != null && _komUp == null) { _komRelayRouter = L.T("Sprawdzam porty…"); KomRelayStatus(); _ = KomRelayAutoAsync(); }   // stan zawsze z bieżącego sprawdzenia, nie sprzed godziny
            ok.Click += (s, e) => d.DialogResult = true;
            d.Closed += (s, e) => { _komRelayStatusText = null; _komRelaySummary = null; };
            d.ShowDialog();   // zamknięcie okna (✕ albo OK) zapisuje zmiany - wcześniej bez OK zostawał stary adres i port
            if (_komRelayOn && on.IsChecked != true) _komRelayAsked = true;   // wylaczone recznie - zostaje wylaczone
            _komRelayOn = on.IsChecked == true;
            var typed = host.Text.Trim();
            _komRelayAuto = typed.Length == 0;   // puste - adres ustala Velivo (i pilnuje zmian IP)
            if (!_komRelayAuto) _komRelayHost = typed;
            else if (!IPAddress.TryParse(_komRelayHost, out _)) _komRelayHost = "";   // automatycznie - stara, kiedyś wpisana nazwa (np. No-IP) nie zostaje; adres ustali sprawdzenie
            if (int.TryParse(port.Text.Trim(), out var pt) && pt > 0 && pt < 65536)
            {   // 41917–41919 zajmuje samo Velivo (filmy, sieć domowa, komunikator w domu) - serwer na nich nie ruszy; zostaje poprzedni port
                if (pt >= 41917 && pt <= 41919) MessageBox.Show(L.T("Port ") + pt + L.T(" używa samo Velivo w sieci domowej – serwer na nim nie zadziała. Wybierz 41920–41929 (ten sam co w routerze)."), "🌍", MessageBoxButton.OK, MessageBoxImage.Warning);
                else { _komRelayPortFixed = pt != 41920; _komRelayPort = pt; }   // OK w oknie - port zatwierdzony przez właściciela
            }
            KomSave();
            KomRelayStart();
            if (_komRelayOn) { KomAnnounce(); KomInfo(L.T("🌍 Pośrednik włączony – ustalam adres domu i port w routerze…")); }   // urzadzenia w domu od razu dostaja adres pośrednika
        }
    }
}
