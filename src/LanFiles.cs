using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Threading.Tasks;
using System.Windows;

namespace Velivo
{
    // Pliki miedzy sparowanymi urzadzeniami (komputer <-> komputer / telefon), TCP na porcie LAN:
    // naglowek (nazwa, rozmiar, suma) podpisany HMAC -> odbiorca PYTA uzytkownika -> 1 bajt zgody -> plik
    // zaszyfrowany AES-GCM -> potwierdzenie. Bez zgody nic nie jest przesylane ani zapisywane.
    public partial class MainWindow
    {
        const int LanFileLimitBytes = 100 * 1024 * 1024;

        static byte[] LanFileAssociatedData(QuickAccessLanHeader h)
        {
            return Encoding.UTF8.GetBytes(string.Join("\n", h.Type ?? "", h.Id ?? "", h.Device ?? "", h.Profile ?? "",
                h.Timestamp.ToString(CultureInfo.InvariantCulture), h.Hash ?? "", h.Length.ToString(CultureInfo.InvariantCulture), h.Name ?? ""));
        }

        static string SafeFileName(string name)
        {
            var n = Path.GetFileName((name ?? "").Replace('\\', '/').Split('/').Last());
            n = new string(n.Where(c => !char.IsControl(c) && Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0).ToArray()).Trim().TrimStart('.');
            if (n.Length == 0) n = "plik";
            return n.Length > 120 ? n.Substring(n.Length - 120) : n;
        }

