using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Przegladarka
{
    // "Smieci" przegladarki: pamiec podreczna stron, skompilowany JavaScript i cache grafiki.
    // To NIE sa dane (logowania, ciasteczka, dane dodatkow i Service Worker) - te zostaja zawsze.
    public partial class MainWindow
    {
        // Program zawsze uzywa wlasnego podfolderu w folderze wybranym przez uzytkownika
        // i usuwa wylacznie jego zawartosc - nigdy innych plikow w wybranym folderze.
        const string JunkSubfolder = "Velivo-smieci";
        static readonly string[] OldJunkSubfolders = { "Przegladarka-smieci", "Tarcza-smieci" }; // nazwy sprzed zmian nazwy programu
        static string JunkFlagFile { get { return Path.Combine(DataDir, "wyczysc-smieci.flag"); } }
        static string ProfileDir { get { return Path.Combine(DataDir, "Profil", "EBWebView"); } }

        string CustomJunkDir
        {
            get { return string.IsNullOrWhiteSpace(_settings.CacheDir) ? null : Path.Combine(_settings.CacheDir, JunkSubfolder); }
        }

        // Argumenty silnika: wlasny folder pamieci podrecznej (cache stron + skompilowany JavaScript).
        string BrowserArguments()
        {
            var args = new List<string>();
            var env = Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS");
            if (!string.IsNullOrWhiteSpace(env)) args.Add(env.Trim());
            if (CustomJunkDir != null) args.Add("--disk-cache-dir=\"" + CustomJunkDir + "\"");
            if (DarkBrowserArgument != null) args.Add(DarkBrowserArgument); // tryb ciemny stron silnika
            _darkEngineAtStart = _settings.DarkPages;
            return string.Join(" ", args);
        }

        // Foldery smieci w profilu (cache grafiki silnik trzyma zawsze tutaj).
        static IEnumerable<string> ProfileJunkDirs()
        {
            yield return Path.Combine(ProfileDir, "Default", "Cache");
            yield return Path.Combine(ProfileDir, "Default", "Code Cache");
            yield return Path.Combine(ProfileDir, "Default", "GPUCache");
            yield return Path.Combine(ProfileDir, "GrShaderCache");
            yield return Path.Combine(ProfileDir, "ShaderCache");
        }

        IEnumerable<string> AllJunkDirs()
        {
            foreach (var d in ProfileJunkDirs()) yield return d;
            if (CustomJunkDir != null) yield return CustomJunkDir;
        }

        static long DirSize(string dir)
        {
            try
            {
                return Directory.Exists(dir)
                    ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => { try { return f.Length; } catch (IOException) { return 0L; } })
                    : 0;
            }
            catch (Exception) { return 0; }
        }

        long JunkSize() { return AllJunkDirs().Sum(d => DirSize(d)); }

        // Usuwa foldery smieci. Wolac tylko przed uruchomieniem silnika (pliki sa wtedy wolne).
        long DeleteJunkFolders()
        {
            long freed = 0;
            foreach (var d in AllJunkDirs())
            {
                if (!Directory.Exists(d)) continue;
                long size = DirSize(d);
                try { Directory.Delete(d, true); freed += size; }
                catch (Exception) { freed += size - DirSize(d); } // czesc plikow mogla byc zajeta
            }
            return freed;
        }

        // Przy starcie, przed silnikiem: sprzatanie wg ustawien lub prosby z "Wyczysc smieci teraz".
        // Przy wlasnym folderze cache stare "Cache" i "Code Cache" w profilu nie sa juz uzywane - tez znikaja.
        void CleanJunkAtStartup()
        {
            try
            {
                bool requested = File.Exists(JunkFlagFile);
                // podfoldery sprzed zmian nazwy programu - nasze wlasne, juz nieuzywane
                if (!string.IsNullOrWhiteSpace(_settings.CacheDir))
                    foreach (var name in OldJunkSubfolders)
                    {
                        var old = Path.Combine(_settings.CacheDir, name);
                        if (Directory.Exists(old)) try { Directory.Delete(old, true); } catch (Exception) { }
                    }
                if (_settings.CleanJunkOnStart || requested) DeleteJunkFolders();
                else if (CustomJunkDir != null)
                    foreach (var d in new[] { Path.Combine(ProfileDir, "Default", "Cache"), Path.Combine(ProfileDir, "Default", "Code Cache") })
                        if (Directory.Exists(d)) try { Directory.Delete(d, true); } catch (Exception) { }
                if (requested) File.Delete(JunkFlagFile);
            }
            catch (Exception) { }
        }

        // W trakcie pracy: silnik czysci cache stron sam; cache grafiki jest zajety - usuwamy go przy nastepnym starcie.
        async Task<long> CleanJunkNow()
        {
            long before = JunkSize();
            var profile = _profile ?? (Core != null ? Core.Profile : null);
            if (profile != null) await profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
            try { Directory.CreateDirectory(DataDir); File.WriteAllText(JunkFlagFile, DateTime.Now.ToString("s")); } catch (IOException) { }
            return Math.Max(0, before - JunkSize());
        }

        static string Mb(long bytes) { return (bytes / 1048576.0).ToString("0.0") + " MB"; }
    }
}
