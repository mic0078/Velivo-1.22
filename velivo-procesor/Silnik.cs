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
    // przewidywanie temperatury z jej wzrostu, samodostrajanie progów. Sterowanie: plan zasilania Windows (Intel i AMD) + PawnIO (moc AMD).
    enum Tryb { Cichy = 0, Zrownowazony = 1, Turbo = 2 }

    sealed class Pomiar
    {
        public double Cpu, Gpu = -1, Temp = -1, Dysk, DyskMBs, DyskKol, Ghz; public string Program = ""; public int Pid; public double ProgCpu = -1; public bool Bateria;
    }

    sealed partial class Silnik
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
        public double CichyGhz => GetD("cichy-ghz", 1.4);   // 🌙 najwyższy zegar w trybie Cichym (cisza w nocy - właściciel ustawia suwakiem); inne tryby bez ograniczeń
        public double TempLimit => GetD("temp-limit", 90);

        public readonly string ProcesorNazwa; public readonly bool Amd; public readonly double BazaMhz;
        // 🧩 hybrydowy = rdzenie o różnej klasie wydajności (Windows: EfficiencyClass > 0 u części rdzeni)
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetSystemCpuSetInformation(IntPtr info, int len, out int ret, IntPtr proc, int flags);
        public readonly bool Hybrydowy = WykryjHybryde();
        static bool WykryjHybryde()
        {
            try
            {
                GetSystemCpuSetInformation(IntPtr.Zero, 0, out var n, IntPtr.Zero, 0); if (n <= 0) return false;
                var buf = Marshal.AllocHGlobal(n);
                try
                {
                    if (!GetSystemCpuSetInformation(buf, n, out n, IntPtr.Zero, 0)) return false;
                    for (int off = 0; off + 19 <= n;) { int size = Marshal.ReadInt32(buf, off); if (size <= 0) break; if (Marshal.ReadByte(buf, off + 18) > 0) return true; off += size; }   // SYSTEM_CPU_SET_INFORMATION: EfficiencyClass na pozycji 18
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch (Exception) { }
            return false;
        }
        public AmdSilnik AmdE;   // 🔥 silnik AMD (AmdSilnik.cs, PawnIO) albo null
        public bool AmdDziala => AmdE != null && AmdE.Dziala;   // PawnIO odpowiada (uprawnienia administratora i obsługiwany procesor)

        public Silnik()
        {
            try { foreach (var l in File.ReadAllLines(SetFile)) { int i = l.IndexOf('='); if (i > 0) set[l.Substring(0, i)] = l.Substring(i + 1); } } catch (Exception) { }
            try { using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")) { ProcesorNazwa = (k?.GetValue("ProcessorNameString") as string ?? "").Trim(); BazaMhz = Convert.ToDouble(k?.GetValue("~MHz") ?? 0); } } catch (Exception) { ProcesorNazwa = "?"; }
            Amd = ProcesorNazwa.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0;
            var pd = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PawnIO");
            if (Amd && File.Exists(Path.Combine(pd, "RyzenSMU.bin"))) AmdE = new AmdSilnik(pd);
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
            if (AmdE != null) AmdE.Granica = TempLimit;   // suwak - od razu do silnika AMD
            double ta = AmdE != null ? AmdE.Krok() : -1;   // także ponowna próba, gdy PawnIO nie odpowiadał   // 🔥 AMD: telemetria silnika (co 1 s z biblioteki; z programu co 10 s)
            if (ta > 0) { tempOst = ta; TempZrodlo = "z procesora (PawnIO)"; }
            else if (++tempTick % 3 == 1) tempOst = Temperatura();   // co 3 s
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

        // temperatura: AMD - PawnIO (Tctl), inaczej LibreHardwareMonitor / OpenHardwareMonitor (gdy działa), zapasowo strefa ACPI; -1 = nieznana
        public string TempZrodlo = "";   // skąd temperatura (słowami do okna)
        // źródło wybierane raz (potem tylko ono - bez zapytań WMI do nieistniejących źródeł co 3 s); bez wyniku - ponowne szukanie co minutę.
        // Czujnik płyty (ACPI), który stoi w miejscu ponad 10 min, nie mierzy procesora (laptopy: stałe 28 °C przy 4 GHz) - wtedy „nieznana”, bez fałszywej ochrony.
        int tempZr = -1; DateTime tempSzukaj; double acpiOst = -999; DateTime acpiZmiana = DateTime.UtcNow;
        double Temperatura()
        {
            if (tempZr < 0 && DateTime.UtcNow < tempSzukaj) return -1;
            for (int z = tempZr < 0 ? 0 : tempZr; z < 4; z++)
            {
                double t = z == 0 ? TempAmd() : z == 1 ? TempLhm() : z == 2 ? TempAcpiWmi() : TempAcpiPdh();
                if (t > 0)
                {
                    if (z >= 2)
                    {   // czujnik płyty: sprawdzam, czy w ogóle się zmienia
                        if (Math.Abs(t - acpiOst) >= 0.5) { acpiOst = t; acpiZmiana = DateTime.UtcNow; }
                        if ((DateTime.UtcNow - acpiZmiana).TotalMinutes > 10) { TempZrodlo = "brak – czujnik płyty stoi w miejscu (nie mierzy procesora)"; return -1; }
                    }
                    tempZr = z; return t;
                }
                if (tempZr >= 0) { tempZr = -1; z = -1; continue; }   // wybrane źródło przestało działać - szukam od nowa
            }
            tempZr = -1; tempSzukaj = DateTime.UtcNow.AddMinutes(1); TempZrodlo = "brak czujnika (szukam co minutę)"; return -1;
        }
        double TempAmd()
        {
            var t = AmdDziala ? AmdE.Ost?.Temp ?? -1 : -1;   // odczyt silnika AMD
            if (t > 0) TempZrodlo = "z procesora (PawnIO)";
            return t > 0 ? t : -1;
        }
        double TempLhm()
        {   // Intel: „CPU Package” / rdzenie; AMD: „Tctl/Tdie”, „CCD”; bez „Distance to TjMax” (to zapas do granicy, nie temperatura)
            foreach (var ns in new[] { @"root\LibreHardwareMonitor", @"root\OpenHardwareMonitor" })
                try
                {
                    using (var s = new System.Management.ManagementObjectSearcher(ns, "SELECT Name, Value, Identifier FROM Sensor WHERE SensorType='Temperature'"))
                    {
                        double pakiet = -1, rdzen = -1;
                        foreach (System.Management.ManagementObject o in s.Get())
                        {
                            var n = o["Name"] as string ?? ""; var id = o["Identifier"] as string ?? ""; var v = Convert.ToDouble(o["Value"]);
                            if (v <= 0 || v > 125 || n.IndexOf("Distance", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("cpu", StringComparison.OrdinalIgnoreCase) < 0) continue;   // tylko czujniki procesora
                            if (n.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Tctl", StringComparison.OrdinalIgnoreCase) >= 0) pakiet = Math.Max(pakiet, v);
                            else rdzen = Math.Max(rdzen, v);
                        }
                        double best = pakiet > 0 ? pakiet : rdzen;
                        if (best > 0) { TempZrodlo = "z procesora (" + (ns.Contains("Libre") ? "LibreHardwareMonitor" : "OpenHardwareMonitor") + ")"; return best; }
                    }
                }
                catch (Exception) { }
            return -1;
        }
        double TempAcpiWmi()
        {
            try
            {
                using (var s = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                    foreach (System.Management.ManagementObject o in s.Get()) { double t = Convert.ToDouble(o["CurrentTemperature"]) / 10 - 273.15; if (t > 0 && t < 120) { TempZrodlo = "czujnik płyty (ACPI)"; return t; } }
            }
            catch (Exception) { }
            return -1;
        }
        double TempAcpiPdh()
        {
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
        public void Zapomnij(string n) { if (string.Equals(ostProgram, n, StringComparison.OrdinalIgnoreCase)) ostProgram = ""; lock (programy) { programy.Remove(n); ocena.Remove(n); ostatnio.Remove(n); } lock (Studio) { Reczne.Remove(n); Priorytet.Remove(n); Studio.Remove(n); BezPb.Remove(n); } ZapiszNauke(); }
        public double ProgWysoki = 70, ProgNiski = 20;   // samodostrajane
        DateTime naukaZapis = DateTime.UtcNow;

        void WczytajNauke()
        {
            try
            {
                foreach (var l in File.ReadAllLines(NaukaFile))
                {
                    var a = l.Split('\t');
                    if (a[0] == "P" && a.Length >= 5 && !Powloka.Contains(a[1]) && !Obserwacja.Contains(a[1])) programy[a[1]] = (double.Parse(a[2], Inv), double.Parse(a[3], Inv), int.Parse(a[4]), a.Length >= 6 ? double.Parse(a[5], Inv) : double.Parse(a[2], Inv));
                    else if (a[0] == "H" && a.Length >= 4) { int h = int.Parse(a[1]); godziny[h] = double.Parse(a[2], Inv); godzN[h] = int.Parse(a[3]); }
                    else if (a[0] == "R" && a.Length >= 3) Reczne[a[1]] = (Tryb)int.Parse(a[2]);
                    else if (a[0] == "Q" && a.Length >= 3) Priorytet[a[1]] = int.Parse(a[2]);
                    else if (a[0] == "S" && a.Length >= 2) Studio.Add(a[1]);
                    else if (a[0] == "B" && a.Length >= 2) BezPb.Add(a[1]);
                    else if (a[0] == "A" && a.Length >= 6 && !Obserwacja.Contains(a[1])) { ocena[a[1]] = (new[] { double.Parse(a[2], Inv), double.Parse(a[3], Inv), double.Parse(a[4], Inv) }, int.Parse(a[5])); if (a.Length >= 7 && long.TryParse(a[6], out var tk)) ostatnio[a[1]] = new DateTime(tk); }
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
                lock (programy) foreach (var o in ocena) l.Add("A\t" + o.Key + "\t" + string.Join("\t", o.Value.Q.Select(x => x.ToString("0.000", Inv))) + "\t" + o.Value.N + "\t" + (ostatnio.TryGetValue(o.Key, out var od) ? od.Ticks : 0));
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

        // 🤖 nauka skutków (wniosek z Q-learningu CPU Manager v40, lżej): dla każdego programu ocena trybów z tego, co tryb dał - cisza bez przycięć = nagroda,
        // przycięcie (procesor albo rdzeń programu pełny) w słabszym trybie = kara, Turbo przy małym obciążeniu lub cieple = kara; Twój ręczny wybór przy programie = silna nagroda.
        // Wybór: tryb z najlepszą oceną, przy remisie cichszy; dopiero po 1 min pracy programu. Bez losowania (bez skakania trybów).
        readonly Dictionary<string, DateTime> ostatnio = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);   // kiedy program był ostatnio na wierzchu (okno AI)
        readonly Dictionary<string, (double[] Q, int N)> ocena = new Dictionary<string, (double[], int)>(StringComparer.OrdinalIgnoreCase);
        void Ocen(Pomiar p, Tryb t, bool ręcznie)
        {
            if (string.IsNullOrEmpty(p.Program) || Powloka.Contains(p.Program)) return;
            bool lag = p.Cpu > 85 || p.ProgCpu > 90; bool cieplo = p.Temp > 0 && p.Temp >= TempLimit - 5;
            double r = ręcznie ? 2 : t == Tryb.Cichy ? (lag ? -2 : 1) : t == Tryb.Zrownowazony ? (lag ? -0.5 : p.Cpu < 15 ? -0.3 : 0.5) : (lag ? 1.5 : p.Cpu < 30 ? -1.5 : 0.3);
            if (!ręcznie && cieplo) r -= t == Tryb.Turbo ? 1 : t == Tryb.Zrownowazony ? 0.3 : 0;
            lock (programy)
            {
                if (!ocena.TryGetValue(p.Program, out var o)) o = (new double[3], 0);
                o.Q[(int)t] += (r - o.Q[(int)t]) * (ręcznie ? 0.05 : 0.02);
                if (ręcznie) for (int i = 0; i < 3; i++) if (i != (int)t) o.Q[i] += (-1 - o.Q[i]) * 0.02;   // inne tryby przy tym programie - słabiej
                ocena[p.Program] = (o.Q, Math.Min(o.N + 1, 100000)); ostatnio[p.Program] = DateTime.Now;
            }
        }
        bool Oceniony(string prog, out Tryb t)
        {
            t = Tryb.Zrownowazony;
            lock (programy)
            {
                if (string.IsNullOrEmpty(prog) || !ocena.TryGetValue(prog, out var o) || o.N < 60) return false;
                int best = 0; for (int i = 1; i < 3; i++) if (o.Q[i] > o.Q[best] + 0.2) best = i;   // remis - cichszy
                t = (Tryb)best; return true;
            }
        }

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
            if ((DateTime.UtcNow - naukaZapis).TotalMinutes > 5) { naukaZapis = DateTime.UtcNow; ZapiszNauke(); ZapiszGlosy(); }
        }

        // ---------- decyzja ----------
        // sekundy od ostatniego ruchu myszy / klawisza - ktoś przy komputerze = ekran na pewno nie „wyłączony” (zabezpieczenie, gdy Windows nie przyśle „włączony”)
        public Tryb Obecny = Tryb.Zrownowazony; public string Dlaczego = ""; public volatile bool EkranWyl;
        readonly Queue<double> tempHist = new Queue<double>(); DateTime ostZmiana = DateTime.MinValue; int wysokoN, niskoN, gpuWyjscieN; bool gpuBound;
        int przycieciaN, nadmiarN;   // do samodostrajania progów
        int dyskN; DateTime dyskDo;

        // okna, na które tylko patrzysz (Velivo Procesor, okno AI, Menedżer zadań, Monitor) - nie uczą i nie decydują; liczy się program pod spodem
        static readonly HashSet<string> Obserwacja = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "VelivoProcesor", "VelivoMonitor", "VelivoCentrum", "Taskmgr", "perfmon", "resmon", "OpenHardwareMonitor", "LibreHardwareMonitor", "HWiNFO64", "HWiNFO32" };
        string trybOst; DateTime trybOd;
        string ostProgram = ""; DateTime wyjscieCichy; string poprzedniNaWierzchu = ""; DateTime startDo;
        public Tryb Decyduj(Pomiar p)
        {
            // okno, na które tylko patrzysz (Velivo Procesor, pulpit…): decyzja dla programu pod spodem, ale BEZ nauki - inaczej zapomniany program wracał od razu (2026-11-25)
            bool podglad = Obserwacja.Contains(p.Program ?? "") || Powloka.Contains(p.Program ?? "");
            if (podglad) { p.Program = ostProgram; p.ProgCpu = -1; } else ostProgram = p.Program ?? "";
            if (!podglad) Ucz(p);
            StudioCzynne(p);
            if (Get("tryb") == "pauza") { Dlaczego = "Wstrzymany – steruje inny program. Velivo Procesor tylko mierzy i pokazuje (ProBalance też wstrzymany). Wybierz tryb, aby znów sterował."; return Obecny; }
            if (Get("tryb") == "windows") { Dlaczego = "Steruje Windows (ustawienia domyślne) – Velivo Procesor tylko pokazuje pomiary."; return Obecny; }
            if (Get("tryb") != trybOst) { trybOst = Get("tryb"); trybOd = DateTime.UtcNow; }
            // ręczny wybór uczy tylko przez 2 min po kliknięciu (decyzja przy tym programie) - „zawsze Cichy” na cały dzień nie zapisuje Cichego wszystkim programom
            if (!Auto) { var r = Get("tryb") == "cichy" ? Tryb.Cichy : Get("tryb") == "turbo" ? Tryb.Turbo : Tryb.Zrownowazony; if ((DateTime.UtcNow - trybOd).TotalMinutes < 2 && !podglad) Ocen(p, r, true); Dlaczego = "Wybrany ręcznie." + ((DateTime.UtcNow - trybOd).TotalMinutes < 2 && !string.IsNullOrEmpty(p.Program) ? " AI zapamiętuje ten wybór dla " + p.Program + "." : ""); return Ustal(r); }
            if (!podglad) Ocen(p, Obecny, false);
            if (p.Temp > 0) { tempHist.Enqueue(p.Temp); while (tempHist.Count > 10) tempHist.Dequeue(); }
            double trend = tempHist.Count >= 5 ? (tempHist.Last() - tempHist.First()) / (tempHist.Count - 1) : 0;   // °C na sekundę
            wysokoN = p.Cpu > ProgWysoki ? wysokoN + 1 : 0; niskoN = p.Cpu < ProgNiski ? niskoN + 1 : 0;
            Tryb chce; string why; bool reczny = false, goraco = false;
            var glos = Glosuj(p); ZAi = false;   // zawsze (przewidywanie i trafność uczą się co sekundę)
            Procesy();
            // 💽 dysk pracuje (kopiowanie, instalacja, wczytywanie): ponad 15 MB/s albo 2+ operacje w kolejce, dłużej niż 3 s; trzymane jeszcze 5 s po końcu
            bool dyskRuch = p.DyskMBs > 15 || p.DyskKol >= 2; dyskN = dyskRuch ? dyskN + 1 : 0;
            if (dyskN >= 3) dyskDo = DateTime.UtcNow.AddSeconds(5);
            bool kopiuje = DateTime.UtcNow < dyskDo;
            // 1. temperatura - teraz albo za 10 s (przewidywanie ze wzrostu)
            // AMD z silnikiem: granicę trzyma sam procesor (suwak = jego granica temperatury) - bez zwalniania do Cichego, żeby Turbo dawało pełną moc
            if (!AmdDziala && p.Temp > 0 && (p.Temp >= TempLimit || p.Temp + trend * 10 >= TempLimit + 2))
            { chce = Tryb.Cichy; goraco = true; why = "Procesor gorący (" + p.Temp.ToString("0") + " °C" + (trend > 0.3 ? ", rośnie" : "") + ") – zwalniam, zanim wentylator przyspieszy."; }
            // 2. „zawsze ten tryb”: program działa (na wierzchu albo w tle - np. Studio One gra, a Ty jesteś w innym oknie) - jego tryb, bez względu na obciążenie i baterię
            else if (ZawszeDla(p, out var rr, out var rn)) { gpuBound = false; reczny = true; chce = rr; why = rn + " działa – ustawiony na stałe: " + Nazwa(rr) + "."; }
            // 3. wczytywanie (dysk pracuje, procesor też) - krótko pełna moc
            else if (kopiuje && p.Cpu > 30) { chce = Tryb.Turbo; why = "Wczytywanie (dysk " + p.DyskMBs.ToString("0") + " MB/s, procesor " + p.Cpu.ToString("0") + " %) – pełna moc, żeby szybciej skończyć."; }
            // 3. gra obciąża kartę, procesor nie: procesor mniej mocy, karta chłodniej i wyższy zegar (wyjście po 3 s - stabilnie)
            else if (p.Gpu > 75 && p.Cpu < 50 || gpuBound && p.Gpu > 60 && (gpuWyjscieN = p.Cpu > 50 ? gpuWyjscieN + 1 : 0) < 3)
            { gpuBound = true; chce = Tryb.Zrownowazony; why = "Program obciąża kartę graficzną (" + p.Gpu.ToString("0") + " %), procesor tylko " + p.Cpu.ToString("0") + " % – procesor nie potrzebuje więcej mocy, karta ma chłodniej."; }
            // 4. duże obciążenie - od razu w górę (pół sekundy wystarczy, bez przycięć)
            else if (wysokoN >= 1) { gpuBound = false; chce = Tryb.Turbo; why = "Duże obciążenie (" + p.Cpu.ToString("0") + " %) – pełna moc od razu."; }
            // 5. 🤖 głosowanie: nauka skutków (Q), profil programu, przewidywanie obciążenia, pora dnia - wagi z trafności
            else if (glos != null) { gpuBound = false; ZAi = true; chce = glos.Value; why = GlosOpis(p.Program, chce); }
            // 5. ręcznie przypisany / wyuczony program
            else if (ZnanyProgram(p.Program, out var v) && v.N >= 30 && Wyuczony(v) != Tryb.Cichy) { gpuBound = false; chce = Wyuczony(v); why = p.Program + " – zapamiętany: zwykle " + v.Cpu.ToString("0") + " %, szczyt " + v.Szczyt.ToString("0") + " % procesora → " + Nazwa(chce) + "."; }
            // 6. spoczynek (dłużej) - cicho; o porze, gdy zwykle pracujesz ciężko, zostaje zapas
            else if (niskoN >= 8) { gpuBound = false; bool zwykle = godzN[DateTime.Now.Hour] > 120 && godziny[DateTime.Now.Hour] > 40; chce = zwykle ? Tryb.Zrownowazony : Tryb.Cichy; why = zwykle ? "Spokój, ale o tej porze zwykle pracujesz ciężko – zostawiam zapas mocy." : "Spoczynek (" + p.Cpu.ToString("0") + " %) – cicho i oszczędnie, zegar do " + CichyGhz.ToString("0.0", Inv) + " GHz."; }
            else { gpuBound = false; chce = Tryb.Zrownowazony; why = "Zwykła praca (" + p.Cpu.ToString("0") + " %)."; }
            // kopiowanie i instalacja: nie zwalniam do Cichego (małe pliki i skanowanie antywirusa potrzebują procesora), ale bez Turbo - mało ciepła
            // dysk w tle (Windows Update, Defender) nie odbiera ciszy programowi, który zwykle potrzebuje Cichego (np. film) - tylko instalacja tak
            bool cichyProgram = (Oceniony(p.Program, out var oc0) && oc0 == Tryb.Cichy) || (ZnanyProgram(p.Program, out var v0) && v0.N >= 30 && Wyuczony(v0) == Tryb.Cichy);
            if (!goraco && !reczny && chce == Tryb.Cichy && (kopiuje && !cichyProgram || instalator != null))
            { chce = Tryb.Zrownowazony; why = instalator != null ? "Instalacja (" + instalator + ") – nie zwalniam do Cichego, żeby nie szarpała." : "Kopiowanie (dysk " + p.DyskMBs.ToString("0") + " MB/s) – nie zwalniam do Cichego."; }
            if (!reczny && p.Bateria && chce == Tryb.Turbo && !(p.Temp < 0 && wysokoN > 3)) { chce = Tryb.Zrownowazony; why += " Na baterii bez Turbo."; }

            // 🚀 uruchamianie / przełączenie programu i pełny rdzeń: nigdy Cichy - otwieranie programu potrzebuje zegara (było: OpenOffice startował na 1,4 GHz, bo zwykle nic nie robi)
            if (p.Program != poprzedniNaWierzchu) { poprzedniNaWierzchu = p.Program; startDo = DateTime.UtcNow.AddSeconds(6); }
            bool startuje = DateTime.UtcNow < startDo, rdzenPelny = p.ProgCpu > 80;
            if (!goraco && !reczny && chce == Tryb.Cichy && (startuje || rdzenPelny))
            { chce = Tryb.Zrownowazony; why = rdzenPelny ? (p.Program + " w pełni obciąża rdzeń – bez Cichego, żeby nie zamulało.") : (p.Program + " – uruchamianie / przełączenie, chwilę pełny zegar, potem " + Nazwa(Tryb.Cichy) + "."); }
            // ⏳ przerwa przed powrotem do Cichego (jak cooldown w CPU Manager v40): po wyjściu z Cichego przez 20 s nie wraca - koniec skakania Cichy ↔ Zrównoważony
            if (Obecny == Tryb.Cichy && chce != Tryb.Cichy) wyjscieCichy = DateTime.UtcNow;
            if (chce == Tryb.Cichy && Obecny != Tryb.Cichy && !goraco && !reczny && (DateTime.UtcNow - wyjscieCichy).TotalSeconds < 20) { Dlaczego = why + " (Cichy za chwilę – przerwa po ostatniej zmianie, bez skakania)"; return Obecny; }
            // w dół powoli: Turbo → Zrównoważony po 3 s, Zrównoważony → Cichy po 8 s; w górę od razu
            if (chce < Obecny && p.Temp < TempLimit)
            {
                double wait = Obecny == Tryb.Turbo ? (turboPowrot ? 15 : 3) : 8;   // Turbo wróciło w ciągu 30 s - trzymane 15 s (bez skakania Zrównoważony ↔ Turbo)
                if ((DateTime.UtcNow - ostZmiana).TotalSeconds < wait && !(p.Temp >= TempLimit)) { Dlaczego = why + " (zmiana za chwilę – bez skakania trybów)"; return Obecny; }
            }
            // samodostrajanie: w Cichym procesor ciągle pełny = za późno w górę (próg niżej); w Turbo ciągle mało = za łatwo (próg wyżej)
            if (Obecny == Tryb.Cichy && p.Cpu > 95) { if (++przycieciaN > 20) { ProgWysoki = Math.Max(50, ProgWysoki - 2); przycieciaN = 0; } } else przycieciaN = 0;
            if (Obecny == Tryb.Turbo && p.Cpu < 30 && !gpuBound) { if (++nadmiarN > 60) { ProgWysoki = Math.Min(85, ProgWysoki + 2); nadmiarN = 0; } } else nadmiarN = 0;
            // ⏳ Turbo: zapamiętane wyjście; powrót w ciągu 30 s = obciążenie skacze wokół progu - następnym razem Turbo trzymane dłużej
            if (chce == Tryb.Turbo && Obecny != Tryb.Turbo) turboPowrot = (DateTime.UtcNow - wyjscieTurbo).TotalSeconds < 30;
            if (Obecny == Tryb.Turbo && chce != Tryb.Turbo) wyjscieTurbo = DateTime.UtcNow;
            Dlaczego = why;
            goracoTeraz = goraco; return Ustal(chce);
        }
        DateTime wyjscieTurbo; bool turboPowrot;
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
        bool wymus;   // wybór trybu / suwaka przez użytkownika - zapis wszystkiego od nowa (także gdy ktoś inny zmienił ustawienia)
        public void Ponow() { zastosowany = false; wymus = true; }   // zmienione ustawienie trybu (np. najniższy zegar Cichego) - zastosuj od nowa przy następnej decyzji
        bool studioZast, goracoTeraz;
        // 🖥 ekran wyłączony (pilotem, 🌙 w odtwarzaczu): zmiana trybu = PowerSetActiveScheme, a to BUDZI monitor (film się skończył → ekran się włączał;
        // pusty pulpit → po minucie „Ekran wyłączony – cicho” → ekran się włączał, 2026-11-24 NUCBOX). Przy zgaszonym ekranie tryb zostaje do jego włączenia (oprócz przegrzania).
        Tryb Ustal(Tryb t) { bool st = StudioProg != null; if (st && t == Tryb.Cichy) t = Tryb.Zrownowazony;   // Studio: niski zegar przy dźwięku = trzaski
            if (EkranWyl && zastosowany && !goracoTeraz && (t != Obecny || st != studioZast)) { Dlaczego += " (ekran wyłączony – zmiana trybu po jego włączeniu, inaczej Windows zapaliłby ekran)"; return Obecny; }
            if (t != Obecny || !zastosowany || st != studioZast) { if (t != Obecny) ostZmiana = DateTime.UtcNow; Obecny = t; studioZast = st; Zastosuj(t); } return t; }

        // ---------- sterowanie ----------
        bool zastosowany;
        static readonly Guid Sub = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        static readonly Guid Max = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec"), Min = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c"),
            Boost = new Guid("be337238-0d82-4146-a960-4f3749d470c7"), Epp = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863"), Freq = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e100");
        // ⚡ szybkość reakcji Windows na obciążenie (próg i czas podnoszenia / obniżania zegara, sposób podnoszenia) i 🎚 parkowanie rdzeni (najmniej czuwających rdzeni, %)
        static readonly Guid IncThr = new Guid("06cadf0e-64ed-448a-8927-ce7bf90eb35d"), DecThr = new Guid("12a0ab44-fe28-4fa9-b3bd-4b64f44960a6"),
            IncTime = new Guid("984cf492-3bed-4488-a8f9-4286c97bf5aa"), DecTime = new Guid("d8edeb9b-95cf-4f95-a73c-b061973693c8"),
            IncPol = new Guid("465e1f50-b610-473a-ab58-00d1077dc418"), MinCores = new Guid("0cc5b647-c1df-4637-891a-dec35c318583"),
            // AMD (CPPC): tryb autonomiczny - procesor sam wybiera zegar i pomija minimum z planu; wyłączany na czas sterowania (przy zamknięciu wraca)
            Autonom = new Guid("8baa4a8a-14c6-4451-8e8b-14bdbd197537"),
            // co ile ms Windows sprawdza obciążenie i zmienia zegar (domyślnie 30); na NUCBOX było 5000 - zegar reagował co 5 s
            PerfCheck = new Guid("4d2b0152-7d5c-498b-88e2-34345392a2c5"),
            // 🧩 procesory hybrydowe (Intel 12.+ gen., Core Ultra): drugi rodzaj rdzeni ma osobne ustawienia („klasa 1”) - bez nich Cichy nie ograniczał rdzeni wydajnych
            Max1 = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ed"), Min1 = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964d"),
            Epp1 = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6864"), Freq1 = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e101");
        [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid guid);
        [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid s, ref Guid sub, ref Guid set, uint v);
        [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid s, ref Guid sub, ref Guid set, uint v);
        [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid s, ref Guid sub, ref Guid set, out uint v);
        [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid s, ref Guid sub, ref Guid set, out uint v);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
        public string Blad = "";

        static Guid Aktywny() { PowerGetActiveScheme(IntPtr.Zero, out var p); try { return (Guid)Marshal.PtrToStructure(p, typeof(Guid)); } finally { LocalFree(p); } }
        static readonly Guid[] Ustawienia = { Max, Min, Boost, Epp, Freq, IncThr, DecThr, IncTime, DecTime, IncPol, MinCores, Autonom, PerfCheck, Max1, Min1, Epp1, Freq1 };
        readonly Dictionary<Guid, (uint Ac, uint Dc)> oryg = new Dictionary<Guid, (uint, uint)>();   // wartości Windows sprzed programu
        static readonly string KopiaFile = Path.Combine(Dir, "windows-przed.txt");

        // ustawienia Windows sprzed pierwszego uruchomienia - przywracane przy zamknięciu programu
        void ZapamietajWindows(Guid s)
        {   // zapis raz; ustawienie dopisane w nowszej wersji programu dochodzi do zapisu przy pierwszym uruchomieniu tej wersji (jeszcze nieruszone)
            var l = new List<string>(); try { if (File.Exists(KopiaFile)) l.AddRange(File.ReadAllLines(KopiaFile)); } catch (Exception) { }
            // użytkownik przełączył plan zasilania: poprzedni plan wraca do swoich wartości (bez przełączania z powrotem), nowy zapamiętany od zera
            if (l.Count > 0 && l[0] != s.ToString()) { PrzywrocWindows(false); l.Clear(); oryg.Clear(); }
            if (l.Count == 0) l.Add(s.ToString());
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
        // ↺ co Velivo zapisało w planie zasilania (GUID → AC, DC): przy przywracaniu wraca tylko to, czego nikt potem nie zmienił; plik zapisywany najwyżej co minutę
        static readonly string ZapisyFile = Path.Combine(Dir, "velivo-zapisy.txt");
        readonly Dictionary<Guid, (uint Ac, uint Dc)> zapisy = new Dictionary<Guid, (uint, uint)>(); bool zapisyZmiana, zapisyWcz; DateTime zapisyAt;
        void WczytajZapisy()
        {
            if (zapisyWcz) return; zapisyWcz = true;
            try { foreach (var x in File.ReadAllLines(ZapisyFile)) { var a = x.Split('\t'); if (a.Length == 3) lock (zapisy) if (!zapisy.ContainsKey(new Guid(a[0]))) zapisy[new Guid(a[0])] = (uint.Parse(a[1]), uint.Parse(a[2])); } } catch (Exception) { }
        }
        public void ZapiszZapisy(bool teraz = false)
        {
            if (!zapisyZmiana || !teraz && (DateTime.UtcNow - zapisyAt).TotalSeconds < 60) return;
            zapisyAt = DateTime.UtcNow; zapisyZmiana = false; WczytajZapisy();
            try { List<string> l; lock (zapisy) l = zapisy.Select(kv => kv.Key + "\t" + kv.Value.Ac + "\t" + kv.Value.Dc).ToList(); Directory.CreateDirectory(Dir); File.WriteAllLines(ZapisyFile, l); } catch (Exception) { }
        }
        public void PrzywrocWindows(bool aktywuj = true)
        {
            try
            {
                var l = File.ReadAllLines(KopiaFile); var s = new Guid(l[0]); var sub = Sub;
                WczytajZapisy();
                foreach (var x in l.Skip(1))
                {
                    var a = x.Split('\t'); var g = new Guid(a[0]); uint oa = uint.Parse(a[1]), od = uint.Parse(a[2]);
                    // ↺ transakcyjnie: wartość, którą Velivo zapisało i której nikt potem nie zmienił - wraca do Windows; zmieniona później (użytkownik, inny program) - zostaje
                    bool znane; (uint Ac, uint Dc) moje; lock (zapisy) znane = zapisy.TryGetValue(g, out moje);
                    bool acOk = !znane || PowerReadACValueIndex(IntPtr.Zero, ref s, ref sub, ref g, out var ca) != 0 || ca == moje.Ac;
                    bool dcOk = !znane || PowerReadDCValueIndex(IntPtr.Zero, ref s, ref sub, ref g, out var cd) != 0 || cd == moje.Dc;
                    if (acOk) PowerWriteACValueIndex(IntPtr.Zero, ref s, ref sub, ref g, oa);
                    if (dcOk) PowerWriteDCValueIndex(IntPtr.Zero, ref s, ref sub, ref g, od);
                }
                if (aktywuj) PowerSetActiveScheme(IntPtr.Zero, ref s); File.Delete(KopiaFile); oryg.Clear();
                lock (zapisy) zapisy.Clear(); try { File.Delete(ZapisyFile); } catch (Exception) { }
            }
            catch (Exception) { }
            try { if (AmdE != null && !AmdE.Dziala) AmdE.Start(); AmdE?.Przywroc(); } catch (Exception) { }   // 🔥 AMD: fabryczne limity tego komputera (deinstalator: odczyt z pliku tej sesji Windows)
            zastosowany = false;
        }

        // ↺ ustawienia domyślne Windows: aktywny plan zasilania wraca do wartości fabrycznych Windows (tylko ten plan), program przestaje sterować;
        // AMD: limity mocy wracają do fabrycznych po ponownym uruchomieniu komputera (do tego czasu pełna moc)
        [DllImport("powrprof.dll")] static extern uint PowerRestoreIndividualDefaultPowerScheme(ref Guid s);
        public string DomyslneWindows()
        {
            Put("tryb", "windows");
            uint e = 1;
            try { var s = Aktywny(); e = PowerRestoreIndividualDefaultPowerScheme(ref s); PowerSetActiveScheme(IntPtr.Zero, ref s); File.Delete(KopiaFile); oryg.Clear(); } catch (Exception) { }
            try { AmdE?.Przywroc(); } catch (Exception) { }
            zastosowany = false;
            return e == 0 ? "Plan zasilania Windows przywrócony do ustawień domyślnych. Velivo Procesor nie steruje (wybierz tryb, aby znów sterował)." + (AmdDziala ? " Moc AMD: fabryczne limity przywrócone." : "")
                : "Nie udało się przywrócić planu zasilania (kod " + e + ").";
        }

        void Zastosuj(Tryb t)
        {
            Blad = "";
            try
            {
                var s = Aktywny(); ZapamietajWindows(s);
                // 🪽 bez obcinania skrzydeł (2026-11-25, audyt): zegar i boost wybiera procesor (Speed Shift / CPPC) we wszystkich trybach - tryb ustawia tylko skłonność (EPP) i boost;
                //    Cichy: boost efektywny + EPP oszczędne (na AMD do tego niższy budżet mocy i temperatury - AmdSilnik); bez stałego minimum zegaru (było 1,4 GHz = pobór przy lekkiej pracy)
                // 🌙 Cichy: zegar najwyżej do suwaka „Zegar w Cichym” i bez boostu (cisza w nocy - wróciło 2026-11-25 na prośbę właściciela); minimum zostaje jak w Windows
                uint maxC = (uint)Math.Max(5, Math.Min(100, Math.Round(CichyGhz * 1000 / Math.Max(1, BazaMhz) * 100)));
                uint max = t == Tryb.Cichy ? maxC : 100;
                // EPP (preferencja energia ↔ wydajność) w Windows: 0–100 % (0 = wydajność, 100 = oszczędność); było 200 (poza zakresem - Windows odrzucał)
                // i 100 w Zrównoważonym (najbardziej oszczędne - Intel ze Speed Shift ociągał się z zegarem). Teraz jak Windows: Zrównoważony 33
                uint boost = t == Tryb.Cichy ? 0u : t == Tryb.Turbo ? 2u : 3u, epp = t == Tryb.Cichy ? 80u : t == Tryb.Turbo ? 0u : 33u;
                uint freq = t == Tryb.Cichy ? (uint)Math.Round(CichyGhz * 1000) : 0u;   // Intel: limit w MHz (0 = bez limitu)
                uint min = Math.Min(max, oryg.TryGetValue(Min, out var om) ? om.Ac : 5u);   // minimum jak w Windows przed programem (bez sztucznej podłogi w Zrównoważonym i Turbo)
                // ⚡ reakcja (próg w %, czas w okresach sprawdzania ~30 ms, podnoszenie: 0 stopniowo, 1 o krok, 2 od razu do pełna): Cichy leniwie (ciszej), Turbo od razu;
                // 🎚 Studio (muzyka): zegar w górę przy najmniejszym obciążeniu, w dół bardzo powoli, wszystkie rdzenie czuwają - bez opóźnień budzenia (trzaski)
                bool st = StudioProg != null;
                var (it, dt, iT, dT, ip) = st ? (10u, 5u, 1u, 10u, 2u) : t == Tryb.Turbo ? (30u, 15u, 1u, 4u, 2u) : t == Tryb.Cichy ? (80u, 50u, 4u, 1u, 1u) : (50u, 25u, 2u, 2u, 0u);
                // Studio + Turbo: dodatkowo stały pełny zegar (miks z wieloma wtyczkami); Studio + Zrównoważony: bez stałego pełnego zegara - chłodniej i ciszej (nagrywanie mikrofonem)
                if (st && t == Tryb.Turbo) { boost = 2; epp = 0; min = 100; }
                else if (st) epp = 25;
                var w = new List<(Guid, uint, uint)> { (Max, max, max), (Min, min, min), (Boost, boost, boost), (Epp, epp, epp), (Freq, freq, freq), (IncThr, it, it), (DecThr, dt, dt), (IncTime, iT, iT), (DecTime, dT, dT), (IncPol, ip, ip) };
                if (st && !oryg.ContainsKey(MinCores)) { var g0 = MinCores; var sb = Sub; if (PowerReadACValueIndex(IntPtr.Zero, ref s, ref sb, ref g0, out var a0) == 0 && PowerReadDCValueIndex(IntPtr.Zero, ref s, ref sb, ref g0, out var d0) == 0) oryg[MinCores] = (a0, d0); }   // po Studio parkowanie wraca do tej wartości
                if (st) w.Add((MinCores, 100, 100)); else if (oryg.TryGetValue(MinCores, out var mc)) w.Add((MinCores, mc.Ac, mc.Dc));   // poza Studio - parkowanie jak w Windows
                // tryb autonomiczny i co ile ms Windows zmienia zegar - zawsze jak przed programem (u właściciela 5000 ms z CPU Manager v40 = cisza; moje 30 ms = skoki zegara i wentylator)
                foreach (var g in new[] { Autonom, PerfCheck }) if (oryg.TryGetValue(g, out var ov)) w.Add((g, ov.Ac, ov.Dc));
                if (Hybrydowy) w.AddRange(new[] { (Max1, max, max), (Min1, min, min), (Epp1, epp, epp), (Freq1, freq, freq) });   // te same granice dla obu rodzajów rdzeni
                uint err = 0;
                foreach (var (g, va, vd) in w) { bool bez; lock (zapisy) bez = !wymus && zapisy.TryGetValue(g, out var by) && by.Ac == va && by.Dc == vd; if (bez) continue;   // bez zapisu, gdy wartość już taka (zasada: bez zbędnych zapisów)
                    var gg = g; var sub = Sub; uint e = PowerWriteACValueIndex(IntPtr.Zero, ref s, ref sub, ref gg, va); PowerWriteDCValueIndex(IntPtr.Zero, ref s, ref sub, ref gg, vd); if (g == Max || g == Min || g == Boost) err |= e; }
                PowerSetActiveScheme(IntPtr.Zero, ref s); wymus = false;
                Zapamietaj(s, w.Select(x => (x.Item1, x.Item2)));   // ⚠ do wykrywania obcych zmian
                lock (zapisy) { foreach (var (g, va, vd) in w) zapisy[g] = (va, vd); zapisyZmiana = true; }   // ↺ co Velivo zmieniło (przywracanie transakcyjne)
                if (err != 0) Blad = "Windows nie przyjął części ustawień zasilania (kod " + err + ").";
                // 🛟 awaria: 3 razy z rzędu Windows odrzuca ustawienia - przestaję sterować (ustawienia Windows), zamiast próbować na siłę
                bladZasilN = err != 0 ? bladZasilN + 1 : 0;
                if (bladZasilN >= 3) { bladZasilN = 0; Put("tryb", "windows"); PrzywrocWindows();   // tylko to, co zmieniło Velivo (przywracanie transakcyjne)
                     Blad = "Windows 3 razy odrzucił ustawienia zasilania – Velivo przestało sterować, działa zarządzanie Windows. Wybierz tryb, aby spróbować znowu."; return; }
            }
            catch (Exception ex) { Blad = ex.Message; }
            if (AmdDziala) { AmdE.Ustaw(t, wymusAmd); wymusAmd = false; if (AmdE.Blad.Length > 0) Blad = AmdE.Blad; }   // 🔥 budżet mocy i temperatury AMD (od fabrycznych)
            zastosowany = true;
        }

        int bladZasilN; bool wymusAmd;
        public void PoWybudzeniu() { wymusAmd = true; zastosowany = false; try { AmdE?.PoWybudzeniu(); } catch (Exception) { } }   // BIOS wraca po uśpieniu do swoich limitów

        public static bool Admin()
        {
            try { using (var id = System.Security.Principal.WindowsIdentity.GetCurrent()) return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); }
            catch (Exception) { return false; }
        }
    }
}
