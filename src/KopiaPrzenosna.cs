using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Velivo
{
    // 🚚 kopia do przeniesienia na inny komputer / inne konto Windows:
    //  • pliki szyfrowane kluczem konta Windows (DPAPI: hasła, kontakty i klucze komunikatora, słownik klawiatury, Jellyfin, klucz synchronizacji…)
    //    trafiają do kopii także w postaci zaszyfrowanej hasłem przeniesienia (AES-GCM, PBKDF2) - na nowym koncie Velivo odszyfrowuje je tym hasłem
    //    i szyfruje od nowa kluczem nowego konta; na tym samym koncie hasło nie jest potrzebne;
    //  • ścieżki starego konta (C:\Users\stary\…) w plikach ustawień zamieniane na ścieżki nowego;
    //  • ustawienia z rejestru (kopia, Centrum) wracają razem z danymi.
    public partial class MainWindow
    {
        static readonly string[] DpapiEntropie = { "Velivo.Autofill.v1", "Velivo.Jellyfin", "Velivo komunikator v1", "Velivo.LanOldKeys.v1", "Velivo.OpenSubtitles", "Velivo.Passwords.v1", "Velivo.LanSyncKey.v1", "Velivo.Keyboard.v1" };
        static readonly byte[] DpapiHead = { 1, 0, 0, 0, 0xD0, 0x8C, 0x9D, 0xDF, 0x01, 0x15, 0xD1, 0x11, 0x8C, 0x7A, 0x00, 0xC0, 0x4F, 0xC2, 0x97, 0xEB };
        const string DpapiHead64 = "AQAAANCMnd8BFdERjHoAwE/Cl+s";   // ten sam nagłówek zapisany jako tekst base64
        static readonly byte[] HasloEntropy = Encoding.UTF8.GetBytes("Velivo.Kopia.v1");
        static readonly string[] TekstoweExt = { ".txt", ".json", ".ini", ".xml", ".cfg", ".m3u", ".m3u8", ".csv", ".jsonl" };

        // hasło przeniesienia: w rejestrze, zaszyfrowane kluczem tego konta (ustawia je Velivo Centrum)
        internal static string KopiaHaslo()
        {
            try { var b = KopiaSkladniki.Get("haslo"); return string.IsNullOrEmpty(b) ? null : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(b), HasloEntropy, DataProtectionScope.CurrentUser)); }
            catch (Exception) { return null; }
        }
        static byte[] KluczZHasla(string haslo, byte[] sol) => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(haslo), sol, 200000, HashAlgorithmName.SHA256, 32);

        static bool Naglowek(byte[] b) => b.Length > DpapiHead.Length && b.Take(DpapiHead.Length).SequenceEqual(DpapiHead);
        // plik zaszyfrowany kluczem konta: (0 = surowy, 1 = tekst base64, numer entropii, treść jawna); null - zwykły plik albo nie z tego konta
        static (byte Fmt, byte Ent, byte[] Plain)? Dpapi(byte[] raw)
        {
            byte fmt = 0; var blob = raw;
            if (!Naglowek(raw))
            {
                if (raw.Length < 40 || Encoding.ASCII.GetString(raw, 0, Math.Min(raw.Length, DpapiHead64.Length)) != DpapiHead64) return null;
                try { blob = Convert.FromBase64String(Encoding.ASCII.GetString(raw).Trim()); fmt = 1; } catch (Exception) { return null; }
            }
            for (byte i = 0; i < DpapiEntropie.Length; i++)
                try { return (fmt, i, ProtectedData.Unprotect(blob, Encoding.UTF8.GetBytes(DpapiEntropie[i]), DataProtectionScope.CurrentUser)); } catch (Exception) { }
            return null;
        }

        // --- tworzenie kopii ---
        sealed class Przenosna { public byte[] Klucz; }
        static Przenosna PrzenosnaStart(ZipArchive zip)
        {
            var h = KopiaHaslo(); if (h == null) return null;
            var sol = RandomNumberGenerator.GetBytes(16);
            using (var s = zip.CreateEntry("_przenosne/_sol").Open()) s.Write(sol, 0, sol.Length);
            return new Przenosna { Klucz = KluczZHasla(h, sol) };
        }
        static void PrzenosnaDodaj(Przenosna p, ZipArchive zip, string key, string file)
        {
            if (p == null) return;
            try
            {
                var fi = new FileInfo(file); if (fi.Length > 20L << 20) return;
                byte[] raw; using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { raw = new byte[src.Length]; int n = 0; while (n < raw.Length) { int r = src.Read(raw, n, raw.Length - n); if (r <= 0) break; n += r; } }
                var d = Dpapi(raw); if (d == null) return;
                var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var ct = new byte[d.Value.Plain.Length];
                using (var g = new AesGcm(p.Klucz, 16)) g.Encrypt(nonce, d.Value.Plain, ct, tag);
                using (var s = zip.CreateEntry("_przenosne/" + key).Open()) { s.WriteByte(d.Value.Fmt); s.WriteByte(d.Value.Ent); s.Write(nonce, 0, 12); s.Write(tag, 0, 16); s.Write(ct, 0, ct.Length); }
            }
            catch (Exception) { }
        }
        // ścieżki tego konta i ustawienia z rejestru - do zamiany / odtworzenia na nowym komputerze
        static void PrzenosnaKoniec(ZipArchive zip)
        {
            using (var w = new StreamWriter(zip.CreateEntry("_sciezki.txt").Open()))
                foreach (var (k, v) in Sciezki()) w.WriteLine(k + "\t" + v);
            using (var w = new StreamWriter(zip.CreateEntry("_rejestr.txt").Open()))
                foreach (var sub in new[] { "Kopia", "Centrum" })
                    try
                    {
                        using (var r = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Velivo\" + sub))
                            if (r != null) foreach (var n in r.GetValueNames()) if (n != "haslo") { var v = r.GetValue(n); w.WriteLine(sub + "\t" + n + "\t" + (v is int ? "d" : "s") + "\t" + v); }
                    }
                    catch (Exception) { }
        }
        static IEnumerable<(string, string)> Sciezki()
        {
            yield return ("dane", Path.GetFullPath(DataRoot).TrimEnd('\\'));
            yield return ("lokal", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd('\\'));
            yield return ("dokumenty", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments).TrimEnd('\\'));
            yield return ("profil", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\'));
        }

        // --- przywracanie ---
        // pary stara → nowa ścieżka (najdłuższe najpierw), tylko gdy różne
        static List<(string Old, string New)> SciezkiMapa(ZipArchive zip)
        {
            var map = new List<(string, string)>();
            var e = zip.GetEntry("_sciezki.txt"); if (e == null) return map;
            var now = Sciezki().ToDictionary(x => x.Item1, x => x.Item2);
            using (var r = new StreamReader(e.Open()))
                for (string l; (l = r.ReadLine()) != null;) { var t = l.Split('\t'); if (t.Length == 2 && now.TryGetValue(t[0], out var n) && !string.Equals(t[1], n, StringComparison.OrdinalIgnoreCase)) map.Add((t[1], n)); }
            return map.OrderByDescending(x => x.Item1.Length).ToList();
        }
        static string Zamien(string s, List<(string Old, string New)> map)
        {
            foreach (var (o, n) in map)
            {
                s = s.Replace(o, n, StringComparison.OrdinalIgnoreCase);
                s = s.Replace(o.Replace("\\", "\\\\"), n.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase);   // zapis JSON
                s = s.Replace(o.Replace('\\', '/'), n.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
            return s;
        }
        static void SciezkiWPliku(string file, List<(string Old, string New)> map)
        {
            if (map.Count == 0 || !TekstoweExt.Contains(Path.GetExtension(file).ToLowerInvariant())) return;
            try { var fi = new FileInfo(file); if (fi.Length > 20L << 20) return; var t = File.ReadAllText(file); var z = Zamien(t, map); if (z != t) File.WriteAllText(file, z, new UTF8Encoding(false)); } catch (Exception) { }
        }
        static bool PrzenosnyWpis(string name) => name.StartsWith("_przenosne/", StringComparison.OrdinalIgnoreCase) || name == "_sciezki.txt" || name == "_rejestr.txt";

        // pliki konta: na tym samym koncie zostają jak są; na innym - z kopii zaszyfrowanej hasłem, szyfrowane od nowa kluczem tego konta
        static int PrzenosnaPrzywroc(ZipArchive zip, string haslo, List<(string Old, string New)> map, List<string> bez)
        {
            var solE = zip.GetEntry("_przenosne/_sol"); if (solE == null) return 0;
            byte[] klucz = null; int ile = 0;
            foreach (var e in zip.Entries.Where(x => x.FullName.StartsWith("_przenosne/", StringComparison.OrdinalIgnoreCase) && x.FullName != "_przenosne/_sol"))
            {
                var key = e.FullName.Substring("_przenosne/".Length); var dest = RestoreTarget(key); if (dest == null) continue;
                try
                {
                    if (File.Exists(dest) && Dpapi(File.ReadAllBytes(dest)) != null) continue;   // to samo konto - plik czytelny
                    if (haslo == null) { bez.Add(key); continue; }
                    if (klucz == null) { using (var s = solE.Open()) { var sol = new byte[16]; s.ReadExactly(sol); klucz = KluczZHasla(haslo, sol); } }
                    byte[] all; using (var s = e.Open()) using (var m = new MemoryStream()) { s.CopyTo(m); all = m.ToArray(); }
                    byte fmt = all[0], ent = all[1]; var nonce = all.AsSpan(2, 12).ToArray(); var tag = all.AsSpan(14, 16).ToArray(); var ct = all.AsSpan(30).ToArray(); var plain = new byte[ct.Length];
                    using (var g = new AesGcm(klucz, 16)) g.Decrypt(nonce, ct, tag, plain);
                    if (map.Count > 0) { var t = Encoding.UTF8.GetString(plain); var z = Zamien(t, map); if (z != t) plain = Encoding.UTF8.GetBytes(z); }
                    var blob = ProtectedData.Protect(plain, Encoding.UTF8.GetBytes(DpapiEntropie[ent]), DataProtectionScope.CurrentUser);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    if (fmt == 1) File.WriteAllText(dest, Convert.ToBase64String(blob)); else File.WriteAllBytes(dest, blob);
                    ile++;
                }
                catch (CryptographicException) { bez.Add(key + " (złe hasło przeniesienia)"); }
                catch (Exception ex) { bez.Add(key + " (" + ex.Message + ")"); }
            }
            return ile;
        }
        static void RejestrPrzywroc(ZipArchive zip, List<(string Old, string New)> map)
        {
            var e = zip.GetEntry("_rejestr.txt"); if (e == null) return;
            using (var r = new StreamReader(e.Open()))
                for (string l; (l = r.ReadLine()) != null;)
                {
                    var t = l.Split(new[] { '\t' }, 4); if (t.Length != 4 || (t[0] != "Kopia" && t[0] != "Centrum")) continue;
                    try
                    {
                        using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Velivo\" + t[0]))
                            if (t[2] == "d" && int.TryParse(t[3], out var d)) k.SetValue(t[1], d, Microsoft.Win32.RegistryValueKind.DWord); else k.SetValue(t[1], Zamien(t[3], map));
                    }
                    catch (Exception) { }
                }
        }
        // hasło do przywrócenia - w poleceniu przywrócenia, zaszyfrowane kluczem tego konta (Velivo Centrum / okno 🧹)
        internal static string HasloDoZnacznika(string haslo) => "#haslo:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(haslo), HasloEntropy, DataProtectionScope.CurrentUser));
        static string HasloZeZnacznika(IEnumerable<string> lines)
        {
            var l = lines.FirstOrDefault(x => x.StartsWith("#haslo:")); if (l == null) return null;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(l.Substring(7)), HasloEntropy, DataProtectionScope.CurrentUser)); } catch (Exception) { return null; }
        }
        internal static bool MaPrzenosne(string zipPath) { try { using (var z = ZipFile.OpenRead(zipPath)) return z.GetEntry("_przenosne/_sol") != null; } catch (Exception) { return false; } }
    }
}
