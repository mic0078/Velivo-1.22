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

        async Task SendFileToPeer(LanPeerInfo peer)
        {
            // kilka plikow naraz (Ctrl / Shift w oknie wyboru) - wysylane po kolei, telefon pyta o kazdy
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = (L.En ? "Send files to " : "Wyślij pliki do: ") + peer.Device, Multiselect = true };
            if (dlg.ShowDialog(this) != true) return;
            foreach (var f in dlg.FileNames)
            {
                var info = new FileInfo(f);
                if (info.Length > LanFileLimitBytes) { ShowToast((L.En ? "Larger than 100 MB, skipped: " : "Większy niż 100 MB, pominięty: ") + info.Name, null); continue; }
                await SendBytesToPeer(peer, SafeFileName(info.Name), () => File.ReadAllBytes(info.FullName));
            }
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
