using System;
using System.Reflection;
using System.Threading;

namespace Velivo
{
    // 🧩 współpraca z Velivo Centrum (jedna ikona V przy zegarze, lista programów Velivo) - wspólne dla wszystkich programów Velivo na Windows:
    //  • gdy Centrum działa, ikona programu przy zegarze się chowa - chyba że w Centrum zaznaczono „pokazuj też przy zegarze” (HKCU\Software\Velivo\Centrum\<proces> = 1);
    //  • prawy przycisk na programie w Centrum = pełne menu tego programu (to samo co przy jego ikonie) - Centrum daje znak, program pokazuje swoje menu przy kursorze.
    static class CentrumZasobnik
    {
        static readonly string Proc = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        static System.Windows.Forms.NotifyIcon _menuTray; static bool _menuOn;

        internal static void Podlacz(System.Windows.Forms.NotifyIcon tray, bool chowaj = true)
        {
            if (tray == null) return;
            var sc = SynchronizationContext.Current;
            if (chowaj)
            {
            var tm = new System.Windows.Forms.Timer { Interval = 3000 };
            tm.Tick += (s, e) => { bool show = Pokazuj(); if (tray.Visible != show) try { tray.Visible = show; } catch (Exception) { } };
            tm.Start();
            }   // Komunikator: ikona zostaje (przez nią idą powiadomienia o wiadomościach) - tylko menu z Centrum
            if (_menuTray != null && !chowaj) return;   // pełne Velivo: menu z Centrum = główna ikona programu, nie ikona komunikatora
            _menuTray = tray;
            if (_menuOn) return; _menuOn = true;   // jeden znak na program (dwie ikony w jednym procesie - jeden wątek)
            try
            {
                var ev = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Velivo.Menu." + Proc);
                new Thread(() => { while (ev.WaitOne()) sc?.Post(_ => Menu(_menuTray), null); }) { IsBackground = true, Name = "Velivo Centrum menu" }.Start();
            }
            catch (Exception) { }
        }

        // przy zegarze: zawsze bez Centrum; z Centrum - tylko gdy tak wybrano
        static bool Pokazuj()
        {
            bool c = Mutex.TryOpenExisting(@"Local\Velivo.Centrum", out var m); m?.Dispose();
            if (!c) return true;
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Velivo\Centrum")) return (k?.GetValue(Proc) as int?) == 1; } catch (Exception) { return false; }
        }

        // menu ikony przy kursorze - ta sama droga, której używa Windows po prawym kliknięciu ikony (z poprawnym zamykaniem po kliknięciu obok)
        static void Menu(System.Windows.Forms.NotifyIcon tray)
        {
            try
            {
                var mi = typeof(System.Windows.Forms.NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
                if (mi != null) mi.Invoke(tray, null);
                else tray.ContextMenuStrip?.Show(System.Windows.Forms.Cursor.Position);
            }
            catch (Exception) { try { tray.ContextMenuStrip?.Show(System.Windows.Forms.Cursor.Position); } catch (Exception) { } }
        }
    }
}
