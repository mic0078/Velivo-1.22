using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Win32;

namespace Velivo
{
    // Uruchamianie Velivo przy starcie Windows (wpis uzytkownika w HKCU\...\Run - bez uprawnien administratora)
    // z opoznieniem: system startuje szybciej, a Velivo wlacza sie po podanej liczbie sekund. Domyslnie wylaczone.
    public partial class MainWindow
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "Velivo";
        internal static readonly int[] AutostartDelays = { 0, 10, 30, 60, 120, 300 };

        internal static string AutostartCommand(string exe, int delaySec)
        {
            return "\"" + exe + "\" --autostart " + Math.Max(0, delaySec).ToString(CultureInfo.InvariantCulture);
        }

        // "--autostart N" z argumentow: zwraca opoznienie (s) i usuwa oba argumenty; -1 = zwykle uruchomienie
        internal static int TakeAutostartArg(List<string> args)
        {
            int i = args.IndexOf("--autostart");
            if (i < 0) return -1;
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
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    var v = k?.GetValue(RunName) as string;
                    if (string.IsNullOrEmpty(v)) return -1;
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
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunName, AutostartCommand(Environment.ProcessPath, delaySec));
                    else if (k.GetValue(RunName) != null) k.DeleteValue(RunName, false);
                }
            }
            catch (Exception ex) { App.LogError(ex); }
        }
    }
}
