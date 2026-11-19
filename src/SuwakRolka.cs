using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Velivo
{
    // 🖱 kółko myszy na każdym suwaku we wszystkich programach Velivo (właściciel 2026-11-19): najmniejszy krok suwaka (najdokładniej),
    //    Shift - 10 kroków naraz, Ctrl - 1/10 kroku (gdy suwak ma dowolne wartości). Jedna obsługa dla wszystkich okien (klasa Slider).
    static class SuwakRolka
    {
        static bool _on;
        internal static void Wlacz()
        {
            if (_on) return; _on = true;
            EventManager.RegisterClassHandler(typeof(Slider), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(Kolko));
        }
        static void Kolko(object sender, MouseWheelEventArgs e)
        {
            if (!(sender is Slider s) || !s.IsEnabled || e.Handled) return;
            double krok = s.IsSnapToTickEnabled && s.TickFrequency > 0 ? s.TickFrequency : s.SmallChange > 0 ? s.SmallChange : (s.Maximum - s.Minimum) / 100;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) krok *= 10;
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !s.IsSnapToTickEnabled) krok /= 10;
            double v = s.Value + (e.Delta > 0 ? krok : -krok) * (s.IsDirectionReversed ? -1 : 1);
            s.Value = Math.Max(s.Minimum, Math.Min(s.Maximum, Math.Round(v / krok) * krok));
            e.Handled = true;   // strona / okno pod suwakiem się nie przewija
        }
    }
}
