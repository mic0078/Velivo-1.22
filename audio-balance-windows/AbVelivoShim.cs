using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Velivo
{
    static class Edycja { public const bool Premium = true; }   // program samodzielny: wtyczki VST3 zawsze dostępne

    // to, czego panel kompresora z Velivo (Silnik/AudioBalanceDynamicsUi.cs) potrzebuje od okna Velivo - tu z ustawień i silnika programu samodzielnego
    public partial class MainWindow
    {
        internal static AbMain Host; internal static bool AbPreNoSrc, AbPreApps;
        static AbProcessor AbProc => Host.Eng.Proc;
        static string GetPref(string k) => Host.Get(k.StartsWith("ab-") ? k.Substring(3) : k);
        static void SetPref(string k, string v) => Host.Put(k.StartsWith("ab-") ? k.Substring(3) : k, v);
        static bool AbFlowing() => Host.Eng.Running && !AbProc.Bypass;
        static readonly SolidColorBrush AbDark = new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1F));
        void AbNeedsRestartHint() { }
        static void ShowToast(string text, object _) => Host.Toast(text);
        static string DataDir => App.DataDir;
        static readonly SolidColorBrush AbRed = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
        static void AbWriteLatest(string file, string text) { System.IO.Directory.CreateDirectory(App.DataDir); System.IO.File.WriteAllText(file, text); }
        static string Prompt(string text, string def, Window owner) => Host.Ask(text, def, owner);
    }
}
