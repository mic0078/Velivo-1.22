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
        sealed class KomContact { public string Id; public string Name; public byte[] Key; public string Addr; public string Device; public bool Own; public string Relay; public bool Dom; public bool IsGroup => Key.Length == 0; }   // Dom: moje urządzenie z domu (sparowane kodem w sieci domowej albo przedstawione jako domowe przez mój serwer) - tylko takim serwerom ufa wymiana kontaktów   // 👥 grupa: bez klucza, członkowie w Addr („grupa:id,id”)   // Relay: adres pośrednika tego kontaktu (No-IP:port) - rozmowy poza domem
        sealed class KomMsg { public bool Mine; public string Text; public long Ts; public bool Ok; public string Mid; public bool Read; public bool File; public string Path; public string Re = ""; public string Lp; }   // Lp: 🔗 podgląd linku (JSON: u, t, d, s)   // Read: moja - przeczytana (✓✓), odebrana - wyslano potwierdzenie; File: wpis o pliku (nie wysylany ponownie jako tekst)

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
            var noDom = new HashSet<string>();
            if (_komLoaded) return;
            _komLoaded = true;
            Dispatcher.BeginInvoke(new Action(KomClipWatch), System.Windows.Threading.DispatcherPriority.ApplicationIdle);   // 📋 wspólny schowek - gdy włączony
            try
            {
                if (File.Exists(KomFile))
                {
                    var o = JsonNode.Parse(ProtectedData.Unprotect(File.ReadAllBytes(KomFile), KomEntropy, DataProtectionScope.CurrentUser));
                    _komId = (string)o["id"];
                    _komTheme = (string)o["theme"] ?? "auto";
                    _komAutoImages = (bool?)o["autoimg"] ?? true;
                    _komNotifyNew = (bool?)o["notifynew"] ?? true; _komSound = (string)o["sound"] ?? "";
                    _komLock = (bool?)o["lock"] ?? false; _komPhotoV = (long?)o["photov"] ?? 0;
                    if (o["opt"] is JsonObject op) _komOpt = (JsonObject)op.DeepClone();
                    if (o["seen"] is JsonObject se) foreach (var kv in se) if (DateTime.TryParse((string)kv.Value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var sd)) _komSeenAt[kv.Key] = sd;
                    if (o["photosent"] is JsonObject ps) foreach (var kv in ps) _komPhotoSent[kv.Key] = (long?)kv.Value ?? 0;
                    if (o["asks"] is JsonObject ak) foreach (var kv in ak) if (kv.Value != null) _komAskKeys[kv.Key] = (string)kv.Value;
                    if (o["askReq"] is JsonObject aq) foreach (var kv in aq) if (kv.Value is JsonObject v) _komAskReq[kv.Key] = (JsonObject)v.DeepClone();
                    if (o["askRes"] is JsonObject ar) foreach (var kv in ar) if (kv.Value is JsonObject v) _komAskRes[kv.Key] = (JsonObject)v.DeepClone();
                    foreach (var iv in o["invites"]?.AsArray() ?? new JsonArray())   // ✉ zaproszenia czekajace na pośredniku
                    {
                        var ik = KomB64((string)iv["k"]); long ie = (long?)iv["exp"] ?? 0;
                        if (ik.Length == 32 && ie > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) _komInvites[KomInviteId(ik)] = new KomInvite { K = ik, By = (string)iv["by"], Exp = ie };
                    }
                    _komRelayOn = (bool?)o["relayOn"] ?? false; _komRelayAuto = (bool?)o["relayAuto"] ?? true; _komRelayAsked = (bool?)o["relayAsked"] ?? false; _komRelayHost = (string)o["relayHost"] ?? ""; _komRelayPort = (int?)o["relayPort"] ?? 41920; _komRelayPortFixed = (bool?)o["relayPortFixed"] ?? false;
                    // kazdy wpis osobno - jeden uszkodzony nie kasuje reszty kontaktow i rozmow
                    foreach (var c in o["contacts"]?.AsArray() ?? new JsonArray())
                        try
                        {
                            var k = Convert.FromBase64String((string)c["key"] ?? "");
                            if (k.Length == 32 || k.Length == 0 && ((string)c["addr"] ?? "").StartsWith("grupa:", StringComparison.Ordinal)) _komContacts.Add(new KomContact { Id = (string)c["id"], Name = (string)c["name"], Key = k, Addr = (string)c["addr"] ?? "", Device = (string)c["dev"] ?? (string)c["name"], Own = (bool?)c["own"] ?? false, Relay = (string)c["relay"], Dom = (bool?)c["dom"] ?? false }); if (c["dom"] == null) noDom.Add((string)c["id"]);
                        }
                        catch (Exception ex) { App.LogError(ex); }
                    // starsze dane (bez „dom”): domowe = poznane w mojej sieci domowej (ma adres w Wi-Fi)
                    foreach (var c in _komContacts) if (noDom.Contains(c.Id)) c.Dom = !string.IsNullOrEmpty(c.Addr) && !c.Addr.StartsWith("grupa:", StringComparison.Ordinal);
                    foreach (var kv in o["chats"]?.AsObject() ?? new JsonObject())
                        try
                        {
                            var l = new List<KomMsg>();
                            foreach (var m in kv.Value.AsArray())
                                try { l.Add(new KomMsg { Mine = (bool?)m["me"] ?? false, Text = (string)m["t"] ?? "", Ts = (long?)m["ts"] ?? 0, Ok = (bool?)m["ok"] ?? true, Mid = (string)m["mid"] ?? "", Read = (bool?)m["rd"] ?? false, File = (bool?)m["f"] ?? false, Path = (string)m["p"], Re = (string)m["re"] ?? "", Lp = (string)m["lp"] }); }
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

        // zapis zbiorczy: seria pakietów (np. 100 zaległych wiadomości od pośrednika) = jeden zapis, a nie 100 pełnych zapisów w wątku okna
        volatile bool _komSavePending;
        void KomSave()
        {
            if (_komSavePending) return;
            _komSavePending = true;
            Dispatcher.BeginInvoke(new Action(() => { if (_komSavePending) KomSaveNow(); }), System.Windows.Threading.DispatcherPriority.Background);
        }
        void KomSaveNow()
        {
            _komSavePending = false;
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllBytes(KomFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(KomState().ToJsonString()), KomEntropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) { App.LogError(ex); }
        }
        JsonObject KomState()
        {
                foreach (var kv in _komChats) KomPrune(kv.Key, kv.Value);   // ⏳ znikające wiadomości
                var chats = new JsonObject();
                foreach (var kv in _komChats)
                {
                    var a = new JsonArray();
                    foreach (var m in kv.Value.Skip(Math.Max(0, kv.Value.Count - 500))) a.Add(new JsonObject { ["me"] = m.Mine, ["t"] = m.Text, ["ts"] = m.Ts, ["ok"] = m.Ok, ["mid"] = m.Mid, ["rd"] = m.Read, ["f"] = m.File, ["p"] = m.Path, ["re"] = m.Re, ["lp"] = m.Lp });
                    chats[kv.Key] = a;
                }
                var unread = new JsonObject(); foreach (var kv in _komUnread) unread[kv.Key] = kv.Value;
                var o = new JsonObject
                {
                    ["id"] = _komId,
                    ["contacts"] = new JsonArray(_komContacts.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["name"] = c.Name, ["key"] = Convert.ToBase64String(c.Key), ["addr"] = c.Addr, ["dev"] = c.Device, ["own"] = c.Own, ["relay"] = c.Relay, ["dom"] = c.Dom }).ToArray()),
                    ["chats"] = chats, ["unread"] = unread, ["theme"] = _komTheme, ["autoimg"] = _komAutoImages, ["notifynew"] = _komNotifyNew, ["sound"] = _komSound, ["lock"] = _komLock, ["photov"] = _komPhotoV, ["photosent"] = new JsonObject(_komPhotoSent.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode)kv.Value))), ["asks"] = new JsonObject(_komAskKeys.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode)kv.Value))),
                    ["askReq"] = new JsonObject(_komAskReq.Select(kv => KeyValuePair.Create(kv.Key, kv.Value.DeepClone()))), ["askRes"] = new JsonObject(_komAskRes.Select(kv => KeyValuePair.Create(kv.Key, kv.Value.DeepClone()))), ["relayOn"] = _komRelayOn, ["relayAuto"] = _komRelayAuto, ["relayAsked"] = _komRelayAsked, ["invites"] = new JsonArray(_komInvites.Values.Where(v => v.Exp > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Select(v => (JsonNode)new JsonObject { ["k"] = Convert.ToBase64String(v.K), ["by"] = v.By, ["exp"] = v.Exp }).ToArray()), ["relayHost"] = _komRelayHost, ["relayPort"] = _komRelayPort, ["relayPortFixed"] = _komRelayPortFixed, ["opt"] = _komOpt.DeepClone(), ["seen"] = new JsonObject(_komSeenAt.ToArray().Select(kv => KeyValuePair.Create(kv.Key, (JsonNode)kv.Value.ToString("o"))))   // „ostatnio w sieci” zostaje po ponownym uruchomieniu
                };
                return o;
        }

        // 📌 przypięte, 🔕 wyciszone (do kiedy), ⏳ znikające (godziny), ✏ szkice, „Mój adres”, ⏩ głosówki - ustawienia (jak w telefonie)
        JsonObject _komOpt = new JsonObject();
        bool KomPinned(string id) => (bool?)_komOpt["pin-" + id] == true;
        // 📋 wspólny schowek z wybranymi urządzeniami (np. mój telefon): skopiowany tekst idzie szyfrowanym komunikatorem (w domu i przez internet),
        // tekst od nich trafia do schowka Windows; bez haseł (menedżery haseł, sejf i bank Velivo)
        const string KomClipTag = "\u0001velivo-schowek\n";
        bool KomClipOn(string id) => (bool?)_komOpt["schowek-" + id] == true;
        internal static string ClipSecret;   // hasło skopiowane w Velivo (sejf, bank) - nie idzie do innych urządzeń
        string _komClipLast; bool _komClipHooked; System.Windows.Threading.DispatcherTimer _komClipTimer;
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
        void KomClipWatch()
        {
            if (_komClipHooked || !_komContacts.Any(c => KomClipOn(c.Id))) return;
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle; if (h == IntPtr.Zero) return;
            _komClipHooked = true;
            _komClipTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };   // seria zmian schowka - jedna wysyłka
            _komClipTimer.Tick += (s, e) => { _komClipTimer.Stop(); KomClipSend(); };
            System.Windows.Interop.HwndSource.FromHwnd(h).AddHook((IntPtr hw, int msg, IntPtr wp, IntPtr lp, ref bool handled) => { if (msg == 0x031D) { _komClipTimer.Stop(); _komClipTimer.Start(); } return IntPtr.Zero; });   // WM_CLIPBOARDUPDATE
            AddClipboardFormatListener(h);
        }
        void KomClipSend()
        {
            var to = _komContacts.Where(c => !c.IsGroup && KomClipOn(c.Id)).ToList(); if (to.Count == 0) return;
            string t;
            try
            {
                if (!Clipboard.ContainsText() || Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing") || Clipboard.ContainsData("Clipboard Viewer Ignore")) return;   // menedżery haseł oznaczają hasła
                t = Clipboard.GetText();
            }
            catch (Exception) { return; }
            if (string.IsNullOrWhiteSpace(t) || t.Length > 20000 || t == _komClipLast || t == ClipSecret) return;
            _komClipLast = t;
            foreach (var c in to) KomDeliver(c, new KomMsg { Mine = true, Text = KomClipTag + t, Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mid = Guid.NewGuid().ToString("N") });   // poza rozmową
        }
        void KomSetPinned(string id, bool on) { _komOpt["pin-" + id] = on; KomSave(); _komRefresh?.Invoke(); }
        bool KomMuted(string id) => ((long?)_komOpt["cisza-" + id] ?? 0) > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        void KomMute(string id, long ms) { _komOpt["cisza-" + id] = ms <= 0 ? 0 : ms == long.MaxValue ? ms : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ms; KomSave(); _komRefresh?.Invoke(); }
        string KomDraft(string id) => id == null ? "" : (string)_komOpt["szkic-" + id] ?? "";
        void KomSetDraft(string id, string s) { if (id != null) _komOpt["szkic-" + id] = s; }
        int KomTtl(string id) => (int?)_komOpt["znikaj-" + id] ?? 0;
        void KomPrune(string id, List<KomMsg> l)
        {
            var h = KomTtl(id); if (h <= 0) return;
            long old = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - h * 3600000L;
            foreach (var m in l.Where(x => x.Ts < old).ToList())
            {
                l.Remove(m);
                if (m.Path != null && m.Path.StartsWith(KomMediaDir, StringComparison.OrdinalIgnoreCase)) try { File.Delete(m.Path); } catch (Exception) { }   // kopia w Velivo (nie plik użytkownika)
            }
        }
        // ⏳ znikające wiadomości (jak w WhatsApp): czas ustawiony w rozmowie dotyczy obu stron
        const string KomTimerTag = "\u0001velivo-znikanie\n";
        void KomSetTtl(KomContact c, int h) { KomApplyTtl(c.Id, h, null); KomTagOut(c, KomTimerTag, new JsonObject { ["h"] = h }); }
        void KomApplyTtl(string id, int h, string who)
        {
            _komOpt["znikaj-" + id] = h;
            var what = h <= 0 ? L.T("wyłączone") : h % 24 == 0 ? h / 24 + " " + L.T("d.") : h + " h";
            KomChat(id).Add(new KomMsg { Mine = who == null, Text = "⏳ " + (who != null ? who + ": " : "") + L.T("znikające wiadomości – ") + what, Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Ok = true, Mid = Guid.NewGuid().ToString("N") });
            KomSave(); _komRefresh?.Invoke();
        }
        void KomTtlIn(KomContact from, string body)
        {
            JsonNode j; try { j = JsonNode.Parse(body); } catch (Exception) { return; }
            KomApplyTtl(KomChatOf(from, j), Math.Clamp((int?)j?["h"] ?? 0, 0, 24 * 365), from.Name);
        }
        // 🛡 kod bezpieczeństwa: ten sam na obu urządzeniach (z klucza rozmowy) - zgodny = nikt się nie podszywa
        static string KomSafety(KomContact c)
        {
            var h = SHA256.HashData(Encoding.UTF8.GetBytes("velivo-kod\n").Concat(c.Key).ToArray());
            return string.Join(" ", Enumerable.Range(0, 6).Select(i => (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(i * 4)) % 100000).ToString("D5")));
        }

        // 💾 kopia zapasowa (jak w telefonie): kontakty z kluczami, rozmowy, identyfikator i ustawienia - zaszyfrowane hasłem (PBKDF2 + AES-GCM),
        // bez plików; zapis w dowolnym folderze (także Dysk Google, NUCBOX). Przywrócenie na nowym komputerze - kontakty dalej nas znają.
        static byte[] KomPassKey(string p, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(p), salt, 200000, HashAlgorithmName.SHA256, 32);
        // 🔑 kopia 2: dane zaszyfrowane losowym kluczem, a ten zamknięty osobno hasłem i każdym kluczem sprzętowym (sekret hmac-secret) -
        // otwiera hasło ALBO dowolny z kluczy (zgubiony klucz - zostaje hasło i zapasowy klucz)
        static byte[] KomGcm(byte[] k, byte[] p) { var n = RandomNumberGenerator.GetBytes(12); var ct = new byte[p.Length]; var tag = new byte[16]; using (var g = new AesGcm(k, 16)) g.Encrypt(n, p, ct, tag); return n.Concat(ct).Concat(tag).ToArray(); }
        static byte[] KomUngcm(byte[] k, byte[] b) { var p = new byte[b.Length - 28]; using (var g = new AesGcm(k, 16)) g.Decrypt(b.AsSpan(0, 12), b.AsSpan(12, p.Length), b.AsSpan(b.Length - 16), p); return p; }
        static byte[] KomKeyKek(byte[] secret) => SHA256.HashData(Encoding.UTF8.GetBytes("velivo-kopia\n").Concat(secret).ToArray());
        byte[] KomBackup(string pass, byte[] hsalt = null, List<(BankKey Key, byte[] Secret)> keys = null)
        {
            var st = KomState(); if (KomPhoto(_komId) is string ph) st["foto"] = Convert.ToBase64String(File.ReadAllBytes(ph));
            var dk = RandomNumberGenerator.GetBytes(32); var salt = RandomNumberGenerator.GetBytes(16);
            var h = new JsonObject { ["s"] = Convert.ToBase64String(salt), ["p"] = Convert.ToBase64String(KomGcm(KomPassKey(pass, salt), dk)) };
            if (hsalt != null && keys?.Count > 0) { h["hs"] = Convert.ToBase64String(hsalt); h["k"] = new JsonArray(keys.Select(k => (JsonNode)new JsonObject { ["c"] = k.Key.Id, ["x"] = k.Key.X, ["y"] = k.Key.Y, ["w"] = Convert.ToBase64String(KomGcm(KomKeyKek(k.Secret), dk)) }).ToArray()); }
            return Encoding.UTF8.GetBytes("VELIVO-KOPIA2\n" + h.ToJsonString() + "\n" + Convert.ToBase64String(KomGcm(dk, Encoding.UTF8.GetBytes(st.ToJsonString()))));
        }
        // klucze w kopii (sól i klucze publiczne) - null, gdy kopia tylko na hasło
        static (byte[] Salt, List<BankKey> Keys)? KomBackupKeys(byte[] data)
        {
            try
            {
                var l = Encoding.UTF8.GetString(data).Split('\n'); if (l[0] != "VELIVO-KOPIA2" || JsonNode.Parse(l[1]) is not JsonObject h || h["k"] is not JsonArray k) return null;
                return (Convert.FromBase64String((string)h["hs"]), k.Select(x => new BankKey { Id = (string)x["c"], X = (string)x["x"], Y = (string)x["y"] }).ToList());
            }
            catch (Exception) { return null; }
        }
        // przywrócenie hasłem albo kluczem (key - który klucz i jego sekret)
        bool KomRestore(byte[] data, string pass, (BankKey Key, byte[] Secret)? key = null)
        {
            try
            {
                var txt = Encoding.UTF8.GetString(data); byte[] plain;
                if (txt.StartsWith("VELIVO-KOPIA2\n", StringComparison.Ordinal))
                {
                    var l = txt.Split('\n'); var h = (JsonObject)JsonNode.Parse(l[1]);
                    var dk = key is { } kk ? KomUngcm(KomKeyKek(kk.Secret), Convert.FromBase64String((string)((JsonArray)h["k"]).First(x => (string)x["c"] == kk.Key.Id)["w"]))
                        : KomUngcm(KomPassKey(pass, Convert.FromBase64String((string)h["s"])), Convert.FromBase64String((string)h["p"]));
                    plain = KomUngcm(dk, Convert.FromBase64String(l[2].Trim()));
                }
                else
                {
                    if (!txt.StartsWith("VELIVO-KOPIA1\n", StringComparison.Ordinal) || pass == null) return false;
                    var b = Convert.FromBase64String(txt.Substring(14).Trim()); if (b.Length <= 44) return false;
                    plain = new byte[b.Length - 44];
                    using (var g = new AesGcm(KomPassKey(pass, b[..16]), 16)) g.Decrypt(b.AsSpan(16, 12), b.AsSpan(28, plain.Length), b.AsSpan(b.Length - 16), plain);
                }
                var o = JsonNode.Parse(plain) as JsonObject; if (((string)o?["id"])?.Length != 32) return false;   // kopia z Windows
                if (File.Exists(KomFile)) File.Copy(KomFile, KomFile + ".przed-kopia-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
                File.WriteAllBytes(KomFile, ProtectedData.Protect(plain, KomEntropy, DataProtectionScope.CurrentUser));
                _komContacts.Clear(); _komChats.Clear(); _komUnread.Clear(); _komPhotoSent.Clear(); _komAskKeys.Clear(); _komAskReq.Clear(); _komAskRes.Clear(); _komInvites.Clear(); _komOpt = new JsonObject();
                _komLoaded = false; KomLoad();
                if ((string)o["foto"] is { Length: > 0 } f) { Directory.CreateDirectory(KomMediaDir); File.WriteAllBytes(Path.Combine(KomMediaDir, "profil-" + _komId + ".jpg"), Convert.FromBase64String(f)); _komPhotoV = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); KomSave(); }
                _komRefresh?.Invoke(); KomTrayUpdate(); KomAnnounce();
                return true;
            }
            catch (Exception) { return false; }
        }

        List<KomMsg> KomChat(string id) { if (!_komChats.TryGetValue(id, out var l)) _komChats[id] = l = new List<KomMsg>(); return l; }
        KomContact KomContactById(string id) => _komContacts.FirstOrDefault(c => c.Id == id);

        // ---------- gniazda ----------
        // 💬 komunikator ma własny port w sieci domowej (osobna aplikacja Komunikator w telefonie); 41919 - dom (synchronizacja),
        // tam też dalej przychodzą pakiety od starszego Velivo (LanListenLoop i odbiornik TCP LAN)
        const int KomPort = 41918;
        // TCP do urządzenia w domu: nowe Velivo - port komunikatora, starsze - 41919 (3 s, bo zapora może milczeć)
        static async Task<TcpClient> KomTcpConnect(IPAddress a, CancellationToken token)
        {
            var c = new TcpClient();
            try { using (var t = CancellationTokenSource.CreateLinkedTokenSource(token)) { t.CancelAfter(3000); await c.ConnectAsync(a, KomPort, t.Token); } return c; }
            catch (Exception) when (!token.IsCancellationRequested) { c.Dispose(); }
            c = new TcpClient();
            try { await c.ConnectAsync(a, LanPort, token); return c; } catch (Exception) { c.Dispose(); throw; }
        }
        // 41918 zawsze; 41919 też, gdy synchronizacja LAN go nie zajmuje (starsze Velivo i zapora przepuszczająca tylko 41919 - NUCBOX)
        void KomStartOwn(bool old = true)
        {
            KomLoad();
            KomStartResend();
            if (_komContacts.Count > 0) KomEnsureTray();
            try
            {
                _komCts ??= new CancellationTokenSource();
                if (_komUdp == null) { (_komUdp, _komTcp) = KomListen(KomPort, _komCts.Token); if (_komContacts.Count > 0) KomAnnounce(); }
                if (old && _lanRx == null && _komUdpOld == null) (_komUdpOld, _komTcpOld) = KomListen(LanPort, _komCts.Token);
            }
            catch (Exception ex) { App.LogError(ex); }
        }
        UdpClient _komUdpOld; TcpListener _komTcpOld;
        void KomStopOld() { try { _komUdpOld?.Dispose(); } catch (Exception) { } try { _komTcpOld?.Stop(); } catch (Exception) { } _komUdpOld = null; _komTcpOld = null; }   // port 41919 dla synchronizacji LAN
        (UdpClient, TcpListener) KomListen(int port, CancellationToken token)
        {
            var u = new UdpClient(AddressFamily.InterNetwork);
            u.Client.ExclusiveAddressUse = false;
            u.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            u.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            u.EnableBroadcast = true;
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
            var t = new TcpListener(IPAddress.Any, port);
            try { t.Start(); } catch (Exception) { u.Dispose(); throw; }
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
            return (u, t);
        }

        void KomStopOwn()
        {
            try { _komCts?.Cancel(); } catch (Exception) { }
            try { _komUdp?.Dispose(); } catch (Exception) { }
            try { _komTcp?.Stop(); } catch (Exception) { }
            KomStopOld();
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
            foreach (var t in targets) foreach (var pt in new[] { KomPort, LanPort }) try { s.Send(d, d.Length, new IPEndPoint(t, pt)); } catch (Exception) { }   // nowe i starsze Velivo
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
        readonly Dictionary<string, DateTime> _komTypingAt = new Dictionary<string, DateTime>();   // ✍ „pisze…” od kontaktu
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
            var c = KomContactById(from);
            // stara wiadomość (czekała u nadawcy albo na pośredniku) - przyjęta, bez zmiany obecności; inne stare pakiety - pominięte
            if (Math.Abs(ts - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) > 600000) { if (t == "p2p-msg") KomReceive(p, c, sender, ts); return; }
            if (t == "p2p-bye") { if (c != null && _komLastSeen.Remove(c.Id)) _komRefresh?.Invoke(); return; }   // zamknal Velivo - od razu poza siecia
            if (c == null && (bool?)p["fwd"] != true && !IPAddress.IsLoopback(sender) && (t == "p2p-here" || t == "p2p-who")) KomNotifyNew(from, KomClean((string)p["device"]));
            if (c != null)
            {
                bool was = KomOnline(c.Id); _komLastSeen[c.Id] = DateTime.UtcNow; _komSeenAt[c.Id] = DateTime.Now;
                if (t == "p2p-here" && (bool?)p["fwd"] != true && !IPAddress.IsLoopback(sender) && c.Addr != sender.ToString()) { c.Addr = sender.ToString(); KomSave(); }   // aktualny adres - pliki bez ponownego szukania
                if ((bool?)p["fwd"] == true && c.Addr == sender.ToString()) { c.Addr = ""; KomSave(); }   // zapisany adres to pośrednik (NUCBOX), nie kontakt - kasowany
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
                case "p2p-typing": if (c != null && (string)p["to"] == _komId) { _komTypingAt[c.Id] = DateTime.UtcNow; _komRefresh?.Invoke(); _ = Task.Delay(6500).ContinueWith(_ => _komRefresh?.Invoke()); } return;   // ✍ „pisze…”
                case "p2p-here": if ((bool?)p["fwd"] != true) _komFound[from] = (KomClean((string)p["device"]), sender); break;
                case "p2p-ack":
                    {
                        if (c == null) return;
                        var mid = (string)p["mid"] ?? "";
                        var mac = KomB64((string)p["mac"]); var exp = KomHmac(c.Key, "ack\n" + mid);
                        if (mac.Length != exp.Length || !CryptographicOperations.FixedTimeEquals(mac, exp)) return;
                        if ((bool?)p["fwd"] != true && !IPAddress.IsLoopback(sender) && c.Addr != sender.ToString()) { c.Addr = sender.ToString(); KomSave(); }
                        if (_komAcks.TryGetValue(mid, out var tcs)) tcs.TrySetResult(true);
                        // ✓✓ szare: plik ze skrzynki 📬 dotarł do odbiorcy
                        foreach (var m in KomChat(c.Id)) if (m.Mine && m.Mid == mid && m.Text.Contains("📬")) { m.Ok = true; m.Text = m.Text.Replace(L.T(" – 📬 czeka na serwerze"), L.T(" – odebrano")); KomSave(); _komRefresh?.Invoke(); }
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
            if (c != null) { KomResendPending(c); KomSendPhoto(c); }   // odezwal sie - dosylamy niedoreczone i 🖼 zdjęcie profilowe
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
            string lp = null;   // 🔗 podgląd linku od nadawcy
            var ln = KomB64((string)p["ln"]); var lct = KomB64((string)p["lp"]);
            if (ln.Length == 12 && lct.Length > 16 && lct.Length < 4096)
                try { var lpl = new byte[lct.Length - 16]; using (var g = new AesGcm(c.Key, 16)) g.Decrypt(ln, lct.AsSpan(0, lct.Length - 16), lct.AsSpan(lct.Length - 16), lpl, KomMsgAd(c.Id, _komId, mid, ts).Concat(Encoding.UTF8.GetBytes("\nlp")).ToArray()); lp = Encoding.UTF8.GetString(lpl); }
                catch (CryptographicException) { }
            var ack = KomPacket("p2p-ack"); ack["to"] = c.Id; ack["mid"] = mid; ack["mac"] = Convert.ToBase64String(KomHmac(c.Key, "ack\n" + mid));
            KomSendUdp(ack, sender);
            if (!_komSeen.Add(mid) || KomChat(c.Id).Any(x => x.Mid == mid)) return;   // powtorka (takze po ponownym uruchomieniu) - tylko ponowne potwierdzenie
            if ((bool?)p["fwd"] != true && !IPAddress.IsLoopback(sender) && c.Addr != sender.ToString()) c.Addr = sender.ToString();
            _komTypingAt.Remove(c.Id);
            if (text.StartsWith(KomCallTag, StringComparison.Ordinal)) { KomOnCall(c, text.Substring(KomCallTag.Length)); return; }   // 📹 sygnał rozmowy wideo - poza czatem
            if (text.StartsWith(KomAddrTag, StringComparison.Ordinal)) { var sk = text.Substring(KomAddrTag.Length).Trim(); if (sk.Length == 0) _komAskKeys.TryRemove(c.Id, out _); else _komAskKeys[c.Id] = sk; KomSave(); return; }   // 🔗 sekret „Mój adres” od właściciela
            if (text.StartsWith(KomAskOkTag, StringComparison.Ordinal)) { KomAskAnswer(c, text.Substring(KomAskOkTag.Length)); return; }   // 🔗 odpowiedź na prośbę
            if (text.StartsWith(KomAskTag, StringComparison.Ordinal)) { KomAskIn(c, text.Substring(KomAskTag.Length)); return; }   // 🔗 prośba z „Mojego adresu” (przez pośrednika)
            if (text.StartsWith(KomTimerTag, StringComparison.Ordinal)) { KomTtlIn(c, text.Substring(KomTimerTag.Length)); return; }   // ⏳ znikające wiadomości
            if (text.StartsWith(KomCardTag, StringComparison.Ordinal)) { KomCardIn(c, text.Substring(KomCardTag.Length)); return; }   // 👤 kontakt przedstawiony przez znajomego
            if (text.StartsWith(KomDelTag, StringComparison.Ordinal)) { KomDelIn(c, text.Substring(KomDelTag.Length)); return; }   // 🚫 nadawca usunął wiadomość u wszystkich
            if (text.StartsWith(KomEditTag, StringComparison.Ordinal)) { KomEditIn(c, text.Substring(KomEditTag.Length)); return; }   // ✏ nadawca poprawił wiadomość
            if (text.StartsWith(KomReactTag, StringComparison.Ordinal)) { KomReactIn(c, text.Substring(KomReactTag.Length)); return; }   // 😀 reakcja na wiadomość
            if (text.StartsWith(KomPhotoTag, StringComparison.Ordinal)) { KomPhotoIn(c, text.Substring(KomPhotoTag.Length)); return; }   // 🖼 zdjęcie profilowe kontaktu
            if (text.StartsWith(KomClipTag, StringComparison.Ordinal))
            {   // 📋 wspólny schowek - tylko z urządzeniem, dla którego jest włączony
                if (!KomClipOn(c.Id)) return;
                var clipText = text.Substring(KomClipTag.Length); _komClipLast = clipText;
                try { Clipboard.SetText(clipText); ShowToast("📋 " + L.T("Schowek z: ") + c.Name, null); } catch (Exception) { }
                return;
            }
            if (text.StartsWith(KomGroupTag, StringComparison.Ordinal))
            {
                var g = KomGroupIn(c, text.Substring(KomGroupTag.Length)); if (g == null) return;
                try { if ((string)JsonNode.Parse(text.Substring(KomGroupTag.Length, text.IndexOf('\n', KomGroupTag.Length) - KomGroupTag.Length))?["gm"] is { Length: 32 } gm) mid = gm; } catch (Exception) { }   // ten sam id u wszystkich (usuwanie)
                if (KomChat(g.Id).Any(x => x.Mid == mid)) return;
                text = c.Name + ": " + text.Substring(text.IndexOf('\n', KomGroupTag.Length) + 1); c = g;
            }   // 👥 wiadomość w grupie - do rozmowy grupy
            if (text.Length > 4000) text = text.Substring(0, 4000);
            KomChat(c.Id).Add(new KomMsg { Mine = false, Text = text, Ts = ts, Ok = true, Mid = mid, Lp = c.IsGroup ? null : lp });
            bool visible = _komWindow != null && _komWindow.IsVisible && _komWindow.IsActive && _komOpenId == c.Id;
            if (!visible) _komUnread[c.Id] = (_komUnread.TryGetValue(c.Id, out var u) ? u : 0) + 1;
            KomSave();
            _komRefresh?.Invoke();
            KomTrayUpdate();
            if (!visible) KomNotify(c, text); else KomSendRead(c);
            if (_komCallWith == c.Id) try { _komCallView?.CoreWebView2?.PostWebMessageAsString(new JsonObject { ["k"] = "txt-in", ["t"] = text }.ToJsonString()); } catch (Exception) { }   // 💬 widać też w oknie pomocy zdalnej
            KomResendPending(c); KomSendPhoto(c);
        }

        // powiadomienie jak w innych komunikatorach: okienko Velivo w prawym dolnym rogu, nad innymi programami
        // (takze gdy Velivo jest zminimalizowane lub w zasobniku): nadawca, tresc, szybka odpowiedz; klikniecie - rozmowa
        readonly List<Window> _komPopups = new List<Window>();
        void KomNotify(KomContact c, string text)
        {
            if (KomMuted(c.Id)) return;   // 🔕 wyciszona rozmowa - tylko licznik
            if (_komMinis.TryGetValue(c.Id, out var mw) && mw.IsActive) return;   // 🔝 rozmowa otwarta w okienku na wierzchu
            if (KomQuietWindows()) { try { if (_komWindow != null && !_komWindow.IsActive) FlashWindowEx(_komWindow); } catch (Exception) { } return; }   // pełny ekran / prezentacja / „Nie przeszkadzać” - bez okienka i dźwięku
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
            var title = new TextBlock { Text = "💬 " + c.Name + L.T(" pisze:"), FontWeight = FontWeights.SemiBold, FontSize = 15, Foreground = fg, TextTrimming = TextTrimming.CharacterEllipsis };
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
            if (c.IsGroup) { foreach (var m in KomChat(c.Id).Where(m => !m.Mine)) m.Read = true; return; }   // 👥 grupa - bez potwierdzeń
            var mids = KomChat(c.Id).Where(m => !m.Mine && !m.Read && m.Mid.Length == 32).ToList();
            if (mids.Count == 0) return;
            foreach (var m in mids) m.Read = true;
            KomSave();
            IPAddress.TryParse(c.Addr ?? "", out var to); if (to != null && IPAddress.IsLoopback(to)) to = null;
            var parts = mids.Select(m => m.Mid).Chunk(60).Select(part => { var j = string.Join(",", part); var p = KomPacket("p2p-read"); p["to"] = c.Id; p["mids"] = j; p["mac"] = Convert.ToBase64String(KomHmac(c.Key, "read\n" + j)); return p; }).ToList();
            // jak wiadomość: znany adres i cała sieć domowa (pośrednik przekazuje do telefonu poza domem), z powtórką - wcześniej raz na stary adres i ✓✓ nie robiły się niebieskie
            _ = Task.Run(async () => { for (int i = 0; i < 2; i++) { foreach (var p in parts) { if (to != null) KomSendUdp(p, to); KomSendUdp(p, null); } await Task.Delay(1500); } });
        }

        // 🎨 kolorowe ikony przycisków (WPF pokazuje emotki tylko szare) - kształty z Material Icons (Apache 2.0)
        static FrameworkElement KomIcon(string k, double size = 20)
        {
            var c = new Canvas { Width = 24, Height = 24 };
            void P(string d, string col) => c.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(d), Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(col)) });
            switch (k)
            {
                case "phone": P("M6.6 10.8c1.4 2.8 3.8 5.1 6.6 6.6l2.2-2.2c.3-.3.7-.4 1-.2 1.1.4 2.3.6 3.6.6.6 0 1 .4 1 1V20c0 .6-.4 1-1 1C10.6 21 3 13.4 3 4c0-.6.4-1 1-1h3.5c.6 0 1 .4 1 1 0 1.3.2 2.5.6 3.6.1.3 0 .7-.2 1L6.6 10.8z", "#22C55E"); break;
                case "video": P("M17 10.5V7c0-.6-.4-1-1-1H4c-.6 0-1 .4-1 1v10c0 .6.4 1 1 1h12c.6 0 1-.4 1-1v-3.5l4 4v-11l-4 4z", "#3B82F6"); break;
                case "attach": P("M16.5 6v11.5c0 2.2-1.8 4-4 4s-4-1.8-4-4V5c0-1.4 1.1-2.5 2.5-2.5s2.5 1.1 2.5 2.5v10.5c0 .6-.4 1-1 1s-1-.4-1-1V6H10v9.5c0 1.4 1.1 2.5 2.5 2.5s2.5-1.1 2.5-2.5V5c0-2.2-1.8-4-4-4S7 2.8 7 5v12.5c0 3 2.5 5.5 5.5 5.5s5.5-2.5 5.5-5.5V6h-1.5z", "#CBD5E1"); break;
                case "mic": P("M12 14c1.7 0 3-1.3 3-3V5c0-1.7-1.3-3-3-3S9 3.3 9 5v6c0 1.7 1.3 3 3 3zm5.3-3c0 3-2.5 5.1-5.3 5.1S6.7 14 6.7 11H5c0 3.4 2.7 6.2 6 6.7V21h2v-3.3c3.3-.5 6-3.3 6-6.7h-1.7z", "#EF4444"); break;
                case "stop": P("M6 6h12v12H6z", "#EF4444"); break;
                case "gallery": P("M21 19V5c0-1.1-.9-2-2-2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2z", "#38BDF8"); P("M8.5 13.5l2.5 3 3.5-4.5 4.5 6H5l3.5-4.5z", "#15803D"); P("M16 6.5a1.5 1.5 0 1 0 .01 0z", "#FACC15"); break;
                case "emoji": P("M12 2a10 10 0 1 0 .01 0z", "#FACC15"); P("M8.5 8.3a1.3 1.3 0 1 0 .01 0zM15.5 8.3a1.3 1.3 0 1 0 .01 0z", "#3B2A1A"); P("M7.2 13.5c1 2 2.7 3.2 4.8 3.2s3.8-1.2 4.8-3.2h-1.9c-.7 1-1.7 1.6-2.9 1.6s-2.2-.6-2.9-1.6z", "#3B2A1A"); break;
                case "screen": P("M21 2H3c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h7v2H8v2h8v-2h-2v-2h7c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm0 14H3V4h18v12z", "#06B6D4"); break;
            }
            return new Viewbox { Width = size, Height = size, Child = c };
        }

        void KomSend(KomContact c, string text)
        {
            var m = new KomMsg { Mine = true, Text = text.Length > 4000 ? text.Substring(0, 4000) : text, Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mid = Guid.NewGuid().ToString("N") };
            KomChat(c.Id).Add(m);
            if (c.IsGroup) { m.Ok = true; KomGroupOut(c, m.Text, m.Mid); }   // 👥 do każdego z grupy osobno (jego kluczem)
            KomSave();
            if (c.IsGroup) return;
            if (KomFirstUrl(m.Text) is string url) _ = KomPreviewThenDeliver(c, m, url); else KomDeliver(c, m);
        }

        // 🛡 jak na telefonie (Ochrona.kt): link podrabiający znaną firmę albo prośba o kod BLIK, dane karty, kod z SMS-a - najczęstsze oszustwa na starszych osobach
        static readonly string[] KomScamAsks = { "blik", "kod sms", "kod z sms", "numer karty", "dane karty", "cvv", "dopłat", "hasło do banku", "logowania do banku",
            "card number", "card details", "verification code", "bank details", "sort code" };
        static string KomScamWarning(string text)
        {
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text ?? "", @"(?:https?://|www\.)([^/\s:?#]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                if (CheckLookalike(m.Groups[1].Value, null) is string d) return "⚠ " + L.T("Ten link udaje ") + (d == "xn" ? L.T("znaną stronę (podmienione litery)") : d) + L.T(" – to może być oszustwo. Nie podawaj haseł ani danych karty.");
            var t = (text ?? "").ToLowerInvariant();
            return KomScamAsks.Any(t.Contains) ? "💡 " + L.T("Prośba o kod, dane karty albo dopłatę? Zanim cokolwiek zrobisz, zadzwoń do tej osoby i upewnij się.") : null;
        }

        // 🔗 podgląd linku jak w Signal: tytuł i opis strony pobiera nadawca i wysyła zaszyfrowane razem z wiadomością (odbiorca i pośrednik nie łączą się ze stroną)
        static readonly System.Text.RegularExpressions.Regex KomUrlRx = new System.Text.RegularExpressions.Regex(@"(https?://|www\.)[^\s<>""]+[^\s<>"".,;:!?)\]]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        static string KomFirstUrl(string text) { var m = KomUrlRx.Match(text ?? ""); return !m.Success ? null : m.Value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + m.Value : m.Value; }
        async Task KomPreviewThenDeliver(KomContact c, KomMsg m, string url)
        {
            m.Lp = await KomLinkPreview(url);
            if (m.Lp != null) { KomSave(); _komRefresh?.Invoke(); }
            KomDeliver(c, m);
        }
        static readonly System.Net.Http.HttpClient KomHttp = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        static async Task<string> KomLinkPreview(string url)
        {
            try
            {
                using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Velivo");
                using var res = await KomHttp.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                if (!res.IsSuccessStatusCode || res.Content.Headers.ContentType?.MediaType?.Contains("html") == false) return null;
                var buf = new byte[300000]; int n = 0;
                using (var s = await res.Content.ReadAsStreamAsync()) { int r; while (n < buf.Length && (r = await s.ReadAsync(buf, n, buf.Length - n)) > 0) n += r; }   // tylko początek strony (nagłówek z tytułem)
                var html = Encoding.UTF8.GetString(buf, 0, n);
                const System.Text.RegularExpressions.RegexOptions I = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
                string Meta(string k)
                {
                    var tag = System.Text.RegularExpressions.Regex.Match(html, "<meta[^>]+(?:property|name)=[\"']" + k + "[\"'][^>]*>", I); if (!tag.Success) return null;
                    var v = System.Text.RegularExpressions.Regex.Match(tag.Value, "content=(?:\"([^\"]*)\"|'([^']*)')", I); if (!v.Success) return null;
                    var t = WebUtility.HtmlDecode(v.Groups[1].Success ? v.Groups[1].Value : v.Groups[2].Value).Trim(); return t.Length > 0 ? t : null;
                }
                var tt = System.Text.RegularExpressions.Regex.Match(html, "<title[^>]*>([^<]*)</title>", I);
                var title = Meta("og:title") ?? Meta("twitter:title") ?? (tt.Success ? WebUtility.HtmlDecode(tt.Groups[1].Value).Trim() : "");
                if (title.Length == 0) return null;
                string Cut(string x, int l) => x.Length > l ? x.Substring(0, l) + "…" : x;
                return new JsonObject { ["u"] = url, ["t"] = Cut(title, 150), ["d"] = Cut(Meta("og:description") ?? Meta("description") ?? "", 300), ["s"] = Cut(Meta("og:site_name") ?? new Uri(url).Host, 60) }.ToJsonString();
            }
            catch (Exception) { return null; }
        }

        // 👥 grupy (jak w Signal: bez serwera grup) - wiadomość do każdego członka osobno, z nazwą i składem grupy; u odbiorcy grupa tworzy się sama
        const string KomGroupTag = "\u0001velivo-grupa\n";
        List<string> KomMembers(KomContact g) => (g.Addr ?? "").Replace("grupa:", "").Split(',').Where(x => x.Length == 32 && x != _komId).ToList();
        void KomGroupOut(KomContact g, string text, string gm)
        {
            var head = new JsonObject { ["g"] = g.Id, ["n"] = g.Name, ["m"] = new JsonArray(KomMembers(g).Append(_komId).Select(x => (JsonNode)x).ToArray()), ["gm"] = gm }.ToJsonString();
            foreach (var id in KomMembers(g)) { var mc = KomContactById(id); if (mc != null && !mc.IsGroup) KomDeliver(mc, new KomMsg { Mine = true, Text = KomGroupTag + head + "\n" + text, Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mid = Guid.NewGuid().ToString("N") }); }
        }
        KomContact KomGroupIn(KomContact from, string body)
        {
            JsonNode head; try { head = JsonNode.Parse(body.Substring(0, Math.Max(0, body.IndexOf('\n')))); } catch (Exception) { return null; }
            var gid = (string)head?["g"]; if (gid == null || gid.Length != 32) return null;
            var ids = (head["m"] as JsonArray)?.Select(x => (string)x).Where(x => x != null && x.Length == 32 && x != _komId).Append(from.Id).Distinct().ToList() ?? new List<string> { from.Id };
            var g = KomContactById(gid);
            if (g == null) { g = new KomContact { Id = gid, Name = KomClean((string)head["n"]) is { Length: > 0 } n ? n : "👥", Key = Array.Empty<byte>(), Addr = "", Device = "👥", Own = true }; _komContacts.Add(g); }
            if (!g.IsGroup) return null;
            g.Addr = "grupa:" + string.Join(",", KomMembers(g).Concat(ids).Distinct());
            return g;
        }
        // 👥 nowa grupa: nazwa i osoby z kontaktów (pierwsza wiadomość tworzy grupę u wszystkich)
        void KomGroupDialog(Window owner)
        {
            var cs = _komContacts.Where(c => !c.IsGroup).ToList();
            if (cs.Count < 2) { KomInfo(L.T("Do grupy potrzeba co najmniej 2 kontaktów")); return; }
            var name = new TextBox { Text = L.T("Rodzina"), Margin = new Thickness(0, 4, 0, 8) };
            var list = new StackPanel();
            foreach (var c in cs) list.Children.Add(new CheckBox { Content = c.Name, Tag = c.Id, Margin = new Thickness(0, 3, 0, 3) });
            var ok = new Button { Content = L.T("Utwórz"), IsDefault = true, Padding = new Thickness(16, 4, 16, 4), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var d = new Window { Title = "👥 " + L.T("Nowa grupa"), Owner = owner, Width = 320, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
                Content = new StackPanel { Margin = new Thickness(14), Children = { new TextBlock { Text = L.T("Nazwa grupy") }, name, new TextBlock { Text = L.T("Kto w grupie?") }, new ScrollViewer { Content = list, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, ok } } };
            ok.Click += (a, b) =>
            {
                var ids = list.Children.OfType<CheckBox>().Where(x => x.IsChecked == true).Select(x => (string)x.Tag).ToList();
                if (ids.Count == 0) return;
                var g = KomNewGroup(name.Text.Trim().Length > 0 ? name.Text.Trim() : L.T("Grupa"), ids); d.Close(); _komSelect?.Invoke(g.Id);
            };
            KomStyle(d); d.ShowDialog();
        }
        // 👤 przedstawienie: osoby z moich kontaktów dostają siebie nawzajem (nowy wspólny klucz; wizytówki szyfrowane kluczem każdej z nich)
        const string KomCardTag = "\u0001velivo-kontakt\n";
        void KomIntroduce(List<KomContact> cs)
        {
            cs = cs.Concat(_komContacts.Where(c => !string.IsNullOrEmpty(c.Relay) && !c.IsGroup)).GroupBy(c => c.Id).Select(g => g.First()).ToList();   // + moi pośrednicy (np. NUCBOX) - połączenie także przez internet
            for (int i = 0; i < cs.Count; i++)
                for (int j = i + 1; j < cs.Count; j++)
                {
                    var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                    string Card(KomContact c) => KomCardTag + new JsonObject { ["id"] = c.Id, ["name"] = c.Name, ["dev"] = c.Device, ["key"] = key, ["relay"] = c.Relay ?? "" }.ToJsonString();
                    KomDeliver(cs[i], new KomMsg { Mine = true, Text = Card(cs[j]), Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mid = Guid.NewGuid().ToString("N") });
                    KomDeliver(cs[j], new KomMsg { Mine = true, Text = Card(cs[i]), Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mid = Guid.NewGuid().ToString("N") });
                }
        }
        void KomCardIn(KomContact from, string body)
        {
            JsonNode j; try { j = JsonNode.Parse(body); } catch (Exception) { return; }
            var id = (string)j?["id"]; byte[] key; try { key = Convert.FromBase64String((string)j["key"] ?? ""); } catch (Exception) { return; }
            // znany już serwer bez adresu w internecie (np. CHUWI dodany wcześniej w domu) - mój serwer podaje jego adres; klucz bez zmian
            if (KomContactById(id ?? "") is { } kc) { var ra = (string)j["relay"] ?? ""; if (from.Dom && ra != kc.Relay && ra.Contains(':') && ra.Length < 200) { kc.Relay = ra; KomSave(); } return; }   // adres istniejącego kontaktu (także nowy port) - tylko od domowego
            if (id == null || id.Length != 32 || id == _komId || key.Length != 32) return;
            var name = KomClean((string)j["name"]); if (name.Length == 0) name = "Velivo";
            var relay = (string)j["relay"];
            // pośrednik (np. NUCBOX, zwykle bez nikogo przy ekranie) - od zaufanego kontaktu od razu, żeby nowy znajomy mógł się łączyć przez internet
            // 🔁 drugi pośrednik w domu (np. CHUWI) przedstawiony przez mojego pośrednika - też od razu: wiadomości idą przez którykolwiek
            bool dom = from.Dom && (bool?)j["dom"] == true;   // „domowy” tylko od domowego - serwer znajomego nie zrobi z nikogo mojego urządzenia
            // zapasowy serwer od domowego albo od serwera, przez który teraz się łączę (np. mama z zaproszenia) - bez pytania
            // moje domowe urządzenie od mojego domowego - od razu, także bez adresu serwera (np. Ryzen, który pośrednikiem został później)
            if (_komRelayListener != null || dom || (KomTrustedRelay(from) && (bool?)j["dom"] == true && (relay ?? "").Contains(':'))) { KomCardAdd(from, id, name, key, (string)j["dev"], relay, dom); return; }
            // prośba o kontakt od znajomego - zgoda (okno nad innymi; nie blokuje odbioru)
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (KomContactById(id) != null) return;
                if (MessageBox.Show((Window)_komWindow ?? PairDialogOwner(), from.Name + L.T(" poleca Ci: ") + name + L.T(".\n\nDodać do komunikatora? Będziecie mogli pisać i dzwonić także przez internet."),
                    "👤 " + L.T("Prośba o kontakt"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                KomCardAdd(from, id, name, key, (string)j["dev"], relay, false);
            }));
            KomPlaySound();
        }
        void KomCardAdd(KomContact from, string id, string name, byte[] key, string dev, string relay, bool dom)
        {
            _komContacts.Add(new KomContact { Id = id, Name = name, Key = key, Addr = "", Device = KomClean(dev) is { Length: > 0 } dv ? dv : name, Own = false, Relay = string.IsNullOrEmpty(relay) ? null : relay, Dom = dom });
            KomChat(from.Id).Add(new KomMsg { Mine = false, Text = "👤 " + L.T("Nowy kontakt: ") + name + " (" + L.T("od: ") + from.Name + ")", Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Ok = true, Mid = Guid.NewGuid().ToString("N") });
            KomSave(); _komRefresh?.Invoke(); KomAnnounce();
        }
        void KomIntroduceDialog(Window owner)
        {
            var cs = _komContacts.Where(c => !c.IsGroup).ToList();
            if (cs.Count < 2) { KomInfo(L.T("Potrzeba co najmniej 2 kontaktów")); return; }
            var list = new StackPanel();
            foreach (var c in cs) list.Children.Add(new CheckBox { Content = c.Name, Tag = c, Margin = new Thickness(0, 3, 0, 3) });
            var ok = new Button { Content = L.T("Połącz"), IsDefault = true, Padding = new Thickness(16, 4, 16, 4), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var d = new Window { Title = "👤 " + L.T("Kogo połączyć ze sobą?"), Owner = owner, Width = 320, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
                Content = new StackPanel { Margin = new Thickness(14), Children = { new ScrollViewer { Content = list, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, ok } } };
            ok.Click += (a, b) =>
            {
                var sel = list.Children.OfType<CheckBox>().Where(x => x.IsChecked == true).Select(x => (KomContact)x.Tag).ToList();
                if (sel.Count < 2) return;
                KomIntroduce(sel); d.Close(); KomInfo("👤 " + L.T("Wysłano – dodadzą się, gdy będą w sieci (także przez skrzynkę 📬)"));
            };
            KomStyle(d); d.ShowDialog();
        }
        // 👥 pliki w grupie: jeden wpis w rozmowie grupy, do każdego członka osobno (nieobecni - 📬 skrzynka pośrednika)
        async Task KomSendGroupFiles(Window owner, KomContact g, string[] files)
        {
            if (files == null) { var dlg = new Microsoft.Win32.OpenFileDialog { Title = "👥 " + g.Name, Multiselect = true }; if (dlg.ShowDialog(owner) != true) return; files = dlg.FileNames; }
            foreach (var f in files)
            {
                long size = 0; try { size = new FileInfo(f).Length; } catch (Exception) { continue; }
                KomChat(g.Id).Add(new KomMsg { Mine = true, Text = KomFileLabel(Path.GetFileName(f), size), Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Ok = true, Mid = Guid.NewGuid().ToString("N"), File = true, Path = f });
                KomSave(); _komRefresh?.Invoke();
                KomInfo("👥 " + Path.GetFileName(f) + L.T(" – wysyłam do grupy…"), 0);
                foreach (var id in KomMembers(g))
                {
                    var mc = KomContactById(id); if (mc == null || mc.IsGroup) continue;
                    IPAddress.TryParse(mc.Addr ?? "", out var a0); var mid = Guid.NewGuid().ToString("N");
                    var r = await KomSendFile(KomOnline(mc.Id) && a0 != null ? a0 : IPAddress.Loopback, f, _ => { }, mc, null, mid, group: g);
                    if (r != "ok" && r != L.T("odrzucono")) await KomSendViaBox(mc, f, mid, g);
                }
                KomInfo("👥 " + Path.GetFileName(f) + L.T(" – wysłano do grupy (nieobecnym przez skrzynkę 📬)"));
            }
        }
        // 🚫 usuń u wszystkich (jak w WhatsApp): u mnie i u odbiorców zostaje „Wiadomość usunięta”
        const string KomDelTag = "\u0001velivo-usun\n";
        void KomDeleteForAll(KomContact c, KomMsg m)
        {
            if (!m.Mine) return;
            m.Text = "🚫 " + L.T("Usunięto tę wiadomość"); m.Path = null; m.Ok = true; KomSave(); _komRefresh?.Invoke();
            var body = new JsonObject { ["m"] = m.Mid }; if (c.IsGroup) body["g"] = c.Id;
            foreach (var to in c.IsGroup ? KomMembers(c).Select(KomContactById).Where(x => x != null && !x.IsGroup) : new[] { c })
                KomDeliver(to, new KomMsg { Mine = true, Text = KomDelTag + body.ToJsonString(), Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mid = Guid.NewGuid().ToString("N") });
        }
        void KomDelIn(KomContact from, string body)
        {
            JsonNode j; try { j = JsonNode.Parse(body); } catch (Exception) { return; }
            var gid = (string)j?["g"]; var chatId = gid != null && KomContactById(gid)?.IsGroup == true ? gid : from.Id;
            var m = KomChat(chatId).FirstOrDefault(x => x.Mid == (string)j["m"] && !x.Mine); if (m == null) return;
            m.Text = (chatId != from.Id ? from.Name + ": " : "") + "🚫 " + L.T("Wiadomość usunięta"); m.Path = null;
            KomSave(); _komRefresh?.Invoke();
        }
        // ✏ poprawka, 😀 reakcja - jak usuwanie: do rozmówcy albo do każdego w grupie (wiadomość wskazana jej id)
        const string KomEditTag = "\u0001velivo-edycja\n", KomReactTag = "\u0001velivo-reakcja\n", KomPhotoTag = "\u0001velivo-zdjecie\n";
        void KomTagOut(KomContact c, string tag, JsonObject body)
        {
            if (c.IsGroup) body["g"] = c.Id;
            foreach (var to in c.IsGroup ? KomMembers(c).Select(KomContactById).Where(x => x != null && !x.IsGroup) : new[] { c })
                KomDeliver(to, new KomMsg { Mine = true, Text = tag + body.ToJsonString(), Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mid = Guid.NewGuid().ToString("N") });
        }
        string KomChatOf(KomContact from, JsonNode j) => (string)j?["g"] is { Length: 32 } g && KomContactById(g)?.IsGroup == true ? g : from.Id;
        void KomEdit(KomContact c, KomMsg m, string text)
        {
            if (!m.Mine || m.File) return;
            if (text.Length > 4000) text = text.Substring(0, 4000);
            m.Text = text + " ✏"; KomSave(); _komRefresh?.Invoke();
            KomTagOut(c, KomEditTag, new JsonObject { ["m"] = m.Mid, ["t"] = text });
        }
        void KomEditIn(KomContact from, string body)
        {
            JsonNode j; try { j = JsonNode.Parse(body); } catch (Exception) { return; }
            var id = KomChatOf(from, j); var m = KomChat(id).FirstOrDefault(x => x.Mid == (string)j["m"] && !x.Mine && !x.File); if (m == null) return;
            var t = (id != from.Id ? from.Name + ": " : "") + ((string)j["t"] ?? ""); m.Text = (t.Length > 4000 ? t.Substring(0, 4000) : t) + " ✏";
            KomSave(); _komRefresh?.Invoke();
        }
        void KomReact(KomContact c, KomMsg m, string e)
        {
            if (("," + m.Re + ",").Contains("," + _komId + ":" + e + ",")) e = "";   // ta sama reakcja ponownie - zdjęta
            m.Re = KomReSet(m.Re, _komId, e); KomSave(); _komRefresh?.Invoke();
            KomTagOut(c, KomReactTag, new JsonObject { ["m"] = m.Mid, ["e"] = e });
        }
        void KomReactIn(KomContact from, string body)
        {
            JsonNode j; try { j = JsonNode.Parse(body); } catch (Exception) { return; }
            var m = KomChat(KomChatOf(from, j)).FirstOrDefault(x => x.Mid == (string)j["m"]); if (m == null) return;
            var e = (string)j["e"] ?? ""; m.Re = KomReSet(m.Re, from.Id, e.Length > 8 ? e.Substring(0, 8) : e); KomSave(); _komRefresh?.Invoke();
        }
        static string KomReSet(string re, string id, string e) => string.Join(",", (re ?? "").Split(',').Where(x => x.Length > 33 && !x.StartsWith(id + ":", StringComparison.Ordinal)).Concat(e.Length > 0 ? new[] { id + ":" + e } : Array.Empty<string>()));
        static string KomReactions(KomMsg m) => string.Concat((m.Re ?? "").Split(',').Where(x => x.Length > 33).Select(x => x.Substring(33)));
        // ↩ odpowiedź: pierwszy wiersz cytowanej wiadomości (bez jej cytatu) - czytelne także w starszym Velivo
        static string KomQuote(KomMsg m) { var l = m.Text.Split('\n').FirstOrDefault(x => !x.StartsWith("› ", StringComparison.Ordinal)) ?? ""; if (l.EndsWith(" ✏", StringComparison.Ordinal)) l = l.Substring(0, l.Length - 2); return "› " + (l.Length > 70 ? l.Substring(0, 70) : l) + "\n"; }

        // 🖼 moje zdjęcie profilowe (małe) - u kontaktów zamiast kropki; nowe dostaje każdy, kto się odezwie
        bool _komLock; long _komPhotoV; readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _komPhotoSent = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        string KomPhoto(string id) { var p = Path.Combine(KomMediaDir, "profil-" + id + ".jpg"); return File.Exists(p) ? p : null; }
        void KomSetPhoto(byte[] jpg)
        {
            Directory.CreateDirectory(KomMediaDir); var p = Path.Combine(KomMediaDir, "profil-" + _komId + ".jpg");
            if (jpg == null) File.Delete(p); else File.WriteAllBytes(p, jpg);
            _komPhotoV = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); KomSave();
            foreach (var c in _komContacts.Where(x => !x.IsGroup).ToList()) KomSendPhoto(c);
        }
        void KomSendPhoto(KomContact c)
        {
            if (_komPhotoV == 0 || c.IsGroup || _komPhotoSent.TryGetValue(c.Id, out var v) && v == _komPhotoV) return;
            _komPhotoSent[c.Id] = _komPhotoV; KomSave();
            var p = KomPhoto(_komId);
            KomDeliver(c, new KomMsg { Mine = true, Text = KomPhotoTag + new JsonObject { ["p"] = p != null ? Convert.ToBase64String(File.ReadAllBytes(p)) : "" }.ToJsonString(), Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Mid = Guid.NewGuid().ToString("N") });
        }
        void KomPhotoIn(KomContact from, string body)
        {
            byte[] b; try { b = Convert.FromBase64String((string)JsonNode.Parse(body)?["p"] ?? ""); } catch (Exception) { return; }
            Directory.CreateDirectory(KomMediaDir); var p = Path.Combine(KomMediaDir, "profil-" + from.Id + ".jpg");
            try { if (b.Length == 0) File.Delete(p); else if (b.Length < 60000) File.WriteAllBytes(p, b); } catch (Exception) { }
            _komRefresh?.Invoke();
        }
        static ImageSource KomAvatar(string path)
        {
            try { var b = new System.Windows.Media.Imaging.BitmapImage(); b.BeginInit(); b.StreamSource = new MemoryStream(File.ReadAllBytes(path)); b.DecodePixelWidth = 64; b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; b.EndInit(); b.Freeze(); return b; }
            catch (Exception) { return null; }
        }
        static byte[] KomPhotoJpg(string file)
        {   // kwadrat 128 px ze środka zdjęcia
            var src = new System.Windows.Media.Imaging.BitmapImage(); src.BeginInit(); src.UriSource = new Uri(file); src.DecodePixelWidth = 400; src.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; src.EndInit();
            int n = Math.Min(src.PixelWidth, src.PixelHeight);
            var crop = new System.Windows.Media.Imaging.CroppedBitmap(src, new Int32Rect((src.PixelWidth - n) / 2, (src.PixelHeight - n) / 2, n, n));
            var small = new System.Windows.Media.Imaging.TransformedBitmap(crop, new ScaleTransform(128.0 / n, 128.0 / n));
            var enc = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 80 }; enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(small));
            using var ms = new MemoryStream(); enc.Save(ms); return ms.ToArray();
        }
        KomContact KomNewGroup(string name, IEnumerable<string> ids)
        {
            var g = new KomContact { Id = Guid.NewGuid().ToString("N"), Name = name, Key = Array.Empty<byte>(), Addr = "grupa:" + string.Join(",", ids), Device = "👥", Own = true };
            _komContacts.Add(g); KomSave(); _komRefresh?.Invoke();
            return g;
        }

        void KomResendPending(KomContact c)
        {
            long week = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 7L * 86400000;
            foreach (var m in KomChat(c.Id).Where(x => x.Mine && !x.Ok && !x.File && x.Ts > week).ToList()) KomDeliver(c, m);
            foreach (var m in KomChat(c.Id).Where(x => x.Mine && !x.Ok && x.File && x.Ts > week && KomOnline(c.Id) && File.Exists(x.Path ?? "")).ToList()) KomResendFile(c, m);
        }
        // 📎 plik, który czekał na odbiorcę - wysyłany sam, gdy kontakt się odezwie
        async void KomResendFile(KomContact c, KomMsg m)
        {
            if (!_komInFlight.Add(m.Mid)) return;
            try
            {
                IPAddress.TryParse(c.Addr ?? "", out var addr);
                if (_komUp == null && _komViaNet.ContainsKey(c.Id) && !_komRemote.ContainsKey(c.Id))
                {   // odbiorca poza domem (🌍), ten komputer w domu - przez skrzynkę pośrednika (stary adres z sieci domowej nie działa)
                    if (await KomSendViaBox(c, m.Path, m.Mid) == "ok") { m.Ok = true; m.Text += L.T(" – 📬 czeka na serwerze"); KomSave(); _komRefresh?.Invoke(); }
                    return;
                }
                var r = await KomSendFile(addr ?? IPAddress.Loopback, m.Path, _ => { }, c, null, m.Mid);
                if (r == "ok") { m.Ok = true; m.Text += L.T(" – odebrano"); KomSave(); _komRefresh?.Invoke(); }
                else if (r == L.T("odrzucono")) { KomChat(c.Id).Remove(m); KomSave(); _komRefresh?.Invoke(); }
            }
            finally { _komInFlight.Remove(m.Mid); }
        }

        // najpierw na znany adres, potem do calej sieci; 4 proby co 1,5 s; potem co minute (zegar) i gdy kontakt sie odezwie
        async void KomDeliver(KomContact c, KomMsg m)
        {
            if (c.IsGroup || !_komInFlight.Add(m.Mid)) return;   // grupa nie ma klucza - jej wiadomości idą przez KomGroupOut
            try
            {
                var p = KomPacket("p2p-msg"); p["ts"] = m.Ts; p["to"] = c.Id; p["mid"] = m.Mid;
                var n = RandomNumberGenerator.GetBytes(12); p["n"] = Convert.ToBase64String(n);
                var plain = Encoding.UTF8.GetBytes(m.Text); var ct = new byte[plain.Length]; var tag = new byte[16];
                using (var g = new AesGcm(c.Key, 16)) g.Encrypt(n, plain, ct, tag, KomMsgAd(_komId, c.Id, m.Mid, m.Ts));
                p["c"] = Convert.ToBase64String(ct.Concat(tag).ToArray());
                if (m.Lp != null)
                {   // 🔗 podgląd linku - zaszyfrowany jak wiadomość (osobne pole: starsze Velivo go pomija)
                    var ln = RandomNumberGenerator.GetBytes(12); var lpl = Encoding.UTF8.GetBytes(m.Lp); var lct = new byte[lpl.Length]; var ltag = new byte[16];
                    using (var g = new AesGcm(c.Key, 16)) g.Encrypt(ln, lpl, lct, ltag, KomMsgAd(_komId, c.Id, m.Mid, m.Ts).Concat(Encoding.UTF8.GetBytes("\nlp")).ToArray());
                    p["ln"] = Convert.ToBase64String(ln); p["lp"] = Convert.ToBase64String(lct.Concat(ltag).ToArray());
                }
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
        // ⏱ czas na liście rozmów: dziś - godzina, wczoraj, w tym tygodniu - dzień, starsze - data
        static string KomWhen(long ts)
        {
            var t = DateTimeOffset.FromUnixTimeMilliseconds(ts).LocalDateTime;
            if (t.Date == DateTime.Today) return t.ToString("HH:mm");
            if (t.Date == DateTime.Today.AddDays(-1)) return L.T("wczoraj");
            return t.Date > DateTime.Today.AddDays(-7) ? t.ToString("dddd", KomCulture) : t.ToString("dd.MM.yy");
        }
        static string KomDay(DateTime d) => d == DateTime.Today ? L.T("Dzisiaj") : d == DateTime.Today.AddDays(-1) ? L.T("Wczoraj") : d.ToString(d.Year == DateTime.Today.Year ? "d MMMM, dddd" : "d MMMM yyyy", KomCulture);
        static System.Globalization.CultureInfo KomCulture => System.Globalization.CultureInfo.GetCultureInfo(L.En ? "en-GB" : "pl-PL");
        bool KomViaNet(string id) => _komUp != null || _komViaNet.ContainsKey(id);   // 🌍 kontakt osiagalny przez internet (nie w sieci domowej)
        bool KomOnline(string id) => _komLastSeen.TryGetValue(id, out var t) && DateTime.UtcNow - t < TimeSpan.FromSeconds(95);

        // zamkniecie Velivo: kontakty od razu widza "poza siecia"
        internal void KomBye()
        {
            if (_komSavePending) KomSaveNow();   // zamknięcie - zaległy zapis od razu
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
                _ = KomPollAsks();   // 🔗 odpowiedzi na moje prośby z czyjegoś adresu
                // 📬 skrzynka pośrednika: nieudana próba oddania pliku (słaby zasięg odbiorcy) - ponawiana co minutę, póki odbiorca w sieci, a nie dopiero przy jego ponownym połączeniu
                if (_komRelayListener != null) KomBoxDoneRepeat();
                if (_komRelayListener != null) foreach (var id in _komRemote.Keys.Concat(_komLanSeen.Where(kv => DateTime.UtcNow - kv.Value < TimeSpan.FromSeconds(100)).Select(kv => kv.Key)).Distinct().ToList()) _ = KomBoxFlush(id);
                if (_komContacts.Count == 0) return;
                KomSendUdp(KomPacket("p2p-here"), null);
                foreach (var c in _komContacts.ToList()) KomResendPending(c);
                KomRelayPair();   // 🔁 dwa serwery w domu, które się nie znają - przedstawione sobie
                _komRefresh?.Invoke();   // kropki w sieci / poza siecia
            };
            _komResend.Start();
            // powrot do domowej sieci (np. laptop) - ogloszenie, kontakty dosylaja zalegle wiadomosci
            NetworkChange.NetworkAddressChanged += (s, e) => Dispatcher.BeginInvoke(new Action(() => { if (_komContacts.Count > 0) KomAnnounce(); }));
        }

        async Task<List<(string Id, string Name, IPAddress Addr)>> KomFind()
        {
            _komFound.Clear();
            var sweep = KomPacket("p2p-who"); _ = Task.Run(() => KomSweep(sweep));   // 508 wysyłek i lista kart sieciowych - poza wątkiem okna
            for (int i = 0; i < 3; i++) { KomSendUdp(KomPacket("p2p-who"), null); await Task.Delay(800); }
            return _komFound.Select(kv => (kv.Key, kv.Value.Name, kv.Value.Addr)).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // 🔎 pytanie do każdego adresu domowej sieci (/24) osobno - telefony gubiące rozgłoszenia (Oppo / ColorOS) też się znajdą
        void KomSweep(JsonObject p)
        {
            var s = _komUdp ?? _lanTx; if (s == null) return;
            var d = Encoding.UTF8.GetBytes(p.ToJsonString());
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork))
                    {
                        var b = ua.Address.GetAddressBytes();
                        if (!(b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] >= 16 && b[1] <= 31)) continue;   // tylko sieć domowa
                        for (int i = 1; i <= 254; i++)
                            if (i != b[3]) foreach (var pt in new[] { KomPort, LanPort }) try { s.Send(d, d.Length, new IPEndPoint(new IPAddress(new[] { b[0], b[1], b[2], (byte)i }), pt)); } catch (Exception) { }
                    }
            }
            catch (Exception) { }
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
            if (type != "p2p-pair" && type != "p2p-file" && type != "p2p-box") return false;
            // plik dla urzadzenia poza domem polaczonego przez tego pośrednika - przekazanie tunelem
            var toId = (string)h["To"];
            if (type == "p2p-box" && toId != _komId)
            {   // 📬 pośrednik: plik do kontaktu, którego może nie być - przechowanie i oddanie, gdy się pojawi
                if (_komRelayListener == null) return true;
                try { await KomBoxStore(h, headerBytes, stream, KomPeerAt(remote) is string pr && pr != (string)h["Id"] ? pr : null, token); } catch (Exception ex) { if (!token.IsCancellationRequested && ex is not IOException) App.LogError(ex); }
                return true;
            }
            if (type == "p2p-file" && toId != null && toId != _komId && _komRemote.ContainsKey(toId)) { await KomRelayToRemote(toId, headerBytes, stream, token); return true; }
            if (type == "p2p-file" && !string.IsNullOrEmpty(toId) && toId != _komId) return true;   // plik dla kogoś innego (np. odbiorca poza domem chwilowo bez połączenia) - bez pytania o kod; nadawca odda go do 📬
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
                await Dispatcher.InvokeAsync(() => KomAddContact(new KomContact { Id = other, Name = dev, Device = dev, Key = key, Addr = remote?.ToString() ?? "", Dom = !auto && !invited }));
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
                using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4)))
                using (var cl = await KomTcpConnect(addr, cts.Token))
                {
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
                    var nc = new KomContact { Id = cid, Name = dev.Length > 0 ? dev : name, Device = dev.Length > 0 ? dev : name, Key = key, Addr = addr ?? "", Relay = relay, Dom = !invited };
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
            bool box = (string)h["Type"] == "p2p-box";   // 📬 plik ze skrzynki pośrednika - klucz z klucza kontaktu (nadawcy mogło już nie być)
            var pubA = KomB64((string)h["Pub"]); var baseNonce = KomB64((string)h["Nonce"]);
            if ((!box && (pubA.Length == 0 || pubA.Length > 200)) || baseNonce.Length != 12) return;
            var name = SafeFileName((string)h["Name"]); var dev = KomClean((string)h["Device"]);
            byte[] key;
            if (box) { var bc = await Dispatcher.InvokeAsync(() => KomContactById(id)); if (bc == null) return; key = KomHmac(bc.Key, "velivo-box\n" + (string)h["Nonce"]); }
            else using (var me = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
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
            if (box && from == null) accept = false;   // skrzynka - tylko od kontaktu
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
                    // 👥 plik w grupie - do rozmowy grupy (grupa tworzy się sama)
                    var grp = from != null && h["Group"] is JsonObject gh ? KomGroupIn(from, gh.ToJsonString() + "\n") : null;
                    var into = grp ?? from;
                    if (from != null) KomNote(into, false, (grp != null ? from.Name + ": " : "") + KomFileLabel(Path.GetFileName(target), size), (string)h["Mid"], target);
                    if (from != null && grp == null && (string)h["Mid"] is { Length: 32 } fm) { var ack = KomPacket("p2p-ack"); ack["to"] = from.Id; ack["mid"] = fm; ack["mac"] = Convert.ToBase64String(KomHmac(from.Key, "ack\n" + fm)); KomSendUdp(ack, null); }   // ✓✓ szare u nadawcy (plik ze skrzynki 📬)
                    if (from != null) from = into;
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

        // box - 📬 do skrzynki pośrednika (odbiorcy nie ma): klucz z klucza kontaktu, bez wymiany kluczy z odbiorcą
        async Task<string> KomSendFile(IPAddress addr, string path, Action<string> showCode, KomContact to = null, Action<long, long> progress = null, string mid = null, bool box = false, KomContact group = null)
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
                        var h = new JsonObject { ["Type"] = box ? "p2p-box" : "p2p-file", ["Id"] = _komId, ["Device"] = _lanDeviceName, ["Hash"] = hash, ["Size"] = size, ["Name"] = name, ["Pub"] = box ? "" : Convert.ToBase64String(pubA), ["Nonce"] = Convert.ToBase64String(baseNonce) };
                        if (mid != null) h["Mid"] = mid;
                        if (to != null) h["To"] = to.Id;   // pośrednik wie, komu przekazac (poza podpisem)
                        if (group != null) h["Group"] = new JsonObject { ["g"] = group.Id, ["n"] = group.Name, ["m"] = new JsonArray(KomMembers(group).Append(_komId).Select(x => (JsonNode)x).ToArray()) };   // 👥 plik w grupie
                        var ad = KomFileAd(h);
                        if (to != null) using (var hm = new HMACSHA256(to.Key)) h["Mac"] = Convert.ToBase64String(hm.ComputeHash(ad));   // kontakt rozpozna nadawce bez kodu
                        cts.CancelAfter(TimeSpan.FromSeconds(15));
                        // poza domem - tunel przez pośrednika; w domu - prosto do urzadzenia
                        Stream s = await KomOpenAsync(cl, addr, to?.Id, cts.Token);
                        using var tunnel = s as IDisposable;
                        await KomWriteBlock(s, Encoding.UTF8.GetBytes(h.ToJsonString()), cts.Token);
                        byte[] key;
                        if (box) key = KomHmac(to.Key, "velivo-box\n" + (string)h["Nonce"]);
                        else
                        {
                            var pubB = await KomReadBlock(s, 200, cts.Token); if (pubB == null) return "błąd";
                            key = KomAgree(me, pubB, "velivo-p2p", pubA, pubB);
                            if (to == null) showCode(KomCode(key));
                        }
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
                        return box || CryptographicOperations.FixedTimeEquals(ack, KomHmac(key, "file-ack\n" + hash)) ? "ok" : "błąd";   // skrzynka: potwierdza pośrednik
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
        // 📷 zdjęcie z aparatu telefonu: piksele „na boku” + znacznik obrotu (EXIF) - WPF go nie czyta, obracamy sami
        static System.Windows.Media.Imaging.BitmapSource KomUpright(System.Windows.Media.Imaging.BitmapSource b, string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                var meta = System.Windows.Media.Imaging.BitmapFrame.Create(fs, System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None).Metadata as System.Windows.Media.Imaging.BitmapMetadata;
                int deg = (meta?.GetQuery("System.Photo.Orientation") as ushort?) switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
                if (deg == 0) return b;
                var r = new System.Windows.Media.Imaging.TransformedBitmap(b, new RotateTransform(deg)); r.Freeze(); return r;
            }
            catch (Exception) { return b; }
        }

        ImageSource KomThumb(string path)
        {
            if (_komThumbs.TryGetValue(path, out var t)) return t;
            try
            {
                var b = new System.Windows.Media.Imaging.BitmapImage();
                b.BeginInit(); b.UriSource = new Uri(path); b.DecodePixelWidth = 260; b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; b.EndInit(); b.Freeze();
                t = KomUpright(b, path);
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
                else if (o is CheckBox or RadioButton or Label) ((Control)o).Foreground = B(p.Fg);   // ☐ kratki i etykiety - napis czytelny w ciemnych motywach
                else if (o is PasswordBox pb) { pb.Background = B(p.Input); pb.Foreground = B(p.Fg); pb.BorderBrush = B(p.Border); pb.CaretBrush = B(p.Fg); }
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
            KomStartOwn();
            KomEnsureTray();
            if (_komLock && !_komUnlocked) { _ = Dispatcher.InvokeAsync(async () => { if (await KomUnlock()) { _komUnlocked = true; ShowMessenger(openId); } }); return; }
            if (_komWindow != null) { if (!_komWindow.IsVisible) _komWindow.Show(); _komWindow.Activate(); if (_komWindow.WindowState == WindowState.Minimized) _komWindow.WindowState = WindowState.Normal; if (openId != null) _komSelect?.Invoke(openId); return; }
            var w = new Window { Icon = KomWindowIcon(), Title = L.T("Komunikator Velivo"), Width = 760, Height = 560, MinWidth = 480, MinHeight = 320, WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = true, Uid = "own-theme",
                TaskbarItemInfo = new System.Windows.Shell.TaskbarItemInfo() };   // 🔢 licznik nieprzeczytanych na ikonie paska zadań
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
            var searchBox = new TextBox { Width = 160, Margin = new Thickness(6, 0, 0, 6), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = L.T("🔍 Szukaj w rozmowie") };
            var top = new DockPanel(); DockPanel.SetDock(top, Dock.Top);
            var input = new TextBox { AcceptsReturn = true, Language = System.Windows.Markup.XmlLanguage.GetLanguage(L.En ? "en-US" : "pl-PL"), SpellCheck = { IsEnabled = true }, TextWrapping = TextWrapping.Wrap, MaxHeight = 120, MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = L.T("Wiadomość (Enter - wyślij, Shift+Enter - nowa linia)") };
            DateTime typedAt = default;
            input.TextChanged += (s0, e0) =>
            {   // ✍ druga osoba widzi „pisze…” (sygnał co najwyżej co 3 s)
                var tc = Current(); if (tc == null || tc.IsGroup || input.Text.Length == 0 || DateTime.UtcNow - typedAt < TimeSpan.FromSeconds(3)) return;
                typedAt = DateTime.UtcNow; var tp = KomPacket("p2p-typing"); tp["to"] = tc.Id; IPAddress.TryParse(tc.Addr ?? "", out var ta); KomSendUdp(tp, ta);
            };
            var sendBtn = new Button { Content = "➤", FontSize = 16, Width = 44, Margin = new Thickness(6, 0, 0, 0), ToolTip = L.T("Wyślij") };
            var fileBtn = new Button { Content = KomIcon("attach"), FontSize = 16, Width = 44, Margin = new Thickness(0, 0, 6, 0), ToolTip = L.T("Załącz plik") };
            var micBtn = new Button { Content = KomIcon("mic"), FontSize = 16, Width = 44, Margin = new Thickness(0, 0, 6, 0), ToolTip = L.T("Wiadomość głosowa: kliknij, mów, kliknij ponownie, by wysłać") };
            var locBtn = new Button { Content = "📍", FontSize = 16, Width = 44, Margin = new Thickness(0, 0, 6, 0), ToolTip = L.T("Wyślij moją lokalizację (link do mapy)") };
            var camBtn = new Button { Content = "📷", FontSize = 16, Width = 44, Margin = new Thickness(0, 0, 6, 0), ToolTip = L.T("Zrób zdjęcie kamerą i wyślij (pełna jakość)") };
            var vidBtn = new Button { Content = "🎬", FontSize = 16, Width = 44, Margin = new Thickness(0, 0, 6, 0), ToolTip = L.T("Nagraj wiadomość wideo") };
            var voiceBtn = new Button { Content = KomIcon("phone"), FontSize = 16, Width = 44, Margin = new Thickness(0, 0, 6, 0), ToolTip = L.T("Rozmowa głosowa") };
            var galBtn = new Button { Content = KomIcon("gallery"), FontSize = 16, Width = 44, Margin = new Thickness(0, 0, 6, 0), ToolTip = L.T("Zdjęcia, filmy i pliki z rozmowy") };
            var callBtn = new Button { Content = KomIcon("video"), FontSize = 16, Width = 44, Margin = new Thickness(0, 0, 6, 0), ToolTip = L.T("Rozmowa wideo") };
            // 🖥 pomoc zdalna - osobny przycisk z napisem (wcześniej tylko w rozmowie wideo - łatwo pomylić)
            var helpBtn = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { KomIcon("screen", 18), new TextBlock { Text = " " + L.T("Pomoc zdalna"), VerticalAlignment = VerticalAlignment.Center } } }, FontSize = 13, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 6, 0), ToolTip = L.T("Pomoc zdalna: pokaż swój ekran albo zobacz ekran rozmówcy; sterowanie myszą tylko za zgodą") };
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
            // jak w WhatsApp na komputerze: na dole 📎 (menu), pole tekstu i 🎤 / ➤; rozmowy, galeria i szukanie - w nagłówku
            micBtn.Margin = sendBtn.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(fileBtn, Dock.Left); DockPanel.SetDock(micBtn, Dock.Right); DockPanel.SetDock(sendBtn, Dock.Right);
            // 😀 panel emoji Windows (to samo co Win+.) - przy polu wiadomości
            var emojiBtn = new Button { Content = KomIcon("emoji"), FontSize = 16, Width = 44, Margin = new Thickness(0, 0, 6, 0), ToolTip = L.T("Emotki (Win+.)") };
            emojiBtn.Click += (s0, e0) => { input.Focus(); keybd_event(0x5B, 0, 0, 0); keybd_event(0xBE, 0, 0, 0); keybd_event(0xBE, 0, 2, 0); keybd_event(0x5B, 0, 2, 0); };
            DockPanel.SetDock(emojiBtn, Dock.Left); buttons.Add(emojiBtn);
            bottom.Children.Add(fileBtn); bottom.Children.Add(emojiBtn); bottom.Children.Add(sendBtn); bottom.Children.Add(micBtn); bottom.Children.Add(input);
            // ☑ zaznaczanie kilku wiadomości: przekaż, kopiuj, usuń u mnie
            bool selMode = false; var picked = new HashSet<KomMsg>();
            var selText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
            var selBar = new DockPanel { Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
            Button SelBtn(string t, string tip) { var b = new Button { Content = t, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0), ToolTip = tip }; DockPanel.SetDock(b, Dock.Left); selBar.Children.Add(b); buttons.Add(b); return b; }
            var selFwd = SelBtn("↪ " + L.T("Przekaż"), L.T("Przekaż zaznaczone")); var selCopy = SelBtn("📋 " + L.T("Kopiuj"), L.T("Kopiuj zaznaczone")); var selDel = SelBtn("🗑 " + L.T("Usuń u mnie"), L.T("Usuń zaznaczone tylko na tym komputerze")); var selEnd = SelBtn("✕", L.T("Zakończ zaznaczanie"));
            selBar.Children.Add(selText); DockPanel.SetDock(selBar, Dock.Bottom);
            var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 0, 0, 6) };
            foreach (var b in new[] { galBtn, voiceBtn, callBtn, helpBtn }) { b.Height = 34; b.Margin = new Thickness(0, 0, 6, 0); tools.Children.Add(b); }
            tools.Children.Add(searchBox); searchBox.Margin = new Thickness(0); searchBox.Height = 34;
            DockPanel.SetDock(tools, Dock.Right); top.Children.Add(tools); top.Children.Add(header);
            void SendOrMic() { bool t = input.Text.Trim().Length > 0 && !Equals(micBtn.Tag, "rec"); sendBtn.Visibility = t ? Visibility.Visible : Visibility.Collapsed; micBtn.Visibility = t ? Visibility.Collapsed : Visibility.Visible; }
            input.TextChanged += (s0, e0) => SendOrMic(); SendOrMic();
            // ↩ odpowiedź na wiadomość: cytat nad polem (kliknięcie - anuluj), idzie przed tekstem
            KomMsg replyTo = null;
            var replyBar = new TextBlock { FontSize = 12, FontStyle = FontStyles.Italic, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 4, 6, 0), Visibility = Visibility.Collapsed, Cursor = Cursors.Hand, ToolTip = L.T("Anuluj odpowiedź") };
            replyBar.MouseLeftButtonUp += (s, e) => { replyTo = null; replyBar.Visibility = Visibility.Collapsed; };
            DockPanel.SetDock(replyBar, Dock.Bottom);
            var msgs = new StackPanel { Margin = new Thickness(4) };
            var scroll = new ScrollViewer { Content = msgs, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            right.Children.Add(top); right.Children.Add(bottom); right.Children.Add(selBar); right.Children.Add(replyBar); right.Children.Add(status); right.Children.Add(scroll);
            root.Children.Add(right);
            w.Content = root;
            buttons.Add(sendBtn); buttons.Add(fileBtn); buttons.Add(micBtn); buttons.Add(vidBtn); buttons.Add(camBtn); buttons.Add(locBtn); buttons.Add(callBtn); buttons.Add(voiceBtn); buttons.Add(galBtn); buttons.Add(helpBtn);
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
                searchBox.Background = B(pal.Input); searchBox.BorderBrush = B(pal.Border); searchBox.Foreground = B(pal.Fg); searchBox.CaretBrush = B(pal.Fg);
                header.Foreground = B(pal.Fg); replyBar.Foreground = B(pal.Dim); statusText.Foreground = B(pal.Dim); statusBar.Foreground = B(pal.Mine); statusBar.Background = B(pal.Input); statusBar.BorderBrush = B(pal.Border);
                foreach (var b in buttons) { b.Background = B(pal.Button); b.Foreground = B(pal.Fg); b.BorderBrush = B(pal.Border); }
                sendBtn.Background = B(pal.Mine); sendBtn.Foreground = B(pal.MineFg);
                try { var h = new System.Windows.Interop.WindowInteropHelper(w).Handle; if (h != IntPtr.Zero) { int on = pal.Dark ? 1 : 0; DwmSetWindowAttribute(h, 20, ref on, 4); } } catch (Exception) { }
            }
            ApplyTheme();

            KomContact Current() => KomContactById(_komOpenId ?? "");
            // bez migania: lista i rozmowa przebudowywane tylko, gdy coś naprawdę się zmieniło (obecność innych, potwierdzenia, zegar 30 s - zwykle nic)
            string listSig = null, chatSig = null;
            void FillList()
            {
                var sel = _komOpenId;
                var ls = new StringBuilder(pal.Key ?? "").Append(sel).Append('|');
                foreach (var x in _komContacts.ToList())
                {
                    var lm = KomChat(x.Id).LastOrDefault(); _komUnread.TryGetValue(x.Id, out var un0);
                    ls.Append(x.Id).Append(x.Name).Append(KomOnline(x.Id) ? '1' : '0').Append(KomViaNet(x.Id) ? '1' : '0').Append(un0).Append(KomPinned(x.Id) ? 'p' : '-').Append(KomMuted(x.Id) ? 'm' : '-')
                      .Append(_komTypingAt.TryGetValue(x.Id, out var ty0) && DateTime.UtcNow - ty0 < TimeSpan.FromSeconds(6) ? 't' : '-').Append(lm?.Mid).Append(lm?.Ok).Append(lm?.Read).Append(lm?.Text?.GetHashCode()).Append(KomPhoto(x.Id) is string pp && File.Exists(pp) ? File.GetLastWriteTimeUtc(pp).Ticks : 0).Append('|');   // nowe zdjęcie profilowe też odświeża
                }
                if (ls.ToString() == listSig) return;
                listSig = ls.ToString();
                list.Items.Clear();
                if (_komContacts.Count == 0)
                    list.Items.Add(new ListBoxItem { IsEnabled = false, Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = L.T("Brak kontaktów. Kliknij „➕ Dodaj”, gdy drugie urządzenie ma otwarty komunikator Velivo w tej samej sieci.") } });
                foreach (var c in _komContacts.OrderByDescending(x => KomPinned(x.Id)).ThenByDescending(x => KomChat(x.Id).LastOrDefault()?.Ts ?? 0))   // 📌 przypięte na górze
                {
                    var last = KomChat(c.Id).LastOrDefault();
                    _komUnread.TryGetValue(c.Id, out var un);
                    bool on = KomOnline(c.Id);
                    var tb = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, Opacity = on ? 1 : 0.7 };
                    tb.Inlines.Add(new System.Windows.Documents.Run("● ") { Foreground = on ? new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)) : new SolidColorBrush(pal.Dim), FontSize = 14 });
                    if (on) tb.Inlines.Add(new System.Windows.Documents.Run(KomViaNet(c.Id) ? "🌍 " : "🏠 ") { ToolTip = KomViaNet(c.Id) ? L.T("przez internet") : L.T("sieć domowa") });
                    tb.Inlines.Add(new System.Windows.Documents.Run((KomPinned(c.Id) ? "📌 " : "") + c.Name + (KomMuted(c.Id) ? " 🔕" : "")) { FontWeight = un > 0 ? FontWeights.Bold : FontWeights.SemiBold });
                    if (c.Own && !string.IsNullOrEmpty(c.Device) && c.Device != c.Name) tb.Inlines.Add(new System.Windows.Documents.Run("  " + c.Device) { FontSize = 11, Foreground = new SolidColorBrush(pal.Dim) });   // prawdziwe urzadzenie
                    var dr = KomDraft(c.Id);   // ✏ niedokończona wiadomość - jak w WhatsApp
                    var green = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
                    if (_komTypingAt.TryGetValue(c.Id, out var ty1) && DateTime.UtcNow - ty1 < TimeSpan.FromSeconds(6)) { tb.Inlines.Add(new System.Windows.Documents.LineBreak()); tb.Inlines.Add(new System.Windows.Documents.Run("✍ " + L.T("pisze…")) { FontSize = 12, Foreground = green }); }   // jak w WhatsApp - „pisze…” na liście
                    else if (dr.Length > 0) { tb.Inlines.Add(new System.Windows.Documents.LineBreak()); tb.Inlines.Add(new System.Windows.Documents.Run("✏ " + L.T("Szkic: ") + dr.Replace('\n', ' ')) { FontSize = 12, Foreground = green }); }
                    else if (last != null)
                    {   // ostatnia wiadomość; moja - z ptaszkami (✓ wysłana, ✓✓ doręczona, niebieskie - przeczytana)
                        tb.Inlines.Add(new System.Windows.Documents.LineBreak());
                        if (last.Mine) tb.Inlines.Add(new System.Windows.Documents.Run(last.Read || (last.Ok && !last.Text.Contains("📬")) ? "✓✓ " : "✓ ") { FontSize = 12, FontWeight = FontWeights.Bold, Foreground = last.Read ? new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)) : new SolidColorBrush(pal.Dim) });
                        tb.Inlines.Add(new System.Windows.Documents.Run(last.Text.Replace('\n', ' ')) { FontSize = 12, Foreground = new SolidColorBrush(pal.Dim) });
                    }
                    var pic = KomAvatarEl(c, on, 44, pal); pic.Margin = new Thickness(0, 0, 10, 0);   // 🖼 zdjęcie albo litera (zielona obwódka - w sieci)
                    // po prawej: czas ostatniej wiadomości i zielone kółko z liczbą nieprzeczytanych (jak w WhatsApp)
                    var side = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    if (last != null) side.Children.Add(new TextBlock { Text = KomWhen(last.Ts), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, Foreground = un > 0 ? green : new SolidColorBrush(pal.Dim) });
                    if (un > 0) side.Children.Add(new Border { Background = green, CornerRadius = new CornerRadius(10), MinWidth = 20, Height = 20, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(0, 3, 0, 0), HorizontalAlignment = HorizontalAlignment.Right,
                        Child = new TextBlock { Text = un > 99 ? "99+" : un.ToString(), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
                    var dp = new DockPanel(); DockPanel.SetDock(pic, Dock.Left); DockPanel.SetDock(side, Dock.Right); dp.Children.Add(pic); dp.Children.Add(side); dp.Children.Add(tb); object row = dp;
                    var item = new ListBoxItem { Content = row, Tag = c.Id, Padding = new Thickness(6), ToolTip = L.T("Rozmowa z ") + c.Name + " – " + (on ? L.T("w sieci") : L.T("poza siecią")) };
                    var cm = new ContextMenu();
                    var ren = new MenuItem { Header = L.T("✏ Zmień nazwę") }; ren.Click += (s, e) => KomRename(c);
                    var del = new MenuItem { Header = L.T("🗑 Usuń kontakt i rozmowę") }; del.Click += (s, e) =>
                    {
                        if (MessageBox.Show(w, (L.En ? "Delete “" + c.Name + "” and the whole chat?" : "Usunąć „" + c.Name + "” i całą rozmowę?"), L.T("Komunikator Velivo"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                        _komContacts.Remove(c); _komChats.Remove(c.Id); _komUnread.Remove(c.Id); if (_komOpenId == c.Id) _komOpenId = null; KomSave(); _komRefresh?.Invoke();
                    };
                    var med = new MenuItem { Header = L.T("🎬 Wyślij Filmy i TV") }; med.Click += async (s, e) => await KomSendMedia(w, c);
                    var mini = new MenuItem { Header = L.T("🔝 Osobne okienko (na wierzchu)") }; mini.Click += (s, e) => KomMini(c);
                    var exp = new MenuItem { Header = L.T("💾 Eksport rozmowy…") }; exp.Click += (s, e) => KomExport(w, c);
                    cm.Items.Add(mini); cm.Items.Add(exp);
                    var pin = new MenuItem { Header = KomPinned(c.Id) ? "📌 " + L.T("Odepnij") : "📌 " + L.T("Przypnij na górze") }; pin.Click += (s, e) => KomSetPinned(c.Id, !KomPinned(c.Id));
                    var mute = new MenuItem { Header = KomMuted(c.Id) ? "🔔 " + L.T("Wyłącz wyciszenie") : "🔕 " + L.T("Wycisz") };
                    if (KomMuted(c.Id)) mute.Click += (s, e) => KomMute(c.Id, 0);
                    else foreach (var (lab, ms) in new[] { (L.T("8 godzin"), 8 * 3600000L), (L.T("Tydzień"), 7 * 86400000L), (L.T("Zawsze"), long.MaxValue) }) { var mi = new MenuItem { Header = lab }; mi.Click += (s, e) => KomMute(c.Id, ms); mute.Items.Add(mi); }
                    var ttl = new MenuItem { Header = "⏳ " + L.T("Znikające wiadomości – u obu stron") };
                    foreach (var (lab, h) in new[] { (L.T("Wyłączone"), 0), ("24 h", 24), (L.T("7 dni"), 7 * 24), (L.T("90 dni"), 90 * 24) }) { var mi = new MenuItem { Header = lab, IsCheckable = true, IsChecked = KomTtl(c.Id) == h }; mi.Click += (s, e) => KomSetTtl(c, h); ttl.Items.Add(mi); }
                    cm.Items.Add(ren); cm.Items.Add(pin); cm.Items.Add(mute); cm.Items.Add(ttl);
                    if (!c.IsGroup)
                    {
                        var safe = new MenuItem { Header = "🛡 " + L.T("Kod bezpieczeństwa") };
                        safe.Click += (s, e) => MessageBox.Show(w, KomSafety(c) + "\n\n" + L.T("Porównajcie ten kod (u drugiej osoby: menu rozmowy z Tobą). Ten sam = rozmowa szyfrowana tylko między Wami, nikt się nie podszywa."), "🛡 " + c.Name);
                        cm.Items.Add(safe);
                        var clip = new MenuItem { Header = "📋 " + L.T("Wspólny schowek z tym urządzeniem"), IsCheckable = true, IsChecked = KomClipOn(c.Id), ToolTip = L.T("Tekst skopiowany tutaj pojawia się w schowku tamtego urządzenia i odwrotnie – szyfrowane, bez haseł") };
                        clip.Click += (s, e) => { _komOpt["schowek-" + c.Id] = clip.IsChecked; KomSave(); KomClipWatch(); KomInfo("📋 " + L.T("Wspólny schowek: ") + c.Name + " – " + (clip.IsChecked ? L.T("wł. (włącz też na tamtym urządzeniu)") : L.T("wył."))); };
                        cm.Items.Add(clip);
                        var upd = new MenuItem { Header = "📲 " + L.T("Wyślij aktualizację aplikacji (APK)…"), ToolTip = L.T("Nowa wersja Velivo na telefon tej osoby – tam jedno stuknięcie „Zainstaluj”") }; upd.Click += async (s, e) => await KomSendUpdate(w, c);
                        cm.Items.Add(upd);
                    }
                    cm.Items.Add(med); cm.Items.Add(del); item.ContextMenu = cm;
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
                else if (c.IsGroup)
                {   // 👥 grupa: skład zamiast „w sieci”
                    header.Inlines.Add(new System.Windows.Documents.Run("👥 " + c.Name + "  "));
                    header.Inlines.Add(new System.Windows.Documents.Run(string.Join(", ", KomMembers(c).Select(id => KomContactById(id)?.Name ?? "…"))) { FontSize = 12, FontWeight = FontWeights.Normal, Foreground = new SolidColorBrush(pal.Dim) });
                }
                else
                {
                    bool on = KomOnline(c.Id);
                    var hav = KomAvatarEl(c, on, 32, pal); hav.Margin = new Thickness(0, 0, 8, -6);   // 🖼 zdjęcie profilowe rozmówcy (bez zdjęcia - litera)
                    header.Inlines.Add(new System.Windows.Documents.InlineUIContainer(hav));
                    header.Inlines.Add(new System.Windows.Documents.Run(c.Name + "  "));
                    if (c.Own && !string.IsNullOrEmpty(c.Device) && c.Device != c.Name) header.Inlines.Add(new System.Windows.Documents.Run("(" + c.Device + ")  ") { FontSize = 12, FontWeight = FontWeights.Normal, Foreground = new SolidColorBrush(pal.Dim) });
                    if (_komTypingAt.TryGetValue(c.Id, out var ty) && DateTime.UtcNow - ty < TimeSpan.FromSeconds(6)) { header.Inlines.Add(new System.Windows.Documents.Run("✍ " + L.T("pisze…")) { FontSize = 12, FontWeight = FontWeights.Normal, Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)) }); goto headerDone; }
                    var seen = _komSeenAt.TryGetValue(c.Id, out var sa) ? " · " + L.T("ostatnio ") + (sa.Date == DateTime.Today ? sa.ToString("HH:mm") : sa.ToString("g")) : "";
                    header.Inlines.Add(new System.Windows.Documents.Run(on ? (KomViaNet(c.Id) ? L.T("● w sieci · 🌍 przez internet") : L.T("● w sieci · 🏠 sieć domowa")) : L.T("● poza siecią") + seen + " – " + L.T("wiadomości dojdą, gdy wróci"))
                        { FontSize = 12, FontWeight = FontWeights.Normal, Foreground = on ? new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)) : new SolidColorBrush(pal.Dim) });
                    headerDone:;
                }
                input.IsEnabled = sendBtn.IsEnabled = c != null;
                fileBtn.IsEnabled = micBtn.IsEnabled = vidBtn.IsEnabled = camBtn.IsEnabled = locBtn.IsEnabled = c != null; callBtn.IsEnabled = voiceBtn.IsEnabled = helpBtn.IsEnabled = c != null && !c.IsGroup; galBtn.IsEnabled = c != null;   // grupa: tekst, pliki, głosówki (rozmowa wideo - 1:1)
                var cs = new StringBuilder(pal.Key ?? "").Append(_komAutoImages).Append(c?.Id).Append('|').Append(searchBox.Text).Append('|').Append(selMode).Append(picked.Count).Append('|');
                if (c != null) foreach (var m in KomChat(c.Id).Skip(Math.Max(0, KomChat(c.Id).Count - 300))) cs.Append(m.Mid).Append(m.Ok ? '1' : '0').Append(m.Read ? '1' : '0').Append(m.Text?.GetHashCode()).Append(m.Path).Append(KomReactions(m)).Append(m.Lp?.Length).Append(';');
                if (cs.ToString() == chatSig) return;   // nagłówek (w sieci, pisze…) odświeżony wyżej; dymki bez zmian - bez przebudowy
                chatSig = cs.ToString();
                msgs.Children.Clear();
                if (c == null) return;
                var q = searchBox.Text.Trim();   // 🔍 szukanie w rozmowie
                DateTime day = default; bool? prevMine = null;
                foreach (var m in KomChat(c.Id).Skip(Math.Max(0, KomChat(c.Id).Count - 300)).Where(x => q.Length == 0 || x.Text.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
                {
                    var time = DateTimeOffset.FromUnixTimeMilliseconds(m.Ts).LocalDateTime;
                    if (time.Date != day)
                    {   // 📅 dzień nad wiadomościami (Dzisiaj, Wczoraj, data) - jak w WhatsApp
                        day = time.Date; prevMine = null;
                        msgs.Children.Add(new Border { Background = new SolidColorBrush(pal.Theirs), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 10, 0, 6), HorizontalAlignment = HorizontalAlignment.Center,
                            Child = new TextBlock { Text = KomDay(day), FontSize = 11, Foreground = new SolidColorBrush(pal.Dim) } });
                    }
                    var stamp = (KomReactions(m) is { Length: > 0 } rx ? rx + "  " : "") + time.ToString("HH:mm") + (m.Mine ? (m.Read || (m.Ok && !m.Text.Contains("📬")) ? " ✓✓" : " ✓") : "");
                    var body = new StackPanel();
                    // kolory dymkow z motywu komunikatora (bez ramek pola tekstowego)
                    var fg = new SolidColorBrush(m.Mine ? pal.MineFg : pal.Fg);
                    // zdjecie: miniatura w dymku; klikniecie - pelny obraz; plik - klikniecie otwiera
                    bool hasFile = !string.IsNullOrEmpty(m.Path) && File.Exists(m.Path);
                    if (hasFile && KomIsImage(m.Path) && KomThumb(m.Path) is ImageSource th)
                    {
                        FrameworkElement img = new Image { Source = th, MaxWidth = 260, MaxHeight = 260, Stretch = Stretch.Uniform, Cursor = Cursors.Hand, Margin = new Thickness(0, 2, 0, 4), ToolTip = L.T("Otwórz zdjęcie") };
                        if (KomIsGif(m.Path)) img = KomGifView(m.Path, 260);   // 🎞 GIF rusza się w rozmowie
                        var ip = m.Path; img.MouseLeftButtonUp += (s2, e2) => { if (!selMode) KomViewer(w, c, ip); };   // 🖼 podgląd w Velivo (← → po zdjęciach rozmowy)
                        KomDragOut(img, ip);
                        body.Children.Add(img);
                    }
                    if (!hasFile && m.Lp != null) try
                    {   // 🔗 podgląd linku (jak w WhatsApp): serwis, tytuł, opis - kliknięcie otwiera stronę
                        var lj = JsonNode.Parse(m.Lp); var lu = (string)lj["u"] ?? "";
                        var card = new StackPanel();
                        card.Children.Add(new TextBlock { Text = (string)lj["s"] ?? "", FontSize = 11, Foreground = fg, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis });
                        card.Children.Add(new TextBlock { Text = (string)lj["t"] ?? "", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = fg, TextWrapping = TextWrapping.Wrap, MaxHeight = 40, TextTrimming = TextTrimming.CharacterEllipsis });
                        if (((string)lj["d"] ?? "").Length > 0) card.Children.Add(new TextBlock { Text = (string)lj["d"], FontSize = 12, Foreground = fg, Opacity = 0.85, TextWrapping = TextWrapping.Wrap, MaxHeight = 50, TextTrimming = TextTrimming.CharacterEllipsis });
                        var lb = new Border { Child = card, Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), BorderBrush = new SolidColorBrush(m.Mine ? pal.MineFg : pal.Mine), BorderThickness = new Thickness(3, 0, 0, 0), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 5, 8, 6), Margin = new Thickness(0, 2, 0, 6), MaxWidth = 340, Cursor = Cursors.Hand, ToolTip = lu };
                        if (lu.StartsWith("http", StringComparison.OrdinalIgnoreCase)) lb.MouseLeftButtonUp += (s2, e2) => { if (!selMode) { AddTab(lu); Activate(); } };
                        body.Children.Add(lb);
                    }
                    catch (Exception) { }
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
                    if (!hasFile && m.Text.StartsWith("› ", StringComparison.Ordinal) && txt.Inlines.FirstInline is System.Windows.Documents.Run q0 && q0.Text.IndexOf('\n') is int qe && qe > 0)
                    {   // ↩ cytat: pierwszy wiersz kursywą, przygaszony
                        var rest = q0.Text.Substring(qe); q0.Text = q0.Text.Substring(0, qe); q0.FontStyle = FontStyles.Italic; q0.Foreground = new SolidColorBrush(Color.FromArgb(0xB0, fg.Color.R, fg.Color.G, fg.Color.B));
                        txt.Inlines.InsertAfter(q0, new System.Windows.Documents.Run(rest));
                    }
                    if (hasFile)
                    {   // 🎤 głosówka - w komunikatorze; 🖼 🎬 - podgląd w Velivo; inne - program Windows; 🖱 przeciągnięcie - plik na pulpit / do folderu
                        var fp = m.Path; txt.Cursor = Cursors.Hand; KomDragOut(txt, fp);
                        txt.MouseLeftButtonUp += (s2, e2) =>
                        {
                            if (selMode) return;
                            if (Path.GetFileName(fp).Contains("Glosowa-")) { KomPlayVoice(fp); return; }
                            if (KomIsVideo(fp)) { PlayVideos(new[] { fp }, 0, false); return; }   // 🎬 film - odtwarzacz Velivo (VLC, wszystkie kodeki), bez dopisywania do biblioteki
                            if (KomIsImage(fp)) { KomViewer(w, c, fp); return; }
                            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fp) { UseShellExecute = true }); } catch (Exception) { }
                        };
                    }
                    var copy = new MenuItem { Header = L.T("Kopiuj") }; var copyText = m.Text; var copyFile = hasFile ? m.Path : null;
                    copy.Click += (s2, e2) =>   // 📋 obraz - sam obraz (do wklejenia w programy) i plik (do folderu); inny plik - plik; tekst - tekst
                    {
                        try
                        {
                            if (copyFile == null) { Clipboard.SetText(copyText); return; }
                            var d = new DataObject(); d.SetFileDropList(new System.Collections.Specialized.StringCollection { copyFile });
                            if (KomIsImage(copyFile)) try { var b = new System.Windows.Media.Imaging.BitmapImage(); b.BeginInit(); b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; b.UriSource = new Uri(copyFile); b.EndInit(); d.SetImage(b); } catch (Exception) { }
                            Clipboard.SetDataObject(d, true);
                        }
                        catch (Exception) { }
                    };
                    var rm = m; var rc = c;
                    var rep = new MenuItem { Header = "↩ " + L.T("Odpowiedz") }; rep.Click += (s2, e2) => { replyTo = rm; replyBar.Text = "↩ " + KomQuote(rm).Trim() + "   ✕"; replyBar.Visibility = Visibility.Visible; input.Focus(); };
                    var react = new MenuItem { Header = "😀 " + L.T("Reakcja") };
                    foreach (var em in new[] { "👍", "❤️", "😂", "😮", "😢", "🙏" }) { var ei = new MenuItem { Header = em }; var e0 = em; ei.Click += (s2, e2) => KomReact(rc, rm, e0); react.Items.Add(ei); }
                    var fwd = new MenuItem { Header = "↪ " + L.T("Przekaż…") }; fwd.Click += async (s2, e2) =>
                    {
                        foreach (var to in KomPickContacts(w, "↪ " + L.T("Przekaż do…"), rc.Id))
                            if (rm.File && File.Exists(rm.Path ?? "")) await AttachTo(to, new[] { rm.Path }, true); else KomSend(to, "↪ " + rm.Text);
                        FillList(); FillChat();
                    };
                    var selIt = new MenuItem { Header = "☑ " + L.T("Zaznacz…") }; selIt.Click += (s2, e2) => { selMode = true; picked.Add(rm); FillChat(); };
                    txt.ContextMenu = new ContextMenu { Items = { rep, react, fwd, copy, selIt } };
                    if (hasFile && Path.GetFileName(m.Path).Contains("Glosowa-"))
                    {   // ⏩ szybkość głosówek 1× / 1,5× / 2×
                        var sp = new MenuItem { Header = "⏩ " + L.T("Szybkość głosówek: ") + KomSpeedText(KomVoiceSpeed) + " → " + KomSpeedText(KomNextSpeed()) };
                        sp.Click += (s2, e2) => { _komOpt["glos-x"] = KomNextSpeed(); KomSave(); if (_komVoice != null) _komVoice.SpeedRatio = KomVoiceSpeed; };
                        txt.ContextMenu.Items.Add(sp);
                    }
                    if (m.Mine && !m.File && !m.Text.StartsWith("🚫") && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - m.Ts < 15 * 60000)
                    {   // ✏ jak w WhatsApp: do 15 min
                        var ed = new MenuItem { Header = "✏ " + L.T("Edytuj") };
                        ed.Click += (s2, e2) => { var nt = KomAsk(w, "✏ " + L.T("Edytuj wiadomość"), rm.Text.EndsWith(" ✏", StringComparison.Ordinal) ? rm.Text.Substring(0, rm.Text.Length - 2) : rm.Text); if (!string.IsNullOrWhiteSpace(nt)) KomEdit(rc, rm, nt.Trim()); };
                        txt.ContextMenu.Items.Add(ed);
                    }
                    if (m.Mine && !m.Text.StartsWith("🚫"))
                    {   // 🚫 jak w WhatsApp
                        var del = new MenuItem { Header = "🚫 " + L.T("Usuń u wszystkich") }; var dm = m; var dc = c;
                        del.Click += (s2, e2) => { if (MessageBox.Show(w, L.T("Usunąć tę wiadomość u wszystkich?"), "Velivo", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) KomDeleteForAll(dc, dm); };
                        txt.ContextMenu.Items.Add(del);
                    }
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
                        foreach (var ch in body.Children.OfType<FrameworkElement>().Where(x => x is Image or MediaElement)) ch.ContextMenu = txt.ContextMenu;
                    }
                    body.Children.Add(txt);
                    if (!m.Mine && !hasFile && KomScamWarning(m.Text) is string scam)   // 🛡 podejrzany link / prośba o kod lub dane karty
                        body.Children.Add(new Border { Background = new SolidColorBrush(scam.StartsWith("⚠") ? Color.FromRgb(0xDC, 0x26, 0x26) : Color.FromArgb(0x50, 0xF5, 0x9E, 0x0B)), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 4, 8, 5), Margin = new Thickness(0, 6, 0, 2),
                            Child = new TextBlock { Text = scam, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = scam.StartsWith("⚠") ? Brushes.White : fg } });
                    if (!m.Mine && !hasFile && m.Text.Contains("VELIVO-KONTAKT:"))
                    {   // 🔗 czyjś „Mój adres Velivo” w rozmowie - jedno kliknięcie zamiast kopiowania
                        var ask = new Button { Content = "👤 " + L.T("Wyślij prośbę o kontakt"), Margin = new Thickness(0, 6, 0, 2), Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand, ToolTip = L.T("Ta osoba dostanie prośbę i zdecyduje: Dodaj / Odrzuć") };
                        var at0 = m.Text; ask.Click += (s2, e2) => KomInfo(KomRequestByLink(at0) ? "👤 " + L.T("Prośba wysłana – kontakt pojawi się, gdy ta osoba ją przyjmie") : L.T("To nie jest adres Velivo"));
                        body.Children.Add(ask);
                    }
                    var stampTb = new TextBlock { FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right, Foreground = fg, ToolTip = m.Mine && !m.Ok ? L.T("Jeszcze nie doręczono – Velivo wyśle, gdy odbiorca będzie w sieci") : m.Mine && m.Read ? L.T("Przeczytane") : null };
                    // przeczytane (✓✓) na niebiesko - jak w telefonie
                    if (m.Mine && m.Read) { stampTb.Inlines.Add(new System.Windows.Documents.Run(stamp.Substring(0, stamp.Length - 3)) { Foreground = new SolidColorBrush(Color.FromArgb(0xB3, fg.Color.R, fg.Color.G, fg.Color.B)) }); stampTb.Inlines.Add(new System.Windows.Documents.Run(" ✓✓") { Foreground = new SolidColorBrush(pal.Key == "kontrast" ? Color.FromRgb(0, 0, 0xCC) : Color.FromRgb(0x7D, 0xF9, 0xFF)), FontWeight = FontWeights.Bold }); }
                    else { stampTb.Text = stamp; stampTb.Opacity = 0.7; }
                    body.Children.Add(stampTb);
                    var bubble = new Border
                    {
                        // pierwszy dymek z serii - „ogonek” (ostry róg od strony nadawcy); kolejne od tej samej osoby bliżej siebie
                        Child = body, CornerRadius = prevMine == m.Mine ? new CornerRadius(12) : m.Mine ? new CornerRadius(12, 3, 12, 12) : new CornerRadius(3, 12, 12, 12),
                        Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(m.Mine ? 60 : 0, prevMine == m.Mine ? 1 : 6, m.Mine ? 0 : 60, 1),
                        HorizontalAlignment = m.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                        Background = new SolidColorBrush(m.Mine ? pal.Mine : pal.Theirs)
                    };
                    if (selMode)
                    {   // ☑ kliknięcie dymka - zaznacz / odznacz
                        var sm = m; bool on = picked.Contains(sm);
                        bubble.BorderThickness = new Thickness(3); bubble.BorderBrush = on ? new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)) : Brushes.Transparent; bubble.Cursor = Cursors.Hand;
                        body.Children.Insert(0, new TextBlock { Text = on ? "☑" : "☐", Foreground = fg, FontSize = 14 });
                        bubble.PreviewMouseLeftButtonUp += (s2, e2) => { if (!picked.Remove(sm)) picked.Add(sm); e2.Handled = true; FillChat(); };
                    }
                    else
                    {   // menu wiadomości w całym dymku: prawy przycisk albo przytrzymanie (mysz / palec - jak w telefonie)
                        bubble.ContextMenu = txt.ContextMenu;
                        var holdT = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) }; var hb = bubble;
                        holdT.Tick += (s2, e2) => { holdT.Stop(); if (hb.ContextMenu is ContextMenu cm0) { cm0.PlacementTarget = hb; cm0.IsOpen = true; } };
                        bubble.PreviewMouseLeftButtonDown += (s2, e2) => holdT.Start();
                        bubble.PreviewMouseLeftButtonUp += (s2, e2) => holdT.Stop();
                        bubble.MouseLeave += (s2, e2) => holdT.Stop();
                    }
                    msgs.Children.Add(bubble); prevMine = m.Mine;
                }
                if (!selMode) scroll.ScrollToEnd();
                selBar.Visibility = selMode ? Visibility.Visible : Visibility.Collapsed; bottom.Visibility = selMode ? Visibility.Collapsed : Visibility.Visible;
                selText.Text = L.T("Zaznaczone: ") + picked.Count;
                if (_komUnread.Remove(c.Id)) { KomSave(); FillList(); KomTrayUpdate(); }
                if (w.IsActive) KomSendRead(c);
            }
            // odswiezanie okna najwyzej co 0,4 s - seria pakietow (obecnosc, potwierdzenia) nie przebudowuje rozmowy setki razy
            // okno schowane (zasobnik) albo zminimalizowane - nic nie przebudowujemy (to samo wątek co całe Velivo: przeglądarka, filmy); po pokazaniu raz
            bool refreshQueued = false, stale = false;
            bool Shown() => w.IsVisible && w.WindowState != WindowState.Minimized;
            void ShownAgain() { if (stale && Shown() && _komWindow == w) { stale = false; FillList(); FillChat(); } }
            w.IsVisibleChanged += (s, e) => ShownAgain(); w.StateChanged += (s, e) => ShownAgain();
            var refreshTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            refreshTimer.Tick += (s, e) => { refreshTimer.Stop(); refreshQueued = false; if (_komWindow == w) { if (!Shown()) { stale = true; return; } FillList(); FillChat(); } };
            _komRefresh = () => Dispatcher.BeginInvoke(new Action(() => { if (refreshQueued) return; refreshQueued = true; refreshTimer.Start(); }));
            // ✏ szkic: tekst w polu zostaje przy rozmowie (zmiana rozmowy, zamknięcie okna)
            void Open(string id) { KomSetDraft(_komOpenId, input.Text); _komOpenId = id; selMode = false; picked.Clear(); searchBox.Clear(); input.Text = KomDraft(id); input.CaretIndex = input.Text.Length; }
            input.TextChanged += (s, e) => KomSetDraft(_komOpenId, input.Text);
            searchBox.TextChanged += (s, e) => FillChat();
            _komSelect = id => { Open(id); FillList(); FillChat(); };
            list.SelectionChanged += (s, e) => { if (list.SelectedItem is ListBoxItem it && it.Tag is string id && id != _komOpenId) { Open(id); FillChat(); } };
            void DoSend()
            {
                var c = Current(); var text = input.Text.Trim();
                if (c == null || text.Length == 0) return;
                input.Clear(); KomSend(c, (replyTo != null ? KomQuote(replyTo) : "") + text); replyTo = null; replyBar.Visibility = Visibility.Collapsed; FillList(); FillChat();
            }
            sendBtn.Click += (s, e) => DoSend();
            // ☑ zaznaczone wiadomości
            selEnd.Click += (s, e) => { selMode = false; picked.Clear(); FillChat(); };
            selCopy.Click += (s, e) =>
            {
                var c = Current(); if (c == null || picked.Count == 0) return;
                try { Clipboard.SetText(string.Join("\n", KomChat(c.Id).Where(picked.Contains).Select(x => DateTimeOffset.FromUnixTimeMilliseconds(x.Ts).LocalDateTime.ToString("g") + " " + (x.Mine ? L.T("Ty") : c.Name) + ": " + x.Text))); KomInfo("📋 " + L.T("Skopiowano")); } catch (Exception) { }
            };
            selDel.Click += (s, e) =>
            {
                var c = Current(); if (c == null || picked.Count == 0) return;
                if (MessageBox.Show(w, L.T("Usunąć zaznaczone wiadomości na tym komputerze?"), "Velivo", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                foreach (var x in picked) { KomChat(c.Id).Remove(x); if (x.Path != null && x.Path.StartsWith(KomMediaDir, StringComparison.OrdinalIgnoreCase)) try { File.Delete(x.Path); } catch (Exception) { } }
                selMode = false; picked.Clear(); KomSave(); FillList(); FillChat();
            };
            selFwd.Click += async (s, e) =>
            {
                var c = Current(); if (c == null || picked.Count == 0) return;
                var chosen = KomChat(c.Id).Where(picked.Contains).ToList();
                foreach (var to in KomPickContacts(w, "↪ " + L.T("Przekaż do…"), c.Id))
                    foreach (var x in chosen) if (x.File && File.Exists(x.Path ?? "")) await AttachTo(to, new[] { x.Path }, true); else KomSend(to, "↪ " + x.Text);
                selMode = false; picked.Clear(); FillList(); FillChat();
            };
            // ⌨ skróty: Ctrl+F szukaj, Ctrl+K przejdź do rozmowy, Alt+↑/↓ poprzednia / następna, Ctrl+Shift+M wycisz, Esc - zamknij odpowiedź / szukanie / zaznaczanie
            w.PreviewKeyDown += (s, e) =>
            {
                var key = e.Key == Key.System ? e.SystemKey : e.Key; var mods = Keyboard.Modifiers;
                if (mods == ModifierKeys.Control && key == Key.F) { searchBox.Focus(); searchBox.SelectAll(); }
                else if (mods == ModifierKeys.Control && key == Key.K) KomQuickSwitch(w, id => _komSelect?.Invoke(id));
                else if (mods == ModifierKeys.Alt && (key == Key.Up || key == Key.Down))
                {
                    int i = list.SelectedIndex + (key == Key.Up ? -1 : 1);
                    if (i >= 0 && i < list.Items.Count && list.Items[i] is ListBoxItem it && it.Tag is string id) _komSelect?.Invoke(id);
                }
                else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.M)
                {
                    var c = Current(); if (c == null) return;
                    KomMute(c.Id, KomMuted(c.Id) ? 0 : long.MaxValue); KomInfo(KomMuted(c.Id) ? "🔕 " + L.T("Wyciszono") : "🔔 " + L.T("Wyłączono wyciszenie"));
                }
                else if (key == Key.Escape && (selMode || replyTo != null || searchBox.Text.Length > 0))
                {
                    if (selMode) { selMode = false; picked.Clear(); FillChat(); }
                    else if (replyTo != null) { replyTo = null; replyBar.Visibility = Visibility.Collapsed; }
                    else searchBox.Clear();
                }
                else return;
                e.Handled = true;
            };
            input.PreviewKeyDown += (s, e) => { if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; DoSend(); } };
            // 📎 do zaznaczonego kontaktu: 🟢 w sieci - od razu na znany adres; szukanie tylko, gdy adresu nie znamy
            async Task AttachTo(KomContact c, string[] files = null, bool confirmed = false)
            {
                if (files == null) { var dlg = new Microsoft.Win32.OpenFileDialog { Title = "📎 " + c.Name, Multiselect = true }; if (dlg.ShowDialog(w) != true) return; files = dlg.FileNames; }
                if (!confirmed && !KomPreview(w, c, files)) return;   // 👁 najpierw podgląd i „Wyślij” (jak w WhatsApp)
                if (c.IsGroup) { await KomSendGroupFiles(w, c, files); return; }   // 👥 do każdego w grupie osobno
                IPAddress addr = null;
                if (!KomOnline(c.Id) || !IPAddress.TryParse(c.Addr ?? "", out addr) || IPAddress.IsLoopback(addr)) { addr = (await KomFind()).FirstOrDefault(x => x.Id == c.Id).Addr; }
                await KomSendFilesTo(w, c.Name, addr ?? IPAddress.Loopback, c, files);   // poza domem: tunel przez pośrednika; nie ma nikogo - 📬 skrzynka / czeka u nas
            }
            // 🎤 wiadomość głosowa: klik - nagrywanie (mikrofon Windows), klik ponownie - wysyłka jak plik
            bool recording = false; string recPath = null; DateTime recAt = default;
            micBtn.Click += async (s, e) =>
            {
                var c = Current(); if (c == null) return;
                if (!recording)
                {
                    Directory.CreateDirectory(KomMediaDir); recPath = Path.Combine(KomMediaDir, "Glosowa-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".wav");
                    KomMci("close velivorec"); 
                    if (KomMci("open new type waveaudio alias velivorec") != 0 || KomMci("set velivorec bitspersample 16 channels 1 samplespersec 22050") != 0 || KomMci("record velivorec") != 0)
                    { KomMci("close velivorec"); KomInfo(L.T("Nie udało się włączyć mikrofonu")); return; }
                    recording = true; recAt = DateTime.UtcNow; micBtn.Content = KomIcon("stop"); micBtn.Tag = "rec"; KomInfo("🎤 " + L.T("Nagrywam… kliknij ⏹, by wysłać"));
                    return;
                }
                recording = false; micBtn.Content = KomIcon("mic"); micBtn.Tag = null;
                KomMci("stop velivorec"); bool ok = KomMci("save velivorec \"" + recPath + "\"") == 0; KomMci("close velivorec");
                if (ok && DateTime.UtcNow - recAt > TimeSpan.FromMilliseconds(700) && File.Exists(recPath)) await AttachTo(c, new[] { recPath });
                else try { File.Delete(recPath); } catch (Exception) { }
            };
            fileBtn.ToolTip = L.T("Załącz: plik, zdjęcie kamerą, wiadomość wideo, lokalizacja");
            fileBtn.Click += (s, e) =>
            {   // 📎 menu jak w WhatsApp - przyciski nie zabierają miejsca na tekst
                var cm = new ContextMenu { PlacementTarget = fileBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
                void Add(string text, Action act) { var mi = new MenuItem { Header = text }; mi.Click += (a, b) => act(); cm.Items.Add(mi); }
                Add("📎 " + L.T("Plik / zdjęcie…"), async () => { var c = Current(); if (c != null) await AttachTo(c); });
                foreach (var b in new[] { camBtn, vidBtn, locBtn }) { var bb = b; Add(bb.Content + " " + bb.ToolTip, () => bb.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))); }
                Add("📸 " + L.T("Zrzut ekranu (zaznacz fragment)…"), () => { var c = Current(); if (c != null) KomSnip(async p => await AttachTo(c, new[] { p })); });
                cm.IsOpen = true;
            };
            locBtn.Click += async (s, e) =>
            {   // 📍 jednorazowo: link do mapy (bez śledzenia)
                var c = Current(); if (c == null) return;
                KomInfo("📍 " + L.T("Ustalam położenie…"));
                var ll = await KomLocation();
                if (ll == null) { KomInfo(L.T("Nie udało się ustalić położenia – włącz lokalizację w ustawieniach Windows")); return; }
                KomSend(c, "📍 " + L.T("Moja lokalizacja") + "\nhttps://maps.google.com/?q=" + ll); FillList(); FillChat();
            };
            camBtn.Click += (s, e) => { var c = Current(); if (c != null) KomRecordVideo(w, async p => await AttachTo(c, new[] { p }), photo: true); };
            vidBtn.Click += (s, e) => { var c = Current(); if (c != null) KomRecordVideo(w, async p => await AttachTo(c, new[] { p })); };
            // 🖱 pliki przeciągnięte myszką z Eksploratora na okno rozmowy - wysyłka jak 📎
            w.AllowDrop = true;
            w.PreviewDragOver += (s, e) => { if (!(e.Data.GetDataPresent(DataFormats.FileDrop) || KomGifSource(e.Data) != null) || _komDragOut) return; e.Effects = Current() is KomContact dc ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };   // Preview - pole tekstowe nie przejmuje plików
            w.PreviewDrop += async (s, e) =>
            {
                if (_komDragOut) return;   // plik przeciągnięty z tej rozmowy - bez ponownej wysyłki
                if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                {   // 🎞 GIF przeciągnięty z przeglądarki - oryginał (ruchomy), nie nieruchoma klatka
                    var src = KomGifSource(e.Data); if (src == null) return; e.Handled = true;
                    var gc = Current(); var gp = await KomGifFetch(src); if (gc != null && gp != null) await AttachTo(gc, new[] { gp });
                    return;
                }
                e.Handled = true;
                var c = Current(); if (c == null || e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return;
                var files = dropped.Where(File.Exists).ToArray(); if (files.Length > 0) await AttachTo(c, files);
            };
            // 📋 wklejanie w polu wiadomości (Ctrl+V, Shift+Insert, prawy przycisk → Wklej): pliki skopiowane z pulpitu / Eksploratora
            // albo obraz (np. zrzut ekranu) - wysyłka do rozmowy; tekst wkleja się jak zwykle (komenda Wklej - przed obsługą pola tekstu)
            bool ClipFiles() { try { return Current() != null && (Clipboard.ContainsFileDropList() || Clipboard.ContainsImage()); } catch (Exception) { return false; } }
            CommandManager.AddPreviewCanExecuteHandler(input, (s, e) => { if (e.Command == ApplicationCommands.Paste && ClipFiles()) { e.CanExecute = true; e.Handled = true; } });
            CommandManager.AddPreviewExecutedHandler(input, async (s, e) =>
            {
                if (e.Command != ApplicationCommands.Paste || !ClipFiles()) return;
                e.Handled = true;
                var c = Current(); string[] files = null;
                try
                {
                    if (Clipboard.ContainsFileDropList()) files = Clipboard.GetFileDropList().Cast<string>().Where(File.Exists).ToArray();
                    else if (KomGifSource(Clipboard.GetDataObject()) is string gsrc && await KomGifFetch(gsrc) is string gif) files = new[] { gif };   // 🎞 „Kopiuj obraz” w przeglądarce: oryginalny GIF zamiast nieruchomej klatki
                    else if (Clipboard.GetImage() is System.Windows.Media.Imaging.BitmapSource img)
                    {
                        var p = Path.Combine(Path.GetTempPath(), "Obraz-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".png");
                        using (var fs = File.Create(p)) { var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img)); enc.Save(fs); }
                        files = new[] { p };
                    }
                }
                catch (Exception) { return; }
                if (c != null && files != null && files.Length > 0) await AttachTo(c, files);
            });
            callBtn.Click += (s, e) => KomOpenCall(Current(), true);
            voiceBtn.Click += (s, e) => KomOpenCall(Current(), true, true);
            helpBtn.Click += (s, e) => KomOpenCall(Current(), true, false, true);
            galBtn.Click += (s, e) => { var c = Current(); if (c != null) KomGallery(w, c); };
            leftButtons.Children.Add(Btn("🎨", L.T("Ustawienia komunikatora: wygląd, profil, bezpieczeństwo, kopia zapasowa"), (s, e) =>
            {
                // 6 grup zamiast jednej długiej listy - każda rzecz tam, gdzie się jej szuka
                var cm = new ContextMenu { PlacementTarget = (UIElement)s, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
                var gLook = new MenuItem { Header = L.T("🎨 Wygląd (motyw)") }; var gProf = new MenuItem { Header = L.T("👤 Mój profil") };
                var gSafe = new MenuItem { Header = L.T("🔒 Bezpieczeństwo") }; var gCopy = new MenuItem { Header = L.T("💾 Kopia zapasowa") };
                var gNote = new MenuItem { Header = L.T("🔔 Powiadomienia i pliki") };
                void Add(string key, string label) { var it = new MenuItem { Header = label, IsCheckable = true, IsChecked = _komTheme == key }; gLook.Items.Add(it); it.Click += (a, b) => { _komTheme = key; KomSave(); ApplyTheme(); FillList(); FillChat(); }; }
                Add("auto", L.T("🖥 Automatyczny (jak Velivo)"));
                foreach (var p in KomPalettes) Add(p.Key, L.En ? p.En : p.Pl);
                var net = new MenuItem { Header = L.T("🌍 Rozmowy poza domem…") }; net.Click += (a, b) => KomRelayDialog(w);
                var ai = new MenuItem { Header = L.T("🖼 Zdjęcia i pliki od kontaktów od razu w rozmowie (bez pytania)"), IsCheckable = true, IsChecked = _komAutoImages };
                ai.Click += (a, b) => { _komAutoImages = ai.IsChecked; KomSave(); };
                gNote.Items.Add(ai);
                var nn = new MenuItem { Header = L.T("📶 Powiadamiaj o urządzeniach Velivo w sieci spoza kontaktów"), IsCheckable = true, IsChecked = _komNotifyNew };
                nn.Click += (a, b) => { _komNotifyNew = nn.IsChecked; KomSave(); };
                gNote.Items.Add(nn);
                var snd = new MenuItem { Header = L.T("🔔 Dźwięk wiadomości…") };
                snd.Click += (a, b) =>
                {   // wybór z dźwięków Windows (albo dowolny .wav); Anuluj - dźwięk domyślny
                    var dlg = new Microsoft.Win32.OpenFileDialog { Title = L.T("🔔 Dźwięk wiadomości (Anuluj – dźwięk domyślny)"), Filter = L.T("Dźwięki") + " (*.wav)|*.wav",
                        InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media") };
                    _komSound = dlg.ShowDialog(w) == true ? dlg.FileName : ""; KomSave(); KomPlaySound();
                };
                gNote.Items.Insert(0, snd);
                var ph = new MenuItem { Header = L.T("👤 Moje zdjęcie profilowe…") };
                ph.Click += (a, b) =>
                {
                    var dlg = new Microsoft.Win32.OpenFileDialog { Title = L.T("👤 Moje zdjęcie profilowe…"), Filter = L.T("Zdjęcia") + "|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp" };
                    if (dlg.ShowDialog(w) != true) return;
                    try { KomSetPhoto(KomPhotoJpg(dlg.FileName)); KomInfo("🖼 " + L.T("Zdjęcie wysłane do kontaktów")); } catch (Exception) { KomInfo(L.T("To nie jest zdjęcie")); }
                };
                var phDel = new MenuItem { Header = L.T("🗑 Usuń moje zdjęcie") }; phDel.Click += (a, b) => KomSetPhoto(null);
                var lk = new MenuItem { Header = L.T("🔒 Blokada komunikatora (Windows Hello)"), IsCheckable = true, IsChecked = _komLock };
                lk.Click += (a, b) => { _komLock = lk.IsChecked; KomSave(); };
                var hk = new MenuItem { Header = L.T("🔑 Klucz sprzętowy (YubiKey)…") + (KomHwKeys().Count > 0 ? "  ✓ " + KomHwKeys().Count : ""), ToolTip = L.T("Komunikator otwierany dotknięciem klucza USB/NFC; zapas: drugi klucz albo Windows Hello") };
                hk.Click += (a, b) => KomHwKeySetup(w);
                gProf.Items.Add(ph); gProf.Items.Add(phDel); gSafe.Items.Add(lk); gSafe.Items.Add(hk);
                var bk = new MenuItem { Header = L.T("💾 Kopia zapasowa (folder, Dysk Google, NUCBOX)…") };
                bk.Click += async (a, b) =>
                {
                    var pw = KomPassword(w, true); if (pw == null) return;
                    var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "Velivo-kopia-" + DateTime.Now.ToString("yyyy-MM-dd") + ".velivo", Filter = L.T("Kopia Velivo") + " (*.velivo)|*.velivo" };
                    if (dlg.ShowDialog(w) != true) return;
                    // 🔑 opcjonalnie: kopię otworzy też klucz sprzętowy (główny i zapasowy); hasło zostaje zapasem
                    var hs = RandomNumberGenerator.GetBytes(32); var keys = new List<(BankKey, byte[])>(); var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                    while (MessageBox.Show(w, keys.Count == 0 ? L.T("Kopię otworzysz hasłem albo kluczem sprzętowym (YubiKey, Titan). Dodać klucz? Dotkniesz go 2 razy.") : L.T("Dodano kluczy: ") + keys.Count + L.T(". Dodać zapasowy klucz?"), "🔑 " + L.T("Klucz sprzętowy do kopii"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                        try
                        {
                            var ex0 = keys.Select(x => Convert.FromBase64String(x.Item1.Id)).ToList();
                            var k = await Task.Run(() => WebAuthn.Register(hwnd, BankRpId, ex0));
                            var r = await Task.Run(() => WebAuthn.Verify(hwnd, BankRpId, new List<BankKey> { k }, hs));
                            if (r.Key == null || r.Hmac == null) { KomInfo("🔑 " + L.T("Ten klucz nie umie szyfrować (hmac-secret)")); continue; }
                            keys.Add((k, r.Hmac));
                        }
                        catch (Exception ex) { KomInfo("🔑 " + ex.Message); }
                    try { File.WriteAllBytes(dlg.FileName, KomBackup(pw, hs, keys)); KomInfo("💾 " + L.T("Kopia zapisana") + (keys.Count > 0 ? " 🔑×" + keys.Count : "")); } catch (Exception ex) { KomInfo(L.T("Nie udało się zapisać kopii") + ": " + ex.Message); }
                };
                var rs = new MenuItem { Header = L.T("📥 Przywróć z kopii (nowy komputer)…") };
                rs.Click += async (a, b) =>
                {
                    if (MessageBox.Show(w, L.T("Kopia zastąpi kontakty i rozmowy na tym komputerze (dostanie identyfikator z kopii – kontakty dalej Cię znają). Dalej?"), "📥", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    var dlg = new Microsoft.Win32.OpenFileDialog { Filter = L.T("Kopia Velivo") + " (*.velivo)|*.velivo|" + L.T("Wszystkie pliki") + "|*.*" };
                    if (dlg.ShowDialog(w) != true) return;
                    var data = File.ReadAllBytes(dlg.FileName);
                    if (KomBackupKeys(data) is { } ks && MessageBox.Show(w, L.T("Ta kopia otwiera się kluczem sprzętowym albo hasłem.\n\nTak – klucz\nNie – hasło"), "📥", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        WebAuthn.VerifyResult r = null; var hw = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                        try { r = await Task.Run(() => WebAuthn.Verify(hw, BankRpId, ks.Keys, ks.Salt)); } catch (Exception ex) { KomInfo("🔑 " + ex.Message); }
                        if (r?.Key != null && r.Hmac != null) { KomInfo(KomRestore(data, null, (r.Key, r.Hmac)) ? "📥 " + L.T("Przywrócono kontakty i rozmowy") : L.T("Klucz nie pasuje do tej kopii – użyj innego klucza albo hasła")); FillList(); FillChat(); return; }
                        KomInfo(L.T("Klucz nie pasuje do tej kopii – użyj innego klucza albo hasła"));
                    }
                    var pw = KomPassword(w, false); if (pw == null) return;
                    KomInfo(KomRestore(data, pw) ? "📥 " + L.T("Przywrócono kontakty i rozmowy") : L.T("Złe hasło albo to nie jest kopia Velivo z Windows"));
                    FillList(); FillChat();
                };
                gCopy.Items.Add(bk); gCopy.Items.Add(rs);
                foreach (var g in new[] { gLook, gProf, gSafe, gCopy, gNote, net }) cm.Items.Add(g);
                cm.IsOpen = true;
            }));
            leftButtons.Children.Add(Btn(L.T("➕ Dodaj"), L.T("Dodaj kontakt: w tej sieci albo przez internet (zaproszenie)"), (s, e) =>
            {
                var cm = new ContextMenu { PlacementTarget = (UIElement)s, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
                var lan = new MenuItem { Header = L.T("📶 W tej sieci (z kodem)") }; lan.Click += async (a, b) => await KomAddDialog(w);
                var inv = new MenuItem { Header = L.T("✉ Zaproś przez internet…") }; inv.Click += (a, b) => KomInviteDialog(w);
                var join = new MenuItem { Header = L.T("🔑 Mam zaproszenie…") }; join.Click += (a, b) => KomJoinDialog(w);
                var grp = new MenuItem { Header = L.T("👥 Nowa grupa…") }; grp.Click += (a, b) => KomGroupDialog(w);
                var intro = new MenuItem { Header = L.T("👤 Połącz moich znajomych ze sobą…") }; intro.Click += (a, b) => KomIntroduceDialog(w);
                var addr = new MenuItem { Header = L.T("🔗 Mój adres Velivo (dla każdego)…") }; addr.Click += (a, b) => KomMyAddressDialog(w);
                var have = new MenuItem { Header = L.T("🔗 Mam czyjś adres Velivo…") };
                have.Click += (a, b) =>
                {
                    string clip = ""; try { if (Clipboard.ContainsText() && Clipboard.GetText().Contains("VELIVO-KONTAKT:")) clip = Clipboard.GetText().Trim(); } catch (Exception) { }   // skopiowany adres - już wpisany
                    var link = KomAsk(w, "🔗 " + L.T("Wklej adres Velivo"), clip); if (string.IsNullOrWhiteSpace(link)) return;
                    KomInfo(KomRequestByLink(link) ? "👤 " + L.T("Prośba wysłana – kontakt pojawi się, gdy ta osoba ją przyjmie") : L.T("To nie jest adres Velivo"));
                };
                cm.Items.Add(addr); cm.Items.Add(have); cm.Items.Add(intro); cm.Items.Add(grp); cm.Items.Add(lan); cm.Items.Add(inv); cm.Items.Add(join);
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
            presence.Tick += (s, e) => { Ping(); if (Shown()) { FillList(); FillChat(); } else stale = true; };
            presence.Start();
            Ping();
            _ = Task.Delay(2500).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() => { if (_komWindow == w) { FillList(); FillChat(); } })));
            w.SourceInitialized += (s, e) => { SetWindowAppId(w, "Velivo.Komunikator"); ApplyTheme(); };
            // ✕ - do zasobnika (ikona komunikatora), komunikator dalej odbiera; zamyka sie naprawde tylko z Velivo
            w.Closing += (s, e) => { if (_komExit) return; e.Cancel = true; w.Hide(); KomEnsureTray(); };
            w.Closed += (s, e) => { presence.Stop(); _komWindow = null; _komRefresh = null; _komSelect = null; _komStatus = null; _komUnlocked = false; };
            w.IsVisibleChanged += (s, e) => { if (!w.IsVisible) { _komUnlocked = false; KomSetDraft(_komOpenId, input.Text); KomSave(); } };   // ✏ szkic zapisany   // 🔒 schowane do zasobnika - przy powrocie znów Windows Hello
            _komWindow = w;
            KomTrayUpdate();   // 🔢 licznik na pasku zadań od razu
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
        // 📲 nowa wersja aplikacji na telefon kontaktu: wybór APK (zapamiętany folder, np. build\android), wysyłka jak plik; telefon pokazuje „stuknij, aby zainstalować”
        async Task KomSendUpdate(Window owner, KomContact c)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "📲 " + c.Name, Filter = "APK (*.apk)|*.apk", InitialDirectory = (string)_komOpt["apk-dir"] ?? "" };
            if (dlg.ShowDialog(owner) != true) return;
            _komOpt["apk-dir"] = Path.GetDirectoryName(dlg.FileName); KomSave();
            IPAddress addr = null;
            if (!KomOnline(c.Id) || !IPAddress.TryParse(c.Addr ?? "", out addr) || IPAddress.IsLoopback(addr)) addr = (await KomFind()).FirstOrDefault(x => x.Id == c.Id).Addr;
            await KomSendFilesTo(owner, c.Name, addr ?? IPAddress.Loopback, c, new[] { dlg.FileName });
        }

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

        // 🖼 zdjęcie kontaktu albo kółko z pierwszą literą w stałym kolorze (jak w WhatsApp / Signal); zielona obwódka - w sieci
        static readonly Color[] KomAvatarColors = { Color.FromRgb(0x7C, 0x3A, 0xED), Color.FromRgb(0x25, 0x63, 0xEB), Color.FromRgb(0x05, 0x96, 0x69), Color.FromRgb(0xD9, 0x77, 0x06), Color.FromRgb(0xDB, 0x27, 0x77), Color.FromRgb(0x08, 0x91, 0xB2), Color.FromRgb(0xDC, 0x26, 0x26), Color.FromRgb(0x65, 0xA3, 0x0D) };
        Border KomAvatarEl(KomContact c, bool on, double size, KomPal pal)
        {
            var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), BorderThickness = new Thickness(2), BorderBrush = on ? new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)) : Brushes.Transparent };
            if (KomPhoto(c.Id) is string ph && KomAvatar(ph) is ImageSource av) b.Background = new ImageBrush(av) { Stretch = Stretch.UniformToFill };
            else
            {
                b.Background = new SolidColorBrush(KomAvatarColors[(int)((uint)c.Id.GetHashCode() % KomAvatarColors.Length)]);
                b.Child = new TextBlock { Text = c.IsGroup ? "👥" : (c.Name.Trim().Length > 0 ? c.Name.Trim().Substring(0, 1).ToUpper() : "?"), Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = size * 0.42, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            }
            return b;
        }
        // 🎤 głosówka w komunikatorze (ponownie - stop), ⏩ 1× / 1,5× / 2×; długość w pasku stanu
        MediaPlayer _komVoice; string _komVoicePath;
        double KomVoiceSpeed => (double?)_komOpt["glos-x"] ?? 1.0;
        double KomNextSpeed() => KomVoiceSpeed == 1.0 ? 1.5 : KomVoiceSpeed == 1.5 ? 2.0 : 1.0;
        static string KomSpeedText(double x) => x == 1.5 ? L.T("1,5×") : (int)x + "×";
        void KomPlayVoice(string path)
        {
            var was = _komVoicePath; try { _komVoice?.Stop(); _komVoice?.Close(); } catch (Exception) { }
            _komVoice = null; _komVoicePath = null;
            if (was == path) return;
            var p = new MediaPlayer();
            p.MediaOpened += (s, e) => KomInfo("▶ " + L.T("Wiadomość głosowa") + (p.NaturalDuration.HasTimeSpan ? " " + p.NaturalDuration.TimeSpan.ToString(@"m\:ss") : "") + " · " + KomSpeedText(KomVoiceSpeed) + " – " + L.T("kliknij, by zatrzymać"));
            p.MediaEnded += (s, e) => { p.Close(); if (_komVoice == p) { _komVoice = null; _komVoicePath = null; } };
            p.MediaFailed += (s, e) => { if (_komVoice == p) { _komVoice = null; _komVoicePath = null; } try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception) { } };
            p.Open(new Uri(path)); p.SpeedRatio = KomVoiceSpeed; p.Play();
            _komVoice = p; _komVoicePath = path;
        }
        // 📍 położenie z Windows (lokalizacja musi być włączona w ustawieniach prywatności)
        static async Task<string> KomLocation()
        {
            try
            {
                if (await Windows.Devices.Geolocation.Geolocator.RequestAccessAsync() != Windows.Devices.Geolocation.GeolocationAccessStatus.Allowed) return null;
                var p = await new Windows.Devices.Geolocation.Geolocator { DesiredAccuracy = Windows.Devices.Geolocation.PositionAccuracy.High }.GetGeopositionAsync(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(20));
                var c = p.Coordinate.Point.Position;
                return string.Format(CultureInfo.InvariantCulture, "{0:F6},{1:F6}", c.Latitude, c.Longitude);
            }
            catch (Exception) { return null; }
        }
        // 🔑 hasło kopii (min. 6 znaków)
        // nowa kopia: hasło dwa razy (literówka = kopia nie do odczytania) i 👁 podgląd
        string KomPassword(Window owner, bool create)
        {
            var box = new PasswordBox { Margin = new Thickness(10, 10, 10, 0), MinWidth = 300 }; var box2 = new PasswordBox { Margin = new Thickness(10, 6, 10, 0), MinWidth = 300 };
            var plain = new TextBox { Margin = box.Margin, MinWidth = 300, Visibility = Visibility.Collapsed }; var plain2 = new TextBox { Margin = box2.Margin, MinWidth = 300, Visibility = Visibility.Collapsed };
            var eye = new CheckBox { Content = "👁 " + L.T("Pokaż hasło"), Margin = new Thickness(10, 8, 10, 0) };
            eye.Click += (s, e) =>
            {
                bool on = eye.IsChecked == true;
                if (on) { plain.Text = box.Password; plain2.Text = box2.Password; } else { box.Password = plain.Text; box2.Password = plain2.Text; }
                box.Visibility = on ? Visibility.Collapsed : Visibility.Visible; plain.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (create) { box2.Visibility = box.Visibility; plain2.Visibility = plain.Visibility; }
            };
            if (!create) box2.Visibility = Visibility.Collapsed;
            var info = new TextBlock { Text = create ? L.T("Zapamiętaj je – bez hasła kopii nie da się przywrócić (min. 6 znaków).") : L.T("Hasło kopii"), Margin = new Thickness(10, 10, 10, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
            var rep = new TextBlock { Text = L.T("Powtórz hasło"), Margin = new Thickness(10, 8, 10, 0), Visibility = create ? Visibility.Visible : Visibility.Collapsed };
            var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Right };
            var d = new Window { Title = "🔑 " + L.T("Hasło kopii"), SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new StackPanel { Children = { info, box, plain, rep, box2, plain2, eye, ok } } };
            KomStyle(d); ok.Click += (s, e) => d.DialogResult = true; box.Focus();
            if (d.ShowDialog() != true) return null;
            bool shown = eye.IsChecked == true; var p = shown ? plain.Text : box.Password; var p2 = shown ? plain2.Text : box2.Password;
            if (p.Length < 6) { KomInfo(L.T("Za krótkie hasło")); return null; }
            if (create && p != p2) { KomInfo(L.T("Hasła się różnią – wpisz jeszcze raz")); return null; }
            return p;
        }
        // 🔗 mój adres: link do skopiowania (WhatsApp, SMS, e-mail); „Zmień” - stary link przestaje działać
        void KomMyAddressDialog(Window owner)
        {
            var link = KomMyAddress(false);
            if (link == null) { KomInfo(L.T("Potrzebny pośrednik (np. NUCBOX) w kontaktach albo ten komputer jako pośrednik (🎨 → 🌍)")); return; }
            var box = new TextBox { Text = link, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(10) };
            var info = new TextBlock { Text = L.T("Daj go każdemu, kto ma Velivo – wyśle Ci prośbę, a Ty zdecydujesz: Dodaj / Odrzuć. Dostanie tylko komunikator (bez Twojej sieci domowej)."), TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(10, 10, 10, 0) };
            var copy = new Button { Content = "📋 " + L.T("Kopiuj"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(10, 0, 6, 10) };
            var change = new Button { Content = "🔄 " + L.T("Zmień"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 10, 10), ToolTip = L.T("Nowy link – stary przestaje działać") };
            var d = new Window { Title = "🔗 " + L.T("Mój adres Velivo"), SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel { Children = { info, box, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { copy, change } } } } };
            copy.Click += (s, e) => { try { Clipboard.SetText(box.Text); KomInfo("📋 " + L.T("Skopiowano")); } catch (Exception) { } };
            change.Click += (s, e) => box.Text = KomMyAddress(true) ?? box.Text;
            KomStyle(d); d.ShowDialog();
        }

        // 🔢 czerwone kółko z liczbą nieprzeczytanych (nakładka na ikonę paska zadań, jak w WhatsApp Desktop)
        static ImageSource KomBadge(int n)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)), new Pen(Brushes.White, 1.5), new Point(16, 16), 15, 15);
                var ft = new FormattedText(n > 99 ? "99+" : n.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), n > 9 ? 13 : 18, Brushes.White, 1.0);
                dc.DrawText(ft, new Point(16 - ft.Width / 2, 16 - ft.Height / 2));
            }
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32); bmp.Render(dv); bmp.Freeze();
            return bmp;
        }
        // pełny ekran (film, gra), prezentacja albo cisza Windows - bez wyskakującego okienka i dźwięku (licznik zostaje)
        [System.Runtime.InteropServices.DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
        static bool KomQuietWindows() { try { return SHQueryUserNotificationState(out var st) == 0 && st is 2 or 3 or 4 or 6; } catch (Exception) { return false; } }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, int extra);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();

        // 🎞 GIF z przeglądarki („Kopiuj obraz”, przeciągnięcie): schowek ma tylko jedną klatkę, ale też adres oryginału (HTML) - bierzemy oryginał
        static string KomGifSource(IDataObject d)
        {
            try
            {
                if (d == null) return null;
                if (d.GetDataPresent("GIF") && d.GetData("GIF") is MemoryStream ms) { var p = KomGifPath(); File.WriteAllBytes(p, ms.ToArray()); return p; }
                var html = d.GetDataPresent(DataFormats.Html) ? d.GetData(DataFormats.Html) as string : null;
                var m = html == null ? null : System.Text.RegularExpressions.Regex.Match(html, "<img[^>]+src=[\"']([^\"']+)[\"']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var url = m != null && m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : d.GetDataPresent("UniformResourceLocatorW") ? (d.GetData("UniformResourceLocatorW") as MemoryStream) is MemoryStream us ? Encoding.Unicode.GetString(us.ToArray()).TrimEnd('\0') : null : null;
                if (url == null) return null;
                if (url.StartsWith("data:image/gif;base64,", StringComparison.OrdinalIgnoreCase)) { var p = KomGifPath(); File.WriteAllBytes(p, Convert.FromBase64String(url.Substring(22))); return p; }
                return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : null;
            }
            catch (Exception) { return null; }
        }
        static string KomGifPath() { Directory.CreateDirectory(KomMediaDir); return Path.Combine(KomMediaDir, "Obraz-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".gif"); }
        // plik lokalny - gotowy; adres - pobranie (do 25 MB, 15 s); tylko prawdziwy GIF (inaczej null - zostaje zwykły obraz)
        static async Task<string> KomGifFetch(string src)
        {
            if (File.Exists(src)) return src;
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 25 * 1024 * 1024 };
                var b = await http.GetByteArrayAsync(src);
                if (b.Length < 6 || b[0] != 'G' || b[1] != 'I' || b[2] != 'F') return null;
                var p = KomGifPath(); File.WriteAllBytes(p, b); return p;
            }
            catch (Exception) { return null; }
        }
        // 🎞 ruchomy GIF (WPF Image pokazuje tylko pierwszą klatkę) - odtwarzany w kółko, bez dźwięku
        static MediaElement KomGifView(string path, double max)
        {
            var v = new MediaElement { Source = new Uri(path), LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, IsMuted = true, Stretch = Stretch.Uniform, MaxWidth = max, MaxHeight = max, Cursor = Cursors.Hand, Margin = new Thickness(0, 2, 0, 4), ToolTip = L.T("Otwórz zdjęcie") };
            v.Loaded += (s, e) => v.Play();
            v.MediaEnded += (s, e) => { v.Position = TimeSpan.FromMilliseconds(1); v.Play(); };
            return v;
        }
        static bool KomIsGif(string p) => Path.GetExtension(p ?? "").Equals(".gif", StringComparison.OrdinalIgnoreCase);
        static bool KomIsVideo(string p) => new[] { ".mp4", ".webm", ".mkv", ".mov", ".3gp", ".avi", ".wmv", ".m4v" }.Contains(Path.GetExtension(p ?? "").ToLowerInvariant());
        // 🖼 podgląd zdjęć i filmów z rozmowy w Velivo: ← → po kolei, spacja - pauza, Enter - program Windows, Esc - zamknij
        void KomViewer(Window owner, KomContact c, string path)
        {
            var items = KomChat(c.Id).Select(m => m.Path).Where(p => !string.IsNullOrEmpty(p) && File.Exists(p) && KomIsImage(p)).Distinct().ToList();   // zdjęcia i GIF-y; filmy - odtwarzacz Velivo
            if (items.Count == 0) return;
            int i = Math.Max(0, items.IndexOf(path));
            var img = new Image { Stretch = Stretch.Uniform };
            var vid = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform };
            var hint = new TextBlock { Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(10), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center };
            var grid = new Grid { Background = Brushes.Black, Children = { img, vid, hint } };
            var d = new Window { Uid = "own-theme", Owner = owner, Width = 1000, Height = 700, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = grid, Background = Brushes.Black, Icon = KomWindowIcon() };
            void Show()
            {
                var p = items[i]; d.Title = "🖼 " + Path.GetFileName(p) + "  (" + (i + 1) + "/" + items.Count + ")";
                vid.Stop(); vid.Source = null;
                if (KomIsGif(p))
                {
                    img.Source = null; img.Visibility = Visibility.Collapsed; vid.Visibility = Visibility.Visible; vid.Source = new Uri(p); vid.Play();
                }
                else
                {
                    vid.Visibility = Visibility.Collapsed; img.Visibility = Visibility.Visible;
                    try { var b = new System.Windows.Media.Imaging.BitmapImage(); b.BeginInit(); b.UriSource = new Uri(p); b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; b.EndInit(); img.Source = KomUpright(b, p); } catch (Exception) { img.Source = null; }
                }
                hint.Text = "← →  " + L.T("poprzednie / następne") + "   ·   Enter – " + L.T("otwórz w programie Windows") + "   ·   Esc – " + L.T("zamknij");
            }
            vid.MediaEnded += (s, e) => { if (KomIsGif(items[i])) { vid.Position = TimeSpan.FromMilliseconds(1); vid.Play(); } };   // 🎞 GIF w kółko
            d.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Right && i < items.Count - 1) { i++; Show(); }
                else if (e.Key == Key.Left && i > 0) { i--; Show(); }
                else if (e.Key == Key.Escape) d.Close();
                else if (e.Key == Key.Enter) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(items[i]) { UseShellExecute = true }); } catch (Exception) { } }
                else return;
                e.Handled = true;
            };
            grid.MouseLeftButtonUp += (s, e) => { var x = e.GetPosition(grid).X; if (x < grid.ActualWidth / 3 && i > 0) { i--; Show(); } else if (x > grid.ActualWidth * 2 / 3 && i < items.Count - 1) { i++; Show(); } };   // klik z lewej / prawej strony
            d.Closed += (s, e) => { try { vid.Stop(); vid.Close(); } catch (Exception) { } };
            Show(); d.Show();
        }
        // 🖱 plik z rozmowy przeciągnięty myszką na pulpit / do folderu (kopia)
        bool _komDragOut;
        void KomDragOut(FrameworkElement el, string path)
        {
            Point? start = null;
            el.PreviewMouseLeftButtonDown += (s, e) => start = e.GetPosition(el);
            el.PreviewMouseMove += (s, e) =>
            {
                if (start == null || e.LeftButton != MouseButtonState.Pressed) { start = null; return; }
                var p = e.GetPosition(el);
                if (Math.Abs(p.X - start.Value.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - start.Value.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                start = null; if (!File.Exists(path)) return;
                _komDragOut = true;
                try { DragDrop.DoDragDrop(el, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy); } catch (Exception) { } finally { _komDragOut = false; }
            };
        }
        // 📸 zrzut fragmentu ekranu (Wycinanie Windows) - gdy obraz trafi do schowka, idzie do rozmowy
        void KomSnip(Action<string> send)
        {
            var seq = GetClipboardSequenceNumber();
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-screenclip:") { UseShellExecute = true }); }
            catch (Exception) { KomInfo(L.T("Brak narzędzia Wycinanie w Windows – użyj Win+Shift+S i Ctrl+V")); return; }
            KomInfo("📸 " + L.T("Zaznacz fragment ekranu – trafi do rozmowy"));
            var until = DateTime.UtcNow.AddSeconds(90);
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            t.Tick += (s, e) =>
            {
                if (DateTime.UtcNow > until) { t.Stop(); return; }
                var now = GetClipboardSequenceNumber(); if (now == seq) return; seq = now;
                try
                {
                    if (!Clipboard.ContainsImage() || Clipboard.GetImage() is not System.Windows.Media.Imaging.BitmapSource img) return;
                    t.Stop();
                    Directory.CreateDirectory(KomMediaDir); var p = Path.Combine(KomMediaDir, "Zrzut-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".png");
                    using (var fs = File.Create(p)) { var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img)); enc.Save(fs); }
                    send(p);
                }
                catch (Exception) { }
            };
            t.Start();
        }
        // 🔝 rozmowa w małym okienku na wierzchu (np. z mamą, gdy pracujesz w innym programie)
        readonly Dictionary<string, Window> _komMinis = new Dictionary<string, Window>();
        void KomMini(KomContact c)
        {
            if (_komMinis.TryGetValue(c.Id, out var old)) { old.Activate(); return; }
            var pal = KomCurrentPal();
            var msgs = new StackPanel { Margin = new Thickness(6) }; var sv = new ScrollViewer { Content = msgs, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var box = new TextBox { MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(6), ToolTip = L.T("Wiadomość (Enter - wyślij)") };
            var dp = new DockPanel(); DockPanel.SetDock(box, Dock.Bottom); dp.Children.Add(box); dp.Children.Add(sv);
            var d = new Window { Title = "💬 " + c.Name, Width = 340, Height = 460, Topmost = true, Content = dp, Icon = KomWindowIcon() };
            string sig = null;
            void Fill()
            {
                var l = KomChat(c.Id); var last = l.LastOrDefault();
                var ns = l.Count + ":" + last?.Mid + last?.Read + last?.Ok + last?.Text?.Length; if (ns == sig) return; sig = ns;
                msgs.Children.Clear();
                foreach (var m in l.Skip(Math.Max(0, l.Count - 60)))
                    msgs.Children.Add(new Border { Background = new SolidColorBrush(m.Mine ? pal.Mine : pal.Theirs), CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(m.Mine ? 40 : 0, 2, m.Mine ? 0 : 40, 2), HorizontalAlignment = m.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                        Child = new TextBlock { Text = m.Text + "   " + DateTimeOffset.FromUnixTimeMilliseconds(m.Ts).LocalDateTime.ToString("HH:mm") + (m.Mine ? (m.Read || (m.Ok && !m.Text.Contains("📬")) ? " ✓✓" : " ✓") : ""), TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(m.Mine ? pal.MineFg : pal.Fg) } });
                sv.ScrollToEnd();
                if (d.IsActive) { if (_komUnread.Remove(c.Id)) { KomSave(); KomTrayUpdate(); _komRefresh?.Invoke(); } KomSendRead(c); }
            }
            box.PreviewKeyDown += (s, e) => { if (e.Key != Key.Enter) return; e.Handled = true; var t = box.Text.Trim(); if (t.Length == 0) return; box.Clear(); KomSend(c, t); Fill(); _komRefresh?.Invoke(); };
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; timer.Tick += (s, e) => Fill(); timer.Start();
            d.Activated += (s, e) => { sig = null; Fill(); };
            d.Closed += (s, e) => { timer.Stop(); _komMinis.Remove(c.Id); };
            _komMinis[c.Id] = d;
            KomStyle(d); d.Show(); Fill(); box.Focus();
        }
        // 💾 eksport rozmowy: HTML (ze zdjęciami i plikami w folderze obok) albo tekst
        void KomExport(Window owner, KomContact c)
        {
            var safe = string.Concat(c.Name.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)));
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "Rozmowa-" + safe + "-" + DateTime.Now.ToString("yyyy-MM-dd") + ".html", Filter = "HTML (*.html)|*.html|" + L.T("Tekst") + " (*.txt)|*.txt" };
            if (dlg.ShowDialog(owner) != true) return;
            try
            {
                var l = KomChat(c.Id).ToList();
                string Who(KomMsg m) => m.Mine ? L.T("Ty") : c.Name;
                string When(KomMsg m) => DateTimeOffset.FromUnixTimeMilliseconds(m.Ts).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
                static string H(string x) => System.Net.WebUtility.HtmlEncode(x ?? "");
                if (dlg.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) File.WriteAllLines(dlg.FileName, l.Select(m => When(m) + "  " + Who(m) + ": " + m.Text));
                else
                {
                    var dir = Path.Combine(Path.GetDirectoryName(dlg.FileName), Path.GetFileNameWithoutExtension(dlg.FileName) + "_pliki");
                    var sb = new StringBuilder("<!doctype html><meta charset=\"utf-8\"><title>" + H(c.Name) + "</title><style>body{font-family:Segoe UI,sans-serif;background:#f4f2fb;max-width:760px;margin:auto;padding:16px}.m{margin:6px 0;padding:8px 12px;border-radius:12px;max-width:75%;background:#fff}.me{background:#ddd2ff;margin-left:auto}.t{font-size:11px;color:#666}img{max-width:100%;border-radius:8px}</style><h2>💬 " + H(c.Name) + "</h2>");
                    foreach (var m in l)
                    {
                        string media = "";
                        if (!string.IsNullOrEmpty(m.Path) && File.Exists(m.Path))
                        {
                            Directory.CreateDirectory(dir); var dest = Path.Combine(dir, Path.GetFileName(m.Path)); if (!File.Exists(dest)) File.Copy(m.Path, dest);
                            var rel = Uri.EscapeDataString(Path.GetFileName(dir)) + "/" + Uri.EscapeDataString(Path.GetFileName(m.Path));
                            media = KomIsImage(m.Path) ? "<br><img src=\"" + rel + "\">" : "<br><a href=\"" + rel + "\">" + H(Path.GetFileName(m.Path)) + "</a>";
                        }
                        sb.Append("<div class=\"m" + (m.Mine ? " me" : "") + "\"><div class=\"t\">" + H(Who(m)) + " · " + When(m) + "</div>" + H(m.Text).Replace("\n", "<br>") + media + "</div>");
                    }
                    File.WriteAllText(dlg.FileName, sb.ToString());
                }
                KomInfo("💾 " + L.T("Rozmowa zapisana"));
            }
            catch (Exception ex) { KomInfo(L.T("Nie udało się zapisać") + ": " + ex.Message); }
        }
        // ⌨ Ctrl+K: przejście do rozmowy po nazwie (wpisz kilka liter, Enter)
        void KomQuickSwitch(Window owner, Action<string> open)
        {
            var box = new TextBox { Margin = new Thickness(10, 10, 10, 6), MinWidth = 300 };
            var lb = new ListBox { Margin = new Thickness(10, 0, 10, 10), Height = 220 };
            var d = new Window { Title = "⌨ " + L.T("Przejdź do rozmowy"), SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new StackPanel { Children = { box, lb } } };
            void Fill() { lb.Items.Clear(); foreach (var c in _komContacts.Where(x => x.Name.Contains(box.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).OrderByDescending(x => KomChat(x.Id).LastOrDefault()?.Ts ?? 0)) lb.Items.Add(new ListBoxItem { Content = (c.IsGroup ? "👥 " : "💬 ") + c.Name, Tag = c.Id }); if (lb.Items.Count > 0) lb.SelectedIndex = 0; }
            void Go() { if (lb.SelectedItem is ListBoxItem it && it.Tag is string id) { d.Close(); open(id); } }
            box.TextChanged += (s, e) => Fill();
            box.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Down && lb.SelectedIndex < lb.Items.Count - 1) lb.SelectedIndex++;
                else if (e.Key == Key.Up && lb.SelectedIndex > 0) lb.SelectedIndex--;
                else if (e.Key == Key.Enter) Go();
                else if (e.Key == Key.Escape) d.Close();
                else return;
                e.Handled = true;
            };
            lb.MouseDoubleClick += (s, e) => Go();
            Fill(); KomStyle(d); box.Focus(); d.ShowDialog();
        }

        // ✏ edycja wiadomości (Enter - zapisz)
        string KomAsk(Window owner, string title, string text)
        {
            var box = new TextBox { Text = text, Margin = new Thickness(10), MinWidth = 340, MaxWidth = 520, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false };
            var ok = new Button { Content = L.T("Zapisz"), IsDefault = true, Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(10, 0, 10, 10), HorizontalAlignment = HorizontalAlignment.Right };
            var d = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new StackPanel { Children = { box, ok } } };
            KomStyle(d); ok.Click += (s, e) => d.DialogResult = true; box.SelectAll(); box.Focus();
            return d.ShowDialog() == true ? box.Text : null;
        }
        // ↪ wybór osób / grup (przekazanie)
        List<KomContact> KomPickContacts(Window owner, string title, string skip)
        {
            var list = new StackPanel();
            foreach (var c in _komContacts.Where(x => x.Id != skip)) list.Children.Add(new CheckBox { Content = (c.IsGroup ? "👥 " : "") + c.Name, Tag = c, Margin = new Thickness(0, 3, 0, 3) });
            var ok = new Button { Content = L.T("Wyślij"), IsDefault = true, Padding = new Thickness(16, 4, 16, 4), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var d = new Window { Title = title, Owner = owner, Width = 320, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
                Content = new StackPanel { Margin = new Thickness(14), Children = { new ScrollViewer { Content = list, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, ok } } };
            ok.Click += (a, b) => d.DialogResult = true;
            KomStyle(d);
            return d.ShowDialog() == true ? list.Children.OfType<CheckBox>().Where(x => x.IsChecked == true).Select(x => (KomContact)x.Tag).ToList() : new List<KomContact>();
        }
        // 🖼 zdjęcia, filmy, głosówki i pliki z rozmowy w jednym miejscu (najnowsze na początku); kliknięcie otwiera
        void KomGallery(Window owner, KomContact c)
        {
            var ms = KomChat(c.Id).Where(m => !string.IsNullOrEmpty(m.Path) && File.Exists(m.Path)).Reverse().ToList();
            if (ms.Count == 0) { KomInfo(L.T("Brak plików w tej rozmowie")); return; }
            var wrap = new WrapPanel { Margin = new Thickness(8) };
            foreach (var m in ms)
            {
                var p = m.Path;
                FrameworkElement el = KomIsImage(p) && KomThumb(p) is ImageSource th ? new Image { Source = th, Width = 120, Height = 120, Stretch = Stretch.UniformToFill }
                    : new TextBlock { Text = m.Text, Width = 120, Height = 120, TextWrapping = TextWrapping.Wrap, FontSize = 12, Padding = new Thickness(6) };
                var b = new Border { Child = el, Margin = new Thickness(4), CornerRadius = new CornerRadius(8), ClipToBounds = true, Cursor = Cursors.Hand, ToolTip = m.Text, BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray };
                b.MouseLeftButtonUp += (s, e) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true }); } catch (Exception) { } };
                wrap.Children.Add(b);
            }
            var d = new Window { Title = "🖼 " + c.Name, Owner = owner, Width = 600, Height = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
            KomStyle(d); d.Show();
        }
        // 🔒 blokada: przy otwarciu komunikatora Windows Hello (odcisk palca, twarz albo PIN Windows)
        bool _komUnlocked;
        // 🔑 klucze sprzętowe komunikatora (WebAuthn, jak w trybie bankowym): tylko klucz publiczny w ustawieniach, podpis sprawdzany przy otwarciu
        List<BankKey> KomHwKeys() => (_komOpt["klucze"] as JsonArray)?.Select(x => new BankKey { Id = (string)x["id"], X = (string)x["x"], Y = (string)x["y"], Name = (string)x["n"] }).Where(k => k.Id != null).ToList() ?? new List<BankKey>();
        // 🔑 menu kluczy: dodaj (pierwszy / zapasowy), usuń jeden (wymiana zepsutego albo zgubionego) - zawsze po Windows Hello / PIN-ie
        void KomHwKeySetup(Window owner)
        {
            var keys = KomHwKeys();
            var cm = new ContextMenu { PlacementTarget = owner, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            var add = new MenuItem { Header = keys.Count == 0 ? L.T("➕ Dodaj klucz") : L.T("➕ Dodaj zapasowy klucz"), ToolTip = L.T("Gdy jeden klucz się zepsuje albo zginie, drugi działa") };
            add.Click += (a, b) => KomHwKeyAdd(owner);
            cm.Items.Add(add);
            if (keys.Count == 1) cm.Items.Add(new MenuItem { Header = "⚠ " + L.T("Dodaj zapasowy klucz"), IsEnabled = false });
            if (keys.Count > 0) { var tst = new MenuItem { Header = L.T("🧪 Sprawdź klucze"), ToolTip = L.T("Dotknij po kolei każdego klucza – Velivo pokaże, który działa") }; tst.Click += (a, b) => KomHwKeyTest(owner); cm.Items.Add(tst); }
            if (keys.Count > 0) cm.Items.Add(new Separator());
            foreach (var k in keys)
            {
                var rm = new MenuItem { Header = L.T("🗑 Usuń: ") + (k.Name ?? "?"), ToolTip = L.T("Wymiana zepsutego albo zgubionego klucza – pozostałe działają dalej") };
                rm.Click += async (a, b) =>
                {   // po dotknięciu innego klucza albo Windows Hello - obcy przy komputerze nie usunie Twojego klucza
                    if (!await KomUnlock()) return;
                    if (_komOpt["klucze"] is JsonArray l) foreach (var x in l.ToList()) if ((string)x["id"] == k.Id) l.Remove(x);
                    KomSave(); KomInfo("🔑 " + L.T("Klucz usunięty: ") + k.Name);
                };
                cm.Items.Add(rm);
            }
            cm.IsOpen = true;
        }
        // 🧪 test każdego klucza osobno; przypomnienie co 3 miesiące po otwarciu kluczem
        async void KomHwKeyTest(Window owner)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(owner).Handle; var res = new List<string>();
            foreach (var k in KomHwKeys())
            {
                KomInfo("🔑 " + L.T("Dotknij teraz: ") + k.Name);
                bool good; try { good = (await Task.Run(() => WebAuthn.Verify(hwnd, BankRpId, new List<BankKey> { k }, null)))?.Key != null; } catch (Exception) { good = false; }
                res.Add((good ? "✓ " : "✗ ") + k.Name);
            }
            _komOpt["klucze-test"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); KomSave();
            MessageBox.Show(owner, string.Join("\n", res) + (res.Any(x => x.StartsWith("✗")) ? "\n\n" + L.T("Nie działający klucz usuń (🗑) i dodaj nowy zapasowy.") : ""), "🧪 " + L.T("Sprawdź klucze"));
        }
        void KomHwKeyDue()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), last = (long?)_komOpt["klucze-test"] ?? 0;
            if (last == 0) { _komOpt["klucze-test"] = now; KomSave(); return; }
            if (now - last < 90L * 86400000) return;
            _komOpt["klucze-test"] = now - 83L * 86400000;   // następne przypomnienie za tydzień, jeśli nie sprawdzisz
            KomSave(); KomInfo(L.T("🧪 Minęły 3 miesiące – sprawdź klucze sprzętowe (także zapasowy): 🎨 → 🔒 Bezpieczeństwo → 🔑 → 🧪 Sprawdź klucze"));
        }
        async void KomHwKeyAdd(Window owner)
        {
            var keys = KomHwKeys(); var hwnd = new System.Windows.Interop.WindowInteropHelper(owner).Handle;
            try
            {   // najpierw PIN / Windows Hello tego komputera - nikt przy odblokowanym komputerze nie doda swojego klucza; bez Windows Hello klucza nie dodajemy
                // (zgubiony klucz bez zapasu zamknąłby komunikator na zawsze)
                if (await Windows.Security.Credentials.UI.UserConsentVerifier.CheckAvailabilityAsync() != Windows.Security.Credentials.UI.UserConsentVerifierAvailability.Available)
                { MessageBox.Show(owner, L.T("Najpierw ustaw PIN Windows (Ustawienia → Konta → Opcje logowania). To zapas, gdy klucz się zepsuje albo zginie."), "🔑", MessageBoxButton.OK, MessageBoxImage.Information); return; }
                if (await Windows.Security.Credentials.UI.UserConsentVerifier.RequestVerificationAsync(L.T("Najpierw PIN tego komputera (Windows Hello) – potem dotkniesz klucza")) != Windows.Security.Credentials.UI.UserConsentVerificationResult.Verified) return;
                KomInfo("🔑 " + L.T("Teraz dotknij klucza. Jeśli Windows zapyta o PIN klucza – to PIN Twojego YubiKey / Titana, nie komputera."));
                var k = await Task.Run(() => WebAuthn.Register(hwnd, BankRpId, keys.Select(x => Convert.FromBase64String(x.Id)).ToList()));
                // nazwa (np. YubiKey, Titan) - przy wymianie widać, który usunąć; zamknięcie okna zapisuje
                var box = new TextBox { Text = L.T("Klucz ") + (keys.Count + 1), Margin = new Thickness(10, 0, 10, 10), MinWidth = 260 };
                var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(10, 0, 10, 10), HorizontalAlignment = HorizontalAlignment.Right };
                var d = new Window { Title = "🔑 " + L.T("Nazwa klucza (np. YubiKey, Titan)"), SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new StackPanel { Children = { new TextBlock { Text = L.T("Nazwa klucza (np. YubiKey, Titan)"), Margin = new Thickness(10, 10, 10, 6) }, box, ok } } };
                KomStyle(d); ok.Click += (s, e) => d.Close(); box.SelectAll(); box.Focus(); d.ShowDialog();
                var nm = box.Text.Trim(); if (nm.Length == 0) nm = L.T("Klucz"); if (nm.Length > 30) nm = nm.Substring(0, 30);
                if (_komOpt["klucze"] is not JsonArray l) _komOpt["klucze"] = l = new JsonArray();
                l.Add(new JsonObject { ["id"] = k.Id, ["x"] = k.X, ["y"] = k.Y, ["n"] = nm + " · " + DateTime.Now.ToString("yyyy-MM-dd") });
                _komLock = true; KomSave();
                KomInfo("🔑 " + (keys.Count == 0 ? L.T("Klucz dodany – komunikator otwierasz dotknięciem klucza. Dodaj też zapasowy klucz.") : L.T("Zapasowy klucz dodany")));
            }
            catch (Exception ex) { KomInfo("🔑 " + ex.Message); }
        }

        async Task<bool> KomUnlock()
        {
            var keys = KomHwKeys();
            if (keys.Count > 0)
            {   // najpierw klucz; anulowanie / brak klucza - zapas: Windows Hello (PIN tego komputera), żeby zgubiony klucz nie zamknął rozmów na zawsze
                var hwnd = new System.Windows.Interop.WindowInteropHelper((Window)_komWindow ?? this).Handle;
                try { if ((await Task.Run(() => WebAuthn.Verify(hwnd, BankRpId, keys, null)))?.Key != null) { KomHwKeyDue(); return true; } } catch (Exception) { }
                try { return await Windows.Security.Credentials.UI.UserConsentVerifier.RequestVerificationAsync(L.T("Komunikator Velivo")) == Windows.Security.Credentials.UI.UserConsentVerificationResult.Verified; }
                catch (Exception) { return false; }
            }
            try
            {
                if (await Windows.Security.Credentials.UI.UserConsentVerifier.CheckAvailabilityAsync() != Windows.Security.Credentials.UI.UserConsentVerifierAvailability.Available) return true;   // bez Windows Hello - bez blokady
                return await Windows.Security.Credentials.UI.UserConsentVerifier.RequestVerificationAsync(L.T("Komunikator Velivo")) == Windows.Security.Credentials.UI.UserConsentVerificationResult.Verified;
            }
            catch (Exception) { return true; }
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

        [System.Runtime.InteropServices.DllImport("winmm.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "mciSendStringW")] static extern int KomMciSend(string cmd, System.Text.StringBuilder ret, int len, IntPtr cb);
        // 🎤 wiadomość głosowa (plik „Glosowa-…”) - opis w rozmowie zamiast nazwy pliku (jak w telefonie)
        static string KomFileLabel(string name, long size) => (name.StartsWith("Glosowa-", StringComparison.Ordinal) ? "🎤 " + L.T("Wiadomość głosowa") : (new[] { ".mp4", ".webm", ".mkv", ".mov", ".3gp" }.Contains(System.IO.Path.GetExtension(name).ToLowerInvariant()) ? "🎬 " + L.T("Wiadomość wideo") : (KomIsImage(name) ? "🖼 " : "📎 ") + name)) + " · " + KomSize(size);
        static int KomMci(string cmd) { try { return KomMciSend(cmd, null, 0, IntPtr.Zero); } catch (Exception) { return -1; } }

        // files - gotowe pliki (np. 🎤 nagranie); bez - okno wyboru
        // 👁 przed wysłaniem: zdjęcie, ▶ odsłuchaj / obejrzyj; Wyślij albo Anuluj (nagranie, zrzut z Velivo - usuwane)
        bool KomPreview(Window owner, KomContact c, string[] files)
        {
            var pal = KomCurrentPal(); bool send = false;
            var panel = new StackPanel { Margin = new Thickness(16) };
            if (files.Length == 1 && KomIsImage(files[0]))
                try
                {
                    var bi = new System.Windows.Media.Imaging.BitmapImage(); bi.BeginInit(); bi.UriSource = new Uri(files[0]); bi.DecodePixelWidth = 1040; bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bi.EndInit();   // OnLoad - plik nie zostaje zablokowany
                    panel.Children.Add(new Image { Source = KomUpright(bi, files[0]), MaxWidth = 520, MaxHeight = 380, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 10) });
                }
                catch (Exception) { }
            foreach (var f in files) panel.Children.Add(new TextBlock { Text = (KomIsImage(f) ? "🖼 " : Path.GetFileName(f).StartsWith("Glosowa-") ? "🎤 " : KomIsVideo(f) ? "🎬 " : "📎 ") + Path.GetFileName(f), Foreground = new SolidColorBrush(pal.Fg), TextWrapping = TextWrapping.Wrap });
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var d = new Window { Title = "📤 " + c.Name, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, MinWidth = 340, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
            Button B(string t, string tip, Action a) { var b = new Button { Content = t, ToolTip = tip, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(6, 0, 0, 0) }; b.Click += (s, e) => a(); row.Children.Add(b); return b; }
            if (files.Length == 1)
            {
                var f0 = files[0]; bool voice = Path.GetFileName(f0).StartsWith("Glosowa-");
                B(voice ? "▶ " + L.T("Odsłuchaj") : "👁 " + L.T("Podgląd"), L.T("Sprawdź przed wysłaniem"), () =>
                {
                    if (voice) KomPlayVoice(f0);
                    else try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(f0) { UseShellExecute = true }); } catch (Exception) { }
                });
            }
            B(L.T("Anuluj"), L.T("Nie wysyłaj"), () => d.Close());
            var ok = B("➤ " + L.T("Wyślij"), L.T("Wyślij do ") + c.Name, () => { send = true; d.Close(); }); ok.IsDefault = true;
            panel.Children.Add(row);
            KomStyle(d); d.ShowDialog();
            if (!send)   // nagrane / wycięte w Velivo - nie zostaje; pliki użytkownika nietknięte
                foreach (var f in files) if (f.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) || (f.StartsWith(KomMediaDir, StringComparison.OrdinalIgnoreCase) && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), "^(Glosowa|Wideo|Zdjecie)-"))) try { File.Delete(f); } catch (Exception) { }
            return send;
        }

        async Task KomSendFilesTo(Window owner, string name, IPAddress addr, KomContact to = null, string[] files = null)
        {
            if (files == null)
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = (L.En ? "Send files to " : "Wyślij pliki do: ") + name, Multiselect = true };
                if (dlg.ShowDialog(owner) != true) return;
                files = dlg.FileNames;
            }
            foreach (var f in files)
            {
                Window codeWin = null;
                var fn = Path.GetFileName(f); var fmid = Guid.NewGuid().ToString("N"); long fsize = 0; try { fsize = new FileInfo(f).Length; } catch (Exception) { }
                KomInfo("📎 " + fn + L.T(" – czekam, aż odbiorca przyjmie…"), 0);
                // wpis w rozmowie od razu („…”): potwierdzenie przeczytania od odbiorcy moze przyjsc, zanim tu skonczymy
                KomMsg pend = null;
                if (to != null) { pend = new KomMsg { Mine = true, Text = KomFileLabel(fn, fsize), Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Ok = false, Mid = fmid, File = true, Path = f }; KomChat(to.Id).Add(pend); KomSave(); _komRefresh?.Invoke(); }
                // w trakcie wysyłania - bez drugiej, równoległej wysyłki z „dosyłania niedoręczonych” (duży plik szedł dwa razy)
                _komInFlight.Add(fmid);
                string r;
                try
                {
                    // ten komputer w domu, odbiorca poza domem (🌍): od razu przez pośrednika (skrzynka oddaje od razu, gdy odbiorca połączony) -
                    // stary adres odbiorcy z sieci domowej już nie działa (wcześniej czekanie na błąd i dopiero wtedy pośrednik)
                    r = to != null && _komUp == null && _komViaNet.ContainsKey(to.Id) && !_komRemote.ContainsKey(to.Id) ? "" : await KomSendFile(addr, f, code => Dispatcher.Invoke(() => codeWin = KomCodeWindow(owner, "📎 " + fn + " → " + name, code)), to,
                        (done, all) => Dispatcher.BeginInvoke(new Action(() => KomInfo("📎 " + fn + " → " + name + "  " + (all > 0 ? (int)(done * 100 / all) : 100) + " %  (" + KomSize(done) + L.T(" z ") + KomSize(all) + ")", all > 0 ? (double)done / all : 1))), fmid);
                }
                finally { _komInFlight.Remove(fmid); }
                codeWin?.Close();
                if (pend != null)
                {
                    if (r == "ok") { pend.Ok = true; pend.Text += L.T(" – odebrano"); }
                    else if (r == L.T("odrzucono")) KomChat(to.Id).Remove(pend);   // odrzucony - bez wpisu
                    // odbiorcy nie ma (wyłączony, poza zasięgiem) - plik czeka („…”) i idzie sam, gdy się pojawi
                    else if (await KomSendViaBox(to, f, fmid) == "ok") { pend.Ok = true; pend.Text += L.T(" – 📬 czeka na serwerze"); KomInfo("📎 " + fn + L.T(" – 📬 czeka na serwerze, dojdzie, gdy odbiorca się pojawi")); KomSave(); _komRefresh?.Invoke(); continue; }
                    else { KomInfo("📎 " + fn + L.T(" – odbiorca niedostępny, wyślę, gdy się pojawi")); KomSave(); _komRefresh?.Invoke(); continue; }
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
            _ = Dispatcher.BeginInvoke(new Action(() => { if (_komWindow?.TaskbarItemInfo is System.Windows.Shell.TaskbarItemInfo ti) { int un = _komUnread.Values.Sum(); ti.Overlay = un > 0 ? KomBadge(un) : null; } }));   // 🔢 czerwona liczba na ikonie paska zadań
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
