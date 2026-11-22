using System;
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
                case "pc-screen": ScreenPower(v == "on"); break;
                case "pc-lock": LockWorkStation(); break;
                case "pc-logoff": PcPower("/l"); break;
                case "pc-restart": PcPower("/r /t 0"); break;
                case "pc-shutdown": PcPower("/s /t 0"); break;
            }
        }

        static void PcPower(string args)
        {
            App.LogInfo("Pilot: polecenie z telefonu - shutdown " + args);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false }); }
            catch (Exception ex) { App.LogError(ex); }
        }
    }
}
