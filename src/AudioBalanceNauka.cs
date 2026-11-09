using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Velivo
{
    // 🧠 Audio Balance uczy się sam (bez AI - pamięć pomiarów i Twoich poprawek), osobno dla każdego kanału TV / filmu:
    // 1) poziom wyrównania - kanał startuje od razu z właściwym poziomem; 2) reklamy - gdy kanał ma głośne skoki, ściszanie 2× szybsze;
    // 3) Twoja głośność - gdy na tym źródle podgłaśniasz/ściszasz, cel tego źródła przesuwa się o połowę zmiany (do ±6 dB).
    public partial class MainWindow
    {
        sealed class AbLearnEntry { public double Lev { get; set; } public double Ad { get; set; } public double Adj { get; set; } public double Avg { get; set; } public int N { get; set; } public DateTime T { get; set; } }
        static string AbLearnFile => System.IO.Path.Combine(DataDir, "audio-nauka.json");   // właściwość: DataDir z innego pliku może nie być jeszcze ustawione przy inicjalizacji statycznej
        static Dictionary<string, AbLearnEntry> _abLearn;
        static readonly object AbLearnLock = new object();
        string _abLearnSrc; DateTime _abLearnStart; double _abLearnVol0 = -1, _abLearnVolNow = -1; int _abLearnSamples;
        System.Windows.Threading.DispatcherTimer _abLearnTimer;
        internal static bool AbLearnOn => GetPref("ab-nauka") != "0";

        static Dictionary<string, AbLearnEntry> AbLearnData()
        {
            lock (AbLearnLock)
            {
                if (_abLearn != null) return _abLearn;
                try { _abLearn = System.IO.File.Exists(AbLearnFile) ? JsonSerializer.Deserialize<Dictionary<string, AbLearnEntry>>(System.IO.File.ReadAllText(AbLearnFile)) : null; } catch (Exception ex) { App.LogError(ex); }
                return _abLearn ??= new Dictionary<string, AbLearnEntry>();
            }
        }

        static void AbLearnSave()
        {
            string json;
            lock (AbLearnLock)
            {
                var d = AbLearnData();
                foreach (var k in d.OrderByDescending(x => x.Value.T).Skip(400).Select(x => x.Key).ToList()) d.Remove(k);   // limit: 400 ostatnich źródeł
                json = JsonSerializer.Serialize(d);
            }
            System.Threading.Tasks.Task.Run(() => { try { VelivoData.SafeWrite(AbLearnFile, json); } catch (Exception ex) { App.LogError(ex); } });
        }

        internal static void AbLearnForget() { lock (AbLearnLock) { _abLearn = new Dictionary<string, AbLearnEntry>(); } AbLearnSave(); AbProc.SeedLevDb = float.NaN; AbProc.FastMul = 1; AbProc.TargetAdj = 0; AbProc.SeedAvg = float.NaN; }

        internal static int AbLearnCount { get { lock (AbLearnLock) return AbLearnData().Count; } }

        // przed NewMedia: zamknij naukę poprzedniego źródła i ustaw to, co wiadomo o nowym
        void AbLearnBegin(string name)
        {
            AbLearnEnd();
            _abLearnSrc = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            _abLearnStart = DateTime.Now; _abLearnVol0 = _abLearnVolNow; _abLearnSamples = 0; AbProc.FastMinDb = 0;
            AbLearnEntry e = null;
            if (AbLearnOn && _abLearnSrc != null) lock (AbLearnLock) AbLearnData().TryGetValue(_abLearnSrc, out e);
            // start ze ŚREDNIEJ filmu (punkt odniesienia - średnia, ze strefą komfortu i intensywnością), nie ze średniej chwilowych podbić
            AbProc.SeedLevDb = float.NaN;
            if (e != null && e.Avg < -5 && AbProc.Profile is AbProfile pr)
            {
                double tg = (AbProc.Target >= -0.5 ? -23 : AbProc.Target) + e.Adj, dv = tg - e.Avg; dv = Math.Sign(dv) * Math.Max(0, Math.Abs(dv) - pr.Dead);
                AbProc.SeedLevDb = (float)Math.Max(-pr.MaxCut, Math.Min(pr.MaxBoost, pr.Amount * dv));
            }
            AbProc.FastMul = e != null && e.Ad <= -6 ? 2 : 1;
            AbProc.TargetAdj = e != null ? (float)e.Adj : 0;
            AbProc.SeedAvg = e != null && e.Avg < -5 ? (float)e.Avg : float.NaN;
            if (_abLearnTimer == null)
            {
                _abLearnTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _abLearnTimer.Tick += (s, a) => AbLearnSample();
            }
            _abLearnTimer.Start();
        }

        // co sekundę (od 3. sekundy źródła): poziom wyrównania, reklamy i Twoja głośność - zapis do pamięci od razu, na dysk co 10 s
        bool _abLearnDirty; DateTime _abLearnSaved, _abLearnVolAt;
        void AbLearnSample()
        {
            if (_abLearnSrc == null || _abOut == null || !AbProc.Active || AbProc.Bypass || (DateTime.Now - _abLearnStart).TotalSeconds < 3) return;
            double lev = AbProc.LevDb, ad = AbProc.FastMinDb; AbProc.FastMinDb = 0;
            if (double.IsNaN(lev) || double.IsNaN(ad)) return;
            _abLearnSamples++;
            if (!AbLearnOn) return;
            lock (AbLearnLock)
            {
                var d = AbLearnData();
                if (!d.TryGetValue(_abLearnSrc, out var e)) d[_abLearnSrc] = e = new AbLearnEntry { Lev = lev };
                double ig = AbProc.In.Integrated; if (ig > -70) e.Avg = e.Avg >= -5 ? ig : e.Avg * 0.9 + ig * 0.1;   // średnia głośność źródła (Auto)
                e.Lev = e.N == 0 ? lev : e.Lev * 0.9 + lev * 0.1; e.N++; e.T = DateTime.Now;   // ~10 s pamięci, od pierwszej próbki
                // reklamy: głęboki skok zapamiętany od razu, bez skoków powoli zapominany (ok. 10 min)
                e.Ad = ad < e.Ad ? e.Ad * 0.5 + ad * 0.5 : e.Ad * 0.998;
                AbProc.FastMul = e.Ad <= -6 ? 2 : 1;   // od razu, także w trakcie tego kanału
                // Twoja głośność: zmiana trzymana 3 s = poprawka tego źródła (użyta przy jego następnym włączeniu - teraz już słyszysz swoją głośność)
                if (_abLearnVol0 > 0 && _abLearnVolNow > 0 && Math.Abs(_abLearnVolNow - _abLearnVol0) >= 3 && (DateTime.Now - _abLearnVolAt).TotalSeconds >= 3)
                {
                    e.Adj = Math.Max(-6, Math.Min(6, e.Adj + 0.5 * 20 * Math.Log10(_abLearnVolNow / _abLearnVol0)));
                    _abLearnVol0 = _abLearnVolNow;
                }
            }
            _abLearnDirty = true;
            if ((DateTime.Now - _abLearnSaved).TotalSeconds >= 10) { _abLearnSaved = DateTime.Now; _abLearnDirty = false; AbLearnSave(); }
        }

        // koniec źródła: zapis tego, czego jeszcze nie zapisano
        void AbLearnEnd()
        {
            _abLearnTimer?.Stop();
            if (_abLearnDirty) { _abLearnDirty = false; AbLearnSave(); }
            _abLearnSrc = null;
        }

        // suwak głośności (ręcznie, Pilot, kontroler) - nie ściszanie wyłącznika czasowego
        internal void AbLearnUserVolume(double v) { if (_abLearnVol0 < 0) _abLearnVol0 = v; if (Math.Abs(v - _abLearnVolNow) > 0.01) _abLearnVolAt = DateTime.Now; _abLearnVolNow = v; }

        internal static string AbLearnInfo(string src)
        {
            if (string.IsNullOrWhiteSpace(src) || !AbLearnOn) return "";
            AbLearnEntry e; lock (AbLearnLock) if (!AbLearnData().TryGetValue(src.Trim(), out e)) return L.T("🧠 Nowe źródło – uczę się");
            return L.T("🧠 Znam to źródło: ") + (e.Avg < -5 ? L.T("średnia ") + e.Avg.ToString("0.0") + " LUFS" : L.T("uczę się")) + (e.Ad <= -6 ? L.T(", głośne reklamy – ściszam szybciej") : "") + (Math.Abs(e.Adj) >= 0.5 ? L.T(", Twoja poprawka ") + (e.Adj >= 0 ? "+" : "") + e.Adj.ToString("0.0") + " dB" : "");
        }
    }
}
