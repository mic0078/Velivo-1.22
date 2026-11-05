using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Velivo
{
    // 🏛 PRYWATNA INFRASTRUKTURA SERWERÓW (klaster) - osobno od kontaktów i od uprawnień „moje urządzenie”.
    // Klaster: identyfikator + klucz podpisu (ECDSA P-256), wspólny dla członków (każdy członek może przyjąć następnego - serwery równorzędne).
    // Serwer: własny klucz (ECDSA P-256), identyfikator = skrót klucza publicznego. Certyfikat członkostwa = podpis klastra na
    // {klaster, serwer, klucz publiczny serwera, identyfikator w komunikatorze, czas}. Odwołanie = podpisany wpis {serwer, czas} - wygrywa z certyfikatem.
    // Klasa bez okien i sieci - testowana osobno (KlasterTests).
    // 🔬 diagnostyka: SHA-256 kanonicznej postaci zapisanego obiektu (zaszyfrowana treść + metadane dostarczenia), bez znaczników transportu
    // między serwerami (fwd, box). Kodowanie jednoznaczne (wersja v1): każdy element ma typ i długość - O<liczba pól>{K<bajty>:<klucz><wartość>} (klucze
    // w kolejności bajtów UTF-8), A<liczba>…, S<bajty>:<tekst>, N<liczba w postaci R>, T/F, Z. Bez łączenia separatorami - brak kolizji z samego sklejania.
    // Wersja w skrócie („v1:…”) - zmiana formatu nie da fałszywej niezgodności: serwery porównują tylko tę samą wersję.
    public static class KomCanon
    {
        public const int Version = 1;
        static readonly HashSet<string> Skip = new HashSet<string> { "fwd", "box" };
        public static string Sha(JsonNode n) { var sb = new StringBuilder("velivo-diag-v1|"); Write(sb, n, true); return "v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))); }
        // lista elementów (np. wierszy skrzynki) - każdy z długością, w kolejności bajtów
        public static string ShaList(IEnumerable<string> items)
        {
            var sb = new StringBuilder("velivo-diag-v1-list|");
            foreach (var x in items.OrderBy(x => x, StringComparer.Ordinal)) Str(sb, x);
            return "v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        }
        static void Str(StringBuilder sb, string x) => sb.Append('S').Append(Encoding.UTF8.GetByteCount(x)).Append(':').Append(x);
        static void Write(StringBuilder sb, JsonNode n, bool top)
        {
            switch (n)
            {
                case null: sb.Append('Z'); return;
                case JsonObject o:
                    var kv = o.Where(x => !(top && Skip.Contains(x.Key))).OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
                    sb.Append('O').Append(kv.Count).Append('{');
                    foreach (var x in kv) { sb.Append('K'); Str(sb, x.Key); Write(sb, x.Value, false); }
                    sb.Append('}'); return;
                case JsonArray a:
                    sb.Append('A').Append(a.Count).Append('[');
                    foreach (var x in a) Write(sb, x, false);
                    sb.Append(']'); return;
                case JsonValue v:
                    if (v.TryGetValue<string>(out var st)) { Str(sb, st); return; }
                    if (v.TryGetValue<bool>(out var bo)) { sb.Append(bo ? 'T' : 'F'); return; }
                    var raw = v.ToJsonString();
                    if (decimal.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dm)) { sb.Append('N').Append((dm / 1.0000000000000000000000000000m).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(';'); return; }   // 5 = 5.0 = 5e0
                    if (raw == "true" || raw == "false") { sb.Append(raw == "true" ? 'T' : 'F'); return; }
                    if (raw.StartsWith("\"")) { Str(sb, System.Text.Json.JsonSerializer.Deserialize<string>(raw)); return; }
                    sb.Append('R'); Str(sb, raw); return;
            }
        }
    }

    public sealed class KomCluster
    {
        public string Id;
        public byte[] Priv, Pub;   // PKCS#8 / SubjectPublicKeyInfo
        public readonly Dictionary<string, JsonObject> Certs = new Dictionary<string, JsonObject>();   // serwer -> certyfikat
        public readonly Dictionary<string, JsonObject> Revoked = new Dictionary<string, JsonObject>();   // serwer -> podpisane odwołanie

        public static KomCluster Create()
        {
            using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return new KomCluster { Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), Priv = k.ExportPkcs8PrivateKey(), Pub = k.ExportSubjectPublicKeyInfo() };
        }
        public static (byte[] priv, byte[] pub) NewServerKey()
        {
            using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return (k.ExportPkcs8PrivateKey(), k.ExportSubjectPublicKeyInfo());
        }
        public static string ServerId(byte[] pub) => Convert.ToHexString(SHA256.HashData(pub), 0, 16);

        static byte[] Sign(byte[] priv, string msg) { using var k = ECDsa.Create(); k.ImportPkcs8PrivateKey(priv, out _); return k.SignData(Encoding.UTF8.GetBytes(msg), HashAlgorithmName.SHA256); }
        static bool Check(byte[] pub, string msg, byte[] sig) { try { using var k = ECDsa.Create(); k.ImportSubjectPublicKeyInfo(pub, out _); return k.VerifyData(Encoding.UTF8.GetBytes(msg), sig, HashAlgorithmName.SHA256); } catch (Exception) { return false; } }
        static string CertMsg(JsonObject c) => "velivo-cert|" + (string)c["c"] + "|" + (string)c["s"] + "|" + (string)c["p"] + "|" + (string)c["d"] + "|" + (long?)c["at"];
        static string RevMsg(JsonObject r) => "velivo-rev|" + (string)r["c"] + "|" + (string)r["s"] + "|" + (long?)r["at"];
        static byte[] B64(JsonNode n) { try { return Convert.FromBase64String((string)n ?? ""); } catch (Exception) { return Array.Empty<byte>(); } }

        public JsonObject Issue(byte[] serverPub, string komId)
        {
            var c = new JsonObject { ["c"] = Id, ["s"] = ServerId(serverPub), ["p"] = Convert.ToBase64String(serverPub), ["d"] = komId, ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            c["sig"] = Convert.ToBase64String(Sign(Priv, CertMsg(c)));
            Certs[(string)c["s"]] = c; return c;
        }
        // certyfikat ważny: podpis tego klastra, klucz zgodny z identyfikatorem, nieodwołany
        public bool Valid(JsonObject c) => c != null && (string)c["c"] == Id && ServerId(B64(c["p"])) == (string)c["s"] && !Revoked.ContainsKey((string)c["s"] ?? "") && Check(Pub, CertMsg(c), B64(c["sig"]));
        public JsonObject Revoke(string serverId)
        {
            var r = new JsonObject { ["c"] = Id, ["s"] = serverId, ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            r["sig"] = Convert.ToBase64String(Sign(Priv, RevMsg(r)));
            Revoked[serverId] = r; Certs.Remove(serverId); return r;
        }
        public bool AddRevoke(JsonObject r)
        {
            if (r == null || (string)r["c"] != Id || !Check(Pub, RevMsg(r), B64(r["sig"])) || Revoked.ContainsKey((string)r["s"])) return false;
            Revoked[(string)r["s"]] = (JsonObject)r.DeepClone(); Certs.Remove((string)r["s"]); return true;
        }
        public bool AddCert(JsonObject c)
        {
            if (!Valid(c)) return false;
            var s = (string)c["s"];
            if (Certs.TryGetValue(s, out var old) && ((long?)old["at"] ?? 0) >= ((long?)c["at"] ?? 0)) return false;
            Certs[s] = (JsonObject)c.DeepClone(); return true;
        }
        // członek po identyfikatorze komunikatora (połączenie uwierzytelnione kluczem pary) - ważny certyfikat z tym identyfikatorem
        public JsonObject CertOf(string komId) => Certs.Values.FirstOrDefault(c => (string)c["d"] == komId && Valid(c));

        // dowód przy połączeniu: serwer podpisuje swoim kluczem (od, do, klaster) - certyfikat + podpis
        public static string ProofMsg(string clusterId, string from, string to) => "velivo-member|" + clusterId + "|" + from + "|" + to;
        public static byte[] Proof(byte[] serverPriv, string clusterId, string from, string to) => Sign(serverPriv, ProofMsg(clusterId, from, to));
        // podpis dowolnej wiadomości kluczem serwera (polecenia administratora) i sprawdzenie certyfikatem
        public static byte[] ProofSign(byte[] serverPriv, string msg) => Sign(serverPriv, msg);
        public static bool ProofCheck(JsonObject cert, string msg, string sigB64) { try { return Check(B64(cert["p"]), msg, Convert.FromBase64String(sigB64 ?? "")); } catch (Exception) { return false; } }
        public bool CheckProof(JsonObject cert, string from, string to, byte[] sig) => Valid(cert) && (string)cert["d"] == from && Check(B64(cert["p"]), ProofMsg(Id, from, to), sig);

        public JsonObject ToJson(bool withPriv)
        {
            var o = new JsonObject { ["id"] = Id, ["pub"] = Convert.ToBase64String(Pub), ["certs"] = new JsonArray(Certs.Values.Select(c => (JsonNode)c.DeepClone()).ToArray()), ["rev"] = new JsonArray(Revoked.Values.Select(r => (JsonNode)r.DeepClone()).ToArray()) };
            if (withPriv && Priv != null) o["priv"] = Convert.ToBase64String(Priv);
            return o;
        }
        public static KomCluster FromJson(JsonObject o)
        {
            if (o == null || (string)o["id"] is not string id) return null;
            var k = new KomCluster { Id = id, Pub = B64(o["pub"]), Priv = o["priv"] != null ? B64(o["priv"]) : null };
            if (k.Pub.Length == 0) return null;
            if (o["rev"] is JsonArray ra) foreach (var r in ra.OfType<JsonObject>()) k.AddRevoke(r);
            if (o["certs"] is JsonArray ca) foreach (var c in ca.OfType<JsonObject>()) k.AddCert(c);
            return k;
        }
        // stan innego członka tego samego klastra: suma certyfikatów i odwołań (nic nie jest nadpisywane ani tracone)
        public bool Merge(KomCluster other)
        {
            if (other == null || other.Id != Id) return false;
            bool ch = false;
            foreach (var r in other.Revoked.Values) ch |= AddRevoke(r);
            foreach (var c in other.Certs.Values) ch |= AddCert(c);
            return ch;
        }
    }
}

namespace Velivo
{
    public partial class MainWindow
    {
        // 🏛 członkostwo serwerów - stan w _komOpt: "klaster" (z kluczem klastra), "srvKey" (klucz tego serwera), "clMine" (zatwierdziłem), "clYes" (tamten zatwierdził mnie)
        const string KomClTag = "\u0001velivo-klaster\n";
        KomCluster _komCl; byte[] _komSrvPriv, _komSrvPub;
        readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _komLinkOk = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();   // połączenie z dowodem członkostwa
        readonly object _komClLock = new object();
        DateTime _komClSyncAt;

        void KomClInit()
        {
            if (_komRelayListener == null) return;
            lock (_komClLock)
            {
                bool ch = false;
                if (_komSrvPriv == null)
                {
                    if (_komOpt["srvKey"] is JsonObject sk) try { _komSrvPriv = Convert.FromBase64String((string)sk["priv"]); _komSrvPub = Convert.FromBase64String((string)sk["pub"]); } catch (Exception) { _komSrvPriv = null; }
                    if (_komSrvPriv == null) { (_komSrvPriv, _komSrvPub) = KomCluster.NewServerKey(); _komOpt["srvKey"] = new JsonObject { ["priv"] = Convert.ToBase64String(_komSrvPriv), ["pub"] = Convert.ToBase64String(_komSrvPub) }; ch = true; }
                }
                if (_komCl == null) { _komCl = KomCluster.FromJson(_komOpt["klaster"] as JsonObject); if (_komCl?.Priv == null) { _komCl = KomCluster.Create(); ch = true; } }
                if (_komCl.CertOf(_komId) == null) { _komCl.Issue(_komSrvPub, _komId); ch = true; }
                if (ch) KomClSave();
            }
        }
        void KomClSave() { _komOpt["klaster"] = _komCl.ToJson(true); KomSave(); }
        bool KomIsMember(string komId) { lock (_komClLock) return komId != _komId && _komCl?.CertOf(komId) != null; }
        bool KomClMine(string id) => _komOpt["clMine"] is JsonArray a && a.Any(x => (string)x == id);
        static void KomClAdd(JsonObject opt, string key, string id) { if (opt[key] is not JsonArray a) opt[key] = a = new JsonArray(); if (!a.Any(x => (string)x == id)) a.Add(id); }
        void KomClSend(KomContact c, JsonObject o) => KomDeliver(c, new KomMsg { Mine = true, Text = KomClTag + o.ToJsonString(), Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mid = Guid.NewGuid().ToString("N") });

        // właściciel kliknął „Tak, to mój serwer” (albo migracja: serwer, z którym już dzielę wspólny sekret)
        void KomClApprove(KomContact c)
        {
            KomClInit(); if (_komCl == null) return;
            KomClAdd(_komOpt, "clMine", c.Id); KomSave();
            KomClSend(c, new JsonObject { ["k"] = "yes", ["cl"] = _komCl.Id, ["pub"] = Convert.ToBase64String(_komSrvPub) });
            if (_komOpt["clYes"] is JsonArray y && y.Any(x => (string)x == c.Id)) KomClResolve(c, (string)_komOpt["clYesCl-" + c.Id], (string)_komOpt["clYesPub-" + c.Id]);
        }
        // obie strony zatwierdziły: klaster o mniejszym identyfikatorze przyjmuje drugi serwer (bez wyścigu - obie strony liczą to samo)
        void KomClResolve(KomContact c, string theirCl, string theirPub)
        {
            if (_komCl == null || theirCl == null || theirPub == null) return;
            lock (_komClLock)
            {
                if (string.CompareOrdinal(_komCl.Id, theirCl) > 0) return;   // tamten wysyła mnie swój klaster
                if (_komCl.CertOf(c.Id) == null) try { _komCl.Issue(Convert.FromBase64String(theirPub), c.Id); } catch (Exception) { return; }   // certyfikat dla tamtego serwera
            }
            KomClSave(); App.LogInfo("Serwery: " + c.Name + " dołączony do moich serwerów");
            KomClSend(c, new JsonObject { ["k"] = "join", ["cl"] = KomClState(true), ["adm"] = KomAdmCredOut() });
        }
        JsonObject KomClState(bool priv) { lock (_komClLock) return _komCl.ToJson(priv); }

        void KomClIn(KomContact c, string json)
        {
            if (_komRelayListener == null) return;
            KomClInit();
            JsonObject o; try { o = JsonNode.Parse(json) as JsonObject; } catch (Exception) { return; }
            var k = (string)o?["k"];
            if (k == "yes")
            {   // tamten serwer zatwierdził mnie
                KomClAdd(_komOpt, "clYes", c.Id); _komOpt["clYesCl-" + c.Id] = (string)o["cl"]; _komOpt["clYesPub-" + c.Id] = (string)o["pub"]; KomSave();
                if (KomClMine(c.Id)) KomClResolve(c, (string)o["cl"], (string)o["pub"]);
                else if (_komSrvAt.ContainsKey(c.Id)) _ = Dispatcher.BeginInvoke(new Action(KomSrvAsk));   // zapytaj właściciela tutaj (raz)
                return;
            }
            var other = KomCluster.FromJson(o?["cl"] as JsonObject); if (other?.Priv == null) return;
            bool changed = false;
            lock (_komClLock)
            {
                var revD = (_komOpt["clRevD"] as JsonArray)?.Select(x => (string)x).ToList() ?? new List<string>();
                if ((k == "join" || (k == "sync" && other.Id != _komCl.Id)) && KomClMine(c.Id) && other.CertOf(c.Id) != null && !revD.Any(d => other.CertOf(d) != null)
                    && (other.Id == _komCl.Id || k == "join" || string.CompareOrdinal(other.Id, _komCl.Id) < 0))
                {   // przyjęty do klastra tamtego serwera (zatwierdziłem go): moi dotychczasowi członkowie przechodzą ze mną (nowe certyfikaty), nic nie ginie
                    if (other.Id != _komCl.Id)
                    {
                        foreach (var cert in _komCl.Certs.Values.Where(_komCl.Valid).ToList())
                            if (other.CertOf((string)cert["d"]) == null) other.Issue(Convert.FromBase64String((string)cert["p"]), (string)cert["d"]);
                        _komCl = other;
                    }
                    else _komCl.Merge(other);
                    if (_komCl.CertOf(_komId) == null) _komCl.Issue(_komSrvPub, _komId);
                    if (_komCl.CertOf(c.Id) == null) return;
                    changed = true;
                    App.LogInfo("Serwery: dołączono do infrastruktury razem z " + c.Name);
                }
                else if ((k == "sync" || k == "rot") && _komCl.CertOf(c.Id) != null)
                {
                    if (k == "sync" && other.Id == _komCl.Id) changed = _komCl.Merge(other);
                    else if (k == "rot" && other.Id != _komCl.Id && o["rev"] is JsonObject rev && _komCl.AddRevoke(rev) && other.CertOf(_komId) != null && other.CertOf(c.Id) != null)
                    {   // odwołanie serwera: nowy klucz infrastruktury (odwołany zna stary) - od członka, który odwołał
                        if ((string)o["d"] is string rd) KomClAdd(_komOpt, "clRevD", rd);
                        _komCl = other; changed = true; App.LogInfo("Serwery: odłączono serwer " + (string)rev["s"] + " – nowy klucz infrastruktury od " + c.Name);
                    }
                }
            }
            if (_komCl.CertOf(c.Id) != null && o["adm"] is JsonObject ad) KomAdmCredIn(ad);   // 🔐 dane logowania administratora od mojego serwera (nowsze wygrywa)
            if (changed) { KomClSave(); KomClSyncNow(); _komLinkOk.Clear(); KomPeerLinks(); }
        }
        // stan infrastruktury do członków co 10 min i po zmianie (suma certyfikatów i odwołań)
        void KomClSyncNow()
        {
            _komClSyncAt = DateTime.UtcNow;
            foreach (var m in _komContacts.Where(x => KomIsMember(x.Id)).ToList()) KomClSend(m, new JsonObject { ["k"] = "sync", ["cl"] = KomClState(true), ["adm"] = KomAdmCredOut() });
        }
        void KomClTick() { if (_komRelayListener == null) return; KomClInit(); KomClMigrate(); KomAdmKeysStamp(); if (DateTime.UtcNow - _komClSyncAt > TimeSpan.FromMinutes(10)) KomClSyncNow(); }
        // migracja: moje serwery sprzed infrastruktury (wspólny sekret uzgodniony) - zatwierdzone bez pytania, jak dotąd współpracowały
        void KomClMigrate()
        {
            if (_komCl == null) return;
            foreach (var c in _komContacts.Where(x => x.Dom && !x.IsGroup && !string.IsNullOrEmpty(x.Relay) && !KomClMine(x.Id) && !KomIsMember(x.Id) && _komSekPeer.TryGetValue(x.Id, out var po) && po == KomSekOrigin).ToList())
                KomClApprove(c);
        }

        // 🔐 przy każdym połączeniu serwer–serwer: certyfikat + podpis kluczem serwera (od, do, infrastruktura)
        JsonObject KomSrvIdFrame(string peerId)
        {
            KomClInit();
            lock (_komClLock) return new JsonObject { ["t"] = "p2p-srvid", ["id"] = _komId, ["cert"] = _komCl?.CertOf(_komId)?.DeepClone(), ["proof"] = _komCl == null ? "" : Convert.ToBase64String(KomCluster.Proof(_komSrvPriv, _komCl.Id, _komId, peerId)) };
        }
        bool KomSrvIdIn(string peerId, JsonNode p)
        {
            bool ok;
            try { lock (_komClLock) ok = _komCl != null && p["cert"] is JsonObject cert && _komCl.CheckProof(cert, peerId, _komId, Convert.FromBase64String((string)p["proof"] ?? "")) && _komCl.CertOf(peerId) != null; }
            catch (Exception) { ok = false; }
            if (ok) { if (_komLinkOk.TryAdd(peerId, true)) { Dispatcher.BeginInvoke(new Action(KomInfraSync)); App.LogInfo("Serwery: " + (KomContactById(peerId)?.Name ?? peerId) + " – certyfikat sprawdzony, łącze serwerów aktywne"); } }
            else { _komLinkOk.TryRemove(peerId, out _); var rk = "srvid" + peerId; if (!_komRejLog.TryGetValue(rk, out var ra) || DateTime.UtcNow - ra > TimeSpan.FromMinutes(30)) { _komRejLog[rk] = DateTime.UtcNow; App.LogInfo("Serwery: " + (KomContactById(peerId)?.Name ?? peerId) + " – odrzucono: brak ważnego certyfikatu mojej infrastruktury (nie dołączony albo odłączony)"); } }
            return ok;
        }

        // ⛔ odłączenie serwera: odwołanie + nowy klucz infrastruktury dla pozostałych (odwołany nie wystawi już certyfikatu)
        void KomClRevoke(KomContact c)
        {
            KomCluster nw; JsonObject rev;
            lock (_komClLock)
            {
                var cert = _komCl?.CertOf(c.Id); if (cert == null) return;
                rev = _komCl.Revoke((string)cert["s"]);
                nw = KomCluster.Create();
                foreach (var x in _komCl.Certs.Values.Where(_komCl.Valid).ToList()) nw.Issue(Convert.FromBase64String((string)x["p"]), (string)x["d"]);
                var members = _komContacts.Where(x => _komCl.CertOf(x.Id) != null && x.Id != _komId).ToList();
                _komCl = nw;
                foreach (var m in members) KomClSend(m, new JsonObject { ["k"] = "rot", ["cl"] = nw.ToJson(true), ["rev"] = rev, ["d"] = c.Id });
            }
            if (_komOpt["clMine"] is JsonArray a) foreach (var x in a.Where(x => (string)x == c.Id).ToList()) a.Remove(x);
            KomClAdd(_komOpt, "srvNo", c.Id); KomClAdd(_komOpt, "clRevD", c.Id);
            _komLinkOk.TryRemove(c.Id, out _);
            if (_komPeerLink.TryRemove(c.Id, out var l)) try { l.Fs.Dispose(); } catch (Exception) { }
            KomClSave(); App.LogInfo("Serwery: odłączono " + c.Name + " od moich serwerów");
        }
    }
}

namespace Velivo
{
    public partial class MainWindow
    {
        // 🛠 POLECENIA ADMINISTRATORA dla dowolnego mojego serwera (p2p-admcmd / p2p-admack): panel na jednym serwerze każe innemu (albo wszystkim) coś zrobić -
        // wykonuje backend tamtego serwera, wynik wraca do panelu. Polecenie podpisane kluczem serwera-nadawcy, sprawdzane certyfikatem infrastruktury,
        // wykonywane raz (identyfikator), każde zapisane w dzienniku operacji (adm.log) po obu stronach. Drogi: sieć domowa i łącze serwerów (KomBoxPeers).
        public static readonly (string Op, string Name)[] KomAdmOps = {
            ("restart", "Uruchom serwer od nowa"), ("on", "Włącz serwer"), ("off", "Wyłącz serwer"), ("ports", "Sprawdź porty i router"), ("firewall", "Otwórz zaporę Windows"),
            ("flush", "Wyślij ponownie kolejki (skrzynka)"), ("sync", "Synchronizuj teraz"), ("keys", "Wymień kontakty i klucze teraz"), ("syncoff", "Wstrzymaj synchronizację"), ("syncon", "Wznów synchronizację"), ("diagclr", "Wyczyść diagnostykę"), ("logout", "Wyloguj sesje administratora"), ("repair", "Wyrównaj dane z innymi serwerami") };
        readonly HashSet<string> _komAdmSeen = new HashSet<string>();
        public event Action KomAdmChanged;
        static string KomAdmMsg(JsonObject c) => "velivo-adm|" + (string)c["cid"] + "|" + (string)c["id"] + "|" + (string)c["to"] + "|" + (string)c["op"] + "|" + ((string)c["arg"] ?? "") + "|" + (long?)c["ts"];
        void KomAdmLog(string dir, string cid, string op, string from, string to, string result)
        {
            try
            {
                var p = System.IO.Path.Combine(DataDir, "adm.log");
                if (System.IO.File.Exists(p) && new System.IO.FileInfo(p).Length > 2_000_000) System.IO.File.Delete(p);
                System.IO.File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | " + Environment.MachineName + " | " + dir + " | " + cid.Substring(0, 8) + " | " + op + " | " + from + " → " + to + " | " + result + Environment.NewLine);
            }
            catch (Exception) { }
            _ = Dispatcher.BeginInvoke(new Action(() => KomAdmChanged?.Invoke()));
        }
        bool KomSyncOff => (bool?)_komOpt["syncOff"] == true;
        // wysłanie polecenia: to = identyfikator serwera albo "*" (wszystkie); ten serwer wykonuje u siebie od razu
        void KomAdmSend(string to, string op, string arg = null)
        {
            KomClInit();
            var c = new JsonObject { ["t"] = "p2p-admcmd", ["id"] = _komId, ["cid"] = Guid.NewGuid().ToString("N"), ["to"] = to, ["op"] = op, ["arg"] = arg, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            lock (_komClLock) c["sig"] = Convert.ToBase64String(KomCluster.ProofSign(_komSrvPriv, KomAdmMsg(c)));
            KomAdmLog("WYSŁANE", (string)c["cid"], op, KomSrvTag(_komId), to == "*" ? "wszystkie serwery" : KomSrvTag(to), "polecenie wysłane");
            if (to == _komId || to == "*") KomAdmIn(c);
            if (to != _komId) KomBoxPeers(c);
        }
        void KomAdmIn(JsonNode p)
        {
            if (p is not JsonObject c) return;
            var from = (string)c["id"]; var cid = (string)c["cid"] ?? ""; if (cid.Length != 32) return;
            if ((string)c["t"] == "p2p-admack")
            {
                if ((string)c["to"] == _komId) KomAdmLog((bool?)c["ok"] == true ? "WYKONANE" : "BŁĄD", cid, (string)c["op"] ?? "?", KomSrvTag(from), KomSrvTag(_komId), (string)c["msg"] ?? "");
                return;
            }
            var to = (string)c["to"]; if (to != _komId && to != "*") return;
            lock (_komAdmSeen) { if (!_komAdmSeen.Add(cid)) return; if (_komAdmSeen.Count > 5000) _komAdmSeen.Clear(); }
            if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ((long?)c["ts"] ?? 0)) > 86_400_000) { KomAdmLog("ODRZUCONE", cid, (string)c["op"] ?? "?", KomSrvTag(from), KomSrvTag(_komId), "polecenie starsze niż doba"); return; }
            bool okSig = from == _komId;
            if (!okSig) { KomClInit(); lock (_komClLock) { var cert = _komCl?.CertOf(from); okSig = cert != null && KomCluster.ProofCheck(cert, KomAdmMsg(c), (string)c["sig"]); } }
            var op = (string)c["op"] ?? "";
            if (!okSig) { KomAdmLog("ODRZUCONE", cid, op, KomSrvTag(from), KomSrvTag(_komId), "nadawca nie jest moim serwerem albo zły podpis"); return; }
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                string msg; bool ok = true;
                try { msg = KomAdmRun(op, (string)c["arg"]); }
                catch (Exception ex) { ok = false; msg = ex.Message; }
                if (msg == null) { ok = false; msg = "nieznane polecenie"; }
                KomAdmLog(ok ? "WYKONANE" : "BŁĄD", cid, op, KomSrvTag(from), KomSrvTag(_komId), msg);
                if (from != _komId) KomBoxPeers(new JsonObject { ["t"] = "p2p-admack", ["id"] = _komId, ["to"] = from, ["cid"] = cid, ["op"] = op, ["ok"] = ok, ["msg"] = msg });
            }));
        }
        // wykonanie u siebie (wątek okna) - zwraca opis wyniku słowami
        string KomAdmRun(string op, string arg)
        {
            switch (op)
            {
                case "restart": KomRelayStart(); return _komRelayListener != null ? "serwer uruchomiony od nowa – nasłuch na porcie " + _komRelayPort : "serwer wyłączony (włącz go poleceniem „Włącz serwer”)";
                case "on": _komRelayOn = true; KomSave(); KomRelayStart(); KomAnnounce(); return _komRelayListener != null ? "serwer włączony – port " + _komRelayPort : "nie udało się otworzyć portu " + _komRelayPort;
                case "off": _komRelayOn = false; _komRelayAsked = true; KomSave(); KomRelayStart(); return "serwer wyłączony – osoby przejdą na inne moje serwery";
                case "ports": _komRelayRouter = L.T("Sprawdzam porty…"); _ = KomRelayAutoAsync(); return "sprawdzanie portów i routera rozpoczęte (wynik w panelu tego serwera po ~1 min)";
                case "firewall": _komFwAsked = DateTime.MinValue; KomFirewallCheck(); return "zapora: sprawdzenie i otwarcie (na tamtym komputerze Windows może zapytać „Tak”)";
                case "flush":
                    {
                        int n = 0; var root = System.IO.Path.Combine(DataDir, "skrzynka");
                        if (System.IO.Directory.Exists(root)) foreach (var d in System.IO.Directory.GetDirectories(root)) { _ = KomBoxFlush(System.IO.Path.GetFileName(d)); n++; }
                        foreach (var id in _komStore.Keys.ToList()) if (_komRemote.TryGetValue(id, out var rc)) KomStoreFlush(id, rc, null);
                        return "kolejki ponowione dla " + n + " odbiorców plików; wiadomości do połączonych wysłane";
                    }
                case "sync": KomSrvInfoSend(); KomEvHave(); KomClSyncNow(); KomPeerLinks(); return "synchronizacja wywołana: stan, dziennik zdarzeń, infrastruktura, łącza";
                case "keys": return KomInfraSyncForce();
                case "syncoff": _komOpt["syncOff"] = true; KomSave(); return "synchronizacja WSTRZYMANA – ten serwer nie wysyła kopii ani zdarzeń do innych (stan i polecenia działają)";
                case "syncon": _komOpt["syncOff"] = false; KomSave(); KomEvHave(); KomPeerLinks(); return "synchronizacja wznowiona – zaległe zdarzenia zostaną dosłane";
                case "logout": _komOpt["admLogout"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); KomSave(); KomAdmEnd("unieważnione poleceniem administratora"); return "sesje administratora na tym serwerze zamknięte – następne otwarcie panelu wymaga logowania";
                case "repair":
                    {   // 🔧 naprawa synchronizacji: każda czekająca wiadomość i każdy plik (oryginał) jeszcze raz jako kopia do moich serwerów - odbiorca kopii odrzuca to, co już ma (mid / hid)
                        int nm = 0, nf = 0;
                        foreach (var kv in _komStore.ToArray()) { List<JsonNode> ms; lock (kv.Value) ms = kv.Value.ToList(); foreach (var m in ms) { var c = m.DeepClone(); c["fwd"] = true; KomBoxPeers(c); nm++; } }
                        var root = System.IO.Path.Combine(DataDir, "skrzynka");
                        if (System.IO.Directory.Exists(root))
                            foreach (var hdr in System.IO.Directory.GetFiles(root, "*.hdr", System.IO.SearchOption.AllDirectories).Where(h => !System.IO.Path.GetFileName(h).StartsWith("k")))
                            { var f = hdr.Substring(0, hdr.Length - 4); if (System.IO.File.Exists(f + ".bin")) { _ = KomBoxCopy(f); nf++; } }
                        KomEvHave(); KomSrvInfoSend();
                        return "wyrównywanie: " + nm + " wiadomości i " + nf + " plików wysłane jeszcze raz do moich serwerów (duplikaty odrzucane), historia zdarzeń porównana od nowa";
                    }
                case "diagclr": foreach (var f in new[] { "serwery-diag.log", "serwery-diag.log.1" }) try { System.IO.File.Delete(System.IO.Path.Combine(DataDir, f)); } catch (Exception) { } return "diagnostyka wyczyszczona";
            }
            return null;
        }
    }
}

