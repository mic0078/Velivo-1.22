using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Velivo
{
    // 🌍 Mój serwer filmów przez internet (makieta dokumentacja/makieta-serwer-wszedzie.html), krok 1: serwer + komputery.
    // Serwer (Velivo Serwer filmów): port 41940, TLS z własnym certyfikatem (bez chmury), jak Jellyfin: adres + hasło serwera,
    // przy logowaniu klient dowodzi, że widzi TEN certyfikat (HMAC hasła i odcisku - podstawiony serwer nie przejdzie), dostaje własny klucz.
    // Każde zapytanie podpisane kluczem urządzenia (czas ±10 min); „Odłącz” unieważnia klucz od razu; 10 złych prób z adresu = blokada na godzinę.
    // Trasy: /ping, /pair, /lib (filmy serwera z informacjami), /p/<id> (plakat), /f/<id> (film, zakresy - przewijanie), /prog (postęp, obejrzany).
    // Komputer: odtwarzacz (VLC) dostaje adres lokalnego pośrednika 127.0.0.1 - pośrednik łączy się szyfrowanie z serwerem i podpisuje zapytania.
    public partial class MainWindow
    {
        internal const int NetFilmPort = 41940;

        // ======================= SERWER =======================
        sealed class NetDevice { public string Id { get; set; } public string Name { get; set; } public string Key { get; set; } public long Seen { get; set; } public bool Lan { get; set; } public string UserId { get; set; } }
        // 👥 użytkownicy jak w Jellyfin (makieta-serwer-uzytkownicy.html): imię + hasło, dostęp do folderów (puste = wszystkie), administrator.
        // Administrator ogląda na bibliotece serwera (jak dotąd); pozostali mają własny postęp, obejrzane i ulubione (serwer-uzytkownicy-stan.json).
        internal sealed class NetUser { public string Id { get; set; } public string Name { get; set; } public string Pass { get; set; } public List<string> Folders { get; set; } public bool Admin { get; set; } public long Seen { get; set; } }
        sealed class UserFilm { public long P { get; set; } public long L { get; set; } public bool D { get; set; } public long W { get; set; } public bool F { get; set; } }
        static string NetUsersFile => Path.Combine(DataDir, "serwer-uzytkownicy.bin");
        static string NetStanFile => Path.Combine(DataDir, "serwer-uzytkownicy-stan.json");
        List<NetUser> _netUsers; Dictionary<string, Dictionary<string, UserFilm>> _netStan;
        internal List<NetUser> NetUsers()
        {
            if (_netUsers != null) return _netUsers;
            try { _netUsers = File.Exists(NetUsersFile) ? JsonSerializer.Deserialize<List<NetUser>>(Dp(File.ReadAllBytes(NetUsersFile), false)) : null; } catch (Exception ex) { App.LogError(ex); }
            _netUsers ??= new List<NetUser>();
            // przejście z jednego hasła serwera: hasło = konto właściciela (administrator), zalogowane urządzenia - jego
            if (_netUsers.Count == 0 && !string.IsNullOrEmpty(NetHaslo))
            {
                var u = new NetUser { Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)), Name = Environment.UserName, Pass = NetHaslo, Admin = true };
                _netUsers.Add(u); lock (NetDevs()) foreach (var d in _netDevs) d.UserId ??= u.Id; NetDevsSave(); NetUsersSave();
            }
            return _netUsers;
        }
        internal void NetUsersSave() { try { lock (NetUsers()) VelivoData.SafeWrite(NetUsersFile, Dp(JsonSerializer.SerializeToUtf8Bytes(_netUsers), true)); } catch (Exception ex) { App.LogError(ex); } }
        Dictionary<string, Dictionary<string, UserFilm>> OsobyStan()
        {
            if (_netStan != null) return _netStan;
            try { _netStan = File.Exists(NetStanFile) ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, UserFilm>>>(File.ReadAllBytes(NetStanFile)) : null; } catch (Exception ex) { App.LogError(ex); }
            return _netStan ??= new Dictionary<string, Dictionary<string, UserFilm>>();
        }
        void NetStanSave() { try { lock (OsobyStan()) VelivoData.SafeWrite(NetStanFile, JsonSerializer.SerializeToUtf8Bytes(_netStan)); } catch (Exception ex) { App.LogError(ex); } }
        internal NetUser NetUserByName(string n) { if (string.IsNullOrWhiteSpace(n)) return null; lock (NetUsers()) return _netUsers.FirstOrDefault(u => string.Equals(u.Name, n.Trim(), StringComparison.CurrentCultureIgnoreCase)); }
        internal List<string> NetUserNames() { lock (NetUsers()) return _netUsers.Select(u => u.Name).ToList(); }
        static bool UserWidzi(NetUser u, string path) => u == null || u.Folders == null || u.Folders.Count == 0 || u.Folders.Any(f => path.StartsWith(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        // stan filmu dla osoby (nie-administrator); administrator - biblioteka serwera
        UserFilm UserStan(NetUser u, string id) { lock (OsobyStan()) return _netStan.TryGetValue(u.Id, out var d) && d.TryGetValue(id, out var f) ? f : null; }
        void UserZmien(NetUser u, string id, Action<UserFilm> zmiana)
        {
            lock (OsobyStan()) { if (!_netStan.TryGetValue(u.Id, out var d)) _netStan[u.Id] = d = new Dictionary<string, UserFilm>(); if (!d.TryGetValue(id, out var f)) d[id] = f = new UserFilm(); zmiana(f); f.W = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
            NetStanSave();
        }
        // postęp / obejrzany / ulubiony od osoby (internet i sieć domowa) - administrator albo brak osoby: biblioteka serwera jak dotąd
        bool UserProg(NetUser u, string id, long pos, long len, bool done, bool unwatch)
        {
            if (u == null || u.Admin) return false;
            UserZmien(u, id, f => { if (done) { f.D = true; f.P = 0; } else if (unwatch) { f.D = false; f.P = 0; } else { f.P = pos; if (len > 0 && pos > len - 60000) { f.D = true; f.P = 0; } } if (len > 0) f.L = len; });
            return true;
        }
        bool UserFav(NetUser u, string id, bool on) { if (u == null || u.Admin) return false; UserZmien(u, id, f => f.F = on); return true; }
        LanFilmItem UserItem(NetUser u, LanFilmItem x)
        {
            if (u == null || u.Admin) return x;
            var f = UserStan(u, x.i); x.p = f == null || f.D ? 0 : f.P; x.d = f?.D == true; x.w = f?.W ?? 0; x.fv = f?.F == true; if (f?.L > 0) x.l = f.L; return x;
        }
        static string NetDevFile => Path.Combine(DataDir, "serwer-urzadzenia.bin");
        static string NetCertFile => Path.Combine(DataDir, "serwer-certyfikat.bin");
        List<NetDevice> _netDevs; X509Certificate2 _netCert; TcpListener _netListener;
        string _netCode; internal string NetStan = "";
        readonly ConcurrentDictionary<string, (int N, DateTime At)> _netBad = new ConcurrentDictionary<string, (int, DateTime)>();

        static byte[] Dp(byte[] b, bool zaszyfruj) => zaszyfruj ? ProtectedData.Protect(b, null, DataProtectionScope.CurrentUser) : ProtectedData.Unprotect(b, null, DataProtectionScope.CurrentUser);
        List<NetDevice> NetDevs()
        {
            if (_netDevs != null) return _netDevs;
            try { _netDevs = File.Exists(NetDevFile) ? JsonSerializer.Deserialize<List<NetDevice>>(Dp(File.ReadAllBytes(NetDevFile), false)) : null; } catch (Exception ex) { App.LogError(ex); }
            return _netDevs ??= new List<NetDevice>();
        }
        void NetDevsSave() { try { lock (NetDevs()) VelivoData.SafeWrite(NetDevFile, Dp(JsonSerializer.SerializeToUtf8Bytes(_netDevs), true)); } catch (Exception ex) { App.LogError(ex); } }
        X509Certificate2 NetCert()
        {
            if (_netCert != null) return _netCert;
            try { if (File.Exists(NetCertFile)) return _netCert = X509CertificateLoader.LoadPkcs12(Dp(File.ReadAllBytes(NetCertFile), false), null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable); } catch (Exception ex) { App.LogError(ex); }
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=Velivo Serwer filmow " + Environment.MachineName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(25));
            var pfx = c.Export(X509ContentType.Pfx);
            Directory.CreateDirectory(DataDir); File.WriteAllBytes(NetCertFile, Dp(pfx, true));
            return _netCert = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        }
        static string Odcisk(X509Certificate c) => Convert.ToHexString(SHA256.HashData(c.GetRawCertData()));

        internal bool NetWlaczony => GetPref("serwer-net") == "1";
        // start / zatrzymanie (okno serwera, start programu); router - UPnP jak komunikator
        internal void NetSerwerStart()
        {
            if (!Wariant.Serwer) return;
            if (!NetWlaczony) { try { _netListener?.Stop(); } catch (Exception) { } _netListener = null; NetStan = L.T("wyłączony"); return; }
            if (_netListener != null) return;
            try
            {
                NetCert(); var l = new TcpListener(IPAddress.Any, NetFilmPort); l.Start(); _netListener = l;
                _ = Task.Run(async () => { while (true) { TcpClient c; try { c = await l.AcceptTcpClientAsync(); } catch (Exception) { return; } _ = Task.Run(() => NetObsluz(c)); } });
                NetStan = L.T("działa – sprawdzam router…");
                // 🔁 router sam (UPnP): ponawiane co 10 min, póki port nie jest otwarty, potem odświeżane co godzinę (router po restarcie zapomina) - bez ręcznego przekierowania
                _ = Task.Run(async () =>
                {
                    while (_netListener == l)
                    {
                        bool mapped = false;
                        try { var (ext, m, note) = await KomUpnpAsync(NetFilmPort); mapped = m;
                            NetStan = m ? L.T("działa – port otwarty w routerze") : L.T("działa w domu; router: ") + note + L.T(" – ponawiam co 10 min (albo przekieruj ręcznie port TCP ") + NetFilmPort + L.T(" na ten komputer)"); }
                        catch (Exception ex) { App.LogError(ex); }
                        await Task.Delay(TimeSpan.FromMinutes(mapped ? 60 : 10));
                    }
                });
            }
            catch (Exception ex) { App.LogError(ex); NetStan = L.T("nie działa – port ") + NetFilmPort + L.T(" zajęty"); }
        }
        // hasło serwera (jak Jellyfin: adres + hasło), zapisane zaszyfrowane (DPAPI); urządzenie po zalogowaniu dostaje własny klucz
        static string NetPassFile => Path.Combine(DataDir, "serwer-haslo.bin");
        internal string NetHaslo { get { try { if (_netCode == null && File.Exists(NetPassFile)) _netCode = Encoding.UTF8.GetString(Dp(File.ReadAllBytes(NetPassFile), false)); } catch (Exception ex) { App.LogError(ex); } return _netCode; }
            set { _netCode = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); try { if (_netCode == null) File.Delete(NetPassFile); else VelivoData.SafeWrite(NetPassFile, Dp(Encoding.UTF8.GetBytes(_netCode), true)); } catch (Exception ex) { App.LogError(ex); } } }
        internal List<(string Id, string Name, long Seen, string UserId)> NetUrzadzenia() { lock (NetDevs()) return _netDevs.Select(d => (d.Id, d.Name, d.Seen, d.UserId)).ToList(); }
        internal void NetOdlaczOsobe(string userId) { lock (NetDevs()) _netDevs.RemoveAll(d => d.UserId == userId); NetDevsSave(); }
        internal void NetOdlacz(string id) { lock (NetDevs()) _netDevs.RemoveAll(d => d.Id == id); NetDevsSave(); }

        static async Task<(string Method, string Path, Dictionary<string, string> H)> HttpHead(Stream s)
        {
            var sb = new StringBuilder(); var b = new byte[1];
            while (sb.Length < 8192) { if (await s.ReadAsync(b, 0, 1) != 1) return (null, null, null); sb.Append((char)b[0]); if (sb.Length >= 4 && sb[sb.Length - 1] == '\n' && sb.ToString().EndsWith("\r\n\r\n")) break; }
            var lines = sb.ToString().Split("\r\n"); var f = lines[0].Split(' '); if (f.Length < 2) return (null, null, null);
            var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in lines.Skip(1)) { int i = x.IndexOf(':'); if (i > 0) h[x.Substring(0, i).Trim()] = x.Substring(i + 1).Trim(); }
            return (f[0], f[1], h);
        }
        static async Task HttpOdp(Stream s, int code, string type, byte[] body)
        {
            var hdr = Encoding.ASCII.GetBytes("HTTP/1.1 " + code + (code == 200 ? " OK" : code == 403 ? " Forbidden" : " Not Found") + "\r\nContent-Type: " + type + "\r\nContent-Length: " + (body?.Length ?? 0) + "\r\nConnection: close\r\n\r\n");
            await s.WriteAsync(hdr, 0, hdr.Length); if (body != null) await s.WriteAsync(body, 0, body.Length);
        }
        static Dictionary<string, string> Zapytanie(string path) { var d = new Dictionary<string, string>(); int i = path.IndexOf('?'); if (i < 0) return d; foreach (var kv in path.Substring(i + 1).Split('&')) { var p = kv.Split('=', 2); if (p.Length == 2) d[p[0]] = Uri.UnescapeDataString(p[1]); } return d; }
        static string Podpis(string key, string method, string path, string ts) => Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(key), Encoding.UTF8.GetBytes(method + " " + path + " " + ts)));

        async Task NetObsluz(TcpClient c)
        {
            using (c)
            {
                var ip = ((IPEndPoint)c.Client.RemoteEndPoint).Address.MapToIPv4().ToString();
                if (_netBad.TryGetValue(ip, out var bad) && bad.N >= 10 && DateTime.UtcNow - bad.At < TimeSpan.FromHours(1)) return;   // blokada adresu na godzinę
                try
                {
                    c.ReceiveTimeout = 20000; c.SendTimeout = 60000;
                    using var ssl = new SslStream(c.GetStream(), false);
                    await ssl.AuthenticateAsServerAsync(NetCert(), false, System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13, false);
                    var (m, path, h) = await HttpHead(ssl); if (m == null) return;
                    var q = Zapytanie(path); var route = path.Split('?')[0];
                    void Zle() { _netBad.AddOrUpdate(ip, (1, DateTime.UtcNow), (_, o) => (DateTime.UtcNow - o.At > TimeSpan.FromHours(1) ? 1 : o.N + 1, DateTime.UtcNow)); }
                    if (route == "/ping") { await HttpOdp(ssl, 200, "text/plain", Encoding.UTF8.GetBytes(Environment.MachineName)); return; }
                    if (route == "/users") { await HttpOdp(ssl, 200, "application/json", JsonSerializer.SerializeToUtf8Bytes(NetUserNames())); return; }   // „Kto ogląda?” - same imiona
                    if (route == "/pair")
                    {   // osoba + jej hasło; dowód, że klient widzi TEN certyfikat (hasło nie idzie siecią); bez imienia (starsza wersja) - każda osoba po kolei
                        var fpS = Odcisk(NetCert()); NetUser kto = null;
                        List<NetUser> kandydaci; lock (NetUsers()) kandydaci = q.TryGetValue("user", out var un) && un.Length > 0 ? _netUsers.Where(u => string.Equals(u.Name, un.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList() : _netUsers.ToList();
                        if (q.TryGetValue("proof", out var pr))
                            foreach (var u in kandydaci)
                            {
                                var dowod = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(u.Pass ?? "-"), Encoding.UTF8.GetBytes(fpS)));
                                if (!string.IsNullOrEmpty(u.Pass) && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(pr.ToUpperInvariant()), Encoding.ASCII.GetBytes(dowod))) { kto = u; break; }
                            }
                        if (kto == null) { Zle(); await HttpOdp(ssl, 403, "text/plain", null); return; }
                        var nm = q.TryGetValue("name", out var nn) ? nn : "?"; if (nm.Length > 60) nm = nm.Substring(0, 60);
                        var d = new NetDevice { Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)), Name = nm, Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), Seen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), UserId = kto.Id };
                        lock (NetDevs()) _netDevs.Add(d); NetDevsSave();
                        await HttpOdp(ssl, 200, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { id = d.Id, key = d.Key, name = Environment.MachineName, user = kto.Name })); return;
                    }
                    // podpis urządzenia
                    NetDevice dev; lock (NetDevs()) dev = h.TryGetValue("X-Dev", out var di) ? _netDevs.FirstOrDefault(x => x.Id == di) : null;
                    if (dev == null || !h.TryGetValue("X-Ts", out var ts) || !long.TryParse(ts, out var tsv) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - tsv) > 600
                        || !h.TryGetValue("X-Sig", out var sig) || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(sig), Encoding.ASCII.GetBytes(Podpis(dev.Key, m, path, ts))))
                    { Zle(); await HttpOdp(ssl, 403, "text/plain", null); return; }
                    NetUser usr; lock (NetUsers()) usr = _netUsers.FirstOrDefault(u => u.Id == dev.UserId);
                    if (usr == null) { Zle(); await HttpOdp(ssl, 403, "text/plain", null); return; }   // osoba usunięta - jej urządzenia odcięte
                    if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - dev.Seen > 60000) { dev.Seen = usr.Seen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); NetDevsSave(); }
                    // pełna lista (sprawdzenie każdego pliku) tylko dla /lib; plakat, film, postęp - tylko ten jeden film (było: setki sprawdzeń plików przy każdym plakacie i przewinięciu)
                    var wszystkie = LoadLibrary();
                    if (route == "/lib")
                    {
                        var lib = wszystkie.Where(v => File.Exists(v.Path) && SerwerUdostepnia(v) && UserWidzi(usr, v.Path)).GroupBy(v => FilmId(v.Path)).Select(g => g.OrderByDescending(v => v.Last).First()).ToList();
                        var ulub = SrvUlub();   // ❤ raz na listę
                        var items = lib.Select(v =>
                        {
                            var mi = LookupMeta(Meta, v.Path); MovieInfo info = null;
                            if (mi != null) { var ov = mi.Overview ?? ""; if (ov.Length > 700) ov = ov.Substring(0, 700) + "…"; info = new MovieInfo { Name = mi.Name, Original = mi.Original, Year = mi.Year, Overview = ov, Rating = mi.Rating, Age = mi.Age, Director = mi.Director, Genres = (mi.Genres ?? new List<string>()).Take(4).ToList() }; }
                            bool pl = mi?.Poster != null && File.Exists(Path.Combine(PosterDir, mi.Poster));
                            return UserItem(usr, new LanFilmItem { i = FilmId(v.Path), fv = ulub.Contains(FilmId(v.Path)), t = v.Title, l = v.Len, p = v.Done ? 0 : v.Pos, d = v.Done, w = v.Last, fn = Path.GetFileName(v.Path), m = info, sv = true, pu = pl ? "p" : null });
                        }).ToList();
                        await HttpOdp(ssl, 200, "application/json", JsonSerializer.SerializeToUtf8Bytes(items)); return;
                    }
                    var id = route.Length > 3 ? route.Substring(3) : ""; var szukane = route == "/prog" || route == "/fav" ? (q.TryGetValue("id", out var qi) ? qi : "") : id;
                    var film = wszystkie.Where(v => FilmId(v.Path) == szukane && File.Exists(v.Path) && SerwerUdostepnia(v) && UserWidzi(usr, v.Path)).OrderByDescending(v => v.Last).FirstOrDefault();
                    if (film == null) { await HttpOdp(ssl, 404, "text/plain", null); return; }
                    if (route == "/fav") { bool on1 = q.TryGetValue("on", out var on) && on == "1"; if (!UserFav(usr, FilmId(film.Path), on1)) SrvUlubSet(FilmId(film.Path), on1); await HttpOdp(ssl, 200, "text/plain", null); return; }
                    if (route.StartsWith("/p/"))
                    {
                        var mi = LookupMeta(Meta, film.Path); var pf = mi?.Poster != null ? Path.Combine(PosterDir, mi.Poster) : null;
                        if (pf == null || !File.Exists(pf)) { await HttpOdp(ssl, 404, "text/plain", null); return; }
                        await HttpOdp(ssl, 200, "image/jpeg", File.ReadAllBytes(pf)); return;
                    }
                    if (route == "/prog")
                    {   // postęp jak z komputera w domu („prog”)
                        long.TryParse(q.TryGetValue("pos", out var ps) ? ps : "0", out var pos); long.TryParse(q.TryGetValue("len", out var ls) ? ls : "0", out var len); bool done = q.TryGetValue("done", out var dn) && dn == "1";
                        if (UserProg(usr, FilmId(film.Path), pos, len, done, q.ContainsKey("unwatch"))) { await HttpOdp(ssl, 200, "text/plain", null); return; }   // osoba - własny postęp
                        await Dispatcher.InvokeAsync(() =>
                        {
                            var l = LoadLibrary(); var v = l.FirstOrDefault(x => FilmId(x.Path) == FilmId(film.Path) && SerwerUdostepnia(x)); if (v == null) return;
                            if (done) { v.Done = true; v.Pos = 0; v.Last = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); if (len > 0) v.Len = len; }
                            else if (pos == 0 && q.ContainsKey("unwatch")) { v.Done = false; v.Pos = 0; }
                            else RecordProgress(v, pos, len, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                            SaveLibrary(l); _libRefresh?.Invoke();
                        });
                        await HttpOdp(ssl, 200, "text/plain", null); return;
                    }
                    if (route.StartsWith("/f/"))
                    {   // film z przewijaniem (zakresy bajtów)
                        using var fs = new FileStream(film.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, true);
                        long size = fs.Length; var rg = ParseRange(h.TryGetValue("Range", out var r) ? r : null, size);
                        long from = rg?.From ?? 0, to = rg?.To ?? size - 1, count = Math.Max(0, to - from + 1);
                        var hdr = Encoding.ASCII.GetBytes((rg != null ? "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes " + from + "-" + to + "/" + size + "\r\n" : "HTTP/1.1 200 OK\r\n")
                            + "Content-Type: application/octet-stream\r\nAccept-Ranges: bytes\r\nContent-Length: " + count + "\r\nConnection: close\r\n\r\n");
                        await ssl.WriteAsync(hdr, 0, hdr.Length); if (m == "HEAD") return;
                        fs.Position = from; var buf = new byte[1 << 16];
                        while (count > 0) { int n = await fs.ReadAsync(buf, 0, (int)Math.Min(buf.Length, count)); if (n <= 0) break; await ssl.WriteAsync(buf, 0, n); count -= n; }
                        return;
                    }
                    await HttpOdp(ssl, 404, "text/plain", null);
                }
                catch (Exception) { }   // zerwane połączenie (przewinięcie, zamknięcie) - normalne
            }
        }

        // ======================= KOMPUTER (klient) =======================
        internal sealed class NetSerwer { public string Name { get; set; } public string Host { get; set; } public int Port { get; set; } public string Id { get; set; } public string Key { get; set; } public string Fp { get; set; } }
        static string NetSrvFile => Path.Combine(DataDir, "moje-serwery-internet.bin");
        List<NetSerwer> _netSrv;
        internal List<NetSerwer> NetSerwery()
        {
            if (_netSrv != null) return _netSrv;
            try { _netSrv = File.Exists(NetSrvFile) ? JsonSerializer.Deserialize<List<NetSerwer>>(Dp(File.ReadAllBytes(NetSrvFile), false)) : null; } catch (Exception ex) { App.LogError(ex); }
            return _netSrv ??= new List<NetSerwer>();
        }
        void NetSerweryZapisz() { try { VelivoData.SafeWrite(NetSrvFile, Dp(JsonSerializer.SerializeToUtf8Bytes(_netSrv), true)); } catch (Exception ex) { App.LogError(ex); } }

        static System.Net.Http.HttpClient NetHttp(string fp, Action<string> widzi = null) => new System.Net.Http.HttpClient(new System.Net.Http.SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (o, cert, ch, err) => { if (cert == null) return false; var f = Odcisk(cert); widzi?.Invoke(f); return fp == null || f == fp; } },
            ConnectTimeout = TimeSpan.FromSeconds(10)
        }) { Timeout = TimeSpan.FromSeconds(30) };

        // logowanie: adres (n5plus.ddns.net:41940) i hasło serwera; dowód = HMAC(hasło, odcisk widzianego certyfikatu)
        internal async Task<string> NetSparuj(string adres, string kod, string osoba = "")
        {
            adres = (adres ?? "").Trim().Replace("https://", "").Replace("http://", "").TrimEnd('/'); kod = (kod ?? "").Trim();
            int port = NetFilmPort; var host = adres; int c = adres.LastIndexOf(':'); if (c > 0 && int.TryParse(adres.Substring(c + 1), out var pp)) { port = pp; host = adres.Substring(0, c); }
            if (host.Length == 0 || kod.Length == 0) return L.T("Wpisz adres serwera i hasło serwera.");
            string fp = null;
            try
            {
                using var http = NetHttp(null, f => fp = f);
                await http.GetAsync("https://" + host + ":" + port + "/ping");   // odcisk certyfikatu
                if (fp == null) return L.T("Nie widać serwera pod tym adresem.");
                var dowod = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(kod), Encoding.UTF8.GetBytes(fp)));
                var r = await http.GetAsync("https://" + host + ":" + port + "/pair?proof=" + dowod + "&user=" + Uri.EscapeDataString((osoba ?? "").Trim()) + "&name=" + Uri.EscapeDataString(Environment.MachineName));
                if (!r.IsSuccessStatusCode) return L.T("Złe imię albo hasło.");
                var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
                var s = new NetSerwer { Name = j.GetProperty("name").GetString(), Host = host, Port = port, Id = j.GetProperty("id").GetString(), Key = j.GetProperty("key").GetString(), Fp = fp };
                NetSerwery().RemoveAll(x => x.Name == s.Name); _netSrv.Add(s); NetSerweryZapisz();
                return null;
            }
            catch (Exception ex) { return L.T("Nie udało się połączyć: ") + ex.Message; }
        }
        // zapytanie podpisane (lista, postęp, ping)
        async Task<byte[]> NetPobierz(NetSerwer s, string path)
        {
            using var http = NetHttp(s.Fp);
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "https://" + s.Host + ":" + s.Port + path);
            req.Headers.Add("X-Dev", s.Id); req.Headers.Add("X-Ts", ts); req.Headers.Add("X-Sig", Podpis(s.Key, "GET", path, ts));
            var r = await http.SendAsync(req); r.EnsureSuccessStatusCode(); return await r.Content.ReadAsByteArrayAsync();
        }
        async Task<List<LanFilmItem>> NetLista(int idx)
        {
            var s = NetSerwery()[idx];
            var items = JsonSerializer.Deserialize<List<LanFilmItem>>(await NetPobierz(s, "/lib")) ?? new List<LanFilmItem>();
            foreach (var x in items) if (x.pu != null) x.pu = NetProxyUrl(idx, "p/" + x.i);   // plakat przez pośrednika (pobierany jak z serwera w domu)
            return items;
        }
        internal async Task<bool> NetPing(int idx) { try { await NetPobierz(NetSerwery()[idx], "/ping"); return true; } catch (Exception) { return false; } }
        internal void NetFav(int idx, string id, bool on) { var path = "/fav?id=" + id + "&on=" + (on ? "1" : "0"); _ = Task.Run(async () => { try { await NetPobierz(NetSerwery()[idx], path); } catch (Exception) { } }); }
        // ❤ ulubione serwera - jedna lista dla wszystkich moich urządzeń (komputery w domu, przez internet, telefon)
        HashSet<string> SrvUlub() => new HashSet<string>((GetPref("serwer-ulubione") ?? "").Split('\n').Where(x => x.Length > 0));
        void SrvUlubSet(string id, bool on)
        {
            if (string.IsNullOrEmpty(id)) return;
            Dispatcher.BeginInvoke(new Action(() => { var l = SrvUlub(); if (on ? l.Add(id) : l.Remove(id)) { SetPref("serwer-ulubione", string.Join("\n", l)); _sharedCache = null; } }));
        }
        internal void NetPostep(int idx, string id, long pos, long len, bool done, bool odznacz = false)
        {
            var path = "/prog?id=" + id + "&pos=" + pos + "&len=" + len + (done ? "&done=1" : "") + (odznacz ? "&unwatch=1" : "");
            _ = Task.Run(async () => { try { await NetPobierz(NetSerwery()[idx], path); } catch (Exception) { } });
        }

        // lokalny pośrednik dla odtwarzacza: http://127.0.0.1:<port>/n/<serwer>/<f|p>/<id> → TLS do serwera, podpisane, z zakresem
        TcpListener _netProxy; int _netProxyPort;
        string NetProxyUrl(int idx, string what)
        {
            if (_netProxy == null)
            {
                var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); _netProxy = l; _netProxyPort = ((IPEndPoint)l.LocalEndpoint).Port;
                _ = Task.Run(async () => { while (true) { TcpClient c; try { c = await l.AcceptTcpClientAsync(); } catch (Exception) { return; } _ = Task.Run(() => NetPosrednik(c)); } });
            }
            return "http://127.0.0.1:" + _netProxyPort + "/n/" + idx + "/" + what;
        }
        async Task NetPosrednik(TcpClient c)
        {
            using (c)
                try
                {
                    var ns = c.GetStream(); var (m, path, h) = await HttpHead(ns); if (m == null) return;
                    var p = path.Split('/');   // "", n, idx, f|p, id
                    if (p.Length < 5 || p[1] != "n" || !int.TryParse(p[2], out var idx) || idx < 0 || idx >= NetSerwery().Count) { await HttpOdp(ns, 404, "text/plain", null); return; }
                    var s = NetSerwery()[idx]; var rpath = "/" + p[3] + "/" + p[4];
                    using var tcp = new TcpClient(); await tcp.ConnectAsync(s.Host, s.Port);
                    using var ssl = new SslStream(tcp.GetStream(), false, (o, cert, ch, err) => cert != null && Odcisk(cert) == s.Fp);
                    await ssl.AuthenticateAsClientAsync(s.Host);
                    var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                    var req = m + " " + rpath + " HTTP/1.1\r\nHost: " + s.Host + "\r\nX-Dev: " + s.Id + "\r\nX-Ts: " + ts + "\r\nX-Sig: " + Podpis(s.Key, m, rpath, ts) + "\r\n"
                        + (h.TryGetValue("Range", out var r) ? "Range: " + r + "\r\n" : "") + "Connection: close\r\n\r\n";
                    var rb = Encoding.ASCII.GetBytes(req); await ssl.WriteAsync(rb, 0, rb.Length);
                    await ssl.CopyToAsync(ns);   // odpowiedź serwera (nagłówki + dane) prosto do odtwarzacza
                }
                catch (Exception) { }
        }

        // 🌍 moje serwery przez internet jako „komputery” w Kanapie - tylko gdy tego serwera nie widać w sieci domowej (w domu bez zmian)
        int? NetIdx(string id) => id != null && id.StartsWith("net:") && int.TryParse(id.Substring(4), out var i) && i >= 0 && i < NetSerwery().Count ? i : (int?)null;
        List<LanPeerInfo> NetPeers(List<LanPeerInfo> lan) => NetSerwery().Select((s, i) => (s, i)).Where(z => !lan.Any(p => string.Equals(p.Device, z.s.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(z => new LanPeerInfo { Id = "net:" + z.i, Device = z.s.Name }).ToList();

        // okno: dodaj mój serwer filmów przez internet (adres domu + hasło serwera) - jak logowanie do Jellyfin
        internal void NetDodajOkno(System.Windows.Window owner, Action done)
        {
            var w = new System.Windows.Window { Title = L.T("Mój serwer filmów przez internet"), Width = 460, SizeToContent = System.Windows.SizeToContent.Height, Owner = owner, WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner, Background = SBg, Topmost = owner?.Topmost == true, ResizeMode = System.Windows.ResizeMode.NoResize };
            var sp = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(18) }; w.Content = sp;
            var op = STxt(L.T("Wpisz adres domu z portem serwera (np. moj-dom.ddns.net:41940) i hasło z okna Velivo Serwer filmów. Hasło nie jest wysyłane – połączenie jest szyfrowane."), 13, SDim); op.TextWrapping = System.Windows.TextWrapping.Wrap; sp.Children.Add(op);
            System.Windows.Controls.TextBox Pole(string etykieta, string tekst)
            {
                var l = STxt(etykieta, 13); l.Margin = new System.Windows.Thickness(0, 12, 0, 4); sp.Children.Add(l);
                var t = new System.Windows.Controls.TextBox { Text = tekst, Background = SField, Foreground = SFg, CaretBrush = SFg, BorderBrush = SLine, Padding = new System.Windows.Thickness(6, 4, 6, 4), FontSize = 14 }; sp.Children.Add(t); return t;
            }
            var adr = Pole(L.T("Adres serwera:"), NetSerwery().LastOrDefault() is NetSerwer o ? o.Host + ":" + o.Port : "");
            var oso = Pole(L.T("Imię (osoba na serwerze):"), Environment.UserName); oso.ToolTip = L.T("Twoje imię na serwerze – jak w oknie serwera („Użytkownicy”)");
            var has = Pole(L.T("Hasło:"), "");
            var wyn = STxt("", 13, SDim); wyn.TextWrapping = System.Windows.TextWrapping.Wrap; wyn.Margin = new System.Windows.Thickness(0, 10, 0, 0); sp.Children.Add(wyn);
            var row = new System.Windows.Controls.WrapPanel { Margin = new System.Windows.Thickness(0, 12, 0, 0) }; sp.Children.Add(row);
            System.Windows.Controls.Button ok = null;
            ok = SBtnMake(L.T("🔐 Połącz"), async () =>
            {
                ok.IsEnabled = false; wyn.Text = L.T("Łączę…");
                var blad = await NetSparuj(adr.Text, has.Text, oso.Text); ok.IsEnabled = true;
                if (blad != null) { wyn.Text = blad; return; }
                w.Close(); done?.Invoke();
            }, SAccent);
            row.Children.Add(ok);
            if (NetSerwery().Count > 0) row.Children.Add(SBtnMake(L.T("🗑 Zapomnij moje serwery internetowe"), () => { NetSerwery().Clear(); NetSerweryZapisz(); w.Close(); done?.Invoke(); }));
            w.Loaded += (s, e) => (adr.Text.Length > 0 ? has : adr).Focus();
            w.ShowDialog();
        }
    }
}
