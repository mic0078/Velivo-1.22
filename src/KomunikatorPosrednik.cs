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
        async Task<Stream> KomOpenAsync(TcpClient cl, IPAddress addr, string toId, CancellationToken token)
        {
            var up = _komUp; var relay = _komUpRelay;
            if (up != null && relay != null && toId != null)
            {
                var fs = await KomRelayConnect(relay, "tunnel", null, toId, token);
                if (fs != null) return fs;
            }
            await cl.ConnectAsync(addr, LanPort, token);
            return cl.GetStream();
        }

        // pakiet p2p do urzadzenia poza domem (pośrednik) albo przez pośrednika (my poza domem); true - wyslany tunelem
        bool KomRouteRemote(JsonObject p, bool broadcast)
        {
            var up = _komUp;
            if (up != null) { KomSendFrame(up, p); return true; }   // poza domem - wszystko przez pośrednika
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
            _komLanAddr[from] = addr; _komLanSeen[from] = DateTime.UtcNow; _komViaNet.TryRemove(from, out _);
            if (from == _komId || _komRemote.IsEmpty || _komRemote.ContainsKey(from)) return;   // nasze / juz przekazane - bez echa
            var to = (string)p["to"];
            if (to != null) { if (_komRemote.TryGetValue(to, out var rc)) KomSendFrame(rc, p); return; }
            if (t == "p2p-here" || t == "p2p-who" || t == "p2p-bye") foreach (var c in _komRemote.Values) KomSendFrame(c, p);
        }

        void KomLanRaw(JsonNode p, IPAddress to)
        {
            var d = Encoding.UTF8.GetBytes(p.ToJsonString());
            var s = _komUdp ?? _lanTx; if (s == null) return;
            foreach (var t in to != null ? new List<IPAddress> { to } : KomBroadcastTargets()) try { s.Send(d, d.Length, new IPEndPoint(t, LanPort)); } catch (Exception) { }
        }

        // ---------- pośrednik (serwer) ----------
        void KomRelayStart()
        {
            KomRelayStop();
            if (!_komRelayOn) return;
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
            KomFrameStream fs = null; string id = null; KomRemoteConn me = null;
            try
            {
                var s = cl.GetStream(); s.ReadTimeout = 15000;
                byte[] hello, mac;
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(15));
                    hello = await KomReadBlock(s, 4096, cts.Token); mac = await KomReadBlock(s, 64, cts.Token);
                }
                if (hello == null || mac == null) { cl.Dispose(); return; }
                var h = JsonNode.Parse(hello);
                id = (string)h["Id"];
                KomContact c = null; await Dispatcher.InvokeAsync(() => { KomLoad(); c = KomContactById(id ?? ""); });
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

                if (mode == "tunnel-in") { if (_komTunnels.TryRemove(tid, out var w)) { w.TrySetResult(fs); fs = null; } return; }   // strumien odbiera czekajacy tunel
                if (mode == "tunnel") { await KomRelayTunnel(fs, to, token); return; }
                if (mode != "main") return;

                me = new KomRemoteConn { Fs = fs };
                if (_komRemote.TryRemove(id, out var old)) try { old.Fs.Dispose(); } catch (Exception) { }
                _komRemote[id] = me;
                _komViaNet[id] = true;
                KomSendFrame(me, KomRelayHere());   // pośrednik jest w sieci
                await Dispatcher.InvokeAsync(() => { KomRelayStatus(); _komRefresh?.Invoke(); });
                while (!token.IsCancellationRequested)
                {
                    var data = await Task.Run(() => fs.ReadFrame(), token);
                    var p = JsonNode.Parse(data); var t = (string)p?["t"];
                    if (t == "relay-ping") { KomSendFrame(me, new JsonObject { ["t"] = "relay-pong" }); continue; }
                    if (t == null || !t.StartsWith("p2p-", StringComparison.Ordinal) || (string)p["id"] != id) continue;   // tylko pakiety tego urzadzenia
                    var pto = (string)p["to"];
                    if (t == "p2p-who") { KomSendFrame(me, KomRelayHere()); KomLanRaw(p, null); foreach (var o in _komRemote) if (o.Key != id) KomSendFrame(o.Value, p); continue; }
                    // do pośrednika (albo do wszystkich) - obsluga tutaj; odpowiedzi ida do urzadzenia przez "to"
                    if (pto == null || pto == _komId) { var pp = p; _ = Dispatcher.BeginInvoke(new Action(() => { try { KomHandle(pp, IPAddress.Loopback); } catch (Exception ex) { App.LogError(ex); } })); }
                    if (pto == _komId) continue;
                    if (pto != null && _komRemote.TryGetValue(pto, out var rc)) { KomSendFrame(rc, p); continue; }   // oba urzadzenia poza domem
                    // do sieci domowej: znany adres albo do wszystkich
                    KomLanRaw(p, pto != null && _komLanAddr.TryGetValue(pto, out var a) ? a : null);
                    if (pto == null) foreach (var o in _komRemote) if (o.Key != id) KomSendFrame(o.Value, p);
                }
            }
            catch (Exception) { }
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
            IPAddress addr = null;
            if (!_komLanAddr.TryGetValue(to, out addr))
            {   // adresu jeszcze nie znamy - pytanie w sieci domowej
                KomLanRaw(KomPacket("p2p-who"), null);
                for (int i = 0; i < 15 && !_komLanAddr.TryGetValue(to, out addr); i++) await Task.Delay(200, token);
            }
            if (addr == null) return;
            using (var lan = new TcpClient())
            {
                await lan.ConnectAsync(addr, LanPort, token);
                await KomPipe(fs, lan.GetStream(), token);
            }
        }

        // plik dla urzadzenia poza domem: prosba o tunel, potem przekazywanie w obie strony
        async Task KomRelayToRemote(string toId, byte[] headerBytes, Stream src, CancellationToken token)
        {
            if (!_komRemote.TryGetValue(toId, out var rc)) return;
            var tid = Guid.NewGuid().ToString("N");
            var w = new TaskCompletionSource<KomFrameStream>(TaskCreationOptions.RunContinuationsAsynchronously);
            _komTunnels[tid] = w;
            KomSendFrame(rc, new JsonObject { ["t"] = "relay-open", ["tid"] = tid });
            var done = await Task.WhenAny(w.Task, Task.Delay(20000, token));
            _komTunnels.TryRemove(tid, out _);
            if (done != w.Task) return;
            using (var fs = w.Task.Result)
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
            if (DateTime.UtcNow - _komUpSince < TimeSpan.FromSeconds(30)) return;   // po starcie najpierw sprawdzamy sieć domową (pośrednik odpowiada w sekundę)
            var relay = _komContacts.FirstOrDefault(c => !string.IsNullOrEmpty(c.Relay));
            bool home = relay == null || _komRelayOn || (_komLanSeen.TryGetValue(relay.Id, out var seen) && DateTime.UtcNow - seen < TimeSpan.FromSeconds(100));
            if (home) { if (_komUp != null) { try { _komUp.Fs.Dispose(); } catch (Exception) { } _komUp = null; KomAnnounce(); } return; }
            if (_komUp != null || _komUpConnecting) { if (_komUp != null) KomSendFrame(_komUp, new JsonObject { ["t"] = "relay-ping" }); return; }
            _komUpConnecting = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    var fs = await KomRelayConnect(relay, "main", null, null, CancellationToken.None);
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

        // ---------- okno ustawien ----------
        TextBlock _komRelayStatusText;
        void KomRelayStatus()
        {
            if (_komRelayStatusText == null) return;
            _komRelayStatusText.Text = !_komRelayOn ? L.T("Wyłączony") : (_komRelayListener != null ? L.T("Działa – urządzenia poza domem: ") + _komRemote.Count : L.T("Nie działa – sprawdź port"));
        }

        void KomRelayDialog(Window owner)
        {
            var on = new CheckBox { Content = L.T("Ten komputer jest pośrednikiem dla rozmów poza domem (musi być zawsze włączony)"), IsChecked = _komRelayOn, Margin = new Thickness(0, 0, 0, 10) };
            var host = new TextBox { Text = _komRelayHost, MinWidth = 300, ToolTip = L.T("Adres domu w internecie, np. z No-IP: mojdom.ddns.net") };
            var port = new TextBox { Text = _komRelayPort.ToString(), Width = 90, ToolTip = L.T("Port TCP przekierowany w routerze na ten komputer") };
            _komRelayStatusText = new TextBlock { Margin = new Thickness(0, 10, 0, 0), FontWeight = FontWeights.SemiBold };
            var info = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(0, 10, 0, 0), Opacity = 0.85,
                Text = L.T("W routerze przekieruj ten port TCP na ten komputer (jak dla Jellyfin). Pozostałe urządzenia dostaną adres same w sieci domowej – poza domem połączą się przez ten komputer, w domu jak dotąd. Korzystać mogą tylko Twoje kontakty; wiadomości i pliki są szyfrowane.") };
            var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            var grid = new StackPanel { Margin = new Thickness(16), Children = {
                on,
                new TextBlock { Text = L.T("Adres (No-IP / domena):") }, host,
                new TextBlock { Text = L.T("Port:"), Margin = new Thickness(0, 8, 0, 0) }, port,
                _komRelayStatusText, info, ok } };
            var d = new Window { Title = L.T("🌍 Rozmowy poza domem"), SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = grid };
            KomStyle(d);
            KomRelayStatus();
            ok.Click += (s, e) => d.DialogResult = true;
            d.Closed += (s, e) => _komRelayStatusText = null;
            if (d.ShowDialog() != true) return;
            _komRelayOn = on.IsChecked == true;
            _komRelayHost = host.Text.Trim();
            if (int.TryParse(port.Text.Trim(), out var pt) && pt > 0 && pt < 65536) _komRelayPort = pt;
            if (_komRelayOn && _komRelayHost.Length == 0) { _komRelayOn = false; KomInfo(L.T("🌍 Wpisz adres domu (No-IP), żeby włączyć pośrednika")); }
            KomSave();
            KomRelayStart();
            if (_komRelayOn) KomAnnounce();   // urzadzenia w domu od razu dostaja adres pośrednika
        }
    }
}
