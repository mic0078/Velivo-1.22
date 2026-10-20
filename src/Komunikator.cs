using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Velivo
{
    // 💬 Komunikator Velivo w sieci domowej - bez internetu i bez serwera (port LAN 41919), ten sam protokol co Komunikator.kt w telefonie:
    //  * szukanie: UDP "p2p-who" -> "p2p-here",
    //  * kontakt: TCP "p2p-pair" - ECDH P-256, ten sam 6-cyfrowy kod na obu ekranach, zgoda odbiorcy,
    //  * wiadomosci: UDP "p2p-msg" (AES-GCM kluczem kontaktu) + "p2p-ack" (HMAC); niedoreczone wysylane ponownie,
    //  * pliki: TCP "p2p-file" - jednorazowe klucze, kod, zgoda, kawalki 1 MB.
    // Kontakty i rozmowy w pliku zaszyfrowanym DPAPI. Gdy synchronizacja LAN jest wylaczona - wlasne gniazda.
    public partial class MainWindow
    {
        sealed class KomContact { public string Id; public string Name; public byte[] Key; public string Addr; }
        sealed class KomMsg { public bool Mine; public string Text; public long Ts; public bool Ok; public string Mid; }

        static readonly byte[] KomEntropy = Encoding.UTF8.GetBytes("Velivo komunikator v1");
        static string KomFile => Path.Combine(DataDir, "komunikator.dat");
        string _komId;
        readonly List<KomContact> _komContacts = new List<KomContact>();
        readonly Dictionary<string, List<KomMsg>> _komChats = new Dictionary<string, List<KomMsg>>();
        readonly Dictionary<string, int> _komUnread = new Dictionary<string, int>();
        readonly Dictionary<string, (string Name, IPAddress Addr)> _komFound = new Dictionary<string, (string, IPAddress)>();
        readonly Dictionary<string, TaskCompletionSource<bool>> _komAcks = new Dictionary<string, TaskCompletionSource<bool>>();
        readonly HashSet<string> _komSeen = new HashSet<string>();
        readonly HashSet<string> _komInFlight = new HashSet<string>();
        UdpClient _komUdp; TcpListener _komTcp; CancellationTokenSource _komCts;
        System.Windows.Threading.DispatcherTimer _komResend;
        Window _komWindow; string _komOpenId;
        Action _komRefresh;
        bool _komLoaded;

        // ---------- zapis ----------
        void KomLoad()
        {
            if (_komLoaded) return;
            _komLoaded = true;
            try
            {
                if (File.Exists(KomFile))
                {
                    var o = JsonNode.Parse(ProtectedData.Unprotect(File.ReadAllBytes(KomFile), KomEntropy, DataProtectionScope.CurrentUser));
                    _komId = (string)o["id"];
                    foreach (var c in o["contacts"]?.AsArray() ?? new JsonArray())
                    {
                        var k = Convert.FromBase64String((string)c["key"] ?? "");
                        if (k.Length == 32) _komContacts.Add(new KomContact { Id = (string)c["id"], Name = (string)c["name"], Key = k, Addr = (string)c["addr"] ?? "" });
                    }
                    foreach (var kv in o["chats"]?.AsObject() ?? new JsonObject())
                        _komChats[kv.Key] = kv.Value.AsArray().Select(m => new KomMsg { Mine = (bool)m["me"], Text = (string)m["t"], Ts = (long)m["ts"], Ok = (bool?)m["ok"] ?? true, Mid = (string)m["mid"] ?? "" }).ToList();
                    foreach (var kv in o["unread"]?.AsObject() ?? new JsonObject()) _komUnread[kv.Key] = (int)kv.Value;
                }
            }
            catch (Exception ex) { App.LogError(ex); }
            if (string.IsNullOrEmpty(_komId) || _komId.Length != 32) _komId = Guid.NewGuid().ToString("N");
        }

        void KomSave()
        {
            try
            {
                var chats = new JsonObject();
                foreach (var kv in _komChats)
                {
                    var a = new JsonArray();
                    foreach (var m in kv.Value.Skip(Math.Max(0, kv.Value.Count - 500))) a.Add(new JsonObject { ["me"] = m.Mine, ["t"] = m.Text, ["ts"] = m.Ts, ["ok"] = m.Ok, ["mid"] = m.Mid });
                    chats[kv.Key] = a;
                }
                var unread = new JsonObject(); foreach (var kv in _komUnread) unread[kv.Key] = kv.Value;
                var o = new JsonObject
                {
                    ["id"] = _komId,
                    ["contacts"] = new JsonArray(_komContacts.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["name"] = c.Name, ["key"] = Convert.ToBase64String(c.Key), ["addr"] = c.Addr }).ToArray()),
                    ["chats"] = chats, ["unread"] = unread
                };
                Directory.CreateDirectory(DataDir);
                File.WriteAllBytes(KomFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(o.ToJsonString()), KomEntropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        List<KomMsg> KomChat(string id) { if (!_komChats.TryGetValue(id, out var l)) _komChats[id] = l = new List<KomMsg>(); return l; }
        KomContact KomContactById(string id) => _komContacts.FirstOrDefault(c => c.Id == id);

        // ---------- gniazda ----------
        // wlasne gniazda, gdy synchronizacja LAN nie dziala (wtedy pakiety p2p przychodza przez LanListenLoop i odbiornik TCP LAN)
        void KomStartOwn()
        {
            KomLoad();
            KomStartResend();
            if (_komUdp != null || _lanRx != null) return;
            try
            {
                _komCts = new CancellationTokenSource();
                var u = new UdpClient(AddressFamily.InterNetwork);
                u.Client.ExclusiveAddressUse = false;
                u.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                u.Client.Bind(new IPEndPoint(IPAddress.Any, LanPort));
                u.EnableBroadcast = true;
                _komUdp = u;
                var token = _komCts.Token;
                _ = Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        try { var r = await u.ReceiveAsync(token); if (r.Buffer.Length < 60000) KomUdp(r.Buffer, r.RemoteEndPoint); }
                        catch (OperationCanceledException) { break; }
                        catch (ObjectDisposedException) { break; }
                        catch (Exception) { }
                    }
                });
                var t = new TcpListener(IPAddress.Any, LanPort);
                t.Start(); _komTcp = t;
                _ = Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            var c = await t.AcceptTcpClientAsync(token);
                            _ = Task.Run(async () =>
                            {
                                using (c)
                                {
                                    var s = c.GetStream(); var lb = new byte[4];
                                    if (!await ReadExactAsync(s, lb, 4, token)) return;
                                    int hl = BitConverter.ToInt32(lb, 0); if (hl <= 0 || hl > 4096) return;
                                    var hb = new byte[hl]; if (!await ReadExactAsync(s, hb, hl, token)) return;
                                    await KomTcpAsync(hb, s, c, token);
                                }
                            });
                        }
                        catch (OperationCanceledException) { break; }
                        catch (ObjectDisposedException) { break; }
                        catch (Exception) { }
                    }
                });
                if (_komContacts.Count > 0) KomAnnounce();
            }
            catch (Exception ex) { App.LogError(ex); KomStopOwn(); }
        }

        void KomStopOwn()
        {
            try { _komCts?.Cancel(); } catch (Exception) { }
            try { _komUdp?.Dispose(); } catch (Exception) { }
            try { _komTcp?.Stop(); } catch (Exception) { }
            _komUdp = null; _komTcp = null; _komCts = null;
        }

        void KomSendUdp(JsonObject p, IPAddress to)
        {
            var d = Encoding.UTF8.GetBytes(p.ToJsonString());
            var s = _komUdp ?? _lanTx;
            if (s == null) return;
            var targets = to != null ? new List<IPAddress> { to } : KomBroadcastTargets();
            foreach (var t in targets) try { s.Send(d, d.Length, new IPEndPoint(t, LanPort)); } catch (Exception) { }
        }

        static List<IPAddress> KomBroadcastTargets()
        {
            var l = new List<IPAddress> { IPAddress.Broadcast };
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        var a = ua.Address.GetAddressBytes(); var m = ua.IPv4Mask.GetAddressBytes();
                        var b = new byte[4]; for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
                        var ip = new IPAddress(b); if (!l.Contains(ip)) l.Add(ip);
                    }
                }
            }
            catch (Exception) { }
            return l;
        }

        JsonObject KomPacket(string t) => new JsonObject { ["t"] = t, ["id"] = _komId, ["device"] = _lanDeviceName, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["kind"] = "pc" };
        static string KomClean(string s) => new string((s ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim() is var x && x.Length > 64 ? x.Substring(0, 64) : new string((s ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        static byte[] KomHmac(byte[] key, string s) { using (var h = new HMACSHA256(key)) return h.ComputeHash(Encoding.UTF8.GetBytes(s)); }
        static byte[] KomB64(string s) { try { return Convert.FromBase64String(s ?? ""); } catch (FormatException) { return Array.Empty<byte>(); } }

        void KomAnnounce()
        {
            _ = Task.Run(async () => { for (int i = 0; i < 3; i++) { KomSendUdp(KomPacket("p2p-here"), null); await Task.Delay(1500); } });
            Dispatcher.BeginInvoke(new Action(() => { foreach (var c in _komContacts.ToList()) KomResendPending(c); }));
        }

        // pakiet UDP z sieci; true - to byl pakiet komunikatora (wywolywane z watku sieci)
        internal bool KomUdp(byte[] buf, IPEndPoint remote)
        {
            if (buf.Length < 8 || Encoding.UTF8.GetString(buf, 0, Math.Min(buf.Length, 200)).IndexOf("\"p2p-", StringComparison.Ordinal) < 0) return false;
            JsonNode p; try { p = JsonNode.Parse(buf); } catch (Exception) { return false; }
            var t = (string)p?["t"]; if (t == null || !t.StartsWith("p2p-", StringComparison.Ordinal)) return false;
            Dispatcher.BeginInvoke(new Action(() => { try { KomHandle(p, remote.Address); } catch (Exception ex) { App.LogError(ex); } }));
            return true;
        }

        void KomHandle(JsonNode p, IPAddress sender)
        {
            KomLoad();
            string t = (string)p["t"], from = (string)p["id"];
            if (from == null || from == _komId || from.Length != 32) return;
            long ts = (long?)p["ts"] ?? 0;
            if (Math.Abs(ts - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) > 600000) return;
            var c = KomContactById(from);
            if (c != null)
            {
                bool was = KomOnline(c.Id); _komLastSeen[c.Id] = DateTime.UtcNow;
                if (t == "p2p-here" && c.Addr != sender.ToString()) { c.Addr = sender.ToString(); KomSave(); }   // aktualny adres - pliki bez ponownego szukania
                if (!was) _komRefresh?.Invoke();
            }
            switch (t)
            {
                case "p2p-who": KomSendUdp(KomPacket("p2p-here"), sender); break;
                case "p2p-here": _komFound[from] = (KomClean((string)p["device"]), sender); break;
                case "p2p-ack":
                    {
                        if (c == null) return;
                        var mid = (string)p["mid"] ?? "";
                        var mac = KomB64((string)p["mac"]); var exp = KomHmac(c.Key, "ack\n" + mid);
                        if (mac.Length != exp.Length || !CryptographicOperations.FixedTimeEquals(mac, exp)) return;
                        if (c.Addr != sender.ToString()) { c.Addr = sender.ToString(); KomSave(); }
                        if (_komAcks.TryGetValue(mid, out var tcs)) tcs.TrySetResult(true);
                        return;
                    }
                case "p2p-msg": KomReceive(p, c, sender, ts); return;
            }
            if (c != null) KomResendPending(c);   // odezwal sie - dosylamy niedoreczone
        }

        static byte[] KomMsgAd(string from, string to, string mid, long ts) => Encoding.UTF8.GetBytes("p2p-msg\n" + from + "\n" + to + "\n" + mid + "\n" + ts.ToString(CultureInfo.InvariantCulture));

        void KomReceive(JsonNode p, KomContact c, IPAddress sender, long ts)
        {
            if (c == null || (string)p["to"] != _komId) return;
            var mid = (string)p["mid"] ?? ""; if (mid.Length != 32) return;
            var n = KomB64((string)p["n"]); var ct = KomB64((string)p["c"]);
            if (n.Length != 12 || ct.Length < 16) return;
            string text;
            try
            {
                var plain = new byte[ct.Length - 16];
                using (var g = new AesGcm(c.Key, 16)) g.Decrypt(n, ct.AsSpan(0, ct.Length - 16), ct.AsSpan(ct.Length - 16), plain, KomMsgAd(c.Id, _komId, mid, ts));
                text = Encoding.UTF8.GetString(plain);
            }
            catch (CryptographicException) { return; }
            var ack = KomPacket("p2p-ack"); ack["mid"] = mid; ack["mac"] = Convert.ToBase64String(KomHmac(c.Key, "ack\n" + mid));
            KomSendUdp(ack, sender);
            if (!_komSeen.Add(mid)) return;   // powtorka - tylko ponowne potwierdzenie
            if (c.Addr != sender.ToString()) c.Addr = sender.ToString();
            if (text.Length > 4000) text = text.Substring(0, 4000);
            KomChat(c.Id).Add(new KomMsg { Mine = false, Text = text, Ts = ts, Ok = true, Mid = mid });
            bool visible = _komWindow != null && _komWindow.IsActive && _komOpenId == c.Id;
            if (!visible) _komUnread[c.Id] = (_komUnread.TryGetValue(c.Id, out var u) ? u : 0) + 1;
            KomSave();
            _komRefresh?.Invoke();
            if (!visible) KomNotify(c, text);
            KomResendPending(c);
        }

        // powiadomienie jak w innych komunikatorach: okienko Velivo w prawym dolnym rogu, nad innymi programami
        // (takze gdy Velivo jest zminimalizowane lub w zasobniku): nadawca, tresc, szybka odpowiedz; klikniecie - rozmowa
        readonly List<Window> _komPopups = new List<Window>();
        void KomNotify(KomContact c, string text)
        {
            try { System.Media.SystemSounds.Asterisk.Play(); } catch (Exception) { }
            try { if (_komWindow != null && !_komWindow.IsActive) FlashWindowEx(_komWindow); } catch (Exception) { }
            // ta sama osoba - jedno okienko z nowa trescia
            var old = _komPopups.FirstOrDefault(p => (string)p.Tag == c.Id);
            if (old != null) { old.Close(); }
            bool dark = DarkWindows;
            var bg = new SolidColorBrush(dark ? Color.FromRgb(0x1B, 0x17, 0x33) : Color.FromRgb(0xFF, 0xFF, 0xFF));
            var fg = new SolidColorBrush(dark ? Color.FromRgb(0xEC, 0xE8, 0xFF) : Color.FromRgb(0x1B, 0x17, 0x33));
            var accent = new SolidColorBrush(Color.FromRgb(0x6D, 0x3F, 0xDB));
            var w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
                Topmost = true, ShowInTaskbar = false, ShowActivated = false, Width = 340, SizeToContent = SizeToContent.Height, Tag = c.Id, Title = "Velivo"
            };
            var shortText = text.Length > 300 ? text.Substring(0, 300) + "…" : text;
            var title = new TextBlock { Text = "💬 " + c.Name, FontWeight = FontWeights.SemiBold, FontSize = 15, Foreground = fg, TextTrimming = TextTrimming.CharacterEllipsis };
            var close = new Button { Content = "✕", Width = 26, Height = 26, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = fg, ToolTip = L.T("Zamknij"), Cursor = Cursors.Hand };
            var head = new DockPanel(); DockPanel.SetDock(close, Dock.Right); head.Children.Add(close); head.Children.Add(title);
            var body = new TextBlock { Text = shortText, TextWrapping = TextWrapping.Wrap, Foreground = fg, FontSize = 14, Margin = new Thickness(0, 6, 0, 8), MaxHeight = 140, TextTrimming = TextTrimming.CharacterEllipsis, Cursor = Cursors.Hand, ToolTip = L.T("Otwórz rozmowę") };
            var reply = new TextBox { MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(dark ? Color.FromRgb(0x24, 0x1F, 0x45) : Color.FromRgb(0xF3, 0xF1, 0xFB)), Foreground = fg, BorderBrush = accent, CaretBrush = fg, ToolTip = L.T("Odpowiedz (Enter - wyślij)") };
            var send = new Button { Content = "➤", Width = 36, Margin = new Thickness(6, 0, 0, 0), Background = accent, Foreground = Brushes.White, BorderThickness = new Thickness(0), ToolTip = L.T("Wyślij"), Cursor = Cursors.Hand };
            var row = new DockPanel(); DockPanel.SetDock(send, Dock.Right); row.Children.Add(send); row.Children.Add(reply);
            var stack = new StackPanel { Children = { head, body, row } };
            w.Content = new Border
            {
                Background = bg, BorderBrush = accent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10, 10, 12), Margin = new Thickness(8),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, Opacity = 0.35, ShadowDepth = 2 }, Child = stack
            };
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            timer.Tick += (s, e) => { if (!w.IsMouseOver && !reply.IsKeyboardFocusWithin && reply.Text.Length == 0) w.Close(); };
            void Open() { w.Close(); ShowMessenger(c.Id); }
            body.MouseLeftButtonUp += (s, e) => Open();
            title.MouseLeftButtonUp += (s, e) => Open();
            close.Click += (s, e) => w.Close();
            void Reply()
            {
                var t = reply.Text.Trim(); if (t.Length == 0) return;
                KomSend(c, t); _komUnread.Remove(c.Id); KomSave(); _komRefresh?.Invoke(); w.Close();
            }
            send.Click += (s, e) => Reply();
            reply.PreviewKeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; Reply(); } else if (e.Key == Key.Escape) w.Close(); };
            reply.GotKeyboardFocus += (s, e) => timer.Stop();
            w.Closed += (s, e) => { timer.Stop(); _komPopups.Remove(w); KomStackPopups(); };
            w.SourceInitialized += (s, e) => KomStackPopups();
            _komPopups.Add(w);
            w.Show();
            KomStackPopups();
            timer.Start();
        }

        // okienka jedno nad drugim w prawym dolnym rogu ekranu (nad paskiem zadan), najwyzej 4
        void KomStackPopups()
        {
            while (_komPopups.Count > 4) _komPopups[0].Close();
            var area = SystemParameters.WorkArea; double bottom = area.Bottom - 4;
            foreach (var p in _komPopups.AsEnumerable().Reverse())
            {
                p.UpdateLayout();
                double h = p.ActualHeight > 0 ? p.ActualHeight : 150;
                p.Left = area.Right - p.Width - 4; p.Top = bottom - h; bottom -= h;
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct FlashInfo { public uint Size; public IntPtr Hwnd; public uint Flags; public uint Count; public uint Timeout; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool FlashWindowEx(ref FlashInfo fi);
        static void FlashWindowEx(Window w)
        {
            var fi = new FlashInfo { Hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle, Flags = 3 | 12, Count = 3 };
            fi.Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(fi);
            FlashWindowEx(ref fi);
        }

        void KomSend(KomContact c, string text)
        {
            var m = new KomMsg { Mine = true, Text = text.Length > 4000 ? text.Substring(0, 4000) : text, Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mid = Guid.NewGuid().ToString("N") };
            KomChat(c.Id).Add(m); KomSave();
            KomDeliver(c, m);
        }

        void KomResendPending(KomContact c)
        {
            long week = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 7L * 86400000;
            foreach (var m in KomChat(c.Id).Where(x => x.Mine && !x.Ok && x.Ts > week).ToList()) KomDeliver(c, m);
        }

        // najpierw na znany adres, potem do calej sieci; 4 proby co 1,5 s; potem co minute (zegar) i gdy kontakt sie odezwie
        async void KomDeliver(KomContact c, KomMsg m)
        {
            if (!_komInFlight.Add(m.Mid)) return;
            try
            {
                var p = KomPacket("p2p-msg"); p["ts"] = m.Ts; p["to"] = c.Id; p["mid"] = m.Mid;
                var n = RandomNumberGenerator.GetBytes(12); p["n"] = Convert.ToBase64String(n);
                var plain = Encoding.UTF8.GetBytes(m.Text); var ct = new byte[plain.Length]; var tag = new byte[16];
                using (var g = new AesGcm(c.Key, 16)) g.Encrypt(n, plain, ct, tag, KomMsgAd(_komId, c.Id, m.Mid, m.Ts));
                p["c"] = Convert.ToBase64String(ct.Concat(tag).ToArray());
                var tcs = new TaskCompletionSource<bool>(); _komAcks[m.Mid] = tcs;
                IPAddress.TryParse(c.Addr ?? "", out var known);
                for (int i = 0; i < 4 && !m.Ok; i++)
                {
                    var pp = p; var to = i % 2 == 0 ? known : null;
                    await Task.Run(() => KomSendUdp(pp, to));
                    if (await Task.WhenAny(tcs.Task, Task.Delay(1500)) == tcs.Task) m.Ok = true;
                }
                _komAcks.Remove(m.Mid);
                if (m.Ok) KomSave();
                _komRefresh?.Invoke();
            }
            catch (Exception ex) { App.LogError(ex); }
            finally { _komInFlight.Remove(m.Mid); }
        }

        // 🟢 w sieci: kontakt odezwal sie w ostatnich 2,5 min (kazde Velivo z komunikatorem oglasza sie co minute)
        readonly Dictionary<string, DateTime> _komLastSeen = new Dictionary<string, DateTime>();
        bool KomOnline(string id) => _komLastSeen.TryGetValue(id, out var t) && DateTime.UtcNow - t < TimeSpan.FromSeconds(150);

        void KomStartResend()
        {
            if (_komResend != null) return;
            _komResend = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _komResend.Tick += (s, e) =>
            {
                if (_komContacts.Count == 0) return;
                KomSendUdp(KomPacket("p2p-here"), null);
                foreach (var c in _komContacts.ToList()) KomResendPending(c);
                _komRefresh?.Invoke();   // kropki w sieci / poza siecia
            };
            _komResend.Start();
            // powrot do domowej sieci (np. laptop) - ogloszenie, kontakty dosylaja zalegle wiadomosci
            NetworkChange.NetworkAddressChanged += (s, e) => Dispatcher.BeginInvoke(new Action(() => { if (_komContacts.Count > 0) KomAnnounce(); }));
        }

        async Task<List<(string Id, string Name, IPAddress Addr)>> KomFind()
        {
            _komFound.Clear();
            for (int i = 0; i < 3; i++) { KomSendUdp(KomPacket("p2p-who"), null); await Task.Delay(800); }
            return _komFound.Select(kv => (kv.Key, kv.Value.Name, kv.Value.Addr)).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // ---------- TCP: kontakt i pliki ----------
        static byte[] KomAgree(ECDiffieHellman me, byte[] other, string label, byte[] pubA, byte[] pubB)
        {
            using (var o = ECDiffieHellman.Create())
            {
                o.ImportSubjectPublicKeyInfo(other, out _);
                var secret = me.DeriveRawSecretAgreement(o.PublicKey);
                return SHA256.HashData(secret.Concat(Encoding.ASCII.GetBytes(label)).Concat(pubA).Concat(pubB).ToArray());
            }
        }
        static string KomCode(byte[] key)
        {
            var d = SHA256.HashData(key.Concat(Encoding.ASCII.GetBytes("kod")).ToArray());
            long n = (((long)d[0] << 24) | ((long)d[1] << 16) | ((long)d[2] << 8) | d[3]) % 1000000;
            return (n / 1000).ToString("000", CultureInfo.InvariantCulture) + " " + (n % 1000).ToString("000", CultureInfo.InvariantCulture);
        }
        static async Task KomWriteBlock(Stream s, byte[] b, CancellationToken t) { await s.WriteAsync(BitConverter.GetBytes(b.Length), 0, 4, t); await s.WriteAsync(b, 0, b.Length, t); await s.FlushAsync(t); }
        static async Task<byte[]> KomReadBlock(Stream s, int max, CancellationToken t)
        {
            var lb = new byte[4]; if (!await ReadExactAsync(s, lb, 4, t)) return null;
            int l = BitConverter.ToInt32(lb, 0); if (l <= 0 || l > max) return null;
            var b = new byte[l]; return await ReadExactAsync(s, b, l, t) ? b : null;
        }

        // naglowek TCP z odbiornika LAN albo wlasnego; true - obsluzony tutaj
        internal async Task<bool> KomTcpAsync(byte[] headerBytes, NetworkStream stream, TcpClient client, CancellationToken token)
        {
            JsonNode h; try { h = JsonNode.Parse(headerBytes); } catch (Exception) { return false; }
            var type = (string)h?["Type"];
            if (type != "p2p-pair" && type != "p2p-file") return false;
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            try { if (type == "p2p-pair") await KomReceivePair(h, stream, remote, token); else await KomReceiveFile(h, stream, token); }
            catch (Exception ex) { if (!token.IsCancellationRequested) App.LogError(ex); }
            return true;
        }

        async Task<bool> KomAsk(string title, string text)
        {
            return await Dispatcher.InvokeAsync(() => MessageBox.Show((Window)_komWindow ?? PairDialogOwner(), text, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
        }

        async Task KomReceivePair(JsonNode h, NetworkStream s, IPAddress remote, CancellationToken token)
        {
            await Dispatcher.InvokeAsync(KomLoad);
            var other = (string)h["Id"] ?? ""; if (other.Length != 32 || other == _komId) return;
            var pubA = KomB64((string)h["Pub"]); if (pubA.Length == 0 || pubA.Length > 200) return;
            var dev = KomClean((string)h["Device"]);
            using (var me = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
            {
                var pubB = me.ExportSubjectPublicKeyInfo();
                await KomWriteBlock(s, pubB, token);
                var key = KomAgree(me, pubA, "velivo-czat", pubA, pubB);
                var ok = await KomAsk(L.T("💬 Nowy kontakt"), (L.En ? "“" + dev + "” wants to add you to Velivo messenger.\n\nCode: " : "„" + dev + "” chce dodać Cię do komunikatora Velivo.\n\nKod: ") + KomCode(key) +
                    L.T("\n\nPrzyjmij tylko, gdy na drugim urządzeniu jest ten sam kod."));
                await s.WriteAsync(new[] { (byte)(ok ? 1 : 0) }, 0, 1, token); await s.FlushAsync(token);
                if (!ok) return;
                await KomWriteBlock(s, Encoding.UTF8.GetBytes(new JsonObject { ["Id"] = _komId, ["Device"] = _lanDeviceName }.ToJsonString()), token);
                await Dispatcher.InvokeAsync(() => KomAddContact(new KomContact { Id = other, Name = dev, Key = key, Addr = remote?.ToString() ?? "" }));
            }
        }

        void KomAddContact(KomContact c)
        {
            _komContacts.RemoveAll(x => x.Id == c.Id); _komContacts.Add(c); KomSave(); _komRefresh?.Invoke();
        }

        async Task<string> KomPair(string id, string name, IPAddress addr, Action<string> showCode)
        {
            try
            {
                using (var cl = new TcpClient())
                using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4)))
                using (var me = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
                {
                    await cl.ConnectAsync(addr, LanPort, cts.Token);
                    var s = cl.GetStream(); var pubA = me.ExportSubjectPublicKeyInfo();
                    await KomWriteBlock(s, Encoding.UTF8.GetBytes(new JsonObject { ["Type"] = "p2p-pair", ["Id"] = _komId, ["Device"] = _lanDeviceName, ["Pub"] = Convert.ToBase64String(pubA) }.ToJsonString()), cts.Token);
                    var pubB = await KomReadBlock(s, 200, cts.Token); if (pubB == null) return "błąd";
                    var key = KomAgree(me, pubB, "velivo-czat", pubA, pubB);
                    showCode(KomCode(key));
                    var b = new byte[1]; if (!await ReadExactAsync(s, b, 1, cts.Token) || b[0] != 1) return L.T("odrzucono");
                    var info = await KomReadBlock(s, 4096, cts.Token); if (info == null) return "błąd";
                    var o = JsonNode.Parse(info); if ((string)o["Id"] != id) return "błąd";
                    var dev = KomClean((string)o["Device"]);
                    KomAddContact(new KomContact { Id = id, Name = dev.Length > 0 ? dev : name, Key = key, Addr = addr.ToString() });
                    return "ok";
                }
            }
            catch (Exception ex) { return ex.Message; }
        }

        static byte[] KomFileAd(JsonNode h) => Encoding.UTF8.GetBytes(string.Join("\n", (string)h["Type"] ?? "", (string)h["Id"] ?? "", (string)h["Device"] ?? "", (string)h["Hash"] ?? "",
            ((long?)h["Size"] ?? 0).ToString(CultureInfo.InvariantCulture), (string)h["Name"] ?? "", (string)h["Pub"] ?? "", (string)h["Nonce"] ?? ""));

        static string KomSize(long size) => size >= 1073741824 ? (size / 1073741824.0).ToString("0.00", CultureInfo.CurrentCulture) + " GB" : size >= 1048576 ? (size / 1048576.0).ToString("0.0", CultureInfo.CurrentCulture) + " MB" : Math.Max(1, size / 1024) + " KB";

        async Task KomReceiveFile(JsonNode h, NetworkStream s, CancellationToken token)
        {
            long size = (long?)h["Size"] ?? -1; var id = (string)h["Id"] ?? "";
            if (size < 0 || size > LanFile2Limit || id.Length != 32 || id == _komId) return;
            var pubA = KomB64((string)h["Pub"]); var baseNonce = KomB64((string)h["Nonce"]);
            if (pubA.Length == 0 || pubA.Length > 200 || baseNonce.Length != 12) return;
            var name = SafeFileName((string)h["Name"]); var dev = KomClean((string)h["Device"]);
            byte[] key;
            using (var me = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
            {
                var pubB = me.ExportSubjectPublicKeyInfo();
                await KomWriteBlock(s, pubB, token);
                key = KomAgree(me, pubA, "velivo-p2p", pubA, pubB);
            }
            string target = null; bool accept = false;
            var ad = KomFileAd(h);
            // od kontaktu (podpis kluczem kontaktu) - bez kodu, jak zalacznik w rozmowie; zgoda zawsze
            KomContact from = null;
            await Dispatcher.InvokeAsync(() =>
            {
                var c = KomContactById(id); var mac = KomB64((string)h["Mac"]);
                if (c != null) using (var hm = new HMACSHA256(c.Key)) { var exp = hm.ComputeHash(ad); if (mac.Length == 32 && CryptographicOperations.FixedTimeEquals(mac, exp)) from = c; }
                if (from != null) dev = from.Name;
                var text = dev + (L.En ? " is sending a file:\n\n" : " wysyła plik:\n\n") + name + "\n" + KomSize(size) +
                    (from != null ? "" : (L.En ? "\n\nCode: " : "\n\nKod: ") + KomCode(key) + L.T("\n\nPrzyjmij tylko, gdy telefon nadawcy pokazuje ten sam kod."));
                accept = MessageBox.Show((Window)_komWindow ?? PairDialogOwner(), text, L.En ? "Velivo – file" : "Velivo – plik", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                if (!accept) return;
                var def = FreeDownloadPath(name);
                var dlg = new Microsoft.Win32.SaveFileDialog { Title = (L.En ? "Save file from " : "Zapisz plik od: ") + dev, FileName = Path.GetFileName(def), InitialDirectory = Path.GetDirectoryName(def),
                    Filter = (L.En ? "All files" : "Wszystkie pliki") + "|*.*", OverwritePrompt = true };
                if (dlg.ShowDialog((Window)_komWindow ?? PairDialogOwner()) == true) target = dlg.FileName; else accept = false;
            });
            await s.WriteAsync(new[] { (byte)(accept ? 1 : 0) }, 0, 1, token); await s.FlushAsync(token);
            if (!accept) return;
            string tmp = target + ".velivo-part";
            try
            {
                using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                using (var gcm = new AesGcm(key, 16))
                {
                    using (var fo = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    {
                        int n = ChunkCount(size); long got = 0; var lb = new byte[4]; var buf = new byte[LanChunk + 16]; var plain = new byte[LanChunk];
                        for (int i = 0; i < n; i++)
                        {
                            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
                            {
                                idle.CancelAfter(TimeSpan.FromSeconds(60));
                                if (!await ReadExactAsync(s, lb, 4, idle.Token)) return;
                                int l = BitConverter.ToInt32(lb, 0); if (l < 16 || l > LanChunk + 16) return;
                                if (!await ReadExactAsync(s, buf, l, idle.Token)) return;
                                gcm.Decrypt(ChunkNonce(baseNonce, i), buf.AsSpan(0, l - 16), buf.AsSpan(l - 16, 16), plain.AsSpan(0, l - 16), ChunkAad(ad, i, i == n - 1));
                                got += l - 16; if (got > size) return;
                                sha.AppendData(plain, 0, l - 16);
                                await fo.WriteAsync(plain.AsMemory(0, l - 16), token);
                            }
                        }
                        if (got != size) return;
                    }
                    if (!string.Equals(Convert.ToHexString(sha.GetHashAndReset()), (string)h["Hash"], StringComparison.OrdinalIgnoreCase)) return;
                }
                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target); tmp = null;
                var ack = KomHmac(key, "file-ack\n" + (string)h["Hash"]);
                await s.WriteAsync(ack, 0, ack.Length, token);
                await Dispatcher.InvokeAsync(() => { ShowToast((L.En ? "📎 File from " : "📎 Plik od ") + dev + ": " + Path.GetFileName(target), target); if (from != null) KomNote(from, false, "📎 " + Path.GetFileName(target)); });
            }
            finally { if (tmp != null) try { File.Delete(tmp); } catch (Exception) { } CryptographicOperations.ZeroMemory(key); }
        }

        void KomNote(KomContact c, bool mine, string text)
        {
            KomChat(c.Id).Add(new KomMsg { Mine = mine, Text = text, Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Ok = true, Mid = Guid.NewGuid().ToString("N") });
            KomSave(); _komRefresh?.Invoke();
        }

        async Task<string> KomSendFile(IPAddress addr, string path, Action<string> showCode, KomContact to = null)
        {
            var name = SafeFileName(Path.GetFileName(path));
            try
            {
                return await Task.Run(async () =>
                {
                    var size = new FileInfo(path).Length;
                    string hash; using (var f = File.OpenRead(path)) hash = Convert.ToHexString(await SHA256.HashDataAsync(f));
                    using (var me = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
                    using (var cl = new TcpClient())
                    using (var cts = new CancellationTokenSource())
                    {
                        var pubA = me.ExportSubjectPublicKeyInfo(); var baseNonce = RandomNumberGenerator.GetBytes(12);
                        var h = new JsonObject { ["Type"] = "p2p-file", ["Id"] = _komId, ["Device"] = _lanDeviceName, ["Hash"] = hash, ["Size"] = size, ["Name"] = name, ["Pub"] = Convert.ToBase64String(pubA), ["Nonce"] = Convert.ToBase64String(baseNonce) };
                        var ad = KomFileAd(h);
                        if (to != null) using (var hm = new HMACSHA256(to.Key)) h["Mac"] = Convert.ToBase64String(hm.ComputeHash(ad));   // kontakt rozpozna nadawce bez kodu
                        cts.CancelAfter(TimeSpan.FromSeconds(10));
                        await cl.ConnectAsync(addr, LanPort, cts.Token);
                        var s = cl.GetStream();
                        await KomWriteBlock(s, Encoding.UTF8.GetBytes(h.ToJsonString()), cts.Token);
                        var pubB = await KomReadBlock(s, 200, cts.Token); if (pubB == null) return "błąd";
                        var key = KomAgree(me, pubB, "velivo-p2p", pubA, pubB);
                        if (to == null) showCode(KomCode(key));
                        cts.CancelAfter(TimeSpan.FromMinutes(4));
                        var b = new byte[1]; if (!await ReadExactAsync(s, b, 1, cts.Token) || b[0] != 1) return L.T("odrzucono");
                        cts.CancelAfter(Timeout.InfiniteTimeSpan);
                        int n = ChunkCount(size); long sent = 0; var buf = new byte[LanChunk]; var ct = new byte[LanChunk]; var tag = new byte[16];
                        using (var gcm = new AesGcm(key, 16))
                        using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                        {
                            for (int i = 0; i < n; i++)
                            {
                                int len = 0; while (len < LanChunk) { int r = await f.ReadAsync(buf, len, LanChunk - len); if (r <= 0) break; len += r; }
                                sent += len;
                                gcm.Encrypt(ChunkNonce(baseNonce, i), buf.AsSpan(0, len), ct.AsSpan(0, len), tag, ChunkAad(ad, i, i == n - 1));
                                await s.WriteAsync(BitConverter.GetBytes(len + 16), 0, 4); await s.WriteAsync(ct, 0, len); await s.WriteAsync(tag, 0, 16);
                            }
                        }
                        if (sent != size) return L.T("plik zmienił się w trakcie");
                        var ack = new byte[32];
                        using (var t2 = new CancellationTokenSource(TimeSpan.FromMinutes(2))) if (!await ReadExactAsync(s, ack, 32, t2.Token)) return "błąd";
                        return CryptographicOperations.FixedTimeEquals(ack, KomHmac(key, "file-ack\n" + hash)) ? "ok" : "błąd";
                    }
                });
            }
            catch (Exception ex) { return ex.Message; }
        }

        // ---------- okno ----------
        void ShowMessenger(string openId = null)
        {
            KomLoad();
            if (_lanRx == null) KomStartOwn();
            if (_komWindow != null) { _komWindow.Activate(); if (_komWindow.WindowState == WindowState.Minimized) _komWindow.WindowState = WindowState.Normal; if (openId != null) _komSelect?.Invoke(openId); return; }
            var w = new Window { Title = L.T("Komunikator Velivo"), Width = 760, Height = 560, MinWidth = 480, MinHeight = 320, WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = true };
            if (IsVisible) w.Owner = null;
            var root = new Grid { Margin = new Thickness(8) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            var splitter = new GridSplitter { HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent }; Grid.SetColumn(splitter, 1); root.Children.Add(splitter);

            var left = new DockPanel();
            var leftButtons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(leftButtons, Dock.Bottom);
            Button Btn(string text, string tip, RoutedEventHandler h) { var b = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 6), ToolTip = tip }; b.Click += h; return b; }
            var list = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            left.Children.Add(leftButtons); left.Children.Add(list);
            root.Children.Add(left);

            var right = new DockPanel(); Grid.SetColumn(right, 2);
            var header = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 0, 6) };
            DockPanel.SetDock(header, Dock.Top);
            var input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 120, MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = L.T("Wiadomość (Enter - wyślij, Shift+Enter - nowa linia)") };
            var sendBtn = new Button { Content = "➤", FontSize = 16, Width = 44, Margin = new Thickness(6, 0, 0, 0), ToolTip = L.T("Wyślij") };
            var fileBtn = new Button { Content = "📎", FontSize = 16, Width = 44, Margin = new Thickness(0, 0, 6, 0), ToolTip = L.T("Załącz plik") };
            var bottom = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(bottom, Dock.Bottom);
            DockPanel.SetDock(fileBtn, Dock.Left); DockPanel.SetDock(sendBtn, Dock.Right);
            bottom.Children.Add(fileBtn); bottom.Children.Add(sendBtn); bottom.Children.Add(input);
            var msgs = new StackPanel { Margin = new Thickness(4) };
            var scroll = new ScrollViewer { Content = msgs, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            right.Children.Add(header); right.Children.Add(bottom); right.Children.Add(scroll);
            root.Children.Add(right);
            w.Content = root;

            KomContact Current() => KomContactById(_komOpenId ?? "");
            void FillList()
            {
                var sel = _komOpenId;
                list.Items.Clear();
                if (_komContacts.Count == 0)
                    list.Items.Add(new ListBoxItem { IsEnabled = false, Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = L.T("Brak kontaktów. Kliknij „➕ Dodaj”, gdy drugie urządzenie ma otwarty komunikator Velivo w tej samej sieci.") } });
                foreach (var c in _komContacts.OrderByDescending(x => KomChat(x.Id).LastOrDefault()?.Ts ?? 0))
                {
                    var last = KomChat(c.Id).LastOrDefault();
                    _komUnread.TryGetValue(c.Id, out var un);
                    bool on = KomOnline(c.Id);
                    var tb = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, Opacity = on ? 1 : 0.7 };
                    tb.Inlines.Add(new System.Windows.Documents.Run("● ") { Foreground = on ? new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)) : Brushes.Gray, FontSize = 14 });
                    tb.Inlines.Add(new System.Windows.Documents.Run(c.Name + (un > 0 ? "  (" + un + ")" : "")) { FontWeight = un > 0 ? FontWeights.Bold : FontWeights.Normal });
                    if (last != null) { tb.Inlines.Add(new System.Windows.Documents.LineBreak()); tb.Inlines.Add(new System.Windows.Documents.Run((last.Mine ? "➜ " : "") + last.Text.Replace('\n', ' ')) { FontSize = 11, Foreground = Brushes.Gray }); }
                    var item = new ListBoxItem { Content = tb, Tag = c.Id, Padding = new Thickness(6), ToolTip = L.T("Rozmowa z ") + c.Name + " – " + (on ? L.T("w sieci") : L.T("poza siecią")) };
                    var cm = new ContextMenu();
                    var ren = new MenuItem { Header = L.T("✏ Zmień nazwę") }; ren.Click += (s, e) => KomRename(c);
                    var del = new MenuItem { Header = L.T("🗑 Usuń kontakt i rozmowę") }; del.Click += (s, e) =>
                    {
                        if (MessageBox.Show(w, (L.En ? "Delete “" + c.Name + "” and the whole chat?" : "Usunąć „" + c.Name + "” i całą rozmowę?"), L.T("Komunikator Velivo"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                        _komContacts.Remove(c); _komChats.Remove(c.Id); _komUnread.Remove(c.Id); if (_komOpenId == c.Id) _komOpenId = null; KomSave(); _komRefresh?.Invoke();
                    };
                    cm.Items.Add(ren); cm.Items.Add(del); item.ContextMenu = cm;
                    list.Items.Add(item);
                    if (c.Id == sel) item.IsSelected = true;
                }
            }
            void FillChat()
            {
                var c = Current();
                header.Inlines.Clear();
                if (c == null) header.Text = L.T("Wybierz rozmowę z listy");
                else
                {
                    bool on = KomOnline(c.Id);
                    header.Inlines.Add(new System.Windows.Documents.Run("💬 " + c.Name + "  "));
                    header.Inlines.Add(new System.Windows.Documents.Run(on ? L.T("● w sieci") : L.T("● poza siecią – wiadomości dojdą, gdy wróci"))
                        { FontSize = 12, FontWeight = FontWeights.Normal, Foreground = on ? new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)) : Brushes.Gray });
                }
                input.IsEnabled = sendBtn.IsEnabled = fileBtn.IsEnabled = c != null;
                msgs.Children.Clear();
                if (c == null) return;
                foreach (var m in KomChat(c.Id).Skip(Math.Max(0, KomChat(c.Id).Count - 300)))
                {
                    var time = DateTimeOffset.FromUnixTimeMilliseconds(m.Ts).LocalDateTime;
                    var stamp = (time.Date == DateTime.Today ? time.ToString("HH:mm") : time.ToString("g")) + (m.Mine ? (m.Ok ? " ✓" : " …") : "");
                    var body = new StackPanel();
                    // kolory dymkow na sztywno - czytelne w jasnym i ciemnym motywie (bez ramek pola tekstowego)
                    bool dark = DarkWindows;
                    var fg = m.Mine ? Brushes.White : new SolidColorBrush(dark ? Color.FromRgb(0xEC, 0xE8, 0xFF) : Color.FromRgb(0x1B, 0x17, 0x33));
                    var txt = new TextBlock { Text = m.Text, TextWrapping = TextWrapping.Wrap, Foreground = fg, FontSize = 14 };
                    var copy = new MenuItem { Header = L.T("Kopiuj") }; var copyText = m.Text; copy.Click += (s2, e2) => { try { Clipboard.SetText(copyText); } catch (Exception) { } };
                    txt.ContextMenu = new ContextMenu { Items = { copy } };
                    body.Children.Add(txt);
                    body.Children.Add(new TextBlock { Text = stamp, FontSize = 10, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Right, Foreground = fg, ToolTip = m.Mine && !m.Ok ? L.T("Jeszcze nie doręczono – Velivo wyśle, gdy odbiorca będzie w sieci") : null });
                    msgs.Children.Add(new Border
                    {
                        Child = body, CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(m.Mine ? 60 : 0, 3, m.Mine ? 0 : 60, 3),
                        HorizontalAlignment = m.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                        Background = m.Mine ? new SolidColorBrush(Color.FromRgb(0x6D, 0x3F, 0xDB)) : new SolidColorBrush(dark ? Color.FromRgb(0x3A, 0x33, 0x60) : Color.FromRgb(0xEC, 0xE8, 0xF8))
                    });
                }
                scroll.ScrollToEnd();
                if (_komUnread.Remove(c.Id)) { KomSave(); FillList(); }
            }
            _komRefresh = () => Dispatcher.BeginInvoke(new Action(() => { FillList(); FillChat(); }));
            _komSelect = id => { _komOpenId = id; FillList(); FillChat(); };
            list.SelectionChanged += (s, e) => { if (list.SelectedItem is ListBoxItem it && it.Tag is string id && id != _komOpenId) { _komOpenId = id; FillChat(); } };
            void DoSend()
            {
                var c = Current(); var text = input.Text.Trim();
                if (c == null || text.Length == 0) return;
                input.Clear(); KomSend(c, text); FillList(); FillChat();
            }
            sendBtn.Click += (s, e) => DoSend();
            input.PreviewKeyDown += (s, e) => { if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; DoSend(); } };
            // 📎 do zaznaczonego kontaktu: 🟢 w sieci - od razu na znany adres; szukanie tylko, gdy adresu nie znamy
            async Task AttachTo(KomContact c)
            {
                IPAddress addr = null;
                if (!KomOnline(c.Id) || !IPAddress.TryParse(c.Addr ?? "", out addr)) { addr = (await KomFind()).FirstOrDefault(x => x.Id == c.Id).Addr; }
                if (addr == null) { ShowToast(L.T("Tej osoby nie ma teraz w sieci"), null); return; }
                await KomSendFilesTo(w, c.Name, addr, c);
            }
            fileBtn.Click += async (s, e) => { var c = Current(); if (c != null) await AttachTo(c); };
            leftButtons.Children.Add(Btn(L.T("➕ Dodaj"), L.T("Dodaj kontakt: telefon lub komputer z Velivo w tej samej sieci"), async (s, e) => await KomAddDialog(w)));
            leftButtons.Children.Add(Btn(L.T("📎 Plik do…"), L.T("Wyślij pliki na telefon lub komputer z Velivo w tej sieci (bez parowania, z kodem)"), async (s, e) =>
            {
                var c = Current(); if (c != null) { await AttachTo(c); return; }   // zaznaczony kontakt - bez szukania i bez kodu
                var peer = await KomChoosePeer(w, L.T("📎 Wyślij pliki do…")); if (peer.Addr != null) await KomSendFilesTo(w, peer.Name, peer.Addr, KomContactById(peer.Id));
            }));
            w.Activated += (s, e) => { if (_komOpenId != null && _komUnread.ContainsKey(_komOpenId)) FillChat(); };
            w.Closed += (s, e) => { _komWindow = null; _komRefresh = null; _komSelect = null; };
            _komWindow = w;
            if (openId != null) _komOpenId = openId;
            FillList(); FillChat();
            w.Show(); w.Activate();
            input.Focus();
        }
        Action<string> _komSelect;

        void KomRename(KomContact c)
        {
            var box = new TextBox { Text = c.Name, Margin = new Thickness(10), MinWidth = 260 };
            var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(10, 0, 10, 10), HorizontalAlignment = HorizontalAlignment.Right };
            var d = new Window { Title = L.T("✏ Zmień nazwę"), SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = _komWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new StackPanel { Children = { box, ok } } };
            ok.Click += (s, e) => d.DialogResult = true;
            box.SelectAll(); box.Focus();
            if (d.ShowDialog() == true && box.Text.Trim().Length > 0) { c.Name = box.Text.Trim().Length > 64 ? box.Text.Trim().Substring(0, 64) : box.Text.Trim(); KomSave(); _komRefresh?.Invoke(); }
        }

        async Task<(string Id, string Name, IPAddress Addr)> KomChoosePeer(Window owner, string title)
        {
            var wait = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new TextBlock { Text = L.T("Szukam urządzeń z Velivo w tej sieci…"), Margin = new Thickness(24) } };
            wait.Show();
            var found = await KomFind();
            wait.Close();
            if (found.Count == 0) { MessageBox.Show(owner, L.T("Nie znaleziono. Na drugim urządzeniu otwórz komunikator Velivo (albo przeglądarkę Velivo) w tej samej sieci i spróbuj ponownie."), title); return default; }
            var lb = new ListBox { Margin = new Thickness(10), MinWidth = 280, MinHeight = 120 };
            foreach (var f in found) lb.Items.Add((KomContactById(f.Id) != null ? "👤 " : "📱 ") + f.Name);
            lb.SelectedIndex = 0;
            var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(10, 0, 10, 10), HorizontalAlignment = HorizontalAlignment.Right };
            var d = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new StackPanel { Children = { lb, ok } } };
            ok.Click += (s, e) => d.DialogResult = true;
            lb.MouseDoubleClick += (s, e) => d.DialogResult = true;
            return d.ShowDialog() == true && lb.SelectedIndex >= 0 ? found[lb.SelectedIndex] : default;
        }

        Window KomCodeWindow(Window owner, string title, string code)
        {
            var w = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel { Margin = new Thickness(24), Children = {
                    new TextBlock { Text = code, FontSize = 32, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = L.T("Sprawdź, czy drugie urządzenie pokazuje ten sam kod, i przyjmij tam."), Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 320 } } } };
            w.Show();
            return w;
        }

        async Task KomAddDialog(Window owner)
        {
            var p = await KomChoosePeer(owner, L.T("➕ Dodaj kontakt"));
            if (p.Addr == null) return;
            Window codeWin = null;
            var r = await KomPair(p.Id, p.Name, p.Addr, code => Dispatcher.Invoke(() => codeWin = KomCodeWindow(owner, "➕ " + p.Name, code)));
            codeWin?.Close();
            ShowToast(r == "ok" ? L.T("Dodano: ") + p.Name : L.T("Nie dodano: ") + r, null);
            if (r == "ok") _komSelect?.Invoke(p.Id);
        }

        async Task KomSendFilesTo(Window owner, string name, IPAddress addr, KomContact to = null)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = (L.En ? "Send files to " : "Wyślij pliki do: ") + name, Multiselect = true };
            if (dlg.ShowDialog(owner) != true) return;
            foreach (var f in dlg.FileNames)
            {
                Window codeWin = null;
                if (to != null) ShowToast(L.T("Wysyłam: ") + Path.GetFileName(f), null);
                var r = await KomSendFile(addr, f, code => Dispatcher.Invoke(() => codeWin = KomCodeWindow(owner, "📎 " + Path.GetFileName(f) + " → " + name, code)), to);
                codeWin?.Close();
                if (r == "ok" && to != null) KomNote(to, true, "📎 " + Path.GetFileName(f));
                ShowToast(Path.GetFileName(f) + ": " + (r == "ok" ? L.T("wysłano") : r), null);
            }
        }

        // menu zasobnika: 💬 Komunikator
        System.Windows.Forms.ToolStripMenuItem TrayMessengerItem()
        {
            var it = new System.Windows.Forms.ToolStripMenuItem(L.T("💬 Komunikator"));
            it.Click += (s, e) => Dispatcher.Invoke(() => ShowMessenger());
            return it;
        }

        // 💬 na pasku Velivo obok 📎
        void InitMessengerButton()
        {
            var b = new Button { Content = "💬", FontSize = 16, Width = 40, Height = 36, Margin = new Thickness(2, 3, 2, 0), ToolTip = L.T("Komunikator Velivo – rozmowy w sieci domowej, bez internetu") };
            DockPanel.SetDock(b, Dock.Right);
            b.Click += (s, e) => ShowMessenger();
            int at = _sendFileBtn != null ? TabBarPanel.Children.IndexOf(_sendFileBtn) : -1;
            TabBarPanel.Children.Insert(at >= 0 ? at : 1, b);
        }
    }
}
