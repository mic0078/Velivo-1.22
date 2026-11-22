using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Velivo
{
    // 🖱 Pilot → sterowanie komputerem (dokumentacja/makieta-pilot-komputer.html): mysz (touchpad w telefonie), pisanie, skróty,
    // głośność główna Windows, ekran, zablokuj, wyloguj, uruchom ponownie, wyłącz. Tylko sparowany telefon (klucz grupy, jak Pilot).
    // Ruchy w osobnym wątku (jak pomoc zdalna) - pętla Windows przy pasku tytułu nie zawiesza okien Velivo.
    public partial class MainWindow
    {
        [StructLayout(LayoutKind.Sequential)] struct PcMouse { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct PcKey { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Explicit)] struct PcUnion { [FieldOffset(0)] public PcMouse m; [FieldOffset(0)] public PcKey k; }
        [StructLayout(LayoutKind.Sequential)] struct PcInput { public int type; public PcUnion u; }
        [DllImport("user32.dll", EntryPoint = "SendInput")] static extern uint PcSendInput(uint n, PcInput[] inputs, int size);
        [DllImport("user32.dll")] static extern bool LockWorkStation();

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr p);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr p);
            [PreserveSig] int GetChannelCount(out uint n);
            [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
            [PreserveSig] int SetMasterVolumeLevelScalar(float v, ref Guid ctx);
            [PreserveSig] int GetMasterVolumeLevel(out float db);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float v);
            [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float v, ref Guid ctx);
            [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float v);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        static IAudioEndpointVolume PcVolumeCtl()
        {
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                if (en.GetDefaultAudioEndpoint(eRender, 1, out var d) != 0 || d == null) return null;
                var iid = typeof(IAudioEndpointVolume).GUID;
                return d.Activate(ref iid, 23, IntPtr.Zero, out var o) == 0 ? o as IAudioEndpointVolume : null;
            }
            catch (Exception) { return null; }
        }

        // głośność główna dla telefonu: 0–100, -1 = nieznana
        static (int Vol, bool Mute) PcVolume()
        {
            var v = PcVolumeCtl(); if (v == null) return (-1, false);
            try { v.GetMasterVolumeLevelScalar(out var f); v.GetMute(out var m); return ((int)Math.Round(f * 100), m); }
            catch (Exception) { return (-1, false); }
        }

        static void PcIn(PcInput i) => PcSendInput(1, new[] { i }, Marshal.SizeOf<PcInput>());
        static void PcMouseEv(uint flags, int dx = 0, int dy = 0, int data = 0) => PcIn(new PcInput { type = 0, u = new PcUnion { m = new PcMouse { dx = dx, dy = dy, flags = flags, data = unchecked((uint)data) } } });
        static void PcKeyEv(ushort vk, bool up, ushort scan = 0, bool unicode = false)
        {
            bool ext = (vk >= 0x21 && vk <= 0x2E) || vk == 0x5B || vk == 0x5C;
            PcIn(new PcInput { type = 1, u = new PcUnion { k = new PcKey { vk = vk, scan = scan, flags = (up ? 2u : 0u) | (ext ? 1u : 0u) | (unicode ? 4u : 0u) } } });
        }
        static void PcCombo(params ushort[] vks) { foreach (var k in vks) PcKeyEv(k, false); for (int i = vks.Length - 1; i >= 0; i--) PcKeyEv(vks[i], true); }

        readonly System.Collections.Concurrent.BlockingCollection<Action> _pcQ = new System.Collections.Concurrent.BlockingCollection<Action>();
        System.Threading.Thread _pcThread;
        void PcDo(Action a)
        {
            if (_pcThread == null)
            {
                _pcThread = new System.Threading.Thread(() => { foreach (var x in _pcQ.GetConsumingEnumerable()) try { x(); } catch (Exception ex) { App.LogError(ex); } }) { IsBackground = true, Name = "Velivo Pilot komputer" };
                _pcThread.Start();
            }
            _pcQ.Add(a);
        }

        // polecenie z telefonu: op = "pc-…"; v - tekst / nazwa, n - liczba
        void PilotPc(LanPilotCmd cmd)
        {
            string v = cmd.v ?? ""; double n = cmd.n;
            switch (cmd.op)
            {
                case "pc-move":   // v = "dx,dy" (piksele, już ze współczynnikiem z telefonu)
                    var p = v.Split(',');
                    if (p.Length == 2 && int.TryParse(p[0], out var dx) && int.TryParse(p[1], out var dy)) PcDo(() => PcMouseEv(0x0001, dx, dy));
                    break;
                case "pc-click":
                    PcDo(() =>
                    {
                        uint d = v == "right" ? 0x0008u : 0x0002u;
                        PcMouseEv(d); PcMouseEv(d << 1);
                        if (v == "double") { PcMouseEv(d); PcMouseEv(d << 1); }
                    });
                    break;
                case "pc-scroll": PcDo(() => PcMouseEv(0x0800, data: (int)(n * 120))); break;   // n > 0 = w górę
                case "pc-text":
                    PcDo(() =>
                    {
                        foreach (var ch in v.Replace("\r\n", "\n"))
                        {
                            if (ch == '\n') { PcKeyEv(0x0D, false); PcKeyEv(0x0D, true); continue; }
                            PcKeyEv(0, false, ch, true); PcKeyEv(0, true, ch, true);   // znaki Unicode - polskie litery bez względu na układ klawiatury
                        }
                    });
                    break;
                case "pc-key":
                    ushort[] k = v switch
                    {
                        "enter" => new ushort[] { 0x0D }, "back" => new ushort[] { 0x08 }, "tab" => new ushort[] { 0x09 }, "esc" => new ushort[] { 0x1B },
                        "left" => new ushort[] { 0x25 }, "up" => new ushort[] { 0x26 }, "right" => new ushort[] { 0x27 }, "down" => new ushort[] { 0x28 },
                        "copy" => new ushort[] { 0x11, 0x43 }, "paste" => new ushort[] { 0x11, 0x56 }, "undo" => new ushort[] { 0x11, 0x5A },
                        "alttab" => new ushort[] { 0x12, 0x09 }, "win" => new ushort[] { 0x5B }, "altf4" => new ushort[] { 0x12, 0x73 },
                        "f5" => new ushort[] { 0x74 }, "f11" => new ushort[] { 0x7A }, _ => null
                    };
                    if (k != null) PcDo(() => PcCombo(k));
                    break;
                case "pc-vol":   // v = "up" / "down" / "mute" albo n = 0–100
                    PcDo(() =>
                    {
                        var c = PcVolumeCtl(); if (c == null) return;
                        var g = Guid.Empty;
                        if (v == "mute") { c.GetMute(out var m); c.SetMute(!m, ref g); return; }
                        c.GetMasterVolumeLevelScalar(out var f);
                        float to = v == "up" ? f + 0.05f : v == "down" ? f - 0.05f : (float)(n / 100.0);
                        c.SetMasterVolumeLevelScalar(Math.Max(0f, Math.Min(1f, to)), ref g);
                        if (to > 0) c.SetMute(false, ref g);
                    });
                    break;
                case "pc-run":   // ⭐ ulubiona aplikacja - tylko skrót z menu Start (telefon nie może uruchomić dowolnego pliku)
                    var app = PcApps().FirstOrDefault(x => string.Equals(x.Path, v, StringComparison.OrdinalIgnoreCase));
                    if (app.Path != null)
                        try
                        {
                            bool file = System.IO.Path.IsPathRooted(app.Path) && File.Exists(app.Path);   // skrót / program - wprost; aplikacja ze Sklepu i inne - przez shell:AppsFolder\{id}
                            System.Diagnostics.Process.Start(file ? new System.Diagnostics.ProcessStartInfo(app.Path) { UseShellExecute = true } : new System.Diagnostics.ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + app.Path) { UseShellExecute = true });
                            App.LogInfo("Pilot: uruchomiono z telefonu - " + app.Name);
                        }
                        catch (Exception ex) { App.LogError(ex); }
                    break;
                case "pc-screen": ScreenPower(v == "on"); break;
                case "pc-lock": LockWorkStation(); break;
                case "pc-logoff": PcPower("/l"); break;
                case "pc-restart": PcPower("/r /t 0"); break;
                case "pc-shutdown": PcPower("/s /t 0"); break;
            }
        }

        // aplikacje z menu Start (użytkownika i wspólne) - nazwa skrótu i pełna ścieżka; bez odinstalowywania i pomocy
        // aplikacje jak w menu Start → „Wszystkie aplikacje” (shell:AppsFolder - także ze Sklepu Microsoft); zapasowo skróty z folderów menu Start.
        // Path = identyfikator aplikacji Windows (AUMID) albo pełna ścieżka skrótu. Liczone raz na minutę (lista bywa długa).
        static List<(string Name, string Path)> _pcApps; static DateTime _pcAppsAt;
        static List<(string Name, string Path)> PcApps()
        {
            if (_pcApps != null && DateTime.UtcNow - _pcAppsAt < TimeSpan.FromMinutes(1)) return _pcApps;
            var res = new List<(string Name, string Path)>();
            var th = new System.Threading.Thread(() =>   // Shell.Application wymaga wątku STA
            {
                try
                {
                    dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                    dynamic folder = sh.NameSpace("shell:AppsFolder");
                    foreach (dynamic it in folder.Items())
                    {
                        string n = it.Name, p = it.Path;
                        if (!string.IsNullOrWhiteSpace(n) && !string.IsNullOrWhiteSpace(p)) res.Add((n, p));
                    }
                }
                catch (Exception ex) { App.LogError(ex); }
            }) { IsBackground = true };
            th.SetApartmentState(System.Threading.ApartmentState.STA); th.Start(); th.Join(15000);
            var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };   // folder bez dostępu nie przerywa całej listy (było: 0 aplikacji)
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) })
            {
                try
                {
                    if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                    foreach (var f in Directory.EnumerateFiles(root, "*.lnk", opt)) res.Add((System.IO.Path.GetFileNameWithoutExtension(f), f));
                }
                catch (Exception ex) { App.LogError(ex); }
            }
            bool Skip(string n) { var low = n.ToLowerInvariant(); return low.Contains("uninstall") || low.Contains("odinstaluj") || low.Contains("readme"); }
            lock (res)
                _pcApps = res.Where(x => !Skip(x.Name)).GroupBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).Select(g => g.First()).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            _pcAppsAt = DateTime.UtcNow;
            App.LogInfo("Pilot: aplikacji dla telefonu: " + _pcApps.Count);
            return _pcApps;
        }

        // 🖼 ikona aplikacji dla kafelka w telefonie (PNG 96 px, base64) - ta sama, którą pokazuje menu Start
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory { [PreserveSig] int GetImage(PcSize size, int flags, out IntPtr hbmp); }
        [StructLayout(LayoutKind.Sequential)] struct PcSize { public int cx, cy; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)] static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
        static string PcAppIcon(string path)
        {
            if (PcApps().All(x => !string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase))) return null;   // tylko aplikacje z listy
            string res = null;
            var th = new System.Threading.Thread(() =>
            {
                IntPtr hb = IntPtr.Zero;
                try
                {
                    var iid = typeof(IShellItemImageFactory).GUID;
                    var name = System.IO.Path.IsPathRooted(path) && File.Exists(path) ? path : "shell:AppsFolder\\" + path;
                    if (SHCreateItemFromParsingName(name, IntPtr.Zero, ref iid, out var f) != 0 || f == null) return;
                    if (f.GetImage(new PcSize { cx = 96, cy = 96 }, 0x1 /* SIIGBF_BIGGERSIZEOK */, out hb) != 0 || hb == IntPtr.Zero) return;
                    var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hb, IntPtr.Zero, System.Windows.Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    var bgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Bgra32, null, 0);   // przezroczystość z ikony (inaczej czarne tło)
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bgra));
                    using (var ms = new MemoryStream()) { enc.Save(ms); res = Convert.ToBase64String(ms.ToArray()); }
                }
                catch (Exception ex) { App.LogError(ex); }
                finally { if (hb != IntPtr.Zero) DeleteObject(hb); }
            }) { IsBackground = true };
            th.SetApartmentState(System.Threading.ApartmentState.STA); th.Start(); th.Join(8000);
            return res;
        }

        static void PcPower(string args)
        {
            App.LogInfo("Pilot: polecenie z telefonu - shutdown " + args);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false }); }
            catch (Exception ex) { App.LogError(ex); }
        }
    }
}
