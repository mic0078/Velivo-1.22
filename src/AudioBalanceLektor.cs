using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Velivo
{
    // 🗣 Doradca lektora (lokalnie, bez chmury i bez modelu AI): ocenia słyszalność lektora (AbLektorAdvisor) i pamięta Twoje ustawienia
    // dla każdego głosu. NICZEGO nie zmienia sam - każda propozycja czeka na „Zastosuj” w Audio Monitorze. Uczy się Twoich przyzwyczajeń.
    public partial class MainWindow
    {
        sealed class LektorVoicePref { public double Vol { get; set; } public int Duck { get; set; } public int N { get; set; } public DateTime T { get; set; } }
        static readonly AbLektorAdvisor _abLek = new AbLektorAdvisor();
        static Dictionary<string, LektorVoicePref> _abLekPrefs;
        static string AbLekPrefsFile => System.IO.Path.Combine(DataDir, "lektor-glosy2.json");   // nowy plik: stary zapis nie miał „Film podczas czytania” (odczyt dawał 0% = film wyciszony przy każdej kwestii)
        internal static string AbLekSugText; static Action AbLekSugApply; static string AbLekSugKey; static string AbLekApplied;   // AbLekApplied - co zastosowano (pokazywane do nowej oceny)   // bieżąca propozycja (czeka na Twoją decyzję)
        static readonly HashSet<string> AbLekDismissed = new HashSet<string>();                         // „Nie teraz” - ta sama propozycja nie wraca w tym uruchomieniu
        internal static Action AbLektorUiRefresh;   // odtwarzacz: suwaki lektora z ustawień (po „Zastosuj”)

        static Dictionary<string, LektorVoicePref> AbLekPrefs()
        {
            if (_abLekPrefs != null) return _abLekPrefs;
            try { _abLekPrefs = System.IO.File.Exists(AbLekPrefsFile) ? JsonSerializer.Deserialize<Dictionary<string, LektorVoicePref>>(System.IO.File.ReadAllText(AbLekPrefsFile), AbLearnEntry.Json) : null; } catch (Exception ex) { App.LogError(ex); }
            return _abLekPrefs ??= new Dictionary<string, LektorVoicePref>();
        }

        static string AbLekVoiceName(string tag) => string.IsNullOrEmpty(tag) ? L.T("automatyczny") : tag.StartsWith("piper:") ? L.T(PiperVoices.FirstOrDefault(v => "piper:" + v.Id == tag)?.Label ?? tag.Substring(6)) : tag.StartsWith("win:") ? tag.Substring(4) : tag;

        // nowy film / kanał - ocena od nowa (propozycja zostaje tylko, gdy dotyczy głosu)
        internal static void AbLektorReset() { _abLek.Reset(); if (AbLekSugKey != null && !AbLekSugKey.StartsWith("voice|")) { AbLekSugText = null; AbLekSugApply = null; AbLekSugKey = null; } }

        // jedna kwestia lektora (wołane z lektora): pomiar → ocena → ewentualnie propozycja
        void AbLektorCue(double lekBandDb, double lekVol, double duck)
        {
            if (AbProc.OutSpeechBandDb < -60 || !AbFlowing()) return;   // film akurat cichy / pauza - porównanie bez sensu (było: „+62 dB”)
            double m = AbLektorAdvisor.CueMargin(lekBandDb, lekVol, AbProc.OutSpeechBandDb, duck, AbProc.InDialogShare);
            _abLek.Add(m);
            int up = _abLek.SuggestOverUp(); if (up <= 0 || AbLekSugText != null) return;
            string src = _abOut?.Source ?? "";
            // propozycja: mocniej ściszać film podczas czytania (Twój suwak „Film podczas czytania”) - o tyle dB, ile brakuje; najniżej 10%
            int cur = _settings.VlcLectorDuck >= 100 ? 100 : _settings.VlcLectorDuck, to = Math.Max(10, (int)Math.Round(cur * Math.Pow(10, -up / 20.0) / 5) * 5);
            if (to < cur) { Propose("duck|" + src + "|" + to, L.T("Lektor często ginie przy tym filmie (przewaga w paśmie mowy ") + _abLek.Margin.ToString("+0.0;-0.0") + L.T(" dB). Ściszać film podczas czytania z ") + cur + L.T("% do ") + to + "%?", () => { _settings.VlcLectorDuck = to; }); return; }
            if (_settings.VlcLectorVolume >= 0.98) Propose("vol|" + src, L.T("Lektor ginie, choć film jest już maksymalnie ściszony. Spróbuj wyraźniejszego głosu (panel lektora)."), null);
            else { double nv = Math.Min(0.99, _settings.VlcLectorVolume + 0.15); Propose("vol|" + src, L.T("Lektor ginie, a filmu nie da się już bardziej ściszyć. Podnieść głośność lektora do ") + (int)Math.Round(nv * 100) + "%?", () => { _settings.VlcLectorVolume = nv; }); }
        }

        static DateTime AbLekLastAsk = DateTime.MinValue; static readonly HashSet<string> AbLekAskedSrc = new HashSet<string>();
        static void Propose(string key, string text, Action apply)
        {
            if (AbLekDismissed.Contains(key)) return;
            // bez nachalności: najwyżej jedna propozycja na film / kanał (nie dotyczy głosu) i co najmniej 10 min między propozycjami
            var parts = key.Split('|'); string srcKey = parts.Length > 1 && parts[0] != "voice" ? parts[1] : null;
            if ((DateTime.Now - AbLekLastAsk).TotalMinutes < 10 || srcKey != null && AbLekAskedSrc.Contains(srcKey)) return;
            AbLekLastAsk = DateTime.Now; if (srcKey != null) AbLekAskedSrc.Add(srcKey);
            AbLekSugKey = key; AbLekSugText = text; AbLekSugApply = apply;
        }

        // Twoja decyzja w Audio Monitorze
        internal void AbLektorSugAccept()
        {
            var a = AbLekSugApply; var k = AbLekSugKey; AbLekSugText = null; AbLekSugApply = null; AbLekSugKey = null;
            if (a == null) { if (k != null) AbLekDismissed.Add(k); return; }
            a(); try { _settings.Save(DataDir); } catch (Exception ex) { App.LogError(ex); }
            AbLektorUiRefresh?.Invoke(); _abLek.Reset();   // ocena od nowa - już z nowym ustawieniem
            AbLekApplied = L.T("✔ Zastosowano: głośność lektora ") + (int)Math.Round(_settings.VlcLectorVolume * 100) + L.T("%, film podczas czytania ") + _settings.VlcLectorDuck + L.T("% – oceniam od nowa po kilku kwestiach");
            AbLektorUserSet(_settings.VlcLectorVoice, _settings.VlcLectorVolume, _settings.VlcLectorDuck);   // zaakceptowane = Twoje ustawienie dla tego głosu
        }
        internal static void AbLektorSugDismiss() { if (AbLekSugKey != null) AbLekDismissed.Add(AbLekSugKey); AbLekSugText = null; AbLekSugApply = null; AbLekSugKey = null; }

        // Twoje ręczne ustawienie (suwaki lektora) = przyzwyczajenie dla tego głosu (ostatnie ustawienie, zapis od razu)
        internal static void AbLektorUserSet(string voice, double vol, int duck)
        {
            var d = AbLekPrefs(); var k = voice ?? "";
            if (!d.TryGetValue(k, out var p)) d[k] = p = new LektorVoicePref();
            if (Math.Abs(p.Vol - vol) < 0.005 && p.Duck == duck && p.N > 0) return;
            if (double.IsNaN(vol) || double.IsInfinity(vol)) return;
            p.Vol = Math.Max(0.1, Math.Min(0.99, vol)); p.Duck = Math.Max(10, Math.Min(100, duck)); p.N++; p.T = DateTime.Now;
            string json = JsonSerializer.Serialize(d, AbLearnEntry.Json);
            AbWriteLatest(AbLekPrefsFile, json);   // zapisy po kolei, tylko najnowszy (było: kilka naraz → „plik .tmp zajęty”)
        }

        // zmiana głosu lektora: jeśli dla tego głosu ustawiasz zwykle co innego - propozycja (bez zmiany)
        internal void AbLektorVoiceChanged(string voice)
        {
            if (AbLekSugKey != null && AbLekSugKey.StartsWith("voice|")) { AbLekSugText = null; AbLekSugApply = null; AbLekSugKey = null; }
            if (!AbLekPrefs().TryGetValue(voice ?? "", out var p) || p.N == 0) return;
            if (p.Duck < 10) return;   // nigdy nie proponuj wyciszenia filmu
            if (Math.Abs(p.Vol - _settings.VlcLectorVolume) < 0.03 && p.Duck == _settings.VlcLectorDuck) return;
            Propose("voice|" + voice + "|" + p.Vol.ToString("0.00") + "|" + p.Duck, L.T("Dla głosu „") + AbLekVoiceName(voice) + L.T("” zwykle ustawiasz: głośność ") + (int)Math.Round(p.Vol * 100) + L.T("%, film podczas czytania ") + p.Duck + L.T("%. Zastosować?"), () => { _settings.VlcLectorVolume = p.Vol; _settings.VlcLectorDuck = p.Duck; });
        }

        // tekst i kolor wskaźnika w Audio Monitorze
        internal static (string text, System.Windows.Media.Color color) AbLektorStatus()
        {
            if (_abLek.Status >= 0) AbLekApplied = null;
            if (_abLek.Status < 0) return (AbLekApplied ?? (AbWanted && !AbBypass ? L.T("🗣 Lektor: czekam na kwestie lektora (ocena po pierwszych kwestiach)") : L.T("🗣 Lektor: ocena działa, gdy Audio Balance jest włączony")), AbLekApplied != null ? System.Windows.Media.Color.FromRgb(0x4A, 0xDE, 0x80) : System.Windows.Media.Color.FromRgb(0x8A, 0x90, 0xA6));
            string m = " (" + _abLek.Margin.ToString("+0.0;-0.0") + L.T(" dB nad filmem w paśmie mowy, ") + _abLek.Count + L.T(" kwestii)");
            return _abLek.Status == 0 ? (L.T("🟢 Lektor dobrze słyszalny") + m, System.Windows.Media.Color.FromRgb(0x4A, 0xDE, 0x80))
                 : _abLek.Status == 1 ? (L.T("🟡 Lektor częściowo zagłuszany") + m, System.Windows.Media.Color.FromRgb(0xFA, 0xCC, 0x15))
                 : (L.T("🔴 Lektor zagłuszany przez film") + m, System.Windows.Media.Color.FromRgb(0xF8, 0x71, 0x71));
        }
    }
}
