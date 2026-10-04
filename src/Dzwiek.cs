using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Przegladarka
{
    // Glosniki Velivo: dzwiek nie ginie, gdy program muzyczny (Ableton, Cubase - ASIO/WASAPI exclusive) zajmie karte.
    // Co 2 s sprawdzamy, czy wybrane (albo domyslne) wyjscie jest wolne. Zajete -> dzwiek Velivo idzie na inne aktywne
    // wyjscie; zwolnione -> wraca. Przypisanie urzadzenia do aplikacji to ten sam mechanizm Windows co w Mikserze glosnosci.
    public partial class MainWindow
    {
        // ---------------- MMDevice API ----------------
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetAt(int index, out PropKey key);
            [PreserveSig] int GetValue(ref PropKey key, out PropVariant value);
        }

        [StructLayout(LayoutKind.Sequential)] struct PropKey { public Guid fmtid; public int pid; }
        [StructLayout(LayoutKind.Sequential)] struct PropVariant { public ushort vt; ushort r1, r2, r3; public IntPtr p; IntPtr p2; }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint padding);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
        }

        // ---------------- przypisanie wyjscia do aplikacji (jak Mikser glosnosci) ----------------
        // IAudioPolicyConfigFactory (nieudokumentowany, uzywa go m.in. EarTrumpet): IUnknown(3) + IInspectable(3) + 19 metod,
        // potem SetPersistedDefaultAudioEndpoint - slot 25 w tablicy metod.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int SetPersistedDefaultAudioEndpointFn(IntPtr self, uint processId, int flow, int role, IntPtr deviceId);

        [DllImport("combase.dll", PreserveSig = true)] static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
        [DllImport("combase.dll", PreserveSig = true)] static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string src, int length, out IntPtr hstring);
        [DllImport("combase.dll", PreserveSig = true)] static extern int WindowsDeleteString(IntPtr hstring);

        const int eRender = 0, DEVICE_STATE_ACTIVE = 1, AUDCLNT_E_DEVICE_IN_USE = unchecked((int)0x8889000A);

        sealed class AudioOut { public string Id, Name; }

        static List<AudioOut> ListAudioOutputs()
        {
            var list = new List<AudioOut>();
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                en.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var col);
                col.GetCount(out var n);
                for (uint i = 0; i < n; i++)
                {
                    col.Item(i, out var d);
                    d.GetId(out var id);
                    list.Add(new AudioOut { Id = id, Name = DeviceName(d) ?? id });
                }
            }
            catch (Exception ex) { App.LogError(ex); }
            return list;
        }

        static string DeviceName(IMMDevice d)
        {
            try
            {
                d.OpenPropertyStore(0, out var ps);
                var key = new PropKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
                ps.GetValue(ref key, out var v);
                return v.vt == 31 && v.p != IntPtr.Zero ? Marshal.PtrToStringUni(v.p) : null;
            }
            catch (Exception) { return null; }
        }

        static string DefaultAudioOutputId()
        {
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                en.GetDefaultAudioEndpoint(eRender, 1, out var d);
                d.GetId(out var id);
                return id;
            }
            catch (Exception) { return null; }
        }

        // czy wyjscie przyjmie dzwiek w trybie wspoldzielonym (false = zajete na wylacznosc albo niedostepne)
        static bool AudioOutputFree(string id)
        {
            object o = null;
            IntPtr fmt = IntPtr.Zero;
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                if (en.GetDevice(id, out var d) != 0 || d == null) return false;
                d.GetState(out var state);
                if (state != DEVICE_STATE_ACTIVE) return false;
                var iid = typeof(IAudioClient).GUID;
                if (d.Activate(ref iid, 1 /*CLSCTX_INPROC_SERVER*/, IntPtr.Zero, out o) != 0) return false;
                var client = (IAudioClient)o;
                if (client.GetMixFormat(out fmt) != 0) return false;
                int hr = client.Initialize(0 /*shared*/, 0, 1000000, 0, fmt, IntPtr.Zero);
                return hr != AUDCLNT_E_DEVICE_IN_USE && hr >= 0;
            }
            catch (Exception) { return true; }   // nie wiadomo - nie przelaczamy
            finally
            {
                if (fmt != IntPtr.Zero) Marshal.FreeCoTaskMem(fmt);
                if (o != null) try { Marshal.ReleaseComObject(o); } catch (Exception) { }
            }
        }

        static IntPtr _policy;
        static SetPersistedDefaultAudioEndpointFn _setEndpoint;
        static bool AudioPolicy()
        {
            if (_setEndpoint != null) return true;
            IntPtr cls = IntPtr.Zero;
            try
            {
                const string name = "Windows.Media.Internal.AudioPolicyConfig";
                WindowsCreateString(name, name.Length, out cls);
                var iid = Environment.OSVersion.Version.Build >= 21390 ? new Guid("ab3d4648-e242-459f-b02f-541c70306324") : new Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258");
                if (RoGetActivationFactory(cls, ref iid, out var f) != 0 || f == IntPtr.Zero) return false;
                var vtbl = Marshal.ReadIntPtr(f);
                _setEndpoint = Marshal.GetDelegateForFunctionPointer<SetPersistedDefaultAudioEndpointFn>(Marshal.ReadIntPtr(vtbl, 25 * IntPtr.Size));
                _policy = f;   // trzymamy do konca dzialania programu
                return true;
            }
            catch (Exception ex) { App.LogError(ex); return false; }
            finally { if (cls != IntPtr.Zero) WindowsDeleteString(cls); }
        }

        // null/"" = wroc do domyslnego wyjscia Windows
        void RouteVelivoAudio(string mmDeviceId)
        {
            if (_env == null || !AudioPolicy()) return;
            IntPtr h = IntPtr.Zero;
            try
            {
                if (!string.IsNullOrEmpty(mmDeviceId))
                {
                    var full = @"\\?\SWD#MMDEVAPI#" + mmDeviceId + "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
                    WindowsCreateString(full, full.Length, out h);
                }
                var pids = new HashSet<uint>();
                try { foreach (var pi in _env.GetProcessInfos()) pids.Add((uint)pi.ProcessId); } catch (Exception) { }
                foreach (var pid in pids)
                {
                    _setEndpoint(_policy, pid, eRender, 0, h);   // eConsole
                    _setEndpoint(_policy, pid, eRender, 1, h);   // eMultimedia
                }
            }
            catch (Exception ex) { App.LogError(ex); }
            finally { if (h != IntPtr.Zero) WindowsDeleteString(h); }
        }

        DispatcherTimer _audioGuard;
        string _audioRoutedTo = "";   // "" = domyslne Windows
        DateTime _audioChangedAt = DateTime.MinValue;   // ostatnia zmiana urzadzenia (zajecie, zwolnienie, nowe domyslne)
        string _lastDefaultOut; bool? _lastWantedFree;
        readonly HashSet<BrowserTab> _wasPlaying = new HashSet<BrowserTab>();
        readonly List<Microsoft.Web.WebView2.Core.CoreWebView2> _floatCores = new List<Microsoft.Web.WebView2.Core.CoreWebView2>();

        // film sam sie zatrzymal tuz po zmianie urzadzenia dzwieku (np. Omnisphere zmienil czestotliwosc) - wznawiamy
        void ResumeAfterAudioChange()
        {
            foreach (var t in _tabs.ToList())
            {
                bool now;
                try { now = t.View.CoreWebView2 != null && t.View.CoreWebView2.IsDocumentPlayingAudio; } catch (Exception) { continue; }
                if (now) { _wasPlaying.Add(t); continue; }
                if (!_wasPlaying.Remove(t)) continue;
                if (DateTime.UtcNow - _audioChangedAt > TimeSpan.FromSeconds(8)) continue;
                try
                {
                    _ = t.View.CoreWebView2.ExecuteScriptAsync("(function(){ var mp=document.getElementById('movie_player'); document.querySelectorAll('video,audio').forEach(function(m){ if(m.paused && !m.ended && m.currentTime > 0 && !m.__velivoUserPaused){ try{ if(mp && mp.playVideo && mp.contains(m)){ mp.playVideo(); return; } }catch(e){} m.play().catch(function(){}); } }); })();");
                }
                catch (Exception) { }
            }
            _wasPlaying.RemoveWhere(t => !_tabs.Contains(t));
        }

        void StartAudioGuard()
        {
            if (_audioGuard != null) return;
            _audioGuard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _audioGuard.Tick += (s, e) =>
            {
                try
                {
                    if (_settings == null || !_settings.AudioGuard) { if (_audioRoutedTo != "") { RouteVelivoAudio(null); _audioRoutedTo = ""; } return; }
                    var def = DefaultAudioOutputId();
                    if (_lastDefaultOut != null && def != _lastDefaultOut) _audioChangedAt = DateTime.UtcNow;
                    _lastDefaultOut = def;
                    ResumeAfterAudioChange();
                    // sprawdzamy tylko, gdy cos gra (oszczednie)
                    // karty i okienka "Film na wierzchu" (graja takze po zamknieciu glownego okna)
                    bool playing = _tabs.Any(t => { try { return t.View.CoreWebView2 != null && t.View.CoreWebView2.IsDocumentPlayingAudio; } catch (Exception) { return false; } })
                        || _floatCores.ToList().Any(c => { try { return c.IsDocumentPlayingAudio; } catch (Exception) { _floatCores.Remove(c); return false; } });
                    if (!playing) return;
                    var wanted = string.IsNullOrEmpty(_settings.AudioOut) ? DefaultAudioOutputId() : _settings.AudioOut;
                    if (wanted == null) return;
                    string target;
                    bool free = AudioOutputFree(wanted);
                    if (_lastWantedFree.HasValue && _lastWantedFree.Value != free) _audioChangedAt = DateTime.UtcNow;
                    _lastWantedFree = free;
                    if (free) target = string.IsNullOrEmpty(_settings.AudioOut) ? "" : wanted;
                    else
                    {
                        // zajete (np. Ableton/Cubase) - pierwsze inne wolne wyjscie
                        var alt = ListAudioOutputs().FirstOrDefault(d => d.Id != wanted && AudioOutputFree(d.Id));
                        if (alt == null) return;
                        target = alt.Id;
                    }
                    if (target == _audioRoutedTo) return;
                    RouteVelivoAudio(target);
                    _audioRoutedTo = target;
                    _audioChangedAt = DateTime.UtcNow;
                    var name = target == "" ? null : ListAudioOutputs().FirstOrDefault(d => d.Id == target)?.Name;
                    ShowToast("🔊 " + L.T("Dźwięk Velivo: ") + (name ?? L.T("domyślne wyjście Windows")), null);
                }
                catch (Exception ex) { App.LogError(ex); }
            };
            _audioGuard.Start();
            // przy wyjsciu z programu: zadnego pozostawionego przypisania glosnikow w Windows
            try { System.Windows.Application.Current.Exit += (s3, e3) => { if (!string.IsNullOrEmpty(_audioRoutedTo) && string.IsNullOrEmpty(_settings?.AudioOut)) RouteVelivoAudio(null); }; } catch (Exception) { }
            // nowe procesy silnika (np. usluga dzwieku startuje dopiero przy pierwszym dzwieku) dostaja to samo wyjscie
            try { _env.ProcessInfosChanged += (s2, e2) => { if (!string.IsNullOrEmpty(_audioRoutedTo)) RouteVelivoAudio(_audioRoutedTo); }; } catch (Exception) { }
            if (_settings != null && !string.IsNullOrEmpty(_settings.AudioOut)) { RouteVelivoAudio(_settings.AudioOut); _audioRoutedTo = _settings.AudioOut; }
        }
    }
}
