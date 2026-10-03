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
            _settings.DarkPages = !_settings.DarkPages;
            try { _settings.Save(DataDir); } catch (Exception) { }
            foreach (var t in _tabs) ApplyDarkMode(t);
            UpdateDarkButton();
            OfferRestartForDarkMode();
        }

        // Silnik ma inny tryb niz ustawienie -> zaproponuj ponowne uruchomienie (karty wroca).
        void OfferRestartForDarkMode()
        {
            if (_settings.DarkPages == _darkEngineAtStart) return;
            string extra = "";
            if (_tabs.Any(t => t.Private)) extra += "\n• karty prywatne zostaną zamknięte,";
            if (_jobs.Any(j => j.State == JobState.Running)) extra += "\n• pobieranie zostanie wstrzymane (wznowisz je potem jednym kliknięciem),";
            var msg = (_settings.DarkPages ? "Tryb ciemny" : "Wyłączenie trybu ciemnego") +
                      " zadziała na wszystkich stronach po ponownym uruchomieniu Velivo.\nKarty wrócą same." +
                      (extra.Length > 0 ? "\n\nUwaga:" + extra.TrimEnd(',') + "." : "") +
                      "\n\nUruchomić Velivo ponownie teraz?";
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
            catch (Exception ex) { App.LogError(ex); MessageBox.Show(this, "Nie udało się uruchomić ponownie:\n" + ex.Message, "Velivo"); }
        }

        void UpdateDarkButton()
        {
            bool on = _settings.DarkPages;
            DarkBtn.Content = on ? "" : ""; // slonce (wylacz) / ksiezyc (wlacz)
            DarkBtn.Foreground = new SolidColorBrush(on ? Color.FromRgb(0xB4, 0x53, 0x09) : Color.FromRgb(0x1E, 0x29, 0x3B));
            DarkBtn.Background = new SolidColorBrush(on ? Color.FromRgb(0xFE, 0xF3, 0xC7) : Color.FromRgb(0xE2, 0xE8, 0xF0));
            DarkBtn.ToolTip = on ? "Tryb ciemny stron: WŁĄCZONY\nKliknij, aby wyłączyć" : "Tryb ciemny stron: wyłączony\nKliknij, aby przyciemnić strony (zdjęcia zostają w prawdziwych kolorach)";
        }
    }
}
