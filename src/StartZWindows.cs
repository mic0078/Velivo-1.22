using System;
using Microsoft.Win32;

namespace Velivo
{
    // ▶ start z Windows - jedno miejsce dla wszystkich programów Velivo (było 7 kopii, każda sprawdzała tylko HKCU\...\Run).
    // Windows ma drugi przełącznik: Menedżer zadań → Aplikacje autostartu (HKCU\...\Explorer\StartupApproved\Run, pierwszy bajt nieparzysty = wyłączony).
    // Wpis w Run przy wyłączonym tam przełączniku = program NIE startuje, a okno pokazywało „zaznaczone” (2026-11-25, Komunikator).
    static class StartZWindows
    {
        const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run", Zgoda = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        // prawda tylko, gdy Windows naprawdę uruchomi program: wpis jest i nie jest wyłączony w Menedżerze zadań
        internal static bool Wlaczony(string nazwa)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Run)) if (k?.GetValue(nazwa) == null) return false;
                using (var z = Registry.CurrentUser.OpenSubKey(Zgoda)) return !(z?.GetValue(nazwa) is byte[] b && b.Length > 0 && (b[0] & 1) == 1);
            }
            catch (Exception) { return false; }
        }
        internal static string Polecenie(string nazwa) { try { using (var k = Registry.CurrentUser.OpenSubKey(Run)) return k?.GetValue(nazwa) as string; } catch (Exception) { return null; } }

        // polecenie = null - wyłączenie; inaczej wpis z tym poleceniem i zdjęta blokada z Menedżera zadań (włączenie w programie ma działać)
        internal static void Ustaw(string nazwa, string polecenie)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Run)) { if (polecenie != null) k.SetValue(nazwa, polecenie); else k.DeleteValue(nazwa, false); }
                using (var z = Registry.CurrentUser.OpenSubKey(Zgoda, true)) if (z?.GetValue(nazwa) != null) { if (polecenie != null) z.SetValue(nazwa, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary); else z.DeleteValue(nazwa, false); }
            }
            catch (Exception) { }
        }
    }
}
