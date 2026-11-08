using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace Velivo
{
    // 🔄 aktualizacje pobranych glosow (Piper, Supertonic, XTTS): raz na dobe w tle sprawdza nowe wersje
    // (Hugging Face - wersja repozytorium, PyPI - coqui-tts) i powiadamia; aktualizacja dopiero po zgodzie uzytkownika.
    // Zainstalowane wersje: Dane\glosy-wersje.json (brak wpisu = wersja wbudowana w Velivo).
    public partial class MainWindow
    {
        static string VoiceVerFile => Path.Combine(VelivoData.Root, "glosy-wersje.json");
        static Dictionary<string, string> _voiceVer;

        static Dictionary<string, string> VoiceVers()
        {
            if (_voiceVer != null) return _voiceVer;
            try { _voiceVer = File.Exists(VoiceVerFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(VoiceVerFile)) : null; } catch (Exception) { }
            return _voiceVer ??= new Dictionary<string, string>();
        }
        static string VoiceRev(string k, string def) => VoiceVers().TryGetValue(k, out var v) && !string.IsNullOrEmpty(v) ? v : def;
        static void VoiceRevSet(string k, string v) { VoiceVers()[k] = v; try { VelivoData.SafeWrite(VoiceVerFile, JsonSerializer.Serialize(_voiceVer)); } catch (Exception ex) { App.LogError(ex); } }

        sealed class VoiceUpd { public string Key, Name, Now, New; }

        static async Task<string> HfSha(HttpClient http, string repo)
        {
            using (var d = JsonDocument.Parse(await http.GetStringAsync("https://huggingface.co/api/models/" + repo))) return d.RootElement.GetProperty("sha").GetString();
        }

        async Task<List<VoiceUpd>> VoiceFindUpdates()
        {
            var list = new List<VoiceUpd>();
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Velivo");
                if (PiperVoices.Any(v => v.St == null && v.Xt == null && PiperVoiceInstalled(v)))
                {
                    var now = VoiceRev("piper", null); var sha = await HfSha(http, "rhasspy/piper-voices");
                    if (now == null)   // wersja wbudowana (v1.0.0): zapamietujemy stan repozytorium przy pierwszym sprawdzeniu, zmiana = nowa wersja
                    {
                        var b = VoiceRev("piper-base", null);
                        if (b == null) VoiceRevSet("piper-base", sha); else if (b != sha) list.Add(new VoiceUpd { Key = "piper", Name = "Piper", Now = "v1.0.0", New = sha });
                    }
                    else if (now != sha) list.Add(new VoiceUpd { Key = "piper", Name = "Piper", Now = now, New = sha });
                }
                if (Directory.Exists(SupertonicRoot))
                {
                    var now = VoiceRev("supertonic", "aafc6e32416a594460b32413efc49d7fe4ce6d46"); var sha = await HfSha(http, "supertone-oss-archive/supertonic-3");
                    if (sha != now) list.Add(new VoiceUpd { Key = "supertonic", Name = "Supertonic", Now = now, New = sha });
                }
                if (XttsInstalled())
                {
                    using (var d = JsonDocument.Parse(await http.GetStringAsync("https://pypi.org/pypi/coqui-tts/json")))
                    {
                        var nv = d.RootElement.GetProperty("info").GetProperty("version").GetString(); var now = VoiceRev("coqui-tts", "0.25.3");
                        if (nv != now) list.Add(new VoiceUpd { Key = "coqui-tts", Name = "XTTS (coqui-tts)", Now = now, New = nv });
                    }
                }
            }
            return list.Where(u => VoiceRev("pominiete-" + u.Key, "") != u.New || _voiceAskAll).ToList();
        }

        bool _voiceUpdBusy, _voiceAskAll;

        // owner == null: sprawdzenie w tle (raz na dobe, tylko powiadomienie); owner != null: przycisk w Ustawieniach
        async Task VoiceUpdateCheckAsync(Window owner)
        {
            if (_voiceUpdBusy) return;
            if (owner == null)
            {
                if (DateTime.TryParse(VoiceRev("sprawdzono", ""), null, System.Globalization.DateTimeStyles.RoundtripKind, out var last) && DateTime.UtcNow - last < TimeSpan.FromHours(24)) return;
                await Task.Delay(TimeSpan.FromMinutes(2));   // nie w czasie startu
            }
            _voiceUpdBusy = true; _voiceAskAll = owner != null;
            try
            {
                List<VoiceUpd> ups;
                try { ups = await VoiceFindUpdates(); VoiceRevSet("sprawdzono", DateTime.UtcNow.ToString("o")); }
                catch (Exception ex) { if (owner != null) MessageBox.Show(owner, L.T("Nie udało się sprawdzić aktualizacji głosów: ") + ex.Message, "Velivo"); return; }
                if (ups.Count == 0) { if (owner != null) MessageBox.Show(owner, L.T("✅ Głosy są aktualne."), "Velivo"); return; }
                var names = string.Join(", ", ups.Select(u => u.Name));
                if (owner == null) { ShowToast(L.T("🔄 Nowa wersja głosów: ") + names + L.T(" – Ustawienia → Czytanie na głos → „Aktualizacje głosów”."), null); return; }
                var txt = string.Join("\n", ups.Select(u => "• " + u.Name + ": " + Short(u.Now) + " → " + Short(u.New)));
                var r = MessageBox.Show(owner, L.T("Dostępne nowe wersje:") + "\n\n" + txt + "\n\n" + L.T("Zaktualizować teraz? Nowa wersja może brzmieć inaczej; gotowe nagrania lektora zostają.\nNie – nie przypominaj o tych wersjach."), "Velivo", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.No) { foreach (var u in ups) VoiceRevSet("pominiete-" + u.Key, u.New); return; }
                if (r != MessageBoxResult.Yes) return;
                var failed = new List<string>();
                foreach (var u in ups) { try { if (!await VoiceApplyUpdate(u)) failed.Add(u.Name); } catch (Exception ex) { App.LogError(ex); failed.Add(u.Name + ": " + ex.Message); } }
                MessageBox.Show(owner, failed.Count == 0 ? L.T("✅ Głosy zaktualizowane.") : L.T("Nie udało się zaktualizować:") + "\n" + string.Join("\n", failed), "Velivo");
            }
            finally { _voiceUpdBusy = false; }
        }

        static string Short(string v) => v != null && v.Length > 12 ? v.Substring(0, 8) : v;

        // PyTorch z tego samego zrodla co przy instalacji - nowa wersja coqui-tts nie zamieni wersji na karte NVIDIA na wersje na procesor
        async Task<bool> VoiceApplyUpdate(VoiceUpd u)
        {
            if (u.Key == "coqui-tts")
            {
                StopXttsProcess();
                XtShow(); XtStatus(4, L.T("XTTS: instaluję coqui-tts…") + " " + u.New);
                if (!await RunXttsStep(XttsPy, "-m pip install --no-warn-script-location -U coqui-tts==" + u.New + " click --extra-index-url https://download.pytorch.org/whl/" + (XttsHasNvidia() ? "cu124" : "cpu"), "coqui-tts")) return false;
                foreach (var tmp in new[] { "pip-cache", "tmp" }) try { Directory.Delete(Path.Combine(XttsRoot, tmp), true); } catch (Exception) { }
                XtStatus(0, L.T("✅ Głosy zaktualizowane.") + " coqui-tts " + u.New, true);
                VoiceRevSet(u.Key, u.New); return true;
            }
            // Piper / Supertonic: pobranie zainstalowanych plikow z nowej wersji (stare zostaja, gdy pobieranie sie nie uda)
            var old = VoiceRev(u.Key, null);
            VoiceRevSet(u.Key, u.New);
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Velivo");
                    if (u.Key == "supertonic")
                    {
                        await _stLock.WaitAsync();
                        try
                        {
                            _stTts = null; _stStyles.Clear();
                            var files = SupertonicModelFiles.Concat(Directory.Exists(SupertonicPath("voice_styles")) ? Directory.GetFiles(SupertonicPath("voice_styles"), "*.json").Select(f => "voice_styles/" + Path.GetFileName(f)) : new string[0]).ToList();
                            foreach (var rel in files) { ShowToast("⬇ Supertonic: " + Path.GetFileName(rel), null); await DownloadFile(http, SupertonicBase + rel + "?download=true", SupertonicPath(rel), null); }
                        }
                        finally { _stLock.Release(); }
                    }
                    else
                    {
                        StopPiperProcess();
                        foreach (var v in PiperVoices.Where(v => v.St == null && v.Xt == null && PiperVoiceInstalled(v)))
                        {
                            var model = Path.Combine(PiperRoot, "voices", v.Id + ".onnx");
                            ShowToast("⬇ Piper: " + v.Label.Split(' ')[0], null);
                            await DownloadFile(http, PiperVoicesBase + v.Path + v.Id + ".onnx.json?download=true", model + ".json", null);
                            await DownloadFile(http, PiperVoicesBase + v.Path + v.Id + ".onnx?download=true", model, null);
                        }
                    }
                }
                return true;
            }
            catch (Exception) { if (old == null) VoiceVers().Remove(u.Key); else VoiceRevSet(u.Key, old); throw; }
        }
    }
}