        static string FreeDownloadPath(string name)
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + " (" + i + ")" + Path.GetExtension(name));
            return path;
        }

        async Task ReceiveLanFileAsync(NetworkStream stream, QuickAccessLanHeader header, IPEndPoint remote, CancellationToken token)
        {
            if (header.Length <= 16 || header.Length > LanFileLimitBytes + 16 || !Guid.TryParseExact(header.Id, "N", out _) || header.Id == _lanId) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (Math.Abs(header.Timestamp - now) > 300000) return;
            byte[] enc = null, auth = null;
            string device = null;
            await Dispatcher.InvokeAsync(() =>
            {
                // tylko od sparowanego urzadzenia widzianego w ostatnich 30 s, z tego samego adresu
                if (_lanEncryptionKey == null || _lanAuthenticationKey == null || remote == null) return;
                if (!_lanPeers.TryGetValue(header.Id, out var peer) || DateTime.UtcNow - peer.LastSeenUtc > TimeSpan.FromSeconds(30) ||
                    !(peer.Address ?? "").StartsWith(remote.Address + ":", StringComparison.Ordinal)) return;
                enc = (byte[])_lanEncryptionKey.Clone();
                auth = (byte[])_lanAuthenticationKey.Clone();
                device = PairDeviceLabel(peer.Device);
            });
            if (enc == null) return;
            try
            {
                var ad = LanFileAssociatedData(header);
                byte[] mac;
                try { mac = Convert.FromBase64String(header.Mac ?? ""); } catch (FormatException) { return; }
                var expected = QuickAccessLanMac(auth, ad);
                if (mac.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(mac, expected)) return;

                var name = SafeFileName(header.Name);
                bool accept = false; string target = null;
                await Dispatcher.InvokeAsync(() =>
                {
                    var size = header.Length - 16;
                    var text = (L.En ? "" : "") + device + (L.En ? " is sending a file:\n\n" : " wysyła plik:\n\n") + name + "\n" +
                        (size >= 1048576 ? (size / 1048576.0).ToString("0.0", CultureInfo.CurrentCulture) + " MB" : Math.Max(1, size / 1024) + " KB") +
                        (L.En ? "\n\nAccept? Next you choose where to save it." : "\n\nPrzyjąć? Potem wybierzesz, gdzie go zapisać.");
                    accept = MessageBox.Show(PairDialogOwner(), text, L.En ? "Velivo – file" : "Velivo – plik", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                    if (accept)
                    {   // gdzie zapisac: okno Windows "Zapisz jako" (domyslnie Pobrane, nazwa z telefonu); Anuluj = odrzucenie pliku
                        var def = FreeDownloadPath(name);
                        var dlg = new Microsoft.Win32.SaveFileDialog { Title = (L.En ? "Save file from " : "Zapisz plik od: ") + device, FileName = Path.GetFileName(def), InitialDirectory = Path.GetDirectoryName(def),
                            Filter = (L.En ? "All files" : "Wszystkie pliki") + "|*.*", OverwritePrompt = true };
                        if (dlg.ShowDialog(PairDialogOwner()) == true) target = dlg.FileName; else accept = false;
                    }
                });
                await stream.WriteAsync(new[] { (byte)(accept ? 1 : 0) }, 0, 1, token);
                if (!accept) return;

                var nonce = Convert.FromBase64String(header.Nonce ?? "");
                if (nonce.Length != 12) return;
                var data = new byte[header.Length];
                using (var total = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    total.CancelAfter(TimeSpan.FromMinutes(15));
                    if (!await ReadExactAsync(stream, data, data.Length, total.Token)) return;
                }
                var cipher = data.AsSpan(0, data.Length - 16);
                var tag = data.AsSpan(data.Length - 16);
                var plain = new byte[cipher.Length];
                using (var gcm = new AesGcm(enc, 16)) gcm.Decrypt(nonce, cipher, tag, plain, ad);
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(plain)), header.Hash, StringComparison.OrdinalIgnoreCase)) return;
                var path = target ?? FreeDownloadPath(name);
                File.WriteAllBytes(path, plain);
                var ack = QuickAccessLanMac(auth, "file-ack\n" + header.Hash);
                await stream.WriteAsync(ack, 0, ack.Length, token);
                await Dispatcher.InvokeAsync(() =>
                {
                    LanLog(L.T("Odebrano plik od ") + device + ": " + Path.GetFileName(path));
                    ShowToast((L.En ? "📎 File from " : "📎 Plik od ") + device + ": " + Path.GetFileName(path), path);
                });
            }
            catch (Exception ex) { if (!token.IsCancellationRequested) App.LogError(ex); }
            finally
            {
                CryptographicOperations.ZeroMemory(enc);
                CryptographicOperations.ZeroMemory(auth);
            }
        }

        // 📎 na pasku Velivo i w menu zasobnika: pliki do sparowanego telefonu / komputera (jeden - od razu, kilka - wybór)
        Button _sendFileBtn;
        void InitSendFileButton()
        {
            _sendFileBtn = new Button { Content = "📎", FontSize = 16, Width = 40, Height = 36, Margin = new Thickness(2, 3, 2, 0), ToolTip = L.T("Wyślij pliki do telefonu lub komputera (sparowanego w sieci)") };
            DockPanel.SetDock(_sendFileBtn, Dock.Right);
            _sendFileBtn.Click += (s, e) => SendFilesChoose(_sendFileBtn);
            int at = _tidyBtn != null ? TabBarPanel.Children.IndexOf(_tidyBtn) : -1;
            TabBarPanel.Children.Insert(at >= 0 ? at : 1, _sendFileBtn);
        }

        bool SendFilesReady()
        {
            if (_lanEncryptionKey == null || _lanLegacyNoKeyMode || !_settings.LanSync) { ShowToast(L.T("Najpierw sparuj telefon lub komputer (Synchronizacja LAN w ustawieniach)"), null); return false; }
            if (SendTargets().Count == 0) { ShowToast(L.T("Brak innych Velivo w sieci (muszą być włączone)"), null); return false; }
            return true;
        }

        void SendFilesChoose(FrameworkElement anchor)
        {
            if (!SendFilesReady()) return;
            var t = SendTargets();
            if (t.Count == 1) { _ = SendFileToPeer(t[0]); return; }
            var cm = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var p0 in t) { var p = p0; var it = new MenuItem { Header = "📎 " + (string.IsNullOrWhiteSpace(p.Device) ? p.Id.Substring(0, 8) : p.Device) }; it.Click += async (a, b) => await SendFileToPeer(p); cm.Items.Add(it); }
            cm.IsOpen = true;
        }

        // menu zasobnika: lista urzadzen budowana przy otwarciu
        System.Windows.Forms.ToolStripMenuItem TraySendFilesItem()
        {
            var root = new System.Windows.Forms.ToolStripMenuItem(L.T("📎 Wyślij pliki do…"));
            root.DropDownItems.Add("…");
            root.DropDownOpening += (s, e) => Dispatcher.Invoke(() =>
            {
                root.DropDownItems.Clear();
                var t = (_lanEncryptionKey == null || _lanLegacyNoKeyMode || !_settings.LanSync) ? new List<LanPeerInfo>() : SendTargets();
                if (t.Count == 0) { root.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem(_lanEncryptionKey == null ? L.T("Najpierw sparuj telefon lub komputer (Synchronizacja LAN w ustawieniach)") : L.T("Brak innych Velivo w sieci (muszą być włączone)")) { Enabled = false }); return; }
                foreach (var p0 in t) { var p = p0; root.DropDownItems.Add((string.IsNullOrWhiteSpace(p.Device) ? p.Id.Substring(0, 8) : p.Device), null, (a, b) => Dispatcher.Invoke(async () => await SendFileToPeer(p))); }
            });
            return root;
        }

        async Task SendFileToPeer(LanPeerInfo peer)
        {
            // kilka plikow naraz (Ctrl / Shift w oknie wyboru) - wysylane po kolei, telefon pyta o kazdy
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = (L.En ? "Send files to " : "Wyślij pliki do: ") + peer.Device, Multiselect = true };
            if ((IsVisible ? dlg.ShowDialog(this) : dlg.ShowDialog()) != true) return;   // z zasobnika - okno Velivo bywa schowane
            foreach (var f in dlg.FileNames) await SendFile2ToPeer(peer, f);   // po kawalkach - bez limitu rozmiaru
        }

        // ---------- file2: duze pliki po kawalkach 1 MB (jak w telefonie, LanSync.kt) ----------
        // AES-GCM osobno dla kazdego kawalka: nonce = bazowy XOR numer (ostatnie 4 bajty), AAD = naglowek + numer + znacznik konca
        const int LanChunk = 1 << 20;
        const long LanFile2Limit = 1L << 40;   // bez praktycznego limitu (1 TB - ochrona przed blednym naglowkiem)

        static byte[] LanFile2Ad(QuickAccessLanHeader h)
        {
            return Encoding.UTF8.GetBytes(string.Join("\n", h.Type ?? "", h.Id ?? "", h.Device ?? "", h.Profile ?? "",
                h.Timestamp.ToString(CultureInfo.InvariantCulture), h.Hash ?? "", h.Size.ToString(CultureInfo.InvariantCulture), h.Name ?? ""));
        }
        static byte[] ChunkNonce(byte[] b, int i) { var n = (byte[])b.Clone(); for (int k = 0; k < 4; k++) n[8 + k] ^= (byte)(i >> (24 - 8 * k)); return n; }
        static byte[] ChunkAad(byte[] ad, int i, bool last) { return ad.Concat(Encoding.UTF8.GetBytes("\n" + i.ToString(CultureInfo.InvariantCulture) + "\n" + (last ? "1" : "0"))).ToArray(); }
        static int ChunkCount(long size) { return (int)Math.Max(1, (size + LanChunk - 1) / LanChunk); }

        async Task ReceiveLanFile2Async(NetworkStream stream, QuickAccessLanHeader header, IPEndPoint remote, CancellationToken token)
        {
            if (header.Size < 0 || header.Size > LanFile2Limit || !Guid.TryParseExact(header.Id, "N", out _) || header.Id == _lanId) return;
            if (Math.Abs(header.Timestamp - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) > 300000) return;
            byte[] enc = null, auth = null; string device = null;
            await Dispatcher.InvokeAsync(() =>
            {
                if (_lanEncryptionKey == null || _lanAuthenticationKey == null || remote == null) return;
                if (!_lanPeers.TryGetValue(header.Id, out var peer) || DateTime.UtcNow - peer.LastSeenUtc > TimeSpan.FromSeconds(30) ||
                    !(peer.Address ?? "").StartsWith(remote.Address + ":", StringComparison.Ordinal)) return;
                enc = (byte[])_lanEncryptionKey.Clone(); auth = (byte[])_lanAuthenticationKey.Clone(); device = PairDeviceLabel(peer.Device);
            });
            if (enc == null) return;
            string tmp = null;
            try
            {
                var ad = LanFile2Ad(header);
                byte[] mac; try { mac = Convert.FromBase64String(header.Mac ?? ""); } catch (FormatException) { return; }
                var expected = QuickAccessLanMac(auth, ad);
                if (mac.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(mac, expected)) return;
                byte[] baseNonce; try { baseNonce = Convert.FromBase64String(header.Nonce ?? ""); } catch (FormatException) { return; }
                if (baseNonce.Length != 12) return;
                var name = SafeFileName(header.Name);
                string target = null; bool accept = false;
                await Dispatcher.InvokeAsync(() =>
                {
                    var size = header.Size;
                    var text = device + (L.En ? " is sending a file:\n\n" : " wysyła plik:\n\n") + name + "\n" +
                        (size >= 1073741824 ? (size / 1073741824.0).ToString("0.00", CultureInfo.CurrentCulture) + " GB" : size >= 1048576 ? (size / 1048576.0).ToString("0.0", CultureInfo.CurrentCulture) + " MB" : Math.Max(1, size / 1024) + " KB") +
                        (L.En ? "\n\nAccept? Next you choose where to save it." : "\n\nPrzyjąć? Potem wybierzesz, gdzie go zapisać.");
                    accept = MessageBox.Show(PairDialogOwner(), text, L.En ? "Velivo – file" : "Velivo – plik", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                    if (accept)
                    {
                        var def = FreeDownloadPath(name);
                        var dlg = new Microsoft.Win32.SaveFileDialog { Title = (L.En ? "Save file from " : "Zapisz plik od: ") + device, FileName = Path.GetFileName(def), InitialDirectory = Path.GetDirectoryName(def),
                            Filter = (L.En ? "All files" : "Wszystkie pliki") + "|*.*", OverwritePrompt = true };
                        if (dlg.ShowDialog(PairDialogOwner()) == true) target = dlg.FileName; else accept = false;
                    }
                });
                await stream.WriteAsync(new[] { (byte)(accept ? 1 : 0) }, 0, 1, token);
                if (!accept) return;
                tmp = target + ".velivo-part";   // skladany obok celu; po sprawdzeniu sumy - zamiana na wlasciwy plik
                using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                using (var gcm = new AesGcm(enc, 16))
                {
                    using (var fo = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    {
                        int n = ChunkCount(header.Size); long got = 0; var lb = new byte[4]; var buf = new byte[LanChunk + 16]; var plain = new byte[LanChunk];
                        for (int i = 0; i < n; i++)
                        {
                            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
                            {
                                idle.CancelAfter(TimeSpan.FromSeconds(60));   // bezczynnosc - zerwane polaczenie
                                if (!await ReadExactAsync(stream, lb, 4, idle.Token)) return;
                                int l = BitConverter.ToInt32(lb, 0);
                                if (l < 16 || l > LanChunk + 16) return;
                                if (!await ReadExactAsync(stream, buf, l, idle.Token)) return;
                                gcm.Decrypt(ChunkNonce(baseNonce, i), buf.AsSpan(0, l - 16), buf.AsSpan(l - 16, 16), plain.AsSpan(0, l - 16), ChunkAad(ad, i, i == n - 1));
                                got += l - 16; if (got > header.Size) return;
                                sha.AppendData(plain, 0, l - 16);
                                await fo.WriteAsync(plain.AsMemory(0, l - 16), token);
                            }
                        }
                        if (got != header.Size) return;
                    }
                    if (!string.Equals(Convert.ToHexString(sha.GetHashAndReset()), header.Hash, StringComparison.OrdinalIgnoreCase)) return;
                }
                if (File.Exists(target)) File.Delete(target);   // zgoda na nadpisanie byla w oknie "Zapisz jako"
                File.Move(tmp, target); tmp = null;
                var ack = QuickAccessLanMac(auth, "file-ack\n" + header.Hash);
                await stream.WriteAsync(ack, 0, ack.Length, token);
                await Dispatcher.InvokeAsync(() => { LanLog(L.T("Odebrano plik od ") + device + ": " + Path.GetFileName(target)); ShowToast((L.En ? "📎 File from " : "📎 Plik od ") + device + ": " + Path.GetFileName(target), target); });
            }
            catch (Exception ex) { if (!token.IsCancellationRequested) App.LogError(ex); }
            finally
            {
                if (tmp != null) try { File.Delete(tmp); } catch (Exception) { }
                CryptographicOperations.ZeroMemory(enc); CryptographicOperations.ZeroMemory(auth);
            }
        }

        // wyslanie pliku z dysku po kawalkach (bez wczytywania calego do pamieci)
        async Task SendFile2ToPeer(LanPeerInfo peer, string path)
        {
            int sep = (peer.Address ?? "").LastIndexOf(':');
            if (sep <= 0 || !IPAddress.TryParse(peer.Address.Substring(0, sep), out var address) || _lanEncryptionKey == null) return;
            var enc = (byte[])_lanEncryptionKey.Clone(); var auth = (byte[])_lanAuthenticationKey.Clone();
            var name = SafeFileName(Path.GetFileName(path));
            ShowToast((L.En ? "📎 Waiting for " : "📎 Czekam, aż ") + peer.Device + (L.En ? " to accept…" : " przyjmie plik…") + " " + name, null);
            string result;
            try
            {
                result = await Task.Run(async () =>
                {
                    var size = new FileInfo(path).Length;
                    string hash;
                    using (var f = File.OpenRead(path)) hash = Convert.ToHexString(await SHA256.HashDataAsync(f));
                    var header = new QuickAccessLanHeader
                    {
                        Type = "file2", Id = _lanId, Device = _lanDeviceName, Profile = SelectedProfileName,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Hash = hash, Size = size, Length = 0, Name = name,
                        Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12))
                    };
                    var ad = LanFile2Ad(header);
                    header.Mac = Convert.ToBase64String(QuickAccessLanMac(auth, ad));
                    var baseNonce = Convert.FromBase64String(header.Nonce);
                    var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header);
                    using (var client = new TcpClient(AddressFamily.InterNetwork))
                    {
                        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5))) await client.ConnectAsync(address, LanPort, cts.Token);
                        using (var s = client.GetStream())
                        {
                            await s.WriteAsync(BitConverter.GetBytes(headerBytes.Length));
                            await s.WriteAsync(headerBytes);
                            var answer = new byte[1];
                            using (var wait = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
                                if (!await ReadExactAsync(s, answer, 1, wait.Token)) return "nie odpowiada";
                            if (answer[0] != 1) return "odrzucono";
                            int n = ChunkCount(size); long sent = 0;
                            var buf = new byte[LanChunk]; var outb = new byte[LanChunk + 16];
                            using (var gcm = new AesGcm(enc, 16))
                            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                                for (int i = 0; i < n; i++)
                                {
                                    int len = 0, r;
                                    while (len < LanChunk && (r = await f.ReadAsync(buf.AsMemory(len, LanChunk - len))) > 0) len += r;
                                    sent += len;
                                    gcm.Encrypt(ChunkNonce(baseNonce, i), buf.AsSpan(0, len), outb.AsSpan(0, len), outb.AsSpan(len, 16), ChunkAad(ad, i, i == n - 1));
                                    await s.WriteAsync(BitConverter.GetBytes(len + 16));
                                    await s.WriteAsync(outb.AsMemory(0, len + 16));
                                }
                            if (sent != size) return "błąd";
                            var ack = new byte[32];
                            using (var wait = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
                                if (!await ReadExactAsync(s, ack, 32, wait.Token)) return "błąd";
                            return CryptographicOperations.FixedTimeEquals(ack, QuickAccessLanMac(auth, "file-ack\n" + hash)) ? "ok" : "błąd";
                        }
                    }
                });
            }
            catch (Exception ex) { App.LogError(ex); result = "błąd"; }
            finally { CryptographicOperations.ZeroMemory(enc); CryptographicOperations.ZeroMemory(auth); }
            ShowToast(result == "ok" ? (L.En ? "📎 Sent: " : "📎 Wysłano: ") + name
                : result == "odrzucono" ? (L.En ? "📎 " + peer.Device + " declined the file." : "📎 " + peer.Device + " nie przyjął pliku.")
                : (L.En ? "📎 Could not send the file to " : "📎 Nie udało się wysłać pliku do: ") + peer.Device, null);
        }

        // wyslanie danych jako pliku przez TCP (telefon/komputer pyta o zgode) - takze duze listy TV z 📱 Wyslij Filmy i TV
        async Task SendBytesToPeer(LanPeerInfo peer, string name, Func<byte[]> read)
        {
            int sep = (peer.Address ?? "").LastIndexOf(':');
            if (sep <= 0 || !IPAddress.TryParse(peer.Address.Substring(0, sep), out var address) || _lanEncryptionKey == null) return;
            var enc = (byte[])_lanEncryptionKey.Clone();
            var auth = (byte[])_lanAuthenticationKey.Clone();
            ShowToast((L.En ? "📎 Waiting for " : "📎 Czekam, aż ") + peer.Device + (L.En ? " to accept…" : " przyjmie plik…"), null);
            string result;
            try
            {
                result = await Task.Run(async () =>
                {
                    var plain = read();
                    var header = new QuickAccessLanHeader
                    {
                        Type = "file", Id = _lanId, Device = _lanDeviceName, Profile = SelectedProfileName,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Hash = Convert.ToHexString(SHA256.HashData(plain)),
                        Length = plain.Length + 16, Name = name, Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12))
                    };
                    var ad = LanFileAssociatedData(header);
                    header.Mac = Convert.ToBase64String(QuickAccessLanMac(auth, ad));
                    var data = new byte[plain.Length + 16];
                    using (var gcm = new AesGcm(enc, 16))
                        gcm.Encrypt(Convert.FromBase64String(header.Nonce), plain, data.AsSpan(0, plain.Length), data.AsSpan(plain.Length), ad);
                    var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header);
                    using (var client = new TcpClient(AddressFamily.InterNetwork))
                    {
                        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5))) await client.ConnectAsync(address, LanPort, cts.Token);
                        using (var s = client.GetStream())
                        {
                            await s.WriteAsync(BitConverter.GetBytes(headerBytes.Length));
                            await s.WriteAsync(headerBytes);
                            var answer = new byte[1];
                            using (var wait = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
                                if (!await ReadExactAsync(s, answer, 1, wait.Token)) return "nie odpowiada";
                            if (answer[0] != 1) return "odrzucono";
                            await s.WriteAsync(data);
                            var ack = new byte[32];
                            using (var wait = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
                                if (!await ReadExactAsync(s, ack, 32, wait.Token)) return "błąd";
                            return CryptographicOperations.FixedTimeEquals(ack, QuickAccessLanMac(auth, "file-ack\n" + header.Hash)) ? "ok" : "błąd";
                        }
                    }
                });
            }
            catch (Exception ex) { App.LogError(ex); result = "błąd"; }
            finally { CryptographicOperations.ZeroMemory(enc); CryptographicOperations.ZeroMemory(auth); }
            ShowToast(result == "ok" ? (L.En ? "📎 Sent: " : "📎 Wysłano: ") + name
                : result == "odrzucono" ? (L.En ? "📎 " + peer.Device + " declined the file." : "📎 " + peer.Device + " nie przyjął pliku.")
                : (L.En ? "📎 Could not send the file to " : "📎 Nie udało się wysłać pliku do: ") + peer.Device, null);
        }
    }
}
