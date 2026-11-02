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

namespace Velivo
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
            public bool Phone;   // na zaproszenie z telefonu: komputer zostawia swoj klucz (siec komputerow bez zmian)
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

        void BeginLanPairing(bool phone = false)
        {
            if (_settings == null || !_settings.LanSync || _lanTx == null)
            {
                MessageBox.Show(PairDialogOwner(), L.T("Włącz synchronizację LAN, zapisz ustawienia i otwórz je ponownie, aby rozpocząć parowanie."), "Parowanie Velivo");
                return;
            }
            ExpireLanPairings();
            foreach (var old in _lanPendingPairs.Keys.ToList()) RemoveLanPairing(old);   // nowa prośba zastępuje poprzednią (wcześniej blokada „trwa już próba” na 3 minuty bez możliwości przerwania)

            var pairId = Guid.NewGuid().ToString("N");
            var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _lanPendingPairs[pairId] = new PendingLanPair { Ecdh = ecdh, CreatedUtc = DateTime.UtcNow, Initiator = true, Phone = phone };
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
            LanLog(L.T("Wysłano prośbę o parowanie; oczekuję na potwierdzenie drugiego urządzenia."));
            ShowToast(L.T("Prośba o parowanie LAN została wysłana."), null);
            // po 30 s bez żadnej odpowiedzi - powiedz wprost, co sprawdzić (wcześniej cisza)
            var wait = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            wait.Tick += (s, e) =>
            {
                wait.Stop();
                if (!_lanPendingPairs.TryGetValue(pairId, out var p) || p.TransportKey != null || _lanPairSeen.Contains(pairId)) return;
                var m = L.T("Żaden komputer nie odpowiedział na prośbę o parowanie.\n\nNa drugim komputerze sprawdź: Velivo jest uruchomione, synchronizacja LAN jest włączona, wybrany jest ten sam profil i oba komputery są w tej samej sieci.");
                LanLog(m); MessageBox.Show(PairDialogOwner(), m, L.T("Parowanie Velivo"), MessageBoxButton.OK, MessageBoxImage.Information);
            };
            wait.Start();
        }
        readonly HashSet<string> _lanPairSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // druga strona widzi prośbę (pytanie na ekranie) - bez komunikatu „nikt nie odpowiedział”
        void LanPairNack(LanStatePacket pkt, string why) => LanSend(new LanStatePacket { t = "pair-nack", id = _lanId, device = _lanDeviceName, profile = SelectedProfileName, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), pairId = pkt.pairId, data = why });
        void HandleLanPairNack(LanStatePacket pkt)
        {
            if (!_lanPendingPairs.TryGetValue(pkt.pairId ?? "", out var p) || !p.Initiator || p.TransportKey != null) return;
            if (pkt.data == "ask") { _lanPairSeen.Add(pkt.pairId); return; }
            var who = PairDeviceLabel(pkt.device);
            var m = pkt.data == "profile" ? L.T("Komputer „") + who + L.T("” ma wybrany inny profil: „") + (pkt.profile ?? "") + L.T("”. Wybierz na obu ten sam profil i spróbuj ponownie.")
                : L.T("Komputer „") + who + L.T("” odrzucił parowanie.");
            _lanPairSeen.Add(pkt.pairId); LanLog(m); ShowToast(m, null);
        }

        readonly HashSet<string> _lanPairDone = new HashSet<string>(StringComparer.Ordinal);   // ten sam pakiet parowania dochodzi rozgłoszeniem i wprost - obsłużony raz (bez dwóch okienek)
        void HandleLanPairPacket(LanStatePacket pkt, string sourceAddress)
        {
            if (!_lanPairDone.Add(pkt.t + "|" + pkt.pairId + "|" + pkt.id + "|" + pkt.data)) return;
            try
            {
                switch (pkt.t)
                {
                    case "pair-invite": HandleLanPairInvite(pkt, sourceAddress); break;
                    case "pair-offer": HandleLanPairOffer(pkt, sourceAddress); break;
                    case "pair-response": HandleLanPairResponse(pkt, sourceAddress); break;
                    case "pair-key": HandleLanPairKey(pkt); break;
                    case "pair-ack": HandleLanPairAck(pkt); break;
                    case "pair-nack": HandleLanPairNack(pkt); break;
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                LanLog(L.T("Nie udało się obsłużyć parowania: ") + ex.Message);
            }
        }

        // Zaproszenie z telefonu (Velivo na Androida): parowanie zaczyna telefon; komputer pyta raz na 3 minuty o kazdy telefon,
        // po zgodzie wysyla oferte - dalej ten sam kod na obu ekranach. Klucz komputera zostaje, wiec synchronizacja
        // miedzy komputerami dziala dalej (telefon dolacza do niej, nie zastepuje jej).
        readonly Dictionary<string, DateTime> _lanInvitesAsked = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        void HandleLanPairInvite(LanStatePacket pkt, string sourceAddress)
        {
            if (_settings == null || !_settings.LanSync || _lanTx == null) return;
            ExpireLanPairings();
            if (_lanPendingPairs.Count > 0) return;
            if (_lanInvitesAsked.TryGetValue(pkt.id, out var asked) && DateTime.UtcNow - asked < TimeSpan.FromMinutes(3)) return;
            _lanInvitesAsked[pkt.id] = DateTime.UtcNow;
            var device = PairDeviceLabel(pkt.device);
            var text = L.T("Telefon „") + device + "” (" + sourceAddress + L.T(") prosi o połączenie z Velivo na tym komputerze (profil „") + SelectedProfileName + "”).\n\n" +
                       L.T("Połączyć? Na obu ekranach pojawi się ten sam 6-cyfrowy kod do porównania.");
            if (MessageBox.Show(PairDialogOwner(), text, L.T("Zaproszenie z telefonu"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                LanLog(L.T("Odrzucono zaproszenie z telefonu ") + device + ".");
                return;
            }
            BeginLanPairing(true);
        }

        byte[] CurrentLanKeyBytes()
        {
            try
            {
                var k = _settings?.LanSyncKey;
                if (!AppSettings.IsLanSyncKeyStrong(k)) return null;
                var b = Convert.FromBase64String(k);
                return b.Length == 32 ? b : null;
            }
            catch (FormatException) { return null; }
        }

        void HandleLanPairOffer(LanStatePacket pkt, string sourceAddress)
        {
            if (_settings == null || !_settings.LanSync || _lanTx == null ||
                !Guid.TryParseExact(pkt.pairId, "N", out _) || string.IsNullOrWhiteSpace(pkt.pub) || pkt.pub.Length > 2048)
                return;
            if (!string.Equals(pkt.profile ?? "", SelectedProfileName, StringComparison.OrdinalIgnoreCase))
            {
                LanLog(L.T("Odrzucono prośbę o parowanie z innego profilu."));
                LanPairNack(pkt, "profile");   // nadawca od razu wie, dlaczego
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
                var text = L.T("Urządzenie ") + device + " (" + sourceAddress + L.T(") chce sparować profil „") + SelectedProfileName + "”.\n\n" +
                           L.T("Na obu komputerach musi być widoczny ten sam kod: ") + code + "\n\n" +
                           L.T("Po sparowaniu urządzenia będą synchronizować także zapisane hasła. Czy akceptujesz?");
                LanPairNack(pkt, "ask");   // „pytanie jest na ekranie” - nadawca czeka spokojnie
                if (MessageBox.Show(PairDialogOwner(), text, L.T("Potwierdź parowanie Velivo"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    LanLog(L.T("Odrzucono prośbę o parowanie od ") + device + ".");
                    LanPairNack(pkt, "no");
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
                LanLog(L.T("Zaakceptowano prośbę od ") + device + L.T("; oczekiwanie na zakończenie parowania."));
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
            var text = L.T("Potwierdź sparowanie z ") + device + " (" + sourceAddress + ").\n\n" +
                       L.T("Porównaj kod z drugim komputerem: ") + code + "\n\n" +
                       L.T("Akceptuj tylko wtedy, gdy kody są identyczne. Ten komputer przekaże drugiemu swój klucz LAN – wcześniej sparowane urządzenia zostają.");
            if (MessageBox.Show(PairDialogOwner(), text, L.T("Potwierdź parowanie Velivo"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                CryptographicOperations.ZeroMemory(transportKey);
                RemoveLanPairing(pkt.pairId);
                LanLog(L.T("Anulowano parowanie z ") + device + ".");
                return;
            }

            pending.PeerId = pkt.id;
            pending.PeerDevice = device;
            pending.TransportKey = transportKey;
            pending.LanKey = CurrentLanKeyBytes() ?? RandomNumberGenerator.GetBytes(32);   // klucz grupy (jak dla telefonu) - nowy klucz odcinał wcześniej sparowane komputery (NUCBOX + CHUWI → Ryzen znikał)
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
            LanLog(L.T("Potwierdzono kod z ") + device + L.T("; wysłano zaszyfrowany klucz synchronizacji."));
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
                LanLog(L.T("Parowanie z ") + pending.PeerDevice + L.T(" zakończone. Klucz zapisano lokalnie."));
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
            if (!pending.Phone) AskFirstSyncChoice(pkt.device);   // telefon nie wysyla ustawien - pytanie tylko przy komputerach
            LanLog(L.T("Parowanie z ") + pending.PeerDevice + L.T(" zakończone. Klucz zapisano lokalnie."));
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
                throw new InvalidDataException(L.T("Nieprawidłowe dane parowania."));
            var plain = new byte[cipher.Length];
            using (var gcm = new AesGcm(transportKey, 16))
                gcm.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(pkt.pairId));
            return plain;
        }

        void PersistPairedLanKey(string key)
        {
            if (!AppSettings.IsLanSyncKeyStrong(key)) throw new InvalidDataException(L.T("Nieprawidłowy klucz synchronizacji."));
            var old = _settings.LanSyncKey;
            if (AppSettings.IsLanSyncKeyStrong(old) && old != key) { LanSendRekey(key); LanAddOldKey(old); }   // dawna grupa przechodzi na nowy klucz razem z tym komputerem
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
                L.T("Połączono z ") + other + L.T(".\n\nZakładki, hasła i Szybki Dostęp zostaną POŁĄCZONE z obu komputerów – nic nie zginie.\n\n") +
                L.T("Ustawienia przeglądarki (wygląd, wyszukiwarka, prywatność itd.):\n") +
                L.T("• Tak – zachowaj ustawienia z TEGO komputera\n• Nie – przyjmij ustawienia z ") + other,
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
                LanLog(L.T("Próba parowania wygasła."));
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
            foreach (var target in LanTargets(pkt.t))
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
            var clean = new string((device ?? L.T("Nieznane urządzenie")).Where(c => !char.IsControl(c)).Take(64).ToArray()).Trim();
            return clean.Length == 0 ? L.T("Nieznane urządzenie") : clean;
        }

        // pytanie (parowanie, prośba z telefonu…) zawsze na wierzchu: gdy żadne okno Velivo nie jest aktywne (schowane w zasobniku albo pod innymi programami),
        // właścicielem jest niewidoczne okienko „na wierzchu” na środku ekranu - wcześniej okno pytania ginęło pod innymi oknami i druga strona „nic nie dostawała”
        Window _pairTop;
        Window PairDialogOwner()
        {
            var act = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible);
            if (act != null) return act;
            if (_pairTop == null) _pairTop = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Opacity = 0, ShowInTaskbar = false, Topmost = true, Width = 1, Height = 1, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            try { if (!_pairTop.IsVisible) _pairTop.Show(); _pairTop.Activate(); System.Media.SystemSounds.Asterisk.Play(); } catch (Exception) { return this; }
            return _pairTop;
        }

        string PromptRecoveryPassphrase(string title)
        {
            var input = new PasswordBox { Margin = new Thickness(10), MinWidth = 320, Padding = new Thickness(6) };
            var ok = new Button { Content = L.T("Dalej"), Width = 86, Margin = new Thickness(6), IsDefault = true };
            var cancel = new Button { Content = L.T("Anuluj"), Width = 86, Margin = new Thickness(6), IsCancel = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(10, 10, 10, 0), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(input);
            panel.Children.Add(buttons);
            var win = new Window
            {
                Title = L.T("Odzyskiwanie parowania LAN"),
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
                MessageBox.Show(PairDialogOwner(), L.T("Najpierw sparuj urządzenie lub skonfiguruj synchronizację LAN."), "Odzyskiwanie parowania");
                return;
            }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = L.T("Zapisz zaszyfrowany plik odzyskiwania"),
                FileName = "velivo-parowanie.vlpair",
                Filter = "Velivo Pair Recovery (*.vlpair)|*.vlpair|Wszystkie pliki (*.*)|*.*"
            };
            if (dlg.ShowDialog(PairDialogOwner()) != true) return;
            var pass = PromptRecoveryPassphrase(L.T("Ustaw hasło do pliku odzyskiwania (co najmniej 12 znaków):"));
            if (pass == null) return;
            if (pass.Length < 12)
            {
                MessageBox.Show(PairDialogOwner(), L.T("Hasło musi mieć co najmniej 12 znaków."), "Odzyskiwanie parowania");
                return;
            }
            var confirmation = PromptRecoveryPassphrase(L.T("Wpisz ponownie hasło do pliku odzyskiwania:"));
            if (confirmation == null) return;
            if (!string.Equals(pass, confirmation, StringComparison.Ordinal))
            {
                MessageBox.Show(PairDialogOwner(), L.T("Hasła nie są identyczne."), "Odzyskiwanie parowania");
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
                MessageBox.Show(PairDialogOwner(), L.T("Plik odzyskiwania został zapisany. Przechowuj go poza tym komputerem."), "Odzyskiwanie parowania");
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                MessageBox.Show(PairDialogOwner(), L.T("Nie udało się zapisać pliku odzyskiwania:\n") + ex.Message, "Odzyskiwanie parowania");
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
                Title = L.T("Odtwórz parowanie z pliku recovery"),
                Filter = "Velivo Pair Recovery (*.vlpair)|*.vlpair|Wszystkie pliki (*.*)|*.*"
            };
            if (dlg.ShowDialog(PairDialogOwner()) != true) return;
            var pass = PromptRecoveryPassphrase(L.T("Wpisz hasło do pliku odzyskiwania:"));
            if (pass == null) return;

            byte[] key = null;
            byte[] plain = null;
            try
            {
                if (new FileInfo(dlg.FileName).Length > MaxPairRecoveryBytes)
                    throw new InvalidDataException(L.T("Plik odzyskiwania przekracza limit 1 MB."));
                var package = JsonSerializer.Deserialize<PairRecoveryPackage>(File.ReadAllText(dlg.FileName));
                if (package == null || package.v != 1 || package.iterations < 300000 || package.iterations > 1000000)
                    throw new InvalidDataException(L.T("Nieobsługiwana lub nieprawidłowa wersja pliku odzyskiwania."));
                var salt = Convert.FromBase64String(package.salt ?? "");
                var nonce = Convert.FromBase64String(package.nonce ?? "");
                var tag = Convert.FromBase64String(package.tag ?? "");
                var cipher = Convert.FromBase64String(package.data ?? "");
                if (salt.Length != 16 || nonce.Length != 12 || tag.Length != 16 || cipher.Length == 0 || cipher.Length > MaxPairRecoveryBytes)
                    throw new InvalidDataException(L.T("Nieprawidłowy rozmiar danych pliku odzyskiwania."));
                key = Rfc2898DeriveBytes.Pbkdf2(pass, salt, package.iterations, HashAlgorithmName.SHA256, 32);
                plain = new byte[cipher.Length];
                using (var gcm = new AesGcm(key, 16)) gcm.Decrypt(nonce, cipher, tag, plain);
                var payload = JsonSerializer.Deserialize<PairRecoveryPayload>(plain);
                if (payload == null || payload.Version != 1 || !AppSettings.IsLanSyncKeyStrong(payload.Key))
                    throw new InvalidDataException(L.T("Plik nie zawiera prawidłowego klucza parowania."));
                PersistPairedLanKey(payload.Key);
                StartLanSync();
                MessageBox.Show(PairDialogOwner(), L.T("Parowanie odtworzono. Synchronizacja LAN jest aktywna."), "Odzyskiwanie parowania");
            }
            catch (CryptographicException)
            {
                MessageBox.Show(PairDialogOwner(), L.T("Hasło jest nieprawidłowe albo plik odzyskiwania jest uszkodzony."), "Odzyskiwanie parowania");
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                MessageBox.Show(PairDialogOwner(), L.T("Nie udało się odtworzyć parowania:\n") + ex.Message, "Odzyskiwanie parowania");
            }
            finally
            {
                if (plain != null) CryptographicOperations.ZeroMemory(plain);
                if (key != null) CryptographicOperations.ZeroMemory(key);
            }
        }
    
        // ---------- jedna grupa przy dowolnej liczbie komputerów ----------
        // Parowanie może dać komputerowi inny klucz niż miała jego dotychczasowa grupa (np. NUCBOX + Ryzen, potem CHUWI).
        // Wtedy: (1) nowy klucz idzie do dawnej grupy zaszyfrowany starym kluczem („rekey”); (2) dawne klucze są pamiętane
        // (DPAPI, najwyżej 5) - komputer wyłączony w czasie zmiany dostaje nowy klucz, gdy tylko odezwie się starym.
        sealed class LanRekey { public string k { get; set; } }
        static readonly byte[] LanOldKeysEntropy = Encoding.UTF8.GetBytes("Velivo.LanOldKeys.v1");
        string LanOldKeysPath => Path.Combine(DataDir, "lan-dawne-klucze.dpapi");
        readonly Dictionary<string, (byte[] Enc, byte[] Auth)> _lanOldDerived = new Dictionary<string, (byte[] Enc, byte[] Auth)>();
        readonly Dictionary<string, DateTime> _lanRekeySent = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        List<string> LanOldKeys()
        {
            try { return File.Exists(LanOldKeysPath) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(LanOldKeysPath), LanOldKeysEntropy, DataProtectionScope.CurrentUser)).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList() : new List<string>(); }
            catch (Exception) { return new List<string>(); }
        }

        void LanAddOldKey(string key)
        {
            try
            {
                var l = LanOldKeys(); l.Remove(key); l.Insert(0, key);
                File.WriteAllBytes(LanOldKeysPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(string.Join("\n", l.Take(5))), LanOldKeysEntropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        // nowy klucz do dawnej grupy - zaszyfrowany obecnym (starym) kluczem, więc odczyta go tylko ta grupa
        void LanSendRekey(string newKey)
        {
            if (_lanTx == null || _lanEncryptionKey == null || _lanLegacyNoKeyMode) return;
            try
            {
                var pkt = new LanStatePacket { t = "rekey", id = _lanId, device = _lanDeviceName, profile = SelectedProfileName, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                EncryptLanBlob(pkt, JsonSerializer.SerializeToUtf8Bytes(new LanRekey { k = newKey }));
                LanSend(pkt);
                LanLog(L.T("Nowy klucz grupy wysłany do wcześniej sparowanych urządzeń."));
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        // przyszedł nowy klucz grupy (zaszyfrowany obecnym kluczem) - przechodzimy na niego; nigdy z powrotem na dawny klucz
        void HandleLanRekey(LanStatePacket pkt, LanRekey r)
        {
            if (r == null || !AppSettings.IsLanSyncKeyStrong(r.k) || r.k == _settings.LanSyncKey || LanOldKeys().Contains(r.k)) return;
            PersistPairedLanKey(r.k);
            LanLog(L.T("Nowy klucz grupy od ") + PairDeviceLabel(pkt.device) + L.T(" – synchronizacja dalej działa ze wszystkimi."));
            StartLanSync();
        }

        // komputer odzywa się dawnym kluczem (był wyłączony przy zmianie) - wysyłamy mu obecny klucz zaszyfrowany tym dawnym
        void LanRekeyStraggler(LanStatePacket hello)
        {
            if (_lanTx == null || _settings == null || !AppSettings.IsLanSyncKeyStrong(_settings.LanSyncKey) || string.IsNullOrWhiteSpace(hello.key)) return;
            lock (_lanRekeySent)
            {
                if (_lanRekeySent.TryGetValue(hello.id ?? "", out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(1)) return;
                _lanRekeySent[hello.id ?? ""] = DateTime.UtcNow;
            }
            byte[] supplied;
            try { supplied = Convert.FromBase64String(hello.key); } catch (FormatException) { return; }
            foreach (var old in LanOldKeys())
            {
                (byte[] Enc, byte[] Auth) d;
                lock (_lanOldDerived)
                    if (!_lanOldDerived.TryGetValue(old, out d))
                    {
                        var m = DeriveLanKeyMaterial(old.Trim());
                        d = (m.Take(32).ToArray(), m.Skip(32).Take(32).ToArray());
                        CryptographicOperations.ZeroMemory(m);
                        _lanOldDerived[old] = d;
                    }
                byte[] expected;
                using (var h = new HMACSHA256(d.Auth)) expected = h.ComputeHash(LanAssociatedData(hello));
                if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected)) continue;
                var pkt = new LanStatePacket { t = "rekey", id = _lanId, device = _lanDeviceName, profile = SelectedProfileName, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                var plain = CompressLanPayload(JsonSerializer.SerializeToUtf8Bytes(new LanRekey { k = _settings.LanSyncKey }));
                var nonce = RandomNumberGenerator.GetBytes(12); var cipher = new byte[plain.Length]; var tag = new byte[16];
                using (var gcm = new AesGcm(d.Enc, 16)) gcm.Encrypt(nonce, plain, cipher, tag, LanAssociatedData(pkt));
                CryptographicOperations.ZeroMemory(plain);
                pkt.nonce = Convert.ToBase64String(nonce); pkt.tag = Convert.ToBase64String(tag); pkt.data = Convert.ToBase64String(cipher);
                var dev = PairDeviceLabel(hello.device);
                Dispatcher.BeginInvoke(new Action(() => { LanSend(pkt); LanLog(L.T("Wysłano obecny klucz grupy do ") + dev + L.T(" (miał dawny klucz).")); }));
                return;
            }
        }
}
}