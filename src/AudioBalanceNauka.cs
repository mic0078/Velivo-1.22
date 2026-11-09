using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Velivo
{
    // 🧠 Audio Balance uczy się sam (bez AI - pamięć pomiarów i Twoich poprawek), osobno dla każdego kanału TV / filmu:
    // 1) średnia głośność źródła - z całych seansów (mediana seansu, waga = długość, AbLearnMath), znane źródło startuje od razu z właściwym poziomem; 2) reklamy - gdy kanał ma głośne skoki, ściszanie 2× szybsze;
    // 3) Twoja głośność - upodobanie źródła względem Twojej głośności bazowej (średnia z seansów, do ±6 dB), stosowana tylko brakująca część.
    public partial class MainWindow
    {
        static string AbLearnFile => System.IO.Path.Combine(DataDir, "audio-nauka.json");   // właściwość: DataDir z innego pliku może nie być jeszcze ustawione przy inicjalizacji statycznej
        static Dictionary<string, AbLearnEntry> _abLearn;
        static readonly object AbLearnLock = new object();
        string _abLearnSrc; bool _abLearnTv; DateTime _abLearnStart, _abLearnChunk; double _abLearnVolNow = -1, _abLearnEff; int _abLearnSamples; readonly List<float> _abLearnSess = new List<float>(), _abLearnVols = new List<float>();
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
        void AbLearnBegin(string name, bool tv)
        {
            AbLearnEnd();
            _abLearnSrc = string.IsNullOrWhiteSpace(name) ? null : name.Trim(); _abLearnTv = tv; _abLearnSess.Clear(); _abLearnVols.Clear();
            _abLearnStart = _abLearnChunk = DateTime.Now; _abLearnSamples = 0; AbProc.FastMinDb = 0;
            AbLearnEntry e = null;
            if (AbLearnOn && _abLearnSrc != null) lock (AbLearnLock) AbLearnData().TryGetValue(_abLearnSrc, out e);
            // start ze ŚREDNIEJ filmu (punkt odniesienia - średnia, ze strefą komfortu i intensywnością), nie ze średniej chwilowych podbić
            // korekta źródła względem Twojej głośności bazowej - tylko brakująca część (suwak, który już podniosłeś, nie liczy się drugi raz)
            _abLearnEff = e != null && e.AdjW > 0 ? AbLearnMath.EffAdj(e.Adj, AbLearnMath.VolDb(_abLearnVolNow, AbVolBase)) : 0;
            AbProc.SeedLevDb = float.NaN;
            if (e != null && e.Avg < -5 && e.W > 0 && AbProc.Profile is AbProfile pr)
            {
                double tg = (AbProc.Target >= -0.5 ? -23 : AbProc.Target) + _abLearnEff, dv = tg - e.Avg; dv = Math.Sign(dv) * Math.Max(0, Math.Abs(dv) - pr.Dead);
                AbProc.SeedLevDb = (float)Math.Max(-pr.MaxCut, Math.Min(pr.MaxBoost, pr.Amount * dv));
            }
            AbProc.FastMul = e != null && e.Ad <= -6 ? 2 : 1;
            AbProc.TargetAdj = (float)_abLearnEff;
            if (_abLearnTimer == null)
            {
                _abLearnTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _abLearnTimer.Tick += (s, a) => AbLearnSample();
            }
            _abLearnTimer.Start();
        }

        // co sekundę (od 3. sekundy źródła): poziom wyrównania, reklamy i Twoja głośność - zapis do pamięci od razu, na dysk co 10 s
        bool _abLearnDirty; DateTime _abLearnSaved;
        void AbLearnSample()
        {
            if (_abLearnSrc == null || _abOut == null || !AbProc.Active || AbProc.Bypass || (DateTime.Now - _abLearnStart).TotalSeconds < 3) return;
            double ad = AbProc.FastMinDb, st = AbProc.In.ShortTerm; AbProc.FastMinDb = 0;
            if (double.IsNaN(ad)) return;
            if (st > -60 && !double.IsInfinity(st)) _abLearnSess.Add((float)st);   // pomiar seansu (3 s, co sekundę) - cisza i pauzy pominięte
            if (_abLearnVolNow > 0) _abLearnVols.Add((float)_abLearnVolNow);   // Twoja głośność w seansie (wyciszenie pominięte)
            _abLearnSamples++;
            if (!AbLearnOn) return;
            lock (AbLearnLock)
            {
                var d = AbLearnData();
                if (!d.TryGetValue(_abLearnSrc, out var e)) d[_abLearnSrc] = e = new AbLearnEntry();
                e.T = DateTime.Now;
                // reklamy: głęboki skok zapamiętany od razu, bez skoków powoli zapominany (ok. 10 min)
                e.Ad = ad < e.Ad ? e.Ad * 0.5 + ad * 0.5 : e.Ad * 0.998;
                AbProc.FastMul = e.Ad <= -6 ? 2 : 1;   // od razu, także w trakcie tego kanału
            }
            if ((DateTime.Now - _abLearnChunk).TotalMinutes >= 10) AbLearnCommit();   // długi seans: co 10 min część do historii (awaria / wyłączenie nie gubi całego seansu)
            _abLearnDirty = true;
            if ((DateTime.Now - _abLearnSaved).TotalSeconds >= 10) { _abLearnSaved = DateTime.Now; _abLearnDirty = false; AbLearnSave(); }
        }

        // koniec źródła: zapis tego, czego jeszcze nie zapisano
        // seans (albo jego 10-minutowa część) do historii źródła: mediana seansu, waga = czas oglądania
        void AbLearnCommit()
        {
            double min = (DateTime.Now - _abLearnChunk).TotalMinutes, med = AbLearnMath.Median(_abLearnSess), vol = _abLearnVols.Count > 0 ? _abLearnVols.Average() : -1;
            _abLearnChunk = DateTime.Now; _abLearnSess.Clear(); _abLearnVols.Clear();
            if (_abLearnSrc == null || !AbLearnOn) return;
            lock (AbLearnLock)
            {
                if (!AbLearnData().TryGetValue(_abLearnSrc, out var e)) return;
                if (e.W <= 0 && e.Avg < -5) e.W = 1;   // zapis ze starszej wersji: średnia bez wagi - liczy się jak krótki seans
                var (na, nw) = AbLearnMath.Merge(e.W > 0 ? e.Avg : double.NaN, e.W, med, min, _abLearnTv);
                if (nw != e.W || na != e.Avg) { e.Avg = na; e.W = nw; e.N++; _abLearnDirty = true; }
                if (vol > 0 && min >= AbLearnMath.MinSessionMin)
                {
                    // korekta źródła: Twoje upodobanie względem głośności bazowej (zastosowana korekta + Twój suwak) - wpis zastępuje się średnią, nie dodaje
                    double bv = AbVolBase, pref = AbLearnMath.SessionPref(_abLearnEff, AbLearnMath.VolDb(vol, bv));
                    (e.Adj, e.AdjW) = AbLearnMath.MergeW(e.AdjW > 0 ? e.Adj : 0, e.AdjW, pref, min, AbLearnMath.CapTv);
                    // głośność bazowa: Twój zwykły poziom ze wszystkich seansów (powoli, do 600 min historii)
                    var (nb, nbw) = AbLearnMath.MergeW(bv, AbVolBaseW, vol, min, AbLearnMath.CapBase);
                    SetPref("ab-vol-baza", nb.ToString(System.Globalization.CultureInfo.InvariantCulture)); SetPref("ab-vol-bazaw", nbw.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    _abLearnDirty = true;
                }
            }
        }

        void AbLearnEnd()
        {
            _abLearnTimer?.Stop();
            AbLearnCommit();
            if (_abLearnDirty) { _abLearnDirty = false; AbLearnSave(); }
            _abLearnSrc = null;
        }

        // suwak głośności (ręcznie, Pilot, kontroler) - nie ściszanie wyłącznika czasowego
        internal void AbLearnUserVolume(double v) { _abLearnVolNow = v; }
        // Twoja głośność bazowa (zwykły poziom, %) i waga jej historii (min); przed pierwszym seansem = obecny suwak
        double AbVolBase => double.TryParse(GetPref("ab-vol-baza"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b) && b > 0 ? b : _abLearnVolNow > 0 ? _abLearnVolNow : 100;
        static double AbVolBaseW => double.TryParse(GetPref("ab-vol-bazaw"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : 0;

        internal static string AbLearnInfo(string src)
        {
            if (string.IsNullOrWhiteSpace(src) || !AbLearnOn) return "";
            AbLearnEntry e; lock (AbLearnLock) if (!AbLearnData().TryGetValue(src.Trim(), out e)) return L.T("🧠 Nowe źródło – uczę się");
            return L.T("🧠 Znam to źródło: ") + (e.Avg < -5 && e.W > 0 ? L.T("średnia ") + e.Avg.ToString("0.0") + " LUFS (" + e.N + L.T(" seans.") + ")" : L.T("uczę się – średnia po pierwszym seansie")) + (e.Ad <= -6 ? L.T(", głośne reklamy – ściszam szybciej") : "") + (e.AdjW > 0 && Math.Abs(e.Adj) >= 0.5 ? L.T(", Twoja poprawka ") + (e.Adj >= 0 ? "+" : "") + e.Adj.ToString("0.0") + " dB" : "");
        }
    }
}
