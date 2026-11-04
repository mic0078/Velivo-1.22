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
            KomClSend(c, new JsonObject { ["k"] = "join", ["cl"] = KomClState(true) });
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
            if (changed) { KomClSave(); KomClSyncNow(); _komLinkOk.Clear(); KomPeerLinks(); }
        }
        // stan infrastruktury do członków co 10 min i po zmianie (suma certyfikatów i odwołań)
        void KomClSyncNow()
        {
            _komClSyncAt = DateTime.UtcNow;
            foreach (var m in _komContacts.Where(x => KomIsMember(x.Id)).ToList()) KomClSend(m, new JsonObject { ["k"] = "sync", ["cl"] = KomClState(true) });
        }
        void KomClTick() { if (_komRelayListener == null) return; KomClInit(); KomClMigrate(); if (DateTime.UtcNow - _komClSyncAt > TimeSpan.FromMinutes(10)) KomClSyncNow(); }
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
            if (ok) { if (_komLinkOk.TryAdd(peerId, true)) App.LogInfo("Serwery: " + (KomContactById(peerId)?.Name ?? peerId) + " – certyfikat sprawdzony, łącze serwerów aktywne"); }
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
