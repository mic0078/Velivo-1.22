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
    // - telemetria co 10 s przez ryzenadj.exe;
    // - ponowne ustawienie po wybudzeniu i gdy BIOS / inny program nadpisze (najwyżej 3 razy w 10 min - bez walki);
    // - przy zamknięciu, wylogowaniu, deinstalacji: fabryczne wartości wszystkich zmienionych parametrów.
    sealed class AmdSilnik
    {
        // ---------- dostęp: ryzenadj.exe (biblioteka w procesie wywracała program z uprawnieniami administratora - 2026-11-25, NUCBOX) ----------

        readonly string exe; readonly object zamek = new object();
        public bool Dziala { get; private set; }   // RyzenAdj odpowiada (administrator, obsługiwany procesor)
        public AmdSilnik(string exePath) { exe = exePath; }

        public void Start()
        {
            lock (zamek)
            {
                Dziala = false;
                if (exe == null) return;
                Dziala = Odczyt() != null;
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
                {   // najwyżej 5 s - zawieszony RyzenAdj (np. sterownik zablokowany) nie może zatrzymać pętli programu
                    var t = p.StandardOutput.ReadToEndAsync();
                    if (!t.Wait(5000)) { try { p.Kill(); } catch (Exception) { } return w; }
                    p.WaitForExit(1000); w.AddRange(t.Result.Split('\n'));
                }
            }
            catch (Exception) { }
            return w;
        }

        // co 1 s z pętli, odczyt co 10 s (osobny program); zwraca temperaturę albo -1
        public double Krok()
        {
            if (!Dziala) return -1;
            if (Ost != null && (DateTime.UtcNow - odczytAt).TotalSeconds < 10) return Ost.Temp;
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
            tempSr = tempSr <= 0 ? s.Temp : tempSr + (s.Temp - tempSr) / 6;   // średnia z ok. 1 min
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
        public double FStapm, FFast, FSlow, FTctl; public bool Skalibrowany;
        // 🔝 najwyższe limity, jakie BIOS tego komputera kiedykolwiek ustawił (np. tryb „Performance”) - sprawdzone przez producenta dla tego sprzętu;
        //    Zrównoważony i Turbo liczone od nich także przy BIOS „Balance” (RyzenAdj omija niższe ustawienie BIOS), Cichy i przywracanie - od bieżącego BIOS
        public double MStapm, MFast, MSlow, MTctl; string kalibracjaUwaga = "";
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
                FStapm = s.StapmL; FFast = s.FastL; FSlow = s.SlowL; FTctl = s.TempL; Skalibrowany = true;   // zapis niżej - razem z najwyższymi znanymi
            }
            else if (P("stapm") > 0)
            {   // ta sama sesja Windows po zmianach Velivo - wartości z pliku
                FStapm = P("stapm"); FFast = P("fast"); FSlow = P("slow"); FTctl = P("tctl"); Skalibrowany = true; ustawione = zmienione;   // limity są jeszcze zmienione przez Velivo (np. po awarii programu) - przywracanie wie, że ma co cofnąć
            }
            else if (s != null) kalibracjaUwaga = "uruchom komputer ponownie, aby Velivo odczytało fabryczne limity (teraz są ustawione przez starszą wersję)";
            if (Skalibrowany && (FFast < FSlow || FSlow < FStapm * 0.5 || FTctl < 50 || FTctl > 105)) { Skalibrowany = false; kalibracjaUwaga = "nietypowe limity fabryczne – nie zmieniam mocy"; }
            if (Skalibrowany)
            {
                double M(string k, double f) { double v = Math.Max(P("m" + k), P(k)); return v > 0 && v <= f * 2.5 ? Math.Max(v, f) : f; }   // najwyższe: zapisane „najwyższe”, poprzednio odczytane z BIOS (np. „Performance” przed przestawieniem na „Balance”) i bieżące; bzdurna wartość - bez niej
                MStapm = M("stapm", FStapm); MFast = M("fast", FFast); MSlow = M("slow", FSlow); MTctl = Math.Min(95, M("tctl", FTctl));
                if (MFast < MSlow || MSlow < MStapm) { MStapm = FStapm; MFast = FFast; MSlow = FSlow; MTctl = FTctl; }
                Zapisz(ustawione);
            }
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
                File.WriteAllLines(Plik, new[] { "stapm=" + FStapm.ToString(ci), "fast=" + FFast.ToString(ci), "slow=" + FSlow.ToString(ci), "tctl=" + FTctl.ToString(ci), "mstapm=" + MStapm.ToString(ci), "mfast=" + MFast.ToString(ci), "mslow=" + MSlow.ToString(ci), "mtctl=" + MTctl.ToString(ci), "start=" + Start_Windows(), "zmienione=" + (zmienione ? "1" : "0") });
            }
            catch (Exception) { }
        }
        public string Fabryczne => Skalibrowany ? "BIOS teraz: długotrwała " + FStapm.ToString("0") + " W, średnia " + FSlow.ToString("0") + " W, chwilowa " + FFast.ToString("0") + " W, temperatura " + FTctl.ToString("0") + " °C · najwyższe z BIOS tego komputera (Zrównoważony i Turbo): średnia " + MSlow.ToString("0") + " W, chwilowa " + MFast.ToString("0") + " W, temperatura " + MTctl.ToString("0") + " °C" : kalibracjaUwaga;

        // ---------- poziomy ----------
        // Cichy: moc długotrwała fabryczna, bez szpilek (chwilowa = średnia = długotrwała), granica temperatury −5 °C - procesor sam łagodnie zwalnia;
        // Zrównoważony: długotrwała do fabrycznej średniej (krótkie i długie zadania tak samo szybkie), reszta fabryczna;
        // Turbo: jak Zrównoważony + granica temperatury +5 °C (nie ponad 90 °C) - wyższy zegar kilku rdzeni.
        public (double Stapm, double Fast, double Slow, double Tctl) Cel(Tryb t)
        {
            double dl = Math.Max(FStapm, MSlow - obnizka);
            if (t == Tryb.Cichy) return (FStapm, FStapm, FStapm, FTctl - 5);
            if (t == Tryb.Turbo) return (MSlow, MFast, MSlow, Math.Max(MTctl, Math.Min(MTctl + 5, 90)));
            return (dl, MFast, MSlow, MTctl);
        }
        // 🌡 regulator kultury pracy (Zrównoważony): średnia temperatura z minuty ≥ granica − 3 °C → moc długotrwała −1 W (do fabrycznej); ≤ granica − 10 °C → z powrotem +1 W; najwyżej raz na minutę
        double tempSr, obnizka; DateTime regAt; Tryb tryb; bool ustawione;
        void Reguluj()
        {
            if (!Skalibrowany || tryb != Tryb.Zrownowazony || (DateTime.UtcNow - regAt).TotalSeconds < 60 || tempSr <= 0) return;
            double o = obnizka;
            if (tempSr >= MTctl - 3) o = Math.Min(Math.Max(0, MSlow - FStapm), o + 1); else if (tempSr <= MTctl - 10) o = Math.Max(0, o - 1);
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
            if (!ustawione || zadane.Stapm <= 0) return;   // po ponownym uruchomieniu programu: zadane dopiero przy pierwszej decyzji o trybie
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
