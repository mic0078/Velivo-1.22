using System;
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
            // trzy tryby po kolei: jasny -> ciemny -> nocny -> jasny
            if (_settings.DarkPages) { _settings.DarkPages = false; _settings.NightLight = true; }
            else if (_settings.NightLight) _settings.NightLight = false;
            else _settings.DarkPages = true;
            try { _settings.Save(DataDir); } catch (Exception) { }
            foreach (var t in _tabs) { ApplyDarkMode(t); ApplyLiveDarkCss(t.View.CoreWebView2); }
            UpdateDarkButton();
            // Bez restartu: do nastepnego uruchomienia dziala przyciemnianie CSS (zdjecia odwracane z powrotem),
            // a przy kolejnym starcie wlacza sie pelny tryb silnika.
            ShowToast(_settings.DarkPages ? L.T("🌙 Tryb ciemny") : _settings.NightLight ? L.T("🌅 Tryb nocny – cieplejsze kolory") : L.T("☀ Tryb jasny"), null);
        }

        // Tryb nocny: ciepla, polprzezroczysta warstwa nad strona (mniej niebieskiego swiatla), bez wplywu na klikanie.
        // Natezenie (jak suwak "Swiatlo nocne" w Windows): 5-100%, zmieniane kolkiem myszy na przycisku trybu.
        string NightLightCss
        {
            get
            {
                int s = Math.Max(5, Math.Min(100, _settings.NightStrength));
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
            if (!_settings.NightLight) return;   // natezenie dotyczy tylko trybu nocnego
            e.Handled = true;
            _settings.NightStrength = Math.Max(5, Math.Min(100, _settings.NightStrength + (e.Delta > 0 ? 5 : -5)));
            foreach (var t in _tabs) ApplyLiveDarkCss(t.View.CoreWebView2);
            // chwilowo pokazujemy wartosc na przycisku
            DarkBtn.FontSize = 13;
            DarkBtn.Content = _settings.NightStrength + "%";
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
            "img,video,picture,canvas,svg image,iframe,embed,object,[style*='background-image']{filter:invert(1) hue-rotate(180deg)!important}";
        const string LiveLightCss = ":root{color-scheme:only light!important}";

        async void ApplyLiveDarkCss(CoreWebView2 core)
        {
            try
            {
                if (core == null || _settings == null) return;
                var src = core.Source ?? "";
                if (!(src.StartsWith("http://") || src.StartsWith("https://"))) return;
                string css = _settings.DarkPages == _darkEngineAtStart ? "" : (_settings.DarkPages ? LiveDarkCss : LiveLightCss);
                if (_settings.NightLight) css += NightLightCss;
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
            bool on = _settings.DarkPages;
            if (_settings.NightLight)
            {
                DarkBtn.Content = "";
                DarkBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xC2, 0x41, 0x0C));
                DarkBtn.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0xA8));
                DarkBtn.ToolTip = L.T("Tryb nocny: WŁĄCZONY (cieplejsze kolory)\nKliknij, aby wrócić do trybu jasnego") +
                    (L.En ? "\nMouse wheel: strength " : "\nKółko myszy: natężenie ") + _settings.NightStrength + "%";
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
