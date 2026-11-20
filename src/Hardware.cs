using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FoxideDisk
{
    // =====================================================================================================================
    // Hardware sensors without kernel drivers: WMI / registry / performance counters / D3DKMT (gdi32) and the vendors'
    // user-mode libraries (NVIDIA nvml.dll, AMD atiadlxx.dll). Every native call is tolerant: a missing DLL, export or
    // WMI class simply leaves the value null, nothing throws out of the public API.
    //   HwInfo.Gpus()            — slow, cached adapter detection
    //   new HwSensorsLive().Poll — periodic readings (CPU/GPU/RAM temperatures, fans, NVIDIA fan control)
    // =====================================================================================================================

    /// <summary>A display adapter (static facts, detected once).</summary>
    public class GpuInfo
    {
        public int Index;                    // position in HwInfo.Gpus() (discrete first)
        public string Name;
        public string Vendor;                // "NVIDIA" | "AMD" | "Intel" | "Other"
        public int VendorId, DeviceId;       // PCI ids (0 when unknown)
        public string PnpId;
        public int Bus = -1;                 // PCI bus number (-1 unknown)
        public bool Integrated;
        public ulong VramBytes;              // dedicated video memory
        public string VramSource;            // "registry" | "NVML" | "D3DKMT" | "WMI"
        public string DriverVersion;         // Windows driver version, e.g. 32.0.15.6636
        public string VendorDriver;          // vendor version, e.g. 566.36 (NVIDIA)
        public DateTime? DriverDate;
        public int? Width, Height, RefreshHz, BitsPerPixel;
        // NVIDIA (NVML) extras
        public int? PcieGen, PcieWidth, PcieMaxGen, PcieMaxWidth;
        public double? PowerLimitW, PowerDefaultW, PowerMaxW;
        public int? MaxCoreMHz, MaxMemMHz;
        public string Vbios;
        public int FanCount;
        internal int NvmlIndex = -1;
        internal string Luid;                // "0x00000000_0x0000D1A3" for GPU Engine counters

        public string VramText { get { return VramBytes == 0 ? "" : (VramBytes >= (1UL << 30) ? (VramBytes / (double)(1UL << 30)).ToString("0.#", CultureInfo.CurrentCulture) + " GB" : (VramBytes >> 20) + " MB"); } }
        public string PcieText
        {
            get
            {
                if (PcieGen == null && PcieWidth == null) return "";
                string s = (PcieGen != null ? "PCIe " + PcieGen + ".0" : "PCIe") + (PcieWidth != null ? " x" + PcieWidth : "");
                if (PcieMaxGen != null && PcieMaxWidth != null && (PcieMaxGen != PcieGen || PcieMaxWidth != PcieWidth))
                    s += " (max " + PcieMaxGen + ".0 x" + PcieMaxWidth + ")";
                return s;
            }
        }
    }

    /// <summary>Live readings of one adapter (index = GpuInfo.Index).</summary>
    public class GpuLive
    {
        public int Index;
        public string Name, Vendor;
        public bool Integrated;
        public double? TempC, LoadPct, ClockMHz, MemClockMHz, PowerW, FanPct, MemUsedMB, MemTotalMB;
        public int? FanRpm;
        public string Source;                // "NVML" | "ADL" | "Windows" | null
    }

    public class ThermalZone
    {
        public string Name;
        public double TempC;
        public string Source;                // "ACPI" | "Perf" | "APU"
    }

    public class FanInfo
    {
        public string Name;
        public string Source;                // "NVIDIA" | "AMD" | "ACPI" | "WMI"
        public int? Rpm;
        public double? Percent;
        public bool CanControl;
        public bool Manual;                  // set by us (SetGpuFanPercent), not yet reset
        public int GpuIndex = -1, FanIndex = -1;
        public string Status;                // device status / why control is unavailable
    }

    // ------------------------------------------------------------------------------------------------ native helpers
    static class HwNative
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] public static extern IntPtr GetProcAddress(IntPtr mod, string name);
        [DllImport("kernel32.dll")] public static extern bool FreeLibrary(IntPtr mod);

        public static IntPtr Load(params string[] paths)
        {
            foreach (var p in paths)
            {
                try
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    if (p.IndexOf('\\') >= 0 && !File.Exists(p)) continue;
                    IntPtr h = LoadLibraryW(p);
                    if (h != IntPtr.Zero) return h;
                }
                catch { }
            }
            return IntPtr.Zero;
        }

        public static T Fn<T>(IntPtr lib, string name) where T : class
        {
            if (lib == IntPtr.Zero) return null;
            try
            {
                IntPtr p = GetProcAddress(lib, name);
                return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
            }
            catch { return null; }
        }

        public static ulong? RegU64(object v)
        {
            try
            {
                if (v == null) return null;
                if (v is long) return (ulong)(long)v;
                if (v is int) return (uint)(int)v;
                var b = v as byte[];
                if (b != null && b.Length >= 8) return BitConverter.ToUInt64(b, 0);
                if (b != null && b.Length >= 4) return BitConverter.ToUInt32(b, 0);
            }
            catch { }
            return null;
        }
    }

    // ------------------------------------------------------------------------------------------------ NVIDIA NVML
    static class HwNvml
    {
        [StructLayout(LayoutKind.Sequential)] public struct Util { public uint gpu, memory; }
        [StructLayout(LayoutKind.Sequential)] public struct Mem { public ulong total, free, used; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct Pci
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string busIdLegacy;
            public uint domain, bus, device, pciDeviceId, pciSubSystemId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string busId;
        }
        [StructLayout(LayoutKind.Sequential)] public struct FanRpm { public uint version, fan, speed; }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int VoidFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int CountFn(out uint n);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int HandleFn(uint i, out IntPtr dev);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] public delegate int StrFn(StringBuilder sb, uint len);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] public delegate int DevStrFn(IntPtr dev, StringBuilder sb, uint len);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DevU(IntPtr dev, out uint v);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DevIU(IntPtr dev, int type, out uint v);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DevFanU(IntPtr dev, uint fan, out uint v);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DevFanSet(IntPtr dev, uint fan, uint v);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DevFan(IntPtr dev, uint fan);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DevUtil(IntPtr dev, out Util u);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DevMem(IntPtr dev, out Mem m);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DevPci(IntPtr dev, out Pci p);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DevFanRpm(IntPtr dev, ref FanRpm f);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DevMinMax(IntPtr dev, out uint min, out uint max);

        public const int NOT_FOUND = 13;     // NVML_ERROR_FUNCTION_NOT_FOUND (also used for a missing export)
        static readonly object sync = new object();
        static bool tried; static IntPtr lib; static int refs;
        public static VoidFn InitV2, ShutdownFn;
        public static CountFn GetCount;
        public static HandleFn GetHandle;
        public static StrFn SysDriver;
        public static DevStrFn GetName, GetVbios;
        public static DevU GetPower, GetPowerLimit, GetPowerDefault, GetNumFans, GetFanLegacy, PcieGen, PcieWidth, PcieMaxGen, PcieMaxWidth;
        public static DevIU GetTemp, GetClock, GetMaxClock;
        public static DevFanU GetFan;
        public static DevFanSet SetFan;
        public static DevFan SetFanDefault;
        public static DevUtil GetUtil;
        public static DevMem GetMem;
        public static DevPci GetPci;
        public static DevFanRpm GetFanRpm;
        public static DevMinMax GetFanMinMax, GetPowerConstraints;

        static void Load()
        {
            if (tried) return;
            tried = true;
            string pf = Environment.GetEnvironmentVariable("ProgramW6432");
            if (string.IsNullOrEmpty(pf)) pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            lib = HwNative.Load(Path.Combine(Environment.SystemDirectory, "nvml.dll"),
                                Path.Combine(pf ?? "", @"NVIDIA Corporation\NVSMI\nvml.dll"));
            if (lib == IntPtr.Zero) return;
            InitV2 = HwNative.Fn<VoidFn>(lib, "nvmlInit_v2");
            ShutdownFn = HwNative.Fn<VoidFn>(lib, "nvmlShutdown");
            GetCount = HwNative.Fn<CountFn>(lib, "nvmlDeviceGetCount_v2");
            GetHandle = HwNative.Fn<HandleFn>(lib, "nvmlDeviceGetHandleByIndex_v2");
            SysDriver = HwNative.Fn<StrFn>(lib, "nvmlSystemGetDriverVersion");
            GetName = HwNative.Fn<DevStrFn>(lib, "nvmlDeviceGetName");
            GetVbios = HwNative.Fn<DevStrFn>(lib, "nvmlDeviceGetVbiosVersion");
            GetPower = HwNative.Fn<DevU>(lib, "nvmlDeviceGetPowerUsage");
            GetPowerLimit = HwNative.Fn<DevU>(lib, "nvmlDeviceGetPowerManagementLimit");
            GetPowerDefault = HwNative.Fn<DevU>(lib, "nvmlDeviceGetPowerManagementDefaultLimit");
            GetPowerConstraints = HwNative.Fn<DevMinMax>(lib, "nvmlDeviceGetPowerManagementLimitConstraints");
            GetNumFans = HwNative.Fn<DevU>(lib, "nvmlDeviceGetNumFans");
            GetFanLegacy = HwNative.Fn<DevU>(lib, "nvmlDeviceGetFanSpeed");
            PcieGen = HwNative.Fn<DevU>(lib, "nvmlDeviceGetCurrPcieLinkGeneration");
            PcieWidth = HwNative.Fn<DevU>(lib, "nvmlDeviceGetCurrPcieLinkWidth");
            PcieMaxGen = HwNative.Fn<DevU>(lib, "nvmlDeviceGetMaxPcieLinkGeneration");
            PcieMaxWidth = HwNative.Fn<DevU>(lib, "nvmlDeviceGetMaxPcieLinkWidth");
            GetTemp = HwNative.Fn<DevIU>(lib, "nvmlDeviceGetTemperature");
            GetClock = HwNative.Fn<DevIU>(lib, "nvmlDeviceGetClockInfo");
            GetMaxClock = HwNative.Fn<DevIU>(lib, "nvmlDeviceGetMaxClockInfo");
            GetFan = HwNative.Fn<DevFanU>(lib, "nvmlDeviceGetFanSpeed_v2");
            SetFan = HwNative.Fn<DevFanSet>(lib, "nvmlDeviceSetFanSpeed_v2");
            SetFanDefault = HwNative.Fn<DevFan>(lib, "nvmlDeviceSetDefaultFanSpeed_v2");
            GetUtil = HwNative.Fn<DevUtil>(lib, "nvmlDeviceGetUtilizationRates");
            GetMem = HwNative.Fn<DevMem>(lib, "nvmlDeviceGetMemoryInfo");
            GetPci = HwNative.Fn<DevPci>(lib, "nvmlDeviceGetPciInfo_v3") ?? HwNative.Fn<DevPci>(lib, "nvmlDeviceGetPciInfo_v2");
            GetFanRpm = HwNative.Fn<DevFanRpm>(lib, "nvmlDeviceGetFanSpeedRPM");
            GetFanMinMax = HwNative.Fn<DevMinMax>(lib, "nvmlDeviceGetMinMaxFanSpeed");
        }

        /// <summary>Reference-counted nvmlInit (NVML itself counts init/shutdown pairs too).</summary>
        public static bool Init()
        {
            lock (sync)
            {
                try
                {
                    Load();
                    if (InitV2 == null || GetCount == null || GetHandle == null) return false;
                    if (InitV2() != 0) return false;
                    refs++;
                    return true;
                }
                catch { return false; }
            }
        }

        public static void Shutdown()
        {
            lock (sync)
            {
                try { if (refs > 0) { refs--; if (ShutdownFn != null) ShutdownFn(); } } catch { }
            }
        }

        public static object Sync { get { return sync; } }

        public static int Count()
        {
            uint n; try { return GetCount != null && GetCount(out n) == 0 ? (int)n : 0; } catch { return 0; }
        }
        public static IntPtr Handle(int i)
        {
            IntPtr d; try { return GetHandle != null && GetHandle((uint)i, out d) == 0 ? d : IntPtr.Zero; } catch { return IntPtr.Zero; }
        }
        public static uint? U(DevU f, IntPtr dev)
        {
            uint v; try { return f != null && dev != IntPtr.Zero && f(dev, out v) == 0 ? v : (uint?)null; } catch { return null; }
        }
        public static uint? U(DevIU f, IntPtr dev, int type)
        {
            uint v; try { return f != null && dev != IntPtr.Zero && f(dev, type, out v) == 0 ? v : (uint?)null; } catch { return null; }
        }
        public static string S(DevStrFn f, IntPtr dev)
        {
            try { var sb = new StringBuilder(128); return f != null && dev != IntPtr.Zero && f(dev, sb, 128) == 0 ? sb.ToString().Trim() : null; } catch { return null; }
        }
        public static string Driver()
        {
            try { var sb = new StringBuilder(96); return SysDriver != null && SysDriver(sb, 96) == 0 ? sb.ToString().Trim() : null; } catch { return null; }
        }
        public static int FanCount(IntPtr dev)
        {
            uint? n = U(GetNumFans, dev);
            if (n != null) return (int)Math.Min(n.Value, 16);
            return U(GetFanLegacy, dev) != null ? 1 : 0;   // old driver: single-fan API
        }
        public static uint? FanPct(IntPtr dev, int fan)
        {
            uint v;
            try
            {
                if (GetFan != null) return GetFan(dev, (uint)fan, out v) == 0 ? v : (uint?)null;
                return fan == 0 ? U(GetFanLegacy, dev) : null;
            }
            catch { return null; }
        }
        public static uint? FanRpmOf(IntPtr dev, int fan)
        {
            try
            {
                if (GetFanRpm == null) return null;
                var f = new FanRpm { fan = (uint)fan };
                f.version = (uint)(Marshal.SizeOf(typeof(FanRpm)) | (1 << 24));
                return GetFanRpm(dev, ref f) == 0 && f.speed > 0 ? f.speed : (uint?)null;
            }
            catch { return null; }
        }

        public static string Error(int code)
        {
            switch (code)
            {
                case 1: return L.T("Библиотека NVIDIA не инициализирована");
                case 2: return L.T("Неверный номер видеокарты или вентилятора");
                case 3: return L.T("Драйвер NVIDIA не поддерживает ручное управление этим вентилятором");
                case 4: return L.T("Драйвер NVIDIA отказал в доступе (нет прав)");
                case NOT_FOUND: return L.T("Эта версия драйвера NVIDIA не умеет управлять вентиляторами");
                case 15: return L.T("Видеокарта NVIDIA перестала отвечать");
                default: return L.F("Ошибка NVML {0}", code);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ AMD ADL2 (read-only)
    sealed class HwAdl : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr MallocCb(int size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CreateFn(MallocCb cb, int connectedOnly, out IntPtr ctx);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CtxFn(IntPtr ctx);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CtxOutFn(IntPtr ctx, out int n);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CtxBufFn(IntPtr ctx, IntPtr buf, int size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IdxOutFn(IntPtr ctx, int idx, out int v);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IdxBufFn(IntPtr ctx, int idx, IntPtr buf);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IdxTypeOutFn(IntPtr ctx, int idx, int type, out int v);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IdxTypeBufFn(IntPtr ctx, int idx, int type, IntPtr buf);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CapsFn(IntPtr ctx, int idx, out int supported, out int enabled, out int version);

        static readonly MallocCb malloc = AdlAlloc;           // kept alive for the process lifetime
        static IntPtr AdlAlloc(int size) { return Marshal.AllocHGlobal(Math.Max(size, 1)); }

        public class Adapter { public int Index, Bus, VendorId; public string Name, Pnp; public int OdVersion; }
        public class Reading { public double? TempC, LoadPct, ClockMHz, MemClockMHz, PowerW, FanPct; public int? FanRpm; }

        IntPtr lib, ctx;
        CtxFn destroy;
        IdxOutFn od6Temp;
        IdxBufFn odnPerf, odnFan, pmLog, od6Fan, od6Status;
        IdxTypeOutFn odnTemp;
        IdxTypeBufFn od5Temp, od5Fan;
        IdxBufFn od5Activity;
        public readonly List<Adapter> Adapters = new List<Adapter>();
        public bool Ok { get { return ctx != IntPtr.Zero; } }

        public HwAdl()
        {
            try
            {
                lib = HwNative.Load(IntPtr.Size == 8 ? "atiadlxx.dll" : "atiadlxy.dll", "atiadlxx.dll");
                if (lib == IntPtr.Zero) return;
                var create = HwNative.Fn<CreateFn>(lib, "ADL2_Main_Control_Create");
                destroy = HwNative.Fn<CtxFn>(lib, "ADL2_Main_Control_Destroy");
                var num = HwNative.Fn<CtxOutFn>(lib, "ADL2_Adapter_NumberOfAdapters_Get");
                var info = HwNative.Fn<CtxBufFn>(lib, "ADL2_Adapter_AdapterInfo_Get");
                var caps = HwNative.Fn<CapsFn>(lib, "ADL2_Overdrive_Caps");
                if (create == null || num == null || info == null) return;
                IntPtr c;
                if (create(malloc, 1, out c) != 0 || c == IntPtr.Zero) return;
                ctx = c;
                odnTemp = HwNative.Fn<IdxTypeOutFn>(lib, "ADL2_OverdriveN_Temperature_Get");
                odnPerf = HwNative.Fn<IdxBufFn>(lib, "ADL2_OverdriveN_PerformanceStatus_Get");
                odnFan = HwNative.Fn<IdxBufFn>(lib, "ADL2_OverdriveN_FanControl_Get");
                pmLog = HwNative.Fn<IdxBufFn>(lib, "ADL2_New_QueryPMLogData_Get");
                od6Temp = HwNative.Fn<IdxOutFn>(lib, "ADL2_Overdrive6_Temperature_Get");
                od6Fan = HwNative.Fn<IdxBufFn>(lib, "ADL2_Overdrive6_FanSpeed_Get");
                od6Status = HwNative.Fn<IdxBufFn>(lib, "ADL2_Overdrive6_CurrentStatus_Get");
                od5Temp = HwNative.Fn<IdxTypeBufFn>(lib, "ADL2_Overdrive5_Temperature_Get");
                od5Fan = HwNative.Fn<IdxTypeBufFn>(lib, "ADL2_Overdrive5_FanSpeed_Get");
                od5Activity = HwNative.Fn<IdxBufFn>(lib, "ADL2_Overdrive5_CurrentActivity_Get");

                int n;
                if (num(ctx, out n) != 0 || n <= 0 || n > 64) return;
                const int SZ = 1572;                          // sizeof(AdapterInfo) on Windows
                IntPtr buf = Marshal.AllocHGlobal(SZ * n);
                try
                {
                    for (int i = 0; i < SZ * n; i++) Marshal.WriteByte(buf, i, 0);
                    for (int i = 0; i < n; i++) Marshal.WriteInt32(buf, i * SZ, SZ);
                    if (info(ctx, buf, SZ * n) != 0) return;
                    var seen = new HashSet<int>();
                    for (int i = 0; i < n; i++)
                    {
                        IntPtr p = new IntPtr(buf.ToInt64() + (long)i * SZ);
                        int idx = Marshal.ReadInt32(p, 4), bus = Marshal.ReadInt32(p, 264), ven = Marshal.ReadInt32(p, 276);
                        if (ven != 1002 && ven != 0x1002 && ven != 0x1022) continue;   // ADL reports 1002 as a decimal number
                        if (!seen.Add(bus)) continue;             // ADL lists every display output; one entry per GPU
                        var a = new Adapter
                        {
                            Index = idx, Bus = bus, VendorId = 0x1002,
                            Name = (Marshal.PtrToStringAnsi(new IntPtr(p.ToInt64() + 280)) ?? "").Trim(),
                            Pnp = (Marshal.PtrToStringAnsi(new IntPtr(p.ToInt64() + 1312)) ?? "").Trim()
                        };
                        if (caps != null)
                        {
                            int s, e, v;
                            try { if (caps(ctx, idx, out s, out e, out v) == 0 && s != 0) a.OdVersion = v; } catch { }
                        }
                        Adapters.Add(a);
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { }
        }

        static IntPtr Buf(int ints)
        {
            IntPtr b = Marshal.AllocHGlobal(ints * 4);
            for (int i = 0; i < ints; i++) Marshal.WriteInt32(b, i * 4, 0);
            return b;
        }
        static int I(IntPtr b, int i) { return Marshal.ReadInt32(b, i * 4); }

        /// <summary>Temperature, load, clocks, power and fan for one adapter; every field may stay null.</summary>
        public Reading Read(Adapter a)
        {
            var r = new Reading();
            if (!Ok || a == null) return r;
            try
            {
                // Overdrive 8 (RX 5000+ / recent APUs): one call with the PM log
                if (pmLog != null && a.OdVersion >= 8)
                {
                    IntPtr b = Buf(1 + 512);
                    try
                    {
                        Marshal.WriteInt32(b, 0, 4 + 512 * 4);
                        if (pmLog(ctx, a.Index, b) == 0)
                        {
                            Func<int, double?> s = k => I(b, 1 + k * 2) != 0 ? I(b, 2 + k * 2) : (double?)null;
                            r.ClockMHz = Pos(s(1)); r.MemClockMHz = Pos(s(2)); r.TempC = Temp(s(8));
                            double? rpm = s(17); if (rpm != null && rpm > 0) r.FanRpm = (int)rpm.Value;
                            r.FanPct = Pct(s(18)); r.LoadPct = Pct(s(22)); r.PowerW = Pos(s(26));
                        }
                    }
                    finally { Marshal.FreeHGlobal(b); }
                }
                // Overdrive N (Polaris / Vega)
                if (r.TempC == null && odnTemp != null)
                {
                    int t; if (odnTemp(ctx, a.Index, 1, out t) == 0) r.TempC = Temp(t / 1000.0);
                }
                if (r.LoadPct == null && odnPerf != null && a.OdVersion == 7)
                {
                    IntPtr b = Buf(18);
                    try
                    {
                        if (odnPerf(ctx, a.Index, b) == 0)
                        {
                            r.ClockMHz = r.ClockMHz ?? Pos(I(b, 0) * 0.01); r.MemClockMHz = r.MemClockMHz ?? Pos(I(b, 1) * 0.01);
                            r.LoadPct = Pct(I(b, 6));
                        }
                    }
                    finally { Marshal.FreeHGlobal(b); }
                }
                if (r.FanRpm == null && r.FanPct == null && odnFan != null && a.OdVersion == 7)
                {
                    IntPtr b = Buf(8);
                    try
                    {
                        if (odnFan(ctx, a.Index, b) == 0)
                        {
                            int mode = I(b, 2), v = I(b, 3);
                            if (mode == 1 && v >= 0 && v <= 100) r.FanPct = v; else if (v > 0) r.FanRpm = v;
                        }
                    }
                    finally { Marshal.FreeHGlobal(b); }
                }
                // Overdrive 6
                if (r.TempC == null && od6Temp != null)
                {
                    int t; if (od6Temp(ctx, a.Index, out t) == 0) r.TempC = Temp(t / 1000.0);
                }
                if (r.FanRpm == null && r.FanPct == null && od6Fan != null && a.OdVersion == 6)
                {
                    IntPtr b = Buf(5);
                    try
                    {
                        if (od6Fan(ctx, a.Index, b) == 0)
                        {
                            int type = I(b, 0);
                            if ((type & 1) != 0) r.FanPct = Pct(I(b, 1));
                            if ((type & 2) != 0 && I(b, 2) > 0) r.FanRpm = I(b, 2);
                        }
                    }
                    finally { Marshal.FreeHGlobal(b); }
                }
                if (r.LoadPct == null && od6Status != null && a.OdVersion == 6)
                {
                    IntPtr b = Buf(9);
                    try
                    {
                        if (od6Status(ctx, a.Index, b) == 0)
                        {
                            r.ClockMHz = r.ClockMHz ?? Pos(I(b, 0) * 0.01); r.MemClockMHz = r.MemClockMHz ?? Pos(I(b, 1) * 0.01);
                            r.LoadPct = Pct(I(b, 2));
                        }
                    }
                    finally { Marshal.FreeHGlobal(b); }
                }
                // Overdrive 5 (old cards)
                if (a.OdVersion > 0 && a.OdVersion <= 5)
                {
                    if (r.TempC == null && od5Temp != null)
                    {
                        IntPtr b = Buf(2);
                        try { Marshal.WriteInt32(b, 0, 8); if (od5Temp(ctx, a.Index, 0, b) == 0) r.TempC = Temp(I(b, 1) / 1000.0); }
                        finally { Marshal.FreeHGlobal(b); }
                    }
                    if (od5Fan != null)
                    {
                        IntPtr b = Buf(4);
                        try
                        {
                            Marshal.WriteInt32(b, 0, 16); Marshal.WriteInt32(b, 4, 1);
                            if (od5Fan(ctx, a.Index, 0, b) == 0) r.FanPct = Pct(I(b, 2));
                            Marshal.WriteInt32(b, 0, 16); Marshal.WriteInt32(b, 4, 2);
                            if (od5Fan(ctx, a.Index, 0, b) == 0 && I(b, 2) > 0) r.FanRpm = I(b, 2);
                        }
                        finally { Marshal.FreeHGlobal(b); }
                    }
                    if (r.LoadPct == null && od5Activity != null)
                    {
                        IntPtr b = Buf(10);
                        try
                        {
                            Marshal.WriteInt32(b, 0, 40);
                            if (od5Activity(ctx, a.Index, b) == 0)
                            {
                                r.ClockMHz = r.ClockMHz ?? Pos(I(b, 1) * 0.01); r.MemClockMHz = r.MemClockMHz ?? Pos(I(b, 2) * 0.01);
                                r.LoadPct = Pct(I(b, 4));
                            }
                        }
                        finally { Marshal.FreeHGlobal(b); }
                    }
                }
            }
            catch { }
            return r;
        }

        static double? Temp(double? t) { return t != null && t > 0 && t < 150 ? t : null; }
        static double? Pct(double? v) { return v != null && v >= 0 && v <= 100 ? v : null; }
        static double? Pos(double? v) { return v != null && v > 0 && v < 100000 ? v : null; }

        public void Dispose()
        {
            try { if (ctx != IntPtr.Zero && destroy != null) destroy(ctx); } catch { }
            ctx = IntPtr.Zero;
        }
    }

    // ------------------------------------------------------------------------------------------------ D3DKMT (gdi32)
    static class HwKmt
    {
        [StructLayout(LayoutKind.Sequential)] struct AdapterInfo { public uint hAdapter; public uint LuidLow; public int LuidHigh; public uint NumOfSources; public int bPrecise; }
        [StructLayout(LayoutKind.Sequential)] struct Enum2 { public uint NumAdapters; public IntPtr pAdapters; }
        [StructLayout(LayoutKind.Sequential)] struct Query { public uint hAdapter; public int Type; public IntPtr pData; public uint DataSize; }
        [StructLayout(LayoutKind.Sequential)] struct Close { public uint hAdapter; }
        [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(ref Enum2 e);
        [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref Query q);
        [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref Close c);

        public class Entry { public string Luid, Name; public int Bus = -1; public ulong Dedicated; }

        static bool Q(uint h, int type, IntPtr buf, int size)
        {
            var q = new Query { hAdapter = h, Type = type, pData = buf, DataSize = (uint)size };
            return D3DKMTQueryAdapterInfo(ref q) == 0;
        }

        public static List<Entry> Adapters()
        {
            var list = new List<Entry>();
            try
            {
                var e = new Enum2();
                if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0 || e.NumAdapters > 64) return list;
                int sz = Marshal.SizeOf(typeof(AdapterInfo));
                e.pAdapters = Marshal.AllocHGlobal(sz * (int)e.NumAdapters);
                IntPtr tmp = Marshal.AllocHGlobal(4096);
                try
                {
                    if (D3DKMTEnumAdapters2(ref e) != 0) return list;
                    for (int i = 0; i < e.NumAdapters; i++)
                    {
                        var ai = (AdapterInfo)Marshal.PtrToStructure(new IntPtr(e.pAdapters.ToInt64() + (long)i * sz), typeof(AdapterInfo));
                        var en = new Entry { Luid = string.Format("0x{0:X8}_0x{1:X8}", ai.LuidHigh, ai.LuidLow) };
                        try
                        {
                            if (Q(ai.hAdapter, 8, tmp, 2080)) en.Name = (Marshal.PtrToStringUni(tmp) ?? "").Trim();        // ADAPTERREGISTRYINFO
                            if (Q(ai.hAdapter, 6, tmp, 12)) en.Bus = Marshal.ReadInt32(tmp, 0);                              // ADAPTERADDRESS
                            if (Q(ai.hAdapter, 3, tmp, 24)) en.Dedicated = (ulong)Marshal.ReadInt64(tmp, 0);                 // GETSEGMENTSIZE
                        }
                        catch { }
                        try { var c = new Close { hAdapter = ai.hAdapter }; D3DKMTCloseAdapter(ref c); } catch { }
                        list.Add(en);
                    }
                }
                finally { Marshal.FreeHGlobal(e.pAdapters); Marshal.FreeHGlobal(tmp); }
            }
            catch { }
            return list;
        }
    }

    // ------------------------------------------------------------------------------------------------ adapter detection
    public static class HwInfo
    {
        static List<GpuInfo> cache;
        static readonly object cacheLock = new object();
        static bool? elevated;

        public static bool IsElevated
        {
            get
            {
                if (elevated == null)
                {
                    try { elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
                    catch { elevated = false; }
                }
                return elevated.Value;
            }
        }

        /// <summary>All physical display adapters, discrete first. Slow on first call (WMI + NVML), then cached.</summary>
        public static List<GpuInfo> Gpus()
        {
            lock (cacheLock)
            {
                if (cache == null)
                {
                    try { cache = Demo.On ? HwDemo.Gpus() : Detect(); }
                    catch { cache = new List<GpuInfo>(); }
                }
                return new List<GpuInfo>(cache);
            }
        }

        /// <summary>Forget the cached detection (e.g. after a driver update).</summary>
        public static void ResetCache() { lock (cacheLock) cache = null; }

        public static string VendorName(int ven)
        {
            switch (ven)
            {
                case 0x10DE: return "NVIDIA";
                case 0x1002: case 0x1022: return "AMD";
                case 0x8086: return "Intel";
                default: return "Other";
            }
        }

        static int Hex(string s, string key)
        {
            var m = Regex.Match(s ?? "", key + "_([0-9A-Fa-f]{4})");
            return m.Success ? int.Parse(m.Groups[1].Value, NumberStyles.HexNumber) : 0;
        }

        static int? PosInt(object v)
        {
            try { if (v == null) return null; long x = Convert.ToInt64(v); return x > 0 && x < 1000000 ? (int)x : (int?)null; } catch { return null; }
        }

        static List<GpuInfo> Detect()
        {
            var list = new List<GpuInfo>();
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, DriverVersion, DriverDate, AdapterRAM, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate, CurrentBitsPerPixel FROM Win32_VideoController"))
                using (var res = s.Get())
                    foreach (ManagementObject mo in res)
                        using (mo)
                        {
                            try
                            {
                                string name = Convert.ToString(mo["Name"]) ?? "", pnp = Convert.ToString(mo["PNPDeviceID"]) ?? "";
                                if (!pnp.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) continue;       // remote / indirect / software
                                if (name.IndexOf("Microsoft Basic", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                                var g = new GpuInfo { Name = name.Trim(), PnpId = pnp, VendorId = Hex(pnp, "VEN"), DeviceId = Hex(pnp, "DEV") };
                                g.Vendor = VendorName(g.VendorId);
                                g.DriverVersion = Convert.ToString(mo["DriverVersion"]);
                                try { string dd = Convert.ToString(mo["DriverDate"]); if (!string.IsNullOrEmpty(dd)) g.DriverDate = ManagementDateTimeConverter.ToDateTime(dd).Date; } catch { }
                                g.Width = PosInt(mo["CurrentHorizontalResolution"]); g.Height = PosInt(mo["CurrentVerticalResolution"]);
                                g.RefreshHz = PosInt(mo["CurrentRefreshRate"]); g.BitsPerPixel = PosInt(mo["CurrentBitsPerPixel"]);
                                ReadRegistry(g);
                                if (g.VramBytes == 0)
                                {
                                    try { object r = mo["AdapterRAM"]; if (r != null) { g.VramBytes = Convert.ToUInt64(r); g.VramSource = "WMI"; } } catch { }
                                }
                                if (g.Vendor == "NVIDIA") g.VendorDriver = NvidiaVersion(g.DriverVersion);
                                list.Add(g);
                            }
                            catch { }
                        }
            }
            catch { }

            // D3DKMT: LUID for the GPU Engine counters, bus number, dedicated memory
            try
            {
                var kmt = HwHelpers.Where(HwKmt.Adapters(), k => !string.IsNullOrEmpty(k.Name));
                var used = new HashSet<HwKmt.Entry>();
                foreach (var g in list)
                {
                    HwKmt.Entry hit = null;
                    foreach (var k in kmt)
                        if (!used.Contains(k) && g.Bus >= 0 && k.Bus == g.Bus && string.Equals(k.Name, g.Name, StringComparison.OrdinalIgnoreCase)) { hit = k; break; }
                    if (hit == null)
                        foreach (var k in kmt)
                            if (!used.Contains(k) && string.Equals(k.Name, g.Name, StringComparison.OrdinalIgnoreCase)) { hit = k; break; }
                    if (hit == null) continue;
                    used.Add(hit);
                    g.Luid = hit.Luid;
                    if (g.Bus < 0 && hit.Bus >= 0) g.Bus = hit.Bus;
                    if ((g.VramBytes == 0 || g.VramSource == "WMI") && hit.Dedicated > 0) { g.VramBytes = hit.Dedicated; g.VramSource = "D3DKMT"; }
                }
            }
            catch { }

            DetectNvml(list);

            foreach (var g in list) g.Integrated = IsIntegrated(g);
            var sorted = list.Where(g => !g.Integrated).Concat(list.Where(g => g.Integrated)).ToList();
            for (int i = 0; i < sorted.Count; i++) sorted[i].Index = i;
            return sorted;
        }

        static void ReadRegistry(GpuInfo g)
        {
            try
            {
                using (var en = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + g.PnpId))
                {
                    if (en == null) return;
                    string loc = Convert.ToString(en.GetValue("LocationInformation"));
                    // "@System32\drivers\pci.sys,#65536;PCI bus %1, device %2, function %3;(6,0,0)" or "PCI bus 6, device 0, function 0"
                    var m = Regex.Match(loc ?? "", @";\((\d+),\d+,\d+\)\s*$");
                    if (!m.Success) m = Regex.Match(loc ?? "", @"^[^\d@]*PCI[^\d]*(\d+)", RegexOptions.IgnoreCase);
                    if (m.Success) g.Bus = int.Parse(m.Groups[1].Value);
                    string drv = Convert.ToString(en.GetValue("Driver"));             // {4d36e968-…}\0001
                    if (string.IsNullOrEmpty(drv)) return;
                    using (var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + drv))
                    {
                        if (cls == null) return;
                        ulong? q = HwNative.RegU64(cls.GetValue("HardwareInformation.qwMemorySize"));
                        if (q == null || q == 0) q = HwNative.RegU64(cls.GetValue("HardwareInformation.MemorySize"));
                        if (q != null && q > 0) { g.VramBytes = q.Value; g.VramSource = "registry"; }
                    }
                }
            }
            catch { }
        }

        /// <summary>Windows NVIDIA driver version 32.0.15.6636 → 566.36.</summary>
        static string NvidiaVersion(string win)
        {
            try
            {
                var p = (win ?? "").Split('.');
                if (p.Length < 4) return null;
                string d = p[2] + p[3].PadLeft(4, '0');
                if (d.Length < 5) return null;
                d = d.Substring(d.Length - 5);
                return int.Parse(d.Substring(0, 3)).ToString(CultureInfo.InvariantCulture) + "." + d.Substring(3);
            }
            catch { return null; }
        }

        static void DetectNvml(List<GpuInfo> list)
        {
            if (!list.Any(g => g.Vendor == "NVIDIA") && !File.Exists(Path.Combine(Environment.SystemDirectory, "nvml.dll"))) return;
            if (!HwNvml.Init()) return;
            try
            {
                lock (HwNvml.Sync)
                {
                    string drv = HwNvml.Driver();
                    int n = HwNvml.Count();
                    for (int i = 0; i < n; i++)
                    {
                        IntPtr d = HwNvml.Handle(i);
                        if (d == IntPtr.Zero) continue;
                        int bus = -1, dev = 0;
                        try
                        {
                            HwNvml.Pci pci;
                            if (HwNvml.GetPci != null && HwNvml.GetPci(d, out pci) == 0) { bus = (int)pci.bus; dev = (int)(pci.pciDeviceId >> 16); }
                        }
                        catch { }
                        string nm = HwNvml.S(HwNvml.GetName, d);
                        GpuInfo g = list.FirstOrDefault(x => x.Vendor == "NVIDIA" && x.NvmlIndex < 0 && bus >= 0 && x.Bus == bus)
                                 ?? list.FirstOrDefault(x => x.Vendor == "NVIDIA" && x.NvmlIndex < 0 && (dev == 0 || x.DeviceId == dev));
                        if (g == null)
                        {
                            g = new GpuInfo { Name = nm ?? "NVIDIA GPU", Vendor = "NVIDIA", VendorId = 0x10DE, DeviceId = dev, Bus = bus };
                            list.Add(g);
                        }
                        g.NvmlIndex = i;
                        if (!string.IsNullOrEmpty(drv)) g.VendorDriver = drv;
                        try
                        {
                            HwNvml.Mem mem;
                            if (HwNvml.GetMem != null && HwNvml.GetMem(d, out mem) == 0 && mem.total > 0 && (g.VramBytes == 0 || g.VramSource == "WMI"))
                            { g.VramBytes = mem.total; g.VramSource = "NVML"; }
                        }
                        catch { }
                        uint? v;
                        v = HwNvml.U(HwNvml.PcieGen, d); if (v != null) g.PcieGen = (int)v;
                        v = HwNvml.U(HwNvml.PcieWidth, d); if (v != null) g.PcieWidth = (int)v;
                        v = HwNvml.U(HwNvml.PcieMaxGen, d); if (v != null) g.PcieMaxGen = (int)v;
                        v = HwNvml.U(HwNvml.PcieMaxWidth, d); if (v != null) g.PcieMaxWidth = (int)v;
                        v = HwNvml.U(HwNvml.GetPowerLimit, d); if (v != null && v > 0) g.PowerLimitW = v.Value / 1000.0;
                        v = HwNvml.U(HwNvml.GetPowerDefault, d); if (v != null && v > 0) g.PowerDefaultW = v.Value / 1000.0;
                        try
                        {
                            uint lo, hi;
                            if (HwNvml.GetPowerConstraints != null && HwNvml.GetPowerConstraints(d, out lo, out hi) == 0 && hi > 0) g.PowerMaxW = hi / 1000.0;
                        }
                        catch { }
                        v = HwNvml.U(HwNvml.GetMaxClock, d, 0); if (v != null && v > 0) g.MaxCoreMHz = (int)v;
                        v = HwNvml.U(HwNvml.GetMaxClock, d, 2); if (v != null && v > 0) g.MaxMemMHz = (int)v;
                        g.Vbios = HwNvml.S(HwNvml.GetVbios, d);
                        g.FanCount = HwNvml.FanCount(d);
                    }
                }
            }
            catch { }
            finally { HwNvml.Shutdown(); }
        }

        static bool IsIntegrated(GpuInfo g)
        {
            string n = g.Name ?? "";
            const ulong GiB = 1UL << 30;
            if (g.Vendor == "NVIDIA") return false;
            if (g.Vendor == "Intel")
                return !Regex.IsMatch(n, @"Arc\(TM\)\s*[AB]\d|Arc\s+[AB]\d|Iris\(R\) Xe MAX|DG\d", RegexOptions.IgnoreCase);
            if (g.Vendor == "AMD")
            {
                if (Regex.IsMatch(n, @"Radeon\(TM\)\s*Graphics|Radeon Graphics|Radeon\(TM\) Vega|Radeon Vega \d+ Graphics|Radeon\(TM\)?\s*\d{3}M\b|Radeon \d{3}M\b|Radeon\(TM\) R\d Graphics", RegexOptions.IgnoreCase))
                    return true;
                if (Regex.IsMatch(n, @"\bRX\b|Radeon Pro|FirePro|Instinct", RegexOptions.IgnoreCase)) return false;
                return g.VramBytes > 0 && g.VramBytes <= 2 * GiB;
            }
            if (g.Vendor == "Other") return g.VramBytes == 0 || g.VramBytes <= 1 * GiB;
            return false;
        }
    }

    static class HwHelpers
    {
        public static List<T> Where<T>(IEnumerable<T> src, Func<T, bool> f) { return src == null ? new List<T>() : src.Where(f).ToList(); }
    }

    // ------------------------------------------------------------------------------------------------ live sensors
    /// <summary>
    /// Periodic readings. Call Poll() every 1–2 s from a background thread; the properties are swapped atomically at the end
    /// of each Poll (lists are never modified after publishing), so the UI thread can read them at any time.
    /// </summary>
    public class HwSensorsLive : IDisposable
    {
        readonly object pollLock = new object();
        readonly List<GpuInfo> adapters;
        IntPtr[] nvDev;                              // by GpuInfo.Index (Zero = not NVIDIA/NVML)
        bool nvOk;
        HwAdl adl;
        HwAdl.Adapter[] adlOf;
        PerformanceCounterCategory engineCat;
        Dictionary<string, CounterSample> enginePrev = new Dictionary<string, CounterSample>();
        bool zonesPerfOk = true, zonesAcpiOk = true, ramProbeChecked, ramProbeHasData;
        List<FanInfo> staticFans = new List<FanInfo>();
        DateTime staticFansAt = DateTime.MinValue;
        readonly Dictionary<long, int> manual = new Dictionary<long, int>();        // fans we changed → percent
        readonly Dictionary<long, string> blocked = new Dictionary<long, string>(); // fans whose control failed → reason
        bool disposed;
        // demo state
        int[] demoFan = { -1, -1 };
        readonly Random rnd = new Random(7);

        volatile List<ThermalZone> cpuZones = new List<ThermalZone>();
        volatile List<GpuLive> gpus = new List<GpuLive>();
        volatile List<FanInfo> fans = new List<FanInfo>();
        readonly object valLock = new object();
        double? cpuTemp, ramTemp;
        string cpuSource, cpuNote, ramNote;
        DateTime lastPoll;

        public HwSensorsLive()
        {
            adapters = HwInfo.Gpus();
            nvDev = new IntPtr[adapters.Count];
            adlOf = new HwAdl.Adapter[adapters.Count];
            if (Demo.On) return;
            try
            {
                if (adapters.Any(g => g.NvmlIndex >= 0) && HwNvml.Init())
                {
                    nvOk = true;
                    lock (HwNvml.Sync)
                        foreach (var g in adapters) if (g.NvmlIndex >= 0) nvDev[g.Index] = HwNvml.Handle(g.NvmlIndex);
                }
            }
            catch { }
            try
            {
                if (adapters.Any(g => g.Vendor == "AMD"))
                {
                    adl = new HwAdl();
                    if (adl.Ok)
                    {
                        var used = new HashSet<HwAdl.Adapter>();
                        foreach (var g in adapters.Where(x => x.Vendor == "AMD"))
                        {
                            string dev = g.DeviceId.ToString("X4");
                            var a = adl.Adapters.FirstOrDefault(x => !used.Contains(x) && g.Bus >= 0 && x.Bus == g.Bus)
                                 ?? adl.Adapters.FirstOrDefault(x => !used.Contains(x) && x.Pnp.IndexOf("DEV_" + dev, StringComparison.OrdinalIgnoreCase) >= 0)
                                 ?? adl.Adapters.FirstOrDefault(x => !used.Contains(x));
                            if (a != null) { used.Add(a); adlOf[g.Index] = a; }
                        }
                    }
                }
            }
            catch { }
        }

        // ---------------------------------------------------------------- published values
        public List<GpuInfo> Adapters { get { return adapters; } }
        /// <summary>Best CPU temperature available without a driver (max ACPI thermal zone, or the APU sensor), °C.</summary>
        public double? CpuTempC { get { lock (valLock) return cpuTemp; } }
        /// <summary>"ACPI" | "APU" | null.</summary>
        public string CpuTempSource { get { lock (valLock) return cpuSource; } }
        /// <summary>Explains what the CPU value is (or why it is missing).</summary>
        public string CpuTempNote { get { lock (valLock) return cpuNote; } }
        public List<ThermalZone> CpuZones { get { return cpuZones; } }
        public List<GpuLive> Gpus { get { return gpus; } }
        public List<FanInfo> Fans { get { return fans; } }
        public double? RamTempC { get { lock (valLock) return ramTemp; } }
        public string RamTempNote { get { lock (valLock) return ramNote; } }
        public DateTime LastPoll { get { lock (valLock) return lastPoll; } }

        // ---------------------------------------------------------------- polling
        public void Poll()
        {
            lock (pollLock)
            {
                if (disposed) return;
                if (Demo.On) { PollDemo(); return; }
                List<ThermalZone> zones = new List<ThermalZone>();
                var gl = new List<GpuLive>();
                var fl = new List<FanInfo>();
                double? rt = null; string rn = null;
                try { zones = ReadZones(); } catch { }
                try { ReadGpus(gl, fl); } catch { }
                try { ReadRam(out rt, out rn); } catch { }
                try { fl.AddRange(StaticFans()); } catch { }

                double? ct = null; string cs = null, cn;
                if (zones.Count > 0) { ct = zones.Max(z => z.TempC); cs = "ACPI"; cn = L.T("Температура ACPI-зоны: близка к процессорной, но это не датчик ядра"); }
                else
                {
                    var apu = gl.FirstOrDefault(g => g.Integrated && g.Vendor == "AMD" && g.TempC != null);
                    if (apu != null)
                    {
                        ct = apu.TempC; cs = "APU";
                        zones.Add(new ThermalZone { Name = "APU", TempC = apu.TempC.Value, Source = "APU" });
                        cn = L.T("Датчик гибридного процессора AMD (общий кристалл с графикой)");
                    }
                    else cn = HwInfo.IsElevated ? L.T("Windows не отдаёт температуру процессора без драйвера")
                                                : L.T("Температура процессора доступна только с правами администратора (и не на всех ПК)");
                }
                cpuZones = zones; gpus = gl; fans = fl;
                lock (valLock) { cpuTemp = ct; cpuSource = cs; cpuNote = cn; ramTemp = rt; ramNote = rn; lastPoll = DateTime.Now; }
            }
        }

        List<ThermalZone> ReadZones()
        {
            var byName = new Dictionary<string, ThermalZone>(StringComparer.OrdinalIgnoreCase);
            if (zonesPerfOk)
            {
                try
                {
                    using (var s = new ManagementObjectSearcher("root\\cimv2", "SELECT Name, HighPrecisionTemperature, Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation"))
                    using (var res = s.Get())
                        foreach (ManagementObject mo in res)
                            using (mo)
                            {
                                double k = 0;
                                try { k = Convert.ToDouble(mo["HighPrecisionTemperature"]) / 10.0; } catch { }
                                if (k <= 0) try { k = Convert.ToDouble(mo["Temperature"]); } catch { }
                                double c = Math.Round(k - 273.15, 1);
                                if (c <= 0 || c >= 130) continue;
                                string n = ZoneName(Convert.ToString(mo["Name"]));
                                byName[n] = new ThermalZone { Name = n, TempC = c, Source = "Perf" };
                            }
                }
                catch (ManagementException) { zonesPerfOk = false; }
                catch { }
            }
            if (zonesAcpiOk && HwInfo.IsElevated)
            {
                try
                {
                    using (var s = new ManagementObjectSearcher("root\\wmi", "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                    using (var res = s.Get())
                        foreach (ManagementObject mo in res)
                            using (mo)
                            {
                                double c;
                                try { c = Math.Round(Convert.ToDouble(mo["CurrentTemperature"]) / 10.0 - 273.15, 1); } catch { continue; }
                                if (c <= 0 || c >= 130) continue;
                                string n = ZoneName(Convert.ToString(mo["InstanceName"]));
                                if (!byName.ContainsKey(n)) byName[n] = new ThermalZone { Name = n, TempC = c, Source = "ACPI" };
                            }
                }
                catch (ManagementException) { zonesAcpiOk = false; }
                catch { }
            }
            return byName.Values.OrderBy(z => z.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>"\_TZ.THM0" / "ACPI\ThermalZone\THM0_0" → "THM0".</summary>
        static string ZoneName(string raw)
        {
            string s = raw ?? "";
            int i = Math.Max(s.LastIndexOf('\\'), s.LastIndexOf('.'));
            if (i >= 0) s = s.Substring(i + 1);
            var m = Regex.Match(s, @"^(.*?)_\d+$");
            if (m.Success && m.Groups[1].Value.Length > 0) s = m.Groups[1].Value;
            return s.Length == 0 ? "TZ" : s;
        }

        void ReadGpus(List<GpuLive> gl, List<FanInfo> fl)
        {
            Dictionary<string, double> engineLoad = null;
            foreach (var g in adapters)
            {
                var r = new GpuLive { Index = g.Index, Name = g.Name, Vendor = g.Vendor, Integrated = g.Integrated };
                IntPtr d = g.Index < nvDev.Length ? nvDev[g.Index] : IntPtr.Zero;
                if (nvOk && d != IntPtr.Zero)
                {
                    lock (HwNvml.Sync)
                    {
                        r.Source = "NVML";
                        uint? v;
                        v = HwNvml.U(HwNvml.GetTemp, d, 0); if (v != null) r.TempC = v;
                        v = HwNvml.U(HwNvml.GetClock, d, 0); if (v != null) r.ClockMHz = v;
                        v = HwNvml.U(HwNvml.GetClock, d, 2); if (v != null) r.MemClockMHz = v;
                        v = HwNvml.U(HwNvml.GetPower, d); if (v != null) r.PowerW = Math.Round(v.Value / 1000.0, 1);
                        try { HwNvml.Util u; if (HwNvml.GetUtil != null && HwNvml.GetUtil(d, out u) == 0) r.LoadPct = u.gpu; } catch { }
                        try
                        {
                            HwNvml.Mem m;
                            if (HwNvml.GetMem != null && HwNvml.GetMem(d, out m) == 0 && m.total > 0) { r.MemUsedMB = m.used >> 20; r.MemTotalMB = m.total >> 20; }
                        }
                        catch { }
                        int nf = HwNvml.FanCount(d);
                        for (int f = 0; f < nf; f++)
                        {
                            uint? pct = HwNvml.FanPct(d, f), rpm = HwNvml.FanRpmOf(d, f);
                            if (pct == null && rpm == null) continue;
                            long key = Key(g.Index, f);
                            string why;
                            blocked.TryGetValue(key, out why);
                            if (why == null && HwNvml.SetFan == null) why = HwNvml.Error(HwNvml.NOT_FOUND);
                            if (why == null && !HwInfo.IsElevated) why = L.T("Нужен запуск от имени администратора");
                            var fi = new FanInfo
                            {
                                Name = nf > 1 ? L.F("Вентилятор видеокарты {0}", f + 1) : L.T("Вентилятор видеокарты"),
                                Source = "NVIDIA", GpuIndex = g.Index, FanIndex = f,
                                Percent = pct, Rpm = rpm != null ? (int?)rpm.Value : null,
                                CanControl = why == null, Manual = manual.ContainsKey(key), Status = why
                            };
                            if (adapters.Count(x => x.Vendor == "NVIDIA") > 1) fi.Name += " · " + g.Name;
                            fl.Add(fi);
                            if (f == 0) { r.FanPct = pct; r.FanRpm = fi.Rpm; }
                        }
                    }
                }
                else if (adl != null && adlOf[g.Index] != null)
                {
                    HwAdl.Reading a;
                    lock (adl) a = adl.Read(adlOf[g.Index]);
                    if (a.TempC != null || a.LoadPct != null || a.FanPct != null || a.FanRpm != null) r.Source = "ADL";
                    r.TempC = a.TempC; r.LoadPct = a.LoadPct; r.ClockMHz = a.ClockMHz; r.MemClockMHz = a.MemClockMHz; r.PowerW = a.PowerW;
                    r.FanPct = a.FanPct; r.FanRpm = a.FanRpm;
                    if (a.FanPct != null || a.FanRpm != null)
                        fl.Add(new FanInfo
                        {
                            Name = L.T("Вентилятор видеокарты") + (adapters.Count(x => x.Vendor == "AMD") > 1 ? " · " + g.Name : ""),
                            Source = "AMD", GpuIndex = g.Index, FanIndex = 0, Percent = a.FanPct, Rpm = a.FanRpm,
                            CanControl = false, Status = L.T("Только чтение: управление вентиляторами AMD не поддерживается")
                        });
                }
                if (r.LoadPct == null && !string.IsNullOrEmpty(g.Luid))
                {
                    if (engineLoad == null) engineLoad = EngineLoad();
                    double l;
                    if (engineLoad.TryGetValue(g.Luid, out l)) { r.LoadPct = Math.Round(Math.Min(100, l), 1); if (r.Source == null) r.Source = "Windows"; }
                }
                gl.Add(r);
            }
        }

        /// <summary>Busiest engine type per adapter LUID from the "GPU Engine" counters (as Task Manager shows it).</summary>
        Dictionary<string, double> EngineLoad()
        {
            var res = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // One ReadCategory() snapshot of every instance (~20 ms) instead of hundreds of PerformanceCounter objects;
                // utilisation = difference against the previous snapshot.
                if (engineCat == null) engineCat = new PerformanceCounterCategory("GPU Engine");
                InstanceDataCollection data = engineCat.ReadCategory()["Utilization Percentage"];
                var now = new Dictionary<string, CounterSample>(StringComparer.OrdinalIgnoreCase);
                var sums = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);   // luid|engtype → sum over processes
                if (data != null)
                    foreach (InstanceData inst in data.Values)
                    {
                        string name = inst.InstanceName;
                        now[name] = inst.Sample;
                        CounterSample old;
                        if (!enginePrev.TryGetValue(name, out old)) continue;
                        var m = Regex.Match(name, @"luid_(0x[0-9A-Fa-f]{8}_0x[0-9A-Fa-f]{8}).*engtype_(\w+)", RegexOptions.IgnoreCase);
                        if (!m.Success) continue;
                        float v;
                        try { v = CounterSample.Calculate(old, inst.Sample); } catch { continue; }
                        if (float.IsNaN(v) || v < 0) continue;
                        string k = m.Groups[1].Value + "|" + m.Groups[2].Value;
                        double cur; sums.TryGetValue(k, out cur);
                        sums[k] = cur + v;
                    }
                enginePrev = now;
                foreach (var kv in sums)
                {
                    string luid = kv.Key.Substring(0, kv.Key.IndexOf('|'));
                    double cur; res.TryGetValue(luid, out cur);
                    res[luid] = Math.Max(cur, kv.Value);
                }
            }
            catch { }
            return res;
        }

        void ReadRam(out double? temp, out string note)
        {
            temp = null;
            note = L.T("Нет датчика: температуру модулей памяти можно прочитать только через шину SMBus, а для этого нужен драйвер");
            if (ramProbeChecked && !ramProbeHasData) return;
            ramProbeChecked = true;
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name, Description, CurrentReading FROM Win32_TemperatureProbe"))
                using (var res = s.Get())
                    foreach (ManagementObject mo in res)
                        using (mo)
                        {
                            string n = (Convert.ToString(mo["Name"]) + " " + Convert.ToString(mo["Description"])).ToLowerInvariant();
                            if (n.IndexOf("memory") < 0 && n.IndexOf("dimm") < 0 && n.IndexOf("ram") < 0) continue;
                            object cr = mo["CurrentReading"];
                            if (cr == null) continue;
                            double c = Convert.ToDouble(cr) / 10.0;        // CIM: tenths of a degree Celsius
                            if (c <= 0 || c >= 120) continue;
                            temp = temp == null ? c : Math.Max(temp.Value, c);
                        }
            }
            catch { }
            ramProbeHasData = temp != null;
            if (temp != null) note = L.T("Датчик SMBIOS (Win32_TemperatureProbe)");
        }

        List<FanInfo> StaticFans()
        {
            if ((DateTime.Now - staticFansAt).TotalSeconds < 30) return staticFans;
            staticFansAt = DateTime.Now;
            var list = new List<FanInfo>();
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, Status, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'ACPI%'"))
                using (var res = s.Get())
                    foreach (ManagementObject mo in res)
                        using (mo)
                        {
                            string id = (Convert.ToString(mo["PNPDeviceID"]) ?? "").ToUpperInvariant();
                            string name = Convert.ToString(mo["Name"]) ?? "";
                            bool fan = Regex.IsMatch(id, @"^ACPI\\(PNP0C0B|INT3404|INTC1044|INTC1048|INTC10A2|INTC1063|INTC10D6|INTC10FE|INTC1069)\b")
                                    || (id.StartsWith("ACPI\\") && Regex.IsMatch(name, @"\bfan\b", RegexOptions.IgnoreCase));
                            if (!fan) continue;
                            int err = 0;
                            try { err = Convert.ToInt32(mo["ConfigManagerErrorCode"]); } catch { }
                            list.Add(new FanInfo
                            {
                                Name = string.IsNullOrEmpty(name) ? L.T("Системный вентилятор") : name,
                                Source = "ACPI",
                                Status = err == 0 ? L.T("Есть, скорость не сообщает") : L.F("Ошибка устройства (код {0})", err)
                            });
                        }
            }
            catch { }
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name, DeviceID, DesiredSpeed, ActiveCooling, Status FROM Win32_Fan"))
                using (var res = s.Get())
                    foreach (ManagementObject mo in res)
                        using (mo)
                        {
                            int? rpm = null;
                            try { object ds = mo["DesiredSpeed"]; if (ds != null) { long x = Convert.ToInt64(ds); if (x > 0 && x < 30000) rpm = (int)x; } } catch { }
                            string st = Convert.ToString(mo["Status"]);
                            string name = Convert.ToString(mo["Name"]);
                            list.Add(new FanInfo
                            {
                                Name = string.IsNullOrEmpty(name) ? L.T("Вентилятор (WMI)") : name,
                                Source = "WMI", Rpm = rpm,
                                Status = string.IsNullOrEmpty(st) ? null : st
                            });
                        }
            }
            catch { }
            staticFans = list;
            return list;
        }

        static long Key(int gpu, int fan) { return ((long)gpu << 8) | (uint)fan; }

        // ---------------------------------------------------------------- fan control (NVIDIA only)
        /// <summary>Set a fixed fan speed (clamped to 30…100 %). False + reason when the driver refuses (common on GeForce/Windows).</summary>
        public bool SetGpuFanPercent(int gpuIndex, int fanIndex, int percent, out string reason)
        {
            reason = null;
            percent = Math.Max(30, Math.Min(100, percent));
            if (Demo.On)
            {
                if (fanIndex < 0 || fanIndex > 1) { reason = HwNvml.Error(2); return false; }
                demoFan[fanIndex] = percent; return true;
            }
            IntPtr d;
            if (!NvDevice(gpuIndex, out d, out reason)) return false;
            if (HwNvml.SetFan == null) { reason = HwNvml.Error(HwNvml.NOT_FOUND); return false; }
            lock (HwNvml.Sync)
            {
                try
                {
                    uint lo, hi;
                    if (HwNvml.GetFanMinMax != null && HwNvml.GetFanMinMax(d, out lo, out hi) == 0 && hi > lo)
                        percent = (int)Math.Max(lo, Math.Min(hi, (uint)percent));
                }
                catch { }
                int rc;
                try { rc = HwNvml.SetFan(d, (uint)fanIndex, (uint)percent); }
                catch (EntryPointNotFoundException) { rc = HwNvml.NOT_FOUND; }
                catch { rc = 999; }
                long key = Key(gpuIndex, fanIndex);
                if (rc == 0) { manual[key] = percent; blocked.Remove(key); return true; }
                reason = HwNvml.Error(rc);
                if (rc == 3 || rc == 4 || rc == HwNvml.NOT_FOUND) blocked[key] = reason;
                return false;
            }
        }

        public bool SetGpuFanPercent(int gpuIndex, int fanIndex, int percent)
        {
            string r; return SetGpuFanPercent(gpuIndex, fanIndex, percent, out r);
        }

        /// <summary>Give the fan back to the driver's automatic curve.</summary>
        public bool ResetGpuFan(int gpuIndex, int fanIndex, out string reason)
        {
            reason = null;
            if (Demo.On) { if (fanIndex >= 0 && fanIndex < 2) demoFan[fanIndex] = -1; return true; }
            IntPtr d;
            if (!NvDevice(gpuIndex, out d, out reason)) return false;
            if (HwNvml.SetFanDefault == null) { reason = HwNvml.Error(HwNvml.NOT_FOUND); return false; }
            lock (HwNvml.Sync)
            {
                int rc;
                try { rc = HwNvml.SetFanDefault(d, (uint)fanIndex); }
                catch (EntryPointNotFoundException) { rc = HwNvml.NOT_FOUND; }
                catch { rc = 999; }
                if (rc == 0) { manual.Remove(Key(gpuIndex, fanIndex)); return true; }
                reason = HwNvml.Error(rc);
                return false;
            }
        }

        public bool ResetGpuFan(int gpuIndex, int fanIndex)
        {
            string r; return ResetGpuFan(gpuIndex, fanIndex, out r);
        }

        /// <summary>Reset every fan changed through SetGpuFanPercent (call on exit).</summary>
        public void RestoreAll()
        {
            if (Demo.On) { demoFan[0] = demoFan[1] = -1; return; }
            List<long> keys;
            lock (HwNvml.Sync) keys = manual.Keys.ToList();
            foreach (var k in keys)
            {
                string r;
                try { ResetGpuFan((int)(k >> 8), (int)(k & 0xFF), out r); } catch { }
            }
        }

        bool NvDevice(int gpuIndex, out IntPtr d, out string reason)
        {
            d = IntPtr.Zero; reason = null;
            if (disposed) { reason = L.T("Датчики уже закрыты"); return false; }
            if (gpuIndex < 0 || gpuIndex >= adapters.Count) { reason = L.T("Видеокарта не найдена"); return false; }
            if (adapters[gpuIndex].Vendor != "NVIDIA") { reason = L.T("Управление вентиляторами доступно только для видеокарт NVIDIA"); return false; }
            if (!nvOk || nvDev[gpuIndex] == IntPtr.Zero) { reason = L.T("Библиотека NVIDIA (nvml.dll) недоступна"); return false; }
            d = nvDev[gpuIndex];
            return true;
        }

        // ---------------------------------------------------------------- tray
        /// <summary>Short lines for the tray menu: ("CPU", "54 °C"), ("GPU", "61 °C · fan 40 %"), ("RAM", "41 °C"). Only existing values.</summary>
        public List<KeyValuePair<string, string>> TrayLines()
        {
            var res = new List<KeyValuePair<string, string>>();
            var ci = CultureInfo.CurrentCulture;
            double? c = CpuTempC;
            if (c != null) res.Add(new KeyValuePair<string, string>("CPU", Math.Round(c.Value).ToString(ci) + " °C"));
            var gl = Gpus;
            var shown = gl.Where(g => g.TempC != null || g.FanPct != null || g.FanRpm != null).ToList();
            for (int i = 0; i < shown.Count; i++)
            {
                var g = shown[i];
                var parts = new List<string>();
                if (g.TempC != null) parts.Add(Math.Round(g.TempC.Value).ToString(ci) + " °C");
                if (g.FanPct != null) parts.Add(L.F("вент. {0} %", Math.Round(g.FanPct.Value)));
                else if (g.FanRpm != null) parts.Add(L.F("вент. {0} об/мин", g.FanRpm.Value));
                res.Add(new KeyValuePair<string, string>(shown.Count > 1 ? "GPU " + (i + 1) : "GPU", string.Join(" · ", parts)));
            }
            double? r = RamTempC;
            if (r != null) res.Add(new KeyValuePair<string, string>("RAM", Math.Round(r.Value).ToString(ci) + " °C"));
            return res;
        }

        // ---------------------------------------------------------------- demo
        void PollDemo()
        {
            Func<double, double, double> j = (v, a) => Math.Round(v + (rnd.NextDouble() * 2 - 1) * a, 1);
            var zones = new List<ThermalZone>
            {
                new ThermalZone { Name = "CPUZ", TempC = j(54, 1.5), Source = "ACPI" },
                new ThermalZone { Name = "TZ01", TempC = j(41, 0.5), Source = "ACPI" }
            };
            var g0 = adapters.Count > 0 ? adapters[0] : HwDemo.Gpus()[0];
            var fl = new List<FanInfo>();
            for (int f = 0; f < 2; f++)
            {
                int pct = demoFan[f] >= 0 ? demoFan[f] : 38;
                fl.Add(new FanInfo
                {
                    Name = L.F("Вентилятор видеокарты {0}", f + 1), Source = "NVIDIA", GpuIndex = 0, FanIndex = f,
                    Percent = pct, Rpm = (int)(pct * 38 + rnd.Next(-20, 20)), CanControl = true, Manual = demoFan[f] >= 0
                });
            }
            fl.Add(new FanInfo { Name = L.T("Системный вентилятор"), Source = "ACPI", Status = L.T("Есть, скорость не сообщает") });
            var gl = new List<GpuLive>
            {
                new GpuLive
                {
                    Index = 0, Name = g0.Name, Vendor = "NVIDIA", Source = "NVML", TempC = j(46, 1), LoadPct = j(12, 4),
                    ClockMHz = 1920, MemClockMHz = 10501, PowerW = j(38, 3), FanPct = fl[0].Percent, FanRpm = fl[0].Rpm,
                    MemUsedMB = 1840, MemTotalMB = 12282
                }
            };
            cpuZones = zones; gpus = gl; fans = fl;
            lock (valLock)
            {
                cpuTemp = zones.Max(z => z.TempC); cpuSource = "ACPI";
                cpuNote = L.T("Температура ACPI-зоны: близка к процессорной, но это не датчик ядра");
                ramTemp = j(41, 0.3); ramNote = L.T("Датчик SMBIOS (Win32_TemperatureProbe)"); lastPoll = DateTime.Now;
            }
        }

        public void Dispose()
        {
            lock (pollLock)
            {
                if (disposed) return;
                try { RestoreAll(); } catch { }
                disposed = true;
                if (adl != null) { lock (adl) adl.Dispose(); }
                if (nvOk) { nvOk = false; HwNvml.Shutdown(); }
            }
        }
    }

    static class HwDemo
    {
        public static List<GpuInfo> Gpus()
        {
            return new List<GpuInfo>
            {
                new GpuInfo
                {
                    Index = 0, Name = Demo.GpuName, Vendor = "NVIDIA", VendorId = 0x10DE, DeviceId = 0x2786, Bus = 1,
                    PnpId = "PCI\\VEN_10DE&DEV_2786", VramBytes = 12UL << 30, VramSource = "registry",
                    DriverVersion = "32.0.15.6636", VendorDriver = "566.36", DriverDate = new DateTime(2024, 12, 6),
                    Width = 2560, Height = 1440, RefreshHz = 165, BitsPerPixel = 32,
                    PcieGen = 4, PcieWidth = 16, PcieMaxGen = 4, PcieMaxWidth = 16,
                    PowerLimitW = 200, PowerDefaultW = 200, PowerMaxW = 220, MaxCoreMHz = 3105, MaxMemMHz = 10501,
                    Vbios = "95.04.31.00.7D", FanCount = 2, NvmlIndex = 0
                }
            };
        }
    }
}
