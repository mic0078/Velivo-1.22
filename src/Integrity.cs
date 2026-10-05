using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Przegladarka
{
    // Sprawdzanie pobranych komponentow (yt-dlp, FFmpeg, Piper, uBlock Origin Lite) przed ich uzyciem.
    // Plik, ktorego suma SHA-256 nie zgadza sie z oczekiwana, jest usuwany i nie zostanie uruchomiony.
    static class Integrity
    {
        // Piper ma przypieta wersje - suma wpisana na stale (policzona z oficjalnego wydania 2023.11.14-2).
        internal const string PiperZipSha256 = "f3c58906402b24f3a96d92145f58acba6d86c9b5db896d207f78dc80811efcea";

        internal static string Sha256File(string path)
        {
            using (var fs = File.OpenRead(path)) return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }

        internal static bool Matches(string path, string expectedHex)
        {
            if (string.IsNullOrWhiteSpace(expectedHex) || !File.Exists(path)) return false;
            var exp = expectedHex.Trim().ToLowerInvariant();
            if (exp.StartsWith("sha256:", StringComparison.Ordinal)) exp = exp.Substring(7);
            if (exp.Length != 64) return false;
            return CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(Sha256File(path)), System.Text.Encoding.ASCII.GetBytes(exp));
        }

        // Znacznik Windows "plik z internetu" (Mark-of-the-Web, strumien Zone.Identifier, strefa 3 = Internet).
        // Dzieki niemu SmartScreen ostrzega przy uruchamianiu, a Office otwiera dokument w widoku chronionym (makra zablokowane).
        // Adresu strony nie zapisujemy (HostUrl=about:internet) - prywatnosc, tak jak przy kartach prywatnych w Edge.
        // Nie nadpisuje istniejacego znacznika (np. ustawionego juz przez silnik Edge).
        internal static bool MarkFromInternet(string file)
        {
            try
            {
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) return false;
                var ads = file + ":Zone.Identifier";
                if (File.Exists(ads)) return true;
                File.WriteAllText(ads, "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=about:internet\r\n", System.Text.Encoding.ASCII);
                return true;
            }
            catch (Exception) { return false; }   // np. dysk FAT32/exFAT nie ma strumieni NTFS
        }

        // Linia z pliku sum (format sha256sum: "<64 hex>  nazwa" albo "<64 hex> *nazwa") dla podanej nazwy pliku.
        internal static string FindInSums(string sums, string fileName)
        {
            foreach (var raw in (sums ?? "").Split('\n'))
            {
                var line = raw.Trim();
                int sp = line.IndexOf(' ');
                if (sp != 64) continue;
                var name = line.Substring(sp).Trim().TrimStart('*');
                if (string.Equals(name, fileName, StringComparison.Ordinal)) return line.Substring(0, 64).ToLowerInvariant();
            }
            return null;
        }

        // Pobiera plik sum z wydania i sprawdza plik. Brak sumy = blad (nie uruchamiamy niesprawdzonego programu).
        internal static async Task VerifyAgainstSumsAsync(HttpClient http, string sumsUrl, string fileName, string path)
        {
            string sums;
            using (var resp = await http.GetAsync(sumsUrl)) { resp.EnsureSuccessStatusCode(); sums = await resp.Content.ReadAsStringAsync(); }
            var expected = FindInSums(sums, fileName);
            if (expected == null) throw new InvalidDataException("Brak sumy kontrolnej dla " + fileName + ".");
            if (!Matches(path, expected)) throw new InvalidDataException("Suma kontrolna " + fileName + " się nie zgadza – plik odrzucony.");
        }
    }
}
