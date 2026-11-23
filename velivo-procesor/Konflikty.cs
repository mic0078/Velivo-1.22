using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace VelivoProcesor
{
    // ⚠ ochrona przed konfliktem (makieta dokumentacja/makieta-procesor-konflikty.html): inne programy sterujące procesorem (lista znanych)
    // i obce zmiany ustawień zasilania (odczyt tego, co Velivo zapisało). Velivo tylko ostrzega - nie zamyka cudzych programów i nie walczy o ustawienia.
    sealed partial class Silnik
    {
        static readonly (string Proc, string Nazwa)[] Znane =
        {
            ("ArmouryCrate", "Armoury Crate (ASUS)"), ("LenovoVantage", "Lenovo Vantage"), ("MSI.CentralServer", "MSI Center"), ("MSI_Center", "MSI Center"), ("MSICenter", "MSI Center"),
            ("DellPowerManager", "Dell Power Manager"), ("OmenCommandCenter", "Omen Gaming Hub (HP)"), ("OmenGamingHub", "Omen Gaming Hub (HP)"),
            ("ryzen-controller", "Ryzen Controller"), ("RyzenController", "Ryzen Controller"), ("UXTU", "Universal x86 Tuning Utility"), ("Universal x86 Tuning", "Universal x86 Tuning Utility"),
            ("ThrottleStop", "ThrottleStop"), ("XtuService", "Intel XTU"), ("PerfTune", "Intel XTU"), ("ProcessLasso", "Process Lasso"), ("ProcessGovernor", "Process Lasso"),
            ("CPUManager", "CPU Manager"), ("CPU Manager", "CPU Manager"),
        };
        // znane programy sterujące procesorem, które teraz działają (bez wyciszonych)
        public List<string> InneSterujace()
        {
            Procesy(); var cisza = new HashSet<string>((Get("konflikty-cisza") ?? "").Split('|'), StringComparer.OrdinalIgnoreCase);
            var d = dzialajace;
            return Znane.Where(z => d.Any(n => n.StartsWith(z.Proc, StringComparison.OrdinalIgnoreCase))).Select(z => z.Nazwa).Distinct().Where(n => !cisza.Contains(n)).ToList();
        }

        // obce zmiany: co 5 s odczyt aktywnego planu i wartości, które zapisało Velivo; różnica = ktoś inny je zmienił (liczone, bez ponownego zapisu - bez walki)
        Guid zapisanyPlan; readonly Dictionary<Guid, uint> zapisane = new Dictionary<Guid, uint>(); readonly Queue<DateTime> obce = new Queue<DateTime>(); DateTime obceSpr;
        void Zapamietaj(Guid plan, IEnumerable<(Guid G, uint V)> w) { lock (zapisane) { zapisanyPlan = plan; zapisane.Clear(); foreach (var (g, v) in w) if (g == Max || g == Min || g == Boost || g == Epp) zapisane[g] = v; } }   // tylko główne (inne Windows potrafi sam poprawiać - fałszywe alarmy)
        public int ObceZmiany()
        {
            if ((DateTime.UtcNow - obceSpr).TotalSeconds >= 5 && zastosowany && Get("tryb") != "windows" && Get("tryb") != "pauza")
            {
                obceSpr = DateTime.UtcNow; bool inne = false;
                try
                {
                    var s = Aktywny(); var sub = Sub;
                    lock (zapisane)
                    {
                        if (zapisane.Count > 0 && s != zapisanyPlan) inne = true;
                        else foreach (var kv in zapisane) { var g = kv.Key; if (PowerReadACValueIndex(IntPtr.Zero, ref s, ref sub, ref g, out var a) == 0 && a != kv.Value) { inne = true; break; } }
                    }
                }
                catch (Exception) { }
                if (inne) { obce.Enqueue(DateTime.UtcNow); lock (zapisane) { zapisanyPlan = Aktywny(); zapisane.Clear(); } }   // przyjmuję cudzy stan jako punkt odniesienia - liczę następne zmiany, nie nadpisuję
            }
            while (obce.Count > 0 && (DateTime.UtcNow - obce.Peek()).TotalSeconds > 60) obce.Dequeue();
            return obce.Count;
        }

        // 🎚 suwak „Tryb zasilania” Windows 11 (nakładka na plan: zmienia EPP i boost) - na czas sterowania „Zrównoważony”, potem jak był
        [DllImport("powrprof.dll")] static extern uint PowerGetEffectiveOverlayScheme(out Guid g);
        [DllImport("powrprof.dll")] static extern uint PowerSetEffectiveOverlayScheme(ref Guid g);
        public void SuwakW11(bool steruje)
        {
            try
            {
                if (steruje && Get("suwak-w11", "1") == "1")
                {
                    if (PowerGetEffectiveOverlayScheme(out var cur) != 0 || cur == Guid.Empty) return;
                    if (string.IsNullOrEmpty(Get("suwak-przed"))) Put("suwak-przed", cur.ToString());
                    var z = Guid.Empty; PowerSetEffectiveOverlayScheme(ref z);
                }
                else if (Get("suwak-przed") is string p && Guid.TryParse(p, out var g)) { PowerSetEffectiveOverlayScheme(ref g); Put("suwak-przed", ""); }
            }
            catch (Exception) { }
        }
    }
}
