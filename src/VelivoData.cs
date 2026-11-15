using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Velivo
{
    // Folder danych uzytkownika: %LOCALAPPDATA%\Programs\Velivo\Dane - wszystko w folderze Velivo, ale osobno od plikow
    // programu (instalator nadpisuje tylko swoje pliki; dezinstalacja zostawia dane). Sciezka stala, niezalezna od tego,
    // skad uruchomiono Velivo.exe - inaczej powstawaly drugie, osobne dane.
    // Dawniej %LOCALAPPDATA%\Przegladarka - nazwa sprzed rozbudowy programu. Przy starcie przenosimy go
    // jedna zmiana nazwy (na tym samym dysku: cale albo nic). Gdy sie nie da (pliki zajete), zostajemy na starym
    // folderze i probujemy przy nastepnym uruchomieniu - dane nigdy nie sa kopiowane ani dzielone na dwa miejsca.
    static class VelivoData
    {
        static string Local { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
        public static string NewRoot { get { return Path.Combine(Local, "Programs", "Velivo", "Dane"); } }
        public static string OldRoot { get { return Path.Combine(Local, "Przegladarka"); } }

        // zapis „wszystko albo nic”: najpierw obok (.tmp), potem podmiana - wysypanie się / restart w trakcie nie zostawia obciętego pliku
        public static void SafeWrite(string path, byte[] data)
        {
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) { fs.Write(data, 0, data.Length); fs.Flush(true); }
            File.Move(tmp, path, true);
        }
        public static void SafeWrite(string path, string text) => SafeWrite(path, System.Text.Encoding.UTF8.GetBytes(text));

        // 🧩 kilka programów Velivo (pełne, Odtwarzacz, Przeglądarka) na jednym folderze danych: zapis pliku „klucz<sep>wartość”
        // bez kasowania cudzych zmian. Na dysk trafia aktualny plik + tylko to, co TEN program zmienił od ostatniego odczytu
        // (baseline); usunięty u nas klucz znika tylko, gdy nikt go w międzyczasie nie zmienił. Zwraca to, co zapisano (nowy baseline).
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

        // Dopoki stary folder istnieje, dane sa w nim (przeniesienie sie nie udalo albo jeszcze nie bylo).
        public static string Root { get { return Directory.Exists(OldRoot) ? OldRoot : NewRoot; } }

        public static void Migrate()
        {
            try
            {
                if (!Directory.Exists(OldRoot)) return;
                // inne okno Velivo (albo klawiatura) uzywa danych - nie ruszamy
                if (Process.GetProcessesByName("Velivo").Length > 1) { Log("inne okno Velivo jest uruchomione"); return; }
                if (Directory.Exists(NewRoot))
                {
                    // pusty folder (np. utworzony przez cos innego) nie blokuje przeniesienia; z danymi - zostawiamy oba
                    if (Directory.GetFileSystemEntries(NewRoot).Length > 0) { Log("folder " + NewRoot + " juz zawiera dane"); return; }
                    Directory.Delete(NewRoot);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(NewRoot));
                Directory.Move(OldRoot, NewRoot);
                FixPaths();
            }
            // zostajemy na starym folderze - kolejna proba przy nastepnym starcie; przyczyna w bledy.log
            // (najczesciej: folder otwarty w Eksploratorze / Total Commanderze albo drugi proces Velivo)
            catch (Exception ex) { Log(ex.GetType().Name + ": " + ex.Message); }
        }

        static void Log(string why)
        {
            try { File.AppendAllText(Path.Combine(OldRoot, "bledy.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  Nie przeniesiono folderu danych do " + NewRoot + ": " + why + Environment.NewLine); }
            catch (Exception) { }
        }

        // Pelne sciezki zapisane w ustawieniach i w profilu silnika przegladarki (np. dodatki) wskazuja stary folder.
        static void FixPaths()
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
                    var fixedText = Replace(Replace(text, OldRoot, NewRoot), OldRoot.Replace("\\", "\\\\"), NewRoot.Replace("\\", "\\\\"));
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
