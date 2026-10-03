using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Przegladarka
{
    public partial class MainWindow
    {
        sealed class PendingLanPair
        {
            public ECDiffieHellman Ecdh;
            public byte[] TransportKey;
            public byte[] LanKey;
            public string PeerId;
            public string PeerDevice;
            public DateTime CreatedUtc;
            public bool Initiator;
        }

        sealed class PairRecoveryPackage
        {
            public int v { get; set; }
            public int iterations { get; set; }
            public string salt { get; set; }
            public string nonce { get; set; }
            public string tag { get; set; }
            public string data { get; set; }
        }

        sealed class PairRecoveryPayload
        {
            public int Version { get; set; }
            public string Key { get; set; }
        }

        const int PairRecoveryIterations = 600000;
        const long MaxPairRecoveryBytes = 1024 * 1024;
        readonly Dictionary<string, PendingLanPair> _lanPendingPairs = new Dictionary<string, PendingLanPair>(StringComparer.OrdinalIgnoreCase);
        CheckBox _lanSyncSettingCheck;

        void BeginLanPairing()
        {
            if (_settings == null || !_settings.LanSync || _lanTx == null)
            {
                MessageBox.Show(PairDialogOwner(), "Włącz synchronizację LAN, zapisz ustawienia i otwórz je ponownie, aby rozpocząć parowanie.", "Parowanie Velivo");
                return;
            }
            ExpireLanPairings();
            if (_lanPendingPairs.Count > 0)
            {
                MessageBox.Show(PairDialogOwner(), "Trwa już próba parowania. Zakończ ją albo poczekaj na wygaśnięcie.", "Parowanie Velivo");
                return;
            }

            var pairId = Guid.NewGuid().ToString("N");
            var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _lanPendingPairs[pairId] = new PendingLanPair { Ecdh = ecdh, CreatedUtc = DateTime.UtcNow, Initiator = true };
            LanSend(new LanStatePacket
            {
                t = "pair-offer",
                id = _lanId,
                device = _lanDeviceName,
                profile = SelectedProfileName,
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                pairId = pairId,
                pub = Convert.ToBase64String(ecdh.ExportSubjectPublicKeyInfo())
            });
            LanLog("Wysłano prośbę o parowanie; oczekuję na potwierdzenie drugiego urządzenia.");
            ShowToast("Prośba o parowanie LAN została wysłana.", null);
        }

        void HandleLanPairPacket(LanStatePacket pkt, string sourceAddress)
        {
            try
            {
                switch (pkt.t)
                {
                    case "pair-offer": HandleLanPairOffer(pkt, sourceAddress); break;
                    case "pair-response": HandleLanPairResponse(pkt, sourceAddress); break;
                    case "pair-key": HandleLanPairKey(pkt); break;
                    case "pair-ack": HandleLanPairAck(pkt); break;
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                LanLog("Nie udało się obsłużyć parowania: " + ex.Message);
            }
        }

        void HandleLanPairOffer(LanStatePacket pkt, string sourceAddress)
        {
            if (_settings == null || !_settings.LanSync || _lanTx == null ||
                !Guid.TryParseExact(pkt.pairId, "N", out _) || string.IsNullOrWhiteSpace(pkt.pub) || pkt.pub.Length > 2048)
                return;
            if (!string.Equals(pkt.profile ?? "", SelectedProfileName, StringComparison.OrdinalIgnoreCase))
            {
                LanLog("Odrzucono prośbę o parowanie z innego profilu.");
                return;
            }
            if (_lanPendingPairs.ContainsKey(pkt.pairId)) return;

            var local = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            byte[] transportKey = null;
            try
            {
                using (var remote = ECDiffieHellman.Create())
                {
                    var publicBytes = Convert.FromBase64String(pkt.pub);
                    remote.ImportSubjectPublicKeyInfo(publicBytes, out int read);
                    if (read != publicBytes.Length) return;
                    var agreement = local.DeriveKeyMaterial(remote.PublicKey);
                    try { transportKey = DerivePairTransportKey(agreement, pkt.pairId); }
                    finally { CryptographicOperations.ZeroMemory(agreement); }
                }

                var device = PairDeviceLabel(pkt.device);
                var code = PairConfirmationCode(transportKey, pkt.pairId);
                var text = "Urządzenie " + device + " (" + sourceAddress + ") chce sparować profil „" + SelectedProfileName + "”.\n\n" +
                           "Na obu komputerach musi być widoczny ten sam kod: " + code + "\n\n" +
                           "Po sparowaniu urządzenia będą synchronizować także zapisane hasła. Czy akceptujesz?";
                if (MessageBox.Show(PairDialogOwner(), text, "Potwierdź parowanie Velivo", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    LanLog("Odrzucono prośbę o parowanie od " + device + ".");
                    return;
                }

                _lanPendingPairs[pkt.pairId] = new PendingLanPair
                {
                    Ecdh = local,
                    TransportKey = transportKey,
                    PeerId = pkt.id,
                    PeerDevice = device,
                    CreatedUtc = DateTime.UtcNow,
                    Initiator = false
                };
                local = null;
                transportKey = null;
                LanSend(new LanStatePacket
                {
                    t = "pair-response",
                    id = _lanId,
                    device = _lanDeviceName,
                    profile = SelectedProfileName,
                    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    pairId = pkt.pairId,
                    pub = Convert.ToBase64String(_lanPendingPairs[pkt.pairId].Ecdh.ExportSubjectPublicKeyInfo())
                });
                LanLog("Zaakceptowano prośbę od " + device + "; oczekiwanie na zakończenie parowania.");
            }
            finally
            {
                local?.Dispose();
                if (transportKey != null) CryptographicOperations.ZeroMemory(transportKey);
            }
        }

        void HandleLanPairResponse(LanStatePacket pkt, string sourceAddress)
        {
            PendingLanPair pending;
            if (!_lanPendingPairs.TryGetValue(pkt.pairId ?? "", out pending) || !pending.Initiator ||
                string.IsNullOrWhiteSpace(pkt.pub) || pkt.pub.Length > 2048 || pending.TransportKey != null)
                return;
            if (!string.Equals(pkt.profile ?? "", SelectedProfileName, StringComparison.OrdinalIgnoreCase)) return;

            byte[] transportKey;
            using (var remote = ECDiffieHellman.Create())
            {
                var publicBytes = Convert.FromBase64String(pkt.pub);
                remote.ImportSubjectPublicKeyInfo(publicBytes, out int read);
                if (read != publicBytes.Length) return;
                var agreement = pending.Ecdh.DeriveKeyMaterial(remote.PublicKey);
                try { transportKey = DerivePairTransportKey(agreement, pkt.pairId); }
                finally { CryptographicOperations.ZeroMemory(agreement); }
            }

            var device = PairDeviceLabel(pkt.device);
            var code = PairConfirmationCode(transportKey, pkt.pairId);
            var text = "Potwierdź sparowanie z " + device + " (" + sourceAddress + ").\n\n" +
                       "Porównaj kod z drugim komputerem: " + code + "\n\n" +
                       "Akceptuj tylko wtedy, gdy kody są identyczne. Sparowanie zastąpi obecny klucz LAN; pozostałe urządzenia trzeba będzie sparować ponownie.";
            if (MessageBox.Show(PairDialogOwner(), text, "Potwierdź parowanie Velivo", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                CryptographicOperations.ZeroMemory(transportKey);
                RemoveLanPairing(pkt.pairId);
                LanLog("Anulowano parowanie z " + device + ".");
                return;
            }

            pending.PeerId = pkt.id;
            pending.PeerDevice = device;
            pending.TransportKey = transportKey;
            pending.LanKey = RandomNumberGenerator.GetBytes(32);
            var keyPacket = new LanStatePacket
            {
                t = "pair-key",
                id = _lanId,
                device = _lanDeviceName,
                profile = SelectedProfileName,
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                pairId = pkt.pairId
            };
            EncryptPairKey(keyPacket, pending.LanKey, pending.TransportKey);
            LanSend(keyPacket);
            LanLog("Potwierdzono kod z " + device + "; wysłano zaszyfrowany klucz synchronizacji.");
        }

        void HandleLanPairKey(LanStatePacket pkt)
        {
            PendingLanPair pending;
            if (!_lanPendingPairs.TryGetValue(pkt.pairId ?? "", out pending) || pending.Initiator ||
                !string.Equals(pkt.id, pending.PeerId, StringComparison.OrdinalIgnoreCase))
                return;

            var lanKey = DecryptPairKey(pkt, pending.TransportKey);
            try
            {
                if (lanKey.Length != 32) return;
                PersistPairedLanKey(Convert.ToBase64String(lanKey));
                LanSendPairImmediately(new LanStatePacket
                {
                    t = "pair-ack",
                    id = _lanId,
                    device = _lanDeviceName,
                    profile = SelectedProfileName,
                    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    pairId = pkt.pairId,
                    proof = CreatePairAckProof(pending.TransportKey, pkt.pairId, _lanId, pkt.id)
                });
                LanLog("Parowanie z " + pending.PeerDevice + " zakończone. Klucz zapisano lokalnie.");
                StartLanSync();
            }
            finally { CryptographicOperations.ZeroMemory(lanKey); }
        }

        void HandleLanPairAck(LanStatePacket pkt)
        {
            PendingLanPair pending;
            if (!_lanPendingPairs.TryGetValue(pkt.pairId ?? "", out pending) || !pending.Initiator || pending.LanKey == null ||
                !string.Equals(pkt.id, pending.PeerId, StringComparison.OrdinalIgnoreCase))
                return;
            var supplied = Convert.FromBase64String(pkt.proof ?? "");
            var expected = Convert.FromBase64String(CreatePairAckProof(pending.TransportKey, pkt.pairId, pkt.id, _lanId));
            bool valid = supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
            CryptographicOperations.ZeroMemory(supplied);
            CryptographicOperations.ZeroMemory(expected);
            if (!valid) return;

            PersistPairedLanKey(Convert.ToBase64String(pending.LanKey));
            AskFirstSyncChoice(pkt.device);
            LanLog("Parowanie z " + pending.PeerDevice + " zakończone. Klucz zapisano lokalnie.");
            StartLanSync();
        }

        static byte[] DerivePairTransportKey(byte[] agreement, string pairId)
        {
            using (var hmac = new HMACSHA256(agreement))
                return hmac.ComputeHash(Encoding.UTF8.GetBytes("Velivo LAN pairing v1\n" + pairId));
        }

        static string PairConfirmationCode(byte[] key, string pairId)
        {
            using (var hmac = new HMACSHA256(key))
            {
                var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes("Velivo LAN pairing code v1\n" + pairId));
                var number = BitConverter.ToUInt32(digest, 0) % 1000000;
                CryptographicOperations.ZeroMemory(digest);
                return number.ToString("D6", CultureInfo.InvariantCulture);
            }
        }

        static string CreatePairAckProof(byte[] key, string pairId, string responderId, string initiatorId)
        {
            using (var hmac = new HMACSHA256(key))
                return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes("ack\n" + pairId + "\n" + responderId + "\n" + initiatorId)));
        }

        static void EncryptPairKey(LanStatePacket pkt, byte[] lanKey, byte[] transportKey)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[lanKey.Length];
            var tag = new byte[16];
            using (var gcm = new AesGcm(transportKey, 16))
                gcm.Encrypt(nonce, lanKey, cipher, tag, Encoding.UTF8.GetBytes(pkt.pairId));
            pkt.nonce = Convert.ToBase64String(nonce);
            pkt.data = Convert.ToBase64String(cipher);
            pkt.tag = Convert.ToBase64String(tag);
        }

        static byte[] DecryptPairKey(LanStatePacket pkt, byte[] transportKey)
        {
            var nonce = Convert.FromBase64String(pkt.nonce ?? "");
            var cipher = Convert.FromBase64String(pkt.data ?? "");
            var tag = Convert.FromBase64String(pkt.tag ?? "");
            if (nonce.Length != 12 || tag.Length != 16 || cipher.Length != 32)
                throw new InvalidDataException("Nieprawidłowe dane parowania.");
            var plain = new byte[cipher.Length];
            using (var gcm = new AesGcm(transportKey, 16))
                gcm.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(pkt.pairId));
            return plain;
        }

        void PersistPairedLanKey(string key)
        {
            if (!AppSettings.IsLanSyncKeyStrong(key)) throw new InvalidDataException("Nieprawidłowy klucz synchronizacji.");
            _settings.LanSync = true;
            _settings.LanSyncKey = key;
            if (_lanSyncSettingCheck != null) _lanSyncSettingCheck.IsChecked = true;
            _settings.Save(DataDir);
            _lastLanFingerprint = "";
        }

        // Pierwsze polaczenie: czyje USTAWIENIA zostaja. Zakladki, hasla i Szybki Dostep i tak sa laczone z obu.
        void AskFirstSyncChoice(string otherDevice)
        {
            var other = string.IsNullOrWhiteSpace(otherDevice) ? "drugiego komputera" : otherDevice.Trim();
            var ans = MessageBox.Show(PairDialogOwner(),
                "Połączono z " + other + ".\n\nZakładki, hasła i Szybki Dostęp zostaną POŁĄCZONE z obu komputerów – nic nie zginie.\n\n" +
                "Ustawienia przeglądarki (wygląd, wyszukiwarka, prywatność itd.):\n" +
                "• Tak – zachowaj ustawienia z TEGO komputera\n• Nie – przyjmij ustawienia z " + other,
                "Pierwsza synchronizacja", MessageBoxButton.YesNo, MessageBoxImage.Question);
            LoadLanChange();
            var fp = LanContentFingerprint();
            if (ans == MessageBoxResult.Yes) SaveLanChange(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), fp);
            else SaveLanChange(-1, fp);   // -1: kazde ustawienia z drugiego komputera beda "nowsze"
            _lastLanFingerprint = "";   // StartLanSync zaraz wysle stan
        }

        void ExpireLanPairings()
        {
            foreach (var pairId in _lanPendingPairs.Where(x => DateTime.UtcNow - x.Value.CreatedUtc > TimeSpan.FromMinutes(3)).Select(x => x.Key).ToList())
            {
                RemoveLanPairing(pairId);
                LanLog("Próba parowania wygasła.");
            }
        }

        void ClearLanPairings()
        {
            foreach (var pairId in _lanPendingPairs.Keys.ToList()) RemoveLanPairing(pairId);
        }

        void RemoveLanPairing(string pairId)
        {
            PendingLanPair pending;
            if (!_lanPendingPairs.TryGetValue(pairId, out pending)) return;
            _lanPendingPairs.Remove(pairId);
            pending.Ecdh?.Dispose();
            if (pending.TransportKey != null) CryptographicOperations.ZeroMemory(pending.TransportKey);
            if (pending.LanKey != null) CryptographicOperations.ZeroMemory(pending.LanKey);
        }

        void LanSendPairImmediately(LanStatePacket pkt)
        {
            if (_lanTx == null) return;
            var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pkt));
            if (data.Length > 60000) return;
            foreach (var target in LanBroadcastTargets())
            {
                try { _lanTx.Send(data, data.Length, new IPEndPoint(target, LanPort)); }
                catch (Exception) { }
            }
            _lanPacketsTx++;
            _lanLastTxUtc = DateTime.UtcNow;
            RefreshLanDiagnosticsUi();
        }

        static string PairDeviceLabel(string device)
        {
            var clean = new string((device ?? "Nieznane urządzenie").Where(c => !char.IsControl(c)).Take(64).ToArray()).Trim();
            return clean.Length == 0 ? "Nieznane urządzenie" : clean;
        }

        Window PairDialogOwner()
        {
            return Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this;
        }

        string PromptRecoveryPassphrase(string title)
        {
            var input = new PasswordBox { Margin = new Thickness(10), MinWidth = 320, Padding = new Thickness(6) };
            var ok = new Button { Content = "Dalej", Width = 86, Margin = new Thickness(6), IsDefault = true };
            var cancel = new Button { Content = "Anuluj", Width = 86, Margin = new Thickness(6), IsCancel = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(10, 10, 10, 0), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(input);
            panel.Children.Add(buttons);
            var win = new Window
            {
                Title = "Odzyskiwanie parowania LAN",
                Width = 390,
                Height = 170,
                ResizeMode = ResizeMode.NoResize,
                Owner = PairDialogOwner(),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = panel
            };
            ok.Click += (s, e) => { if (!string.IsNullOrWhiteSpace(input.Password)) win.DialogResult = true; };
            return win.ShowDialog() == true ? input.Password : null;
        }

        void ExportLanPairingRecovery()
        {
            if (_settings == null || !AppSettings.IsLanSyncKeyStrong(_settings.LanSyncKey))
            {
                MessageBox.Show(PairDialogOwner(), "Najpierw sparuj urządzenie lub skonfiguruj synchronizację LAN.", "Odzyskiwanie parowania");
                return;
            }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Zapisz zaszyfrowany plik odzyskiwania",
                FileName = "velivo-parowanie.vlpair",
                Filter = "Velivo Pair Recovery (*.vlpair)|*.vlpair|Wszystkie pliki (*.*)|*.*"
            };
            if (dlg.ShowDialog(PairDialogOwner()) != true) return;
            var pass = PromptRecoveryPassphrase("Ustaw hasło do pliku odzyskiwania (co najmniej 12 znaków):");
            if (pass == null) return;
            if (pass.Length < 12)
            {
                MessageBox.Show(PairDialogOwner(), "Hasło musi mieć co najmniej 12 znaków.", "Odzyskiwanie parowania");
                return;
            }
            var confirmation = PromptRecoveryPassphrase("Wpisz ponownie hasło do pliku odzyskiwania:");
            if (confirmation == null) return;
            if (!string.Equals(pass, confirmation, StringComparison.Ordinal))
            {
                MessageBox.Show(PairDialogOwner(), "Hasła nie są identyczne.", "Odzyskiwanie parowania");
                return;
            }

            byte[] plain = null;
            byte[] key = null;
            try
            {
                plain = JsonSerializer.SerializeToUtf8Bytes(new PairRecoveryPayload { Version = 1, Key = _settings.LanSyncKey });
                var salt = RandomNumberGenerator.GetBytes(16);
                var nonce = RandomNumberGenerator.GetBytes(12);
                var cipher = new byte[plain.Length];
                var tag = new byte[16];
                key = Rfc2898DeriveBytes.Pbkdf2(pass, salt, PairRecoveryIterations, HashAlgorithmName.SHA256, 32);
                using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(nonce, plain, cipher, tag);
                var package = new PairRecoveryPackage
                {
                    v = 1,
                    iterations = PairRecoveryIterations,
                    salt = Convert.ToBase64String(salt),
                    nonce = Convert.ToBase64String(nonce),
                    tag = Convert.ToBase64String(tag),
                    data = Convert.ToBase64String(cipher)
                };
                File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(package, new JsonSerializerOptions { WriteIndented = true }));
                MessageBox.Show(PairDialogOwner(), "Plik odzyskiwania został zapisany. Przechowuj go poza tym komputerem.", "Odzyskiwanie parowania");
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                MessageBox.Show(PairDialogOwner(), "Nie udało się zapisać pliku odzyskiwania:\n" + ex.Message, "Odzyskiwanie parowania");
            }
            finally
            {
                if (plain != null) CryptographicOperations.ZeroMemory(plain);
                if (key != null) CryptographicOperations.ZeroMemory(key);
            }
        }

        void ImportLanPairingRecovery()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Odtwórz parowanie z pliku recovery",
                Filter = "Velivo Pair Recovery (*.vlpair)|*.vlpair|Wszystkie pliki (*.*)|*.*"
            };
            if (dlg.ShowDialog(PairDialogOwner()) != true) return;
            var pass = PromptRecoveryPassphrase("Wpisz hasło do pliku odzyskiwania:");
            if (pass == null) return;

            byte[] key = null;
            byte[] plain = null;
            try
            {
                if (new FileInfo(dlg.FileName).Length > MaxPairRecoveryBytes)
                    throw new InvalidDataException("Plik odzyskiwania przekracza limit 1 MB.");
                var package = JsonSerializer.Deserialize<PairRecoveryPackage>(File.ReadAllText(dlg.FileName));
                if (package == null || package.v != 1 || package.iterations < 300000 || package.iterations > 1000000)
                    throw new InvalidDataException("Nieobsługiwana lub nieprawidłowa wersja pliku odzyskiwania.");
                var salt = Convert.FromBase64String(package.salt ?? "");
                var nonce = Convert.FromBase64String(package.nonce ?? "");
                var tag = Convert.FromBase64String(package.tag ?? "");
                var cipher = Convert.FromBase64String(package.data ?? "");
                if (salt.Length != 16 || nonce.Length != 12 || tag.Length != 16 || cipher.Length == 0 || cipher.Length > MaxPairRecoveryBytes)
                    throw new InvalidDataException("Nieprawidłowy rozmiar danych pliku odzyskiwania.");
                key = Rfc2898DeriveBytes.Pbkdf2(pass, salt, package.iterations, HashAlgorithmName.SHA256, 32);
                plain = new byte[cipher.Length];
                using (var gcm = new AesGcm(key, 16)) gcm.Decrypt(nonce, cipher, tag, plain);
                var payload = JsonSerializer.Deserialize<PairRecoveryPayload>(plain);
                if (payload == null || payload.Version != 1 || !AppSettings.IsLanSyncKeyStrong(payload.Key))
                    throw new InvalidDataException("Plik nie zawiera prawidłowego klucza parowania.");
                PersistPairedLanKey(payload.Key);
                StartLanSync();
                MessageBox.Show(PairDialogOwner(), "Parowanie odtworzono. Synchronizacja LAN jest aktywna.", "Odzyskiwanie parowania");
            }
            catch (CryptographicException)
            {
                MessageBox.Show(PairDialogOwner(), "Hasło jest nieprawidłowe albo plik odzyskiwania jest uszkodzony.", "Odzyskiwanie parowania");
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                MessageBox.Show(PairDialogOwner(), "Nie udało się odtworzyć parowania:\n" + ex.Message, "Odzyskiwanie parowania");
            }
            finally
            {
                if (plain != null) CryptographicOperations.ZeroMemory(plain);
                if (key != null) CryptographicOperations.ZeroMemory(key);
            }
        }
    }
}