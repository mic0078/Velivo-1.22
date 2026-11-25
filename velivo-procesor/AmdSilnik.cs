using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace VelivoProcesor
{
    // 🔥 Silnik AMD (projekt: dokumentacja/velivo-procesor-silnik-amd-intel.md). Zasady:
    // - limity liczone od FABRYCZNYCH tego komputera (odczyt po każdym uruchomieniu Windows, zanim Velivo cokolwiek zmieni) - nigdy z góry wpisane waty;
    // - nigdy ponad fabryczne: moc chwilowa (Fast), średnia (Slow), prąd (TDC/EDC); moc długotrwała (STAPM) najwyżej do fabrycznej średniej (Slow);
    //   granica temperatury najwyżej fabryczna + 5 °C (i nie ponad 90 °C) - tylko w Turbo (pomiar NUCBOX 2026-11-25: 15 → 20 W = +12 % zegaru wielu rdzeni, wentylator stabilny);
    // - zegar i boost zostają dla procesora (CPPC) - silnik pilnuje tylko budżetu mocy i temperatury;
    // - telemetria co 10 s (tabela pomiarów SMU przez PawnIO);
    // - ponowne ustawienie po wybudzeniu i gdy BIOS / inny program nadpisze (najwyżej 3 razy w 10 min - bez walki);
    // - przy zamknięciu, wylogowaniu, deinstalacji: fabryczne wartości wszystkich zmienionych parametrów.
    sealed class AmdSilnik
    {
        // ---------- dostęp: PawnIO (podpisany sterownik) + moduł RyzenSMU - jak G-Helper i Handheld Companion ----------
        // RyzenAdj używał WinRing0 - Windows Defender blokuje go od 2025 (VulnerableDriver:WinNT/Winring0, CVE-2020-14979; NUCBOX 2026-11-25:
        // „Unable to get os_access Obj” po każdym starcie Windows, wyjątek w Defenderze nie pomaga)

        readonly string dir; readonly object zamek = new object();
        public bool Dziala { get; private set; }   // PawnIO odpowiada (administrator, obsługiwany procesor)
        public AmdSilnik(string pawnDir) { dir = pawnDir; }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateFile(string n, uint acc, uint share, IntPtr sec, uint disp, uint fl, IntPtr tmpl);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(IntPtr dev, uint code, byte[] inB, uint inSz, byte[] outB, uint outSz, out uint ret, IntPtr ovl);
        const uint IoLoad = 0xA1B22084, IoExec = 0xA1B22104;
        IntPtr dev = IntPtr.Zero; int rodzina; uint mp1Cmd, mp1Rsp, mp1Arg;
        bool instalowany;   // instalacja PawnIO najwyżej raz na uruchomienie programu
        static readonly Mutex pci = new Mutex(false, @"Global\Access_PCI");   // wspólny z innymi programami PawnIO (zalecenie modułu RyzenSMU)

        ulong[] Wykonaj(string fn, ulong[] we, int wyN)
        {
            var inB = new byte[32 + we.Length * 8]; var nb = System.Text.Encoding.ASCII.GetBytes(fn); Array.Copy(nb, inB, Math.Min(31, nb.Length));
            for (int i = 0; i < we.Length; i++) BitConverter.GetBytes(we[i]).CopyTo(inB, 32 + i * 8);
            var outB = new byte[Math.Max(8, wyN * 8)];
            bool mam = false;
            try { mam = pci.WaitOne(2000); } catch (AbandonedMutexException) { mam = true; }
            try { if (!DeviceIoControl(dev, IoExec, inB, (uint)inB.Length, outB, (uint)(wyN * 8), out _, IntPtr.Zero)) return null; }
            finally { if (mam) pci.ReleaseMutex(); }
            var w = new ulong[wyN]; for (int i = 0; i < wyN; i++) w[i] = BitConverter.ToUInt64(outB, i * 8);
            return w;
        }
        uint Rej(uint a) { var w = Wykonaj("ioctl_read_smu_register", new ulong[] { a }, 1); if (w == null) throw new IOException("odczyt SMU"); return (uint)w[0]; }
        void Rej(uint a, uint v) { if (Wykonaj("ioctl_write_smu_register", new ulong[] { a, v }, 0) == null) throw new IOException("zapis SMU"); }
        // skrzynka MP1 (limity mocy) - uzgodnienie jak w RyzenAdj / G-Helper: czekaj na gotowość, wyzeruj odpowiedź, argument, polecenie, czekaj na wynik (1 = OK)
        bool Mp1(uint msg, uint arg)
        {
            var sw = Stopwatch.StartNew();
            while (Rej(mp1Rsp) == 0) if (sw.ElapsedMilliseconds > 200) return false;
            Rej(mp1Rsp, 0);
            for (uint i = 0; i < 6; i++) Rej(mp1Arg + i * 4, i == 0 ? arg : 0);
            Rej(mp1Cmd, msg);
            sw.Restart(); uint r;
            while ((r = Rej(mp1Rsp)) == 0) if (sw.ElapsedMilliseconds > 200) return false;
            return r == 1;
        }
        // skrzynka PSMU (Renoir/Cezanne: drugi zestaw limitów) - przez polecenie modułu
        bool Psmu(uint msg, uint arg) => Wykonaj("ioctl_send_smu_command", new ulong[] { msg, arg, 0, 0, 0, 0, 0 }, 6) != null;

        // instaluj = brak PawnIO → instalacja (tylko z pętli programu, w tle; nie przy deinstalacji)
        public void Start(bool instaluj = false)
        {
            lock (zamek)
            {
                Dziala = false;
                if (dir == null) return;
                if (dev == IntPtr.Zero && !Otworz(instaluj)) return;
                Dziala = Odczyt() != null;
            }
            if (Dziala) Kalibruj();
        }
        bool Otworz(bool instaluj)
        {
            UsunWinRing0();
            dev = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (dev == new IntPtr(-1))
            {
                int e = Marshal.GetLastWin32Error(); dev = IntPtr.Zero;
                if ((e == 2 || e == 3) && instaluj && !instalowany && Silnik.Admin()) { instalowany = true; Powod = Instaluj(); if (Powod.Length == 0) return Otworz(false); return false; }
                Powod = e == 5 ? "brak uprawnień administratora" : e == 2 || e == 3 ? "sterownik PawnIO nie jest zainstalowany" + (Powod.Length > 0 ? " – " + Powod : "") : "PawnIO: błąd " + e;
                return false;
            }
            try
            {
                var blob = File.ReadAllBytes(Path.Combine(dir, "RyzenSMU.bin"));
                if (!DeviceIoControl(dev, IoLoad, blob, (uint)blob.Length, null, 0, out _, IntPtr.Zero)) { Powod = "PawnIO: moduł RyzenSMU nie wczytał się – procesor AMD nieobsługiwany (błąd " + Marshal.GetLastWin32Error() + ")"; Zamknij(); return false; }
                var cn = Wykonaj("ioctl_get_code_name", new ulong[0], 1);
                rodzina = cn == null ? -1 : (int)cn[0];
                // limity mocy (STAPM, chwilowa, średnia, temperatura) mają procesory przenośne: Renoir/Lucienne/Cezanne, Rembrandt…HawkPoint, Strix/Krackan
                if (rodzina == 1 || rodzina == 13 || rodzina == 22) { mp1Cmd = 0x3B10528; mp1Rsp = 0x3B10564; mp1Arg = 0x3B10998; }
                else if (rodzina == 10 || rodzina == 12 || rodzina == 19 || rodzina == 23 || rodzina == 24 || rodzina == 25 || rodzina == 30) { mp1Cmd = 0x3B10528; mp1Rsp = 0x3B10578; mp1Arg = 0x3B10998; }
                else if (rodzina >= 31 && rodzina <= 34) { mp1Cmd = 0x3B10928; mp1Rsp = 0x3B10978; mp1Arg = 0x3B10998; }
                else { Powod = "ten procesor AMD (rodzina " + rodzina + ") nie ma limitów mocy STAPM – zostają ustawienia producenta"; Zamknij(); return false; }
                var t = Wykonaj("ioctl_resolve_pm_table", new ulong[0], 2);
                uint hi = t == null ? 0 : (uint)(t[0] >> 16);
                if (!(hi == 0x37 || hi == 0x3F || hi == 0x40 || hi == 0x45 || hi == 0x4C || hi == 0x5D || hi == 0x65)) { Powod = "nieznana tabela pomiarów procesora (0x" + (t == null ? 0 : t[0]).ToString("X") + ") – zostają ustawienia producenta"; Zamknij(); return false; }
                return true;
            }
            catch (Exception ex) { Powod = "PawnIO: " + ex.Message; Zamknij(); return false; }
        }
        void Zamknij() { if (dev != IntPtr.Zero) CloseHandle(dev); dev = IntPtr.Zero; }
        // PawnIO (pawnio.eu) - instalator z folderu programu, po cichu; gdy cicha instalacja nie wyjdzie - zwykłe okno instalatora (jedno „Zainstaluj”)
        string Instaluj()
        {
            var setup = Path.Combine(dir, "PawnIO_setup.exe");
            if (!File.Exists(setup)) return "brak instalatora PawnIO w folderze programu";
            int kod = -1;
            foreach (var arg in new[] { "-install -silent", "" })
                try
                {
                    using (var p = Process.Start(new ProcessStartInfo(setup, arg) { UseShellExecute = false, CreateNoWindow = arg.Length > 0 }))
                    { if (!p.WaitForExit(arg.Length > 0 ? 120000 : 600000)) return "instalator PawnIO nie skończył"; kod = p.ExitCode; }
                    var h = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (h != new IntPtr(-1)) { CloseHandle(h); return ""; }
                }
                catch (Exception ex) { return "instalator PawnIO: " + ex.Message; }
            return "instalacja PawnIO nie udała się (kod " + kod + ")";
        }
        // 🧹 sterowniki starego RyzenAdj z folderu Velivo (WinRing0 - zgłaszany przez Defender, inpoutx64) - zatrzymane i wyrejestrowane, folder usunięty
        static bool sprzatniete;
        static void UsunWinRing0()
        {
            if (sprzatniete || !Silnik.Admin()) return; sprzatniete = true;
            foreach (var u in new[] { "WinRing0_1_2_0", "inpoutx64" })
                try
                {
                    string img; using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + u)) img = k?.GetValue("ImagePath") as string;
                    if (img == null || img.IndexOf("Velivo Procesor", StringComparison.OrdinalIgnoreCase) < 0) continue;   // cudzy program - nie ruszam
                    foreach (var a in new[] { "stop " + u, "delete " + u })
                        using (var p = Process.Start(new ProcessStartInfo("sc.exe", a) { UseShellExecute = false, CreateNoWindow = true })) p.WaitForExit(10000);
                }
                catch (Exception) { }
            try { var stary = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RyzenAdj"); if (Directory.Exists(stary)) Directory.Delete(stary, true); } catch (Exception) { }
        }

        // ---------- odczyt ----------
        public sealed class Stan { public double Stapm, StapmL, Fast, FastL, Slow, SlowL, Tdc, TdcL, Edc, EdcL, Temp, TempL; }
        public Stan Ost;   // ostatni odczyt
        DateTime odczytAt;
        // tabela pomiarów SMU (wersje 0x37…0x65): limit i wartość na przemian - 0x00 STAPM, 0x08 chwilowa, 0x10 średnia, 0x20 TDC, 0x30 EDC, 0x40 temperatura
        Stan Odczyt()
        {
            lock (zamek)
            {
                try
                {
                    if (dev == IntPtr.Zero || Wykonaj("ioctl_update_pm_table", new ulong[0], 0) == null) { Powod = "PawnIO nie odpowiada"; return null; }
                    var q = Wykonaj("ioctl_read_pm_table", new ulong[0], 9);
                    if (q == null) { Powod = "PawnIO: brak tabeli pomiarów"; return null; }
                    double F(int i) => BitConverter.ToSingle(BitConverter.GetBytes(q[i / 2]), (i % 2) * 4);
                    if (!(F(0) > 0)) { Powod = "PawnIO: pusta tabela pomiarów"; return null; }
                    Powod = "";
                    return new Stan { StapmL = F(0), Stapm = F(1), FastL = F(2), Fast = F(3), SlowL = F(4), Slow = F(5), TdcL = F(8), Tdc = F(9), EdcL = F(12), Edc = F(13), TempL = F(16), Temp = F(17) };
                }
                catch (Exception ex) { Powod = "PawnIO: " + ex.Message; return null; }
            }
        }

        // co 1 s z pętli, odczyt co 10 s (osobny program); zwraca temperaturę albo -1
        // PawnIO nie odpowiedział (np. zaraz po starcie Windows) - ponowna próba co minutę
        DateTime probaAt = DateTime.MinValue; public string Powod = "";   // pierwsza próba od razu w pętli (nie w wątku okna - instalacja PawnIO może potrwać)
        public double Krok()
        {
            if (!Dziala) { if (dir == null || (DateTime.UtcNow - probaAt).TotalSeconds < 60) return -1; probaAt = DateTime.UtcNow; Start(true); if (!Dziala) return -1; }
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
        //    Zrównoważony i Turbo liczone od nich także przy BIOS „Balance” (limity przez SMU omijają niższe ustawienie BIOS), Cichy i przywracanie - od bieżącego BIOS
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
            else if (++blad >= 3) { Dziala = false; Blad = "PawnIO przestał odpowiadać – moc AMD jak ustawił producent, reszta sterowania działa."; }
        }
        bool Wyslij((double Stapm, double Fast, double Slow, double Tctl) c)
        {
            uint W(double w) => (uint)Math.Round(w * 1000);
            lock (zamek)
            {
                try
                {   // Renoir/Lucienne/Cezanne: limity także w drugiej skrzynce (PSMU), jak RyzenAdj i G-Helper
                    bool ok = Mp1(0x14, W(c.Stapm)) & Mp1(0x15, W(c.Fast)) & Mp1(0x16, W(c.Slow)) & Mp1(0x19, (uint)Math.Round(c.Tctl));
                    if (rodzina == 1 || rodzina == 13 || rodzina == 22) ok &= Psmu(0x31, W(c.Stapm)) & Psmu(0x32, W(c.Fast)) & Psmu(0x33, W(c.Slow)) & Psmu(0x34, W(c.Slow));
                    return ok;
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
