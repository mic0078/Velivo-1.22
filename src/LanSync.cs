using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Przegladarka
{
    // Synchronizacja LAN w czasie rzeczywistym dla aktywnego profilu (ustawienia, zakladki, sesja, reguly prywatnosci).
    public partial class MainWindow
    {
        sealed class LanPeerInfo
        {
            public string Id;
            public string Device;
            public string Address;
            public string Profile;
            public string LastType;
            public long LastStamp;
            public DateTime LastSeenUtc;
        }

        sealed class LanStatePacket
        {
            public string t { get; set; }      // hello/state
            public string id { get; set; }     // sender id
            public string device { get; set; } // nazwa komputera
            public string profile { get; set; }
            public long ts { get; set; }
            public string key { get; set; }    // MAC pakietu hello
            public string hash { get; set; }   // HMAC stanu do odrzucania duplikatow
            public string nonce { get; set; }
            public string tag { get; set; }
            public string data { get; set; }
            public string pairId { get; set; }
            public string pub { get; set; }
            public string proof { get; set; }
        }

        sealed class LanSyncPayload
        {
            public string settings { get; set; }
            public string bookmarks { get; set; }
            public string session { get; set; }
            public string sessionActive { get; set; }
            public string privacy { get; set; }
            public string profiles { get; set; }
            public string extensions { get; set; }
            public string passwords { get; set; }
            public long changed { get; set; }
            public string pinned { get; set; }             // karty przypiete (adresy); null = starsza wersja bez tej funkcji
            public string banks { get; set; }              // wszystkie profile bankowe (plik -> zawartosc)
            public string bank { get; set; }               // tryb bankowy: haslo (skrot), klucze, karty zaszyfrowane; null = starsza wersja
            public string bookmarksDeleted { get; set; }   // usuniete zakladki (url<TAB>czas), zeby usuniecie dzialalo na obu   // kiedy dane na nadawcy ostatnio zmienil uzytkownik (ms UTC); nowsze wygrywa
        }

        static readonly HashSet<string> LanSettingsBlockedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "lanSync",
            "audioOut",
            "readerSize",        // rozmiar okna zalezy od ekranu komputera          // glosniki to urzadzenie konkretnego komputera
            "lastDlDir",         // folder (np. dysk Z:) istnieje tylko na tym komputerze
            "lanSyncSilent",
            "lanSyncKey",
            "quickAccessTab",
            "language",          // jezyk interfejsu wybiera kazdy komputer sam
            "videoDir",
            "floatBounds",          // foldery sa rozne na kazdym komputerze
        };

        const int LanPort = 41919;
        string _lanId = Guid.NewGuid().ToString("N");
        readonly string _lanDeviceName = Environment.MachineName;
        CancellationTokenSource _lanCts;
        UdpClient _lanRx;
        UdpClient _lanTx;
        DispatcherTimer _lanTick;
        string _lastLanFingerprint = "";

        // Znacznik "kiedy moje dane ostatnio zmienil uzytkownik" (nie import z sieci) - zapisany na dysku,
        // zeby po ponownym uruchomieniu komputer nie udawal, ze ma najnowsze dane.
        static string LanChangeFile { get { return Path.Combine(DataDir, "lan-zmiana.txt"); } }
        long _lanLocalChanged = -1;
        string _lanLocalChangedFp = "";
        DateTime _lanLastPushBack = DateTime.MinValue;

        void LoadLanChange()
        {
            if (_lanLocalChanged >= 0) return;
            _lanLocalChanged = 0;
            try
            {
                if (!File.Exists(LanChangeFile))
                {
                    // pierwsze uruchomienie z ta funkcja (np. swieza instalacja): obecne dane NIE sa "nowa zmiana" -
                    // znacznik 0, zeby nie nadpisaly danych z drugiego komputera
                    SaveLanChange(0, LanContentFingerprint());
                    return;
                }
                var lines = File.ReadAllLines(LanChangeFile);
                if (lines.Length >= 2) { long.TryParse(lines[0], out _lanLocalChanged); _lanLocalChangedFp = lines[1]; }
            }
            catch (Exception) { }
        }

        // Odcisk TRESCI do znacznika zmian: zakladki, hasla, prywatnosc, profile, dodatki i ustawienia - bez kart
        // (zmieniaja sie ciagle) i bez ustawien samej synchronizacji (wlaczenie sync to nie jest zmiana danych).
        string LanContentFingerprint()
        {
            var settings = string.Join("\n", ReadTextOrEmpty(Path.Combine(DataDir, "ustawienia.txt")).Split('\n')
                .Where(l => { int i = l.IndexOf('='); return i <= 0 || !LanSettingsBlockedKeys.Contains(l.Substring(0, i).Trim()); }));
            return Sha256(settings + "\n--\n" + ReadTextOrEmpty(Path.Combine(DataDir, "zakladki.txt")) + "\n--\n" +
                ReadTextOrEmpty(Path.Combine(DataDir, "prywatnosc.txt")) + "\n--\n" + ReadProfilesRegistry() + "\n--\n" +
                ReadTextOrEmpty(ExtensionsSyncListFile) + "\n--\n" + (_lanLegacyNoKeyMode ? "[]" : ExportPasswordsForSync()) + "\n--\n" + ReadTextOrEmpty(PinnedFile) + BankSyncTerm());
        }

        // "Pusty" komputer: bez zakladek i bez hasel - jego dane nigdy nie nadpisuja pelnych
        static bool LanStateLooksEmpty(string bookmarks, string passwords)
        {
            return string.IsNullOrWhiteSpace(bookmarks) && (string.IsNullOrWhiteSpace(passwords) || passwords.Trim() == "[]");
        }

        void SaveLanChange(long stamp, string fingerprint)
        {
            _lanLocalChanged = stamp; _lanLocalChangedFp = fingerprint ?? "";
            try { Directory.CreateDirectory(DataDir); File.WriteAllLines(LanChangeFile, new[] { stamp.ToString(CultureInfo.InvariantCulture), _lanLocalChangedFp }); } catch (Exception) { }
        }
        byte[] _lanEncryptionKey;
        byte[] _lanAuthenticationKey;
        bool _lanLegacyNoKeyMode;
        bool _lanApplying;
        DateTime _lanStartedUtc = DateTime.MinValue;
        DateTime _lastHello = DateTime.MinValue;
        readonly Dictionary<string, LanPeerInfo> _lanPeers = new Dictionary<string, LanPeerInfo>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, long> _lanPeerStamps = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> _lanDeviceProfile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, DateTime> _lanProfilePrompted = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> _lanLog = new List<string>();
        DateTime _lanLastRxUtc;
        DateTime _lanLastTxUtc;
        int _lanPacketsRx;
        int _lanPacketsTx;
        int _lanErrors;

        Window _lanDiagWindow;
        TextBlock _lanDiagStatus;
        TextBlock _lanDiagCounters;
        ListBox _lanDiagPeers;
        ListBox _lanDiagLog;
        DispatcherTimer _lanDiagTimer;

        static string ReadTextOrEmpty(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch (Exception) { return ""; }
        }

        static string Sha256(string text)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""));
            return Convert.ToHexString(bytes);
        }

        // Tryb bankowy w odcisku tylko, gdy jest ustawiony (starsze wersje bez trybu bankowego licza to samo co dawniej)
        string BankSyncTerm()
        {
            if (_lanLegacyNoKeyMode) return "";
            var main = ReadTextOrEmpty(BankFileFor(""));
            var all = ExportBanksForSync();
            if (all.Length == 0) return "";
            // tylko profil glowny -> ten sam odcisk co wczesniej (zgodnosc ze starsza wersja)
            return BankProfiles().Count == 1 && main.Length > 0 ? "\n--bank--\n" + main : "\n--banks--\n" + all;
        }

        string CurrentLanFingerprint()
        {
            var settings = ReadTextOrEmpty(Path.Combine(DataDir, "ustawienia.txt"));
            var bookmarks = ReadTextOrEmpty(Path.Combine(DataDir, "zakladki.txt"));
            var session = ReadTextOrEmpty(Path.Combine(DataDir, "sesja.txt"));
            var sessionActive = ReadTextOrEmpty(Path.Combine(DataDir, "sesja.txt.aktywna"));
            var privacy = ReadTextOrEmpty(Path.Combine(DataDir, "prywatnosc.txt"));
            var profiles = ReadProfilesRegistry();
            var extensions = ReadTextOrEmpty(ExtensionsSyncListFile);
            var passwords = _lanLegacyNoKeyMode ? "[]" : ExportPasswordsForSync(); // bez sparowania pakiet jest jawny - bez hasel
            // otwarte karty (sesja) nie sa synchronizowane - kazdy komputer ma swoje
            return Sha256(settings + "\n--\n" + bookmarks + "\n--\n" + ReadTextOrEmpty(BookmarkTombstonesFile) + "\n--\n" + privacy + "\n--\n" + profiles + "\n--\n" + extensions + "\n--\n" + passwords + "\n--\n" + ReadTextOrEmpty(PinnedFile) + BankSyncTerm());
        }

        static string FilterLanSettingsForImport(string settingsText)
        {
            if (settingsText == null) return null;
            var lines = settingsText
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line =>
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0) return true;
                    var key = line.Substring(0, separator).Trim();
                    return !LanSettingsBlockedKeys.Contains(key);
                });
            return string.Join(Environment.NewLine, lines);
        }

        static string MergeLanSettingsForImport(string remoteSettings, string localSettingsPath)
        {
            var remote = FilterLanSettingsForImport(remoteSettings);
            var localOnly = ReadTextOrEmpty(localSettingsPath)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line =>
                {
                    int separator = line.IndexOf('=');
                    return separator > 0 && LanSettingsBlockedKeys.Contains(line.Substring(0, separator).Trim());
                });
            return string.Join(Environment.NewLine, new[] { remote }.Concat(localOnly).Where(x => !string.IsNullOrEmpty(x)));
        }

        static byte[] DeriveLanKeyMaterial(string key)
        {
            return Rfc2898DeriveBytes.Pbkdf2(key, Encoding.UTF8.GetBytes("Velivo LAN sync v1"), 250000, HashAlgorithmName.SHA256, 64);
        }

        void InitializeLanKeys()
        {
            var material = DeriveLanKeyMaterial(_settings.LanSyncKey.Trim());
            _lanEncryptionKey = new byte[32];
            _lanAuthenticationKey = new byte[32];
            Buffer.BlockCopy(material, 0, _lanEncryptionKey, 0, 32);
            Buffer.BlockCopy(material, 32, _lanAuthenticationKey, 0, 32);
            CryptographicOperations.ZeroMemory(material);
        }

        static byte[] LanAssociatedData(LanStatePacket pkt)
        {
            var header = string.Join("\n", pkt.t ?? "", pkt.id ?? "", pkt.device ?? "", pkt.profile ?? "",
                pkt.ts.ToString(CultureInfo.InvariantCulture), pkt.hash ?? "");
            return Encoding.UTF8.GetBytes(header);
        }

        string LanHelloMac(LanStatePacket pkt)
        {
            using (var hmac = new HMACSHA256(_lanAuthenticationKey))
                return Convert.ToBase64String(hmac.ComputeHash(LanAssociatedData(pkt)));
        }

        bool VerifyLanHello(LanStatePacket pkt)
        {
            if (_lanAuthenticationKey == null || string.IsNullOrWhiteSpace(pkt.key)) return false;
            try
            {
                var supplied = Convert.FromBase64String(pkt.key);
                using (var hmac = new HMACSHA256(_lanAuthenticationKey))
                {
                    var expected = hmac.ComputeHash(LanAssociatedData(pkt));
                    return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
                }
            }
            catch (FormatException) { return false; }
        }

        string LanFingerprintMac(string fingerprint)
        {
            using (var hmac = new HMACSHA256(_lanAuthenticationKey))
                return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(fingerprint ?? "")));
        }

        void EncryptLanState(LanStatePacket pkt, LanSyncPayload payload)
        {
            // Stan (zakladki, sesja, hasla) szybko przekracza limit pakietu UDP - kompresujemy go przed szyfrowaniem.
            var plain = CompressLanPayload(JsonSerializer.SerializeToUtf8Bytes(payload));
            try
            {
                var nonce = RandomNumberGenerator.GetBytes(12);
                var cipher = new byte[plain.Length];
                var tag = new byte[16];
                using (var gcm = new AesGcm(_lanEncryptionKey, 16))
                    gcm.Encrypt(nonce, plain, cipher, tag, LanAssociatedData(pkt));
                pkt.nonce = Convert.ToBase64String(nonce);
                pkt.tag = Convert.ToBase64String(tag);
                pkt.data = Convert.ToBase64String(cipher);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }

        LanSyncPayload DecryptLanState(LanStatePacket pkt)
        {
            if (_lanEncryptionKey == null || string.IsNullOrWhiteSpace(pkt.nonce) ||
                string.IsNullOrWhiteSpace(pkt.tag) || string.IsNullOrWhiteSpace(pkt.data)) return null;
            var nonce = Convert.FromBase64String(pkt.nonce);
            var tag = Convert.FromBase64String(pkt.tag);
            var cipher = Convert.FromBase64String(pkt.data);
            if (nonce.Length != 12 || tag.Length != 16 || cipher.Length == 0 || cipher.Length > 60000) return null;
            var plain = new byte[cipher.Length];
            try
            {
                using (var gcm = new AesGcm(_lanEncryptionKey, 16))
                    gcm.Decrypt(nonce, cipher, tag, plain, LanAssociatedData(pkt));
                var json = DecompressLanPayload(plain);
                try { return JsonSerializer.Deserialize<LanSyncPayload>(json); }
                finally { if (!ReferenceEquals(json, plain)) CryptographicOperations.ZeroMemory(json); }
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }

        static byte[] CompressLanPayload(byte[] json)
        {
            try
            {
                using (var output = new MemoryStream())
                {
                    using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true)) gzip.Write(json, 0, json.Length);
                    return output.ToArray();
                }
            }
            finally { CryptographicOperations.ZeroMemory(json); }
        }

        // Starsze wersje wysylaja czysty JSON (zaczyna sie od '{'), nowe - GZip (naglowek 1F 8B).
        static byte[] DecompressLanPayload(byte[] data)
        {
            if (data.Length < 2 || data[0] != 0x1f || data[1] != 0x8b) return data;
            using (var input = new MemoryStream(data, false))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[8192];
                int read;
                while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    if (output.Length > 8 * 1024 * 1024) throw new InvalidDataException("Stan LAN po rozpakowaniu jest za duży.");
                }
                return output.ToArray();
            }
        }

        bool ShouldShowLanToast()
        {
            return _settings != null && !_settings.LanSyncSilent;
        }

        void LanLog(string line)
        {
            string msg = DateTime.Now.ToString("HH:mm:ss") + "  " + line;
            _lanLog.Add(msg);
            if (_lanLog.Count > 250) _lanLog.RemoveRange(0, _lanLog.Count - 250);
            RefreshLanDiagnosticsUi();
        }

        void StartLanSync()
        {
            StopLanSync();
            if (_settings == null || !_settings.LanSync) return;
            bool hasKey = AppSettings.IsLanSyncKeyStrong(_settings.LanSyncKey);
            _lanLegacyNoKeyMode = !hasKey;
            _lanCts = new CancellationTokenSource();
            try
            {
                if (hasKey) InitializeLanKeys();
                _lanRx = new UdpClient(AddressFamily.InterNetwork);
                _lanRx.Client.ExclusiveAddressUse = false;
                _lanRx.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _lanRx.Client.Bind(new IPEndPoint(IPAddress.Any, LanPort));
                _lanRx.EnableBroadcast = true;
                _lanTx = new UdpClient(AddressFamily.InterNetwork);
                _lanTx.EnableBroadcast = true;
                StartQuickAccessLanListener(_lanCts.Token);
                _ = Task.Run(() => LanListenLoop(_lanCts.Token));
                _lanStartedUtc = DateTime.UtcNow;

                _lanTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _lanTick.Tick += (s, e) =>
                {
                    if (_lanApplying) return;
                    ExpireLanPairings();
                    if (_lanLegacyNoKeyMode)
                    {
                        // przed sparowaniem: tylko informacja "jestem, mozna mnie sparowac" - zadnych danych uzytkownika
                        if (DateTime.UtcNow - _lastAnnounce > TimeSpan.FromSeconds(10)) LanBroadcastAnnounce();
                        return;
                    }
                    if (_lanAuthenticationKey != null && DateTime.UtcNow - _lastHello > TimeSpan.FromSeconds(12)) LanBroadcastHello();
                    if (_lanEncryptionKey != null)
                    {
                        LanBroadcastState(false);
                        BroadcastQuickAccessLan();
                    }
                };
                _lanTick.Start();
                LanLog(hasKey
                    ? L.T("LAN sync uruchomiona na porcie ") + LanPort + " (profil: " + SelectedProfileName + ")."
                    : L.T("LAN sync czeka na sparowanie – dane nie są wysyłane (profil: ") + SelectedProfileName + ").");
                if (hasKey)
                {
                    LanBroadcastHello();
                    LanBroadcastState(true);
                    BroadcastQuickAccessLan();
                }
                else
                {
                    LanBroadcastAnnounce();
                }
            }
            catch (Exception ex) { _lanErrors++; App.LogError(ex); StopLanSync(); LanLog(L.T("Błąd startu LAN sync: ") + ex.Message); }
        }

        void StopLanSync()
        {
            try { _lanTick?.Stop(); } catch (Exception) { }
            _lanTick = null;
            try { _lanCts?.Cancel(); } catch (Exception) { }
            _lanCts = null;
            StopQuickAccessLanListener();
            try { _lanRx?.Dispose(); } catch (Exception) { }
            try { _lanTx?.Dispose(); } catch (Exception) { }
            _lanRx = null;
            _lanTx = null;
            _lanPeers.Clear();
            _lanPeerStamps.Clear();
            _lanDeviceProfile.Clear();
            _lanProfilePrompted.Clear();
            if (_lanEncryptionKey != null) CryptographicOperations.ZeroMemory(_lanEncryptionKey);
            if (_lanAuthenticationKey != null) CryptographicOperations.ZeroMemory(_lanAuthenticationKey);
            _lanEncryptionKey = null;
            _lanAuthenticationKey = null;
            _lanLegacyNoKeyMode = false;
            ClearLanPairings();
            LanLog("LAN sync zatrzymana.");
        }

        async Task LanListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var res = await _lanRx.ReceiveAsync(ct);
                    if (res.Buffer == null || res.Buffer.Length == 0 || res.Buffer.Length > 60000) continue;
                    var msg = Encoding.UTF8.GetString(res.Buffer);
                    var pkt = JsonSerializer.Deserialize<LanStatePacket>(msg);
                    Guid senderId;
                    if (pkt == null || pkt.id == _lanId || !Guid.TryParseExact(pkt.id, "N", out senderId)) continue;
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (pkt.ts < now - 300000 || pkt.ts > now + 300000)
                    {
                        var skewed = pkt;
                        await Dispatcher.InvokeAsync(() => LogLanClockSkew(skewed.device ?? skewed.id, skewed.ts - now));
                        continue;
                    }

                    if (pkt.t != null && pkt.t.StartsWith("pair-", StringComparison.Ordinal))
                    {
                        await Dispatcher.InvokeAsync(() => HandleLanPairPacket(pkt, res.RemoteEndPoint.Address.ToString()));
                        continue;
                    }

                    if (pkt.t == "hello" || pkt.t == "state" || pkt.t == "state-plain" || pkt.t == "announce")
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            var device = string.IsNullOrWhiteSpace(pkt.device) ? pkt.id : pkt.device.Trim();
                            string previousProfile;
                            bool profileChanged = _lanDeviceProfile.TryGetValue(device, out previousProfile) &&
                                !string.Equals(previousProfile, NormalizeProfileName(pkt.profile ?? "domyslny"), StringComparison.OrdinalIgnoreCase);
                            TryPromptProfileSwitch(pkt, !_lanPeers.ContainsKey(pkt.id), profileChanged);
                            // niesparowany komputer (np. po czystej instalacji) widzi drugi Velivo - proponujemy polaczenie
                            if (_lanLegacyNoKeyMode) OfferLanPairing(pkt);
                        });
                    }

                    LanSyncPayload state = null;
                    LanHistoryPayload hist = null;
                    LanTabPayload sentTab = null;
                    if (pkt.t == "tab")
                    {
                        if (_lanLegacyNoKeyMode) continue;
                        try { sentTab = DecryptLanBlob<LanTabPayload>(pkt); }
                        catch (CryptographicException) { continue; }
                        catch (FormatException) { continue; }
                        catch (JsonException) { continue; }
                        catch (InvalidDataException) { continue; }
                        if (sentTab == null) continue;
                    }
                    else if (pkt.t == "hist")
                    {
                        if (_lanLegacyNoKeyMode) continue;
                        try { hist = DecryptLanHistory(pkt); }
                        catch (CryptographicException) { continue; }
                        catch (FormatException) { continue; }
                        catch (JsonException) { continue; }
                        catch (InvalidDataException) { continue; }
                        if (hist == null) continue;
                    }
                    else if (pkt.t == "hello")
                    {
                        if (_lanLegacyNoKeyMode) continue;
                        if (!VerifyLanHello(pkt)) continue;
                    }
                    else if (pkt.t == "state")
                    {
                        if (_lanLegacyNoKeyMode) continue;
                        try { state = DecryptLanState(pkt); }
                        catch (CryptographicException) { continue; }
                        catch (FormatException) { continue; }
                        catch (JsonException) { continue; }
                        catch (InvalidDataException) { continue; }
                        if (state == null) continue;
                    }
                    else continue;   // announce i jawne state-plain (starsze wersje): tylko propozycja parowania, danych nie przyjmujemy

                    _lanPacketsRx++;
                    _lanLastRxUtc = DateTime.UtcNow;
                    bool isNewPeer = false;
                    bool profileChanged = false;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        isNewPeer = TouchLanPeer(pkt, res.RemoteEndPoint, out profileChanged);
                        if (!string.Equals(pkt.profile ?? "", SelectedProfileName, StringComparison.OrdinalIgnoreCase))
                            TryPromptProfileSwitch(pkt, isNewPeer, profileChanged);
                    });

                    if (!string.Equals(pkt.profile ?? "", SelectedProfileName, StringComparison.OrdinalIgnoreCase)) continue;

                    if (pkt.t == "tab")
                    {
                        var st = sentTab; var sp = pkt;
                        await Dispatcher.InvokeAsync(() => ReceiveLanTab(sp, st));
                        continue;
                    }
                    if (pkt.t == "hist")
                    {
                        var h = hist;
                        await Dispatcher.InvokeAsync(() => ApplyLanHistory(h));
                        continue;
                    }
                    if (pkt.t == "hello")
                    {
                        await Dispatcher.InvokeAsync(() => { LanBroadcastState(isNewPeer); LanBroadcastHistory(isNewPeer); });
                        continue;
                    }
                    if (pkt.t == "state" || pkt.t == "state-plain")
                    {
                        var statePacket = pkt;
                        var statePayload = state;
                        await Dispatcher.InvokeAsync(() => ApplyLanState(statePacket, statePayload));
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _lanErrors++;
                    App.LogError(ex);
                    await Dispatcher.InvokeAsync(() => LanLog(L.T("Błąd odbioru LAN: ") + ex.Message));
                    try { await Task.Delay(500, ct); } catch (Exception) { }
                }
            }
        }

        bool TouchLanPeer(LanStatePacket pkt, IPEndPoint ep, out bool profileChanged)
        {
            profileChanged = false;
            bool isNew = false;
            LanPeerInfo p;
            if (!_lanPeers.TryGetValue(pkt.id, out p))
            {
                p = new LanPeerInfo { Id = pkt.id };
                _lanPeers[pkt.id] = p;
                LanLog(L.T("Wykryto urządzenie: ") + pkt.id.Substring(0, 8));
                isNew = true;
            }
            var normalizedProfile = NormalizeProfileName(pkt.profile ?? "domyslny");
            if (!string.IsNullOrWhiteSpace(p.Profile) && !string.Equals(p.Profile, normalizedProfile, StringComparison.OrdinalIgnoreCase))
                profileChanged = true;

            var device = string.IsNullOrWhiteSpace(pkt.device) ? pkt.id : pkt.device.Trim();
            string oldDeviceProfile;
            if (_lanDeviceProfile.TryGetValue(device, out oldDeviceProfile) && !string.Equals(oldDeviceProfile, normalizedProfile, StringComparison.OrdinalIgnoreCase))
                profileChanged = true;
            _lanDeviceProfile[device] = normalizedProfile;

            p.Device = device;
            p.Address = ep != null ? ep.Address + ":" + ep.Port : "?";
            p.Profile = normalizedProfile;
            p.LastType = pkt.t ?? "?";
            p.LastStamp = pkt.ts;
            p.LastSeenUtc = DateTime.UtcNow;
            RefreshLanDiagnosticsUi();
            return isNew;
        }

        void TryPromptProfileSwitch(LanStatePacket pkt, bool isNewPeer, bool profileChanged)
        {
            var target = NormalizeProfileName(pkt.profile ?? "domyslny");
            if (string.Equals(target, SelectedProfileName, StringComparison.OrdinalIgnoreCase)) return;

            // Podczas dolaczania pytamy tylko dolaczajaca maszyne. Dla stalej pracy pytamy po wykryciu zmiany trybu na peerze.
            bool joiningPrompt = isNewPeer && (DateTime.UtcNow - _lanStartedUtc) < TimeSpan.FromSeconds(45);
            if (!joiningPrompt && !profileChanged) return;

            var device = string.IsNullOrWhiteSpace(pkt.device) ? pkt.id : pkt.device.Trim();
            if (string.Equals(device, _lanDeviceName, StringComparison.OrdinalIgnoreCase)) return;

            var key = device + "|" + target;
            DateTime last;
            if (_lanProfilePrompted.TryGetValue(key, out last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(2)) return;
            _lanProfilePrompted[key] = DateTime.UtcNow;

            string mode = joiningPrompt ? L.T("Dołączyłeś do sieci z innym trybem pracy.") : L.T("Wykryto zmianę trybu pracy na innym urządzeniu.");
            var ans = MessageBox.Show(this,
                mode + L.T("\n\nUrządzenie: ") + device +
                L.T("\nWykryty profil: ") + target +
                L.T("\nAktualny profil: ") + SelectedProfileName +
                L.T("\n\nPrzełączyć się na profil \"") + target + L.T("\", aby zachować synchronizację LAN?"),
                "Synchronizacja LAN", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ans == MessageBoxResult.Yes)
                SwitchProfile(target);
        }

        void LanBroadcastHello()
        {
            if (_lanTx == null || _lanAuthenticationKey == null) return;
            _lastHello = DateTime.UtcNow;
            var pkt = new LanStatePacket
            {
                t = "hello",
                id = _lanId,
                device = _lanDeviceName,
                profile = SelectedProfileName,
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            pkt.key = LanHelloMac(pkt);
            LanSend(pkt);
        }

        DateTime _lastAnnounce;

        // Jedyny pakiet wysylany przed sparowaniem: identyfikator, nazwa komputera i profil - bez ustawien, zakladek, kart i hasel.
        void LanBroadcastAnnounce()
        {
            if (_lanTx == null || !_lanLegacyNoKeyMode) return;
            _lastAnnounce = DateTime.UtcNow;
            LanSend(new LanStatePacket
            {
                t = "announce",
                id = _lanId,
                device = _lanDeviceName,
                profile = SelectedProfileName,
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
        }

        void LanBroadcastState(bool force)
        {
            if (_lanTx == null || _lanApplying) return;
            // bez sparowania nie wysylamy zadnych danych (dawniej jawny pakiet state-plain) - tylko LanBroadcastAnnounce
            if (_lanLegacyNoKeyMode || _lanEncryptionKey == null || _lanAuthenticationKey == null) return;
            var settings = ReadTextOrEmpty(Path.Combine(DataDir, "ustawienia.txt"));
            var bookmarks = ReadTextOrEmpty(Path.Combine(DataDir, "zakladki.txt"));
            var session = ReadTextOrEmpty(Path.Combine(DataDir, "sesja.txt"));
            var sessionActive = ReadTextOrEmpty(Path.Combine(DataDir, "sesja.txt.aktywna"));
            var privacy = ReadTextOrEmpty(Path.Combine(DataDir, "prywatnosc.txt"));
            var profiles = ReadProfilesRegistry();
            var extensions = ReadTextOrEmpty(ExtensionsSyncListFile);
            var passwords = _lanLegacyNoKeyMode ? "[]" : ExportPasswordsForSync(); // bez sparowania pakiet jest jawny - bez hasel

            var fingerprint = Sha256(settings + "\n--\n" + bookmarks + "\n--\n" + ReadTextOrEmpty(BookmarkTombstonesFile) + "\n--\n" + privacy + "\n--\n" + profiles + "\n--\n" + extensions + "\n--\n" + passwords + "\n--\n" + ReadTextOrEmpty(PinnedFile) + BankSyncTerm());
            // dane rozne od ostatnio zapamietanych = zmiana zrobiona tu, przez uzytkownika -> nowy znacznik czasu
            LoadLanChange();
            var contentFp = LanContentFingerprint();
            if (contentFp != _lanLocalChangedFp) SaveLanChange(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), contentFp);
            if (!force && fingerprint == _lastLanFingerprint) return;
            // pulsujemy tylko, gdy wysylamy rzeczywista zmiane (nie przy okresowym przypomnieniu tego samego stanu)
            if (fingerprint != _lastLanFingerprint) TrayPulse(2);
            _lastLanFingerprint = fingerprint;

            var pkt = new LanStatePacket
            {
                t = _lanLegacyNoKeyMode ? "state-plain" : "state",
                id = _lanId,
                device = _lanDeviceName,
                profile = SelectedProfileName,
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                hash = _lanLegacyNoKeyMode ? Sha256(fingerprint) : LanFingerprintMac(fingerprint),
            };
            var payload = new LanSyncPayload
            {
                settings = settings,
                bookmarks = bookmarks,
                session = session,
                sessionActive = sessionActive,
                privacy = privacy,
                profiles = profiles,
                extensions = extensions,
                passwords = passwords,
                changed = _lanLocalChanged,
                bookmarksDeleted = ReadTextOrEmpty(BookmarkTombstonesFile),
                pinned = ReadTextOrEmpty(PinnedFile),
                bank = _lanLegacyNoKeyMode ? null : ReadTextOrEmpty(BankFileFor("")),   // tylko w zaszyfrowanym pakiecie
                banks = _lanLegacyNoKeyMode ? null : ExportBanksForSync(),               // wszystkie profile bankowe
            };
            if (_lanLegacyNoKeyMode)
            {
                pkt.data = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload));
            }
            else
            {
                EncryptLanState(pkt, payload);
            }
            LanSend(pkt);
        }

        void LanSend(LanStatePacket pkt)
        {
            if (_lanTx == null) return;
            try
            {
                var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pkt));
                if (data.Length > 60000)
                {
                    _lanErrors++;
                    LanLog(L.T("Nie wysłano pakietu LAN: przekracza limit UDP (") + data.Length.ToString("N0") + " B).");
                    return;
                }
                foreach (var target in LanBroadcastTargets())
                    _ = _lanTx.SendAsync(data, data.Length, new IPEndPoint(target, LanPort));
                _lanPacketsTx++;
                _lanLastTxUtc = DateTime.UtcNow;
                RefreshLanDiagnosticsUi();
            }
            catch (Exception ex) { _lanErrors++; App.LogError(ex); LanLog(L.T("Błąd nadawania LAN: ") + ex.Message); }
        }

        void ApplyLanState(LanStatePacket pkt, LanSyncPayload state)
        {
            // dane przyjmujemy tylko z zaszyfrowanego i uwierzytelnionego pakietu od sparowanego komputera
            if (pkt == null || pkt.t != "state" || _lanLegacyNoKeyMode || _lanEncryptionKey == null) return;
            long lastStamp;
            if (_lanPeerStamps.TryGetValue(pkt.id, out lastStamp) && pkt.ts <= lastStamp) return;
            var localFingerprint = CurrentLanFingerprint();
            var localHash = pkt.t == "state-plain" ? Sha256(localFingerprint) : LanFingerprintMac(localFingerprint);
            if (string.Equals(pkt.hash, localHash, StringComparison.Ordinal))
            {
                _lanPeerStamps[pkt.id] = pkt.ts;
                return;
            }
            if (state == null || state.settings == null || state.bookmarks == null || state.session == null ||
                state.sessionActive == null || state.privacy == null || state.profiles == null ||
                state.extensions == null || state.passwords == null)
            {
                LanLog(L.T("Odrzucono niekompletny stan synchronizacji."));
                return;
            }
            TrayPulse(3);   // przyszly NOWE dane z drugiego komputera - ikonka w zasobniku lagodnie pulsuje

            // Kto ma nowsze dane? Wczesniej oba komputery przyjmowaly stan od siebie nawzajem w tej samej chwili
            // i dane zamienialy sie miejscami. Teraz przyjmujemy tylko nowsze; przy remisie decyduje identyfikator.
            LoadLanChange();
            var localContent = LanContentFingerprint();
            if (localContent != _lanLocalChangedFp) SaveLanChange(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), localContent);
            bool localEmpty = LanStateLooksEmpty(ReadTextOrEmpty(Path.Combine(DataDir, "zakladki.txt")), _lanLegacyNoKeyMode ? "[]" : ExportPasswordsForSync());
            bool incomingEmpty = LanStateLooksEmpty(state.bookmarks, state.passwords);
            // Ustawienia (oraz prywatnosc, profile, dodatki): wygrywa nowsza zmiana. Pusty komputer zawsze przyjmuje.
            bool incomingNewer =
                (localEmpty && !incomingEmpty) ? true :
                (!localEmpty && incomingEmpty) ? false :
                state.changed > _lanLocalChanged ||
                (state.changed == _lanLocalChanged && string.CompareOrdinal(pkt.id, _lanId) < 0);

            try
            {
                _lanApplying = true;
                Directory.CreateDirectory(DataDir);

                // Zakladki i hasla: LACZONE z obu komputerow (nic nie ginie); usuniecia przenosza sie przez liste usunietych.
                var localBookmarks = ReadTextOrEmpty(Path.Combine(DataDir, "zakladki.txt"));
                var mergedBookmarks = MergeBookmarksText(localBookmarks, state.bookmarks, state.bookmarksDeleted);
                bool bookmarksChanged = mergedBookmarks != localBookmarks;
                if (bookmarksChanged) File.WriteAllText(Path.Combine(DataDir, "zakladki.txt"), mergedBookmarks);
                ImportPasswordsFromSync(state.passwords, true);

                if (incomingNewer)
                {
                    var settingsPath = Path.Combine(DataDir, "ustawienia.txt");
                    File.WriteAllText(settingsPath, MergeLanSettingsForImport(state.settings, settingsPath));
                    File.WriteAllText(Path.Combine(DataDir, "prywatnosc.txt"), state.privacy);
                    WriteProfilesRegistry(state.profiles);
                    WriteIfNotEmpty(ExtensionsSyncListFile, state.extensions);
                    _settings = AppSettings.Load(DataDir);
                    ApplySettingsToAllTabs();
                    UpdateDarkButton();
                    ApplyBrowserTheme();
                    LoadSitePrivacyRules();
                    UpdatePrivacyButton();
                    _ = RebuildBlocker();
                    _ = ApplyExtensionsSyncListAsync();
                    if (state.pinned != null) ApplySyncedPinnedTabs(state.pinned);
                    if (pkt.t != "state-plain")
                    {
                        if (!string.IsNullOrEmpty(state.banks)) ApplySyncedBanks(state.banks);
                        else if (!string.IsNullOrEmpty(state.bank)) ApplySyncedBank(state.bank);
                    }
                    SaveLanChange(state.changed, LanContentFingerprint());   // przyjete dane maja czas nadawcy
                }
                else SaveLanChange(_lanLocalChanged, LanContentFingerprint()); // polaczenie zakladek/hasel to nie zmiana ustawien

                if (bookmarksChanged) LoadBookmarks();
                _lanPeerStamps[pkt.id] = pkt.ts;
                var after = CurrentLanFingerprint();
                var sentHash = pkt.t == "state-plain" ? Sha256(after) : LanFingerprintMac(after);
                if (!string.Equals(sentHash, pkt.hash, StringComparison.Ordinal) && DateTime.UtcNow - _lanLastPushBack > TimeSpan.FromSeconds(3))
                {
                    // mamy cos, czego drugi komputer nie ma (nowsze ustawienia albo dodatkowe zakladki) - odsylamy
                    _lanLastPushBack = DateTime.UtcNow;
                    _lastLanFingerprint = "";
                }
                else _lastLanFingerprint = after;
                if (incomingNewer || bookmarksChanged)
                {
                    if (ShouldShowLanToast()) ShowToast(L.T("🌐 Zsynchronizowano z Velivo w sieci lokalnej."), null);
                    TrayPulse(3);
                    LanLog(L.T("Zsynchronizowano z ") + pkt.id.Substring(0, 8) + (incomingNewer ? L.T(" (ustawienia przyjęte)") : " (ustawienia tu nowsze)") + (bookmarksChanged ? L.T(", zakładki połączone.") : "."));
                }
            }
            catch (Exception ex) { _lanErrors++; App.LogError(ex); LanLog(L.T("Błąd importu LAN: ") + ex.Message); }
            finally { _lanApplying = false; }
        }

        // ---------- laczenie zakladek ----------
        static string BookmarkTombstonesFile { get { return Path.Combine(DataDir, "zakladki-usuniete.txt"); } }

        static Dictionary<string, long> ParseTombstones(string text)
        {
            var d = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var line in (text ?? "").Split('\n'))
            {
                var p = line.TrimEnd('\r').Split('\t');
                long t;
                if (p.Length >= 2 && p[0].Length > 0 && long.TryParse(p[1], out t) && (!d.ContainsKey(p[0]) || d[p[0]] < t)) d[p[0]] = t;
            }
            return d;
        }

        // Suma zakladek z obu komputerow (kolejnosc lokalna, nowe na koncu) minus usuniete na ktorymkolwiek.
        string MergeBookmarksText(string local, string incoming, string incomingDeleted)
        {
            var tomb = ParseTombstones(ReadTextOrEmpty(BookmarkTombstonesFile));
            foreach (var kv in ParseTombstones(incomingDeleted))
                if (!tomb.ContainsKey(kv.Key) || tomb[kv.Key] < kv.Value) tomb[kv.Key] = kv.Value;
            // usuniecie przechowujemy 90 dni
            long cutoff = DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeMilliseconds();
            var keep = tomb.Where(kv => kv.Value > cutoff).ToList();
            try { File.WriteAllLines(BookmarkTombstonesFile, keep.Select(kv => kv.Key + "\t" + kv.Value)); } catch (Exception) { }
            var dead = new HashSet<string>(keep.Select(kv => kv.Key), StringComparer.Ordinal);

            var lines = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in (local ?? "").Replace("\r", "").Split('\n').Concat((incoming ?? "").Replace("\r", "").Split('\n')))
            {
                var url = line.Split('\t')[0];
                if (url.Length == 0 || dead.Contains(url) || !seen.Add(url)) continue;
                lines.Add(line);
            }
            return lines.Count == 0 ? "" : string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }

        readonly HashSet<string> _lanPairOffered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Drugi komputer z tym samym profilem, ale bez sparowania: nic sie nie synchronizuje (tylko pakiet announce)
        // Proponujemy pelne polaczenie - szyfrowane, z haslami i Szybkim Dostepem.
        // Pyta tylko jeden z dwoch komputerow (mniejszy identyfikator), zeby nie wyslaly dwoch ofert naraz;
        // drugi dostaje zwykle okno potwierdzenia parowania z kodem.
        void OfferLanPairing(LanStatePacket pkt)
        {
            if (!_lanLegacyNoKeyMode || pkt == null || string.IsNullOrEmpty(pkt.id)) return;
            if (!string.Equals(NormalizeProfileName(pkt.profile ?? "domyslny"), SelectedProfileName, StringComparison.OrdinalIgnoreCase)) return;
            // oba niesparowane: pyta tylko jeden (mniejszy identyfikator); drugi sparowany (hello/state) - pytamy zawsze my
            if ((pkt.t == "state-plain" || pkt.t == "announce") && string.CompareOrdinal(_lanId, pkt.id) > 0) return;
            if (!_lanPairOffered.Add(pkt.id)) return;
            var device = string.IsNullOrWhiteSpace(pkt.device) ? pkt.id.Substring(0, 8) : pkt.device.Trim();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var ans = MessageBox.Show(this,
                    L.T("W sieci jest drugi Velivo:\n\nUrządzenie: ") + device + L.T("\nProfil: ") + SelectedProfileName +
                    L.T("\n\nPołączyć oba komputery i synchronizować wszystko – ustawienia, zakładki, karty, hasła i Szybki Dostęp?") +
                    L.T("\n\nNa obu ekranach pojawi się ten sam krótki kod do porównania. Dane będą szyfrowane."),
                    "Synchronizacja LAN", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (ans == MessageBoxResult.Yes) BeginLanPairing();
            }));
        }

        readonly Dictionary<string, DateTime> _lanClockSkewLogged = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // Pakiety z komputera z zegarem przesunietym o ponad 5 minut sa odrzucane (ochrona przed powtorkami).
        // Wczesniej dzialo sie to po cichu i synchronizacja "po prostu nie dzialala".
        void LogLanClockSkew(string device, long skewMs)
        {
            DateTime last;
            if (_lanClockSkewLogged.TryGetValue(device ?? "", out last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(5)) return;
            _lanClockSkewLogged[device ?? ""] = DateTime.UtcNow;
            LanLog(L.T("Odrzucono pakiety z ") + device + L.T(": zegar różni się o ") + Math.Round(Math.Abs(skewMs) / 60000.0) +
                L.T(" min. Ustaw automatyczny czas w Windows na obu komputerach."));
        }

        // 255.255.255.255 Windows wysyla tylko jedna karta sieciowa (czesto VPN / Hyper-V / VirtualBox),
        // wiec inne komputery w LAN nie dostawaly pakietow. Wysylamy tez na adres rozgloszeniowy kazdej aktywnej karty IPv4.
        static List<IPAddress> LanBroadcastTargets()
        {
            var list = new List<IPAddress> { IPAddress.Broadcast };
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                        nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        var ip = ua.Address.GetAddressBytes();
                        var mask = ua.IPv4Mask.GetAddressBytes();
                        if (mask.Length != 4 || (mask[0] == 255 && mask[1] == 255 && mask[2] == 255 && mask[3] == 255)) continue;
                        var bcast = new byte[4];
                        for (int i = 0; i < 4; i++) bcast[i] = (byte)(ip[i] | ~mask[i]);
                        var addr = new IPAddress(bcast);
                        if (!list.Contains(addr)) list.Add(addr);
                    }
                }
            }
            catch (Exception) { }
            return list;
        }

        void NotifyLanStateChanged()
        {
            if (_lanApplying) return;
            LanBroadcastState(false);
        }

        static string ReadProfilesRegistry()
        {
            try
            {
                var list = GetKnownProfiles();
                return string.Join("\n", list);
            }
            catch (Exception) { return "domyslny"; }
        }

        static void WriteProfilesRegistry(string data)
        {
            try
            {
                var lines = (data ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(NormalizeProfileName)
                    .Where(x => x.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (!lines.Contains("domyslny")) lines.Insert(0, "domyslny");
                SaveKnownProfiles(lines);
            }
            catch (Exception) { }
        }

        void OpenLanDiagnosticsPanel()
        {
            if (_lanDiagWindow != null) { _lanDiagWindow.Activate(); return; }

            _lanDiagStatus = new TextBlock { Margin = new Thickness(8, 8, 8, 2), FontWeight = FontWeights.SemiBold };
            _lanDiagCounters = new TextBlock { Margin = new Thickness(8, 0, 8, 8) };
            _lanDiagPeers = new ListBox { Margin = new Thickness(8), Height = 180 };
            _lanDiagLog = new ListBox { Margin = new Thickness(8) };

            var ping = SmallButton(L.T("Odśwież teraz"), () => { LanBroadcastHello(); LanBroadcastState(true); });
            var clear = SmallButton(L.T("Wyczyść log"), () => { _lanLog.Clear(); RefreshLanDiagnosticsUi(); });
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0, 8, 8) };
            bar.Children.Add(ping); bar.Children.Add(clear);

            var root = new DockPanel();
            DockPanel.SetDock(_lanDiagStatus, Dock.Top);
            DockPanel.SetDock(_lanDiagCounters, Dock.Top);
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(_lanDiagStatus);
            root.Children.Add(_lanDiagCounters);
            root.Children.Add(bar);

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var tPeers = new TextBlock { Text = L.T("Wykryte urządzenia"), Margin = new Thickness(8, 0, 8, 2), FontWeight = FontWeights.SemiBold };
            var tLog = new TextBlock { Text = L.T("Log synchronizacji"), Margin = new Thickness(8, 6, 8, 2), FontWeight = FontWeights.SemiBold };
            Grid.SetRow(tPeers, 0); Grid.SetRow(_lanDiagPeers, 1); Grid.SetRow(tLog, 2); Grid.SetRow(_lanDiagLog, 3);
            grid.Children.Add(tPeers); grid.Children.Add(_lanDiagPeers); grid.Children.Add(tLog); grid.Children.Add(_lanDiagLog);
            root.Children.Add(grid);

            _lanDiagWindow = new Window
            {
                Title = L.T("Diagnostyka synchronizacji LAN"),
                Width = 760,
                Height = 560,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = root
            };
            _lanDiagWindow.Closed += (s, e) =>
            {
                _lanDiagTimer?.Stop();
                _lanDiagTimer = null;
                _lanDiagWindow = null;
            };

            _lanDiagTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _lanDiagTimer.Tick += (s, e) => RefreshLanDiagnosticsUi();
            _lanDiagTimer.Start();
            RefreshLanDiagnosticsUi();
            _lanDiagWindow.Show();
        }

        void RefreshLanDiagnosticsUi()
        {
            if (_lanDiagWindow == null || _lanDiagStatus == null) return;

            var now = DateTime.UtcNow;
            var active = _lanPeers.Values.Where(p => now - p.LastSeenUtc < TimeSpan.FromSeconds(20)).OrderByDescending(p => p.LastSeenUtc).ToList();

            if (_settings == null || !_settings.LanSync)
                _lanDiagStatus.Text = L.T("Status: synchronizacja LAN wyłączona");
            else if (_lanRx == null)
                _lanDiagStatus.Text = L.T("Status: synchronizacja LAN nieaktywna");
            else if (active.Count == 0)
                _lanDiagStatus.Text = L.T("Status: aktywna, ale brak połączonych urządzeń");
            else
                _lanDiagStatus.Text = L.T("Status: połączono z ") + active.Count + L.T(" urządzeniem/urządzeniami");

            _lanDiagCounters.Text = L.T("Profil: ") + SelectedProfileName +
                                    "   |   RX: " + _lanPacketsRx +
                                    "   TX: " + _lanPacketsTx +
                                    L.T("   Błędy: ") + _lanErrors +
                                    "   |   Ostatni RX: " + (_lanLastRxUtc == default ? "-" : _lanLastRxUtc.ToLocalTime().ToString("HH:mm:ss")) +
                                    L.T("   Ostatni TX: ") + (_lanLastTxUtc == default ? "-" : _lanLastTxUtc.ToLocalTime().ToString("HH:mm:ss"));

            _lanDiagPeers.Items.Clear();
            foreach (var p in active)
            {
                int ago = Math.Max(0, (int)(now - p.LastSeenUtc).TotalSeconds);
                var device = string.IsNullOrWhiteSpace(p.Device) ? "?" : p.Device;
                _lanDiagPeers.Items.Add(p.Id.Substring(0, Math.Min(8, p.Id.Length)) + "   " + device + "   " + p.Address + "   profil=" + p.Profile + "   typ=" + p.LastType + "   " + ago + " s temu");
            }
            if (active.Count == 0) _lanDiagPeers.Items.Add(L.T("Brak aktywnych peerów."));

            _lanDiagLog.Items.Clear();
            foreach (var l in _lanLog.TakeLast(120).Reverse()) _lanDiagLog.Items.Add(l);
        }
    }
}
