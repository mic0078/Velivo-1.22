using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Velivo
{
    // 🔌 API serwera (HTTP, tylko ten komputer: 127.0.0.1:41930) - inne programy pytają serwer o stan i wydają polecenia administratora.
    // Klucz API w pliku api-klucz.txt (folder danych); każde zapytanie: nagłówek „Authorization: Bearer <klucz>” albo ?klucz=<klucz>.
    // Polecenia idą tą samą drogą co z panelu (KomAdmSend: podpisane, w dzienniku operacji adm.log). Opis: dokumentacja/api-serwerow.md
    public partial class MainWindow
    {
        public const int KomApiPort = 41930;
        TcpListener _komApi;
        string KomApiKey()
        {
            if ((string)_komOpt["apiKey"] is not { Length: 43 } k) { _komOpt["apiKey"] = k = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'); KomSave(); }
            try { var f = Path.Combine(DataDir, "api-klucz.txt"); if (!File.Exists(f) || File.ReadAllText(f).Trim() != k) File.WriteAllText(f, k); } catch (Exception) { }
            return k;
        }
        void KomApiStart()
        {
            if (_komApi != null || _komRelayListener == null) return;
            try { var l = new TcpListener(IPAddress.Loopback, KomApiPort); l.Start(); _komApi = l; } catch (Exception ex) { App.LogInfo("API serwera: port " + KomApiPort + " zajęty – " + ex.Message); return; }
            var key = KomApiKey();
            App.LogInfo("API serwera: http://127.0.0.1:" + KomApiPort + "/api (klucz w api-klucz.txt)");
            _ = Task.Run(async () =>
            {
                while (_komApi != null)
                {
                    TcpClient cl; try { cl = await _komApi.AcceptTcpClientAsync(); } catch (Exception) { break; }
                    _ = Task.Run(() => KomApiServe(cl, key));
                }
            });
        }
        async Task KomApiServe(TcpClient cl, string key)
        {
            using (cl)
            try
            {
                cl.ReceiveTimeout = 5000; var s = cl.GetStream(); var r = new StreamReader(s, Encoding.UTF8);
                var first = (await r.ReadLineAsync()) ?? ""; var parts = first.Split(' '); if (parts.Length < 2) return;
                string auth = null; string line;
                while (!string.IsNullOrEmpty(line = await r.ReadLineAsync())) if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) auth = line.Substring(14).Trim();
                var url = parts[1]; var path = url.Split('?')[0].TrimEnd('/');
                var q = (url.Contains('?') ? url.Substring(url.IndexOf('?') + 1) : "").Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => x.Length > 1 ? Uri.UnescapeDataString(x[1]) : "");
                var given = auth != null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth.Substring(7).Trim() : q.GetValueOrDefault("klucz");
                int code = 200; JsonNode body;
                if (path == "/api/wyzwanie" || path == "/api/zaloguj") body = KomApiHw(path, q, ref code);   // 🔑 logowanie kluczem sprzętowym - bez klucza API
                else if (given == null || !(CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(key)) || KomApiTokenOk(given))) { code = 401; body = new JsonObject { ["blad"] = "zły albo brak klucza API (api-klucz.txt w folderze danych Velivo)" }; }
                else body = await Dispatcher.InvokeAsync(() => KomApiRun(path, q, ref code)).Task;
                var d = Encoding.UTF8.GetBytes(body.ToJsonString());
                var h = Encoding.ASCII.GetBytes("HTTP/1.1 " + code + (code == 200 ? " OK" : " Error") + "\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " + d.Length + "\r\nConnection: close\r\n\r\n");
                await s.WriteAsync(h); await s.WriteAsync(d);
            }
            catch (Exception) { }
        }
        // 🔑 klucz sprzętowy administratora (te same klucze co w panelu, WebAuthn/FIDO2, rpId velivo.local):
        // 1) GET /api/wyzwanie → challenge; 2) klient FIDO2 podpisuje kluczem (dotknięcie); 3) GET /api/zaloguj?id=&authData=&clientData=&sig= (base64url)
        // → token; token działa jak klucz API i wygasa po 15 min bez zapytań (jak sesja panelu)
        readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _komApiCh = new(), _komApiTok = new();
        bool KomApiTokenOk(string t)
        {
            if (!_komApiTok.TryGetValue(t, out var at)) return false;
            if (DateTime.UtcNow - at > TimeSpan.FromMinutes(15)) { _komApiTok.TryRemove(t, out _); return false; }
            _komApiTok[t] = DateTime.UtcNow; return true;
        }
        static byte[] KomB64u(string s) { if (s == null) return null; s = s.Replace('-', '+').Replace('_', '/').Replace(' ', '+'); s += new string('=', (4 - s.Length % 4) % 4); try { return Convert.FromBase64String(s); } catch (Exception) { return null; } }
        static string KomB64uOut(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        JsonNode KomApiHw(string path, Dictionary<string, string> q, ref int code)
        {
            var keys = KomHwKeys();
            if (keys.Count == 0) { code = 404; return new JsonObject { ["blad"] = "brak kluczy sprzętowych administratora – dodaj w panelu (zakładka Klucze)" }; }
            foreach (var old in _komApiCh.Where(x => DateTime.UtcNow - x.Value > TimeSpan.FromMinutes(2)).ToList()) _komApiCh.TryRemove(old.Key, out _);
            if (path == "/api/wyzwanie")
            {
                var ch = KomB64uOut(RandomNumberGenerator.GetBytes(32)); _komApiCh[ch] = DateTime.UtcNow;
                return new JsonObject { ["challenge"] = ch, ["rpId"] = BankRpId, ["allowCredentials"] = new JsonArray(keys.Select(k => (JsonNode)KomB64uOut(Convert.FromBase64String(k.Id))).ToArray()), ["waznosc"] = "2 min" };
            }
            var id = KomB64u(q.GetValueOrDefault("id")); var ad = KomB64u(q.GetValueOrDefault("authData")); var cd = KomB64u(q.GetValueOrDefault("clientData")); var sig = KomB64u(q.GetValueOrDefault("sig"));
            JsonNode Fail(string why) { KomAdmLog("API", Guid.NewGuid().ToString("N"), "logowanie-klucz", "API", KomSrvTag(_komId), "odrzucono: " + why); return new JsonObject { ["blad"] = why }; }
            code = 401;
            if (id == null || ad == null || cd == null || sig == null || ad.Length < 37) return Fail("brak pól id, authData, clientData, sig");
            var key = keys.FirstOrDefault(k => Convert.FromBase64String(k.Id).AsSpan().SequenceEqual(id)); if (key == null) return Fail("nieznany klucz sprzętowy");
            JsonNode c; try { c = JsonNode.Parse(cd); } catch (Exception) { return Fail("zły clientData"); }
            var ch2 = (string)c?["challenge"];
            if ((string)c?["type"] != "webauthn.get" || ch2 == null || !_komApiCh.TryRemove(ch2, out _)) return Fail("nieznane albo przeterminowane wyzwanie");
            if (!ad.AsSpan(0, 32).SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(BankRpId))) || (ad[32] & 1) == 0) return Fail("klucz nie potwierdził dotknięcia albo inny rpId");
            bool ok;
            using (var ec = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = Convert.FromBase64String(key.X), Y = Convert.FromBase64String(key.Y) } }))
                ok = ec.VerifyData(ad.Concat(SHA256.HashData(cd)).ToArray(), sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            if (!ok) return Fail("zły podpis klucza");
            code = 200; var tok = KomB64uOut(RandomNumberGenerator.GetBytes(32)); _komApiTok[tok] = DateTime.UtcNow;
            KomAdmLog("API", Guid.NewGuid().ToString("N"), "logowanie-klucz", "API", KomSrvTag(_komId), "zalogowano kluczem: " + (key.Name ?? "?"));
            return new JsonObject { ["token"] = tok, ["klucz"] = key.Name, ["wygasa"] = "po 15 min bez zapytań" };
        }
        JsonNode KomApiRun(string path, System.Collections.Generic.Dictionary<string, string> q, ref int code)
        {
            switch (path)
            {
                case "/api": case "/api/pomoc":
                    return new JsonObject { ["api"] = "Velivo – serwer " + Environment.MachineName, ["zapytania"] = new JsonArray("GET /api/stan", "GET /api/serwery", "GET /api/polecenia", "GET /api/polecenie?serwer=<id|*>&op=<czynność>[&arg=…]", "GET /api/dziennik", "GET /api/konta", "GET /api/konto/usun?id=<id konta>", "GET /api/klucze", "GET /api/wyzwanie (bez klucza API)", "GET /api/zaloguj?id=&authData=&clientData=&sig= (bez klucza API)") };
                case "/api/stan": return KomSrvInfoNow();
                case "/api/serwery":
                    var a = new JsonArray();
                    a.Add(new JsonObject { ["id"] = _komId, ["ten"] = true, ["stan"] = KomSrvInfoNow() });
                    foreach (var kv in _komSrvInfo) a.Add(new JsonObject { ["id"] = kv.Key, ["ostatnio"] = kv.Value.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), ["dziala"] = DateTime.UtcNow - kv.Value.At < TimeSpan.FromMinutes(3), ["stan"] = kv.Value.Info.DeepClone() });
                    return a;
                case "/api/polecenia": return new JsonArray(KomAdmOps.Select(o => (JsonNode)new JsonObject { ["op"] = o.Op, ["opis"] = o.Name }).ToArray());
                case "/api/polecenie":
                    var op = q.GetValueOrDefault("op"); var to = q.GetValueOrDefault("serwer") ?? _komId;
                    if (op == null || !KomAdmOps.Any(o => o.Op == op)) { code = 400; return new JsonObject { ["blad"] = "nieznana czynność – lista: /api/polecenia" }; }
                    if (to != "*" && to != _komId && !_komSrvInfo.ContainsKey(to)) { code = 404; return new JsonObject { ["blad"] = "nie znam takiego serwera – lista: /api/serwery" }; }
                    KomAdmSend(to, op, q.GetValueOrDefault("arg"));
                    return new JsonObject { ["wyslane"] = true, ["op"] = op, ["serwer"] = to, ["wynik"] = "w dzienniku operacji: /api/dziennik" };
                case "/api/konta":   // 📝 konta (numer + PIN) - wspólne dla moich serwerów; bez PIN-u
                    return new JsonArray(KomAccounts().Select(kv => (JsonNode)new JsonObject { ["id"] = kv.Key, ["imie"] = (string)kv.Value?["name"], ["numer"] = (string)kv.Value?["nr"], ["zalozone"] = DateTimeOffset.FromUnixTimeMilliseconds((long?)kv.Value?["at"] ?? 0).LocalDateTime.ToString("yyyy-MM-dd HH:mm"), ["wSieci"] = KomOnlineAny(kv.Key) }).ToArray());
                case "/api/konto/usun":   // jak w panelu: konto, skrzynka i osoba na wszystkich moich serwerach
                    var id = q.GetValueOrDefault("id");
                    if (id == null || !KomAccounts().ContainsKey(id)) { code = 404; return new JsonObject { ["blad"] = "nie ma takiego konta – lista: /api/konta" }; }
                    KomAccounts().Remove(id); _komStore.TryRemove(id, out _); try { Directory.Delete(KomBoxDir(id), true); } catch (Exception) { } KomForget(id, true); KomSave();
                    KomAdmLog("API", Guid.NewGuid().ToString("N"), "konto-usun", "API", KomSrvTag(_komId), "usunięto konto " + id.Substring(0, Math.Min(6, id.Length)) + " na wszystkich moich serwerach");
                    return new JsonObject { ["usuniete"] = true, ["id"] = id };
                case "/api/klucze": return new JsonArray(KomHwKeys().Select(k => (JsonNode)new JsonObject { ["nazwa"] = k.Name, ["id"] = KomB64uOut(Convert.FromBase64String(k.Id)) }).ToArray());
                case "/api/dziennik":
                    var f = Path.Combine(DataDir, "adm.log");
                    return new JsonArray((File.Exists(f) ? File.ReadAllLines(f).Where(x => x.Length > 0).TakeLast(200) : Enumerable.Empty<string>()).Select(x => (JsonNode)x).ToArray());
                default: code = 404; return new JsonObject { ["blad"] = "nie ma takiego zapytania – lista: /api" };
            }
        }
    }
}
