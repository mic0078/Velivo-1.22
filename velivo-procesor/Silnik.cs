using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace VelivoProcesor
{
    // 🧠 Velivo Procesor - silnik: pomiar (procesor, karta graficzna, temperatura, dysk, program na wierzchu), decyzja z nauką i sterowanie.
    // Ta sama hierarchia co CPU Manager v40 (temperatura → wczytywanie → obciążenie / gra na karcie → znany program → spoczynek),
    // w jednym przejrzystym mechanizmie: szybko w górę (bez przycięć), powoli w dół (bez skakania trybów), nauka programów i pór dnia,
    // przewidywanie temperatury z jej wzrostu, samodostrajanie progów. Sterowanie: plan zasilania Windows (Intel i AMD) + RyzenAdj (moc AMD).
    enum Tryb { Cichy = 0, Zrownowazony = 1, Turbo = 2 }

    sealed class Pomiar
    {
        public double Cpu, Gpu = -1, Temp = -1, Dysk, DyskMBs, DyskKol, Ghz; public string Program = ""; public int Pid; public double ProgCpu = -1; public bool Bateria;
    }

    sealed class Silnik
    {
        // ---------- ustawienia ----------
        internal static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velivo", "Procesor");
        static readonly string SetFile = Path.Combine(Dir, "ustawienia.txt"), NaukaFile = Path.Combine(Dir, "nauka.txt");
        readonly Dictionary<string, string> set = new Dictionary<string, string>();
        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
        public string Get(string k, string d = null) { lock (set) return set.TryGetValue(k, out var v) ? v : d; }
        public double GetD(string k, double d) => double.TryParse(Get(k), System.Globalization.NumberStyles.Float, Inv, out var v) ? v : d;
        public void Put(string k, object v)
        {
            lock (set)
            {
                set[k] = Convert.ToString(v, Inv);
                try { Directory.CreateDirectory(Dir); File.WriteAllLines(SetFile + ".tmp", set.Select(x => x.Key + "=" + x.Value)); if (File.Exists(SetFile)) File.Replace(SetFile + ".tmp", SetFile, null); else File.Move(SetFile + ".tmp", SetFile); } catch (Exception) { }
            }
        }
        public bool Auto => Get("tryb", "auto") == "auto";
        public double CichyGhz => GetD("cichy-ghz", 1.4);   // najniższy zegar w trybie Cichym (może być poniżej bazowego - jak v40)
        public double TempLimit => GetD("temp-limit", 90);

        public readonly string ProcesorNazwa; public readonly bool Amd; public readonly double BazaMhz;
        public string Ryzen;   // ścieżka ryzenadj.exe (AMD) albo null
        public bool RyzenDziala;   // RyzenAdj odpowiada (uprawnienia administratora i obsługiwany procesor)

        public Silnik()
        {
            try { foreach (var l in File.ReadAllLines(SetFile)) { int i = l.IndexOf('='); if (i > 0) set[l.Substring(0, i)] = l.Substring(i + 1); } } catch (Exception) { }
            try { using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")) { ProcesorNazwa = (k?.GetValue("ProcessorNameString") as string ?? "").Trim(); BazaMhz = Convert.ToDouble(k?.GetValue("~MHz") ?? 0); } } catch (Exception) { ProcesorNazwa = "?"; }
            Amd = ProcesorNazwa.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0;
            var rp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RyzenAdj", "ryzenadj.exe");
            if (Amd && File.Exists(rp)) Ryzen = rp;
            WczytajNauke();
        }

        // ---------- pomiar ----------
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out PowerStatus s);
        [StructLayout(LayoutKind.Sequential)] struct PowerStatus { public byte AC, Battery, Percent, Saver; public int Life, Full; }
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string src, IntPtr user, out IntPtr q);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr q, string path, IntPtr user, out IntPtr c);
        [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr q);
        [DllImport("pdh.dll")] static extern int PdhGetFormattedCounterValue(IntPtr c, uint fmt, IntPtr type, out PdhValue v);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArray(IntPtr c, uint fmt, ref int size, out int count, IntPtr buf);
        [StructLayout(LayoutKind.Sequential)] struct PdhValue { public uint Status; public uint Pad; public double Value; }

        long lIdle, lKer, lUsr; IntPtr pdhQ, cPerf, cDysk, cDyskB, cDyskQ, cGpu, cTz; double tzMnoznik = 1; int tempTick; double tempOst = -1;

        void InitPdh()
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out pdhQ) != 0) { pdhQ = IntPtr.Zero; return; }
            PdhAddEnglishCounter(pdhQ, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out cPerf);
            PdhAddEnglishCounter(pdhQ, @"\PhysicalDisk(_Total)\% Idle Time", IntPtr.Zero, out cDysk);
            // 💽 dysk: prędkość i kolejka operacji („% zajętości” na dyskach NVMe bywa fałszywe - 100 % przy małym ruchu)
            PdhAddEnglishCounter(pdhQ, @"\PhysicalDisk(_Total)\Disk Bytes/sec", IntPtr.Zero, out cDyskB);
            PdhAddEnglishCounter(pdhQ, @"\PhysicalDisk(_Total)\Current Disk Queue Length", IntPtr.Zero, out cDyskQ);
            if (PdhAddEnglishCounter(pdhQ, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out cGpu) != 0) cGpu = IntPtr.Zero;
            if (PdhAddEnglishCounter(pdhQ, @"\Thermal Zone Information(*)\High Precision Temperature", IntPtr.Zero, out cTz) != 0) { tzMnoznik = 10; if (PdhAddEnglishCounter(pdhQ, @"\Thermal Zone Information(*)\Temperature", IntPtr.Zero, out cTz) != 0) cTz = IntPtr.Zero; }   // strefy ACPI (dziesiąte części K) - bez uprawnień   // karta: suma silników 3D wszystkich programów
            PdhCollectQueryData(pdhQ);
        }
        double Licznik(IntPtr c) => c != IntPtr.Zero && PdhGetFormattedCounterValue(c, 0x200, IntPtr.Zero, out var v) == 0 ? v.Value : -1;
        double Gpu() => Tablica(cGpu, true);
        // licznik z wieloma wystąpieniami: suma (karta - silniki 3D) albo największa wartość (strefy temperatury)
        double Tablica(IntPtr c, bool suma)
        {
            if (c == IntPtr.Zero) return -1;
            int size = 0; PdhGetFormattedCounterArray(c, 0x200, ref size, out var n, IntPtr.Zero);
            if (size <= 0) return -1;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (PdhGetFormattedCounterArray(c, 0x200, ref size, out n, buf) != 0) return -1;
                double sum = 0, mx = -1; int step = IntPtr.Size + 16;   // PDH_FMT_COUNTERVALUE_ITEM: wskaźnik nazwy + {status, (wyrównanie), double}
                for (int i = 0; i < n; i++) { double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buf, i * step + IntPtr.Size + 8)); sum += v; mx = Math.Max(mx, v); }
                return suma ? Math.Min(100, sum) : mx;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public Pomiar Zmierz()
        {
            var p = new Pomiar();
            if (pdhQ == IntPtr.Zero) InitPdh();
            if (GetSystemTimes(out var idle, out var ker, out var usr))
            {
                long di = idle - lIdle, dt = (ker - lKer) + (usr - lUsr);
                if (lKer != 0 && dt > 0) p.Cpu = Math.Max(0, Math.Min(100, 100.0 * (dt - di) / dt));
                lIdle = idle; lKer = ker; lUsr = usr;
            }
            if (pdhQ != IntPtr.Zero && PdhCollectQueryData(pdhQ) == 0)
            {
                double perf = Licznik(cPerf); if (perf > 0 && BazaMhz > 0) p.Ghz = BazaMhz * perf / 100 / 1000;
                double di2 = Licznik(cDysk); p.Dysk = di2 >= 0 ? Math.Max(0, 100 - di2) : 0;
                p.DyskMBs = Math.Max(0, Licznik(cDyskB)) / 1e6; p.DyskKol = Math.Max(0, Licznik(cDyskQ));
                p.Gpu = Gpu();
            }
            if (++tempTick % 3 == 1) tempOst = Temperatura();   // co 3 s (odczyt bywa kosztowny)
            p.Temp = tempOst;
            try
            {
                var fw = GetForegroundWindow(); GetWindowThreadProcessId(fw, out var pid);
                using (var pr = Process.GetProcessById((int)pid)) p.Program = pr.ProcessName;
                if (p.Program == "ApplicationFrameHost")   // aplikacje ze Sklepu: ramka okna to inny proces niż sam program - szukam okna programu w środku
                {
                    uint host = pid, wl = 0;
                    EnumChildWindows(fw, (h, l) => { GetWindowThreadProcessId(h, out var c); if (c != host) { wl = c; return false; } return true; }, IntPtr.Zero);
                    if (wl != 0) { pid = wl; using (var pr = Process.GetProcessById((int)wl)) p.Program = pr.ProcessName; }
                }
                p.Pid = (int)pid;
            }
            catch (Exception) { }
            try { p.Bateria = GetSystemPowerStatus(out var ps) && ps.AC == 0; } catch (Exception) { }
            return p;
        }

        // temperatura: AMD - RyzenAdj (Tctl), inaczej LibreHardwareMonitor / OpenHardwareMonitor (gdy działa), zapasowo strefa ACPI; -1 = nieznana
        public string TempZrodlo = "";   // skąd temperatura (słowami do okna)
        double Temperatura()
        {
            TempZrodlo = "";
            if (RyzenDziala)
                foreach (var l in RyzenWywolaj("--info"))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(l, @"THM VALUE( CORE)?\s*\|\s*([\d.]+)");
                    if (m.Success && double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float, Inv, out var t) && t > 0) { TempZrodlo = "z procesora (RyzenAdj)"; return t; }
                }
            foreach (var ns in new[] { @"root\LibreHardwareMonitor", @"root\OpenHardwareMonitor" })
                try
                {
                    using (var s = new System.Management.ManagementObjectSearcher(ns, "SELECT Name, Value FROM Sensor WHERE SensorType='Temperature'"))
                    {
                        double best = -1;
                        foreach (System.Management.ManagementObject o in s.Get())
                        {
                            var n = o["Name"] as string ?? ""; var v = Convert.ToDouble(o["Value"]);
                            if (n.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Tctl", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) >= 0) best = Math.Max(best, v);
                        }
                        if (best > 0) { TempZrodlo = "z procesora (LibreHardwareMonitor)"; return best; }
                    }
                }
                catch (Exception) { }
            try
            {
                using (var s = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                    foreach (System.Management.ManagementObject o in s.Get()) { double t = Convert.ToDouble(o["CurrentTemperature"]) / 10 - 273.15; if (t > 0 && t < 120) { TempZrodlo = "czujnik płyty (ACPI)"; return t; } }
            }
            catch (Exception) { }
            double tz = Tablica(cTz, false) * tzMnoznik / 10 - 273.15;   // bez uprawnień: strefy ACPI z liczników Windows
            if (tz > 10 && tz < 120) { TempZrodlo = "czujnik płyty (ACPI)"; return tz; }
            return -1;
        }

        // ---------- nauka ----------
        // program na wierzchu → średnie obciążenie, gdy był na wierzchu (próbki); pora dnia (0–23) → średnie obciążenie
        readonly Dictionary<string, (double Cpu, double Gpu, int N, double Szczyt)> programy = new Dictionary<string, (double, double, int, double)>(StringComparer.OrdinalIgnoreCase);
        // nie programy użytkownika: pulpit, pasek, menu Start, ekran blokady i sam Velivo Procesor (jego okno na wierzchu to nie praca)
        static readonly HashSet<string> Powloka = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Idle", "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "LockApp", "TextInputHost", "ApplicationFrameHost", "VelivoProcesor", "VelivoCentrum", "dwm", "csrss" };
        readonly double[] godziny = new double[24]; readonly int[] godzN = new int[24];
        public readonly Dictionary<string, Tryb> Reczne = new Dictionary<string, Tryb>(StringComparer.OrdinalIgnoreCase);   // „zawsze ten tryb” dla programu
        public readonly Dictionary<string, int> Priorytet = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // 1 Wysoki, -1 Niski (brak wpisu = bez zmian)
        public readonly HashSet<string> Studio = new HashSet<string>(StringComparer.OrdinalIgnoreCase), BezPb = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // priorytet, który program dostaje: wybrany, a w trybie Studio domyślnie Wysoki
        public int PriorytetDla(string n) { lock (Studio) return Priorytet.TryGetValue(n, out var v) ? v : Studio.Contains(n) ? 1 : 0; }
        public bool BezPbDla(string n) { lock (Studio) return BezPb.Contains(n); }
        public void Zapomnij(string n) { lock (programy) programy.Remove(n); lock (Studio) { Reczne.Remove(n); Priorytet.Remove(n); Studio.Remove(n); BezPb.Remove(n); } ZapiszNauke(); }
        public double ProgWysoki = 70, ProgNiski = 20;   // samodostrajane
        DateTime naukaZapis = DateTime.UtcNow;

        void WczytajNauke()
        {
            try
            {
                foreach (var l in File.ReadAllLines(NaukaFile))
                {
                    var a = l.Split('\t');
                    if (a[0] == "P" && a.Length >= 5 && !Powloka.Contains(a[1])) programy[a[1]] = (double.Parse(a[2], Inv), double.Parse(a[3], Inv), int.Parse(a[4]), a.Length >= 6 ? double.Parse(a[5], Inv) : double.Parse(a[2], Inv));
                    else if (a[0] == "H" && a.Length >= 4) { int h = int.Parse(a[1]); godziny[h] = double.Parse(a[2], Inv); godzN[h] = int.Parse(a[3]); }
                    else if (a[0] == "R" && a.Length >= 3) Reczne[a[1]] = (Tryb)int.Parse(a[2]);
                    else if (a[0] == "Q" && a.Length >= 3) Priorytet[a[1]] = int.Parse(a[2]);
                    else if (a[0] == "S" && a.Length >= 2) Studio.Add(a[1]);
                    else if (a[0] == "B" && a.Length >= 2) BezPb.Add(a[1]);
                    else if (a[0] == "T" && a.Length >= 3) { ProgWysoki = double.Parse(a[1], Inv); ProgNiski = double.Parse(a[2], Inv); }
                }
            }
            catch (Exception) { }
        }
        public void ZapiszNauke()
        {
            try
            {
                var l = new List<string>();
                lock (programy) foreach (var p in programy) l.Add("P\t" + p.Key + "\t" + p.Value.Cpu.ToString("0.0", Inv) + "\t" + p.Value.Gpu.ToString("0.0", Inv) + "\t" + p.Value.N + "\t" + p.Value.Szczyt.ToString("0.0", Inv));
                for (int h = 0; h < 24; h++) if (godzN[h] > 0) l.Add("H\t" + h + "\t" + godziny[h].ToString("0.0", Inv) + "\t" + godzN[h]);
                lock (Studio)
                {
                    foreach (var r in Reczne) l.Add("R\t" + r.Key + "\t" + (int)r.Value);
                    foreach (var r in Priorytet) l.Add("Q\t" + r.Key + "\t" + r.Value);
                    foreach (var r in Studio) l.Add("S\t" + r);
                    foreach (var r in BezPb) l.Add("B\t" + r);
                }
                l.Add("T\t" + ProgWysoki.ToString("0.0", Inv) + "\t" + ProgNiski.ToString("0.0", Inv));
                Directory.CreateDirectory(Dir); File.WriteAllLines(NaukaFile + ".tmp", l); if (File.Exists(NaukaFile)) File.Replace(NaukaFile + ".tmp", NaukaFile, null); else File.Move(NaukaFile + ".tmp", NaukaFile);
            }
            catch (Exception) { }
        }
        // lista programów do okna: wyuczone (po ok. 30 s pracy) i wszystkie z własnym ustawieniem
        public sealed class ProgInfo
        {
            public string Prog { get; set; } public string TrybOpis { get; set; } public double Sr { get; set; } public double Szczyt { get; set; } public double Karta { get; set; } public int N { get; set; }
            public int Prio { get; set; } public bool StudioOn { get; set; } public bool BezPbOn { get; set; } public bool Zawsze;
            public string SrT => Sr.ToString("0") + " %"; public string SzczytT => Szczyt.ToString("0") + " %"; public string KartaT => Karta.ToString("0") + " %"; public string CzasT => Czas(N);
            public string PrioT => Prio > 0 ? "⬆ Wysoki" : Prio < 0 ? "⬇ Niski" : "–"; public string StudioT => StudioOn ? "🎚 tak" : "–"; public string PbT => BezPbOn ? "nie" : "tak";
        }
        public List<ProgInfo> ProgramyDane()
        {
            var w = new List<ProgInfo>();
            lock (programy) lock (Studio)
            {
                var nazwy = new HashSet<string>(programy.Where(p => p.Value.N >= 10).Select(p => p.Key), StringComparer.OrdinalIgnoreCase);   // na liście po 10 s pracy (decyzje z nauki po 30 s)
                nazwy.UnionWith(Reczne.Keys); nazwy.UnionWith(Priorytet.Keys); nazwy.UnionWith(Studio); nazwy.UnionWith(BezPb);
                foreach (var n in nazwy)
                {
                    programy.TryGetValue(n, out var v); bool z = Reczne.TryGetValue(n, out var rt);
                    w.Add(new ProgInfo { Prog = n, Zawsze = z, TrybOpis = z ? Nazwa(rt) + " (zawsze)" : v.N > 0 ? Nazwa(Wyuczony(v)) + (v.Gpu > 70 && v.Cpu < 50 ? " (karta)" : "") : "–",
                        Sr = v.Cpu, Szczyt = v.Szczyt, Karta = v.Gpu, N = v.N, Prio = Priorytet.TryGetValue(n, out var q) ? q : Studio.Contains(n) ? 1 : 0, StudioOn = Studio.Contains(n), BezPbOn = BezPb.Contains(n) });
                }
            }
            return w;
        }
        // wymagania programu: średnia i szczyt (program, który często skacze do pełnej mocy - kompilacja, eksport - dostaje Turbo zawczasu, bez przycięć)
        static Tryb Wyuczony((double Cpu, double Gpu, int N, double Szczyt) v) => v.Gpu > 70 && v.Cpu < 50 ? Tryb.Zrownowazony : v.Cpu > 55 || v.Szczyt > 85 && v.Cpu > 30 ? Tryb.Turbo : v.Cpu < 12 && v.Szczyt < 40 ? Tryb.Cichy : Tryb.Zrownowazony;
        static string Czas(int n) => n >= 3600 ? (n / 3600) + " h" : Math.Max(1, n / 60) + " min";
        public static string Nazwa(Tryb t) => t == Tryb.Cichy ? "Cichy" : t == Tryb.Turbo ? "Turbo" : "Zrównoważony";

        void Ucz(Pomiar p)
        {
            if (!string.IsNullOrEmpty(p.Program) && !Powloka.Contains(p.Program) && p.Program.IndexOf("setup", StringComparison.OrdinalIgnoreCase) < 0 && !p.Program.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))   // instalatory (jednorazowe) - bez nauki
                lock (programy)
                {
                    programy.TryGetValue(p.Program, out var v); int n = Math.Min(v.N + 1, 600);   // średnia z ok. 10 min pracy (starsze wygasa)
                    double c = p.ProgCpu >= 0 ? p.ProgCpu : p.Cpu;   // obciążenie SAMEGO programu (% jednego rdzenia, z ProBalance); nieznane - całego komputera jak dotąd
                    double sz = c > v.Szczyt ? v.Szczyt + (c - v.Szczyt) * 0.2 : v.Szczyt - (v.Szczyt - c) / 300;   // szczyt: szybko w górę, powoli w dół (ok. 5 min)
                    programy[p.Program] = (v.Cpu + (c - v.Cpu) / n, v.Gpu + (Math.Max(0, p.Gpu) - v.Gpu) / n, v.N + 1, sz);
                }
            int h = DateTime.Now.Hour; int hn = Math.Min(++godzN[h], 3600); godziny[h] += (p.Cpu - godziny[h]) / hn;
            if ((DateTime.UtcNow - naukaZapis).TotalMinutes > 5) { naukaZapis = DateTime.UtcNow; ZapiszNauke(); }
        }

        // ---------- decyzja ----------
        // sekundy od ostatniego ruchu myszy / klawisza - ktoś przy komputerze = ekran na pewno nie „wyłączony” (zabezpieczenie, gdy Windows nie przyśle „włączony”)
        [StructLayout(LayoutKind.Sequential)] struct LastInput { public uint Size, Time; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInput li);
        static double BezRuchuS() { var li = new LastInput { Size = 8 }; return GetLastInputInfo(ref li) ? unchecked((uint)Environment.TickCount - li.Time) / 1000.0 : 0; }
        public Tryb Obecny = Tryb.Zrownowazony; public string Dlaczego = ""; public volatile bool EkranWyl;
        readonly Queue<double> tempHist = new Queue<double>(); DateTime ostZmiana = DateTime.MinValue; int wysokoN, niskoN, gpuWyjscieN; bool gpuBound;
        int przycieciaN, nadmiarN;   // do samodostrajania progów
        int dyskN; DateTime dyskDo;

        public Tryb Decyduj(Pomiar p)
        {
            Ucz(p);
            StudioCzynne(p);
            if (Get("tryb") == "windows") { Dlaczego = "Steruje Windows (ustawienia domyślne) – Velivo Procesor tylko pokazuje pomiary."; return Obecny; }
            if (!Auto) { var r = Get("tryb") == "cichy" ? Tryb.Cichy : Get("tryb") == "turbo" ? Tryb.Turbo : Tryb.Zrownowazony; Dlaczego = "Wybrany ręcznie."; return Ustal(r); }
            if (p.Temp > 0) { tempHist.Enqueue(p.Temp); while (tempHist.Count > 10) tempHist.Dequeue(); }
            double trend = tempHist.Count >= 5 ? (tempHist.Last() - tempHist.First()) / (tempHist.Count - 1) : 0;   // °C na sekundę
            wysokoN = p.Cpu > ProgWysoki ? wysokoN + 1 : 0; niskoN = p.Cpu < ProgNiski ? niskoN + 1 : 0;
            Tryb chce; string why; bool reczny = false, goraco = false;
            Procesy();
            // 💽 dysk pracuje (kopiowanie, instalacja, wczytywanie): ponad 15 MB/s albo 2+ operacje w kolejce, dłużej niż 3 s; trzymane jeszcze 5 s po końcu
            bool dyskRuch = p.DyskMBs > 15 || p.DyskKol >= 2; dyskN = dyskRuch ? dyskN + 1 : 0;
            if (dyskN >= 3) dyskDo = DateTime.UtcNow.AddSeconds(5);
            bool kopiuje = DateTime.UtcNow < dyskDo;
            // 1. temperatura - teraz albo za 10 s (przewidywanie ze wzrostu)
            if (p.Temp > 0 && (p.Temp >= TempLimit || p.Temp + trend * 10 >= TempLimit + 2))
            { chce = Tryb.Cichy; goraco = true; why = "Procesor gorący (" + p.Temp.ToString("0") + " °C" + (trend > 0.3 ? ", rośnie" : "") + ") – zwalniam, zanim wentylator przyspieszy."; }
            // 2. „zawsze ten tryb”: program działa (na wierzchu albo w tle - np. Studio One gra, a Ty jesteś w innym oknie) - jego tryb, bez względu na obciążenie i baterię
            else if (ZawszeDla(p, out var rr, out var rn)) { gpuBound = false; reczny = true; chce = rr; why = rn + " działa – ustawiony na stałe: " + Nazwa(rr) + "."; }
            // 🖥 ekran wyłączony (np. pilotem, film tylko do słuchania): Windows uruchamia wtedy zadania „w bezczynności” (indeksowanie, skanowanie, aktualizacje),
            // a Auto brało je za pracę i przyspieszało (wentylator w nocy). Teraz cicho - te zadania idą wolniej, odtwarzanie ma dość mocy
            else if (EkranWyl && BezRuchuS() > 60) { gpuBound = false; chce = Tryb.Cichy; why = "Ekran wyłączony – cicho (zadania Windows w tle dostają mniej mocy, wentylator nie przyspiesza)."; }
            // 3. wczytywanie (dysk pracuje, procesor też) - krótko pełna moc
            else if (kopiuje && p.Cpu > 30) { chce = Tryb.Turbo; why = "Wczytywanie (dysk " + p.DyskMBs.ToString("0") + " MB/s, procesor " + p.Cpu.ToString("0") + " %) – pełna moc, żeby szybciej skończyć."; }
            // 3. gra obciąża kartę, procesor nie: procesor mniej mocy, karta chłodniej i wyższy zegar (wyjście po 3 s - stabilnie)
            else if (p.Gpu > 75 && p.Cpu < 50 || gpuBound && p.Gpu > 60 && (gpuWyjscieN = p.Cpu > 50 ? gpuWyjscieN + 1 : 0) < 3)
            { gpuBound = true; chce = Tryb.Zrownowazony; why = "Program obciąża kartę graficzną (" + p.Gpu.ToString("0") + " %), procesor tylko " + p.Cpu.ToString("0") + " % – procesor nie potrzebuje więcej mocy, karta ma chłodniej."; }
            // 4. duże obciążenie - od razu w górę (pół sekundy wystarczy, bez przycięć)
            else if (wysokoN >= 1) { gpuBound = false; chce = Tryb.Turbo; why = "Duże obciążenie (" + p.Cpu.ToString("0") + " %) – pełna moc od razu."; }
            // 5. ręcznie przypisany / wyuczony program
            else if (ZnanyProgram(p.Program, out var v) && v.N >= 30 && Wyuczony(v) != Tryb.Cichy) { gpuBound = false; chce = Wyuczony(v); why = p.Program + " – zapamiętany: zwykle " + v.Cpu.ToString("0") + " %, szczyt " + v.Szczyt.ToString("0") + " % procesora → " + Nazwa(chce) + "."; }
            // 6. spoczynek (dłużej) - cicho; o porze, gdy zwykle pracujesz ciężko, zostaje zapas
            else if (niskoN >= 8) { gpuBound = false; bool zwykle = godzN[DateTime.Now.Hour] > 120 && godziny[DateTime.Now.Hour] > 40; chce = zwykle ? Tryb.Zrownowazony : Tryb.Cichy; why = zwykle ? "Spokój, ale o tej porze zwykle pracujesz ciężko – zostawiam zapas mocy." : "Spoczynek (" + p.Cpu.ToString("0") + " %) – cicho i oszczędnie, zegar do " + CichyGhz.ToString("0.0", Inv) + " GHz."; }
            else { gpuBound = false; chce = Tryb.Zrownowazony; why = "Zwykła praca (" + p.Cpu.ToString("0") + " %)."; }
            // kopiowanie i instalacja: nie zwalniam do Cichego (małe pliki i skanowanie antywirusa potrzebują procesora), ale bez Turbo - mało ciepła
            if (!goraco && !reczny && chce == Tryb.Cichy && (kopiuje || instalator != null))
            { chce = Tryb.Zrownowazony; why = instalator != null ? "Instalacja (" + instalator + ") – nie zwalniam do Cichego, żeby nie szarpała." : "Kopiowanie (dysk " + p.DyskMBs.ToString("0") + " MB/s) – nie zwalniam do Cichego."; }
            if (!reczny && p.Bateria && chce == Tryb.Turbo && !(p.Temp < 0 && wysokoN > 3)) { chce = Tryb.Zrownowazony; why += " Na baterii bez Turbo."; }

            // w dół powoli: Turbo → Zrównoważony po 3 s, Zrównoważony → Cichy po 8 s; w górę od razu
            if (chce < Obecny && p.Temp < TempLimit)
            {
                double wait = Obecny == Tryb.Turbo ? 3 : 8;
                if ((DateTime.UtcNow - ostZmiana).TotalSeconds < wait && !(p.Temp >= TempLimit)) { Dlaczego = why + " (zmiana za chwilę – bez skakania trybów)"; return Obecny; }
            }
            // samodostrajanie: w Cichym procesor ciągle pełny = za późno w górę (próg niżej); w Turbo ciągle mało = za łatwo (próg wyżej)
            if (Obecny == Tryb.Cichy && p.Cpu > 95) { if (++przycieciaN > 20) { ProgWysoki = Math.Max(50, ProgWysoki - 2); przycieciaN = 0; } } else przycieciaN = 0;
            if (Obecny == Tryb.Turbo && p.Cpu < 30 && !gpuBound) { if (++nadmiarN > 60) { ProgWysoki = Math.Min(85, ProgWysoki + 2); nadmiarN = 0; } } else nadmiarN = 0;
            Dlaczego = why;
            return Ustal(chce);
        }
        // program z „zawsze ten tryb”: najpierw ten na wierzchu, inaczej działający w tle (kilka - najwyższy tryb); lista procesów co 3 s
        HashSet<string> dzialajace = new HashSet<string>(StringComparer.OrdinalIgnoreCase); DateTime dzialajaceT;
        // lista działających programów co 3 s (zawsze ten tryb, Studio, instalatory)
        string instalator;   // działający instalator albo null
        void Procesy()
        {
            if ((DateTime.UtcNow - dzialajaceT).TotalSeconds < 3) return;
            dzialajaceT = DateTime.UtcNow; var d = new HashSet<string>(StringComparer.OrdinalIgnoreCase); int msi = 0; string ins = null;
            foreach (var pr in Process.GetProcesses())
                using (pr)
                    try
                    {
                        var n = pr.ProcessName; d.Add(n);
                        if (n.Equals("msiexec", StringComparison.OrdinalIgnoreCase)) msi++;   // usługa Instalatora Windows zostaje chwilę po instalacji - instalacja trwa, gdy są dwa
                        else if (ins == null && (n.IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("install", StringComparison.OrdinalIgnoreCase) >= 0 || n.StartsWith("unins", StringComparison.OrdinalIgnoreCase))
                            && !n.Equals("TrustedInstaller", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("Velivo", StringComparison.OrdinalIgnoreCase)) ins = n;
                    }
                    catch (Exception) { }
            dzialajace = d; instalator = ins ?? (msi >= 2 ? "Instalator Windows" : null);
        }
        bool ZawszeDla(Pomiar p, out Tryb t, out string nazwa)
        {
            t = Tryb.Zrownowazony; nazwa = null;
            lock (Studio) if (Reczne.TryGetValue(p.Program ?? "", out t)) { nazwa = p.Program; return true; }
            Procesy();
            bool jest = false;
            KeyValuePair<string, Tryb>[] lista; lock (Studio) lista = Reczne.ToArray();
            // w tle tylko „więcej mocy” (Studio One gra, a Ty w innym oknie); „zawsze Cichy” obowiązuje, gdy program jest na wierzchu -
            // program przy zegarze z „zawsze Cichy” nie może trzymać całego komputera w Cichym (Sejf-Klient, Velivo Kursory)
            foreach (var r in lista) if (r.Value != Tryb.Cichy && dzialajace.Contains(r.Key) && (!jest || r.Value > t)) { t = r.Value; nazwa = r.Key; jest = true; }
            return jest;
        }
        // 🎚 tryb Studio: działa program z trybem Studio (lista procesów jak wyżej, co 3 s)
        public string StudioProg;   // nazwa działającego programu Studio albo null
        void StudioCzynne(Pomiar p)
        {
            string[] st; lock (Studio) st = Studio.ToArray();
            if (st.Length == 0) { StudioProg = null; return; }
            Procesy();
            StudioProg = st.FirstOrDefault(n => string.Equals(n, p.Program, StringComparison.OrdinalIgnoreCase)) ?? st.FirstOrDefault(n => dzialajace.Contains(n));
        }
        bool ZnanyProgram(string n, out (double Cpu, double Gpu, int N, double Szczyt) v) { lock (programy) return programy.TryGetValue(n ?? "", out v); }
        public void Ponow() => zastosowany = false;   // zmienione ustawienie trybu (np. najniższy zegar Cichego) - zastosuj od nowa przy następnej decyzji
        bool studioZast;
        Tryb Ustal(Tryb t) { bool st = StudioProg != null; if (st && t == Tryb.Cichy) t = Tryb.Zrownowazony;   // Studio: niski zegar przy dźwięku = trzaski
            if (t != Obecny || !zastosowany || st != studioZast) { if (t != Obecny) ostZmiana = DateTime.UtcNow; Obecny = t; studioZast = st; Zastosuj(t); } return t; }

        // ---------- sterowanie ----------
        bool zastosowany;
        static readonly Guid Sub = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        static readonly Guid Max = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec"), Min = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c"),
            Boost = new Guid("be337238-0d82-4146-a960-4f3749d470c7"), Epp = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863"), Freq = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e100");
        // ⚡ szybkość reakcji Windows na obciążenie (próg i czas podnoszenia / obniżania zegara, sposób podnoszenia) i 🎚 parkowanie rdzeni (najmniej czuwających rdzeni, %)
        static readonly Guid IncThr = new Guid("06cadf0e-64ed-448a-8927-ce7bf90eb35d"), DecThr = new Guid("12a0ab44-fe28-4fa9-b3bd-4b64f44960a6"),
            IncTime = new Guid("984cf492-3bed-4488-a8f9-4286c97bf5aa"), DecTime = new Guid("d8edeb9b-95cf-4f95-a73c-b061973693c8"),
            IncPol = new Guid("465e1f50-b610-473a-ab58-00d1077dc418"), MinCores = new Guid("0cc5b647-c1df-4637-891a-dec35c318583");
        [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid guid);
        [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid s, ref Guid sub, ref Guid set, uint v);
        [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid s, ref Guid sub, ref Guid set, uint v);
        [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid s, ref Guid sub, ref Guid set, out uint v);
        [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid s, ref Guid sub, ref Guid set, out uint v);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
        public string Blad = "";

        static Guid Aktywny() { PowerGetActiveScheme(IntPtr.Zero, out var p); try { return (Guid)Marshal.PtrToStructure(p, typeof(Guid)); } finally { LocalFree(p); } }
        static readonly Guid[] Ustawienia = { Max, Min, Boost, Epp, Freq, IncThr, DecThr, IncTime, DecTime, IncPol, MinCores };
        readonly Dictionary<Guid, (uint Ac, uint Dc)> oryg = new Dictionary<Guid, (uint, uint)>();   // wartości Windows sprzed programu
        static readonly string KopiaFile = Path.Combine(Dir, "windows-przed.txt");

        // ustawienia Windows sprzed pierwszego uruchomienia - przywracane przy zamknięciu programu
        void ZapamietajWindows(Guid s)
        {   // zapis raz; ustawienie dopisane w nowszej wersji programu dochodzi do zapisu przy pierwszym uruchomieniu tej wersji (jeszcze nieruszone)
            var l = new List<string>(); try { if (File.Exists(KopiaFile)) l.AddRange(File.ReadAllLines(KopiaFile)); } catch (Exception) { }
            if (l.Count == 0 || l[0] != s.ToString()) { if (l.Count > 0) return; l.Add(s.ToString()); }
            bool nowe = false;
            foreach (var x in l.Skip(1)) { var a = x.Split('\t'); if (a.Length >= 3) oryg[new Guid(a[0])] = (uint.Parse(a[1]), uint.Parse(a[2])); }
            foreach (var g in Ustawienia)
            {
                if (oryg.ContainsKey(g)) continue;
                var gg = g; var sub = Sub;
                if (PowerReadACValueIndex(IntPtr.Zero, ref s, ref sub, ref gg, out var a) == 0 && PowerReadDCValueIndex(IntPtr.Zero, ref s, ref sub, ref gg, out var d) == 0) { oryg[g] = (a, d); l.Add(g + "\t" + a + "\t" + d); nowe = true; }
            }
            if (nowe) try { Directory.CreateDirectory(Dir); File.WriteAllLines(KopiaFile, l); } catch (Exception) { }
        }
        public void PrzywrocWindows()
        {
            try
            {
                var l = File.ReadAllLines(KopiaFile); var s = new Guid(l[0]); var sub = Sub;
                foreach (var x in l.Skip(1)) { var a = x.Split('\t'); var g = new Guid(a[0]); PowerWriteACValueIndex(IntPtr.Zero, ref s, ref sub, ref g, uint.Parse(a[1])); PowerWriteDCValueIndex(IntPtr.Zero, ref s, ref sub, ref g, uint.Parse(a[2])); }
                PowerSetActiveScheme(IntPtr.Zero, ref s); File.Delete(KopiaFile); oryg.Clear();
            }
            catch (Exception) { }
            if (RyzenDziala) { var m = MocAmd(Tryb.Turbo); RyzenWywolaj("--stapm-limit=" + m.Stapm * 1000 + " --fast-limit=" + m.Fast * 1000 + " --slow-limit=" + m.Slow * 1000 + " --tctl-temp=" + m.Tctl); }   // AMD: limity fabryczne wracają same po restarcie; do tego czasu pełna moc
            zastosowany = false;
        }

        // ↺ ustawienia domyślne Windows: aktywny plan zasilania wraca do wartości fabrycznych Windows (tylko ten plan), program przestaje sterować;
        // AMD: limity mocy RyzenAdj wracają do fabrycznych po ponownym uruchomieniu komputera (do tego czasu pełna moc)
        [DllImport("powrprof.dll")] static extern uint PowerRestoreIndividualDefaultPowerScheme(ref Guid s);
        public string DomyslneWindows()
        {
            Put("tryb", "windows");
            uint e = 1;
            try { var s = Aktywny(); e = PowerRestoreIndividualDefaultPowerScheme(ref s); PowerSetActiveScheme(IntPtr.Zero, ref s); File.Delete(KopiaFile); oryg.Clear(); } catch (Exception) { }
            if (RyzenDziala) { var m = MocAmd(Tryb.Turbo); RyzenWywolaj("--stapm-limit=" + m.Stapm * 1000 + " --fast-limit=" + m.Fast * 1000 + " --slow-limit=" + m.Slow * 1000 + " --tctl-temp=" + m.Tctl); }
            zastosowany = false;
            return e == 0 ? "Plan zasilania Windows przywrócony do ustawień domyślnych. Velivo Procesor nie steruje (wybierz tryb, aby znów sterował)." + (RyzenDziala ? " Moc AMD: fabryczne limity wrócą po ponownym uruchomieniu komputera." : "")
                : "Nie udało się przywrócić planu zasilania (kod " + e + ").";
        }

        public (int Stapm, int Fast, int Slow, int Tctl) MocAmd(Tryb t)
        {   // profile z CPU Manager v40 (W, °C); zmienialne w ustawieniach
            var d = t == Tryb.Cichy ? "12,28,15,75" : t == Tryb.Turbo ? "25,35,30,88" : "15,28,22,80";
            var a = (Get("amd-" + (int)t) ?? d).Split(','); int I(int i, int def) => a.Length > i && int.TryParse(a[i], out var v) ? v : def;
            return (I(0, 15), I(1, 28), I(2, 22), I(3, 80));
        }

        void Zastosuj(Tryb t)
        {
            Blad = "";
            try
            {
                var s = Aktywny(); ZapamietajWindows(s);
                // Cichy: zegar do wybranej wartości (także poniżej bazowego), bez boostu, oszczędzanie; Zrównoważony: boost oszczędny; Turbo: boost agresywny, pełna wydajność
                uint max = t == Tryb.Cichy ? (uint)Math.Max(5, Math.Min(99, Math.Round(CichyGhz * 1000 / Math.Max(1, BazaMhz) * 100))) : 100;
                uint boost = t == Tryb.Cichy ? 0u : t == Tryb.Turbo ? 2u : 3u, epp = t == Tryb.Cichy ? 200u : t == Tryb.Turbo ? 0u : 100u;
                uint freq = t == Tryb.Cichy ? (uint)Math.Round(CichyGhz * 1000) : 0u;   // Intel: limit w MHz (0 = bez limitu)
                uint min = t == Tryb.Turbo ? 30u : t == Tryb.Cichy ? max : 5u;   // Cichy: zegar stoi na wartości z suwaka (bez schodzenia niżej - wcześniej spadał do 0,8 GHz przy 1,2)
                // ⚡ reakcja (próg w %, czas w okresach sprawdzania ~30 ms, podnoszenie: 0 stopniowo, 1 o krok, 2 od razu do pełna): Cichy leniwie (ciszej), Turbo od razu;
                // 🎚 Studio (muzyka): zegar w górę przy najmniejszym obciążeniu, w dół bardzo powoli, wszystkie rdzenie czuwają - bez opóźnień budzenia (trzaski)
                bool st = StudioProg != null;
                var (it, dt, iT, dT, ip) = st ? (10u, 5u, 1u, 10u, 2u) : t == Tryb.Turbo ? (30u, 15u, 1u, 4u, 2u) : t == Tryb.Cichy ? (80u, 50u, 4u, 1u, 1u) : (50u, 25u, 2u, 2u, 0u);
                // Studio + Turbo: dodatkowo stały pełny zegar (miks z wieloma wtyczkami); Studio + Zrównoważony: bez stałego pełnego zegara - chłodniej i ciszej (nagrywanie mikrofonem)
                if (st && t == Tryb.Turbo) { boost = 2; epp = 0; min = 100; }
                else if (st) epp = 50;
                var w = new List<(Guid, uint, uint)> { (Max, max, max), (Min, min, min), (Boost, boost, boost), (Epp, epp, epp), (Freq, freq, freq), (IncThr, it, it), (DecThr, dt, dt), (IncTime, iT, iT), (DecTime, dT, dT), (IncPol, ip, ip) };
                if (st && !oryg.ContainsKey(MinCores)) { var g0 = MinCores; var sb = Sub; if (PowerReadACValueIndex(IntPtr.Zero, ref s, ref sb, ref g0, out var a0) == 0 && PowerReadDCValueIndex(IntPtr.Zero, ref s, ref sb, ref g0, out var d0) == 0) oryg[MinCores] = (a0, d0); }   // po Studio parkowanie wraca do tej wartości
                if (st) w.Add((MinCores, 100, 100)); else if (oryg.TryGetValue(MinCores, out var mc)) w.Add((MinCores, mc.Ac, mc.Dc));   // poza Studio - parkowanie jak w Windows
                uint err = 0;
                foreach (var (g, va, vd) in w) { var gg = g; var sub = Sub; uint e = PowerWriteACValueIndex(IntPtr.Zero, ref s, ref sub, ref gg, va); PowerWriteDCValueIndex(IntPtr.Zero, ref s, ref sub, ref gg, vd); if (g == Max || g == Min || g == Boost) err |= e; }
                PowerSetActiveScheme(IntPtr.Zero, ref s);
                if (err != 0) Blad = "Windows nie przyjął części ustawień zasilania (kod " + err + ").";
            }
            catch (Exception ex) { Blad = ex.Message; }
            if (RyzenDziala) { var m = MocAmd(t); RyzenWywolaj("--stapm-limit=" + m.Stapm * 1000 + " --fast-limit=" + m.Fast * 1000 + " --slow-limit=" + m.Slow * 1000 + " --tctl-temp=" + m.Tctl); }
            zastosowany = true;
        }

        public void SprawdzRyzen()
        {
            RyzenDziala = false;
            if (Ryzen == null) return;
            RyzenDziala = RyzenWywolaj("--info").Any(l => l.IndexOf("STAPM LIMIT", StringComparison.OrdinalIgnoreCase) >= 0);
        }
        List<string> RyzenWywolaj(string args)
        {
            var wynik = new List<string>();
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(Ryzen, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                {
                    var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(3000);
                    wynik.AddRange(o.Split('\n'));
                }
            }
            catch (Exception) { }
            return wynik;
        }

        public static bool Admin()
        {
            try { using (var id = System.Security.Principal.WindowsIdentity.GetCurrent()) return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); }
            catch (Exception) { return false; }
        }
    }
}
