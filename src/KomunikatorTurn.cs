using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Velivo
{
    // 📹 Prywatna droga obrazu i dźwięku poza domem: pośrednik (domowy komputer) jest serwerem TURN (RFC 5766) na tym samym
    // porcie co komunikator - bez serwerów firm trzecich. Urządzenie poza domem łączy się po TCP, pośrednik przekazuje
    // zaszyfrowane pakiety rozmowy (DTLS-SRTP - pośrednik nie widzi obrazu ani dźwięku) do urządzeń w sieci domowej
    // i do innych urządzeń poza domem (przez własny adres w sieci domowej). Tylko kontakty pośrednika (hasło z klucza
    // kontaktu) i tylko adresy sieci domowej - nie jest otwartym przekaźnikiem do internetu.
    public partial class MainWindow
    {
        const uint StunCookie = 0x2112A442;
        const string TurnRealm = "velivo";
        static readonly string TurnNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

        // hasło TURN kontaktu - to samo liczy telefon (Komunikator.kt) i komputer poza domem
        static string KomTurnPass(byte[] contactKey) => Convert.ToBase64String(KomHmac(contactKey, "velivo-turn"));

        // początek połączenia na porcie pośrednika: wiadomość STUN (ciasteczko 0x2112A442) - TURN; inaczej - komunikator
        static bool KomIsStun(byte[] b) => b.Length >= 8 && (b[0] & 0xC0) == 0 && b[4] == 0x21 && b[5] == 0x12 && b[6] == 0xA4 && b[7] == 0x42;

        static bool TurnPeerOk(IPAddress a)
        {
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (IPAddress.IsLoopback(a)) return true;
            var b = a.GetAddressBytes(); if (b.Length != 4) return false;
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }

        sealed class TurnMsg
        {
            public int Type; public byte[] Tid; public byte[] Raw;
            public string User, Realm, Nonce; public int MiAt = -1; public byte[] Mi;
            public List<IPEndPoint> Peers = new List<IPEndPoint>(); public byte[] Data; public int Channel = -1; public int Lifetime = -1;
        }

        static IPEndPoint TurnXorAddr(byte[] v, int o, int len)
        {
            if (len < 8 || v[o + 1] != 1) return null;   // tylko IPv4
            int port = ((v[o + 2] << 8) | v[o + 3]) ^ (int)(StunCookie >> 16);
            var ip = new byte[4]; for (int i = 0; i < 4; i++) ip[i] = (byte)(v[o + 4 + i] ^ (byte)(StunCookie >> (24 - 8 * i)));
            return new IPEndPoint(new IPAddress(ip), port);
        }

        static byte[] TurnXorValue(IPEndPoint ep)
        {
            var a = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
            var ip = a.GetAddressBytes(); var v = new byte[8];
            v[1] = 1; int port = ep.Port ^ (int)(StunCookie >> 16); v[2] = (byte)(port >> 8); v[3] = (byte)port;
            for (int i = 0; i < 4; i++) v[4 + i] = (byte)(ip[i] ^ (byte)(StunCookie >> (24 - 8 * i)));
            return v;
        }

        static TurnMsg TurnParse(byte[] m)
        {
            var r = new TurnMsg { Type = (m[0] << 8) | m[1], Tid = m.AsSpan(8, 12).ToArray(), Raw = m };
            int o = 20;
            while (o + 4 <= m.Length)
            {
                int t = (m[o] << 8) | m[o + 1], l = (m[o + 2] << 8) | m[o + 3], v = o + 4;
                if (v + l > m.Length) break;
                switch (t)
                {
                    case 0x0006: r.User = Encoding.UTF8.GetString(m, v, l); break;
                    case 0x0014: r.Realm = Encoding.UTF8.GetString(m, v, l); break;
                    case 0x0015: r.Nonce = Encoding.UTF8.GetString(m, v, l); break;
                    case 0x0008: if (l == 20) { r.MiAt = o; r.Mi = m.AsSpan(v, 20).ToArray(); } break;
                    case 0x0012: var p = TurnXorAddr(m, v, l); if (p != null) r.Peers.Add(p); break;
                    case 0x0013: r.Data = m.AsSpan(v, l).ToArray(); break;
                    case 0x000C: if (l >= 2) r.Channel = (m[v] << 8) | m[v + 1]; break;
                    case 0x000D: if (l >= 4) r.Lifetime = (m[v] << 24) | (m[v + 1] << 16) | (m[v + 2] << 8) | m[v + 3]; break;
                }
                if (r.MiAt >= 0) break;   // po MESSAGE-INTEGRITY liczy się tylko FINGERPRINT
                o = v + ((l + 3) & ~3);
            }
            return r;
        }

        static byte[] TurnBuild(int type, byte[] tid, List<(int t, byte[] v)> attrs, byte[] key)
        {
            var b = new List<byte>(256) { (byte)(type >> 8), (byte)type, 0, 0, 0x21, 0x12, 0xA4, 0x42 };
            b.AddRange(tid);
            foreach (var (t, v) in attrs)
            {
                b.Add((byte)(t >> 8)); b.Add((byte)t); b.Add((byte)(v.Length >> 8)); b.Add((byte)v.Length); b.AddRange(v);
                for (int i = v.Length; i % 4 != 0; i++) b.Add(0);
            }
            if (key != null)
            {   // MESSAGE-INTEGRITY: HMAC-SHA1 z długością obejmującą ten atrybut
                int len = b.Count - 20 + 24; b[2] = (byte)(len >> 8); b[3] = (byte)len;
                byte[] mi; using (var h = new HMACSHA1(key)) mi = h.ComputeHash(b.ToArray());
                b.Add(0x00); b.Add(0x08); b.Add(0x00); b.Add(20); b.AddRange(mi);
            }
            int total = b.Count - 20; b[2] = (byte)(total >> 8); b[3] = (byte)total;
            return b.ToArray();
        }

        static bool TurnCheck(TurnMsg m, byte[] key)
        {
            if (m.MiAt < 0) return false;
            var part = m.Raw.AsSpan(0, m.MiAt).ToArray();
            int len = m.MiAt - 20 + 24; part[2] = (byte)(len >> 8); part[3] = (byte)len;
            using (var h = new HMACSHA1(key)) return CryptographicOperations.FixedTimeEquals(h.ComputeHash(part), m.Mi);
        }

        static byte[] TurnError(int method, byte[] tid, int code, string reason, bool challenge)
        {
            var r = Encoding.UTF8.GetBytes(reason); var v = new byte[4 + r.Length];
            v[2] = (byte)(code / 100); v[3] = (byte)(code % 100); r.CopyTo(v, 4);
            var a = new List<(int, byte[])> { (0x0009, v) };
            if (challenge) { a.Add((0x0014, Encoding.UTF8.GetBytes(TurnRealm))); a.Add((0x0015, Encoding.UTF8.GetBytes(TurnNonce))); }
            return TurnBuild(method | 0x0110, tid, a, null);
        }

        async Task KomTurnServe(TcpClient cl, CancellationToken token)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var s = cl.GetStream(); var wl = new SemaphoreSlim(1, 1);
            var local = ((IPEndPoint)cl.Client.LocalEndPoint).Address; if (local.IsIPv4MappedToIPv6) local = local.MapToIPv4();
            var remote = (IPEndPoint)cl.Client.RemoteEndPoint;
            UdpClient udp = null; byte[] key = null; string user = null; var expires = DateTime.MinValue;
            var perms = new ConcurrentDictionary<string, DateTime>();
            var chanPeer = new ConcurrentDictionary<int, IPEndPoint>(); var peerChan = new ConcurrentDictionary<string, int>();
            async Task Send(byte[] b) { await wl.WaitAsync(cts.Token); try { await s.WriteAsync(b, cts.Token); await s.FlushAsync(cts.Token); } finally { wl.Release(); } }
            bool Allowed(IPAddress a) => perms.TryGetValue((a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToString(), out var e) && e > DateTime.UtcNow;
            void Permit(IPEndPoint p) => perms[p.Address.ToString()] = DateTime.UtcNow.AddMinutes(5);
            try
            {
                var hdr = new byte[4];
                while (!cts.IsCancellationRequested)
                {
                    using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
                    {
                        idle.CancelAfter(TimeSpan.FromMinutes(5));
                        if (!await ReadExactAsync(s, hdr, 4, idle.Token)) break;
                    }
                    if (udp != null && DateTime.UtcNow > expires) break;
                    if ((hdr[0] & 0xC0) == 0x40)
                    {   // ChannelData: kanał, długość, dane (po TCP dopełnione do 4 bajtów)
                        int ch = (hdr[0] << 8) | hdr[1], len = (hdr[2] << 8) | hdr[3];
                        var d = new byte[(len + 3) & ~3]; if (!await ReadExactAsync(s, d, d.Length, cts.Token)) break;
                        if (udp != null && chanPeer.TryGetValue(ch, out var pe)) try { await udp.SendAsync(d, len, pe); } catch (Exception) { }
                        continue;
                    }
                    if ((hdr[0] & 0xC0) != 0) break;
                    int mlen = (hdr[2] << 8) | hdr[3]; if (mlen > 16384) break;
                    var raw = new byte[20 + mlen]; hdr.CopyTo(raw, 0);
                    var rest = new byte[16 + mlen]; if (!await ReadExactAsync(s, rest, rest.Length, cts.Token)) break;
                    rest.CopyTo(raw, 4);
                    if (raw[4] != 0x21 || raw[5] != 0x12 || raw[6] != 0xA4 || raw[7] != 0x42) break;
                    var m = TurnParse(raw);
                    int method = m.Type & 0x3EEF, cls = m.Type & 0x0110;
                    if (cls == 0x0010)
                    {   // Send indication - dane do rozmówcy
                        if (method == 0x006 && udp != null && m.Data != null && m.Peers.Count > 0 && Allowed(m.Peers[0].Address)) try { await udp.SendAsync(m.Data, m.Data.Length, m.Peers[0]); } catch (Exception) { }
                        continue;
                    }
                    if (cls != 0) continue;
                    if (method == 0x001) { await Send(TurnBuild(0x0101, m.Tid, new List<(int, byte[])> { (0x0020, TurnXorValue(remote)) }, null)); continue; }   // Binding
                    // dostęp: nazwa = identyfikator kontaktu, hasło z klucza kontaktu
                    if (m.User == null || m.MiAt < 0) { await Send(TurnError(method, m.Tid, 401, "Unauthorized", true)); continue; }
                    if (m.Nonce != TurnNonce) { await Send(TurnError(method, m.Tid, 438, "Stale Nonce", true)); continue; }
                    if (key == null || m.User != user)
                    {
                        if (user != null) break;   // jedno połączenie - jeden kontakt
                        KomContact c = null; await Dispatcher.InvokeAsync(() => c = KomContactById(m.User));
                        if (c == null) { await Send(TurnError(method, m.Tid, 401, "Unauthorized", true)); continue; }
                        using (var md5 = MD5.Create()) key = md5.ComputeHash(Encoding.UTF8.GetBytes(m.User + ":" + TurnRealm + ":" + KomTurnPass(c.Key)));
                        if (!TurnCheck(m, key)) { key = null; await Send(TurnError(method, m.Tid, 401, "Unauthorized", true)); continue; }
                        user = m.User;
                    }
                    else if (!TurnCheck(m, key)) { await Send(TurnError(method, m.Tid, 401, "Unauthorized", true)); continue; }
                    var ok = new List<(int, byte[])>();
                    if (method == 0x003)
                    {   // Allocate: port UDP na komputerze; adres przekaźnika - w sieci domowej (dla domowników i innych urządzeń poza domem)
                        if (udp != null) { await Send(TurnError(method, m.Tid, 437, "Allocation Mismatch", false)); continue; }
                        udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)); expires = DateTime.UtcNow.AddMinutes(10);
                        var u = udp;
                        _ = Task.Run(async () =>
                        {
                            while (!cts.IsCancellationRequested)
                            {
                                UdpReceiveResult r;
                                try { r = await u.ReceiveAsync(cts.Token); } catch (Exception) { break; }
                                if (!Allowed(r.RemoteEndPoint.Address)) continue;
                                byte[] f;
                                if (peerChan.TryGetValue(r.RemoteEndPoint.ToString(), out var ch))
                                {
                                    f = new byte[4 + ((r.Buffer.Length + 3) & ~3)];
                                    f[0] = (byte)(ch >> 8); f[1] = (byte)ch; f[2] = (byte)(r.Buffer.Length >> 8); f[3] = (byte)r.Buffer.Length; r.Buffer.CopyTo(f, 4);
                                }
                                else f = TurnBuild(0x0017, RandomNumberGenerator.GetBytes(12), new List<(int, byte[])> { (0x0012, TurnXorValue(r.RemoteEndPoint)), (0x0013, r.Buffer) }, null);
                                try { await Send(f); } catch (Exception) { break; }
                            }
                        });
                        ok.Add((0x0016, TurnXorValue(new IPEndPoint(local, ((IPEndPoint)udp.Client.LocalEndPoint).Port))));
                        ok.Add((0x0020, TurnXorValue(remote)));
                        ok.Add((0x000D, new byte[] { 0, 0, 0x02, 0x58 }));   // 600 s
                    }
                    else if (method == 0x004)
                    {   // Refresh
                        if (m.Lifetime == 0) { await Send(TurnBuild(0x0104, m.Tid, new List<(int, byte[])> { (0x000D, new byte[4]) }, key)); break; }
                        expires = DateTime.UtcNow.AddMinutes(10); ok.Add((0x000D, new byte[] { 0, 0, 0x02, 0x58 }));
                    }
                    else if (method == 0x008 || method == 0x009)
                    {   // CreatePermission / ChannelBind - tylko adresy sieci domowej
                        if (udp == null) { await Send(TurnError(method, m.Tid, 437, "Allocation Mismatch", false)); continue; }
                        if (m.Peers.Count == 0 || m.Peers.Exists(p => !TurnPeerOk(p.Address))) { await Send(TurnError(method, m.Tid, 403, "Forbidden", false)); continue; }
                        if (method == 0x009)
                        {
                            if (m.Channel < 0x4000 || m.Channel > 0x7FFE) { await Send(TurnError(method, m.Tid, 400, "Bad Request", false)); continue; }
                            chanPeer[m.Channel] = m.Peers[0]; peerChan[m.Peers[0].ToString()] = m.Channel;
                        }
                        m.Peers.ForEach(Permit);
                    }
                    else { await Send(TurnError(method, m.Tid, 400, "Bad Request", false)); continue; }
                    await Send(TurnBuild(method | 0x0100, m.Tid, ok, key));
                }
            }
            catch (Exception) { }
            finally { try { cts.Cancel(); } catch (Exception) { } udp?.Dispose(); cl.Dispose(); }
        }
    }
}
