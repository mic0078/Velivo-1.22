using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Velivo
{
    // 🎛 VST3: Twoje wtyczki (kompresory, limitery, korektory…) w torze dźwięku filmu. Gdy łańcuch VST3 jest aktywny, przejmuje przetwarzanie:
    //    VLC → wtyczki po kolei → głośność odtwarzacza → wyjście (korektor Velivo, dialogi i Audio Balance pominięte - bez podwójnego przetwarzania).
    // Host bez SDK i bez COM .NET: wywołania wprost przez tablice funkcji (vtable) - COM .NET wiąże obiekty z wątkiem, a dźwięk liczy inny wątek niż okno.
    // Interfejsy i IID wg pluginterfaces Steinberga (VST3 SDK, GPLv3/licencja Steinberg - tu tylko zgodny układ binarny, bez kodu SDK).
    public static unsafe class Vst3
    {
        // ---- IID (COM_COMPATIBLE na Windows: układ jak GUID) ----
        static byte[] Uid(uint a, uint b, uint c, uint d)
        {
            var g = new byte[16];
            BitConverter.GetBytes(a).CopyTo(g, 0); BitConverter.GetBytes((ushort)(b >> 16)).CopyTo(g, 4); BitConverter.GetBytes((ushort)b).CopyTo(g, 6);
            for (int i = 0; i < 4; i++) { g[8 + i] = (byte)(c >> (24 - 8 * i)); g[12 + i] = (byte)(d >> (24 - 8 * i)); }
            return g;
        }
        internal static readonly byte[] IID_FUnknown = Uid(0x00000000, 0x00000000, 0xC0000000, 0x00000046),
            IID_IPluginFactory = Uid(0x7A4D811C, 0x52114A1F, 0xAED9D2EE, 0x0B43BF9F),
            IID_IComponent = Uid(0xE831FF31, 0xF2D54301, 0x928EBBEE, 0x25697802),
            IID_IAudioProcessor = Uid(0x42043F99, 0xB7DA453C, 0xA569E79D, 0x9AAEC33D),
            IID_IEditController = Uid(0xDCD7BBE3, 0x7742448D, 0xA874AACC, 0x979C759E),
            IID_IPlugFrame = Uid(0x367FAF01, 0xAFA94693, 0x8D4DA2A0, 0xED0882A3),
            IID_IComponentHandler = Uid(0x93A0BEA3, 0x0BD045DB, 0x8E890B0C, 0xC1E46AC6),
            IID_IBStream = Uid(0xC3BF6EA2, 0x30994752, 0x9B6BF990, 0x1EE33E9B),
            IID_IParameterChanges = Uid(0xA4779663, 0x0BB64A56, 0xB44384A8, 0x466FEB9D),
            IID_IParamValueQueue = Uid(0x01263A18, 0xED074F6F, 0x98C9D356, 0x4686F9BA),
            IID_IHostApplication = Uid(0x58E595CC, 0xDB2D4969, 0x8B6AAF8C, 0x36A664E5),
            IID_IConnectionPoint = Uid(0x70A4156F, 0x6E6E4026, 0x989148BF, 0xAA60D8D1),
            IID_IMessage = Uid(0x936F033B, 0xC6C047DB, 0xBB0882F8, 0x13C1E613),
            IID_IAttributeList = Uid(0x1E5F0AEB, 0xCC7F4533, 0xA2544011, 0x38AD5EE4);

        const int kOk = 0, kFalse = 1, kNoInterface = unchecked((int)0x80004002), kNotImpl = unchecked((int)0x80004001), kInvalid = unchecked((int)0x80070057);
        internal static bool Same(IntPtr p, byte[] iid) { if (p == IntPtr.Zero) return false; for (int i = 0; i < 16; i++) if (((byte*)p)[i] != iid[i]) return false; return true; }

        // ---- wywołania wtyczki przez vtable ----
        static IntPtr Fn(IntPtr o, int slot) => ((IntPtr*)*(IntPtr*)o)[slot];
        internal static int QI(IntPtr o, byte[] iid, out IntPtr r) { IntPtr x = IntPtr.Zero; fixed (byte* p = iid) { int h = ((delegate* unmanaged[Stdcall]<IntPtr, byte*, IntPtr*, int>)Fn(o, 0))(o, p, &x); r = x; return h; } }
        internal static void AddRef(IntPtr o) { if (o != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Fn(o, 1))(o); }
        internal static void Release(IntPtr o) { if (o != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Fn(o, 2))(o); }
        static int C0(IntPtr o, int s) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Fn(o, s))(o);
        static int CI(IntPtr o, int s, int a) => ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Fn(o, s))(o, a);
        static int CP(IntPtr o, int s, IntPtr a) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Fn(o, s))(o, a);

        // ---- obiekty hosta (vtable budowane tutaj, „this” = uchwyt GC) ----
        internal abstract class NObj
        {
            public readonly IntPtr Ptr; GCHandle h; int refs = 1; readonly byte[] iid; bool freed;
            protected NObj(IntPtr vtbl, byte[] iid)
            {
                this.iid = iid; h = GCHandle.Alloc(this); Ptr = Marshal.AllocHGlobal(2 * IntPtr.Size);
                ((IntPtr*)Ptr)[0] = vtbl; ((IntPtr*)Ptr)[1] = GCHandle.ToIntPtr(h);
            }
            public static T Of<T>(IntPtr self) where T : NObj => (T)GCHandle.FromIntPtr(((IntPtr*)self)[1]).Target;
            public int Query(IntPtr riid, IntPtr* obj) { if (Same(riid, iid) || Same(riid, IID_FUnknown)) { Interlocked.Increment(ref refs); *obj = Ptr; return kOk; } *obj = IntPtr.Zero; return kNoInterface; }
            public uint Add() => (uint)Interlocked.Increment(ref refs);
            public uint Rel() { int r = Interlocked.Decrement(ref refs); if (r == 0) Free(); return (uint)Math.Max(0, r); }
            public void Free() { if (freed) return; freed = true; OnFree(); Marshal.FreeHGlobal(Ptr); h.Free(); }
            protected virtual void OnFree() { }
        }
        static IntPtr Vtbl(params IntPtr[] f) { var p = (IntPtr*)Marshal.AllocHGlobal(f.Length * IntPtr.Size); for (int i = 0; i < f.Length; i++) p[i] = f[i]; return (IntPtr)p; }   // na cały czas działania programu
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static int UQi(IntPtr s, IntPtr iid, IntPtr* o) => NObj.Of<NObj>(s).Query(iid, o);
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static uint UAdd(IntPtr s) => NObj.Of<NObj>(s).Add();
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static uint URel(IntPtr s) => NObj.Of<NObj>(s).Rel();
        static IntPtr[] Unk => new[] { (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)&UQi, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&UAdd, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&URel };

        // IHostApplication: nazwa hosta i tworzenie wiadomości (IMessage) dla połączenia procesor ↔ okno wtyczki
        sealed class HostApp : NObj
        {
            static readonly IntPtr V = Vtbl(Unk.Concat(new[] { (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, char*, int>)&GetName, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)&Create }).ToArray());
            public HostApp() : base(V, IID_IHostApplication) { }
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static int GetName(IntPtr s, char* name) { var n = "Velivo"; for (int i = 0; i < n.Length; i++) name[i] = n[i]; name[n.Length] = '\0'; return kOk; }
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
            static int Create(IntPtr s, IntPtr cid, IntPtr iid, IntPtr* obj)
            {
                *obj = IntPtr.Zero;
                if (Same(cid, IID_IMessage) && Same(iid, IID_IMessage)) { *obj = new Message().Ptr; return kOk; }
                if (Same(cid, IID_IAttributeList) && Same(iid, IID_IAttributeList)) { *obj = new Attrs().Ptr; return kOk; }
                return kNoInterface;
            }
        }
        sealed class Message : NObj
        {
            static readonly IntPtr V = Vtbl(Unk.Concat(new[] { (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)&GetId, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, sbyte*, void>)&SetId, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)&GetAttr }).ToArray());
            IntPtr id; readonly Attrs attrs = new Attrs();
            public Message() : base(V, IID_IMessage) { }
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static IntPtr GetId(IntPtr s) => Of<Message>(s).id;
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static void SetId(IntPtr s, sbyte* v) { var m = Of<Message>(s); if (m.id != IntPtr.Zero) Marshal.FreeHGlobal(m.id); m.id = v == null ? IntPtr.Zero : Marshal.StringToHGlobalAnsi(new string(v)); }
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static IntPtr GetAttr(IntPtr s) => Of<Message>(s).attrs.Ptr;
            protected override void OnFree() { if (id != IntPtr.Zero) Marshal.FreeHGlobal(id); attrs.Rel(); }
        }
        sealed class Attrs : NObj
        {
            static readonly IntPtr V = Vtbl(Unk.Concat(new[] {
                (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, sbyte*, long, int>)&SetInt, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, sbyte*, long*, int>)&GetInt,
                (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, sbyte*, double, int>)&SetFloat, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, sbyte*, double*, int>)&GetFloat,
                (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, sbyte*, char*, int>)&SetStr, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, sbyte*, char*, uint, int>)&GetStr,
                (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, sbyte*, void*, uint, int>)&SetBin, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, sbyte*, void**, uint*, int>)&GetBin }).ToArray());
            readonly Dictionary<string, object> v = new Dictionary<string, object>(); readonly List<IntPtr> bins = new List<IntPtr>();
            public Attrs() : base(V, IID_IAttributeList) { }
            static Dictionary<string, object> D(IntPtr s) => Of<Attrs>(s).v;
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static int SetInt(IntPtr s, sbyte* k, long x) { D(s)[new string(k)] = x; return kOk; }
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static int GetInt(IntPtr s, sbyte* k, long* x) { if (D(s).TryGetValue(new string(k), out var o) && o is long l) { *x = l; return kOk; } return kFalse; }
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static int SetFloat(IntPtr s, sbyte* k, double x) { D(s)[new string(k)] = x; return kOk; }
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static int GetFloat(IntPtr s, sbyte* k, double* x) { if (D(s).TryGetValue(new string(k), out var o) && o is double d) { *x = d; return kOk; } return kFalse; }
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static int SetStr(IntPtr s, sbyte* k, char* x) { D(s)[new string(k)] = x == null ? "" : new string(x); return kOk; }
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
            static int GetStr(IntPtr s, sbyte* k, char* x, uint bytes)
            {
                if (!D(s).TryGetValue(new string(k), out var o) || !(o is string t) || bytes < 2) return kFalse;
                int n = Math.Min(t.Length, (int)(bytes / 2) - 1); for (int i = 0; i < n; i++) x[i] = t[i]; x[n] = '\0'; return kOk;
            }
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static int SetBin(IntPtr s, sbyte* k, void* d, uint n) { var b = new byte[n]; if (n > 0) Marshal.Copy((IntPtr)d, b, 0, (int)n); D(s)[new string(k)] = b; return kOk; }
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
            static int GetBin(IntPtr s, sbyte* k, void** d, uint* n)
            {
                var a = Of<Attrs>(s); if (!a.v.TryGetValue(new string(k), out var o) || !(o is byte[] b)) return kFalse;
                var p = Marshal.AllocHGlobal(Math.Max(1, b.Length)); Marshal.Copy(b, 0, p, b.Length); a.bins.Add(p); *d = (void*)p; *n = (uint)b.Length; return kOk;   // ważne do zwolnienia listy
            }
            protected override void OnFree() { foreach (var p in bins) Marshal.FreeHGlobal(p); }
        }

        // IBStream w pamięci - stan wtyczki (presety) do zapisu i odtworzenia
        internal sealed class Stream : NObj
        {
            static readonly IntPtr V = Vtbl(Unk.Concat(new[] { (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, void*, int, int*, int>)&Read, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, void*, int, int*, int>)&Write,
                (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, long, int, long*, int>)&Seek, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, long*, int>)&Tell }).ToArray());
            public readonly MemoryStream M;
            public Stream(byte[] data = null) : base(V, IID_IBStream) { M = data == null ? new MemoryStream() : new MemoryStream(data.ToArray()); }
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static int Read(IntPtr s, void* b, int n, int* got) { var m = Of<Stream>(s).M; var t = new byte[Math.Max(0, n)]; int r = m.Read(t, 0, t.Length); if (r > 0) Marshal.Copy(t, 0, (IntPtr)b, r); if (got != null) *got = r; return kOk; }
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static int Write(IntPtr s, void* b, int n, int* put) { var m = Of<Stream>(s).M; var t = new byte[Math.Max(0, n)]; if (n > 0) Marshal.Copy((IntPtr)b, t, 0, n); m.Write(t, 0, t.Length); if (put != null) *put = n; return kOk; }
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static int Seek(IntPtr s, long pos, int mode, long* res) { var m = Of<Stream>(s).M; long p = m.Seek(pos, mode == 1 ? SeekOrigin.Current : mode == 2 ? SeekOrigin.End : SeekOrigin.Begin); if (res != null) *res = p; return kOk; }
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static int Tell(IntPtr s, long* pos) { if (pos != null) *pos = Of<Stream>(s).M.Position; return kOk; }
        }

        // ruch gałki w oknie wtyczki (wątek okna) → kolejka → zmiany parametrów w najbliższym bloku dźwięku (wątek dźwięku) - jak w każdym hoście VST3
        sealed class Handler : NObj
        {
            static readonly IntPtr V = Vtbl(Unk.Concat(new[] { (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, int>)&Begin, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, double, int>)&Perform,
                (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, int>)&End, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int>)&Restart }).ToArray());
            public readonly Plugin P; public Handler(Plugin p) : base(V, IID_IComponentHandler) { P = p; }
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static int Begin(IntPtr s, uint id) => kOk;
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static int Perform(IntPtr s, uint id, double v) { Of<Handler>(s).P.Pending.Enqueue((id, v)); return kOk; }
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static int End(IntPtr s, uint id) => kOk;
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static int Restart(IntPtr s, int flags) { var p = Of<Handler>(s).P; p.LatencyDirty = true; return kOk; }   // m.in. zmiana opóźnienia wtyczki
        }
        internal sealed class Queue : NObj
        {
            static readonly IntPtr V = Vtbl(Unk.Concat(new[] { (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&Id, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&Count,
                (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int*, double*, int>)&Get, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, double, int*, int>)&Add }).ToArray());
            public uint PId; public double Val; public Queue() : base(V, IID_IParamValueQueue) { }
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static uint Id(IntPtr s) => Of<Queue>(s).PId;
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static int Count(IntPtr s) => 1;
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static int Get(IntPtr s, int i, int* off, double* v) { if (i != 0) return kInvalid; *off = 0; *v = Of<Queue>(s).Val; return kOk; }
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static int Add(IntPtr s, int off, double v, int* i) { Of<Queue>(s).Val = v; *i = 0; return kOk; }
        }
        internal sealed class Changes : NObj
        {
            static readonly IntPtr V = Vtbl(Unk.Concat(new[] { (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&Count, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr>)&Data,
                (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint*, int*, IntPtr>)&AddData }).ToArray());
            public readonly Queue[] Q = Enumerable.Range(0, 64).Select(_ => new Queue()).ToArray(); public int N;
            public Changes() : base(V, IID_IParameterChanges) { }
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static int Count(IntPtr s) => Of<Changes>(s).N;
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static IntPtr Data(IntPtr s, int i) { var c = Of<Changes>(s); return i >= 0 && i < c.N ? c.Q[i].Ptr : IntPtr.Zero; }
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] static IntPtr AddData(IntPtr s, uint* id, int* idx) { *idx = -1; return IntPtr.Zero; }   // wejściowe zmiany wypełnia tylko host
            protected override void OnFree() { foreach (var q in Q) q.Free(); }
        }
        internal sealed class Frame : NObj
        {
            static readonly IntPtr V = Vtbl(Unk.Concat(new[] { (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int*, int>)&Resize }).ToArray());
            public Action<int, int> OnResize; public Frame() : base(V, IID_IPlugFrame) { }
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
            static int Resize(IntPtr s, IntPtr view, int* r) { Of<Frame>(s).OnResize?.Invoke(r[2] - r[0], r[3] - r[1]); if (view != IntPtr.Zero) CP(view, 10, (IntPtr)r); return kOk; }   // onSize
        }

        // ---- struktury przetwarzania (układ jak w SDK, wyrównanie 8) ----
        [StructLayout(LayoutKind.Sequential)] struct ProcessSetup { public int Mode, SampleSize, MaxBlock; public double Rate; }
        [StructLayout(LayoutKind.Sequential)] struct AudioBus { public int Channels; public ulong Silence; public IntPtr Buffers; }
        [StructLayout(LayoutKind.Sequential)] struct ProcessData { public int Mode, SampleSize, NumSamples, NumIn, NumOut; public IntPtr In, Out, InParams, OutParams, InEvents, OutEvents, Context; }
        [StructLayout(LayoutKind.Sequential)] struct ClassInfo { public fixed byte Cid[16]; public int Cardinality; public fixed byte Category[32]; public fixed byte Name[64]; }

        public const int MaxBlock = 8192;
        static readonly HostApp App = new HostApp();

        // ---- jedna wtyczka ----
        public sealed class Plugin : IDisposable
        {
            public string Path, Name, Cid; public volatile bool On = true; public int Latency; internal volatile bool LatencyDirty;
            internal readonly System.Collections.Concurrent.ConcurrentQueue<(uint, double)> Pending = new System.Collections.Concurrent.ConcurrentQueue<(uint, double)>();
            IntPtr lib, factory, comp, proc, ctrl; Handler handler; Changes changes; int nIn, nOut; IntPtr bufIn, bufOut, chIn, chOut, data; bool ctrlSeparate, active;
            IntPtr cpComp, cpCtrl;

            public static Plugin Load(string path, string cid = null)
            {
                var p = new Plugin { Path = path };
                try { p.Open(cid); return p; } catch { p.Dispose(); throw; }
            }
            static string Dll(string path)
            {   // pakiet .vst3 (folder): Contents\x86_64-win\<nazwa>.vst3
                if (Directory.Exists(path)) { var f = System.IO.Path.Combine(path, "Contents", "x86_64-win", System.IO.Path.GetFileName(path)); if (File.Exists(f)) return f; var any = Directory.GetFiles(System.IO.Path.Combine(path, "Contents", "x86_64-win"), "*.vst3").FirstOrDefault(); if (any != null) return any; }
                return path;
            }
            void Open(string wantCid)
            {
                lib = NativeLibrary.Load(Dll(Path));
                if (NativeLibrary.TryGetExport(lib, "InitDll", out var init)) ((delegate* unmanaged[Stdcall]<byte>)init)();
                if (!NativeLibrary.TryGetExport(lib, "GetPluginFactory", out var gf)) throw new Exception(L.T("To nie jest wtyczka VST3 (brak GetPluginFactory)"));
                factory = ((delegate* unmanaged[Stdcall]<IntPtr>)gf)();
                if (factory == IntPtr.Zero) throw new Exception(L.T("Wtyczka nie udostępnia fabryki VST3"));
                int n = C0(factory, 4); byte[] cid = null;
                for (int i = 0; i < n && cid == null; i++)
                {
                    ClassInfo ci; if (((delegate* unmanaged[Stdcall]<IntPtr, int, ClassInfo*, int>)Fn(factory, 5))(factory, i, &ci) != kOk) continue;
                    var cat = new string((sbyte*)ci.Category); if (cat != "Audio Module Class") continue;
                    var id = new byte[16]; for (int k = 0; k < 16; k++) id[k] = ci.Cid[k];
                    var hex = Convert.ToHexString(id); if (wantCid != null && hex != wantCid) continue;
                    cid = id; Cid = hex; Name = new string((sbyte*)ci.Name);
                }
                if (cid == null) throw new Exception(L.T("Wtyczka nie zawiera procesora dźwięku VST3"));
                comp = Create(cid, IID_IComponent) ?? throw new Exception(L.T("Wtyczka nie utworzyła procesora"));
                if (CP(comp, 3, App.Ptr) != kOk) throw new Exception(L.T("Wtyczka nie dała się uruchomić (initialize)"));
                if (QI(comp, IID_IAudioProcessor, out proc) != kOk) throw new Exception(L.T("Wtyczka nie przetwarza dźwięku (IAudioProcessor)"));
                // kontroler (okno i parametry): ten sam obiekt albo osobna klasa
                if (QI(comp, IID_IEditController, out ctrl) != kOk)
                {
                    var cc = new byte[16]; fixed (byte* pc = cc) ((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)Fn(comp, 5))(comp, pc);
                    if (cc.Any(b => b != 0)) { ctrl = Create(cc, IID_IEditController) ?? IntPtr.Zero; if (ctrl != IntPtr.Zero) { ctrlSeparate = true; CP(ctrl, 3, App.Ptr); } }
                }
                handler = new Handler(this); changes = new Changes();
                if (ctrl != IntPtr.Zero)
                {
                    CP(ctrl, 16, handler.Ptr);
                    if (ctrlSeparate && QI(comp, IID_IConnectionPoint, out cpComp) == kOk && QI(ctrl, IID_IConnectionPoint, out cpCtrl) == kOk) { CP(cpComp, 3, cpCtrl); CP(cpCtrl, 3, cpComp); }
                    SyncCtrl();
                }
                // magistrale: wszystkie audio stereo; dźwięk filmu w pierwszej, pozostałe (np. sidechain) - cisza
                nIn = Math.Max(0, ((delegate* unmanaged[Stdcall]<IntPtr, int, int, int>)Fn(comp, 7))(comp, 0, 0));
                nOut = Math.Max(1, ((delegate* unmanaged[Stdcall]<IntPtr, int, int, int>)Fn(comp, 7))(comp, 0, 1));
                var arrIn = stackalloc ulong[Math.Max(1, nIn)]; var arrOut = stackalloc ulong[nOut];
                for (int i = 0; i < nIn; i++) arrIn[i] = 3; for (int i = 0; i < nOut; i++) arrOut[i] = 3;   // kStereo (L|R)
                ((delegate* unmanaged[Stdcall]<IntPtr, ulong*, int, ulong*, int, int>)Fn(proc, 3))(proc, arrIn, nIn, arrOut, nOut);
                for (int i = 0; i < nIn; i++) ((delegate* unmanaged[Stdcall]<IntPtr, int, int, int, byte, int>)Fn(comp, 10))(comp, 0, 0, i, 1);
                for (int i = 0; i < nOut; i++) ((delegate* unmanaged[Stdcall]<IntPtr, int, int, int, byte, int>)Fn(comp, 10))(comp, 0, 1, i, 1);
                if (CI(proc, 5, 0) != kOk) throw new Exception(L.T("Wtyczka nie obsługuje dźwięku 32-bit float"));
                var ps = new ProcessSetup { Mode = 0, SampleSize = 0, MaxBlock = MaxBlock, Rate = AbProcessor.Fs };
                ((delegate* unmanaged[Stdcall]<IntPtr, ProcessSetup*, int>)Fn(proc, 7))(proc, &ps);
                // bufory raz na zawsze - wątek dźwięku bez przydziałów pamięci
                int busSz = sizeof(AudioBus); bufIn = Marshal.AllocHGlobal(Math.Max(1, nIn) * busSz); bufOut = Marshal.AllocHGlobal(nOut * busSz);
                chIn = Alloc(Math.Max(1, nIn)); chOut = Alloc(nOut);
                for (int i = 0; i < nIn; i++) ((AudioBus*)bufIn)[i] = new AudioBus { Channels = 2, Silence = i == 0 ? 0UL : 3UL, Buffers = chIn + i * 2 * IntPtr.Size };
                for (int i = 0; i < nOut; i++) ((AudioBus*)bufOut)[i] = new AudioBus { Channels = 2, Silence = 0, Buffers = chOut + i * 2 * IntPtr.Size };
                data = Marshal.AllocHGlobal(sizeof(ProcessData));
                ((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Fn(comp, 11))(comp, 1); active = true;
                CI(proc, 8, 1);
                Latency = Math.Max(0, C0(proc, 6));
            }
            static IntPtr Alloc(int buses)
            {   // tablica wskaźników kanałów: [bus][L,R] → bufory MaxBlock próbek float (wyzerowane)
                var t = Marshal.AllocHGlobal(buses * 2 * IntPtr.Size);
                for (int i = 0; i < buses * 2; i++) { var b = Marshal.AllocHGlobal(MaxBlock * 4); new Span<byte>((void*)b, MaxBlock * 4).Clear(); ((IntPtr*)t)[i] = b; }
                return t;
            }
            IntPtr? Create(byte[] cid, byte[] iid) { IntPtr o = IntPtr.Zero; fixed (byte* c = cid) fixed (byte* i = iid) { if (((delegate* unmanaged[Stdcall]<IntPtr, byte*, byte*, IntPtr*, int>)Fn(factory, 6))(factory, c, i, &o) != kOk || o == IntPtr.Zero) return null; } return o; }
            void SyncCtrl()
            {   // okno wtyczki pokazuje stan procesora (po starcie i po wczytaniu presetu)
                if (ctrl == IntPtr.Zero) return; var st = new Stream(); try { if (CP(comp, 13, st.Ptr) == kOk) { st.M.Position = 0; CP(ctrl, 5, st.Ptr); } } finally { st.Rel(); }
            }
            public byte[] GetState() { var st = new Stream(); try { return CP(comp, 13, st.Ptr) == kOk ? st.M.ToArray() : null; } finally { st.Rel(); } }
            public void SetState(byte[] b)
            {
                if (b == null || b.Length == 0) return; var st = new Stream(b); try { CP(comp, 12, st.Ptr); } finally { st.Rel(); }
                SyncCtrl(); Latency = Math.Max(0, C0(proc, 6));
            }

            // wątek dźwięku: blok stereo (n ≤ MaxBlock) w miejscu
            public void Process(float[] l, float[] r, int n)
            {
                if (LatencyDirty) { LatencyDirty = false; Latency = Math.Max(0, C0(proc, 6)); }
                var inL = (float*)((IntPtr*)chIn)[0]; var inR = (float*)((IntPtr*)chIn)[1];
                for (int i = 0; i < n; i++) { inL[i] = l[i]; inR[i] = r[i]; }
                changes.N = 0;
                while (changes.N < changes.Q.Length && Pending.TryDequeue(out var pv)) { var q = changes.Q[changes.N++]; q.PId = pv.Item1; q.Val = pv.Item2; }
                var d = (ProcessData*)data;
                *d = new ProcessData { Mode = 0, SampleSize = 0, NumSamples = n, NumIn = nIn, NumOut = nOut, In = nIn > 0 ? bufIn : IntPtr.Zero, Out = bufOut, InParams = changes.Ptr };
                if (CP(proc, 9, data) != kOk) return;   // błąd wtyczki - ten blok bez zmian
                var oL = (float*)((IntPtr*)chOut)[0]; var oR = (float*)((IntPtr*)chOut)[1];
                for (int i = 0; i < n; i++) { float a = oL[i], b = oR[i]; l[i] = float.IsFinite(a) ? a : 0; r[i] = float.IsFinite(b) ? b : 0; }
            }

            // okno producenta wtyczki (wątek okna)
            public void ShowEditor(string title)
            {
                if (ctrl == IntPtr.Zero) throw new Exception(L.T("Ta wtyczka nie ma własnego okna"));
                IntPtr view; fixed (byte* nm = Encoding.ASCII.GetBytes("editor\0")) view = ((delegate* unmanaged[Stdcall]<IntPtr, byte*, IntPtr>)Fn(ctrl, 17))(ctrl, nm);
                if (view == IntPtr.Zero) throw new Exception(L.T("Ta wtyczka nie ma własnego okna"));
                fixed (byte* t = Encoding.ASCII.GetBytes("HWND\0")) if (((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)Fn(view, 3))(view, t) != kOk) { Release(view); throw new Exception(L.T("Okno wtyczki nie obsługuje Windows (HWND)")); }
                var form = new System.Windows.Forms.Form { Text = title, FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle, MaximizeBox = false, StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen, TopMost = true };
                var fr = new Frame(); fr.OnResize = (w, h) => { try { form.ClientSize = new System.Drawing.Size(w, h); } catch (Exception) { } };
                var rc = stackalloc int[4]; if (CP(view, 9, (IntPtr)rc) == kOk && rc[2] > rc[0]) form.ClientSize = new System.Drawing.Size(rc[2] - rc[0], rc[3] - rc[1]);
                CP(view, 12, fr.Ptr);
                fixed (byte* t = Encoding.ASCII.GetBytes("HWND\0")) ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, byte*, int>)Fn(view, 4))(view, form.Handle, t);
                form.FormClosed += (s, e) => { try { C0(view, 5); CP(view, 12, IntPtr.Zero); Release(view); } catch (Exception) { } fr.Rel(); try { EditorClosed?.Invoke(); } catch (Exception) { } };   // zamknięcie okna wtyczki = zapis jej ustawień od razu
                form.Show();
            }

            bool disposed;
            public void Dispose()
            {
                if (disposed) return; disposed = true;   // drugie zwolnienie tej samej wtyczki (usunięcie + zamknięcie odtwarzacza) wołało już zwolnione obiekty - błąd, możliwe zamknięcie Velivo
                try
                {
                    if (active) { CI(proc, 8, 0); ((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Fn(comp, 11))(comp, 0); active = false; }
                    if (cpComp != IntPtr.Zero && cpCtrl != IntPtr.Zero) { CP(cpComp, 4, cpCtrl); CP(cpCtrl, 4, cpComp); }
                    Release(cpComp); Release(cpCtrl);
                    if (ctrl != IntPtr.Zero) { CP(ctrl, 16, IntPtr.Zero); if (ctrlSeparate) C0(ctrl, 4); Release(ctrl); }
                    Release(proc); if (comp != IntPtr.Zero) { C0(comp, 4); Release(comp); }
                    Release(factory);
                }
                catch (Exception ex) { App2.Log(ex); }
                foreach (var t in new[] { chIn, chOut }) if (t != IntPtr.Zero) { int k = (t == chIn ? Math.Max(1, nIn) : nOut) * 2; for (int i = 0; i < k; i++) Marshal.FreeHGlobal(((IntPtr*)t)[i]); Marshal.FreeHGlobal(t); }
                foreach (var t in new[] { bufIn, bufOut, data }) if (t != IntPtr.Zero) Marshal.FreeHGlobal(t);
                chIn = chOut = bufIn = bufOut = data = IntPtr.Zero;
                handler?.Free(); changes?.Free();
                // biblioteki wtyczki nie zwalniamy (FreeLibrary) - część wtyczek ma wątki tła, które po zwolnieniu wysypałyby program
            }
        }
        static class App2 { public static void Log(Exception ex) { try { Velivo.App.LogError(ex); } catch (Exception) { } } }

        // ---- łańcuch (wspólny dla procesora dźwięku i okna) ----
        public static volatile Plugin[] Chain = Array.Empty<Plugin>();
        public static volatile bool Active;                 // łańcuch przejmuje przetwarzanie
        public static readonly object Lock = new object();  // blok dźwięku vs zmiana łańcucha w oknie
        public static int Latency { get { int s = 0; if (Active) foreach (var p in Chain) if (p.On) s += p.Latency; return s; } }
        public static bool Ready { get { if (!Active) return false; foreach (var p in Chain) if (p.On) return true; return false; } }   // aktywne i choć jedna wtyczka włączona (bez LINQ - wątek dźwięku)
        static readonly float[] bl = new float[MaxBlock], br = new float[MaxBlock];

        // wątek dźwięku: buf przeplatany stereo; true = przetworzone przez wtyczki
        public static bool Run(float[] buf, int frames)
        {
            if (!Ready) return false;
            lock (Lock)
            {
                for (int off = 0; off < frames; off += MaxBlock)
                {
                    int n = Math.Min(MaxBlock, frames - off);
                    for (int i = 0; i < n; i++) { bl[i] = buf[2 * (off + i)]; br[i] = buf[2 * (off + i) + 1]; }
                    foreach (var p in Chain) if (p.On) { try { p.Process(bl, br, n); } catch (Exception ex) { p.On = false; App2.Log(ex); } }
                    for (int i = 0; i < n; i++) { buf[2 * (off + i)] = bl[i]; buf[2 * (off + i) + 1] = br[i]; }
                }
            }
            return true;
        }

        // domyślne foldery wtyczek VST3 w Windows
        // instrument wg moduleinfo.json pakietu VST3 (Sub Categories z „Instrument”, bez „Fx”); brak pliku = pokazujemy (nie wiadomo)
        public static bool IsInstrument(string bundle)
        {   // syntezator tylko wtedy, gdy KAŻDA klasa audio pakietu ma podkategorię „Instrument” i żadna „Fx” - każdy efekt (także z dodatkiem instrumentu) zostaje na liście
            try
            {
                var mi = System.IO.Path.Combine(bundle, "Contents", "Resources", "moduleinfo.json");
                if (!File.Exists(mi)) return false;
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(mi), new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
                if (!doc.RootElement.TryGetProperty("Classes", out var cls) || cls.ValueKind != System.Text.Json.JsonValueKind.Array) return false;
                int audio = 0, instr = 0;
                foreach (var c in cls.EnumerateArray())
                {
                    if (!c.TryGetProperty("Category", out var cat) || cat.GetString() != "Audio Module Class") continue;
                    audio++;
                    var subs = new List<string>();
                    if (c.TryGetProperty("Sub Categories", out var sc)) { if (sc.ValueKind == System.Text.Json.JsonValueKind.Array) foreach (var x in sc.EnumerateArray()) subs.Add(x.GetString() ?? ""); else if (sc.ValueKind == System.Text.Json.JsonValueKind.String) subs.AddRange((sc.GetString() ?? "").Split('|')); }
                    if (subs.Any(x => x.Equals("Instrument", StringComparison.OrdinalIgnoreCase)) && !subs.Any(x => x.Equals("Fx", StringComparison.OrdinalIgnoreCase))) instr++;
                }
                return audio > 0 && instr == audio;
            }
            catch (Exception) { return false; }
        }

        // krótki opis wtyczki do podpowiedzi pod myszką: rodzaj efektu i producent z moduleinfo.json pakietu (brak pliku - tylko „efekt dźwiękowy”)
        static readonly Dictionary<string, string> _descCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static string Describe(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            lock (_descCache) if (_descCache.TryGetValue(path, out var c)) return c;
            string kind = null, vendor = null;
            try
            {
                var bundle = path; while (bundle != null && !Directory.Exists(System.IO.Path.Combine(bundle, "Contents"))) bundle = System.IO.Path.GetDirectoryName(bundle);
                var mi = bundle == null ? null : System.IO.Path.Combine(bundle, "Contents", "Resources", "moduleinfo.json");
                if (mi != null && File.Exists(mi))
                {
                    var t = File.ReadAllText(mi);
                    var m = System.Text.RegularExpressions.Regex.Match(t, "\"Vendor\"\\s*:\\s*\"([^\"]+)\""); if (m.Success) vendor = m.Groups[1].Value;
                    var kinds = new List<string>();
                    foreach (var (key, pl) in new[] { ("EQ", L.T("korektor (barwa dźwięku)")), ("Dynamics", L.T("dynamika: kompresor / limiter")), ("Restoration", L.T("odszumianie i naprawa dźwięku")), ("Reverb", L.T("pogłos")), ("Delay", L.T("echo")), ("Spatial", L.T("przestrzeń i stereo")), ("Distortion", L.T("przester / „ciepło”")), ("Filter", L.T("filtr")), ("Analyzer", L.T("analizator")), ("Mastering", L.T("mastering (wykończenie całości)")), ("Modulation", L.T("modulacja (chorus, flanger)")), ("Pitch Shift", L.T("zmiana wysokości głosu")) })
                        if (t.IndexOf("|" + key, StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("\"" + key, StringComparison.OrdinalIgnoreCase) >= 0) kinds.Add(pl);
                    if (kinds.Count > 0) kind = string.Join(", ", kinds.Distinct().Take(3));
                }
            }
            catch (Exception) { }
            var d = (kind ?? L.T("efekt dźwiękowy")) + (vendor != null ? " · " + vendor : "");
            d = char.ToUpper(d[0]) + d.Substring(1);
            lock (_descCache) _descCache[path] = d;
            return d;
        }

        public static Action EditorClosed;   // okno producenta zamknięte - Velivo zapisuje stan wtyczek (vst3.json)

        public static List<string> Scan()
        {
            var res = new List<string>();
            foreach (var root in new[] { System.IO.Path.Combine(AppContext.BaseDirectory, "VST3"), System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST3"), System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Common", "VST3") }.Distinct())
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    var stack = new Stack<string>(); stack.Push(root);
                    while (stack.Count > 0)
                    {
                        var d = stack.Pop();
                        foreach (var e in Directory.EnumerateFileSystemEntries(d))
                            if (e.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase)) { if (!IsInstrument(e)) res.Add(e); }   // syntezatory (Analog Lab, ARP…) nie przetwarzają dźwięku filmu - pomijane
                            else if (Directory.Exists(e)) stack.Push(e);
                    }
                }
                catch (Exception ex) { App2.Log(ex); }
            }
            return res.OrderBy(x => System.IO.Path.GetFileNameWithoutExtension(x), StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
