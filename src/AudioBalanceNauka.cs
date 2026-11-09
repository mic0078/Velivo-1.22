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
        sealed class AbLearnEntry { public double Lev { get; set; } public double Ad { get; set; } public double Adj { get; set; } public int N { get; set; } public DateTime T { get; set; } }
        static readonly string AbLearnFile = System.IO.Path.Combine(DataDir, "audio-nauka.json");
        static Dictionary<string, AbLearnEntry> _abLearn;
        static readonly object AbLearnLock = new object();
        string _abLearnSrc; DateTime _abLearnStart; double _abLearnVol0 = -1, _abLearnVolNow = -1; int _abLearnSamples; double _abLearnAdMin;
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

        internal static void AbLearnForget() { lock (AbLearnLock) { _abLearn = new Dictionary<string, AbLearnEntry>(); } AbLearnSave(); AbProc.SeedLevDb = float.NaN; AbProc.FastMul = 1; AbProc.TargetAdj = 0; }

        internal static int AbLearnCount { get { lock (AbLearnLock) return AbLearnData().Count; } }

        // przed NewMedia: zamknij naukę poprzedniego źródła i ustaw to, co wiadomo o nowym
        void AbLearnBegin(string name)
        {
            AbLearnEnd();
            _abLearnSrc = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            _abLearnStart = DateTime.Now; _abLearnVol0 = _abLearnVolNow; _abLearnSamples = 0; _abLearnAdMin = 0; AbProc.FastMinDb = 0;
            AbLearnEntry e = null;
            if (AbLearnOn && _abLearnSrc != null) lock (AbLearnLock) AbLearnData().TryGetValue(_abLearnSrc, out e);
            AbProc.SeedLevDb = e != null && e.N > 0 ? (float)e.Lev : float.NaN;
            AbProc.FastMul = e != null && e.Ad <= -6 ? 2 : 1;
            AbProc.TargetAdj = e != null ? (float)e.Adj : 0;
            if (_abLearnTimer == null)
            {
                _abLearnTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                _abLearnTimer.Tick += (s, a) => AbLearnSample();
            }
            _abLearnTimer.Start();
        }

        // co 30 s: poziom wyrównania i najgłębsze chwilowe ściszenie (tylko gdy dźwięk naprawdę idzie przez Audio Balance)
        void AbLearnSample()
        {
            if (_abLearnSrc == null || _abOut == null || !AbProc.Active || AbProc.Bypass || (DateTime.Now - _abLearnStart).TotalSeconds < 20) return;
            double lev = AbProc.LevDb, ad = AbProc.FastMinDb; AbProc.FastMinDb = 0;
            if (double.IsNaN(lev) || double.IsNaN(ad)) return;
            _abLearnSamples++; _abLearnAdMin = Math.Min(_abLearnAdMin, ad);
            if (!AbLearnOn) return;
            lock (AbLearnLock)
            {
                var d = AbLearnData();
                if (!d.TryGetValue(_abLearnSrc, out var e)) d[_abLearnSrc] = e = new AbLearnEntry { Lev = lev };
                e.Lev = e.N == 0 ? lev : e.Lev * 0.7 + lev * 0.3; e.N++; e.T = DateTime.Now;
            }
        }

        // koniec źródła: reklamy (najgłębsze ściszenie) i Twoja zmiana głośności na tym źródle
        void AbLearnEnd()
        {
            _abLearnTimer?.Stop();
            if (_abLearnSrc == null || !AbLearnOn || _abLearnSamples < 2) { _abLearnSrc = null; return; }
            lock (AbLearnLock)
            {
                if (AbLearnData().TryGetValue(_abLearnSrc, out var e))
                {
                    e.Ad = e.Ad * 0.6 + _abLearnAdMin * 0.4;
                    if (_abLearnVol0 > 0 && _abLearnVolNow > 0 && Math.Abs(_abLearnVolNow - _abLearnVol0) >= 3)
                        e.Adj = Math.Max(-6, Math.Min(6, e.Adj + 0.5 * 20 * Math.Log10(_abLearnVolNow / _abLearnVol0)));
                    e.T = DateTime.Now;
                }
            }
            _abLearnSrc = null; AbLearnSave();
        }

        // suwak głośności (ręcznie, Pilot, kontroler) - nie ściszanie wyłącznika czasowego
        internal void AbLearnUserVolume(double v) { if (_abLearnVol0 < 0) _abLearnVol0 = v; _abLearnVolNow = v; }

        internal static string AbLearnInfo(string src)
        {
            if (string.IsNullOrWhiteSpace(src) || !AbLearnOn) return "";
            AbLearnEntry e; lock (AbLearnLock) if (!AbLearnData().TryGetValue(src.Trim(), out e)) return L.T("🧠 Nowe źródło – uczę się");
            return L.T("🧠 Znam to źródło: start ") + (e.Lev >= 0 ? "+" : "") + e.Lev.ToString("0.0") + " dB" + (e.Ad <= -6 ? L.T(", głośne reklamy – ściszam szybciej") : "") + (Math.Abs(e.Adj) >= 0.5 ? L.T(", Twoja poprawka ") + (e.Adj >= 0 ? "+" : "") + e.Adj.ToString("0.0") + " dB" : "");
        }
    }
}
