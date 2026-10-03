using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Przegladarka
{
    public partial class MainWindow
    {
        sealed class QuickAccessLanHeader
        {
            public string Type { get; set; }
            public string Id { get; set; }
            public string Device { get; set; }
            public string Profile { get; set; }
            public long Timestamp { get; set; }
            public string Hash { get; set; }
            public string Mac { get; set; }
            public string Nonce { get; set; }
            public string Tag { get; set; }
            public int Length { get; set; }
        }

        const int QuickAccessLanPort = LanPort;
        TcpListener _quickAccessLanListener;
        readonly Dictionary<string, string> _quickAccessLanSent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, DateTime> _quickAccessLanLastFailure = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, long> _quickAccessLanPeerStamps = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _quickAccessLanPending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        byte[] _quickAccessLanArchive;
        string _quickAccessLanHash;
        string _quickAccessLanStamp;
        bool _quickAccessLanPreparing;

        void StartQuickAccessLanListener(CancellationToken token)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, QuickAccessLanPort);
                listener.Start();
                _quickAccessLanListener = listener;
                _ = Task.Run(() => QuickAccessLanListenLoop(listener, token));
            }
            catch (Exception ex)
            {
                _quickAccessLanListener = null;
                App.LogError(ex);
                LanLog(L.T("Transfer grafik Szybkiego Dostępu LAN niedostępny: ") + ex.Message);
            }
        }

        void StopQuickAccessLanListener()
        {
            try { _quickAccessLanListener?.Stop(); } catch (Exception) { }
            _quickAccessLanListener = null;
            _quickAccessLanSent.Clear();
            _quickAccessLanLastFailure.Clear();
            _quickAccessLanPeerStamps.Clear();
            _quickAccessLanPending.Clear();
            _quickAccessLanArchive = null;
            _quickAccessLanHash = null;
            _quickAccessLanStamp = null;
            _quickAccessLanPreparing = false;
        }

        async Task QuickAccessLanListenLoop(TcpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(token);
                    _ = Task.Run(() => ReceiveQuickAccessLanArchiveAsync(client, token), token);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException)
                {
                    if (token.IsCancellationRequested) break;
                    await Task.Delay(250, token);
                }
                catch (Exception ex)
                {
                    App.LogError(ex);
                    if (!token.IsCancellationRequested) await Task.Delay(500, token);
                }
            }
        }

        static byte[] QuickAccessLanAssociatedData(QuickAccessLanHeader header)
        {
            return Encoding.UTF8.GetBytes(string.Join("\n", header.Type ?? "", header.Id ?? "", header.Device ?? "", header.Profile ?? "",
                header.Timestamp.ToString(CultureInfo.InvariantCulture), header.Hash ?? "", header.Length.ToString(CultureInfo.InvariantCulture)));
        }

        static byte[] QuickAccessLanMac(byte[] key, string text)
        {
            using (var hmac = new HMACSHA256(key)) return hmac.ComputeHash(Encoding.UTF8.GetBytes(text));
        }

        static byte[] QuickAccessLanMac(byte[] key, byte[] data)
        {
            using (var hmac = new HMACSHA256(key)) return hmac.ComputeHash(data);
        }

        static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken token)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer, offset, count - offset, token);
                if (read <= 0) return false;
                offset += read;
            }
            return true;
        }

        static string QuickAccessFilesStamp()
        {
            try
            {
                if (!Directory.Exists(QuickAccessDataDir)) return null;
                var files = Directory.EnumerateFiles(QuickAccessDataDir, "*", SearchOption.AllDirectories)
                    .Where(file => (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                    .Select(file =>
                    {
                        var relative = Path.GetRelativePath(QuickAccessDataDir, file).Replace('\\', '/');
                        if (string.Equals(Path.GetFileName(relative), "z-sieci.json", StringComparison.OrdinalIgnoreCase)) return null;
                        var info = new FileInfo(file);
                        return relative + "\t" + info.Length.ToString(CultureInfo.InvariantCulture) + "\t" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
                    })
                    .Where(line => line != null)
                    .OrderBy(line => line, StringComparer.OrdinalIgnoreCase);
                return Sha256(string.Join("\n", files));
            }
            catch (Exception) { return null; }
        }

        void BroadcastQuickAccessLan()
        {
            if (_lanEncryptionKey == null || _lanAuthenticationKey == null || _lanApplying || _quickAccessLanListener == null) return;
            var activePeers = _lanPeers.Values.Where(peer => DateTime.UtcNow - peer.LastSeenUtc < TimeSpan.FromSeconds(20)).ToList();
            if (activePeers.Count == 0) return;

            var stamp = QuickAccessFilesStamp();
            if (stamp == null) return;
            if (_quickAccessLanArchive == null || stamp != _quickAccessLanStamp)
            {
                if (_quickAccessLanPreparing) return;
                _quickAccessLanPreparing = true;
                _ = Task.Run(() =>
                {
                    var archive = CreateQuickAccessArchive();
                    return new { Archive = archive, Hash = Convert.ToHexString(SHA256.HashData(archive)), Stamp = stamp };
                }).ContinueWith(task => Dispatcher.BeginInvoke(new Action(() =>
                {
                    _quickAccessLanPreparing = false;
                    if (task.IsFaulted)
                    {
                        _quickAccessLanStamp = null;
                        var error = task.Exception?.GetBaseException();
                        if (error != null) { App.LogError(error); LanLog(L.T("Nie udało się przygotować Szybkiego Dostępu do synchronizacji: ") + error.Message); }
                        return;
                    }
                    if (task.IsCanceled) { _quickAccessLanStamp = null; return; }
                    _quickAccessLanArchive = task.Result.Archive;
                    _quickAccessLanHash = task.Result.Hash;
                    _quickAccessLanStamp = task.Result.Stamp;
                    _quickAccessLanSent.Clear();
                    BroadcastQuickAccessLan();
                })));
                return;
            }

            foreach (var peer in activePeers)
            {
                string previousHash;
                if (_quickAccessLanPending.Contains(peer.Id) ||
                    (_quickAccessLanSent.TryGetValue(peer.Id, out previousHash) && previousHash == _quickAccessLanHash)) continue;
                int separator = (peer.Address ?? "").LastIndexOf(':');
                IPAddress address;
                if (separator <= 0 || !IPAddress.TryParse(peer.Address.Substring(0, separator), out address)) continue;
                if (!_quickAccessLanPending.Add(peer.Id)) continue;

                var archiveCopy = (byte[])_quickAccessLanArchive.Clone();
                var encryptionKey = (byte[])_lanEncryptionKey.Clone();
                var authenticationKey = (byte[])_lanAuthenticationKey.Clone();
                var hash = _quickAccessLanHash;
                var peerId = peer.Id;
                var profile = SelectedProfileName;
                var senderId = _lanId;
                var device = _lanDeviceName;
                var token = _lanCts != null ? _lanCts.Token : CancellationToken.None;
                _ = SendQuickAccessLanArchiveAsync(address, peerId, profile, senderId, device, hash, archiveCopy, encryptionKey, authenticationKey, token);
            }
        }

        async Task SendQuickAccessLanArchiveAsync(IPAddress address, string peerId, string profile, string senderId, string device,
            string hash, byte[] archive, byte[] encryptionKey, byte[] authenticationKey, CancellationToken token)
        {
            bool sent = false;
            try
            {
                var header = new QuickAccessLanHeader
                {
                    Type = "quick-access",
                    Id = senderId,
                    Device = device,
                    Profile = profile,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Hash = hash,
                    Length = archive.Length,
                    Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)),
                };
                var nonce = Convert.FromBase64String(header.Nonce);
                var tag = new byte[16];
                var cipher = new byte[archive.Length];
                var associated = QuickAccessLanAssociatedData(header);
                using (var aes = new AesGcm(encryptionKey, 16)) aes.Encrypt(nonce, archive, cipher, tag, associated);
                header.Tag = Convert.ToBase64String(tag);
                header.Mac = Convert.ToBase64String(QuickAccessLanMac(authenticationKey, associated));
                var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header);
                if (headerBytes.Length > 4096) return;

                using (var client = new TcpClient(AddressFamily.InterNetwork))
                {
                    using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                        await client.ConnectAsync(address, QuickAccessLanPort, connectTimeout.Token);
                    }
                    using (var stream = client.GetStream())
                    {
                        var length = BitConverter.GetBytes(headerBytes.Length);
                        await stream.WriteAsync(length, 0, length.Length, token);
                        await stream.WriteAsync(headerBytes, 0, headerBytes.Length, token);
                        await stream.WriteAsync(cipher, 0, cipher.Length, token);
                        var ack = new byte[32];
                        if (!await ReadExactAsync(stream, ack, ack.Length, token)) return;
                        var expected = QuickAccessLanMac(authenticationKey, "ack\n" + hash);
                        sent = CryptographicOperations.FixedTimeEquals(ack, expected);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested) App.LogError(ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(archive);
                CryptographicOperations.ZeroMemory(encryptionKey);
                CryptographicOperations.ZeroMemory(authenticationKey);
                await Dispatcher.InvokeAsync(() =>
                {
                    _quickAccessLanPending.Remove(peerId);
                    if (sent) _quickAccessLanSent[peerId] = hash;
                    else
                    {
                        DateTime last;
                        if (!_quickAccessLanLastFailure.TryGetValue(peerId, out last) || DateTime.UtcNow - last > TimeSpan.FromMinutes(1))
                        {
                            _quickAccessLanLastFailure[peerId] = DateTime.UtcNow;
                            LanLog(L.T("Nie udało się wysłać grafik Szybkiego Dostępu do ") + peerId.Substring(0, Math.Min(8, peerId.Length)) + L.T(". Sprawdź zaporę LAN."));
                        }
                    }
                });
            }
        }

        async Task ReceiveQuickAccessLanArchiveAsync(TcpClient client, CancellationToken token)
        {
            byte[] authenticationKey = null;
            byte[] encryptionKey = null;
            byte[] archive = null;
            NetworkStream stream = null;
            QuickAccessLanHeader header = null;
            try
            {
                using (client)
                {
                    stream = client.GetStream();
                    var lengthBytes = new byte[4];
                    if (!await ReadExactAsync(stream, lengthBytes, lengthBytes.Length, token)) return;
                    int headerLength = BitConverter.ToInt32(lengthBytes, 0);
                    if (headerLength <= 0 || headerLength > 4096) return;
                    var headerBytes = new byte[headerLength];
                    if (!await ReadExactAsync(stream, headerBytes, headerBytes.Length, token)) return;
                    header = JsonSerializer.Deserialize<QuickAccessLanHeader>(headerBytes);
                    if (header == null || header.Type != "quick-access" || !Guid.TryParseExact(header.Id, "N", out _) ||
                        header.Id == _lanId || header.Length <= 0 || header.Length > QuickAccessArchiveLimitBytes) return;
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (header.Timestamp < now - 300000 || header.Timestamp > now + 300000 ||
                        !string.Equals(header.Profile ?? "", SelectedProfileName, StringComparison.OrdinalIgnoreCase)) return;

                    bool authorized = false;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        LanPeerInfo peer;
                        authorized = _lanPeers.TryGetValue(header.Id, out peer) && DateTime.UtcNow - peer.LastSeenUtc < TimeSpan.FromSeconds(30) &&
                            string.Equals(peer.Profile ?? "", header.Profile ?? "", StringComparison.OrdinalIgnoreCase) &&
                            _lanEncryptionKey != null && _lanAuthenticationKey != null &&
                            (!_quickAccessLanPeerStamps.TryGetValue(header.Id, out long lastStamp) || header.Timestamp > lastStamp);
                        if (authorized)
                        {
                            encryptionKey = (byte[])_lanEncryptionKey.Clone();
                            authenticationKey = (byte[])_lanAuthenticationKey.Clone();
                        }
                    });
                    if (!authorized) return;

                    var associated = QuickAccessLanAssociatedData(header);
                    byte[] suppliedMac;
                    try { suppliedMac = Convert.FromBase64String(header.Mac); }
                    catch (FormatException) { return; }
                    var expectedMac = QuickAccessLanMac(authenticationKey, associated);
                    if (suppliedMac.Length != expectedMac.Length || !CryptographicOperations.FixedTimeEquals(suppliedMac, expectedMac)) return;

                    var nonce = Convert.FromBase64String(header.Nonce);
                    var tag = Convert.FromBase64String(header.Tag);
                    if (nonce.Length != 12 || tag.Length != 16) return;
                    var cipher = new byte[header.Length];
                    if (!await ReadExactAsync(stream, cipher, cipher.Length, token)) return;
                    archive = new byte[cipher.Length];
                    using (var aes = new AesGcm(encryptionKey, 16)) aes.Decrypt(nonce, cipher, tag, archive, associated);
                    CryptographicOperations.ZeroMemory(cipher);
                    var actualHash = Convert.ToHexString(SHA256.HashData(archive));
                    if (!string.Equals(actualHash, header.Hash, StringComparison.Ordinal)) return;

                    var receivedArchive = archive;
                    var current = await Task.Run(CreateQuickAccessArchive, token);
                    var currentHash = Convert.ToHexString(SHA256.HashData(current));
                    if (!string.Equals(currentHash, header.Hash, StringComparison.Ordinal))
                    {
                        await Task.Run(() => ImportQuickAccessArchive(receivedArchive), token);
                        CryptographicOperations.ZeroMemory(receivedArchive);
                        Array.Clear(current, 0, current.Length);
                        current = await Task.Run(CreateQuickAccessArchive, token);
                        currentHash = Convert.ToHexString(SHA256.HashData(current));
                        await Dispatcher.InvokeAsync(() => RefreshQuickAccessPages());
                    }
                    else CryptographicOperations.ZeroMemory(receivedArchive);
                    archive = current;
                    var cachedArchive = (byte[])current.Clone();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _quickAccessLanArchive = cachedArchive;
                        _quickAccessLanHash = currentHash;
                        _quickAccessLanStamp = QuickAccessFilesStamp();
                        // Oznaczamy jako wyslane tylko, gdy oba komputery maja identyczne dane. Po scaleniu
                        // (currentHash != header.Hash) odbiorca musi odeslac swoje skroty - inaczej synchronizacja
                        // dzialala tylko w jedna strone i zmiany z tego komputera nigdy nie trafialy do nadawcy.
                        if (string.Equals(currentHash, header.Hash, StringComparison.Ordinal)) _quickAccessLanSent[header.Id] = currentHash;
                        else _quickAccessLanSent.Remove(header.Id);
                        _quickAccessLanPeerStamps[header.Id] = header.Timestamp;
                        LanLog(L.T("Zsynchronizowano skróty i grafiki Szybkiego Dostępu z ") + header.Device + ".");
                    });
                    var ack = QuickAccessLanMac(authenticationKey, "ack\n" + header.Hash);
                    await stream.WriteAsync(ack, 0, ack.Length, token);
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested) App.LogError(ex);
            }
            finally
            {
                if (archive != null) CryptographicOperations.ZeroMemory(archive);
                if (authenticationKey != null) CryptographicOperations.ZeroMemory(authenticationKey);
                if (encryptionKey != null) CryptographicOperations.ZeroMemory(encryptionKey);
            }
        }

        void RefreshQuickAccessPages()
        {
            foreach (var tab in _tabs)
            {
                var core = tab.View.CoreWebView2;
                if (core != null && IsQuickAccessUrl(core.Source) && core.Source.IndexOf("/newtab.html", StringComparison.OrdinalIgnoreCase) >= 0)
                    _ = core.ExecuteScriptAsync("location.reload()");
            }
        }
    }
}