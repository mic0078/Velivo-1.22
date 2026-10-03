using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Przegladarka
{
    // Lokalna synchronizacja E2E: eksport/import paczki zaszyfrowanej haslem.
    public partial class MainWindow
    {
        sealed class SyncPayload
        {
            public string Version { get; set; }
            public string Profile { get; set; }
            public string SenderId { get; set; }
            public string SenderName { get; set; }
            public string Settings { get; set; }
            public string Bookmarks { get; set; }
            public string History { get; set; }
            public string Session { get; set; }
            public string SessionActive { get; set; }
            public string PrivacyRules { get; set; }
            public string ExtensionsList { get; set; }
            public string PasswordsVault { get; set; }
            public string QuickAccessData { get; set; }
        }

        sealed class EncryptedSync
        {
            public int v { get; set; }
            public int iterations { get; set; }
            public string salt { get; set; }
            public string nonce { get; set; }
            public string tag { get; set; }
            public string data { get; set; }
        }

        sealed class SyncPeerRecord
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public DateTime FirstSeen { get; set; }
            public DateTime LastSeen { get; set; }
        }

        const int SyncIterations = 600000;
        const long MaxSyncPackageBytes = 64L * 1024 * 1024;
        static string SyncDeviceIdFile { get { return Path.Combine(DataDir, "sync-device-id.txt"); } }
        static string SyncPeersFile { get { return Path.Combine(DataDir, "sync-peers.json"); } }

        static byte[] ReadIfExists(string path)
        {
            try { return File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>(); }
            catch (Exception) { return Array.Empty<byte>(); }
        }

        static void WriteIfNotEmpty(string path, string content)
        {
            if (content == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
        }

        void SyncExport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Zapisz paczkę synchronizacji",
                    FileName = "velivo-sync-" + SelectedProfileName + "-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".vlsync",
                    Filter = "Velivo Sync (*.vlsync)|*.vlsync|Wszystkie pliki (*.*)|*.*"
                };
                if (dlg.ShowDialog(this) != true) return;
                var accessCode = GenerateSyncAccessCode();

                var payload = new SyncPayload
                {
                    Version = "1.21",
                    Profile = SelectedProfileName,
                    SenderId = GetSyncDeviceId(),
                    SenderName = Environment.MachineName,
                    Settings = Encoding.UTF8.GetString(ReadIfExists(Path.Combine(DataDir, "ustawienia.txt"))),
                    Bookmarks = Encoding.UTF8.GetString(ReadIfExists(Path.Combine(DataDir, "zakladki.txt"))),
                    History = Encoding.UTF8.GetString(ReadIfExists(Path.Combine(DataDir, "historia.txt"))),
                    Session = Encoding.UTF8.GetString(ReadIfExists(Path.Combine(DataDir, "sesja.txt"))),
                    SessionActive = Encoding.UTF8.GetString(ReadIfExists(Path.Combine(DataDir, "sesja.txt.aktywna"))),
                    PrivacyRules = Encoding.UTF8.GetString(ReadIfExists(Path.Combine(DataDir, "prywatnosc.txt"))),
                    ExtensionsList = Encoding.UTF8.GetString(ReadIfExists(ExtensionsSyncListFile)),
                    PasswordsVault = ExportPasswordsForSync(),
                    QuickAccessData = ExportQuickAccessBase64(),
                };

                var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
                var tempFile = dlg.FileName + ".tmp";
                try
                {
                    var pkg = EncryptSync(plain, accessCode);
                    File.WriteAllText(tempFile, JsonSerializer.Serialize(pkg, new JsonSerializerOptions { WriteIndented = true }));
                    if (new FileInfo(tempFile).Length > MaxSyncPackageBytes)
                        throw new InvalidDataException("Paczka przekracza limit 64 MB. Zmniejsz liczbę miniaturek w Szybkim Dostępie.");
                    File.Move(tempFile, dlg.FileName, true);
                    ShowSyncAccessCode(accessCode, dlg.FileName);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plain);
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                MessageBox.Show(this, "Nie udało się wyeksportować synchronizacji:\n" + ex.Message, "Synchronizacja");
            }
        }

        void SyncImport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Wczytaj paczkę synchronizacji",
                    Filter = "Velivo Sync (*.vlsync)|*.vlsync|Wszystkie pliki (*.*)|*.*"
                };
                if (dlg.ShowDialog(this) != true) return;
                var accessCode = PromptSyncAccessCode();
                if (string.IsNullOrWhiteSpace(accessCode)) return;
                if (new FileInfo(dlg.FileName).Length > MaxSyncPackageBytes)
                    throw new InvalidDataException("Paczka synchronizacji przekracza limit 64 MB.");

                var pkg = JsonSerializer.Deserialize<EncryptedSync>(File.ReadAllText(dlg.FileName));
                if (pkg == null) throw new InvalidDataException("Niepoprawny format paczki.");
                var plain = DecryptSync(pkg, accessCode);
                try
                {
                    var payload = JsonSerializer.Deserialize<SyncPayload>(Encoding.UTF8.GetString(plain));
                    if (payload == null) throw new InvalidDataException("Niepoprawna zawartość paczki.");

                    var peerName = string.IsNullOrWhiteSpace(payload.SenderName) ? "nieznane urządzenie" : payload.SenderName;
                    bool hasPeerId = Guid.TryParseExact(payload.SenderId, "N", out _);
                    bool knownPeer = hasPeerId && IsKnownSyncPeer(payload.SenderId);
                    var status = !hasPeerId
                        ? "Ta starsza paczka nie zawiera identyfikatora urządzenia."
                        : knownPeer ? "To urządzenie było już wcześniej rozpoznane." : "To nowe urządzenie zostanie zapamiętane po imporcie.";
                    if (MessageBox.Show(this,
                        "Paczka od: " + peerName + "\nProfil: " + (payload.Profile ?? "-") + "\n" + status +
                        "\n\nImport zastąpi lokalne ustawienia, zakładki, historię, sesję, reguły i sejf haseł. Skróty Szybkiego Dostępu Velivo i ich grafiki zostaną scalone z lokalnymi. Kontynuować?",
                        "Potwierdź import synchronizacji", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

                    WriteIfNotEmpty(Path.Combine(DataDir, "ustawienia.txt"), payload.Settings);
                    WriteIfNotEmpty(Path.Combine(DataDir, "zakladki.txt"), payload.Bookmarks);
                    WriteIfNotEmpty(Path.Combine(DataDir, "historia.txt"), payload.History);
                    WriteIfNotEmpty(Path.Combine(DataDir, "sesja.txt"), payload.Session);
                    WriteIfNotEmpty(Path.Combine(DataDir, "sesja.txt.aktywna"), payload.SessionActive);
                    WriteIfNotEmpty(Path.Combine(DataDir, "prywatnosc.txt"), payload.PrivacyRules);
                    WriteIfNotEmpty(ExtensionsSyncListFile, payload.ExtensionsList);
                    ImportPasswordsFromSync(payload.PasswordsVault);
                    ImportQuickAccessBase64(payload.QuickAccessData);
                    RememberSyncPeer(payload.SenderId, peerName);

                    _ = ApplyExtensionsSyncListAsync();

                    MessageBox.Show(this, "Wczytano paczkę od " + peerName + ". Urządzenie zostało zapamiętane. Dla pełnego efektu uruchom ponownie przeglądarkę.", "Synchronizacja");
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            catch (CryptographicException)
            {
                MessageBox.Show(this, "Niepoprawny kod dostępu albo uszkodzona paczka.", "Synchronizacja");
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                MessageBox.Show(this, "Nie udało się wczytać synchronizacji:\n" + ex.Message, "Synchronizacja");
            }
        }

        static string GetSyncDeviceId()
        {
            try
            {
                if (File.Exists(SyncDeviceIdFile))
                {
                    var existing = File.ReadAllText(SyncDeviceIdFile).Trim();
                    if (Guid.TryParseExact(existing, "N", out _)) return existing;
                }
                Directory.CreateDirectory(DataDir);
                var id = Guid.NewGuid().ToString("N");
                var temp = SyncDeviceIdFile + ".tmp";
                File.WriteAllText(temp, id);
                File.Move(temp, SyncDeviceIdFile, true);
                return id;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return Guid.NewGuid().ToString("N");
            }
        }

        static List<SyncPeerRecord> LoadSyncPeers()
        {
            try
            {
                if (!File.Exists(SyncPeersFile)) return new List<SyncPeerRecord>();
                return JsonSerializer.Deserialize<List<SyncPeerRecord>>(File.ReadAllText(SyncPeersFile)) ?? new List<SyncPeerRecord>();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                return new List<SyncPeerRecord>();
            }
        }

        static bool IsKnownSyncPeer(string id)
        {
            return Guid.TryParseExact(id, "N", out _) && LoadSyncPeers().Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        static void RememberSyncPeer(string id, string name)
        {
            if (!Guid.TryParseExact(id, "N", out _)) return;
            var peers = LoadSyncPeers();
            var now = DateTime.UtcNow;
            var peer = peers.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (peer == null)
            {
                peer = new SyncPeerRecord { Id = id, FirstSeen = now };
                peers.Add(peer);
            }
            peer.Name = new string((name ?? "").Where(c => !char.IsControl(c)).Take(64).ToArray());
            peer.LastSeen = now;
            peers = peers.OrderByDescending(x => x.LastSeen).Take(50).ToList();
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(SyncPeersFile, JsonSerializer.Serialize(peers, new JsonSerializerOptions { WriteIndented = true }));
        }

        static string GenerateSyncAccessCode()
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var bytes = RandomNumberGenerator.GetBytes(24);
            var code = new StringBuilder(bytes.Length);
            foreach (var value in bytes) code.Append(alphabet[value & 31]);
            CryptographicOperations.ZeroMemory(bytes);
            return code.ToString();
        }

        Window ActiveSyncDialogOwner()
        {
            return Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this;
        }

        void ShowSyncAccessCode(string accessCode, string filePath)
        {
            var code = new TextBox
            {
                Text = accessCode,
                IsReadOnly = true,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 18,
                Padding = new Thickness(8),
                Margin = new Thickness(12, 8, 12, 4),
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            var copy = SmallButton("Kopiuj kod", () =>
            {
                try { Clipboard.SetText(accessCode); }
                catch (Exception ex) { App.LogError(ex); }
            });
            var done = new Button { Content = "Gotowe", Width = 88, Height = 30, Margin = new Thickness(6), IsDefault = true, IsCancel = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0, 8, 8) };
            buttons.Children.Add(copy);
            buttons.Children.Add(done);
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = "Paczka nie wygasa. Przenieś ją przez EchoSync; na drugim komputerze wybierz Importuj paczkę i wpisz ten kod. Zachowaj kod osobno, nie jest zapisany w pliku.",
                Margin = new Thickness(12, 12, 12, 0),
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(code);
            panel.Children.Add(new TextBlock { Text = Path.GetFileName(filePath), Margin = new Thickness(12, 0, 12, 8), Foreground = Brushes.Gray });
            panel.Children.Add(buttons);
            var win = new Window
            {
                Title = "Kod dostępu do paczki",
                Width = 480,
                Height = 210,
                ResizeMode = ResizeMode.NoResize,
                Owner = ActiveSyncDialogOwner(),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = panel
            };
            win.ShowDialog();
        }

        string PromptSyncAccessCode()
        {
            var input = new PasswordBox { Margin = new Thickness(12, 8, 12, 4), Padding = new Thickness(6), MinWidth = 340 };
            var ok = new Button { Content = "Odszyfruj", Width = 88, Height = 30, Margin = new Thickness(6), IsDefault = true };
            var cancel = new Button { Content = "Anuluj", Width = 88, Height = 30, Margin = new Thickness(6), IsCancel = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0, 8, 8) };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "Wpisz kod dostępu wyświetlony przy eksporcie paczki:", Margin = new Thickness(12, 12, 12, 0), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(input);
            panel.Children.Add(buttons);
            var win = new Window
            {
                Title = "Import paczki synchronizacji",
                Width = 420,
                Height = 170,
                ResizeMode = ResizeMode.NoResize,
                Owner = ActiveSyncDialogOwner(),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = panel
            };
            ok.Click += (s, e) => { if (!string.IsNullOrWhiteSpace(input.Password)) win.DialogResult = true; };
            return win.ShowDialog() == true ? input.Password : null;
        }

        static EncryptedSync EncryptSync(byte[] plain, string pass)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var key = Rfc2898DeriveBytes.Pbkdf2(pass, salt, SyncIterations, HashAlgorithmName.SHA256, 32);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            try
            {
                using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(nonce, plain, cipher, tag);
                return new EncryptedSync
                {
                    v = 2,
                    iterations = SyncIterations,
                    salt = Convert.ToBase64String(salt),
                    nonce = Convert.ToBase64String(nonce),
                    tag = Convert.ToBase64String(tag),
                    data = Convert.ToBase64String(cipher)
                };
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }

        static byte[] DecryptSync(EncryptedSync pkg, string pass)
        {
            if (pkg.v != 1 && pkg.v != 2) throw new InvalidDataException("Nieobsługiwana wersja paczki synchronizacji.");
            int iterations = pkg.v == 1 ? 120000 : pkg.iterations;
            if (iterations < 120000 || iterations > 1000000) throw new InvalidDataException("Nieprawidłowe parametry paczki synchronizacji.");
            var salt = Convert.FromBase64String(pkg.salt);
            var nonce = Convert.FromBase64String(pkg.nonce);
            var tag = Convert.FromBase64String(pkg.tag);
            var cipher = Convert.FromBase64String(pkg.data);
            if (salt.Length != 16 || nonce.Length != 12 || tag.Length != 16 || cipher.Length == 0 || cipher.LongLength > MaxSyncPackageBytes)
                throw new InvalidDataException("Nieprawidłowy rozmiar danych w paczce synchronizacji.");
            var key = Rfc2898DeriveBytes.Pbkdf2(pass, salt, iterations, HashAlgorithmName.SHA256, 32);
            var plain = new byte[cipher.Length];
            try
            {
                using (var gcm = new AesGcm(key, 16)) gcm.Decrypt(nonce, cipher, tag, plain);
                return plain;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(plain);
                throw;
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
    }
}
