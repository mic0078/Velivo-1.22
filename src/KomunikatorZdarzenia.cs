using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Velivo
{
    // 🗄 Wspólny dziennik zdarzeń moich serwerów (jeden logiczny system danych, bez serwera głównego).
    // Każda zmiana = zdarzenie { id, o (serwer, który je zapisał), n (kolejny numer na tym serwerze), ts, k (rodzaj), to, d (dane) }.
    // Serwery porównują „mam od serwera X do numeru N” i dosyłają sobie brakujące zdarzenia; zdarzenie o znanym id jest pomijane (bez duplikatów).
    // „Mam do N” liczone bez dziur: zdarzenie 7 przed 6 (zgubiony pakiet) nie przesuwa licznika - 6 zostanie dosłane.
    public sealed class KomEventLog
    {
        readonly string _file, _me;
        readonly object _lock = new object();
        readonly List<JsonObject> _ev = new List<JsonObject>();
        readonly HashSet<string> _ids = new HashSet<string>();
        readonly HashSet<(string, long)> _seqs = new HashSet<(string, long)>();
        readonly Dictionary<string, long> _max = new Dictionary<string, long>();

        public KomEventLog(string file, string me)
        {
            _file = file; _me = me;
            try
            {
                if (File.Exists(file))
                    foreach (var line in File.ReadLines(file))
                    {
                        JsonObject o; try { o = JsonNode.Parse(line) as JsonObject; } catch (Exception) { continue; }   // ucięta ostatnia linia (wyłączenie w trakcie zapisu) - pominięta
                        if (o == null) continue;
                        if (o["max"] is JsonObject hm) { foreach (var kv in hm) if ((long?)kv.Value is long v) _max[kv.Key] = Math.Max(_max.TryGetValue(kv.Key, out var c) ? c : 0, v); continue; }   // nagłówek po porządkach
                        Insert(o);
                    }
            }
            catch (Exception) { }
        }

        static bool Valid(JsonObject e) => e != null && (string)e["id"] is { Length: > 0 } && (string)e["o"] is { Length: > 0 } && (long?)e["n"] is long n && n > 0 && (string)e["k"] is { Length: > 0 };

        bool Insert(JsonObject e)
        {
            if (!Valid(e)) return false;
            var id = (string)e["id"]; var o = (string)e["o"]; var n = (long)e["n"];
            if (_ids.Contains(id) || _seqs.Contains((o, n))) return false;
            _ids.Add(id); _seqs.Add((o, n)); _ev.Add(e);
            var m = _max.TryGetValue(o, out var cur) ? cur : 0;
            while (_seqs.Contains((o, m + 1))) m++;
            _max[o] = m;
            return true;
        }

        void Append(JsonObject e) { try { File.AppendAllText(_file, e.ToJsonString() + "\n"); } catch (Exception) { } }

        // nowe zdarzenie na tym serwerze
        public JsonObject Add(string kind, string to, JsonNode data, long? ts = null)
        {
            lock (_lock)
            {
                long n = (_max.TryGetValue(_me, out var c) ? c : 0) + 1;
                while (_seqs.Contains((_me, n))) n++;
                var e = new JsonObject { ["id"] = Guid.NewGuid().ToString("N"), ["o"] = _me, ["n"] = n, ["ts"] = ts ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["k"] = kind, ["to"] = to, ["d"] = data?.DeepClone() };
                Insert(e); Append(e);
                return (JsonObject)e.DeepClone();
            }
        }

        // zdarzenie od innego serwera: true = nowe (do zastosowania), false = już znane albo uszkodzone
        public bool Merge(JsonObject e)
        {
            lock (_lock)
            {
                if (e == null) return false;
                var c = (JsonObject)e.DeepClone();
                if (!Insert(c)) return false;
                Append(c);
                return true;
            }
        }

        // „mam od każdego serwera do numeru N” (bez dziur)
        public JsonObject Have() { lock (_lock) { var h = new JsonObject(); foreach (var kv in _max) h[kv.Key] = kv.Value; return h; } }

        // zdarzenia, których drugi serwer nie ma (wg jego „mam”) - po kolei, najwyżej maxBytes (paczka mieści się w jednym pakiecie)
        public List<JsonObject> Missing(JsonObject have, int maxBytes)
        {
            lock (_lock)
            {
                var res = new List<JsonObject>(); int size = 0;
                foreach (var e in _ev.OrderBy(x => (string)x["o"], StringComparer.Ordinal).ThenBy(x => (long)x["n"]))
                {
                    long h = have != null && (long?)have[(string)e["o"]] is long v ? v : 0;
                    if ((long)e["n"] <= h) continue;
                    var len = Encoding.UTF8.GetByteCount(e.ToJsonString());
                    if (res.Count > 0 && size + len > maxBytes) break;
                    res.Add((JsonObject)e.DeepClone()); size += len;
                }
                return res;
            }
        }

        // czy drugi serwer ma coś, czego tu brakuje
        public bool Behind(JsonObject have)
        {
            lock (_lock) { if (have == null) return false; foreach (var kv in have) if ((long?)kv.Value is long v && v > (_max.TryGetValue(kv.Key, out var c) ? c : 0)) return true; return false; }
        }

        public IEnumerable<JsonObject> All(string kind) { lock (_lock) return _ev.Where(x => (string)x["k"] == kind).Select(x => (JsonObject)x.DeepClone()).ToList(); }
        public int Count { get { lock (_lock) return _ev.Count; } }

        // porządki: zdarzenia starsze niż granica usuwane; „mam do N” zostaje (nagłówek pliku), więc nic nie wraca od innych serwerów
        public int Prune(long beforeTs)
        {
            lock (_lock)
            {
                int before = _ev.Count;
                var keep = _ev.Where(x => ((long?)x["ts"] ?? 0) >= beforeTs).ToList();
                if (keep.Count == before) return 0;
                _ev.Clear(); _ids.Clear(); _seqs.Clear(); var max = new Dictionary<string, long>(_max);
                foreach (var e in keep) { _ev.Add(e); _ids.Add((string)e["id"]); _seqs.Add(((string)e["o"], (long)e["n"])); }
                _max.Clear(); foreach (var kv in max) _max[kv.Key] = kv.Value;
                foreach (var kv in max) for (long i = 1; i <= kv.Value; i++) _seqs.Add((kv.Key, i));   // numery do N uznane za znane (stare zdarzenia nie wrócą)
                var hdr = new JsonObject { ["max"] = Have() };
                try { VelivoData.SafeWrite(_file, hdr.ToJsonString() + "\n" + string.Join("", keep.Select(x => x.ToJsonString() + "\n"))); } catch (Exception) { }
                return before - keep.Count;
            }
        }
    }

    public partial class MainWindow
    {
        // 🗄 dziennik zdarzeń serwerów - krok 1: wiadomości czekające w skrzynce („msg”) i doręczenie („done”).
        // Działa równolegle ze starym sposobem (kopie skrzynki, p2p-boxdone) - stare urządzenia i serwery dalej działają.
        KomEventLog _komEvLog;
        KomEventLog KomEv => _komEvLog ??= new KomEventLog(Path.Combine(DataDir, "serwery-dziennik.jsonl"), _komId);
        HashSet<string> _komEvDone;   // wiadomości już doręczone (przez dowolny mój serwer) - nie wracają do skrzynki
        HashSet<string> KomEvDoneSet { get { if (_komEvDone == null) { var s = new HashSet<string>(); foreach (var e in KomEv.All("done")) if (e["d"] is JsonArray a) foreach (var x in a) if ((string)x is string m) s.Add(m); _komEvDone = s; } return _komEvDone; } }

        // wiadomość przyjęta do skrzynki na tym serwerze - zapis i od razu do moich serwerów
        void KomEvMsg(string to, JsonNode p)
        {
            try { if (_komRelayListener == null || to == null || p == null || Encoding.UTF8.GetByteCount(p.ToJsonString()) > 50000) return; KomEvPush(KomEv.Add("msg", to, p)); } catch (Exception ex) { App.LogError(ex); }
        }
        // wiadomości doręczone odbiorcy przez ten serwer - inne serwery kasują swoje kopie
        void KomEvDone(string to, IEnumerable<string> mids)
        {
            try
            {
                if (_komRelayListener == null || to == null) return;
                var a = new JsonArray(); lock (KomEvDoneSet) foreach (var m in mids) if (m != null && KomEvDoneSet.Add(m)) a.Add(m);
                if (a.Count > 0) KomEvPush(KomEv.Add("done", to, a));
            }
            catch (Exception ex) { App.LogError(ex); }
        }
        void KomEvPush(JsonObject e)
        {
            var p = new JsonObject { ["t"] = "p2p-evs", ["id"] = _komId, ["evs"] = new JsonArray(e) };
            foreach (var a in KomPeerAddrs()) KomLanRaw(p, a);
        }
        // co minutę i po starcie: „mam do N” do moich serwerów - kto ma więcej, dosyła brakujące
        void KomEvHave()
        {
            try { if (_komRelayListener == null) return; var p = new JsonObject { ["t"] = "p2p-evhave", ["id"] = _komId, ["have"] = KomEv.Have() }; foreach (var a in KomPeerAddrs()) KomLanRaw(p, a); }
            catch (Exception ex) { App.LogError(ex); }
        }

        // pakiety dziennika od moich serwerów (wątek sieci); true = obsłużony
        bool KomEvIn(JsonNode p, string t, string from, IPAddress addr)
        {
            if (t != "p2p-evhave" && t != "p2p-evs" && t != "p2p-srvbye") return false;
            if (!KomFromRelay(addr) || from == _komId || _komRelayListener == null) return true;
            try
            {
                if (t == "p2p-srvbye") { _komSrvInfo.TryRemove(from, out _); return true; }   // ☕ „wyłączam się” - nie liczę go jako działającego, jego ludzie przechodzą do mnie
                if (t == "p2p-evhave" && p["have"] is JsonObject have)
                {
                    var peerHave = (JsonObject)have.DeepClone();
                    for (int i = 0; i < 30; i++)   // do 30 paczek naraz (po ~8 KB - mało fragmentów UDP), reszta w następnej minucie
                    {
                        var miss = KomEv.Missing(have, 8000); if (miss.Count == 0) break;
                        KomLanRaw(new JsonObject { ["t"] = "p2p-evs", ["id"] = _komId, ["evs"] = new JsonArray(miss.Select(x => (JsonNode)x).ToArray()) }, addr);
                        foreach (var e in miss) { var o = (string)e["o"]; if ((long)e["n"] > ((long?)have[o] ?? 0)) have[o] = (long)e["n"]; }
                    }
                    if (KomEv.Behind(peerHave)) KomLanRaw(new JsonObject { ["t"] = "p2p-evhave", ["id"] = _komId, ["have"] = KomEv.Have() }, addr);   // tamten ma więcej - niech mi dośle
                    return true;
                }
                if (t == "p2p-evs" && p["evs"] is JsonArray evs)
                    foreach (var x in evs) if (x is JsonObject e && KomEv.Merge(e)) KomEvApply(e);
            }
            catch (Exception ex) { App.LogError(ex); }
            return true;
        }

        // zastosowanie zdarzenia od innego serwera w stanie tego serwera
        void KomEvApply(JsonObject e)
        {
            var k = (string)e["k"]; var to = (string)e["to"];
            if (to == null || to.Length != 32) return;
            if (k == "done" && e["d"] is JsonArray da)
            {
                var ms = new HashSet<string>(da.Select(x => (string)x).Where(x => x != null));
                lock (KomEvDoneSet) KomEvDoneSet.UnionWith(ms);
                if (_komStore.TryGetValue(to, out var dl)) { int r; lock (dl) r = dl.RemoveAll(x => ms.Contains((string)x["mid"])); if (r > 0) KomStoreSave(); }
                return;
            }
            if (k == "msg" && e["d"] is JsonObject pm && (string)pm["mid"] is string mid)
            {
                lock (KomEvDoneSet) if (KomEvDoneSet.Contains(mid)) return;   // już doręczona przez inny serwer
                var cp = (JsonObject)pm.DeepClone(); cp.Remove("fwd"); cp.Remove("box");
                if (_komRemote.TryGetValue(to, out var rc)) { KomSendFrame(rc, cp); KomEvDone(to, new[] { mid }); return; }   // odbiorca połączony ze mną - doręczam
                if (!_komContacts.ToArray().Any(c => c.Id == to)) return;
                var l = _komStore.GetOrAdd(to, _ => new List<JsonNode>());
                bool add; lock (l) { add = !l.Any(x => (string)x["mid"] == mid); if (add) { l.Add(cp); if (l.Count > 300) l.RemoveAt(0); } }
                if (add) KomStoreSave();
            }
        }
    }
}
