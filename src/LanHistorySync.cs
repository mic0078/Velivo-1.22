using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Przegladarka
{
    // Synchronizacja historii w sieci lokalnej. Historia jest za duza na wspolny pakiet stanu (limit UDP),
    // wiec idzie osobnym, szyfrowanym pakietem "hist": najnowsze wpisy, ile sie zmiesci.
    // Wpisy sa LACZONE (nic nie ginie); usuniecia przenosza sie przez liste usunietych i date "wyczysc wszystko".
    public partial class MainWindow
    {
        static string HistoryTombstonesFile { get { return Path.Combine(DataDir, "historia-usuniete.txt"); } }
        static string HistoryClearedFile { get { return Path.Combine(DataDir, "historia-wyczyszczona.txt"); } }

        sealed class LanHistoryPayload
        {
            public List<string> lines { get; set; }      // linie historia.txt (najnowsze)
            public List<string> deleted { get; set; }    // skroty usunietych linii
            public long cleared { get; set; }            // "wyczysc cala historie" - czas (ms UTC); starsze wpisy znikaja
        }

        static string HistoryLineKey(string line)
        {
            // klucz wpisu: czas + adres (tytul i numer sesji moga sie roznic miedzy komputerami)
            var p = line.Split('\t');
            var key = p.Length >= 2 ? p[0] + "\t" + p[1] : line;
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).Substring(0, 24);
        }

        static DateTime HistoryLineTime(string line)
        {
            DateTime t;
            var tab = line.IndexOf('\t');
            return tab > 0 && DateTime.TryParseExact(line.Substring(0, tab), new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out t) ? t : DateTime.MinValue;
        }

        static HashSet<string> LoadHistoryTombstones()
        {
            try { return File.Exists(HistoryTombstonesFile) ? new HashSet<string>(File.ReadAllLines(HistoryTombstonesFile).Where(l => l.Length > 0)) : new HashSet<string>(); }
            catch (IOException) { return new HashSet<string>(); }
        }

        static long LoadHistoryCleared()
        {
            try { long v; return File.Exists(HistoryClearedFile) && long.TryParse(File.ReadAllText(HistoryClearedFile).Trim(), out v) ? v : 0; }
            catch (IOException) { return 0; }
        }

        // Wywolywane przy usuwaniu wpisow z historii - zeby drugi komputer ich nie odeslal.
        static void RememberDeletedHistory(IEnumerable<string> lines)
        {
            try
            {
                var keys = LoadHistoryTombstones();
                foreach (var l in lines) keys.Add(HistoryLineKey(l));
                File.WriteAllLines(HistoryTombstonesFile, keys.Skip(Math.Max(0, keys.Count - 5000)));
            }
            catch (IOException) { }
        }

        static void RememberHistoryCleared()
        {
            try
            {
                File.WriteAllText(HistoryClearedFile, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
                if (File.Exists(HistoryTombstonesFile)) File.Delete(HistoryTombstonesFile);
            }
            catch (IOException) { }
        }

        string _lastHistoryFp = "";
        DateTime _lastHistorySent = DateTime.MinValue;

        void LanBroadcastHistory(bool force)
        {
            try
            {
                if (_lanTx == null || _lanApplying || _lanLegacyNoKeyMode || _lanEncryptionKey == null || _lanAuthenticationKey == null) return;
                if (_settings == null || !_settings.SaveHistory || _settings.ClearOnExit) return;
                var info = new FileInfo(HistoryFile);
                string fp = (info.Exists ? info.Length + ":" + info.LastWriteTimeUtc.Ticks : "0") + "|" + ReadTextOrEmpty(HistoryTombstonesFile).Length + "|" + LoadHistoryCleared();
                if (!force && fp == _lastHistoryFp && DateTime.UtcNow - _lastHistorySent < TimeSpan.FromMinutes(10)) return;
                _lastHistoryFp = fp; _lastHistorySent = DateTime.UtcNow;

                var all = info.Exists ? File.ReadAllLines(HistoryFile) : new string[0];
                // najnowsze wpisy z ostatnich 60 dni; tyle, ile zmiesci sie w pakiecie UDP
                var since = DateTime.Now.AddDays(-60);
                var recent = all.Where(l => HistoryLineTime(l) >= since).Reverse().ToList();
                var payload = new LanHistoryPayload { deleted = LoadHistoryTombstones().Take(1500).ToList(), cleared = LoadHistoryCleared() };
                int take = Math.Min(recent.Count, 1500);
                while (true)
                {
                    payload.lines = recent.Take(take).ToList();
                    var packed = CompressLanPayload(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload));
                    if (packed.Length <= 30000 || take <= 20) break;
                    take = take * 2 / 3;
                }
                var pkt = new LanStatePacket
                {
                    t = "hist", id = _lanId, device = _lanDeviceName, profile = SelectedProfileName,
                    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), hash = "",
                };
                var plain = CompressLanPayload(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload));
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
                LanSend(pkt);
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        LanHistoryPayload DecryptLanHistory(LanStatePacket pkt)
        {
            if (_lanEncryptionKey == null || string.IsNullOrWhiteSpace(pkt.nonce) || string.IsNullOrWhiteSpace(pkt.tag) || string.IsNullOrWhiteSpace(pkt.data)) return null;
            var nonce = Convert.FromBase64String(pkt.nonce);
            var tag = Convert.FromBase64String(pkt.tag);
            var cipher = Convert.FromBase64String(pkt.data);
            if (nonce.Length != 12 || tag.Length != 16 || cipher.Length == 0 || cipher.Length > 60000) return null;
            var plain = new byte[cipher.Length];
            using (var gcm = new AesGcm(_lanEncryptionKey, 16))
                gcm.Decrypt(nonce, cipher, tag, plain, LanAssociatedData(pkt));
            var json = DecompressLanPayload(plain);
            return System.Text.Json.JsonSerializer.Deserialize<LanHistoryPayload>(json);
        }

        void ApplyLanHistory(LanHistoryPayload incoming)
        {
            if (incoming == null || _settings == null || !_settings.SaveHistory || _settings.ClearOnExit) return;
            try
            {
                var local = File.Exists(HistoryFile) ? File.ReadAllLines(HistoryFile).ToList() : new List<string>();
                var tomb = LoadHistoryTombstones();
                bool tombChanged = false;
                if (incoming.deleted != null) foreach (var k in incoming.deleted) if (k != null && k.Length <= 64 && tomb.Add(k)) tombChanged = true;

                long cleared = Math.Max(LoadHistoryCleared(), incoming.cleared);
                if (cleared > LoadHistoryCleared()) { try { File.WriteAllText(HistoryClearedFile, cleared.ToString(CultureInfo.InvariantCulture)); } catch (IOException) { } }
                var clearedAt = cleared > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(cleared).LocalDateTime : DateTime.MinValue;

                var keys = new HashSet<string>(local.Select(HistoryLineKey));
                var merged = local.Where(l => !tomb.Contains(HistoryLineKey(l)) && HistoryLineTime(l) > clearedAt).ToList();
                bool changed = merged.Count != local.Count;
                if (incoming.lines != null)
                    foreach (var l in incoming.lines)
                    {
                        if (string.IsNullOrEmpty(l) || l.Length > 4000 || l.IndexOf('\n') >= 0 || l.IndexOf('\r') >= 0) continue;
                        var t = HistoryLineTime(l);
                        if (t == DateTime.MinValue || t <= clearedAt) continue;
                        var k = HistoryLineKey(l);
                        if (keys.Contains(k) || tomb.Contains(k)) continue;
                        keys.Add(k); merged.Add(l); changed = true;
                    }
                if (tombChanged) File.WriteAllLines(HistoryTombstonesFile, tomb.Skip(Math.Max(0, tomb.Count - 5000)));
                if (!changed) return;
                // kolejnosc wg czasu - plik zostaje uporzadkowany po polaczeniu
                merged = merged.OrderBy(HistoryLineTime).ToList();
                File.WriteAllLines(HistoryFile, merged);
                _lastHistoryFp = "";   // nasza historia sie zmienila - odeslemy, czego drugi komputer moze nie miec
            }
            catch (Exception ex) { App.LogError(ex); }
        }
    }
}
