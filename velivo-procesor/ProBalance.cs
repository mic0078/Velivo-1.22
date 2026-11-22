using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace VelivoProcesor
{
    // ⚖ ProBalance (jak w Process Lasso): gdy procesor jest mocno zajęty, programy W TLE, które zjadają dużo procesora, dostają na chwilę
    // niższy priorytet - program, z którym pracujesz (okno na wierzchu), nie przycina się. Priorytet wraca, gdy program się uspokoi,
    // trafi na wierzch, procesor odpocznie albo ProBalance zostanie wyłączony / program zamknięty. Tylko programy z priorytetem Normalny,
    // bez systemu, dźwięku i programów Velivo (odtwarzacz w tle nie może się zacinać). Pomiar co 2 s z otwartych raz uchwytów - bez kosztu.
    sealed class ProBalance
    {
        const uint QueryLimited = 0x1000, SetInfo = 0x0200;
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long c, out long e, out long k, out long u);
        [DllImport("kernel32.dll")] static extern uint GetPriorityClass(IntPtr h);
        [DllImport("kernel32.dll")] static extern bool SetPriorityClass(IntPtr h, uint pc);
        const uint Normal = 0x20, BelowNormal = 0x4000;

        static readonly HashSet<string> Pomin = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Idle", "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "explorer", "audiodg", "fontdrvhost", "ctfmon",
          "MsMpEng", "SearchHost", "ShellExperienceHost", "StartMenuExperienceHost", "TextInputHost", "sihost", "taskhostw", "RuntimeBroker", "vlc", "obs64", "obs32" };

        sealed class Proc { public IntPtr H; public string Name; public long Czas; public int Wysoko, Spokoj; public bool Obnizony; }
        readonly Dictionary<int, Proc> procs = new Dictionary<int, Proc>();
        readonly int rdzenie = Environment.ProcessorCount; long ostTick; int spokojCalosc;
        public string Opis = ""; public int Liczba;   // ile programów w tle uspokojonych (wykres)   // co teraz robi - słowami do okna

        // co 2 s: cpu = obciążenie całego procesora (%), fg = proces okna na wierzchu
        public void Krok(bool wlaczony, double cpu, int fg)
        {
            if (!wlaczony) { if (procs.Values.Any(x => x.Obnizony)) PrzywrocWszystkie(); Opis = "wyłączony"; Liczba = 0; return; }
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
                    if (x.H == IntPtr.Zero || !GetProcessTimes(x.H, out _, out _, out var k, out var u)) continue;
                    long t = k + u; double proc = dt > 0 && x.Czas > 0 ? (t - x.Czas) / 1e7 / dt / rdzenie * 100 : 0; x.Czas = t;   // % całego procesora
                    bool tlo = id != fg;
                    if (x.Obnizony)
                    {
                        x.Spokoj = proc < 3 ? x.Spokoj + 1 : 0;
                        if (!tlo || x.Spokoj >= 3 || spokojCalosc >= 3) Przywroc(x);   // na wierzchu, spokojny 6 s albo procesor odpoczywa
                    }
                    else if (tlo && cpu > 60 && proc > 10)
                    {
                        if (++x.Wysoko >= 2 && GetPriorityClass(x.H) == Normal && SetPriorityClass(x.H, BelowNormal)) { x.Obnizony = true; x.Spokoj = 0; }
                    }
                    else x.Wysoko = 0;
                }
            }
            spokojCalosc = cpu < 35 ? spokojCalosc + 1 : 0;
            foreach (var id in procs.Keys.Where(i => !zywe.Contains(i)).ToList()) { if (procs[id].H != IntPtr.Zero) CloseHandle(procs[id].H); procs.Remove(id); }
            var ob = procs.Values.Where(x => x.Obnizony).Select(x => x.Name).Distinct().ToList();
            Liczba = ob.Count;
            Opis = ob.Count == 0 ? "czuwa – nic nie przeszkadza" : "w tle uspokojone: " + string.Join(", ", ob.Take(4)) + (ob.Count > 4 ? " i " + (ob.Count - 4) + " inne" : "");
        }

        void Przywroc(Proc x) { if (GetPriorityClass(x.H) == BelowNormal) SetPriorityClass(x.H, Normal); x.Obnizony = false; x.Wysoko = 0; }
        public void PrzywrocWszystkie() { foreach (var x in procs.Values.Where(x => x.Obnizony && x.H != IntPtr.Zero)) try { Przywroc(x); } catch (Exception) { } }
    }
}
