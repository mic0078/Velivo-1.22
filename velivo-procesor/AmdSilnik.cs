using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace VelivoProcesor
{
    // 🔥 Silnik AMD (projekt: dokumentacja/velivo-procesor-silnik-amd-intel.md). Zasady:
    // - limity liczone od FABRYCZNYCH tego komputera (odczyt po każdym uruchomieniu Windows, zanim Velivo cokolwiek zmieni) - nigdy z góry wpisane waty;
    // - nigdy ponad fabryczne: moc chwilowa (Fast), średnia (Slow), prąd (TDC/EDC); moc długotrwała (STAPM) najwyżej do fabrycznej średniej (Slow);
    //   granica temperatury najwyżej fabryczna + 5 °C (i nie ponad 90 °C) - tylko w Turbo (pomiar NUCBOX 2026-11-25: 15 → 20 W = +12 % zegaru wielu rdzeni, wentylator stabilny);
    // - zegar i boost zostają dla procesora (CPPC) - silnik pilnuje tylko budżetu mocy i temperatury;
    // - telemetria co 1 s przez libryzenadj.dll (bez uruchamiania procesu), zapasowo ryzenadj.exe co 10 s;
    // - ponowne ustawienie po wybudzeniu i gdy BIOS / inny program nadpisze (najwyżej 3 razy w 10 min - bez walki);
    // - przy zamknięciu, wylogowaniu, deinstalacji: fabryczne wartości wszystkich zmienionych parametrów.
    sealed class AmdSilnik
    {
        // ---------- dostęp: biblioteka w procesie albo program ----------
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryEx(string path, IntPtr h, uint flags);
        [DllImport("libryzenadj.dll")] static extern IntPtr init_ryzenadj();
        [DllImport("libryzenadj.dll")] static extern int init_table(IntPtr ry);
        [DllImport("libryzenadj.dll")] static extern int refresh_table(IntPtr ry);
        [DllImport("libryzenadj.dll")] static extern float get_stapm_limit(IntPtr ry);
        [DllImport("libryzenadj.dll")] static extern float get_stapm_value(IntPtr ry);
        [DllImport("libryzenadj.dll")] static extern float get_fast_limit(IntPtr ry);
        [DllImport("libryzenadj.dll")] static extern float get_fast_value(IntPtr ry);
        [DllImport("libryzenadj.dll")] static extern float get_slow_limit(IntPtr ry);
        [DllImport("libryzenadj.dll")] static extern float get_slow_value(IntPtr ry);
        [DllImport("libryzenadj.dll")] static extern float get_vrm_current(IntPtr ry);
        [DllImport("libryzenadj.dll")] static extern float get_vrm_current_value(IntPtr ry);
        [DllImport("libryzenadj.dll")] static extern float get_vrmmax_current(IntPtr ry);
        [DllImport("libryzenadj.dll")] static extern float get_vrmmax_current_value(IntPtr ry);
        [DllImport("libryzenadj.dll")] static extern float get_tctl_temp(IntPtr ry);
        [DllImport("libryzenadj.dll")] static extern float get_tctl_temp_value(IntPtr ry);
        [DllImport("libryzenadj.dll")] static extern int set_stapm_limit(IntPtr ry, uint v);
        [DllImport("libryzenadj.dll")] static extern int set_fast_limit(IntPtr ry, uint v);
        [DllImport("libryzenadj.dll")] static extern int set_slow_limit(IntPtr ry, uint v);
        [DllImport("libryzenadj.dll")] static extern int set_tctl_temp(IntPtr ry, uint v);

        readonly string exe; IntPtr ry; readonly object zamek = new object();
        public bool Dziala { get; private set; }   // RyzenAdj odpowiada (administrator, obsługiwany procesor)
        public bool Biblioteka => ry != IntPtr.Zero;
        public AmdSilnik(string exePath) { exe = exePath; }

        public void Start()
        {
            lock (zamek)
            {
                Dziala = false;
                if (exe == null) return;
                try
                {
                    // zależności biblioteki (WinRing0x64.dll, inpoutx64.dll) leżą obok niej - ładowanie ze ścieżką folderu RyzenAdj
                    if (LoadLibraryEx(Path.Combine(Path.GetDirectoryName(exe), "libryzenadj.dll"), IntPtr.Zero, 0x8) != IntPtr.Zero)
                    {
                        ry = init_ryzenadj();
                        if (ry != IntPtr.Zero && (init_table(ry) != 0 || refresh_table(ry) != 0 || !(get_stapm_limit(ry) > 0))) ry = IntPtr.Zero;   // bez tabeli pomiarów - program
                    }
                }
                catch (Exception) { ry = IntPtr.Zero; }
                Dziala = Biblioteka || Odczyt() != null;
            }
            if (Dziala) Kalibruj();
        }

        // ---------- odczyt ----------
        public sealed class Stan { public double Stapm, StapmL, Fast, FastL, Slow, SlowL, Tdc, TdcL, Edc, EdcL, Temp, TempL; }
        public Stan Ost;   // ostatni odczyt
        DateTime odczytAt;
        Stan Odczyt()
        {
            lock (zamek)
            {
                try
                {
                    if (Biblioteka)
                    {
                        if (refresh_table(ry) != 0) return null;
                        return new Stan { Stapm = get_stapm_value(ry), StapmL = get_stapm_limit(ry), Fast = get_fast_value(ry), FastL = get_fast_limit(ry), Slow = get_slow_value(ry), SlowL = get_slow_limit(ry),
                            Tdc = get_vrm_current_value(ry), TdcL = get_vrm_current(ry), Edc = get_vrmmax_current_value(ry), EdcL = get_vrmmax_current(ry), Temp = get_tctl_temp_value(ry), TempL = get_tctl_temp(ry) };
                    }
                    var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    foreach (var l in Wywolaj("--info"))
                    {   // | STAPM LIMIT         |    15.000 | stapm-limit |
                        var a = l.Split('|'); if (a.Length < 3) continue;
                        if (double.TryParse(a[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) d[a[1].Trim()] = v;
                    }
                    double G(string k) => d.TryGetValue(k, out var v) ? v : -1;
                    if (!(G("STAPM LIMIT") > 0)) return null;
                    return new Stan { Stapm = G("STAPM VALUE"), StapmL = G("STAPM LIMIT"), Fast = G("PPT VALUE FAST"), FastL = G("PPT LIMIT FAST"), Slow = G("PPT VALUE SLOW"), SlowL = G("PPT LIMIT SLOW"),
                        Tdc = G("TDC VALUE VDD"), TdcL = G("TDC LIMIT VDD"), Edc = G("EDC VALUE VDD"), EdcL = G("EDC LIMIT VDD"), Temp = G("THM VALUE CORE"), TempL = G("THM LIMIT CORE") };
                }
                catch (Exception) { return null; }
            }
        }
        List<string> Wywolaj(string args)
        {
            var w = new List<string>();
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }))
                { var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(3000); w.AddRange(o.Split('\n')); }
            }
            catch (Exception) { }
            return w;
        }

        // co 1 s z pętli (biblioteka - odczyt pamięci; program - co 10 s); zwraca temperaturę albo -1
        public double Krok()
        {
            if (!Dziala) return -1;
            if (!Biblioteka && Ost != null && (DateTime.UtcNow - odczytAt).TotalSeconds < 10) return Ost.Temp;
            var s = Odczyt(); if (s == null) return -1;
            Ost = s; odczytAt = DateTime.UtcNow;
            Hist(s); Pilnuj(s);
            return s.Temp > 0 ? s.Temp : -1;
        }

        // ---------- co hamuje (słowami do okna) ----------
        // ogranicznik = wykorzystanie ≥ 97 % przez co najmniej 2 kolejne odczyty; temperatura - w odległości 1 °C od granicy
        string ogr = "", ogrBylo; int ogrN;
        void Hist(Stan s)
        {
            string teraz = s.TempL > 0 && s.Temp >= s.TempL - 1 ? "temperatura (" + s.TempL.ToString("0") + " °C)"
                : s.EdcL > 0 && s.Edc >= s.EdcL * 0.97 ? "prąd szczytowy (EDC)" : s.TdcL > 0 && s.Tdc >= s.TdcL * 0.97 ? "prąd ciągły (TDC)"
                : s.FastL > 0 && s.Fast >= s.FastL * 0.97 ? "moc chwilowa (" + s.FastL.ToString("0") + " W)" : s.SlowL > 0 && s.Slow >= s.SlowL * 0.97 ? "moc średnia (" + s.SlowL.ToString("0") + " W)"
                : s.StapmL > 0 && s.Stapm >= s.StapmL * 0.97 ? "moc długotrwała (" + s.StapmL.ToString("0") + " W)" : "";
            ogrN = teraz == ogrBylo ? ogrN + 1 : 1; ogrBylo = teraz;
            if (ogrN >= 2) ogr = teraz;
            tempSr = tempSr <= 0 ? s.Temp : tempSr + (s.Temp - tempSr) / (Biblioteka ? 60 : 6);   // średnia z ok. 1 min
        }
        public string Opis
        {
            get
            {
                var s = Ost; if (!Dziala) return ""; if (s == null) return "AMD: brak odczytu";
                return "AMD: " + Math.Max(s.Slow, 0).ToString("0.0") + " W (długotrwale " + Math.Max(s.Stapm, 0).ToString("0.0") + " z " + s.StapmL.ToString("0") + " W) · "
                    + (ogr.Length > 0 ? "hamuje: " + ogr : "procesor ma zapas") + (kalibracjaUwaga.Length > 0 ? " · " + kalibracjaUwaga : "") + (nadpisane ? " · ⚠ limity nadpisuje BIOS albo inny program" : "");
            }
        }

        // ---------- wartości fabryczne ----------
        // Odczyt po każdym uruchomieniu Windows (przy pierwszym starcie programu w tej sesji limity są jeszcze z BIOS) - zmiana w BIOS (np. tryb „Performance”) zostaje uwzględniona.
        // Zestawy starego Velivo (nadpisywały fabryczne, a przy zamknięciu zostawiały Turbo) - nie są brane za fabryczne.
        public double FStapm, FFast, FSlow, FTctl; public bool Skalibrowany; string kalibracjaUwaga = "";
        static string Plik => Path.Combine(Silnik.Dir, "amd-fabryczne.txt");
        [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
        static long Start_Windows() => (DateTime.UtcNow - TimeSpan.FromMilliseconds(GetTickCount64())).Ticks / TimeSpan.TicksPerMinute;
        void Kalibruj()
        {
            var plik = new Dictionary<string, string>();
            try { foreach (var l in File.ReadAllLines(Plik)) { int i = l.IndexOf('='); if (i > 0) plik[l.Substring(0, i)] = l.Substring(i + 1); } } catch (Exception) { }
            double P(string k) => plik.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : -1;
            bool taSesja = plik.TryGetValue("start", out var st) && long.TryParse(st, out var sl) && Math.Abs(sl - Start_Windows()) <= 2;
            bool zmienione = plik.TryGetValue("zmienione", out var zm) && zm == "1";
            var s = Odczyt();
            if (s != null && (!taSesja || !zmienione) && !StaryVelivo(s))
            {   // świeże limity z BIOS
                FStapm = s.StapmL; FFast = s.FastL; FSlow = s.SlowL; FTctl = s.TempL; Skalibrowany = true; Zapisz(false);
            }
            else if (P("stapm") > 0)
            {   // ta sama sesja Windows po zmianach Velivo - wartości z pliku
                FStapm = P("stapm"); FFast = P("fast"); FSlow = P("slow"); FTctl = P("tctl"); Skalibrowany = true; ustawione = zmienione;   // limity są jeszcze zmienione przez Velivo (np. po awarii programu) - przywracanie wie, że ma co cofnąć
            }
            else if (s != null) kalibracjaUwaga = "uruchom komputer ponownie, aby Velivo odczytało fabryczne limity (teraz są ustawione przez starszą wersję)";
            if (Skalibrowany && (FFast < FSlow || FSlow < FStapm * 0.5 || FTctl < 50 || FTctl > 105)) { Skalibrowany = false; kalibracjaUwaga = "nietypowe limity fabryczne – nie zmieniam mocy"; }
        }
        static bool StaryVelivo(Stan s)
        {
            bool R(double a, double b) => Math.Abs(a - b) < 0.6;
            return R(s.StapmL, 12) && R(s.FastL, 28) && R(s.SlowL, 15) && R(s.TempL, 75) || R(s.StapmL, 15) && R(s.FastL, 28) && R(s.SlowL, 22) && R(s.TempL, 80)
                || R(s.StapmL, 25) && R(s.FastL, 35) && R(s.SlowL, 30) && R(s.TempL, 88);
        }
        void Zapisz(bool zmienione)
        {
            try
            {
                var ci = CultureInfo.InvariantCulture; Directory.CreateDirectory(Silnik.Dir);
                File.WriteAllLines(Plik, new[] { "stapm=" + FStapm.ToString(ci), "fast=" + FFast.ToString(ci), "slow=" + FSlow.ToString(ci), "tctl=" + FTctl.ToString(ci), "start=" + Start_Windows(), "zmienione=" + (zmienione ? "1" : "0") });
            }
            catch (Exception) { }
        }
        public string Fabryczne => Skalibrowany ? "fabrycznie: długotrwała " + FStapm.ToString("0") + " W, średnia " + FSlow.ToString("0") + " W, chwilowa " + FFast.ToString("0") + " W, temperatura " + FTctl.ToString("0") + " °C" : kalibracjaUwaga;

        // ---------- poziomy ----------
        // Cichy: moc długotrwała fabryczna, bez szpilek (chwilowa = średnia = długotrwała), granica temperatury −5 °C - procesor sam łagodnie zwalnia;
        // Zrównoważony: długotrwała do fabrycznej średniej (krótkie i długie zadania tak samo szybkie), reszta fabryczna;
        // Turbo: jak Zrównoważony + granica temperatury +5 °C (nie ponad 90 °C) - wyższy zegar kilku rdzeni.
        public (double Stapm, double Fast, double Slow, double Tctl) Cel(Tryb t)
        {
            double dl = Math.Max(FStapm, FSlow - obnizka);
            if (t == Tryb.Cichy) return (FStapm, FStapm, FStapm, FTctl - 5);
            if (t == Tryb.Turbo) return (FSlow, FFast, FSlow, Math.Max(FTctl, Math.Min(FTctl + 5, 90)));
            return (dl, FFast, FSlow, FTctl);
        }
        // 🌡 regulator kultury pracy (Zrównoważony): średnia temperatura z minuty ≥ granica − 3 °C → moc długotrwała −1 W (do fabrycznej); ≤ granica − 10 °C → z powrotem +1 W; najwyżej raz na minutę
        double tempSr, obnizka; DateTime regAt; Tryb tryb; bool ustawione;
        void Reguluj()
        {
            if (!Skalibrowany || tryb != Tryb.Zrownowazony || (DateTime.UtcNow - regAt).TotalSeconds < 60 || tempSr <= 0) return;
            double o = obnizka;
            if (tempSr >= FTctl - 3) o = Math.Min(Math.Max(0, FSlow - FStapm), o + 1); else if (tempSr <= FTctl - 10) o = Math.Max(0, o - 1);
            if (o != obnizka) { obnizka = o; regAt = DateTime.UtcNow; Ustaw(tryb, true); }
        }

        // ---------- sterowanie ----------
        (double Stapm, double Fast, double Slow, double Tctl) zadane; int blad;
        public string Blad = "";
        public void Ustaw(Tryb t, bool wymus = false)
        {
            tryb = t; if (!Dziala || !Skalibrowany) return;
            var c = Cel(t); if (!wymus && ustawione && c == zadane) return;
            bool ok = Wyslij(c);
            if (ok) { zadane = c; ustawione = true; Zapisz(true); blad = 0; Blad = ""; }
            else if (++blad >= 3) { Dziala = false; Blad = "RyzenAdj przestał odpowiadać – moc AMD jak ustawił producent, reszta sterowania działa."; }
        }
        bool Wyslij((double Stapm, double Fast, double Slow, double Tctl) c)
        {
            uint W(double w) => (uint)Math.Round(w * 1000);
            lock (zamek)
            {
                try
                {
                    if (Biblioteka)
                        return set_stapm_limit(ry, W(c.Stapm)) == 0 & set_fast_limit(ry, W(c.Fast)) == 0 & set_slow_limit(ry, W(c.Slow)) == 0 & set_tctl_temp(ry, (uint)Math.Round(c.Tctl)) == 0;
                    var o = Wywolaj("--stapm-limit=" + W(c.Stapm) + " --fast-limit=" + W(c.Fast) + " --slow-limit=" + W(c.Slow) + " --tctl-temp=" + (uint)Math.Round(c.Tctl));
                    return o.Count > 0 && !o.Any(l => l.IndexOf("unable", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0);
                }
                catch (Exception) { return false; }
            }
        }

        // 🛡 nadpisanie przez BIOS / EC / inny program: odczytany limit ≠ zadany → ponowne ustawienie najwyżej 3 razy w 10 min, potem tylko ostrzeżenie (bez walki)
        readonly Queue<DateTime> ponowienia = new Queue<DateTime>(); bool nadpisane;
        void Pilnuj(Stan s)
        {
            Reguluj();
            if (!ustawione) return;
            bool R(double a, double b) => Math.Abs(a - b) < 0.6;
            bool rozne = !R(s.StapmL, zadane.Stapm) || !R(s.FastL, zadane.Fast) || !R(s.SlowL, zadane.Slow) || !R(s.TempL, zadane.Tctl);
            if (!rozne) { nadpisane = false; return; }
            while (ponowienia.Count > 0 && (DateTime.UtcNow - ponowienia.Peek()).TotalMinutes > 10) ponowienia.Dequeue();
            if (ponowienia.Count < 3) { ponowienia.Enqueue(DateTime.UtcNow); Ustaw(tryb, true); } else nadpisane = true;
        }
        // po wybudzeniu komputera BIOS zwykle wraca do swoich limitów - od razu od nowa
        public void PoWybudzeniu() { ponowienia.Clear(); nadpisane = false; if (ustawione) Ustaw(tryb, true); }

        // ↺ przywracanie: fabryczne wartości wszystkiego, co silnik zmienił (moc długotrwała, średnia, chwilowa, granica temperatury)
        public void Przywroc()
        {
            if (!Dziala || !Skalibrowany || !ustawione) return;
            if (Wyslij((FStapm, FFast, FSlow, FTctl))) { ustawione = false; Zapisz(false); }
        }
    }
}
