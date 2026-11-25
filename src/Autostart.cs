using System;
using System.Collections.Generic;
using System.Globalization;

namespace Velivo
{
    // Uruchamianie Velivo przy starcie Windows (wpis uzytkownika w HKCU\...\Run - bez uprawnien administratora)
    // z opoznieniem: system startuje szybciej, a Velivo wlacza sie po podanej liczbie sekund. Domyslnie wylaczone.
    public partial class MainWindow
    {
        const string RunName = Wariant.Pid;   // 🧩 każdy program własny autostart (instalator: ValueName {#Pid})
        internal static readonly int[] AutostartDelays = { 0, 10, 30, 60, 120, 300 };

        internal static string AutostartCommand(string exe, int delaySec)
        {
            return "\"" + exe + "\" --autostart " + Math.Max(0, delaySec).ToString(CultureInfo.InvariantCulture);
        }

        // "--autostart N" z argumentow: zwraca opoznienie (s) i usuwa oba argumenty; -1 = zwykle uruchomienie
        internal static bool AutoStarted;   // 🧩 uruchomione razem z Windows (Odtwarzacz: tylko ikona w zasobniku)
        internal static int TakeAutostartArg(List<string> args)
        {
            int i = args.IndexOf("--autostart");
            if (i < 0) return -1;
            AutoStarted = true;
            int d = 0;
            if (i + 1 < args.Count && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out d)) args.RemoveAt(i + 1);
            args.RemoveAt(i);
            return Math.Min(Math.Max(0, d), 600);
        }

        // obecny wpis: opoznienie w s albo -1, gdy autostart wylaczony
        static int ReadAutostart()
        {
            try
            {
                {
                    var v = StartZWindows.Polecenie(RunName);
                    if (string.IsNullOrEmpty(v) || !StartZWindows.Wlaczony(RunName)) return -1;   // wyłączony w Menedżerze zadań = nie startuje
                    var parts = new List<string>(v.Split(' '));
                    int d = TakeAutostartArg(parts);
                    return d < 0 ? 0 : d;
                }
            }
            catch (Exception) { return -1; }
        }

        static void WriteAutostart(bool on, int delaySec)
        {
            try
            {
                StartZWindows.Ustaw(RunName, on ? AutostartCommand(Environment.ProcessPath, delaySec) : null);
            }
            catch (Exception ex) { App.LogError(ex); }
        }
    }
}
