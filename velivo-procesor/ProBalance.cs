using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace VelivoProcesor
{
    // ⚖ ProBalance (jak w Process Lasso): gdy procesor jest mocno zajęty, programy W TLE, które zjadają dużo procesora, dostają na chwilę
    // niższy priorytet i tryb wydajności Windows 11 (EcoQoS: niższy zegar, mniej ciepła) - program, z którym pracujesz (okno na wierzchu),
    // nie przycina się. Wszystko wraca, gdy program się uspokoi, trafi na wierzch, procesor odpocznie albo ProBalance zostanie wyłączony / program zamknięty.
    // Tylko programy z priorytetem Normalny, bez systemu, dźwięku i programów Velivo. Do tego ⬆⬇ priorytet wybrany dla programu (profil), też cofany.
    // Pomiar co 2 s z otwartych raz uchwytów - bez kosztu.
    sealed class ProBalance
    {
        const uint QueryLimited = 0x1000, SetInfo = 0x0200;
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long c, out long e, out long k, out long u);
        [DllImport("kernel32.dll")] static extern uint GetPriorityClass(IntPtr h);
        [DllImport("kernel32.dll")] static extern bool SetPriorityClass(IntPtr h, uint pc);
        [StructLayout(LayoutKind.Sequential)] struct Throttle { public uint Version, ControlMask, StateMask; }
        [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr h, int cls, ref Throttle info, int size);
        const uint Normal = 0x20, BelowNormal = 0x4000, High = 0x80;

        static readonly HashSet<string> Pomin = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Idle", "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "explorer", "audiodg", "fontdrvhost", "ctfmon",
          "MsMpEng", "SearchHost", "ShellExperienceHost", "StartMenuExperienceHost", "TextInputHost", "sihost", "taskhostw", "RuntimeBroker", "vlc", "obs64", "obs32" };

        sealed class Proc { public IntPtr H; public string Name; public long Czas; public int Wysoko, Spokoj; public bool Obnizony, Eco; public uint Profil, Oryg; public double Ost = -1; }
        readonly Dictionary<int, Proc> procs = new Dictionary<int, Proc>();
        readonly int rdzenie = Environment.ProcessorCount; long ostTick; int spokojCalosc;
        public string Opis = ""; public int Liczba;   // co teraz robi - słowami do okna; ile programów w tle uspokojonych (wykres)

        // co 2 s: cpu = obciążenie całego procesora (%), fg = proces okna na wierzchu, prio(nazwa) = 1 Wysoki / -1 Niski / 0 bez zmian, bezPb(nazwa) = nie uspokajać
        public void Krok(bool wlaczony, double cpu, int fg, Func<string, int> prio, Func<string, bool> bezPb)
        {
            long now = Stopwatch.GetTimestamp(); double dt = ostTick == 0 ? 0 : (now - ostTick) / (double)Stopwatch.Frequency; ostTick = now;
            var zywe = new HashSet<int>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    int id = p.Id; string n; try { n = p.ProcessName; } catch (Exception) { continue; }
                    if (id <= 4 || Pomin.Contains(n) || n.StartsWith("Velivo", StringComparison.OrdinalIgnoreCase)) continue;
                    zywe.Add(id);
                    if (!procs.TryGetValue(id, out var x))
                    {
                        var h = OpenProcess(QueryLimited | SetInfo, false, id);
                        x = new Proc { H = h, Name = n }; procs[id] = x;   // bez uprawnień (inny użytkownik, system) - H = 0, pomijany
                    }
                    if (x.H == IntPtr.Zero) continue;
                    // ⬆⬇ priorytet z profilu programu (wraca, gdy profil usunięty)
                    int pr = prio(n); uint chce = pr > 0 ? High : pr < 0 ? BelowNormal : 0;
                    if (chce != 0)
                    {
                        if (x.Obnizony) Przywroc(x);
                        uint jest = GetPriorityClass(x.H);
                        if (jest != chce) { if (x.Profil == 0) x.Oryg = jest; if (SetPriorityClass(x.H, chce)) x.Profil = chce; }
                    }
                    else if (x.Profil != 0) { if (GetPriorityClass(x.H) == x.Profil) SetPriorityClass(x.H, x.Oryg == 0 ? Normal : x.Oryg); x.Profil = 0; }
                    if (!GetProcessTimes(x.H, out _, out _, out var k, out var u)) continue;
                    long t = k + u; double proc = dt > 0 && x.Czas > 0 ? (t - x.Czas) / 1e7 / dt / rdzenie * 100 : 0; x.Czas = t; if (dt > 0) x.Ost = proc;   // % całego procesora
                    if (!wlaczony || chce != 0 || bezPb(n)) { if (x.Obnizony) Przywroc(x); continue; }
                    bool tlo = id != fg;
                    if (x.Obnizony)
                    {
                        x.Spokoj = proc < 3 ? x.Spokoj + 1 : 0;
                        if (!tlo || x.Spokoj >= 3 || spokojCalosc >= 3) Przywroc(x);   // na wierzchu, spokojny 6 s albo procesor odpoczywa
                    }
                    else if (tlo && cpu > 60 && proc > 10)
                    {
                        if (++x.Wysoko >= 2 && GetPriorityClass(x.H) == Normal && SetPriorityClass(x.H, BelowNormal)) { x.Obnizony = true; x.Spokoj = 0; x.Eco = Eco(x.H, true); }
                    }
                    else x.Wysoko = 0;
                }
            }
            spokojCalosc = cpu < 35 ? spokojCalosc + 1 : 0;
            foreach (var id in procs.Keys.Where(i => !zywe.Contains(i)).ToList()) { if (procs[id].H != IntPtr.Zero) CloseHandle(procs[id].H); procs.Remove(id); }
            var ob = procs.Values.Where(x => x.Obnizony).ToList(); var nazwy = ob.Select(x => x.Name).Distinct().ToList();
            Liczba = nazwy.Count;
            Opis = !wlaczony ? "wyłączony" : nazwy.Count == 0 ? "czuwa – nic nie przeszkadza"
                : "w tle uspokojone: " + string.Join(", ", nazwy.Take(4)) + (nazwy.Count > 4 ? " i " + (nazwy.Count - 4) + " inne" : "") + (ob.Any(x => x.Eco) ? " (tryb wydajności Windows + niższy priorytet)" : " (niższy priorytet)");
        }

        // 🎓 do nauki programów: obciążenie samego procesu na wierzchu w % jednego rdzenia (program liczący jednym wątkiem = 100 % - jemu też pomaga Turbo); -1 = nieznane
        public double RdzenProcesu(int pid) => procs.TryGetValue(pid, out var x) && x.Ost >= 0 ? Math.Min(100, x.Ost * rdzenie) : -1;

        // tryb wydajności Windows 11 (EcoQoS): włączony = niższy zegar dla programu; wyłączony = Windows decyduje jak zwykle. Windows 10 - nic (zostaje sam priorytet)
        static bool Eco(IntPtr h, bool on)
        {
            var st = new Throttle { Version = 1, ControlMask = on ? 1u : 0u, StateMask = on ? 1u : 0u };
            try { return SetProcessInformation(h, 4, ref st, Marshal.SizeOf(typeof(Throttle))); } catch (Exception) { return false; }
        }
        void Przywroc(Proc x) { if (GetPriorityClass(x.H) == BelowNormal) SetPriorityClass(x.H, Normal); if (x.Eco) Eco(x.H, false); x.Obnizony = false; x.Eco = false; x.Wysoko = 0; }
        public void PrzywrocWszystkie()
        {
            foreach (var x in procs.Values.Where(x => x.H != IntPtr.Zero))
                try { if (x.Obnizony) Przywroc(x); if (x.Profil != 0) { if (GetPriorityClass(x.H) == x.Profil) SetPriorityClass(x.H, x.Oryg == 0 ? Normal : x.Oryg); x.Profil = 0; } } catch (Exception) { }
        }
    }
}
