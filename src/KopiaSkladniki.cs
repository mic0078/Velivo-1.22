using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Velivo
{
    // 🧩 programy Velivo w kopii i porządkach - wspólne dla Velivo i Velivo Centrum (ten sam opis = ta sama kopia z obu okien).
    // Ścieżki względem folderu danych Velivo (małe litery, ukośnik '/'); Audio Balance i Monitor mają własne foldery obok danych.
    static class KopiaSkladniki
    {
        internal sealed class Czysc { public string Nazwa, Opis; public bool Domyslnie; public Func<string, bool> Pasuje; }
        internal sealed class Skladnik { public string Id, Nazwa, Opis, Proc; public string[] Uzywaja = new string[0], Prefiksy = new string[0]; public string Inny; public Czysc[] Sprzatanie = new Czysc[0]; }

        static readonly HashSet<string> CacheDirs = new HashSet<string> { "cache", "code cache", "gpucache", "grshadercache", "shadercache", "dawncache", "dawngraphitecache", "dawnwebgpucache", "service worker", "crashpad" };
        static Czysc PamiecStron(string profil) => new Czysc { Nazwa = "Pamięć podręczna stron", Opis = "cache, skrypty, grafika – zbuduje się sama", Domyslnie = true,
            Pasuje = r => r.StartsWith(profil + "/") && r.Split('/').Any(CacheDirs.Contains) };

        internal static readonly Skladnik[] Wszystkie =
        {
            new Skladnik { Id = "velivo", Nazwa = "Velivo (pełne)", Opis = "przeglądarka, filmy, komunikator", Proc = "Velivo", Prefiksy = new[] { "profil/" }, Sprzatanie = new[] { PamiecStron("profil") } },
            new Skladnik { Id = "przegladarka", Nazwa = "Velivo Przeglądarka", Opis = "strony, logowania, miniatury", Proc = "VelivoPrzegladarka", Prefiksy = new[] { "profil-velivoprzegladarka/", "miniatury stron/" },
                Sprzatanie = new[] { PamiecStron("profil-velivoprzegladarka"), new Czysc { Nazwa = "Miniatury stron", Opis = "szybki dostęp – odtworzą się", Domyslnie = true, Pasuje = r => r.StartsWith("miniatury stron/") } } },
            new Skladnik { Id = "odtwarzacz", Nazwa = "Velivo Odtwarzacz", Opis = "filmy, TV, lektor (także w pełnym Velivo)", Proc = "VelivoOdtwarzacz", Uzywaja = new[] { "Velivo", "VelivoSerwer" },
                Prefiksy = new[] { "profil-velivoodtwarzacz/", "filmy-info/", "iptv/", "lektor/", "supertonic/", "piper/", "xtts/" },
                Sprzatanie = new[] {
                    new Czysc { Nazwa = "Ikony kanałów TV", Opis = "pobiorą się na nowo", Pasuje = r => r.StartsWith("iptv/logo/") },
                    new Czysc { Nazwa = "Stary program TV", Opis = "wczyta się przy odświeżeniu listy", Pasuje = r => r.StartsWith("iptv/") && r.EndsWith(".epg") },
                    new Czysc { Nazwa = "Gotowe kwestie lektora", Opis = "lektor przeczyta je od nowa", Pasuje = r => r.StartsWith("lektor/") },
                    new Czysc { Nazwa = "Lektor XTTS", Opis = "Python i model, ok. 7 GB – pobierze się przy wyborze głosu", Pasuje = r => r.StartsWith("xtts/") && !r.EndsWith("/moj-glos.wav") } } },
            new Skladnik { Id = "komunikator", Nazwa = "Velivo Komunikator", Opis = "rozmowy, pliki, skrzynka serwera (także w pełnym Velivo)", Proc = "VelivoKomunikator", Uzywaja = new[] { "Velivo" }, Prefiksy = new[] { "komunikator", "skrzynka/" },
                Sprzatanie = new[] { PamiecStron("komunikator-webview") } },
            new Skladnik { Id = "klawiatura", Nazwa = "Velivo Klawiatura", Opis = "słownik i nauczone słowa", Proc = "VelivoKlawiatura", Uzywaja = new[] { "Velivo", "VelivoPrzegladarka", "VelivoOdtwarzacz" }, Prefiksy = new[] { "klawiatura", "profil-velivoklawiatura/" },
                Sprzatanie = new[] { PamiecStron("profil-velivoklawiatura") } },
            new Skladnik { Id = "ab", Nazwa = "Audio Balance", Opis = "ustawienia, wtyczki, kanały i filmy", Proc = "VelivoAudioBalance", Inny = "Audio Balance for Windows",
                Sprzatanie = new[] { new Czysc { Nazwa = "Dziennik", Opis = "zapisy pracy programu", Domyslnie = true, Pasuje = r => r.EndsWith(".log") } } },
            new Skladnik { Id = "monitor", Nazwa = "Velivo Monitor", Opis = "ustawienia", Proc = "VelivoMonitor", Inny = "Monitor" },
            new Skladnik { Id = "wspolne", Nazwa = "Wspólne", Opis = "zakładki, hasła, ustawienia, narzędzia", Uzywaja = new[] { "Velivo", "VelivoPrzegladarka", "VelivoOdtwarzacz", "VelivoKlawiatura", "VelivoKomunikator", "VelivoSerwer" },
                Sprzatanie = new[] {
                    new Czysc { Nazwa = "Dzienniki", Opis = "vlc.log, pobierania – bez dziennika błędów", Domyslnie = true, Pasuje = r => r.EndsWith(".log") && !r.EndsWith("bledy.log") },
                    new Czysc { Nazwa = "Kosz Velivo", Opis = "pliki odłożone do usunięcia", Domyslnie = true, Pasuje = r => r.StartsWith("velivo-smieci/") },
                    new Czysc { Nazwa = "Narzędzia", Opis = "ffmpeg, yt-dlp – pobiorą się przy użyciu", Pasuje = r => r.StartsWith("narzedzia/") } } },
        };

        // do którego programu należy plik w folderze danych (null - wspólne)
        internal static Skladnik Wlasciciel(string rel)
        {
            var r = rel.Replace('\\', '/').ToLowerInvariant();
            foreach (var s in Wszystkie) foreach (var p in s.Prefiksy) if (r.StartsWith(p)) return s;
            return null;
        }
        // procesy, które używają plików składnika (do zamknięcia na czas sprzątania)
        internal static IEnumerable<string> Procesy(Skladnik s) => (s.Proc == null ? s.Uzywaja : new[] { s.Proc }.Concat(s.Uzywaja)).Distinct();
        internal static string Lokal => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velivo");
        internal static string FolderInny(Skladnik s) => Path.Combine(Lokal, s.Inny);

        // ustawienia kopii w rejestrze - te same w Velivo (🧹) i w Velivo Centrum
        const string Klucz = @"Software\Velivo\Kopia";
        internal static string Get(string k) { try { using (var r = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Klucz)) return r?.GetValue(k) as string; } catch (Exception) { return null; } }
        internal static void Set(string k, string v) { try { using (var r = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Klucz)) r.SetValue(k, v ?? ""); } catch (Exception) { } }
        internal static bool WKopii(string id) => !(Get("bez") ?? "").Split(',').Contains(id);
        internal static void UstawWKopii(string id, bool on)
        {
            var l = (Get("bez") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Where(x => x != id).ToList();
            if (!on) l.Add(id);
            Set("bez", string.Join(",", l));
        }
        internal const string StanPlik = "kopia-stan.txt";   // postęp kopii zleconej przez Centrum: trwa|N, gotowe|ścieżka|N, blad|opis
    }
}
