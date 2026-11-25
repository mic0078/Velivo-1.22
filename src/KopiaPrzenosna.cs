using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

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
        static readonly byte[] KluczEntropy = Encoding.UTF8.GetBytes("Velivo.Kopia.v2");
        static readonly string[] TekstoweExt = { ".txt", ".json", ".ini", ".xml", ".cfg", ".m3u", ".m3u8", ".csv", ".jsonl" };

        // klucz przeniesienia K (losowy, 32 B): w rejestrze zaszyfrowany kluczem tego konta; „zamki” - K zamknięty hasłem i każdym kluczem sprzętowym
        // (jak kopia komunikatora: otwiera hasło ALBO dowolny z kluczy) - zamki idą w każdej kopii, sekretów w nich nie ma
        internal static byte[] KluczPrzeniesienia()
        {
            try { var b = KopiaSkladniki.Get("klucz"); return string.IsNullOrEmpty(b) || string.IsNullOrEmpty(KopiaSkladniki.Get("zamki")) ? null : ProtectedData.Unprotect(Convert.FromBase64String(b), KluczEntropy, DataProtectionScope.CurrentUser); }
            catch (Exception) { return null; }
        }
        static void ZapiszKlucz(byte[] k, string zamki) { KopiaSkladniki.Set("klucz", Convert.ToBase64String(ProtectedData.Protect(k, KluczEntropy, DataProtectionScope.CurrentUser))); KopiaSkladniki.Set("zamki", zamki); }

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
            var k = KluczPrzeniesienia(); if (k == null) return null;
            using (var w = new StreamWriter(zip.CreateEntry("_przenosne/_zamki.json").Open())) w.Write(KopiaSkladniki.Get("zamki"));
            return new Przenosna { Klucz = k };
        }
        static void PrzenosnaDodaj(Przenosna p, ZipArchive zip, string key, string file)
        {
            if (p == null) return;
            try
            {
                var fi = new FileInfo(file); if (fi.Length > 20L << 20) return;
                byte[] raw; using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var m = new MemoryStream()) { src.CopyTo(m); raw = m.ToArray(); }
                var d = Dpapi(raw); if (d == null) return;
                var enc = KomGcm(p.Klucz, d.Value.Plain);
                using (var s = zip.CreateEntry("_przenosne/" + key).Open()) { s.WriteByte(d.Value.Fmt); s.WriteByte(d.Value.Ent); s.Write(enc, 0, enc.Length); }
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
            // ▶ które programy Velivo startowały z Windows (wpis Run z folderu programu Velivo i przełącznik Menedżera zadań)
            using (var w = new StreamWriter(zip.CreateEntry("_autostart.txt").Open()))
                try
                {
                    using (var r = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                        if (r != null) foreach (var n in r.GetValueNames())
                                if (r.GetValue(n) is string c && c.IndexOf(@"\Velivo", StringComparison.OrdinalIgnoreCase) >= 0 && StartZWindows.Wlaczony(n)) w.WriteLine(n + "\t" + c);
                }
                catch (Exception) { }
        }
        // ▶ start z Windows jak na starym komputerze - tylko programy, które są tu zainstalowane (ścieżki konta zamienione)
        static void AutostartPrzywroc(ZipArchive zip, List<(string Old, string New)> map)
        {
            var e = zip.GetEntry("_autostart.txt"); if (e == null) return;
            using (var r = new StreamReader(e.Open()))
                for (string l; (l = r.ReadLine()) != null;)
                {
                    var t = l.Split(new[] { '\t' }, 2); if (t.Length != 2) continue;
                    var c = Zamien(t[1], map); var q = c.TrimStart().StartsWith("\"") ? c.TrimStart().Substring(1).Split('"')[0] : c.Split(' ')[0];
                    if (File.Exists(q)) StartZWindows.Ustaw(t[0], c);
                }
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
        static bool PrzenosnyWpis(string name) => name.StartsWith("_przenosne/", StringComparison.OrdinalIgnoreCase) || name == "_sciezki.txt" || name == "_rejestr.txt" || name == "_autostart.txt";

        // pliki konta: na tym samym koncie zostają jak są; na innym - z kopii zaszyfrowanej hasłem, szyfrowane od nowa kluczem tego konta
        static int PrzenosnaPrzywroc(ZipArchive zip, byte[] klucz, List<(string Old, string New)> map, List<string> bez)
        {
            int ile = 0;
            foreach (var e in zip.Entries.Where(x => x.FullName.StartsWith("_przenosne/", StringComparison.OrdinalIgnoreCase) && x.FullName != "_przenosne/_zamki.json"))
            {
                var key = e.FullName.Substring("_przenosne/".Length); var dest = RestoreTarget(key); if (dest == null) continue;
                try
                {
                    if (File.Exists(dest) && Dpapi(File.ReadAllBytes(dest)) != null) continue;   // to samo konto - plik czytelny
                    if (klucz == null) { bez.Add(key); continue; }
                    byte[] all; using (var s = e.Open()) using (var m = new MemoryStream()) { s.CopyTo(m); all = m.ToArray(); }
                    byte fmt = all[0], ent = all[1];
                    var plain = KomUngcm(klucz, all.AsSpan(2).ToArray());
                    if (map.Count > 0) { var t = Encoding.UTF8.GetString(plain); var z = Zamien(t, map); if (z != t) plain = Encoding.UTF8.GetBytes(z); }
                    var blob = ProtectedData.Protect(plain, Encoding.UTF8.GetBytes(DpapiEntropie[ent]), DataProtectionScope.CurrentUser);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    if (fmt == 1) File.WriteAllText(dest, Convert.ToBase64String(blob)); else File.WriteAllBytes(dest, blob);
                    ile++;
                }
                catch (CryptographicException) { bez.Add(key + " (nie pasuje klucz przeniesienia)"); }
                catch (Exception ex) { bez.Add(key + " (" + ex.Message + ")"); }
            }
            // kolejne kopie na tym komputerze - te same zamki (to samo hasło i klucze sprzętowe)
            if (klucz != null && zip.GetEntry("_przenosne/_zamki.json") is ZipArchiveEntry ze) try { using (var r = new StreamReader(ze.Open())) ZapiszKlucz(klucz, r.ReadToEnd()); } catch (Exception) { }
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
        // klucz przeniesienia w poleceniu przywrócenia - zaszyfrowany kluczem tego konta
        static string KluczDoZnacznika(byte[] k) => "#klucz:" + Convert.ToBase64String(ProtectedData.Protect(k, KluczEntropy, DataProtectionScope.CurrentUser));
        static byte[] KluczZeZnacznika(IEnumerable<string> lines)
        {
            var l = lines.FirstOrDefault(x => x.StartsWith("#klucz:")); if (l == null) return null;
            try { return ProtectedData.Unprotect(Convert.FromBase64String(l.Substring(7)), KluczEntropy, DataProtectionScope.CurrentUser); } catch (Exception) { return null; }
        }
        static string Zamki(string zip) { try { using (var z = ZipFile.OpenRead(zip)) { var e = z.GetEntry("_przenosne/_zamki.json"); if (e == null) return null; using (var r = new StreamReader(e.Open())) return r.ReadToEnd(); } } catch (Exception) { return null; } }
        internal static bool MaPrzenosne(string zipPath) => Zamki(zipPath) != null;

        // ---------- 🔑 okna: zabezpieczenie (hasło, klucze sprzętowe) i odblokowanie przy przywracaniu ----------
        static JsonObject ZamkiObj(string z) { try { return JsonNode.Parse(z ?? "") as JsonObject; } catch (Exception) { return null; } }
        static bool KluczPasuje(JsonObject z, byte[] k) { try { return Encoding.UTF8.GetString(KomUngcm(k, Convert.FromBase64String((string)z["t"]))) == "velivo"; } catch (Exception) { return false; } }

        internal void KopiaZabezpiecz()
        {
            var w = new Window { Title = L.T("🔑 Przeniesienie kopii – hasło i klucze sprzętowe"), Width = 520, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true };
            Independent(w);
            var sp = new StackPanel { Margin = new Thickness(16) };
            sp.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Text = L.T("Dane szyfrowane kontem Windows (zapisane hasła stron, rozmowy i kontakty, słownik klawiatury, Jellyfin, synchronizacja) kopia zabezpiecza dodatkowo. Na nowym komputerze otworzysz je hasłem ALBO dowolnym z dodanych kluczy sprzętowych (także kluczami trybu bankowego). Dotyczy kopii robionych od teraz.") });
            var stan = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
            sp.Children.Add(stan);
            Action pokaz = () =>
            {
                var z = ZamkiObj(KopiaSkladniki.Get("zamki")); var k = KluczPrzeniesienia();
                if (z == null || k == null) { stan.Text = L.T("⚠ Wyłączone – kopię z tymi danymi przywrócisz tylko na tym koncie Windows."); return; }
                var keys = (z["k"] as JsonArray)?.Select(x => (string)x["n"]).ToList() ?? new List<string>();
                stan.Text = L.T("✅ Włączone. Hasło: ") + (z["p"] != null ? L.T("ustawione") : L.T("brak")) + L.T(" · klucze sprzętowe: ") + (keys.Count == 0 ? L.T("brak") : string.Join(", ", keys));
            };
            // K i zamki: istniejące albo nowe
            (byte[] K, JsonObject Z) Biez()
            {
                var k = KluczPrzeniesienia(); var z = ZamkiObj(KopiaSkladniki.Get("zamki"));
                if (k == null || z == null) { k = RandomNumberGenerator.GetBytes(32); z = new JsonObject { ["hs"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), ["t"] = Convert.ToBase64String(KomGcm(k, Encoding.UTF8.GetBytes("velivo"))) }; }
                return (k, z);
            }
            var row = new WrapPanel();
            Button B(string t, Action a) { var b = new Button { Content = t, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 8) }; b.Click += (o, e) => a(); row.Children.Add(b); return b; }
            B(L.T("🔒 Ustaw hasło…"), () =>
            {
                var pw = KomPassword(w, true); if (pw == null) return;
                var (k, z) = Biez(); var salt = RandomNumberGenerator.GetBytes(16);
                z["s"] = Convert.ToBase64String(salt); z["p"] = Convert.ToBase64String(KomGcm(KomPassKey(pw, salt), k));
                ZapiszKlucz(k, z.ToJsonString()); pokaz();
            });
            B(L.T("🔑 Dodaj klucz sprzętowy…"), async () =>
            {
                var (k, z) = Biez(); var hs = Convert.FromBase64String((string)z["hs"]); var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                var have = (z["k"] as JsonArray)?.Select(x => (string)x["c"]).ToHashSet() ?? new HashSet<string>();
                var bank = (LoadBank()?.Keys ?? new List<BankKey>()).Where(x => !have.Contains(x.Id)).ToList();
                try
                {
                    WebAuthn.VerifyResult r;
                    if (bank.Count > 0 && MessageBox.Show(w, L.T("Użyć klucza, który zna już tryb bankowy? Dotkniesz go raz.\n\nTak – klucz z trybu bankowego\nNie – nowy klucz (dotkniesz 2 razy)"), "🔑", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                        r = await Task.Run(() => WebAuthn.Verify(hwnd, BankRpId, bank, hs));
                    else
                    {
                        var nk = await Task.Run(() => WebAuthn.Register(hwnd, BankRpId, have.Select(Convert.FromBase64String).ToList()));
                        r = await Task.Run(() => WebAuthn.Verify(hwnd, BankRpId, new List<BankKey> { nk }, hs));
                    }
                    if (r?.Key == null || r.Hmac == null) { MessageBox.Show(w, L.T("Ten klucz nie umie szyfrować (hmac-secret) – użyj innego klucza albo hasła."), "🔑"); return; }
                    var arr = z["k"] as JsonArray ?? new JsonArray(); z["k"] = arr;
                    arr.Add(new JsonObject { ["c"] = r.Key.Id, ["x"] = r.Key.X, ["y"] = r.Key.Y, ["n"] = string.IsNullOrEmpty(r.Key.Name) ? L.T("klucz ") + (arr.Count + 1) : r.Key.Name, ["w"] = Convert.ToBase64String(KomGcm(KomKeyKek(r.Hmac), k)) });
                    ZapiszKlucz(k, z.ToJsonString()); pokaz();
                }
                catch (Exception ex) { MessageBox.Show(w, "🔑 " + ex.Message, "Velivo"); }
            });
            B(L.T("✖ Wyłącz"), () =>
            {
                if (MessageBox.Show(w, L.T("Wyłączyć zabezpieczenie przeniesienia? Nowe kopie z danymi konta przywrócisz tylko na tym koncie Windows (stare kopie dalej otworzysz dawnym hasłem albo kluczem)."), "🔑", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                KopiaSkladniki.Set("klucz", ""); KopiaSkladniki.Set("zamki", ""); pokaz();
            });
            sp.Children.Add(row); pokaz();
            w.Content = sp; w.Show(); w.Activate();
        }

        // klucz przeniesienia kopii: ten komputer (te same zamki) / klucz sprzętowy / hasło; null - anulowano albo nie pasuje
        async Task<byte[]> KopiaOdblokuj(string zamki, Window owner)
        {
            var z = ZamkiObj(zamki); if (z == null) return null;
            var local = KluczPrzeniesienia(); if (local != null && KluczPasuje(z, local)) return local;
            var keys = (z["k"] as JsonArray)?.Select(x => new BankKey { Id = (string)x["c"], X = (string)x["x"], Y = (string)x["y"], Name = (string)x["n"] }).ToList() ?? new List<BankKey>();
            bool hasPw = z["p"] != null;
            if (keys.Count > 0 && (!hasPw || MessageBox.Show(owner, L.T("Kopia z innego komputera. Otworzyć ją kluczem sprzętowym czy hasłem przeniesienia?\n\nTak – klucz\nNie – hasło"), "🔑", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes))
            {
                try
                {
                    var hw = new System.Windows.Interop.WindowInteropHelper(owner).Handle; var hs = Convert.FromBase64String((string)z["hs"]);
                    var r = await Task.Run(() => WebAuthn.Verify(hw, BankRpId, keys, hs));
                    if (r?.Key != null && r.Hmac != null)
                    {
                        var wr = ((JsonArray)z["k"]).First(x => (string)x["c"] == r.Key.Id);
                        var k = KomUngcm(KomKeyKek(r.Hmac), Convert.FromBase64String((string)wr["w"])); if (KluczPasuje(z, k)) return k;
                    }
                }
                catch (Exception ex) { MessageBox.Show(owner, "🔑 " + ex.Message, "Velivo"); }
                if (!hasPw) { MessageBox.Show(owner, L.T("Klucz nie pasuje do tej kopii – użyj innego klucza."), "🔑"); return null; }
            }
            for (int i = 0; i < 3 && hasPw; i++)
            {
                var pw = KomPassword(owner, false); if (pw == null) return null;
                try { var k = KomUngcm(KomPassKey(pw, Convert.FromBase64String((string)z["s"])), Convert.FromBase64String((string)z["p"])); if (KluczPasuje(z, k)) return k; } catch (Exception) { }
                MessageBox.Show(owner, L.T("Złe hasło przeniesienia."), "🔑");
            }
            return null;
        }

        // przywracanie wybranej kopii (okno 🧹 i Velivo Centrum): łańcuch kopii, odblokowanie danych konta, polecenie dla następnego startu
        internal async void PrzywrocKopie(string zip, Window owner)
        {
            Window tmp = null;
            if (owner == null)   // z Velivo Centrum: własne okienko (klucz sprzętowy i pytania potrzebują okna)
            {
                tmp = owner = new Window { Title = "Velivo", Width = 380, Height = 110, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true,
                    Content = new TextBlock { Text = L.T("♻ Przywróć z kopii…"), Margin = new Thickness(16), FontSize = 14 } };
                owner.Show(); owner.Activate();
            }
            try { await PrzywrocKopieW(zip, owner); } finally { tmp?.Close(); }
        }
        async Task PrzywrocKopieW(string zip, Window owner)
        {
            var chain = RestoreChain(Directory.GetFiles(Path.GetDirectoryName(zip), BackupPrefix + "*.zip"), zip);
            if (chain == null) { MessageBox.Show(owner, L.T("To kopia przyrostowa, a w jej folderze nie ma pełnej kopii, od której się zaczyna. Przenieś je razem do jednego folderu."), "Velivo"); return; }
            if (MessageBox.Show(owner, L.T("Przywrócić dane z kopii\n") + Path.GetFileName(zip) + (chain.Count > 1 ? L.T("\n(pełna kopia + ") + (chain.Count - 1) + L.T(" przyrostów)") : "") + L.T("?\n\nVelivo uruchomi się ponownie. Dane z kopii zastąpią obecne."), "Velivo", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var lines = new List<string>(chain);
            var zamki = chain.Select(Zamki).LastOrDefault(x => x != null);
            if (zamki != null)
            {
                var k = await KopiaOdblokuj(zamki, owner);
                if (k == null && MessageBox.Show(owner, L.T("Bez hasła albo klucza przeniesienia zapisane hasła stron, rozmowy, słownik i konta z tej kopii nie wrócą na tym koncie Windows (reszta wróci). Przywrócić mimo to?"), "Velivo", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                if (k != null) lines.Add(KluczDoZnacznika(k));
            }
            try { File.WriteAllLines(RestoreMarker, lines); RestartVelivo(); }
            catch (Exception ex) { App.LogError(ex); MessageBox.Show(owner, ex.Message, "Velivo"); }
        }
    }
}
