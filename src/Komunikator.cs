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
        // Name - nazwa wyswietlana (wlasna albo nazwa urzadzenia); Device - prawdziwa nazwa urzadzenia (aktualizowana z sieci); Own - nazwa nadana przez uzytkownika
        sealed class KomContact { public string Id; public string Name; public byte[] Key; public string Addr; public string Device; public bool Own; public string Relay; }   // Relay: adres pośrednika tego kontaktu (No-IP:port) - rozmowy poza domem
        sealed class KomMsg { public bool Mine; public string Text; public long Ts; public bool Ok; public string Mid; public bool Read; public bool File; public string Path; }   // Read: moja - przeczytana (✓✓), odebrana - wyslano potwierdzenie; File: wpis o pliku (nie wysylany ponownie jako tekst)

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
                    _komTheme = (string)o["theme"] ?? "auto";
                    _komAutoImages = (bool?)o["autoimg"] ?? true;
                    _komNotifyNew = (bool?)o["notifynew"] ?? true; _komSound = (string)o["sound"] ?? "";
                    foreach (var iv in o["invites"]?.AsArray() ?? new JsonArray())   // ✉ zaproszenia czekajace na pośredniku
                    {
                        var ik = KomB64((string)iv["k"]); long ie = (long?)iv["exp"] ?? 0;
                        if (ik.Length == 32 && ie > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) _komInvites[KomInviteId(ik)] = new KomInvite { K = ik, By = (string)iv["by"], Exp = ie };
                    }
                    _komRelayOn = (bool?)o["relayOn"] ?? false; _komRelayAuto = (bool?)o["relayAuto"] ?? true; _komRelayAsked = (bool?)o["relayAsked"] ?? false; _komRelayHost = (string)o["relayHost"] ?? ""; _komRelayPort = (int?)o["relayPort"] ?? 41920;
                    // kazdy wpis osobno - jeden uszkodzony nie kasuje reszty kontaktow i rozmow
                    foreach (var c in o["contacts"]?.AsArray() ?? new JsonArray())
                        try
                        {
                            var k = Convert.FromBase64String((string)c["key"] ?? "");
                            if (k.Length == 32) _komContacts.Add(new KomContact { Id = (string)c["id"], Name = (string)c["name"], Key = k, Addr = (string)c["addr"] ?? "", Device = (string)c["dev"] ?? (string)c["name"], Own = (bool?)c["own"] ?? false, Relay = (string)c["relay"] });
                        }
                        catch (Exception ex) { App.LogError(ex); }
                    foreach (var kv in o["chats"]?.AsObject() ?? new JsonObject())
                        try
                        {
                            var l = new List<KomMsg>();
                            foreach (var m in kv.Value.AsArray())
                                try { l.Add(new KomMsg { Mine = (bool?)m["me"] ?? false, Text = (string)m["t"] ?? "", Ts = (long?)m["ts"] ?? 0, Ok = (bool?)m["ok"] ?? true, Mid = (string)m["mid"] ?? "", Read = (bool?)m["rd"] ?? false, File = (bool?)m["f"] ?? false, Path = (string)m["p"] }); }
                                catch (Exception) { }
                            _komChats[kv.Key] = l;
                        }
                        catch (Exception ex) { App.LogError(ex); }
                    foreach (var kv in o["unread"]?.AsObject() ?? new JsonObject()) try { _komUnread[kv.Key] = (int?)kv.Value ?? 0; } catch (Exception) { }
                }
            }
            catch (Exception ex)
            {   // plik nieczytelny - zostaje jako kopia (nigdy nie nadpisujemy kontaktow pusta lista)
                App.LogError(ex);
                try { if (File.Exists(KomFile)) File.Copy(KomFile, KomFile + ".kopia-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), false); } catch (Exception) { }
            }
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
                    foreach (var m in kv.Value.Skip(Math.Max(0, kv.Value.Count - 500))) a.Add(new JsonObject { ["me"] = m.Mine, ["t"] = m.Text, ["ts"] = m.Ts, ["ok"] = m.Ok, ["mid"] = m.Mid, ["rd"] = m.Read, ["f"] = m.File, ["p"] = m.Path });
                    chats[kv.Key] = a;
                }
                var unread = new JsonObject(); foreach (var kv in _komUnread) unread[kv.Key] = kv.Value;
                var o = new JsonObject
                {
                    ["id"] = _komId,
                    ["contacts"] = new JsonArray(_komContacts.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["name"] = c.Name, ["key"] = Convert.ToBase64String(c.Key), ["addr"] = c.Addr, ["dev"] = c.Device, ["own"] = c.Own, ["relay"] = c.Relay }).ToArray()),
                    ["chats"] = chats, ["unread"] = unread, ["theme"] = _komTheme, ["autoimg"] = _komAutoImages, ["notifynew"] = _komNotifyNew, ["sound"] = _komSound, ["relayOn"] = _komRelayOn, ["relayAuto"] = _komRelayAuto, ["relayAsked"] = _komRelayAsked, ["invites"] = new JsonArray(_komInvites.Values.Where(v => v.Exp > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Select(v => (JsonNode)new JsonObject { ["k"] = Convert.ToBase64String(v.K), ["by"] = v.By, ["exp"] = v.Exp }).ToArray()), ["relayHost"] = _komRelayHost, ["relayPort"] = _komRelayPort
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
            if (_komContacts.Count > 0) KomEnsureTray();
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
            // pośrednik / poza domem: pakiet do urzadzenia za pośrednikiem - tunelem (KomunikatorPosrednik.cs)
            if (KomRouteRemote(p, to == null)) return;
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

        JsonObject KomPacket(string t)
        {
            var p = new JsonObject { ["t"] = t, ["id"] = _komId, ["device"] = _lanDeviceName, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["kind"] = "pc" };
            if (t == "p2p-here" && _komRelayOn && _komRelayHost.Length > 0 && _komUp == null) p["relay"] = _komRelayHost + ":" + _komRelayPort;   // tylko w domu (laptop poza domem nie podaje adresu obcej sieci)   // 🌍 pośrednik: kontakty zapamietuja adres na wyjscie z domu
            return p;
        }
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
            KomRelayFromLan(p, remote.Address);   // pośrednik: do urzadzen poza domem
            Dispatcher.BeginInvoke(new Action(() => { try { KomHandle(p, remote.Address); } catch (Exception ex) { App.LogError(ex); } }));
            return true;
        }

        // 📶 w domowej sieci jest urzadzenie z Velivo spoza kontaktow - powiadomienie raz na dobe (wylaczane w 🎨)
        bool _komNotifyNew = true;
        string _komSound = "";   // 🔔 wybrany dźwięk wiadomości (.wav); pusty - dźwięk Windows
        void KomPlaySound()
        {
            try { if (_komSound.Length > 0 && File.Exists(_komSound)) new System.Media.SoundPlayer(_komSound).Play(); else System.Media.SystemSounds.Asterisk.Play(); } catch (Exception) { }
        }
        readonly Dictionary<string, DateTime> _komNewSeen = new Dictionary<string, DateTime>();
        void KomNotifyNew(string id, string dev)
        {
            if (!_komNotifyNew || _komNewSeen.TryGetValue(id, out var at) && DateTime.UtcNow - at < TimeSpan.FromDays(1)) return;
            _komNewSeen[id] = DateTime.UtcNow;
            ShowToast(L.T("📶 Velivo w tej sieci: ") + (dev.Length > 0 ? dev : L.T("urządzenie")) + L.T(" – dodasz je w komunikatorze: ➕ Dodaj"), null);
        }

        void KomHandle(JsonNode p, IPAddress sender)
        {
            KomLoad();
            string t = (string)p["t"], from = (string)p["id"];
            if (from == null || from == _komId || from.Length != 32) return;
            long ts = (long?)p["ts"] ?? 0;
            if (Math.Abs(ts - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) > 600000) return;
            var c = KomContactById(from);
            if (t == "p2p-bye") { if (c != null && _komLastSeen.Remove(c.Id)) _komRefresh?.Invoke(); return; }   // zamknal Velivo - od razu poza siecia
            if (c == null && !IPAddress.IsLoopback(sender) && (t == "p2p-here" || t == "p2p-who")) KomNotifyNew(from, KomClean((string)p["device"]));
            if (c != null)
            {
                bool was = KomOnline(c.Id); _komLastSeen[c.Id] = DateTime.UtcNow; _komSeenAt[c.Id] = DateTime.Now;
                if (t == "p2p-here" && !IPAddress.IsLoopback(sender) && c.Addr != sender.ToString()) { c.Addr = sender.ToString(); KomSave(); }   // aktualny adres - pliki bez ponownego szukania
                // 🌍 adres pośrednika kontaktu (np. NUCBOX z No-IP) - poza domem rozmowy ida przez niego
                var relayAddr = (string)p["relay"];
                if (t == "p2p-here" && relayAddr != null && relayAddr.Length < 200 && relayAddr.Contains(':') && relayAddr != c.Relay) { c.Relay = relayAddr; KomSave(); }
                // prawdziwa nazwa urzadzenia (zmieniona na tamtym komputerze / telefonie); wlasna nazwa kontaktu zostaje
                var realName = KomClean((string)p["device"]);
                if (realName.Length > 0 && realName != c.Device) { c.Device = realName; if (!c.Own) c.Name = realName; KomSave(); _komRefresh?.Invoke(); }
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
                        if (!IPAddress.IsLoopback(sender) && c.Addr != sender.ToString()) { c.Addr = sender.ToString(); KomSave(); }
                        if (_komAcks.TryGetValue(mid, out var tcs)) tcs.TrySetResult(true);
                        return;
                    }
                case "p2p-msg": KomReceive(p, c, sender, ts); return;
                case "p2p-invite": KomInviteStore(p, c); return;   // ✉ zaproszenie od kontaktu (ten komputer jest pośrednikiem)
                case "p2p-read":   // ✓✓ odbiorca przeczytal nasze wiadomosci
                    {
                        if (c == null || (string)p["to"] != _komId) return;
                        var mids = (string)p["mids"] ?? "";
                        var mac = KomB64((string)p["mac"]); var exp = KomHmac(c.Key, "read\n" + mids);
                        if (mac.Length != exp.Length || !CryptographicOperations.FixedTimeEquals(mac, exp)) return;
                        var set = new HashSet<string>(mids.Split(','));
                        bool any = false;
                        foreach (var m in KomChat(c.Id)) if (m.Mine && !m.Read && set.Contains(m.Mid)) { m.Read = true; m.Ok = true; any = true; }
                        if (any) { KomSave(); _komRefresh?.Invoke(); }
                        return;
                    }
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
            var ack = KomPacket("p2p-ack"); ack["to"] = c.Id; ack["mid"] = mid; ack["mac"] = Convert.ToBase64String(KomHmac(c.Key, "ack\n" + mid));
            KomSendUdp(ack, sender);
            if (!_komSeen.Add(mid)) return;   // powtorka - tylko ponowne potwierdzenie
            if (!IPAddress.IsLoopback(sender) && c.Addr != sender.ToString()) c.Addr = sender.ToString();
            if (text.StartsWith(KomCallTag, StringComparison.Ordinal)) { KomOnCall(c, text.Substring(KomCallTag.Length)); return; }   // 📹 sygnał rozmowy wideo - poza czatem
            if (text.Length > 4000) text = text.Substring(0, 4000);
            KomChat(c.Id).Add(new KomMsg { Mine = false, Text = text, Ts = ts, Ok = true, Mid = mid });
            bool visible = _komWindow != null && _komWindow.IsVisible && _komWindow.IsActive && _komOpenId == c.Id;
            if (!visible) _komUnread[c.Id] = (_komUnread.TryGetValue(c.Id, out var u) ? u : 0) + 1;
            KomSave();
            _komRefresh?.Invoke();
            KomTrayUpdate();
            if (!visible) KomNotify(c, text); else KomSendRead(c);
            KomResendPending(c);
        }

        // powiadomienie jak w innych komunikatorach: okienko Velivo w prawym dolnym rogu, nad innymi programami
        // (takze gdy Velivo jest zminimalizowane lub w zasobniku): nadawca, tresc, szybka odpowiedz; klikniecie - rozmowa
        readonly List<Window> _komPopups = new List<Window>();
        void KomNotify(KomContact c, string text)
        {
            KomPlaySound();
            try { if (_komWindow != null && !_komWindow.IsActive) FlashWindowEx(_komWindow); } catch (Exception) { }
            // ta sama osoba - jedno okienko z nowa trescia
            var old = _komPopups.FirstOrDefault(p => (string)p.Tag == c.Id);
            if (old != null) { old.Close(); }
            var pal = KomCurrentPal();   // powiadomienie w kolorach motywu komunikatora
            var bg = new SolidColorBrush(pal.Panel);
            var fg = new SolidColorBrush(pal.Fg);
            var accent = new SolidColorBrush(pal.Mine);
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
            var reply = new TextBox { MinHeight = 30, Language = System.Windows.Markup.XmlLanguage.GetLanguage(L.En ? "en-US" : "pl-PL"), SpellCheck = { IsEnabled = true }, VerticalContentAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(pal.Input), Foreground = fg, BorderBrush = accent, CaretBrush = fg, ToolTip = L.T("Odpowiedz (Enter - wyślij)") };
            var send = new Button { Content = "➤", Width = 36, Margin = new Thickness(6, 0, 0, 0), Background = accent, Foreground = new SolidColorBrush(pal.MineFg), BorderThickness = new Thickness(0), ToolTip = L.T("Wyślij"), Cursor = Cursors.Hand };
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

        // wlasna ikona komunikatora na pasku zadan: identyfikator okna jak skrot "Komunikator Velivo" (Velivo.Komunikator)
        [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        interface IKomPropStore
        {
            int GetCount(out uint c); int GetAt(uint i, out KomPropKey k); int GetValue(ref KomPropKey k, out KomPropVar v);
            int SetValue(ref KomPropKey k, ref KomPropVar v); int Commit();
        }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
        struct KomPropKey { public Guid Fmtid; public uint Pid; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        struct KomPropVar { [System.Runtime.InteropServices.FieldOffset(0)] public ushort Vt; [System.Runtime.InteropServices.FieldOffset(8)] public IntPtr Ptr; }
        [System.Runtime.InteropServices.DllImport("shell32.dll", EntryPoint = "SHGetPropertyStoreForWindow")] static extern int KomGetPropStore(IntPtr hwnd, ref Guid iid, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Interface)] out IKomPropStore store);
        static void SetWindowAppId(Window w, string id)
        {
            try
            {
                var h = new System.Windows.Interop.WindowInteropHelper(w).Handle; if (h == IntPtr.Zero) return;
                var iid = typeof(IKomPropStore).GUID;
                if (KomGetPropStore(h, ref iid, out var store) != 0 || store == null) return;
                var key = new KomPropKey { Fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Pid = 5 };
                var v = new KomPropVar { Vt = 31, Ptr = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUni(id) };   // VT_LPWSTR
                try { store.SetValue(ref key, ref v); store.Commit(); }
                finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(v.Ptr); System.Runtime.InteropServices.Marshal.ReleaseComObject(store); }
            }
            catch (Exception ex) { App.LogError(ex); }
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

        // rozmowa na ekranie: "przeczytane" do nadawcy
        void KomSendRead(KomContact c)
        {
            var mids = KomChat(c.Id).Where(m => !m.Mine && !m.Read && m.Mid.Length == 32).ToList();
            if (mids.Count == 0) return;
            foreach (var m in mids) m.Read = true;
            KomSave();
            IPAddress.TryParse(c.Addr ?? "", out var to);
            foreach (var part in mids.Select(m => m.Mid).Chunk(60))
            {
                var j = string.Join(",", part);
                var p = KomPacket("p2p-read"); p["to"] = c.Id; p["mids"] = j; p["mac"] = Convert.ToBase64String(KomHmac(c.Key, "read\n" + j));
                KomSendUdp(p, to);
            }
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
            foreach (var m in KomChat(c.Id).Where(x => x.Mine && !x.Ok && !x.File && x.Ts > week).ToList()) KomDeliver(c, m);
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

        // 🟢 w sieci: kontakt odezwal sie w ostatnich 95 s (kazde Velivo z komunikatorem oglasza sie co minute, okno pyta co 30 s)
        readonly Dictionary<string, DateTime> _komLastSeen = new Dictionary<string, DateTime>();
        readonly Dictionary<string, DateTime> _komSeenAt = new Dictionary<string, DateTime>();   // "ostatnio w sieci" - zostaje po pozegnaniu
        bool KomViaNet(string id) => _komUp != null || _komViaNet.ContainsKey(id);   // 🌍 kontakt osiagalny przez internet (nie w sieci domowej)
        bool KomOnline(string id) => _komLastSeen.TryGetValue(id, out var t) && DateTime.UtcNow - t < TimeSpan.FromSeconds(95);

        // zamkniecie Velivo: kontakty od razu widza "poza siecia"
        internal void KomBye()
        {
            if (!_komLoaded || _komContacts.Count == 0) return;
            for (int i = 0; i < 2; i++) KomSendUdp(KomPacket("p2p-bye"), null);
        }

        void KomStartResend()
        {
            if (_komResend != null) return;
            KomRelayStart(); KomUpStart();   // 🌍 pośrednik (gdy wlaczony) i laczenie z pośrednikiem poza domem
            Dispatcher.BeginInvoke(new Action(KomRelayAskOnce), System.Windows.Threading.DispatcherPriority.ApplicationIdle);   // kontakty sprzed tej wersji - takze poza domem
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
        internal Task<bool> KomTcpAsync(byte[] headerBytes, NetworkStream stream, TcpClient client, CancellationToken token) =>
            KomTcpAsync(headerBytes, stream, (client.Client.RemoteEndPoint as IPEndPoint)?.Address, token);

        // strumien: polaczenie w sieci domowej albo zaszyfrowany tunel przez pośrednika (KomunikatorPosrednik.cs)
        internal async Task<bool> KomTcpAsync(byte[] headerBytes, Stream stream, IPAddress remote, CancellationToken token)
        {
            JsonNode h; try { h = JsonNode.Parse(headerBytes); } catch (Exception) { return false; }
            var type = (string)h?["Type"];
            if (type != "p2p-pair" && type != "p2p-file") return false;
            // plik dla urzadzenia poza domem polaczonego przez tego pośrednika - przekazanie tunelem
            var toId = (string)h["To"];
            if (type == "p2p-file" && toId != null && toId != _komId && _komRemote.ContainsKey(toId)) { await KomRelayToRemote(toId, headerBytes, stream, token); return true; }
            try { if (type == "p2p-pair") await KomReceivePair(h, stream, remote, token); else await KomReceiveFile(h, stream, token); }
            catch (Exception ex) { if (!token.IsCancellationRequested) App.LogError(ex); }
            return true;
        }

        async Task<bool> KomAsk(string title, string text)
        {
            return await Dispatcher.InvokeAsync(() => MessageBox.Show((Window)_komWindow ?? PairDialogOwner(), text, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
        }

        // auto - nowe urzadzenie z zaproszeniem dolacza do pośrednika (zgoda dana zaproszeniem; rozmowa wymaga jeszcze zgody zapraszajacego)
        async Task KomReceivePair(JsonNode h, Stream s, IPAddress remote, CancellationToken token, bool auto = false)
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
                var invited = (bool?)h["Invited"] == true;
                if (!auto) await Dispatcher.InvokeAsync(() => { try { System.Media.SystemSounds.Asterisk.Play(); } catch (Exception) { } });   // prośba od razu slyszalna, takze z zasobnika
                var ok = auto || await KomAsk(L.T("💬 Nowy kontakt") + (invited ? L.T(" (zaproszenie przez internet)") : ""), (L.En ? "“" + dev + "” wants to add you to Velivo messenger.\n\nCode: " : "„" + dev + "” chce dodać Cię do komunikatora Velivo.\n\nKod: ") + KomCode(key) +
                    L.T("\n\nPrzyjmij tylko, gdy na drugim urządzeniu jest ten sam kod."));
                await s.WriteAsync(new[] { (byte)(ok ? 1 : 0) }, 0, 1, token); await s.FlushAsync(token);
                if (!ok) return;
                await KomWriteBlock(s, Encoding.UTF8.GetBytes(new JsonObject { ["Id"] = _komId, ["Device"] = _lanDeviceName }.ToJsonString()), token);
                await Dispatcher.InvokeAsync(() => KomAddContact(new KomContact { Id = other, Name = dev, Device = dev, Key = key, Addr = remote?.ToString() ?? "" }));
            }
        }

        void KomAddContact(KomContact c)
        {
            KomEnsureTray();
            var old = KomContactById(c.Id); if (old != null && old.Own) { c.Own = true; c.Name = old.Name; }   // ponowne dodanie - wlasna nazwa zostaje
            _komContacts.RemoveAll(x => x.Id == c.Id); _komContacts.Add(c); KomSave(); _komRefresh?.Invoke();
            Dispatcher.BeginInvoke(new Action(KomRelayAskOnce), System.Windows.Threading.DispatcherPriority.ApplicationIdle);   // 🌍 jedno pytanie o rozmowy poza domem
        }

        async Task<string> KomPair(string id, string name, IPAddress addr, Action<string> showCode)
        {
            try
            {
                using (var cl = new TcpClient())
                using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4)))
                {
                    await cl.ConnectAsync(addr, LanPort, cts.Token);
                    return await KomPairOn(cl.GetStream(), id, name, addr.ToString(), null, false, showCode, cts.Token);
                }
            }
            catch (Exception ex) { return ex.Message; }
        }

        // dodanie kontaktu na gotowym polaczeniu (siec domowa albo tunel przez pośrednika z zaproszeniem)
        async Task<string> KomPairOn(Stream s, string id, string name, string addr, string relay, bool invited, Action<string> showCode, CancellationToken token)
        {
            try
            {
                using (var me = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
                {
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var pubA = me.ExportSubjectPublicKeyInfo();
                    var hj = new JsonObject { ["Type"] = "p2p-pair", ["Id"] = _komId, ["Device"] = _lanDeviceName, ["Pub"] = Convert.ToBase64String(pubA) };
                    if (invited) hj["Invited"] = true;
                    await KomWriteBlock(s, Encoding.UTF8.GetBytes(hj.ToJsonString()), cts.Token);
                    var pubB = await KomReadBlock(s, 200, cts.Token); if (pubB == null) return "błąd";
                    var key = KomAgree(me, pubB, "velivo-czat", pubA, pubB);
                    showCode(KomCode(key));
                    var b = new byte[1]; if (!await ReadExactAsync(s, b, 1, cts.Token) || b[0] != 1) return L.T("odrzucono");
                    var info = await KomReadBlock(s, 4096, cts.Token); if (info == null) return "błąd";
                    var o = JsonNode.Parse(info); if (id != null && (string)o["Id"] != id) return "błąd";
                    var dev = KomClean((string)o["Device"]); var cid = (string)o["Id"]; if (cid == null || cid.Length != 32) return "błąd";
                    var nc = new KomContact { Id = cid, Name = dev.Length > 0 ? dev : name, Device = dev.Length > 0 ? dev : name, Key = key, Addr = addr ?? "", Relay = relay };
                    await Dispatcher.InvokeAsync(() => KomAddContact(nc));
                    return "ok";
                }
            }
            catch (Exception ex) { return ex.Message; }
        }

        static byte[] KomFileAd(JsonNode h) => Encoding.UTF8.GetBytes(string.Join("\n", (string)h["Type"] ?? "", (string)h["Id"] ?? "", (string)h["Device"] ?? "", (string)h["Hash"] ?? "",
            ((long?)h["Size"] ?? 0).ToString(CultureInfo.InvariantCulture), (string)h["Name"] ?? "", (string)h["Pub"] ?? "", (string)h["Nonce"] ?? ""));

        static string KomSize(long size) => size >= 1073741824 ? (size / 1073741824.0).ToString("0.00", CultureInfo.CurrentCulture) + " GB" : size >= 1048576 ? (size / 1048576.0).ToString("0.0", CultureInfo.CurrentCulture) + " MB" : Math.Max(1, size / 1024) + " KB";

        async Task KomReceiveFile(JsonNode h, Stream s, CancellationToken token)
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
                if (from != null && _komAutoImages && size <= KomAutoImageLimit)
                {   // zdjecie / plik od kontaktu - od razu do rozmowy (bez pytania i bez okna zapisu)
                    try
                    {
                        Directory.CreateDirectory(KomMediaDir);
                        var p0 = System.IO.Path.Combine(KomMediaDir, name); var stem = System.IO.Path.GetFileNameWithoutExtension(name); var ext = System.IO.Path.GetExtension(name);
                        for (int i = 2; File.Exists(p0); i++) p0 = System.IO.Path.Combine(KomMediaDir, stem + " (" + i + ")" + ext);
                        target = p0; accept = true; return;
                    }
                    catch (Exception ex) { App.LogError(ex); }
                }
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
                                if (i % 4 == 3 || i == n - 1) { long g = got; _ = Dispatcher.BeginInvoke(new Action(() => KomInfo("📥 " + name + L.T(" od ") + dev + "  " + (size > 0 ? (int)(g * 100 / size) : 100) + " %  (" + KomSize(g) + L.T(" z ") + KomSize(size) + ")", size > 0 ? (double)g / size : 1))); }
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
                await Dispatcher.InvokeAsync(() =>
                {
                    var msg = (L.En ? "📎 File from " : "📎 Plik od ") + dev + ": " + Path.GetFileName(target);
                    if (_komWindow != null && _komWindow.IsVisible) KomInfo(msg + L.T(" – zapisano"));   // okno zostaje na wierzchu
                    else ShowToast(msg, target);
                    if (from != null) KomNote(from, false, (KomIsImage(target) ? "🖼 " : "📎 ") + Path.GetFileName(target) + " · " + KomSize(size), (string)h["Mid"], target);
                    if (from != null && !(_komWindow != null && _komWindow.IsVisible && _komWindow.IsActive && _komOpenId == from.Id))
                    {   // plik / zdjecie jak wiadomosc: nieprzeczytane i powiadomienie (✓ u nadawcy, ✓✓ po otwarciu rozmowy)
                        _komUnread[from.Id] = (_komUnread.TryGetValue(from.Id, out var un0) ? un0 : 0) + 1; KomSave(); KomTrayUpdate();
                        KomNotify(from, (KomIsImage(target) ? "🖼 " : "📎 ") + Path.GetFileName(target));
                    }
                });
            }
            finally { if (tmp != null) try { File.Delete(tmp); } catch (Exception) { } CryptographicOperations.ZeroMemory(key); }
        }

        // plik w rozmowie: ten sam identyfikator u nadawcy i odbiorcy (Mid z naglowka) - "przeczytane" (✓✓) dziala jak przy wiadomosciach
        void KomNote(KomContact c, bool mine, string text, string mid = null, string path = null)
        {
            if (mid == null || mid.Length != 32 || KomChat(c.Id).Any(m => m.Mid == mid)) mid = Guid.NewGuid().ToString("N");
            KomChat(c.Id).Add(new KomMsg { Mine = mine, Text = text, Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Ok = true, Mid = mid, File = true, Path = path });
            KomSave(); _komRefresh?.Invoke();
        }

        async Task<string> KomSendFile(IPAddress addr, string path, Action<string> showCode, KomContact to = null, Action<long, long> progress = null, string mid = null)
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
                        if (mid != null) h["Mid"] = mid;
                        if (to != null) h["To"] = to.Id;   // pośrednik wie, komu przekazac (poza podpisem)
                        var ad = KomFileAd(h);
                        if (to != null) using (var hm = new HMACSHA256(to.Key)) h["Mac"] = Convert.ToBase64String(hm.ComputeHash(ad));   // kontakt rozpozna nadawce bez kodu
                        cts.CancelAfter(TimeSpan.FromSeconds(15));
                        // poza domem - tunel przez pośrednika; w domu - prosto do urzadzenia
                        Stream s = await KomOpenAsync(cl, addr, to?.Id, cts.Token);
                        using var tunnel = s as IDisposable;
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
                                if (progress != null && (i % 4 == 3 || i == n - 1)) progress(sent, size);   // co ok. 4 MB
                                gcm.Encrypt(ChunkNonce(baseNonce, i), buf.AsSpan(0, len), ct.AsSpan(0, len), tag, ChunkAad(ad, i, i == n - 1));
                                await s.WriteAsync(BitConverter.GetBytes(len + 16), 0, 4); await s.WriteAsync(ct, 0, len); await s.WriteAsync(tag, 0, 16);
                                if (s is KomFrameStream) await s.FlushAsync();   // tunel: ramka na kawalek
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

        // ---------- motywy okna komunikatora ----------
        sealed class KomPal
        {
            public string Key, Pl, En; public bool Dark;
            public Color Bg1, Bg2, Panel, Fg, Dim, Mine, MineFg, Theirs, Input, Border, Button;
        }
        static Color Hex(string h) => (Color)ColorConverter.ConvertFromString("#" + h);
        static KomPal Pal(string key, string pl, string en, bool dark, params string[] c) => new KomPal { Key = key, Pl = pl, En = en, Dark = dark,
            Bg1 = Hex(c[0]), Bg2 = Hex(c[1]), Panel = Hex(c[2]), Fg = Hex(c[3]), Dim = Hex(c[4]), Mine = Hex(c[5]), MineFg = Hex(c[6]), Theirs = Hex(c[7]), Input = Hex(c[8]), Border = Hex(c[9]), Button = Hex(c[10]) };
        // te same zestawy co w telefonie (Komunikator.kt)
        static readonly KomPal[] KomPalettes =
        {
            Pal("noc", "🌙 Velivo nocny", "🌙 Velivo night", true, "1E1838", "132A35", "241F45", "ECE8FF", "A9A3CC", "6D3FDB", "FFFFFF", "3A3360", "241F45", "4A3F8A", "2E2768"),
            Pal("jasny", "☀ Velivo jasny", "☀ Velivo light", false, "E6E1F5", "DCE6F0", "F1EEFA", "2A2255", "6E6894", "6D3FDB", "FFFFFF", "FBFAFF", "FAF9FE", "B9ACE6", "E4DEF7"),
            Pal("ocean", "🌊 Ocean", "🌊 Ocean", true, "0B2540", "0E4D64", "10314F", "E3F4FF", "9CC3D9", "0284C7", "FFFFFF", "1D4A6B", "0F2F4A", "2F6C93", "15466B"),
            Pal("las", "🌲 Las", "🌲 Forest", true, "14281D", "1F3D2B", "1B3326", "E6F5EA", "A3C4AE", "15803D", "FFFFFF", "2D5240", "183025", "3C6B52", "24473A"),
            Pal("zachod", "🌅 Zachód słońca", "🌅 Sunset", true, "3B1C32", "6A2C3E", "4A2238", "FFEDE6", "D9AFA8", "EA580C", "FFFFFF", "6E3448", "3F1D30", "8C4A5C", "5C2A42"),
            Pal("grafit", "🖤 Grafit", "🖤 Graphite", true, "1C1C1E", "2C2C2E", "242426", "EDEDED", "A0A0A5", "3A82F7", "FFFFFF", "3A3A3C", "2A2A2C", "48484A", "333336"),
            Pal("roza", "🌸 Róża", "🌸 Rose", false, "FCE7F3", "F5E1FF", "FBEFF7", "4A1D3A", "8E5C7C", "DB2777", "FFFFFF", "FFFFFF", "FFF7FB", "F0ABD0", "F7D9EA"),
            Pal("mieta", "🍃 Mięta", "🍃 Mint", false, "E0F7EF", "DDEFF7", "EEFAF5", "17352B", "5E8577", "0F9D74", "FFFFFF", "FFFFFF", "F7FDFB", "9ED9C5", "D4F0E6"),
            Pal("kontrast", "◐ Wysoki kontrast", "◐ High contrast", true, "000000", "000000", "000000", "FFFFFF", "FFFF00", "FFFF00", "000000", "1A1A1A", "000000", "FFFFFF", "000000"),
        };
        string _komTheme = "auto";
        // 🖼📎 zdjecia i pliki od dodanych kontaktow (do 2 GB) od razu w rozmowie, jak w innych komunikatorach (zgoda: dodanie kontaktu kodem);
        // zapis w wybranym miejscu - prawy przycisk; wylacznik w 🎨. Wieksze i od nieznajomych - z pytaniem
        bool _komAutoImages = true;
        const long KomAutoImageLimit = 2L * 1024 * 1024 * 1024;
        static string KomMediaDir => System.IO.Path.Combine(DataDir, "Komunikator");
        static bool KomIsImage(string name) => new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" }.Contains(System.IO.Path.GetExtension(name ?? "").ToLowerInvariant());
        readonly Dictionary<string, ImageSource> _komThumbs = new Dictionary<string, ImageSource>();
        // miniatura do rozmowy (raz na plik - lista odswieza sie czesto)
        ImageSource KomThumb(string path)
        {
            if (_komThumbs.TryGetValue(path, out var t)) return t;
            try
            {
                var b = new System.Windows.Media.Imaging.BitmapImage();
                b.BeginInit(); b.UriSource = new Uri(path); b.DecodePixelWidth = 260; b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; b.EndInit(); b.Freeze();
                t = b;
            }
            catch (Exception) { t = null; }
            _komThumbs[path] = t; return t;
        }

        // male okna komunikatora (zmiana nazwy, szukanie, kod) w kolorach jego motywu
        void KomStyle(Window d)
        {
            var p = KomCurrentPal();
            SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
            d.Uid = "own-theme"; d.Background = B(p.Panel); d.Foreground = B(p.Fg);
            void Walk(DependencyObject o)
            {
                if (o is TextBox t) { t.Background = B(p.Input); t.Foreground = B(p.Fg); t.BorderBrush = B(p.Border); t.CaretBrush = B(p.Fg); }
                else if (o is ListBox l) { l.Background = B(p.Input); l.Foreground = B(p.Fg); l.BorderBrush = B(p.Border); KomListStyle(l, p); }
                else if (o is Button b) { b.Background = B(p.Mine); b.Foreground = B(p.MineFg); b.BorderBrush = B(p.Border); }
                else if (o is TextBlock tb) tb.Foreground = B(p.Fg);
                foreach (var ch in LogicalTreeHelper.GetChildren(o)) if (ch is DependencyObject dc) Walk(dc);
            }
            Walk(d);
            d.SourceInitialized += (s, e) => { try { var h = new System.Windows.Interop.WindowInteropHelper(d).Handle; int on = p.Dark ? 1 : 0; DwmSetWindowAttribute(h, 20, ref on, 4); } catch (Exception) { } };
        }

        // zaznaczenie i najechanie na liscie w kolorach motywu (zamiast jasnoniebieskiego Windows)
        static void KomListStyle(ListBox l, KomPal p)
        {
            var sel = new SolidColorBrush(Color.FromArgb(0x70, p.Mine.R, p.Mine.G, p.Mine.B)); sel.Freeze();
            var hov = new SolidColorBrush(Color.FromArgb(0x30, p.Mine.R, p.Mine.G, p.Mine.B)); hov.Freeze();
            var border = new FrameworkElementFactory(typeof(Border), "bd");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            var tpl = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border };
            var tOver = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; tOver.Setters.Add(new Setter(Border.BackgroundProperty, hov, "bd")); tpl.Triggers.Add(tOver);
            var tSel = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true }; tSel.Setters.Add(new Setter(Border.BackgroundProperty, sel, "bd")); tpl.Triggers.Add(tSel);
            var st = new Style(typeof(ListBoxItem));
            st.Setters.Add(new Setter(Control.TemplateProperty, tpl));
            st.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            st.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(2, 1, 2, 1)));
            l.ItemContainerStyle = st;
        }   // auto - jak motyw okien Velivo
        KomPal KomCurrentPal() => KomPalettes.FirstOrDefault(p => p.Key == _komTheme) ?? (DarkWindows ? KomPalettes[0] : KomPalettes[1]);

        // ---------- okno ----------
        internal void ShowMessenger(string openId = null)
        {
            KomLoad();
            if (_lanRx == null) KomStartOwn();
            KomEnsureTray();
            if (_komWindow != null) { if (!_komWindow.IsVisible) _komWindow.Show(); _komWindow.Activate(); if (_komWindow.WindowState == WindowState.Minimized) _komWindow.WindowState = WindowState.Normal; if (openId != null) _komSelect?.Invoke(openId); return; }
            var w = new Window { Icon = KomWindowIcon(), Title = L.T("Komunikator Velivo"), Width = 760, Height = 560, MinWidth = 480, MinHeight = 320, WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = true, Uid = "own-theme" };
            if (IsVisible) w.Owner = null;
            var root = new Grid { Margin = new Thickness(8) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            var splitter = new GridSplitter { HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent }; Grid.SetColumn(splitter, 1); root.Children.Add(splitter);

            var left = new DockPanel();
            var leftButtons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(leftButtons, Dock.Bottom);
            var buttons = new List<Button>();
            Button Btn(string text, string tip, RoutedEventHandler h) { var b = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 6), ToolTip = tip }; b.Click += h; buttons.Add(b); return b; }
            var list = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            // 🏠 / 🌍 jak polaczony jest ten komputer - zawsze widoczne u gory
            var netState = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 6) };
            var netText = new TextBlock { FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
            netState.Child = netText; DockPanel.SetDock(netState, Dock.Top);
            left.Children.Add(netState);
            left.Children.Add(leftButtons); left.Children.Add(list);
            root.Children.Add(left);

            var right = new DockPanel(); Grid.SetColumn(right, 2);
            var header = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 0, 6) };
            DockPanel.SetDock(header, Dock.Top);
            var input = new TextBox { AcceptsReturn = true, Language = System.Windows.Markup.XmlLanguage.GetLanguage(L.En ? "en-US" : "pl-PL"), SpellCheck = { IsEnabled = true }, TextWrapping = TextWrapping.Wrap, MaxHeight = 120, MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = L.T("Wiadomość (Enter - wyślij, Shift+Enter - nowa linia)") };
            var sendBtn = new Button { Content = "➤", FontSize = 16, Width = 44, Margin = new Thickness(6, 0, 0, 0), ToolTip = L.T("Wyślij") };
            var fileBtn = new Button { Content = "📎", FontSize = 16, Width = 44, Margin = new Thickness(0, 0, 6, 0), ToolTip = L.T("Załącz plik") };
            var callBtn = new Button { Content = "📹", FontSize = 16, Width = 44, Margin = new Thickness(0, 0, 6, 0), ToolTip = L.T("Rozmowa wideo") };
            var bottom = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(bottom, Dock.Bottom);
            // pasek stanu: wysylanie / odbior pliku z postepem, wyniki - okno zostaje otwarte
            var statusText = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var statusBar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 3, 0, 0), Visibility = Visibility.Collapsed };
            var status = new StackPanel { Margin = new Thickness(4, 4, 4, 0), Visibility = Visibility.Collapsed, Children = { statusText, statusBar } };
            DockPanel.SetDock(status, Dock.Bottom);
            var statusHide = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            statusHide.Tick += (s, e) => { statusHide.Stop(); status.Visibility = Visibility.Collapsed; };
            _komStatus = (t, p) =>
            {
                status.Visibility = Visibility.Visible; statusText.Text = t;
                statusBar.Visibility = p >= 0 ? Visibility.Visible : Visibility.Collapsed; if (p >= 0) statusBar.Value = p;
                statusHide.Stop(); if (p < 0) statusHide.Start();
            };
            DockPanel.SetDock(fileBtn, Dock.Left); DockPanel.SetDock(callBtn, Dock.Left); DockPanel.SetDock(sendBtn, Dock.Right);
            bottom.Children.Add(callBtn); bottom.Children.Add(fileBtn); bottom.Children.Add(sendBtn); bottom.Children.Add(input);
            var msgs = new StackPanel { Margin = new Thickness(4) };
            var scroll = new ScrollViewer { Content = msgs, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            right.Children.Add(header); right.Children.Add(bottom); right.Children.Add(status); right.Children.Add(scroll);
            root.Children.Add(right);
            w.Content = root;
            buttons.Add(sendBtn); buttons.Add(fileBtn); buttons.Add(callBtn);
            root.Margin = new Thickness(0);
            left.Margin = new Thickness(8, 8, 0, 8); right.Margin = new Thickness(0, 8, 8, 8);
            KomPal pal = null;
            // 🎨 motyw komunikatora: tlo, lista, dymki, pole wpisywania, przyciski i pasek tytulu
            void ApplyTheme()
            {
                pal = KomCurrentPal();
                SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
                root.Background = new LinearGradientBrush(pal.Bg1, pal.Bg2, 45);
                w.Background = B(pal.Bg1); w.Foreground = B(pal.Fg);
                list.Background = B(pal.Panel); list.BorderBrush = B(pal.Border); list.Foreground = B(pal.Fg); KomListStyle(list, pal);
                input.Background = B(pal.Input); input.BorderBrush = B(pal.Border); input.Foreground = B(pal.Fg); input.CaretBrush = B(pal.Fg);
                header.Foreground = B(pal.Fg); statusText.Foreground = B(pal.Dim); statusBar.Foreground = B(pal.Mine); statusBar.Background = B(pal.Input); statusBar.BorderBrush = B(pal.Border);
                foreach (var b in buttons) { b.Background = B(pal.Button); b.Foreground = B(pal.Fg); b.BorderBrush = B(pal.Border); }
                sendBtn.Background = B(pal.Mine); sendBtn.Foreground = B(pal.MineFg);
                try { var h = new System.Windows.Interop.WindowInteropHelper(w).Handle; if (h != IntPtr.Zero) { int on = pal.Dark ? 1 : 0; DwmSetWindowAttribute(h, 20, ref on, 4); } } catch (Exception) { }
            }
            ApplyTheme();

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
                    tb.Inlines.Add(new System.Windows.Documents.Run("● ") { Foreground = on ? new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)) : new SolidColorBrush(pal.Dim), FontSize = 14 });
                    if (on) tb.Inlines.Add(new System.Windows.Documents.Run(KomViaNet(c.Id) ? "🌍 " : "🏠 ") { ToolTip = KomViaNet(c.Id) ? L.T("przez internet") : L.T("sieć domowa") });
                    tb.Inlines.Add(new System.Windows.Documents.Run(c.Name + (un > 0 ? "  (" + un + ")" : "")) { FontWeight = un > 0 ? FontWeights.Bold : FontWeights.Normal });
                    if (c.Own && !string.IsNullOrEmpty(c.Device) && c.Device != c.Name) tb.Inlines.Add(new System.Windows.Documents.Run("  " + c.Device) { FontSize = 11, Foreground = new SolidColorBrush(pal.Dim) });   // prawdziwe urzadzenie
                    if (last != null) { tb.Inlines.Add(new System.Windows.Documents.LineBreak()); tb.Inlines.Add(new System.Windows.Documents.Run((last.Mine ? "➜ " : "") + last.Text.Replace('\n', ' ')) { FontSize = 11, Foreground = new SolidColorBrush(pal.Dim) }); }
                    var item = new ListBoxItem { Content = tb, Tag = c.Id, Padding = new Thickness(6), ToolTip = L.T("Rozmowa z ") + c.Name + " – " + (on ? L.T("w sieci") : L.T("poza siecią")) };
                    var cm = new ContextMenu();
                    var ren = new MenuItem { Header = L.T("✏ Zmień nazwę") }; ren.Click += (s, e) => KomRename(c);
                    var del = new MenuItem { Header = L.T("🗑 Usuń kontakt i rozmowę") }; del.Click += (s, e) =>
                    {
                        if (MessageBox.Show(w, (L.En ? "Delete “" + c.Name + "” and the whole chat?" : "Usunąć „" + c.Name + "” i całą rozmowę?"), L.T("Komunikator Velivo"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                        _komContacts.Remove(c); _komChats.Remove(c.Id); _komUnread.Remove(c.Id); if (_komOpenId == c.Id) _komOpenId = null; KomSave(); _komRefresh?.Invoke();
                    };
                    var med = new MenuItem { Header = L.T("🎬 Wyślij Filmy i TV") }; med.Click += async (s, e) => await KomSendMedia(w, c);
                    cm.Items.Add(ren); cm.Items.Add(med); cm.Items.Add(del); item.ContextMenu = cm;
                    list.Items.Add(item);
                    if (c.Id == sel) item.IsSelected = true;
                }
            }
            void FillNet()
            {
                var away = _komUp != null;
                netText.Text = away ? L.T("🌍 Poza domem – przez internet") + (_komUpRelay != null ? L.T(" (przez ") + _komUpRelay.Name + ")" : "") : L.T("🏠 W domu – sieć domowa");
                netState.Background = new SolidColorBrush(away ? Color.FromRgb(0x25, 0x63, 0xEB) : Color.FromRgb(0x16, 0xA3, 0x4A));
                netState.ToolTip = away ? L.T("Wiadomości i pliki idą przez pośrednika w domu, zaszyfrowane") : L.T("Wiadomości i pliki idą prosto w sieci domowej");
            }
            void FillChat()
            {
                FillNet();
                var c = Current();
                header.Inlines.Clear();
                if (c == null) header.Text = L.T("Wybierz rozmowę z listy");
                else
                {
                    bool on = KomOnline(c.Id);
                    header.Inlines.Add(new System.Windows.Documents.Run("💬 " + c.Name + "  "));
                    if (c.Own && !string.IsNullOrEmpty(c.Device) && c.Device != c.Name) header.Inlines.Add(new System.Windows.Documents.Run("(" + c.Device + ")  ") { FontSize = 12, FontWeight = FontWeights.Normal, Foreground = new SolidColorBrush(pal.Dim) });
                    var seen = _komSeenAt.TryGetValue(c.Id, out var sa) ? " · " + L.T("ostatnio ") + (sa.Date == DateTime.Today ? sa.ToString("HH:mm") : sa.ToString("g")) : "";
                    header.Inlines.Add(new System.Windows.Documents.Run(on ? (KomViaNet(c.Id) ? L.T("● w sieci · 🌍 przez internet") : L.T("● w sieci · 🏠 sieć domowa")) : L.T("● poza siecią") + seen + " – " + L.T("wiadomości dojdą, gdy wróci"))
                        { FontSize = 12, FontWeight = FontWeights.Normal, Foreground = on ? new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)) : new SolidColorBrush(pal.Dim) });
                }
                input.IsEnabled = sendBtn.IsEnabled = fileBtn.IsEnabled = callBtn.IsEnabled = c != null;
                msgs.Children.Clear();
                if (c == null) return;
                foreach (var m in KomChat(c.Id).Skip(Math.Max(0, KomChat(c.Id).Count - 300)))
                {
                    var time = DateTimeOffset.FromUnixTimeMilliseconds(m.Ts).LocalDateTime;
                    var stamp = (time.Date == DateTime.Today ? time.ToString("HH:mm") : time.ToString("g")) + (m.Mine ? (m.Read ? " ✓✓" : m.Ok ? " ✓" : " …") : "");
                    var body = new StackPanel();
                    // kolory dymkow z motywu komunikatora (bez ramek pola tekstowego)
                    var fg = new SolidColorBrush(m.Mine ? pal.MineFg : pal.Fg);
                    // zdjecie: miniatura w dymku; klikniecie - pelny obraz; plik - klikniecie otwiera
                    bool hasFile = !string.IsNullOrEmpty(m.Path) && File.Exists(m.Path);
                    if (hasFile && KomIsImage(m.Path) && KomThumb(m.Path) is ImageSource th)
                    {
                        var img = new Image { Source = th, MaxWidth = 260, MaxHeight = 260, Stretch = Stretch.Uniform, Cursor = Cursors.Hand, Margin = new Thickness(0, 2, 0, 4), ToolTip = L.T("Otwórz zdjęcie") };
                        var ip = m.Path; img.MouseLeftButtonUp += (s2, e2) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ip) { UseShellExecute = true }); } catch (Exception) { } };
                        body.Children.Add(img);
                    }
                    var txt = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = fg, FontSize = 14 };
                    // 🔗 adresy w wiadomości - kliknięcie otwiera kartę w Velivo
                    int at = 0;
                    foreach (System.Text.RegularExpressions.Match um in System.Text.RegularExpressions.Regex.Matches(hasFile ? "" : m.Text, @"(https?://|www\.)[^\s<>""]+[^\s<>"".,;:!?)\]]"))
                    {
                        txt.Inlines.Add(new System.Windows.Documents.Run(m.Text.Substring(at, um.Index - at)));
                        var u = um.Value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + um.Value : um.Value;
                        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(um.Value)) { Foreground = fg, Cursor = Cursors.Hand, ToolTip = L.T("Otwórz w Velivo") };
                        link.Click += (s2, e2) => { AddTab(u); Activate(); };
                        txt.Inlines.Add(link); at = um.Index + um.Length;
                    }
                    txt.Inlines.Add(new System.Windows.Documents.Run(m.Text.Substring(at)));
                    if (hasFile) { var fp = m.Path; txt.Cursor = Cursors.Hand; txt.MouseLeftButtonUp += (s2, e2) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fp) { UseShellExecute = true }); } catch (Exception) { } }; }
                    var copy = new MenuItem { Header = L.T("Kopiuj") }; var copyText = m.Text; copy.Click += (s2, e2) => { try { Clipboard.SetText(copyText); } catch (Exception) { } };
                    txt.ContextMenu = new ContextMenu { Items = { copy } };
                    if (hasFile)
                    {
                        var fp2 = m.Path;
                        var saveAs = new MenuItem { Header = L.T("💾 Zapisz jako…") };
                        saveAs.Click += (s2, e2) =>
                        {
                            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = System.IO.Path.GetFileName(fp2), Filter = (L.En ? "All files" : "Wszystkie pliki") + "|*.*", OverwritePrompt = true };
                            if (dlg.ShowDialog(w) == true) try { File.Copy(fp2, dlg.FileName, true); } catch (Exception ex) { KomInfo(ex.Message); }
                        };
                        var showIn = new MenuItem { Header = L.T("📁 Pokaż w folderze") }; showIn.Click += (s2, e2) => { try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + fp2 + "\""); } catch (Exception) { } };
                        txt.ContextMenu.Items.Add(saveAs); txt.ContextMenu.Items.Add(showIn);
                        foreach (var ch in body.Children.OfType<Image>()) ch.ContextMenu = txt.ContextMenu;
                    }
                    body.Children.Add(txt);
                    var stampTb = new TextBlock { FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right, Foreground = fg, ToolTip = m.Mine && !m.Ok ? L.T("Jeszcze nie doręczono – Velivo wyśle, gdy odbiorca będzie w sieci") : m.Mine && m.Read ? L.T("Przeczytane") : null };
                    // przeczytane (✓✓) na niebiesko - jak w telefonie
                    if (m.Mine && m.Read) { stampTb.Inlines.Add(new System.Windows.Documents.Run(stamp.Substring(0, stamp.Length - 3)) { Foreground = new SolidColorBrush(Color.FromArgb(0xB3, fg.Color.R, fg.Color.G, fg.Color.B)) }); stampTb.Inlines.Add(new System.Windows.Documents.Run(" ✓✓") { Foreground = new SolidColorBrush(pal.Key == "kontrast" ? Color.FromRgb(0, 0, 0xCC) : Color.FromRgb(0x7D, 0xF9, 0xFF)), FontWeight = FontWeights.Bold }); }
                    else { stampTb.Text = stamp; stampTb.Opacity = 0.7; }
                    body.Children.Add(stampTb);
                    msgs.Children.Add(new Border
                    {
                        Child = body, CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(m.Mine ? 60 : 0, 3, m.Mine ? 0 : 60, 3),
                        HorizontalAlignment = m.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                        Background = new SolidColorBrush(m.Mine ? pal.Mine : pal.Theirs)
                    });
                }
                scroll.ScrollToEnd();
                if (_komUnread.Remove(c.Id)) { KomSave(); FillList(); KomTrayUpdate(); }
                if (w.IsActive) KomSendRead(c);
            }
            // odswiezanie okna najwyzej co 0,4 s - seria pakietow (obecnosc, potwierdzenia) nie przebudowuje rozmowy setki razy
            bool refreshQueued = false;
            var refreshTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            refreshTimer.Tick += (s, e) => { refreshTimer.Stop(); refreshQueued = false; if (_komWindow == w) { FillList(); FillChat(); } };
            _komRefresh = () => Dispatcher.BeginInvoke(new Action(() => { if (refreshQueued) return; refreshQueued = true; refreshTimer.Start(); }));
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
                if (!KomOnline(c.Id) || !IPAddress.TryParse(c.Addr ?? "", out addr) || IPAddress.IsLoopback(addr)) { addr = (await KomFind()).FirstOrDefault(x => x.Id == c.Id).Addr; }
                if (addr == null && _komUp == null && !_komRemote.ContainsKey(c.Id)) { KomInfo(L.T("Tej osoby nie ma teraz w sieci")); return; }
                await KomSendFilesTo(w, c.Name, addr ?? IPAddress.Loopback, c);   // poza domem: tunel przez pośrednika
            }
            fileBtn.Click += async (s, e) => { var c = Current(); if (c != null) await AttachTo(c); };
            callBtn.Click += (s, e) => KomOpenCall(Current(), true);
            leftButtons.Children.Add(Btn("🎨", L.T("Motyw komunikatora – kolory okna i wiadomości"), (s, e) =>
            {
                var cm = new ContextMenu { PlacementTarget = (UIElement)s, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
                void Add(string key, string label) { var it = new MenuItem { Header = label, IsCheckable = true, IsChecked = _komTheme == key }; it.Click += (a, b) => { _komTheme = key; KomSave(); ApplyTheme(); FillList(); FillChat(); }; cm.Items.Add(it); }
                Add("auto", L.T("🖥 Automatyczny (jak Velivo)"));
                foreach (var p in KomPalettes) Add(p.Key, L.En ? p.En : p.Pl);
                cm.Items.Add(new Separator());
                var net = new MenuItem { Header = L.T("🌍 Rozmowy poza domem…") }; net.Click += (a, b) => KomRelayDialog(w); cm.Items.Add(net);
                var ai = new MenuItem { Header = L.T("🖼 Zdjęcia i pliki od kontaktów od razu w rozmowie (bez pytania)"), IsCheckable = true, IsChecked = _komAutoImages };
                ai.Click += (a, b) => { _komAutoImages = ai.IsChecked; KomSave(); };
                cm.Items.Add(ai);
                var nn = new MenuItem { Header = L.T("📶 Powiadamiaj o urządzeniach Velivo w sieci spoza kontaktów"), IsCheckable = true, IsChecked = _komNotifyNew };
                nn.Click += (a, b) => { _komNotifyNew = nn.IsChecked; KomSave(); };
                cm.Items.Add(nn);
                var snd = new MenuItem { Header = L.T("🔔 Dźwięk wiadomości…") };
                snd.Click += (a, b) =>
                {   // wybór z dźwięków Windows (albo dowolny .wav); Anuluj - dźwięk domyślny
                    var dlg = new Microsoft.Win32.OpenFileDialog { Title = L.T("🔔 Dźwięk wiadomości (Anuluj – dźwięk domyślny)"), Filter = L.T("Dźwięki") + " (*.wav)|*.wav",
                        InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media") };
                    _komSound = dlg.ShowDialog(w) == true ? dlg.FileName : ""; KomSave(); KomPlaySound();
                };
                cm.Items.Add(snd);
                cm.IsOpen = true;
            }));
            leftButtons.Children.Add(Btn(L.T("➕ Dodaj"), L.T("Dodaj kontakt: w tej sieci albo przez internet (zaproszenie)"), (s, e) =>
            {
                var cm = new ContextMenu { PlacementTarget = (UIElement)s, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
                var lan = new MenuItem { Header = L.T("📶 W tej sieci (z kodem)") }; lan.Click += async (a, b) => await KomAddDialog(w);
                var inv = new MenuItem { Header = L.T("✉ Zaproś przez internet…") }; inv.Click += (a, b) => KomInviteDialog(w);
                var join = new MenuItem { Header = L.T("🔑 Mam zaproszenie…") }; join.Click += (a, b) => KomJoinDialog(w);
                cm.Items.Add(lan); cm.Items.Add(inv); cm.Items.Add(join);
                cm.IsOpen = true;
            }));
            leftButtons.Children.Add(Btn(L.T("📎 Plik do…"), L.T("Wyślij pliki na telefon lub komputer z Velivo w tej sieci (bez parowania, z kodem)"), async (s, e) =>
            {
                var c = Current(); if (c != null) { await AttachTo(c); return; }   // zaznaczony kontakt - bez szukania i bez kodu
                var peer = await KomChoosePeer(w, L.T("📎 Wyślij pliki do…")); if (peer.Addr != null) await KomSendFilesTo(w, peer.Name, peer.Addr, KomContactById(peer.Id));
            }));
            // okno na wierzchu: rozmowa widoczna - "przeczytane" (✓✓) dla wszystkiego, co w niej czeka (takze plikow)
            w.Activated += (s, e) => { if (_komOpenId == null) return; if (_komUnread.ContainsKey(_komOpenId)) FillChat(); var oc = Current(); if (oc != null) KomSendRead(oc); };
            // realny stan kontaktow: pytanie "kto jest w sieci" od razu po otwarciu i co 30 s, dopoki okno otwarte
            void Ping() { var who = KomPacket("p2p-who"); var here = KomPacket("p2p-here"); _ = Task.Run(async () => { KomSendUdp(here, null); for (int i = 0; i < 2; i++) { KomSendUdp(who, null); await Task.Delay(700); } }); }
            var presence = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            presence.Tick += (s, e) => { Ping(); FillList(); FillChat(); };
            presence.Start();
            Ping();
            _ = Task.Delay(2500).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() => { if (_komWindow == w) { FillList(); FillChat(); } })));
            w.SourceInitialized += (s, e) => { SetWindowAppId(w, "Velivo.Komunikator"); ApplyTheme(); };
            // ✕ - do zasobnika (ikona komunikatora), komunikator dalej odbiera; zamyka sie naprawde tylko z Velivo
            w.Closing += (s, e) => { if (_komExit) return; e.Cancel = true; w.Hide(); KomEnsureTray(); };
            w.Closed += (s, e) => { presence.Stop(); _komWindow = null; _komRefresh = null; _komSelect = null; _komStatus = null; };
            _komWindow = w;
            if (openId != null) _komOpenId = openId;
            FillList(); FillChat();
            w.Show(); w.Activate();
            input.Focus();
        }
        Action<string> _komSelect;
        Action<string, double> _komStatus;   // pasek stanu w oknie komunikatora: tekst, postep 0..1 (albo -1 - bez paska)

        // informacja w komunikatorze (okno otwarte - jego pasek stanu; schowane - dymek). Dymek przegladarki
        // wyciagal okno przegladarki na wierzch i zaslanial komunikator
        void KomInfo(string text, double progress = -1)
        {
            if (_komWindow != null && _komWindow.IsVisible && _komStatus != null) _komStatus(text, progress);
            else if (progress < 0) ShowToast(text, null);
        }

        // 🎬 Jellyfin i listy TV do kontaktu przez komunikator (w domu i przez internet); telefon w domu zapisuje od razu, przez internet pyta
        async Task KomSendMedia(Window owner, KomContact c)
        {
            var jc = LoadJellyfinConf(); var tv = MediaTvLists();
            if (jc == null && tv.Count == 0) { KomInfo(L.T("Na tym komputerze nie ma ustawionego Jellyfin ani list TV.")); return; }
            var ask = (L.En ? "Send to " : "Wysłać do: ") + c.Name + "\n\n" + (jc != null ? "• Jellyfin (" + jc.Url + ")\n" : "") + (tv.Count > 0 ? L.T("• listy kanałów TV: ") + tv.Count + "\n" : "") +
                      L.T("\nAdresy mogą zawierać Twoje prywatne klucze – idą zaszyfrowane, tylko do tej osoby.");
            if (MessageBox.Show(owner, ask, L.T("🎬 Wyślij Filmy i TV"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var payload = new JsonObject { ["tv"] = new JsonArray(tv.Select(d => (JsonNode)new JsonObject(d.Select(kv => new KeyValuePair<string, JsonNode>(kv.Key, kv.Value)))).ToArray()) };
            if (jc != null) payload["jellyfin"] = new JsonObject { ["url"] = jc.Url, ["key"] = jc.Key };
            var path = Path.Combine(Path.GetTempPath(), "Velivo-Filmy-TV.json");
            File.WriteAllText(path, payload.ToJsonString());
            try
            {
                IPAddress addr = null;
                if (!KomOnline(c.Id) || !IPAddress.TryParse(c.Addr ?? "", out addr) || IPAddress.IsLoopback(addr)) addr = (await KomFind()).FirstOrDefault(x => x.Id == c.Id).Addr;
                if (addr == null && _komUp == null && !_komRemote.ContainsKey(c.Id)) { KomInfo(L.T("Tej osoby nie ma teraz w sieci")); return; }
                KomInfo(L.T("🎬 Wysyłam Filmy i TV…"), 0);
                var r = await KomSendFile(addr ?? IPAddress.Loopback, path, _ => { }, c);
                KomInfo(r == "ok" ? L.T("🎬 Wysłano Filmy i TV do: ") + c.Name : L.T("🎬 Nie wysłano: ") + r);
                if (r == "ok") KomNote(c, true, L.T("🎬 Filmy i TV"));
            }
            finally { try { File.Delete(path); } catch (Exception) { } }
        }

        void KomRename(KomContact c)
        {
            var box = new TextBox { Text = c.Name, Margin = new Thickness(10, 0, 10, 10), MinWidth = 260, ToolTip = L.T("Pusta nazwa – wraca nazwa urządzenia") };
            var real = new TextBlock { Text = L.T("Urządzenie: ") + (c.Device ?? c.Name) + L.T("  (puste pole – nazwa urządzenia)"), Margin = new Thickness(10, 10, 10, 6), Opacity = 0.75 };
            var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(10, 0, 10, 10), HorizontalAlignment = HorizontalAlignment.Right };
            var d = new Window { Title = L.T("✏ Zmień nazwę"), SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = _komWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new StackPanel { Children = { real, box, ok } } };
            KomStyle(d);
            ok.Click += (s, e) => d.DialogResult = true;
            box.SelectAll(); box.Focus();
            if (d.ShowDialog() != true) return;
            // wlasna nazwa kontaktu; pusta albo taka jak urzadzenia - znow nazwa urzadzenia (aktualizowana z sieci)
            var nn = box.Text.Trim(); if (nn.Length > 64) nn = nn.Substring(0, 64);
            if (nn.Length == 0 || nn == c.Device) { c.Own = false; c.Name = c.Device ?? c.Name; } else { c.Own = true; c.Name = nn; }
            KomSave(); _komRefresh?.Invoke();
        }

        async Task<(string Id, string Name, IPAddress Addr)> KomChoosePeer(Window owner, string title)
        {
            var wait = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new TextBlock { Text = L.T("Szukam urządzeń z Velivo w tej sieci…"), Margin = new Thickness(24) } };
            KomStyle(wait);
            wait.Show();
            var found = await KomFind();
            wait.Close();
            if (found.Count == 0) { MessageBox.Show(owner, L.T("Nie znaleziono. Na drugim urządzeniu otwórz komunikator Velivo (albo przeglądarkę Velivo) w tej samej sieci i spróbuj ponownie."), title); return default; }
            var lb = new ListBox { Margin = new Thickness(10), MinWidth = 280, MinHeight = 120 };
            foreach (var f in found) lb.Items.Add((KomContactById(f.Id) != null ? "👤 " : "📱 ") + f.Name);
            lb.SelectedIndex = 0;
            var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(10, 0, 10, 10), HorizontalAlignment = HorizontalAlignment.Right };
            var d = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new StackPanel { Children = { lb, ok } } };
            KomStyle(d);
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
            KomStyle(w);
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
            KomInfo(r == "ok" ? L.T("Dodano: ") + p.Name : L.T("Nie dodano: ") + r);
            if (r == "ok") _komSelect?.Invoke(p.Id);
        }

        async Task KomSendFilesTo(Window owner, string name, IPAddress addr, KomContact to = null)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = (L.En ? "Send files to " : "Wyślij pliki do: ") + name, Multiselect = true };
            if (dlg.ShowDialog(owner) != true) return;
            foreach (var f in dlg.FileNames)
            {
                Window codeWin = null;
                var fn = Path.GetFileName(f); var fmid = Guid.NewGuid().ToString("N"); long fsize = 0; try { fsize = new FileInfo(f).Length; } catch (Exception) { }
                KomInfo("📎 " + fn + L.T(" – czekam, aż odbiorca przyjmie…"), 0);
                // wpis w rozmowie od razu („…”): potwierdzenie przeczytania od odbiorcy moze przyjsc, zanim tu skonczymy
                KomMsg pend = null;
                if (to != null) { pend = new KomMsg { Mine = true, Text = "📎 " + fn + " · " + KomSize(fsize), Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Ok = false, Mid = fmid, File = true, Path = f }; KomChat(to.Id).Add(pend); KomSave(); _komRefresh?.Invoke(); }
                var r = await KomSendFile(addr, f, code => Dispatcher.Invoke(() => codeWin = KomCodeWindow(owner, "📎 " + fn + " → " + name, code)), to,
                    (done, all) => Dispatcher.BeginInvoke(new Action(() => KomInfo("📎 " + fn + " → " + name + "  " + (all > 0 ? (int)(done * 100 / all) : 100) + " %  (" + KomSize(done) + L.T(" z ") + KomSize(all) + ")", all > 0 ? (double)done / all : 1))), fmid);
                codeWin?.Close();
                if (pend != null)
                {
                    if (r == "ok") { pend.Ok = true; pend.Text += L.T(" – odebrano"); } else KomChat(to.Id).Remove(pend);   // odrzucony / blad - bez wpisu
                    KomSave(); _komRefresh?.Invoke();
                }
                KomInfo("📎 " + fn + ": " + (r == "ok" ? L.T("wysłano") : r));
                try { if (owner.IsVisible) owner.Activate(); } catch (Exception) { }   // rozmowa dalej na wierzchu
            }
        }

        // 💬 wlasna ikona komunikatora w zasobniku (jak w innych komunikatorach): ✕ chowa okno tutaj, komunikator dalej odbiera wiadomosci
        System.Windows.Forms.NotifyIcon _komTray;
        static string KomIconFile => Path.Combine(AppContext.BaseDirectory, "komunikator.ico");
        static System.Windows.Media.ImageSource KomWindowIcon()
        {
            try { if (File.Exists(KomIconFile)) return System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(KomIconFile)); } catch (Exception) { }
            try { return new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/komunikator.ico")); } catch (Exception) { return null; }
        }
        bool _komExit;   // prawdziwe zamkniecie (wyjscie z Velivo), nie chowanie do zasobnika

        void KomEnsureTray()
        {
            if (_komTray != null) { KomTrayUpdate(); return; }
            // ta sama ikona co skrot na pulpicie (plik obok Velivo.exe), w rozmiarze zasobnika - nie ikona przegladarki
            System.Drawing.Icon icon = null;
            var size = System.Windows.Forms.SystemInformation.SmallIconSize;
            try { if (File.Exists(KomIconFile)) icon = new System.Drawing.Icon(KomIconFile, size); } catch (Exception ex) { App.LogError(ex); }
            if (icon == null) try { using (var st = Application.GetResourceStream(new Uri("pack://application:,,,/komunikator.ico")).Stream) icon = new System.Drawing.Icon(st, size); } catch (Exception ex) { App.LogError(ex); }
            if (icon == null) icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
            _komTray = new System.Windows.Forms.NotifyIcon { Icon = icon, Visible = true };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add(L.T("💬 Otwórz komunikator"), null, (s, e) => Dispatcher.Invoke(() => ShowMessenger()));
            menu.Items.Add(L.T("➕ Dodaj kontakt"), null, (s, e) => Dispatcher.Invoke(async () => { ShowMessenger(); if (_komWindow != null) await KomAddDialog(_komWindow); }));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(L.T("🌐 Uruchom przeglądarkę"), null, (s, e) => Dispatcher.Invoke(ShowFromTray));
            menu.Items.Add(L.T("Zamknij Velivo"), null, (s, e) => Dispatcher.Invoke(ExitFromTray));
            _komTray.ContextMenuStrip = menu;
            _komTray.MouseClick += (s, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) Dispatcher.Invoke(() => ShowMessenger()); };
            KomTrayUpdate();
        }

        // podpowiedz ikony: liczba nieprzeczytanych
        void KomTrayUpdate()
        {
            if (_komTray == null) return;
            int n = _komUnread.Values.Sum();
            var t = L.T("Komunikator Velivo") + (n > 0 ? " – " + n + L.T(" nowe") : "");
            _komTray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        void KomTrayRemove()
        {
            try { if (_komTray != null) { _komTray.Visible = false; _komTray.Dispose(); _komTray = null; } } catch (Exception) { }
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