namespace Velivo
{
    using System.Windows;
    using System.Windows.Controls;
    public partial class MainWindow
    {
        // 🔐 LOGOWANIE DO PANELU ADMINISTRATORA: klucz sprzętowy (WebAuthn, te same klucze co komunikator) albo hasło administratora
        // (PBKDF2-SHA256, 300 000 powtórzeń, sól; w ustawieniach tylko skrót). Pierwsze otwarcie bez hasła i kluczy - ustawienie hasła (wymagane).
        // 5 błędnych prób = blokada 5 min; każde logowanie (udane i nie) w dzienniku operacji (adm.log). Sesja ważna 10 min od ostatniego logowania.
        DateTime _komAdmUntil, _komAdmLockUntil; int _komAdmFails;
        static byte[] KomAdmHash(string pass, byte[] salt) => System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(System.Text.Encoding.UTF8.GetBytes(pass), salt, 300000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        void KomAdmSetPass(string pass)
        {
            var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
            _komOpt["admPass"] = new JsonObject { ["s"] = Convert.ToBase64String(salt), ["h"] = Convert.ToBase64String(KomAdmHash(pass, salt)), ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }; KomSave();
            if (_komRelayListener != null) KomClSyncNow();   // to samo hasło na wszystkich moich serwerach
        }
        // 🔐 jeden administrator dla wszystkich serwerów: skrót hasła (z solą) i lista kluczy sprzętowych idą do członków infrastruktury
        // (szyfrowanym kanałem pary, razem ze stanem infrastruktury); wygrywa nowsza zmiana - także po powrocie serwera po braku prądu
        JsonObject KomAdmCredOut() => new JsonObject { ["pass"] = _komOpt["admPass"]?.DeepClone(), ["keys"] = _komOpt["klucze"]?.DeepClone(), ["keysDel"] = _komOpt["kluczeDel"]?.DeepClone(), ["keysAt"] = (long?)_komOpt["kluczeAt"] ?? 0, ["logout"] = (long?)_komOpt["admLogout"] ?? 0 };
        void KomAdmCredIn(JsonObject a)
        {
            bool ch = false;
            if (a["pass"] is JsonObject pp && ((long?)pp["at"] ?? 0) > ((long?)(_komOpt["admPass"] as JsonObject)?["at"] ?? 0)) { _komOpt["admPass"] = pp.DeepClone(); ch = true; }
            // 🔑 klucze sprzętowe administratora: SUMA kluczy wszystkich moich serwerów (klucz dodany na Ryzenie i klucz dodany na NUCBOX - oba ważne wszędzie);
            // usunięty klucz (lista kluczeDel) znika na wszystkich i nie wraca przy wymianie. Wcześniej „nowsza lista wygrywa” - klucz jednego serwera kasował klucz drugiego.
            if (_komOpt["kluczeDel"] is not JsonArray del) _komOpt["kluczeDel"] = del = new JsonArray();
            var dset = del.Select(x => (string)x).Where(x => x != null).ToHashSet();
            if (a["keysDel"] is JsonArray kd) foreach (var x in kd) if ((string)x is string di && dset.Add(di)) { del.Add(di); ch = true; }
            if (_komOpt["klucze"] is not JsonArray mine) _komOpt["klucze"] = mine = new JsonArray();
            foreach (var x in mine.Where(k => dset.Contains((string)k?["id"] ?? "")).ToList()) { mine.Remove(x); ch = true; }
            var have = mine.Select(k => (string)k?["id"]).ToHashSet();
            if (a["keys"] is JsonArray ka) foreach (var k in ka) if ((string)k?["id"] is string kid && !have.Contains(kid) && !dset.Contains(kid)) { mine.Add(k.DeepClone()); have.Add(kid); ch = true; }
            if (ch) { _komOpt["kluczeH"] = KomKeysHash(); _komOpt["kluczeAt"] = Math.Max((long?)_komOpt["kluczeAt"] ?? 0, (long?)a["keysAt"] ?? 0); }
            if (((long?)a["logout"] ?? 0) > ((long?)_komOpt["admLogout"] ?? 0)) { _komOpt["admLogout"] = (long?)a["logout"]; KomAdmEnd("unieważnienie sesji z innego serwera"); ch = true; }
            if (ch) KomSave();
        }
        string KomKeysHash() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_komOpt["klucze"]?.ToJsonString() ?? "")), 0, 8);
        // zmiana listy kluczy (dodanie / usunięcie w dowolnym miejscu) - znacznik czasu, żeby rozeszła się na moje serwery
        void KomAdmKeysStamp() { var h = KomKeysHash(); if ((string)_komOpt["kluczeH"] == h) return; bool first = _komOpt["kluczeH"] == null; _komOpt["kluczeH"] = h; if (!first) { _komOpt["kluczeAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); KomClSyncNow(); } KomSave(); }
        // ⏱ sesja: wygasa tylko po 15 min bez Twojego ruchu w panelu (mysz, klawiatura) - nigdy z powodu stanu serwerów (brak prądu, wyłączenie)
        static readonly TimeSpan KomAdmIdle = TimeSpan.FromMinutes(15);
        public event Action<string> KomAdmEnded;
        void KomAdmEnd(string why)
        {
            if (_komAdmUntil == DateTime.MinValue) return;
            _komAdmUntil = DateTime.MinValue;
            KomAdmLog("LOGOWANIE", Guid.NewGuid().ToString("N"), "logout", Environment.MachineName, Environment.MachineName, "sesja zakończona – " + why);
            _ = Dispatcher.BeginInvoke(new Action(() => KomAdmEnded?.Invoke(why)));
        }
        bool KomAdmCheckPass(string pass)
        {
            try { var o = _komOpt["admPass"] as JsonObject; if (o == null) return false; var salt = Convert.FromBase64String((string)o["s"]); return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(KomAdmHash(pass, salt), Convert.FromBase64String((string)o["h"])); }
            catch (Exception) { return false; }
        }
        void KomAdmChangePass(Window owner) { var p = KomPassword(owner, true); if (string.IsNullOrEmpty(p)) return; KomAdmSetPass(p); KomAdmLog("LOGOWANIE", Guid.NewGuid().ToString("N"), "haslo", Environment.MachineName, Environment.MachineName, "hasło administratora zmienione"); MessageBox.Show(owner, L.T("Hasło administratora zmienione."), "🔐"); }
        async System.Threading.Tasks.Task<bool> KomAdmLogin(Window owner)
        {
            if (DateTime.UtcNow < _komAdmUntil) return true;
            if ((long?)_komOpt["admLock"] is long lk && lk > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) _komAdmLockUntil = DateTimeOffset.FromUnixTimeMilliseconds(lk).UtcDateTime;   // blokada po błędnych próbach trwa też po ponownym uruchomieniu
            var keys = KomHwKeys(); bool hasPass = _komOpt["admPass"] is JsonObject;
            if (!hasPass && keys.Count == 0)
            {
                MessageBox.Show(owner, L.T("Panel administratora jest chroniony. Ustaw teraz hasło administratora (później możesz też logować się kluczem sprzętowym)."), "🔐");
                var np = KomPassword(owner, true); if (string.IsNullOrEmpty(np)) return false;
                KomAdmSetPass(np); _komAdmUntil = DateTime.UtcNow.AddYears(1);
                KomAdmLog("LOGOWANIE", Guid.NewGuid().ToString("N"), "login", Environment.MachineName, Environment.MachineName, "ustawiono hasło administratora i zalogowano");
                return true;
            }
            var w = new Window { Title = "🔐 " + L.T("Logowanie administratora") + " – " + Environment.MachineName, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var sp = new StackPanel { Margin = new Thickness(18) };
            sp.Children.Add(new TextBlock { Text = L.T("Panel administratora daje pełną kontrolę nad Twoimi serwerami. Zaloguj się kluczem sprzętowym albo hasłem administratora."), TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Margin = new Thickness(0, 0, 0, 12) });
            var info = new TextBlock { Foreground = System.Windows.Media.Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Margin = new Thickness(0, 0, 0, 8) };
            bool ok = false;
            void Done(string how) { ok = true; _komAdmFails = 0; _komAdmUntil = DateTime.UtcNow.AddYears(1); KomAdmLog("LOGOWANIE", Guid.NewGuid().ToString("N"), "login", Environment.MachineName, Environment.MachineName, "zalogowano – " + how); w.Close(); }
            void Fail(string why) { _komAdmFails++; if (_komAdmFails >= 5) { _komAdmLockUntil = DateTime.UtcNow.AddMinutes(5); _komAdmFails = 0; _komOpt["admLock"] = new DateTimeOffset(_komAdmLockUntil).ToUnixTimeMilliseconds(); KomSave(); } KomAdmLog("LOGOWANIE", Guid.NewGuid().ToString("N"), "login", Environment.MachineName, Environment.MachineName, "NIEUDANE – " + why); info.Text = why; }
            if (keys.Count > 0)
            {
                var kb = new Button { Content = "🔑 " + L.T("Zaloguj kluczem sprzętowym"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 0, 12) };
                kb.Click += async (s, e) =>
                {
                    if (DateTime.UtcNow < _komAdmLockUntil) { info.Text = L.T("Za dużo błędnych prób – spróbuj za kilka minut."); return; }
                    kb.IsEnabled = false; info.Text = L.T("Dotknij klucz…");
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle; bool good;
                    try { good = (await System.Threading.Tasks.Task.Run(() => WebAuthn.Verify(hwnd, BankRpId, keys, null)))?.Key != null; } catch (Exception) { good = false; }
                    kb.IsEnabled = true; if (good) Done(L.T("klucz sprzętowy")); else Fail(L.T("Klucz nie został potwierdzony."));
                };
                sp.Children.Add(kb);
            }
            if (hasPass)
            {
                sp.Children.Add(new TextBlock { Text = L.T("Hasło administratora:") });
                var pb = new PasswordBox { MinWidth = 300, Margin = new Thickness(0, 4, 0, 8) }; sp.Children.Add(pb);
                var lb = new Button { Content = L.T("Zaloguj"), IsDefault = true, Padding = new Thickness(18, 5, 18, 5), HorizontalAlignment = HorizontalAlignment.Right };
                lb.Click += (s, e) =>
                {
                    if (DateTime.UtcNow < _komAdmLockUntil) { info.Text = L.T("Za dużo błędnych prób – spróbuj za kilka minut."); return; }
                    if (KomAdmCheckPass(pb.Password)) Done(L.T("hasło")); else { pb.Clear(); Fail(L.T("Złe hasło.")); }
                };
                sp.Children.Add(lb); w.Loaded += (s, e) => pb.Focus();
            }
            else sp.Children.Add(new TextBlock { Text = L.T("Hasło administratora nie jest ustawione – po zalogowaniu kluczem ustaw je w zakładce Ustawienia (zapas, gdy klucza nie ma przy sobie)."), TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Opacity = 0.75 });
            sp.Children.Add(info);
            w.Content = sp; KomStyle(w); w.ShowDialog();
            return ok;
        }
    }
}
