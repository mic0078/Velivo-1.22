using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace Przegladarka
{
    // Tryb ciemny stron (przycisk z ksiezycem):
    //  1) strony z wlasnym ciemnym wygladem dostaja prosbe o niego (prefers-color-scheme: dark) - od razu,
    //  2) pozostale przyciemnia wbudowany mechanizm silnika Chromium (WebContentsForceDark): element po
    //     elemencie, BEZ ruszania zdjec. Wlacza sie przy starcie silnika - zmiana wymaga ponownego uruchomienia.
    //     (Wczesniejsze przyciemnianie filtrem CSS psulo kolory zdjec - negatyw / wyblakniecie.)
    public partial class MainWindow
    {
        bool _darkEngineAtStart; // czy silnik wystartowal z trybem ciemnym

        string DarkBrowserArgument { get { return _settings.DarkPages ? "--enable-features=WebContentsForceDark" : null; } }

        void ApplyDarkMode(BrowserTab tab)
        {
            var core = tab.View.CoreWebView2;
            if (core == null) return;
            try { core.Profile.PreferredColorScheme = _settings.DarkPages ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Auto; }
            catch (Exception) { }
        }

        void DarkBtn_Click(object sender, RoutedEventArgs e)
        {
            // trzy tryby po kolei: jasny -> ciemny -> nocny -> jasny; zapamietane dla biezacej strony (jak powiekszenie)
            var m = CurrentPageMode();
            string next = m.Dark ? "night" : m.Night ? "light" : "dark";
            var host = _current != null && !_current.Private && _current.View.CoreWebView2 != null ? HostOf(_current.View.CoreWebView2.Source) : null;
            if (host != null) SetSiteMode(host, next, m.Strength);
            else
            {
                // strona wewnetrzna / karta prywatna - zmieniamy tryb domyslny jak dawniej
                _settings.DarkPages = next == "dark"; _settings.NightLight = next == "night";
                try { _settings.Save(DataDir); } catch (Exception) { }
                foreach (var t in _tabs) ApplyDarkMode(t);
            }
            foreach (var t in _tabs) ApplyLiveDarkCss(t.View.CoreWebView2);
            UpdateDarkButton();
            ShowToast(next == "dark" ? L.T("🌙 Tryb ciemny") : next == "night" ? L.T("🌅 Tryb nocny – cieplejsze kolory") : L.T("☀ Tryb jasny"), null);
        }

        // ---------- tryb zapamietany osobno dla kazdej strony (host -> light/dark/night + natezenie) ----------
        readonly Dictionary<string, string> _modeByHost = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static string ModeFile { get { return Path.Combine(DataDir, "tryb-stron.txt"); } }

        struct PageMode { public bool Dark, Night; public int Strength; }

        void LoadSiteModes()
        {
            try
            {
                if (!File.Exists(ModeFile)) return;
                foreach (var line in File.ReadAllLines(ModeFile))
                {
                    var p = line.Split('\t');
                    if (p.Length >= 2) _modeByHost[p[0]] = p[1] + (p.Length >= 3 ? "\t" + p[2] : "");
                }
            }
            catch (Exception) { }
        }

        void SetSiteMode(string host, string mode, int strength)
        {
            _modeByHost[host] = mode + "\t" + Math.Max(5, Math.Min(100, strength));
            try { File.WriteAllLines(ModeFile, _modeByHost.Select(kv => kv.Key + "\t" + kv.Value)); } catch (Exception) { }
        }

        PageMode ModeFor(string url, bool priv)
        {
            var m = new PageMode { Dark = _settings.DarkPages, Night = _settings.NightLight, Strength = _settings.NightStrength };
            var host = priv ? null : HostOf(url);
            string v;
            if (host != null && _modeByHost.TryGetValue(host, out v))
            {
                var p = v.Split('\t');
                m.Dark = p[0] == "dark"; m.Night = p[0] == "night";
                int st; if (p.Length > 1 && int.TryParse(p[1], out st)) m.Strength = Math.Max(5, Math.Min(100, st));
            }
            return m;
        }

        PageMode CurrentPageMode()
        {
            var core = _current != null ? _current.View.CoreWebView2 : null;
            return ModeFor(core != null ? core.Source : null, _current != null && _current.Private);
        }

        // Tryb nocny: ciepla, polprzezroczysta warstwa nad strona (mniej niebieskiego swiatla), bez wplywu na klikanie.
        // Natezenie (jak suwak "Swiatlo nocne" w Windows): 5-100%, zmieniane kolkiem myszy na przycisku trybu.
        static string NightLightCss(int strength)
        {
            {
                int s = Math.Max(5, Math.Min(100, strength));
                double a = 0.04 + s / 100.0 * 0.46;                      // przezroczystosc warstwy
                int g = (int)Math.Round(170 - s / 100.0 * 70);           // im mocniej, tym cieplej (mniej zieleni)
                int b = (int)Math.Round(70 - s / 100.0 * 55);            // i mniej niebieskiego
                return "html::after{content:'';position:fixed;inset:0;background:rgba(255," + g + "," + b + "," +
                       a.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) +
                       ");mix-blend-mode:multiply;pointer-events:none;z-index:2147483647}";
            }
        }

        System.Windows.Threading.DispatcherTimer _nightLabelTimer, _nightSaveTimer;

        void DarkBtn_Wheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            var m = CurrentPageMode();
            if (!m.Night) return;   // natezenie dotyczy tylko trybu nocnego
            e.Handled = true;
            int ns = Math.Max(5, Math.Min(100, m.Strength + (e.Delta > 0 ? 5 : -5)));
            var host = _current != null && !_current.Private && _current.View.CoreWebView2 != null ? HostOf(_current.View.CoreWebView2.Source) : null;
            if (host != null) SetSiteMode(host, "night", ns); else _settings.NightStrength = ns;
            foreach (var t in _tabs) ApplyLiveDarkCss(t.View.CoreWebView2);
            // chwilowo pokazujemy wartosc na przycisku
            DarkBtn.FontSize = 13;
            DarkBtn.Content = ns + "%";
            if (_nightLabelTimer == null)
            {
                _nightLabelTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
                _nightLabelTimer.Tick += (s, a) => { _nightLabelTimer.Stop(); DarkBtn.ClearValue(System.Windows.Controls.Control.FontSizeProperty); UpdateDarkButton(); };
            }
            _nightLabelTimer.Stop(); _nightLabelTimer.Start();
            if (_nightSaveTimer == null)
            {
                _nightSaveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _nightSaveTimer.Tick += (s, a) => { _nightSaveTimer.Stop(); try { _settings.Save(DataDir); } catch (Exception) { } };
            }
            _nightSaveTimer.Stop(); _nightSaveTimer.Start();
        }

        // Tryb wybrany w tej sesji rozni sie od trybu, z ktorym wystartowal silnik -> poprawka CSS na stronie.
        const string LiveDarkCss = "html{filter:invert(1) hue-rotate(180deg)!important;background:#fff!important}" +
            // drugi raz odwracamy tylko najbardziej zewnetrzny element (np. <picture>, a nie jeszcze <img> w nim) - inaczej negatyw
            ":is(img,video,picture,canvas,svg image,iframe,embed,object,[style*='background-image'])" +
            ":not(:is(picture,iframe,embed,object,[style*='background-image']) *){filter:invert(1) hue-rotate(180deg)!important}";
        const string LiveLightCss = ":root{color-scheme:only light!important}";

        async void ApplyLiveDarkCss(CoreWebView2 core)
        {
            try
            {
                if (core == null || _settings == null) return;
                var src = core.Source ?? "";
                if (!(src.StartsWith("http://") || src.StartsWith("https://"))) return;
                var tab = _tabs.FirstOrDefault(t => t.View.CoreWebView2 == core);
                var m = ModeFor(src, tab != null && tab.Private);
                string css = m.Dark == _darkEngineAtStart ? "" : (m.Dark ? LiveDarkCss : LiveLightCss);
                if (m.Night) css += NightLightCss(m.Strength);
                // Silnik nie przyciemnia stron, ktore same deklaruja ciemny motyw (np. GitHub z motywem jasnym ustawionym
                // na koncie) - zostaja jasne. Gdy taka strona mimo trybu ciemnego jest jasna, przyciemniamy ja jak w trybie na zywo.
                if (m.Dark && _darkEngineAtStart)
                {
                    var fix = System.Text.Json.JsonSerializer.Serialize(LiveDarkCss);
                    var check = "(function(){function run(){try{var h=document.documentElement;if(!h||document.getElementById('velivo-ciemny-wymuszony'))return;" +
                        "var cs=getComputedStyle(h).colorScheme||'';var me=document.querySelector('meta[name=color-scheme]');" +
                        "if(!/dark/.test(cs)&&!(me&&/dark/.test(me.content||'')))return;" +
                        "var els=[document.body,h],lum=1;for(var i=0;i<els.length;i++){if(!els[i])continue;var m=getComputedStyle(els[i]).backgroundColor.match(/[\\d.]+/g);" +
                        "if(!m||m.length<3||(m.length>3&&parseFloat(m[3])<.5))continue;lum=(0.299*m[0]+0.587*m[1]+0.114*m[2])/255;break;}" +
                        "if(lum<.6)return;var st=document.createElement('style');st.id='velivo-ciemny-wymuszony';st.textContent=" + fix + ";(document.head||h).appendChild(st);}catch(e){}}" +
                        "run();setTimeout(run,1200);})();";
                    await core.ExecuteScriptAsync(check);
                }
                await core.ExecuteScriptAsync("(function(){try{var id='velivo-tryb-ciemny';var st=document.getElementById(id);" +
                    "if(!" + System.Text.Json.JsonSerializer.Serialize(css) + "){if(st)st.remove();return;}" +
                    "if(!st){st=document.createElement('style');st.id=id;(document.head||document.documentElement).appendChild(st);}" +
                    "st.textContent=" + System.Text.Json.JsonSerializer.Serialize(css) + ";}catch(e){}})();");
            }
            catch (Exception) { }
        }

        // Silnik ma inny tryb niz ustawienie -> zaproponuj ponowne uruchomienie (karty wroca).
        void OfferRestartForDarkMode()
        {
            if (_settings.DarkPages == _darkEngineAtStart) return;
            string extra = "";
            if (_tabs.Any(t => t.Private)) extra += L.T("\n• karty prywatne zostaną zamknięte,");
            if (_jobs.Any(j => j.State == JobState.Running)) extra += L.T("\n• pobieranie zostanie wstrzymane (wznowisz je potem jednym kliknięciem),");
            var msg = (_settings.DarkPages ? L.T("Tryb ciemny") : L.T("Wyłączenie trybu ciemnego")) +
                      L.T(" zadziała na wszystkich stronach po ponownym uruchomieniu Velivo.\nKarty wrócą same.") +
                      (extra.Length > 0 ? L.T("\n\nUwaga:") + extra.TrimEnd(',') + "." : "") +
                      L.T("\n\nUruchomić Velivo ponownie teraz?");
            if (MessageBox.Show(this, msg, "Velivo", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) RestartVelivo();
        }

        static string RestartFlag { get { return Path.Combine(DataDir, "restart.flag"); } }

        void RestartVelivo()
        {
            try
            {
                _sessionLoaded = true;
                SaveSession();                                   // biezace karty
                File.WriteAllText(RestartFlag, "1");              // przywroc karty nawet przy wylaczonym przywracaniu
                var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName) { UseShellExecute = false };
                psi.ArgumentList.Add("--czekaj-na");
                psi.ArgumentList.Add(Environment.ProcessId.ToString());
                if (Environment.GetEnvironmentVariable("PRZEGLADARKA_DANE") != null) psi.Environment["PRZEGLADARKA_DANE"] = DataDir;
                Process.Start(psi);
                Close();
            }
            catch (Exception ex) { App.LogError(ex); MessageBox.Show(this, L.T("Nie udało się uruchomić ponownie:\n") + ex.Message, "Velivo"); }
        }

        void UpdateDarkButton()
        {
            var m = CurrentPageMode();
            bool on = m.Dark;
            if (m.Night)
            {
                DarkBtn.Content = "";
                DarkBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xC2, 0x41, 0x0C));
                DarkBtn.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0xA8));
                DarkBtn.ToolTip = L.T("Tryb nocny: WŁĄCZONY (cieplejsze kolory)\nKliknij, aby wrócić do trybu jasnego") +
                    (L.En ? "\nMouse wheel: strength " : "\nKółko myszy: natężenie ") + m.Strength + "%";
                ModernDarkButton();
                return;
            }
            DarkBtn.Content = on ? "" : ""; // slonce (wylacz) / ksiezyc (wlacz)
            DarkBtn.Foreground = new SolidColorBrush(on ? Color.FromRgb(0xB4, 0x53, 0x09) : Color.FromRgb(0x1E, 0x29, 0x3B));
            DarkBtn.Background = new SolidColorBrush(on ? Color.FromRgb(0xFE, 0xF3, 0xC7) : Color.FromRgb(0xE2, 0xE8, 0xF0));
            ModernDarkButton();
            DarkBtn.ToolTip = on ? L.T("Tryb ciemny stron: WŁĄCZONY\nKliknij, aby wyłączyć") : L.T("Tryb ciemny stron: wyłączony\nKliknij, aby przyciemnić strony (zdjęcia zostają w prawdziwych kolorach)");
        }
    }
}
