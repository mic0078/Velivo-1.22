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
            public KomFrameStream(TcpClient cl, byte[] key) { _cl = cl; _raw = cl.GetStream(); _gcm = new AesGcm(key, 16); _raw.ReadTimeout = 150000; _raw.WriteTimeout = 60000; }
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
                var n = RandomNumberGenerator.GetBytes(12); var f = new byte[len + 28];
                lock (_gcm) _gcm.Encrypt(n, data.AsSpan(off, len), f.AsSpan(12, len), f.AsSpan(12 + len, 16));
                n.CopyTo(f, 0);
                lock (_wl) { _raw.Write(BitConverter.GetBytes(f.Length), 0, 4); _raw.Write(f, 0, f.Length); _raw.Flush(); }
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

        static byte[] KomSessionKey(byte[] key, byte[] nc, byte[] ns) => SHA256.HashData(key.Concat(Encoding.ASCII.GetBytes("velivo-relay")).Concat(nc).Concat(ns).ToArray());

        // ---------- polaczenie z pośrednikiem (urzadzenie poza domem) ----------
        // mode: "main" (pakiety), "tunnel" (plik do To), "tunnel-in" (plik do nas, Tunnel = id)
        async Task<KomFrameStream> KomRelayConnect(KomContact relay, string mode, string tunnel, string to, CancellationToken token)
        {
            var hp = (relay.Relay ?? "").Split(':'); if (hp.Length != 2 || !int.TryParse(hp[1], out var port)) return null;
            var cl = new TcpClient();
            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(12));
                    await cl.ConnectAsync(hp[0], port, cts.Token);   // nazwa No-IP sprawdzana przy kazdym polaczeniu - zmiana IP domu nie przeszkadza
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

        static byte[] KomMacBytes(byte[] key, string label, byte[] data) { using (var h = new HMACSHA256(key)) return h.ComputeHash(Encoding.ASCII.GetBytes(label).Concat(data).ToArray()); }

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
        async Task KomBoxStore(JsonNode h, byte[] hb, Stream s, CancellationToken token)
        {
            var to = (string)h["To"] ?? ""; long size = (long?)h["Size"] ?? -1;
            if (to.Length != 32 || size < 0 || size > LanFile2Limit) return;
            if (!await Dispatcher.InvokeAsync(() => KomContactById(to) != null && KomContactById((string)h["Id"] ?? "") != null)) return;   // tylko między kontaktami tego komputera
            var dir = KomBoxDir(to); Directory.CreateDirectory(dir);
            var f = Path.Combine(dir, DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N"));
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
                File.Move(f + ".part", f + ".bin"); File.WriteAllBytes(f + ".hdr", hb);
                await s.WriteAsync(new byte[32], 0, 32, token); await s.FlushAsync(token);   // przechowane
            }
            finally { try { File.Delete(f + ".part"); } catch (Exception) { } }
            _ = KomBoxFlush(to);
        }
        readonly ConcurrentDictionary<string, bool> _komBoxBusy = new ConcurrentDictionary<string, bool>();
        async Task KomBoxFlush(string id)
        {
            var dir = KomBoxDir(id);
            if (!Directory.Exists(dir) || !_komBoxBusy.TryAdd(id, true)) return;
            try
            {
                foreach (var hdr in Directory.GetFiles(dir, "*.hdr").OrderBy(x => x))
                {
                    var bin = Path.ChangeExtension(hdr, ".bin");
                    if (!File.Exists(bin)) { File.Delete(hdr); continue; }
                    using var cts = new CancellationTokenSource(TimeSpan.FromHours(2));
                    Stream st = null; TcpClient cl = null;
                    try
                    {
                        if (_komRemote.ContainsKey(id)) st = await KomRelayOpenTo(id, cts.Token);
                        else if (_komLanAddr.TryGetValue(id, out var a) && _komLanSeen.TryGetValue(id, out var seen) && DateTime.UtcNow - seen < TimeSpan.FromSeconds(100))
                        { cl = await KomTcpConnect(a, cts.Token); st = cl.GetStream(); }
                        if (st == null) return;   // odbiorcy nie ma - zostaje w skrzynce
                        await KomWriteBlock(st, File.ReadAllBytes(hdr), cts.Token);
                        var b = new byte[1]; if (!await ReadExactAsync(st, b, 1, cts.Token)) return;
                        if (b[0] == 1)
                        {
                            using (var fi = File.OpenRead(bin)) await fi.CopyToAsync(st, 1 << 16, cts.Token);
                            await st.FlushAsync(cts.Token);
                            if (!await ReadExactAsync(st, new byte[32], 32, cts.Token)) return;
                        }
                        File.Delete(bin); File.Delete(hdr);   // odebrany (albo odrzucony przez odbiorcę)
                    }
                    catch (Exception) { return; }
                    finally { st?.Dispose(); cl?.Dispose(); }
                }
                if (Directory.GetFiles(dir).Length == 0) Directory.Delete(dir);
            }
            finally { _komBoxBusy.TryRemove(id, out _); }
        }
        // nadawca: odbiorcy nie ma - plik do skrzynki pośrednika (sam pośrednik: do własnej)
        async Task<string> KomSendViaBox(KomContact to, string path, string mid, KomContact group = null)
        {
            IPAddress addr = IPAddress.Loopback;
            if (_komRelayListener == null && _komUp == null)
            {   // w domu: pośrednik w sieci domowej
                var rc = _komContacts.FirstOrDefault(c => !string.IsNullOrEmpty(c.Relay) && _komLanSeen.TryGetValue(c.Id, out var seen) && DateTime.UtcNow - seen < TimeSpan.FromSeconds(100));
                if (rc == null || !IPAddress.TryParse(rc.Addr ?? "", out addr)) return "brak";
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
            if (from == null || from.Length != 32 || IPAddress.IsLoopback(addr)) return;
            if (_komRemote.ContainsKey(from)) return;   // echo pakietu urzadzenia poza domem, ktory sami przekazalismy - nie jest w domu
            _komLanAddr[from] = addr; _komLanSeen[from] = DateTime.UtcNow; _komViaNet.TryRemove(from, out _);
            KomStoreFlush(from, null, addr);   // 📬 wrócił do domu - wiadomości, które na niego czekały
            if ((string)p["to"] is string sto && KomStoreKeep(p, sto)) return;
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
            lock (l) { if (!l.Any(x => (string)x["mid"] == mid)) { l.Add(p.DeepClone()); if (l.Count > 300) l.RemoveAt(0); } }
            KomStoreSave();
            return true;
        }
        void KomStoreFlush(string id, KomRemoteConn rc, IPAddress addr)
        {
            if (_komRelayListener != null) _ = KomBoxFlush(id);   // 📬 pliki ze skrzynki
            if (!_komStore.TryRemove(id, out var l)) return;
            lock (l) foreach (var p in l) { if (rc != null) KomSendFrame(rc, p); else KomLanRaw(p, addr); }
            KomStoreSave();
        }
        static string KomStoreFile => Path.Combine(DataDir, "komunikator-skrzynka.json");
        void KomStoreSave()
        {
            try
            {
                var o = new JsonObject();
                foreach (var kv in _komStore) lock (kv.Value) o[kv.Key] = new JsonArray(kv.Value.Select(x => x.DeepClone()).ToArray());
                File.WriteAllText(KomStoreFile, o.ToJsonString());
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

        void KomLanRaw(JsonNode p, IPAddress to)
        {
            var d = Encoding.UTF8.GetBytes(p.ToJsonString());
            var s = _komUdp ?? _lanTx; if (s == null) return;
            foreach (var t in to != null ? new List<IPAddress> { to } : KomBroadcastTargets()) foreach (var pt in new[] { KomPort, LanPort }) try { s.Send(d, d.Length, new IPEndPoint(t, pt)); } catch (Exception) { }
        }

        // ---------- pośrednik (serwer) ----------
        void KomRelayStart()
        {
            KomRelayStop();
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
                    await KomRelayAutoAsync();
                };
            }
            _komRelayAutoTimer.Start();
            try
            {
                _komRelayCts = new CancellationTokenSource(); var token = _komRelayCts.Token;
                var l = new TcpListener(IPAddress.Any, _komRelayPort); l.Start(); _komRelayListener = l;
                _ = Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        try { var c = await l.AcceptTcpClientAsync(token); _ = Task.Run(() => KomRelayServe(c, token)); }
                        catch (OperationCanceledException) { break; } catch (ObjectDisposedException) { break; } catch (Exception) { await Task.Delay(300); }
                    }
                });
            }
            catch (Exception ex) { App.LogError(ex); KomInfo(L.T("🌍 Pośrednik: port zajęty lub zablokowany – ") + ex.Message); }
        }

        void KomRelayStop()
        {
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
                if (hello == null || mac == null) { cl.Dispose(); return; }
                var h = JsonNode.Parse(hello);
                if ((string)h?["Type"] == "relay-ask") { await KomAskServe(h, mac, s, token); return; }   // 🔗 prośba o kontakt z linku „Mój adres Velivo” (ktoś spoza kontaktów)
                id = (string)h["Id"];
                KomContact c = null; await Dispatcher.InvokeAsync(() => { KomLoad(); c = KomContactById(id ?? ""); });
                // ✉ nowe urzadzenie z zaproszeniem: klucz zaproszenia zamiast klucza kontaktu (jednorazowo, do dolaczenia)
                KomInvite inv = null;
                if ((string)h["Mode"] == "invite" && _komInvites.TryGetValue((string)h["Tunnel"] ?? "", out inv) && inv.Exp > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() && id?.Length == 32 && id != _komId)
                    c = new KomContact { Id = id, Key = inv.K };
                long ts = (long?)h["Ts"] ?? 0;
                // tylko kontakty tego komputera, swiezy podpis kluczem kontaktu
                if (c == null || (string)h["Type"] != "relay-hello" || (string)h["Relay"] != _komId || Math.Abs(ts - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) > 300000) { cl.Dispose(); return; }
                var exp = KomMacBytes(c.Key, "relay-hello\n", hello);
                if (mac.Length != exp.Length || !CryptographicOperations.FixedTimeEquals(mac, exp)) { cl.Dispose(); return; }
                var nc = KomB64((string)h["Nonce"]); if (nc.Length != 16) { cl.Dispose(); return; }
                var ns = RandomNumberGenerator.GetBytes(16);
                var rep = Encoding.UTF8.GetBytes(new JsonObject { ["Nonce"] = Convert.ToBase64String(ns) }.ToJsonString());
                await KomWriteBlock(s, rep, token);
                await KomWriteBlock(s, KomMacBytes(c.Key, "relay-ok\n", hello.Concat(rep).ToArray()), token);
                fs = new KomFrameStream(cl, KomSessionKey(c.Key, nc, ns));
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
                if (mode == "tunnel") { await KomRelayTunnel(fs, to, token); return; }
                if (mode != "main") return;

                try { cl.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch (Exception) { }   // martwe połączenie z telefonem wykrywa też system
                me = new KomRemoteConn { Fs = fs }; meAt = DateTime.UtcNow;
                if (_komRemote.TryRemove(id, out var old)) try { old.Fs.Dispose(); } catch (Exception) { }
                _komRemote[id] = me;
                _komViaNet[id] = true;
                KomSendFrame(me, KomRelayHere());   // pośrednik jest w sieci
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
                    if (t == "p2p-who") { KomSendFrame(me, KomRelayHere()); KomLanRaw(p, null); foreach (var o in _komRemote) if (o.Key != id) KomSendFrame(o.Value, p); continue; }
                    // do pośrednika (albo do wszystkich) - obsluga tutaj; odpowiedzi ida do urzadzenia przez "to"
                    if (pto == null || pto == _komId) { var pp = p; _ = Dispatcher.BeginInvoke(new Action(() => { try { KomHandle(pp, IPAddress.Loopback); } catch (Exception ex) { App.LogError(ex); } })); }
                    if (pto == _komId) continue;
                    if (pto != null && _komRemote.TryGetValue(pto, out var rc)) { KomSendFrame(rc, p); continue; }   // oba urzadzenia poza domem
                    // do sieci domowej: znany adres albo do wszystkich
                    if (pto != null && KomStoreKeep(p, pto)) continue;   // odbiorca niedostępny - pośrednik przechowuje
                    KomLanRaw(p, pto != null && _komLanAddr.TryGetValue(pto, out var a) ? a : null);
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
                    _komRemote.TryRemove(id, out _);
                    var bye = new JsonObject { ["t"] = "p2p-bye", ["id"] = id, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };   // w domu od razu "poza siecia"
                    KomLanRaw(bye, null);
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
            IPAddress addr = null;
            if (!_komLanAddr.TryGetValue(to, out addr))
            {   // adresu jeszcze nie znamy - pytanie w sieci domowej
                KomLanRaw(KomPacket("p2p-who"), null);
                for (int i = 0; i < 15 && !_komLanAddr.TryGetValue(to, out addr); i++) await Task.Delay(200, token);
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
                var buf = new byte[1 << 16];
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
                    foreach (var cand in relays) { fs = await KomRelayConnect(cand, "main", null, null, CancellationToken.None); if (fs != null) { relay = cand; break; } }   // pierwszy, ktory odpowie
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
                {   // prośba do właściciela (jak zwykła wiadomość: od razu albo przez skrzynkę 📬)
                    var oc = KomContactById(owner); if (oc == null) return;
                    KomDeliver(oc, new KomMsg { Mine = true, Text = KomAskTag + new JsonObject { ["id"] = rid, ["name"] = KomClean((string)h["Name"]), ["pub"] = pub }.ToJsonString(), Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mid = Guid.NewGuid().ToString("N") });
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

        // nowy kod zaproszenia; null - brak pośrednika (opis w tekscie bledu)
        string KomCreateInvite(out string error)
        {
            error = null;
            string host; string relayId; KomContact relay = null;
            if (_komRelayOn && _komRelayHost.Length > 0) { host = _komRelayHost + ":" + _komRelayPort; relayId = _komId; }
            else
            {
                relay = _komContacts.FirstOrDefault(c => !string.IsNullOrEmpty(c.Relay));
                if (relay == null) { error = L.T("Najpierw włącz pośrednika (🎨 → 🌍 Rozmowy poza domem) na komputerze, który jest zawsze włączony, i otwórz tu komunikator w domu."); return null; }
                host = relay.Relay; relayId = relay.Id;
            }
            var token = RandomNumberGenerator.GetBytes(16); var k = SHA256.HashData(token);
            long exp = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeMilliseconds();
            if (relay == null) { _komInvites[KomInviteId(k)] = new KomInvite { K = k, By = _komId, Exp = exp }; KomSave(); }
            else
            {   // pośrednik musi znac zaproszenie - klucz zaszyfrowany kluczem kontaktu z pośrednikiem
                var n = RandomNumberGenerator.GetBytes(12); var ct = new byte[32]; var tag = new byte[16];
                using (var g = new AesGcm(relay.Key, 16)) g.Encrypt(n, k, ct, tag, Encoding.ASCII.GetBytes("velivo-invite\n" + exp));
                var p = KomPacket("p2p-invite"); p["to"] = relay.Id; p["n"] = Convert.ToBase64String(n); p["ek"] = Convert.ToBase64String(ct.Concat(tag).ToArray()); p["exp"] = exp;
                IPAddress.TryParse(relay.Addr ?? "", out var ra);
                for (int i = 0; i < 3; i++) KomSendUdp(p, i == 0 ? ra : null);
            }
            var code = new JsonObject { ["h"] = host, ["r"] = relayId, ["t"] = Convert.ToBase64String(token), ["i"] = _komId, ["n"] = _lanDeviceName }.ToJsonString();
            return "VELIVO1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(code)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
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

        void KomInviteDialog(Window owner)
        {
            var code = KomCreateInvite(out var err);
            if (code == null) { MessageBox.Show(owner, err, L.T("✉ Zaproś przez internet")); return; }
            var box = new TextBox { Text = code, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(0, 8, 0, 8), FontFamily = new System.Windows.Media.FontFamily("Consolas") };
            var copy = new Button { Content = L.T("📋 Kopiuj"), Padding = new Thickness(14, 4, 14, 4), HorizontalAlignment = HorizontalAlignment.Left };
            copy.Click += (s, e) => { try { Clipboard.SetText(code); copy.Content = L.T("✓ Skopiowano"); } catch (Exception) { } };
            var d = new Window { Title = L.T("✉ Zaproś przez internet"), SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel { Margin = new Thickness(16), Children = {
                    new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Text = L.T("Wyślij ten kod osobie, która ma Velivo (SMS, e-mail, komunikator). Ona wkleja go w komunikatorze: ➕ → 🔑 Mam zaproszenie. U Ciebie pojawi się prośba z kodem – przyjmij, gdy kod się zgadza. Zaproszenie działa raz, przez 7 dni.") },
                    box, copy } } };
            KomStyle(d);
            d.ShowDialog();
        }

        void KomJoinDialog(Window owner)
        {
            var box = new TextBox { MinWidth = 420, MaxWidth = 460, Height = 70, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Margin = new Thickness(0, 8, 0, 8) };
            try { if (Clipboard.ContainsText() && Clipboard.GetText().Contains("VELIVO1:")) box.Text = Clipboard.GetText().Trim(); } catch (Exception) { }
            var ok = new Button { Content = L.T("Połącz"), IsDefault = true, Padding = new Thickness(18, 4, 18, 4), HorizontalAlignment = HorizontalAlignment.Right };
            var d = new Window { Title = L.T("🔑 Mam zaproszenie"), SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel { Margin = new Thickness(16), Children = { new TextBlock { Text = L.T("Wklej kod zaproszenia (VELIVO1:…):") }, box, ok } } };
            KomStyle(d);
            ok.Click += (s, e) => d.DialogResult = true;
            if (d.ShowDialog() != true || box.Text.Trim().Length == 0) return;
            var code = box.Text;
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
        bool _komRelayAuto = true;   // adres ustalany automatycznie (pusty albo nie wpisany recznie)
        string _komRelayRouter = "";  // co zrobil router (do okna)
        System.Windows.Threading.DispatcherTimer _komRelayAutoTimer;

        DateTime _komRelayMoved;   // zmiana portu najwyzej raz na godzine (bez przeskakiwania 41920 <-> 41921 w kolko)
        bool _komRelayBusy;
        async Task KomRelayAutoAsync()
        {
            if (!_komRelayOn || _komUp != null || _komRelayBusy) return;
            _komRelayBusy = true;
            try { await KomRelayAutoOnce(); } finally { _komRelayBusy = false; }
        }
        async Task KomRelayAutoOnce()
        {
            bool canMove = DateTime.UtcNow - _komRelayMoved > TimeSpan.FromHours(1);   // poza domem (laptop) - nie ustalamy adresu obcej sieci
            var port = _komRelayPort;
            var (ext, mapped, note) = await Task.Run(() => KomUpnpAsync(port));
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
            if (ext == null && _komRelayAuto)
                try { using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) }) ext = (await http.GetStringAsync("https://api.ipify.org")).Trim(); }
                catch (Exception) { }
            if (ext != null && !IPAddress.TryParse(ext, out _)) ext = null;
            _komRelayRouter = mapped ? L.T("Router: port otwarty automatycznie") : (note == "taken" ? L.T("Router: port już przekierowany (ręcznie)") : L.T("Router nie otworzył portu sam – przekieruj go w routerze na ten komputer"));
            if (_komRelayAuto && ext != null && ext != _komRelayHost) { _komRelayHost = ext; KomSave(); KomAnnounce(); }   // nowy adres - urzadzenia w domu dostaja go od razu
            KomRelayStatus();
        }

        // UPnP (IGD): adres zewnetrzny routera i przekierowanie portu TCP na ten komputer; note: "taken" - port juz zajety (np. recznie)
        static async Task<(string ext, bool mapped, string note)> KomUpnpAsync(int port)
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
                    var (c2, b2) = await Soap("AddPortMapping", "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + port + "</NewExternalPort><NewProtocol>TCP</NewProtocol><NewInternalPort>" + port +
                        "</NewInternalPort><NewInternalClient>" + local + "</NewInternalClient><NewEnabled>1</NewEnabled><NewPortMappingDescription>Velivo</NewPortMappingDescription><NewLeaseDuration>0</NewLeaseDuration>");
                    if (c2 != 200 && b2.Contains("718"))
                    {
                        // juz przekierowany (np. recznie w routerze) - jesli na ten komputer, to dziala
                        var (c3, b3) = await Soap("GetSpecificPortMappingEntry", "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + port + "</NewExternalPort><NewProtocol>TCP</NewProtocol>");
                        var who = c3 == 200 ? System.Text.RegularExpressions.Regex.Match(b3, @"<NewInternalClient>(.*?)</NewInternalClient>").Groups[1].Value.Trim() : "";
                        return (ext, who == local, who == local ? "" : "taken");
                    }
                    return (ext, c2 == 200, "");
                }
            }
            catch (Exception) { return (null, false, ""); }
        }

        // ---------- okno ustawien ----------
        TextBlock _komRelayStatusText;
        void KomRelayStatus()
        {
            if (_komRelayStatusText == null) return;
            _komRelayStatusText.Text = !_komRelayOn ? L.T("Wyłączony") : (_komRelayListener != null ? L.T("Działa – urządzenia poza domem: ") + _komRemote.Count : L.T("Nie działa – sprawdź port")) +
                (_komRelayOn && _komRelayHost.Length > 0 ? "\n" + L.T("Adres domu: ") + _komRelayHost + ":" + _komRelayPort : "") + (_komRelayOn && _komRelayRouter.Length > 0 ? "\n" + _komRelayRouter : "");
        }

        // komputer z kontaktami: rozmowy poza domem wlaczaja sie same (zgoda - polaczenie w domu); adres i port ustawiane same.
        // Wylaczyc mozna w 🎨 → 🌍 (wtedy juz samo sie nie wlacza)
        bool _komRelayAsked;   // uzytkownik sam wylaczyl - nie wlaczamy ponownie
        void KomRelayAskOnce()
        {
            if (_komRelayAsked || _komRelayOn || _komContacts.Count == 0) return;
            _komRelayOn = true; _komRelayAuto = true; KomSave(); KomRelayStart(); KomAnnounce();
        }

        void KomRelayDialog(Window owner)
        {
            var on = new CheckBox { Content = L.T("Ten komputer jest pośrednikiem dla rozmów poza domem (musi być zawsze włączony)"), IsChecked = _komRelayOn, Margin = new Thickness(0, 0, 0, 10) };
            var host = new TextBox { Text = _komRelayAuto ? "" : _komRelayHost, MinWidth = 300, ToolTip = L.T("Puste – Velivo ustali adres domu samo. Możesz wpisać własny, np. z No-IP: mojdom.ddns.net") };
            var port = new TextBox { Text = _komRelayPort.ToString(), Width = 90, ToolTip = L.T("Port TCP przekierowany w routerze na ten komputer") };
            _komRelayStatusText = new TextBlock { Margin = new Thickness(0, 10, 0, 0), FontWeight = FontWeights.SemiBold };
            var info = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(0, 10, 0, 0), Opacity = 0.85,
                Text = L.T("Wystarczy zaznaczyć – Velivo samo ustali adres domu i spróbuje otworzyć port w routerze. Gdy router tego nie umie, przekieruj ten port TCP na ten komputer (jak dla Jellyfin). Pozostałe urządzenia dostaną adres same w sieci domowej. Korzystać mogą tylko Twoje kontakty; wiadomości i pliki są szyfrowane.") };
            var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            var grid = new StackPanel { Margin = new Thickness(16), Children = {
                on,
                new TextBlock { Text = L.T("Adres domu (puste – ustalany automatycznie):") }, host,
                new TextBlock { Text = L.T("Port:"), Margin = new Thickness(0, 8, 0, 0) }, port,
                _komRelayStatusText, info, ok } };
            var d = new Window { Title = L.T("🌍 Rozmowy poza domem"), SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = grid };
            KomStyle(d);
            KomRelayStatus();
            ok.Click += (s, e) => d.DialogResult = true;
            d.Closed += (s, e) => _komRelayStatusText = null;
            if (d.ShowDialog() != true) return;
            if (_komRelayOn && on.IsChecked != true) _komRelayAsked = true;   // wylaczone recznie - zostaje wylaczone
            _komRelayOn = on.IsChecked == true;
            var typed = host.Text.Trim();
            _komRelayAuto = typed.Length == 0;   // puste - adres ustala Velivo (i pilnuje zmian IP)
            if (!_komRelayAuto) _komRelayHost = typed;
            if (int.TryParse(port.Text.Trim(), out var pt) && pt > 0 && pt < 65536) { _komRelayPortFixed = pt != 41920; _komRelayPort = pt; }   // OK w oknie - port zatwierdzony przez właściciela
            KomSave();
            KomRelayStart();
            if (_komRelayOn) { KomAnnounce(); KomInfo(L.T("🌍 Pośrednik włączony – ustalam adres domu i port w routerze…")); }   // urzadzenia w domu od razu dostaja adres pośrednika
        }
    }
}
