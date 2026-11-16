using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Velivo
{
    // Folder danych uzytkownika: %LOCALAPPDATA%\Velivo\Dane (dawniej %LOCALAPPDATA%\Programs\Velivo\Dane) - osobno od plikow
    // programu (instalator nadpisuje tylko swoje pliki; dezinstalacja zostawia dane). Sciezka stala, niezalezna od tego,
    // skad uruchomiono Velivo.exe - inaczej powstawaly drugie, osobne dane.
    // Dawniej %LOCALAPPDATA%\Przegladarka - nazwa sprzed rozbudowy programu. Przy starcie przenosimy go
    // jedna zmiana nazwy (na tym samym dysku: cale albo nic). Gdy sie nie da (pliki zajete), zostajemy na starym
    // folderze i probujemy przy nastepnym uruchomieniu - dane nigdy nie sa kopiowane ani dzielone na dwa miejsca.
    static class VelivoData
    {
        static string Local { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
        // 🧩 dane wspólne wszystkich programów Velivo (pełne, Odtwarzacz, Przeglądarka, Komunikator) - POZA folderem któregokolwiek programu:
        // odinstalowanie albo skasowanie folderu programu nigdy ich nie zabiera
        public static string NewRoot { get { return Path.Combine(Local, "Velivo", "Dane"); } }
        // każdy program Velivo wie, gdzie są dane: spis w Windows (HKCU\Software\Velivo, „Dane”) - gdyby folder był kiedyś gdzie indziej, wszyscy idą za wpisem
        static string SpisDane { get { try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SpisKey)) { var d = k?.GetValue("Dane") as string; return !string.IsNullOrEmpty(d) && Directory.Exists(d) && d != MidRoot ? d : null; } } catch (Exception) { return null; } } }
        public static string MidRoot { get { return Path.Combine(Local, "Programs", "Velivo", "Dane"); } }   // do 2026-11: w folderze programu Velivo
        public static string OldRoot { get { return Path.Combine(Local, "Przegladarka"); } }

        // zapis „wszystko albo nic”: najpierw obok (.tmp), potem podmiana - wysypanie się / restart w trakcie nie zostawia obciętego pliku
        public static void SafeWrite(string path, byte[] data)
        {
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) { fs.Write(data, 0, data.Length); fs.Flush(true); }
            for (int i = 0; ; i++)
            {   // podmiana nie wychodzi, gdy drugi program Velivo właśnie czyta ten plik (Odmowa dostępu) - chwila czekania i znowu
                try { File.Move(tmp, path, true); return; }
                catch (Exception ex) when ((ex is UnauthorizedAccessException || ex is IOException) && i < 20) { System.Threading.Thread.Sleep(100); }
            }
        }
        public static void SafeWrite(string path, string text) => SafeWrite(path, System.Text.Encoding.UTF8.GetBytes(text));

        // 🧩 kilka programów Velivo (pełne, Odtwarzacz, Przeglądarka) na jednym folderze danych: zapis pliku „klucz<sep>wartość”
        // bez kasowania cudzych zmian. Na dysk trafia aktualny plik + tylko to, co TEN program zmienił od ostatniego odczytu
        // (baseline); usunięty u nas klucz znika tylko, gdy nikt go w międzyczasie nie zmienił. Zwraca to, co zapisano (nowy baseline).
        // 🧩 czynność, którą na komputerze wykonuje tylko JEDEN program Velivo (wznowienie TV, zaplanowane nagrania, karteczki, widżet na pulpicie…):
        // pierwszy program ją bierze i trzyma do zamknięcia; drugi dostaje „nie” i może pytać ponownie (przejmie po zamknięciu pierwszego).
        // Wołać zawsze z wątku okna (muteks należy do wątku).
        static readonly System.Collections.Generic.Dictionary<string, System.Threading.Mutex> _jeden = new System.Collections.Generic.Dictionary<string, System.Threading.Mutex>();
        public static bool Jeden(string co)
        {
            if (_jeden.ContainsKey(co)) return true;
            try
            {
                var m = new System.Threading.Mutex(false, @"Local\Velivo.Jeden." + co);
                bool got;
                try { got = m.WaitOne(0); } catch (System.Threading.AbandonedMutexException) { got = true; }
                if (got) { _jeden[co] = m; return true; }
                m.Dispose(); return false;
            }
            catch (Exception) { return true; }   // bez muteksu (brak uprawnień) - jak dawniej, pojedynczy program
        }

        public static System.Collections.Generic.Dictionary<string, string> MergeWrite(string path, char sep,
            System.Collections.Generic.IList<System.Collections.Generic.KeyValuePair<string, string>> mine,
            System.Collections.Generic.Dictionary<string, string> baseline)
        {
            using (var mx = new System.Threading.Mutex(false, @"Local\Velivo.Dane.Zapis"))
            {
                bool got = false;
                try { got = mx.WaitOne(3000); } catch (System.Threading.AbandonedMutexException) { got = true; }
                try
                {
                    var disk = new System.Collections.Generic.Dictionary<string, string>();
                    var order = new System.Collections.Generic.List<string>();
                    if (File.Exists(path))
                        foreach (var l in File.ReadAllLines(path))
                        {
                            int i = l.IndexOf(sep); if (i <= 0) continue;
                            var k = l.Substring(0, i); if (!disk.ContainsKey(k)) order.Add(k);
                            disk[k] = l.Substring(i + 1);
                        }
                    var result = new System.Collections.Generic.Dictionary<string, string>(disk);
                    var mineKeys = new System.Collections.Generic.HashSet<string>();
                    foreach (var kv in mine)
                    {
                        mineKeys.Add(kv.Key);
                        string b = null;
                        if (baseline == null || !baseline.TryGetValue(kv.Key, out b) || b != kv.Value || !disk.ContainsKey(kv.Key)) result[kv.Key] = kv.Value;
                        if (!order.Contains(kv.Key)) order.Add(kv.Key);
                    }
                    if (baseline != null)
                        foreach (var kv in baseline)
                            if (!mineKeys.Contains(kv.Key) && disk.TryGetValue(kv.Key, out var d) && d == kv.Value) result.Remove(kv.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    SafeWrite(path, string.Join("\r\n", order.Where(result.ContainsKey).Select(k => k + sep + result[k])) + "\r\n");
                    return result;
                }
                finally { if (got) try { mx.ReleaseMutex(); } catch (Exception) { } }
            }
        }

        // 🧩 spis programów Velivo (rejestr użytkownika HKCU\Software\Velivo): każdy program wpisuje się przy instalacji i przy każdym
        // starcie (nazwa, plik, wersja), deinstalator wykreśla tylko siebie; „Dane” = wspólny folder danych - znany przed instalacją i po deinstalacji
        const string SpisKey = @"Software\Velivo";
        public static void ZapiszSie(string pid, string nazwa, string exe, string wersja)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(SpisKey)) k.SetValue("Dane", NewRoot);
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(SpisKey + @"\Programy\" + pid))
                { k.SetValue("Nazwa", nazwa ?? pid); k.SetValue("Exe", exe ?? ""); k.SetValue("Wersja", wersja ?? ""); k.SetValue("Start", DateTime.Now.ToString("yyyy-MM-dd HH:mm")); }
            }
            catch (Exception) { }
        }
        // plik programu z innego wpisu spisu (null: nie ma albo plik zniknął - np. folder skasowany ręcznie; wtedy wpis usuwany)
        public static string ProgramZeSpisu(string pid)
        {
            try
            {
                string exe;
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SpisKey + @"\Programy\" + pid)) exe = k?.GetValue("Exe") as string;
                if (string.IsNullOrEmpty(exe)) return null;
                if (File.Exists(exe)) return exe;
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(SpisKey + @"\Programy\" + pid, false);
            }
            catch (Exception) { }
            return null;
        }
        // zainstalowane programy Velivo: identyfikator → nazwa (tylko te, których plik istnieje)
        public static System.Collections.Generic.Dictionary<string, string> Programy()
        {
            var d = new System.Collections.Generic.Dictionary<string, string>();
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SpisKey + @"\Programy"))
                    if (k != null)
                        foreach (var pid in k.GetSubKeyNames())
                            if (ProgramZeSpisu(pid) != null) using (var p = k.OpenSubKey(pid)) d[pid] = p?.GetValue("Nazwa") as string ?? pid;
            }
            catch (Exception) { }
            return d;
        }

        // Dopoki stary folder istnieje, dane sa w nim (przeniesienie sie nie udalo albo jeszcze nie bylo).
        public static string Root { get { return Directory.Exists(OldRoot) ? OldRoot : Directory.Exists(MidRoot) && !IsLink(MidRoot) && !HasData(NewRoot) ? MidRoot : SpisDane ?? NewRoot; } }
        static bool HasData(string d) { try { return Directory.Exists(d) && Directory.GetFileSystemEntries(d).Length > 0; } catch (Exception) { return false; } }

        public static void Migrate()
        {
            try
            {
                var from = Directory.Exists(OldRoot) ? OldRoot : Directory.Exists(MidRoot) && !IsLink(MidRoot) ? MidRoot : null;
                if (from == null) { EnsureLink(MidRoot, NewRoot); return; }   // już przeniesione - łącze w starym miejscu (stare wersje programów, ponowna instalacja starszego Velivo)
                _from = from;
                // inny program Velivo używa danych - nie ruszamy (przeniesie pierwszy, który wystartuje sam); inaczej zapisywałby dalej w starym miejscu
                int me = Process.GetCurrentProcess().Id;
                if (new[] { "Velivo", "VelivoOdtwarzacz", "VelivoPrzegladarka", "VelivoKomunikator" }.SelectMany(Process.GetProcessesByName).Any(p => p.Id != me)) { Log("inny program Velivo jest uruchomiony"); return; }
                if (Directory.Exists(NewRoot))
                {
                    // pusty folder (np. utworzony przez cos innego) nie blokuje przeniesienia; z danymi - zostawiamy oba
                    if (Directory.GetFileSystemEntries(NewRoot).Length > 0) { Log("folder " + NewRoot + " juz zawiera dane"); return; }
                    Directory.Delete(NewRoot);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(NewRoot));
                Directory.Move(from, NewRoot);
                FixPaths(from);
                EnsureLink(MidRoot, NewRoot);
            }
            // zostajemy na starym folderze - kolejna proba przy nastepnym starcie; przyczyna w bledy.log
            // (najczesciej: folder otwarty w Eksploratorze / Total Commanderze albo drugi proces Velivo)
            catch (Exception ex) { Log(ex.GetType().Name + ": " + ex.Message); }
        }

        // 🧩 łącze (junction) w starym miejscu → nowy folder: starsze wersje programów Velivo i ponownie zainstalowane starsze Velivo
        // czytają i zapisują w starym miejscu, a trafiają we wspólne dane; skasowanie folderu programu usuwa samo łącze, nie dane
        public static bool IsLink(string d) { try { return Directory.Exists(d) && (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0; } catch (Exception) { return false; } }
        public static void EnsureLink(string link, string target)
        {
            try
            {
                if (!OperatingSystem.IsWindows() || Directory.Exists(link) || !Directory.Exists(target)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(link));
                MakeJunction(link, Path.GetFullPath(target));   // wprost przez Windows - ukryty cmd.exe przy starcie (zwłaszcza w klawiaturze) Bitdefender brał za szkodliwe oprogramowanie
            }
            catch (Exception) { }
        }
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, byte[] inBuf, int inSize, IntPtr outBuf, int outSize, out int ret, IntPtr ov);
        // łącze katalogu (junction) = to samo co „mklink /J”: punkt ponownej analizy IO_REPARSE_TAG_MOUNT_POINT na pustym folderze
        static void MakeJunction(string link, string target)
        {
            Directory.CreateDirectory(link);
            var sub = System.Text.Encoding.Unicode.GetBytes(@"\??\" + target); var print = System.Text.Encoding.Unicode.GetBytes(target);
            var buf = new byte[8 + 8 + sub.Length + 2 + print.Length + 2];
            BitConverter.GetBytes(0xA0000003u).CopyTo(buf, 0);
            BitConverter.GetBytes((ushort)(buf.Length - 8)).CopyTo(buf, 4);
            BitConverter.GetBytes((ushort)0).CopyTo(buf, 8);
            BitConverter.GetBytes((ushort)sub.Length).CopyTo(buf, 10);
            BitConverter.GetBytes((ushort)(sub.Length + 2)).CopyTo(buf, 12);
            BitConverter.GetBytes((ushort)print.Length).CopyTo(buf, 14);
            sub.CopyTo(buf, 16); print.CopyTo(buf, 16 + sub.Length + 2);
            using (var h = CreateFileW(link, 0x40000000, 0, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero))
            {
                if (h.IsInvalid || !DeviceIoControl(h, 0x000900A4, buf, buf.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                { try { Directory.Delete(link); } catch (Exception) { } }   // nie wyszło - pusty folder nie może udawać danych
            }
        }

        static void Log(string why)
        {
            try { File.AppendAllText(Path.Combine(_from ?? OldRoot, "bledy.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  Nie przeniesiono folderu danych do " + NewRoot + ": " + why + Environment.NewLine); }
            catch (Exception) { }
        }

        // Pelne sciezki zapisane w ustawieniach i w profilu silnika przegladarki (np. dodatki) wskazuja stary folder.
        static string _from;
        static void FixPaths(string oldRoot)
        {
            var files = new System.Collections.Generic.List<string> { Path.Combine(NewRoot, "ustawienia.txt") };
            var profiles = Path.Combine(NewRoot, "Profiles");
            if (Directory.Exists(profiles))
                foreach (var d in Directory.GetDirectories(profiles)) files.Add(Path.Combine(d, "ustawienia.txt"));
            foreach (var root in new[] { NewRoot }.Concat(Directory.Exists(profiles) ? Directory.GetDirectories(profiles) : new string[0]))
                foreach (var name in new[] { "Preferences", "Secure Preferences" })
                    files.Add(Path.Combine(root, "Profil", "EBWebView", "Default", name));
            foreach (var file in files)
            {
                try
                {
                    if (!File.Exists(file)) continue;
                    var text = File.ReadAllText(file);
                    var fixedText = Replace(Replace(text, oldRoot, NewRoot), oldRoot.Replace("\\", "\\\\"), NewRoot.Replace("\\", "\\\\"));
                    if (fixedText != text) File.WriteAllText(file, fixedText);
                }
                catch (Exception) { }
            }
        }

        static string Replace(string text, string from, string to)
        {
            return Regex.Replace(text, Regex.Escape(from) + @"(?=[\\/""\r\n]|$)", to.Replace("$", "$$"), RegexOptions.IgnoreCase);
        }
    }
}
