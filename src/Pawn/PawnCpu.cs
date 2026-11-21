// This file contains register maps and the AMD Tctl offset table studied from LibreHardwareMonitor
// (LibreHardwareMonitorLib/Hardware/Cpu/Amd17Cpu.cs, IntelCpu.cs), https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Copyright (C) LibreHardwareMonitor and Contributors. Partial Copyright (C) Michael Möller and Contributors.
// The code itself is an independent implementation for Foxide Helper.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace FoxideDisk
{
    public class CpuSensor
    {
        public string Name;
        public double Value;
        public string Kind;     // "temp" (°C) | "power" (W) | "clock" (MHz)
        public CpuSensor() { }
        public CpuSensor(string name, double value, string kind) { Name = name; Value = value; Kind = kind; }
        public override string ToString() { return Name + " = " + Value.ToString("0.0") + " " + Kind; }
    }

    /// <summary>
    /// CPU temperatures, package power and effective clocks through the PawnIO driver.
    /// Intel: module IntelMSR (IA32_THERM_STATUS per core, IA32_PACKAGE_THERM_STATUS, RAPL energy counters).
    /// AMD Zen (family 17h/19h/1Ah): module AMDFamily17 (SMN thermal registers Tctl / per-CCD, RAPL-style energy MSRs).
    /// Open() once, then Read() every 1–2 s from a worker thread. Nothing here writes to the hardware.
    /// </summary>
    public sealed class PawnCpu : IDisposable
    {
        public string Vendor;   // "Intel" | "AMD"
        public string Method;   // short description of the data source

        double? packageTemp, packagePower;
        public double? PackageTemp { get { return packageTemp; } }
        public double? PackagePower { get { return packagePower; } }

        PawnModule mod;
        readonly object sync = new object();
        bool intel;
        int family, model;
        string cpuName;

        // ---- cores (one logical CPU per physical core)
        sealed class Core
        {
            public string Name;
            public ushort Group;
            public ulong Mask;
            public double TjMax;
            public ulong LastA, LastM;
            public bool HaveAM;
            public uint LastE;
            public bool HaveE;
            public bool EnergyMoved;
        }
        readonly List<Core> cores = new List<Core>();

        // ---- energy counters (package & co.)
        sealed class Energy
        {
            public uint Msr;
            public string Name;
            public bool IsPackage;
            public uint Last;
            public bool Have, Moved;
            public bool Dead;
        }
        readonly List<Energy> energies = new List<Energy>();
        double energyUnit;          // joules per counter tick
        double coreEnergyUnit;      // AMD per-core energy
        readonly Stopwatch clock = Stopwatch.StartNew();
        double lastT = -1;

        double baseMhz;             // MPERF frequency
        double tjMaxPkg = 100;
        bool pkgThermOk;

        // ---- AMD
        double tctlOffset;          // subtracted (Tdie = Tctl - offset)
        uint ccdBase;
        int ccdCount;
        readonly List<int> ccdPresent = new List<int>();

        readonly HashSet<uint> denied = new HashSet<uint>();

        // MSRs the signed modules let us read (copied from IntelMSR.p / AMDFamily17.p is_allowed_msr_read, 0.2.x).
        // Anything outside this list is never sent; a runtime ACCESS_DENIED also disables the MSR for good.
        static readonly uint[] IntelAllowed = { 0x8B, 0x1B1, 0x198, 0x1A2, 0x19C, 0xE7, 0xE8, 0x601, 0x610, 0x611, 0x613, 0x614,
            0x618, 0x619, 0x61B, 0x61C, 0x620, 0x621, 0x638, 0x639, 0x63A, 0x63B, 0x640, 0x641, 0x642, 0x64D, 0x64F, 0x6B1,
            0x606, 0xCE, 0x2A, 0x1AD, 0x650, 0x150, 0x607, 0x608, 0x1A4 };
        static readonly uint[] AmdAllowed = { 0x8B, 0x179, 0x17A, 0x17B, 0xC00000E7, 0xC00000E8, 0xC0010000, 0xC0010004, 0xC0010015,
            0xC0010061, 0xC0010063, 0xC0010064, 0xC0010065, 0xC0010066, 0xC0010067, 0xC0010068, 0xC0010069, 0xC001006A, 0xC001006B,
            0xC0010071, 0xC0010292, 0xC0010293, 0xC0010296, 0xC0010299, 0xC001029A, 0xC001029B,
            0xC00102B0, 0xC00102B1, 0xC00102B2, 0xC00102B3, 0xC00102B4, 0xC0011020, 0xC0011021, 0xC0011022, 0xC001102B };

        // Intel
        const uint IA32_THERM_STATUS = 0x19C, IA32_PACKAGE_THERM_STATUS = 0x1B1, MSR_TEMPERATURE_TARGET = 0x1A2;
        const uint MSR_RAPL_POWER_UNIT = 0x606, MSR_PKG_ENERGY_STATUS = 0x611, MSR_PP0_ENERGY_STATUS = 0x639, MSR_PP1_ENERGY_STATUS = 0x641;
        const uint MSR_PLATFORM_INFO = 0xCE, IA32_MPERF = 0xE7, IA32_APERF = 0xE8;
        // AMD
        const uint MSR_PWR_UNIT = 0xC0010299, MSR_CORE_ENERGY_STAT = 0xC001029A, MSR_PKG_ENERGY_STAT = 0xC001029B;
        const uint MSR_PSTATE_0 = 0xC0010064, AMD_MPERF = 0xC00000E7, AMD_APERF = 0xC00000E8;
        const uint SMN_THM_TCON_CUR_TMP = 0x00059800;

        PawnCpu() { }

        // ================================================================ open
        /// <summary>Detect the CPU and load the matching module. Null with a reason when unsupported / no driver.</summary>
        public static PawnCpu Open(out string why)
        {
            why = null;
            PawnCpu c = null;
            try
            {
                string vendor, ident;
                double regMhz;
                var p = new PawnCpu();
                ReadCpuId(out vendor, out ident, out p.cpuName, out regMhz);
                ParseFamilyModel(ident, out p.family, out p.model);
                bool isIntel = vendor == "GenuineIntel", isAmd = vendor == "AuthenticAMD";
                if (!isIntel && !isAmd) { why = L.T("Датчики процессора через PawnIO есть только для процессоров Intel и AMD."); return null; }
                if (isAmd && (p.family < 0x17 || p.family > 0x1A))
                { why = L.T("Этот процессор AMD не поддерживается: нужен Ryzen / EPYC (Zen) или новее."); return null; }
                string prob = PawnIo.Problem();
                if (prob != null) { why = prob; return null; }

                string err;
                var m = PawnModule.Load(isIntel ? "IntelMSR" : "AMDFamily17", out err);
                if (m == null) { why = L.T("Не удалось загрузить модуль PawnIO для процессора:") + " " + err; return null; }
                c = p;
                c.mod = m;
                c.intel = isIntel;
                c.Vendor = isIntel ? "Intel" : "AMD";
                c.baseMhz = regMhz;
                c.EnumCores();
                bool ok = isIntel ? c.InitIntel() : c.InitAmd();
                if (!ok)
                {
                    c.Dispose();
                    why = L.T("Процессор не отдал ни одного датчика через PawnIO.");
                    return null;
                }
                c.Read();       // prime the energy / APERF counters so the first real Read() has values
                return c;
            }
            catch (Exception ex)
            {
                if (c != null) c.Dispose();
                why = ex.Message;
                return null;
            }
        }

        bool InitIntel()
        {
            // TjMax per core (hybrid parts may differ), package TjMax from the current CPU
            ulong v;
            if (ReadMsr(MSR_TEMPERATURE_TARGET, out v)) { double t = (v >> 16) & 0xFF; if (t >= 50 && t <= 150) tjMaxPkg = t; }
            bool anyCore = false;
            RunPinned(delegate (Core c)
            {
                c.TjMax = tjMaxPkg;
                ulong tv;
                if (ReadMsr(MSR_TEMPERATURE_TARGET, out tv)) { double t = (tv >> 16) & 0xFF; if (t >= 50 && t <= 150) c.TjMax = t; }
                ulong ts;
                if (ReadMsr(IA32_THERM_STATUS, out ts)) anyCore = true;
            });
            ulong ps;
            pkgThermOk = ReadMsr(IA32_PACKAGE_THERM_STATUS, out ps) && ((ps >> 16) & 0x7F) != 0;

            ulong pi;
            if (ReadMsr(MSR_PLATFORM_INFO, out pi)) { double r = (pi >> 8) & 0xFF; if (r > 0) baseMhz = r * 100; }

            ulong u;
            if (ReadMsr(MSR_RAPL_POWER_UNIT, out u))
            {
                energyUnit = Math.Pow(0.5, (u >> 8) & 0x1F);
                energies.Add(new Energy { Msr = MSR_PKG_ENERGY_STATUS, Name = L.T("Процессор (корпус)"), IsPackage = true });
                energies.Add(new Energy { Msr = MSR_PP0_ENERGY_STATUS, Name = L.T("Ядра процессора") });
                energies.Add(new Energy { Msr = MSR_PP1_ENERGY_STATUS, Name = L.T("Встроенная графика") });
            }
            Method = "IA32_THERM_STATUS, TjMax " + tjMaxPkg.ToString("0") + " °C" + (energyUnit > 0 ? ", RAPL" : "");
            return anyCore || pkgThermOk || energyUnit > 0;
        }

        bool InitAmd()
        {
            uint t;
            if (!ReadSmn(SMN_THM_TCON_CUR_TMP, out t)) return false;

            // Models whose Tctl is reported above the real die temperature (table as in k10temp / LHM)
            string n = cpuName ?? "";
            if (family == 0x17)
            {
                if (n.Contains("1600X") || n.Contains("1700X") || n.Contains("1800X")) tctlOffset = 20;
                else if (n.Contains("2700X")) tctlOffset = 10;
                else if (n.Contains("Threadripper 19") || n.Contains("Threadripper 29")) tctlOffset = 27;
            }

            // Per-CCD temperature registers (offset from 0x59800, number of slots)
            uint off = 0; int cnt = 0;
            if (family == 0x17)
            {
                if (model == 0x01 || model == 0x08 || model == 0x11 || model == 0x18) { off = 0x154; cnt = 4; }
                else if (model == 0x31 || model == 0x47 || model == 0x60 || model == 0x68 || model == 0x71) { off = 0x154; cnt = 8; }
                else if (model >= 0xA0 && model <= 0xAF) { off = 0x300; cnt = 8; }
            }
            else if (family == 0x19)
            {
                if (model <= 0x01 || model == 0x08 || model == 0x21 || (model >= 0x50 && model <= 0x5F)) { off = 0x154; cnt = 8; }
                else if (model >= 0x40 && model <= 0x4F) { off = 0x300; cnt = 8; }
                else if (model >= 0x60 && model <= 0x7F) { off = 0x308; cnt = 8; }
                else if ((model >= 0x10 && model <= 0x1F) || (model >= 0xA0 && model <= 0xAF)) { off = 0x300; cnt = 12; }
            }
            else if (family == 0x1A)
            {
                if (model <= 0x1F) { off = 0x1F0; cnt = 16; }
                else if (model >= 0x40 && model <= 0x4F) { off = 0x308; cnt = 8; }
            }
            if (cnt > 0)
            {
                ccdBase = SMN_THM_TCON_CUR_TMP + off;
                ccdCount = cnt;
                for (int i = 0; i < cnt; i++)
                {
                    uint r;
                    if (ReadSmn(ccdBase + (uint)(4 * i), out r) && (r & 0x800) != 0) ccdPresent.Add(i);
                }
            }

            // P0 frequency = MPERF rate
            ulong p0;
            if (ReadMsr(MSR_PSTATE_0, out p0) && (p0 >> 63) != 0)
            {
                double f = 0;
                if (family == 0x1A) f = (p0 & 0xFFF) * 5.0;
                else { double fid = p0 & 0xFF, dfs = (p0 >> 8) & 0x3F; if (dfs > 0) f = fid / dfs * 200.0; }
                if (f >= 400 && f <= 7000) baseMhz = f;
            }

            ulong u;
            if (ReadMsr(MSR_PWR_UNIT, out u))
            {
                energyUnit = Math.Pow(0.5, (u >> 8) & 0x1F);
                coreEnergyUnit = energyUnit;
                energies.Add(new Energy { Msr = MSR_PKG_ENERGY_STAT, Name = L.T("Процессор (корпус)"), IsPackage = true });
            }
            Method = "SMN Tctl" + (ccdPresent.Count > 0 ? "/Tccd" : "") + (tctlOffset > 0 ? " (Tctl − " + tctlOffset.ToString("0") + " °C)" : "")
                + (energyUnit > 0 ? ", RAPL" : "");
            return true;
        }

        // ================================================================ read
        /// <summary>Current values. Never throws; an empty list when nothing could be read.</summary>
        public List<CpuSensor> Read()
        {
            var list = new List<CpuSensor>();
            try
            {
                lock (sync)
                {
                    if (mod == null) return list;
                    double now = clock.Elapsed.TotalSeconds;
                    double dt = lastT < 0 ? 0 : now - lastT;
                    lastT = now;
                    if (intel) ReadIntel(list, dt); else ReadAmd(list, dt);
                }
            }
            catch { }
            return list;
        }

        void ReadIntel(List<CpuSensor> list, double dt)
        {
            double? pkg = null;
            if (pkgThermOk)
            {
                ulong v;
                if (ReadMsr(IA32_PACKAGE_THERM_STATUS, out v)) pkg = tjMaxPkg - ((v >> 16) & 0x7F);
            }
            var temps = new List<CpuSensor>();
            var clocks = new List<CpuSensor>();
            double maxCore = double.MinValue;
            RunPinned(delegate (Core c)
            {
                ulong ts;
                if (ReadMsr(IA32_THERM_STATUS, out ts) && ((ts >> 31) & 1) != 0)
                {
                    double t = c.TjMax - ((ts >> 16) & 0x7F);
                    temps.Add(new CpuSensor(c.Name, t, "temp"));
                    if (t > maxCore) maxCore = t;
                }
                ReadClock(c, IA32_APERF, IA32_MPERF, clocks);
            });
            if (pkg.HasValue) list.Add(new CpuSensor(L.T("Процессор (корпус)"), pkg.Value, "temp"));
            if (temps.Count > 0)
            {
                list.Add(new CpuSensor(L.T("Ядра (макс.)"), maxCore, "temp"));
                list.AddRange(temps);
            }
            packageTemp = pkg.HasValue ? pkg : (temps.Count > 0 ? maxCore : (double?)null);
            ReadEnergies(list, dt);
            list.AddRange(clocks);
        }

        void ReadAmd(List<CpuSensor> list, double dt)
        {
            uint t = 0;
            bool okT = false;
            var ccdRaw = new uint[ccdCount];
            var ccdOk = new bool[ccdCount];
            PawnIo.WithMutex(PawnIo.PciMutex, 200, delegate
            {
                okT = ReadSmnRaw(SMN_THM_TCON_CUR_TMP, out t);
                foreach (int i in ccdPresent) ccdOk[i] = ReadSmnRaw(ccdBase + (uint)(4 * i), out ccdRaw[i]);
            });
            if (okT)
            {
                double tctl = ((t >> 21) & 0x7FF) * 0.125;
                if ((t & 0x80000) != 0 || (t & 0x30000) == 0x30000) tctl -= 49;
                if (tctlOffset > 0)
                {
                    list.Add(new CpuSensor("Tctl", tctl, "temp"));
                    list.Add(new CpuSensor("Tdie", tctl - tctlOffset, "temp"));
                    packageTemp = tctl - tctlOffset;
                }
                else
                {
                    list.Add(new CpuSensor("Tctl/Tdie", tctl, "temp"));
                    packageTemp = tctl;
                }
            }
            else packageTemp = null;
            foreach (int i in ccdPresent)
            {
                if (!ccdOk[i] || (ccdRaw[i] & 0x800) == 0) continue;
                double v = (ccdRaw[i] & 0x7FF) * 0.125 - 49;
                if (v > -40 && v < 125) list.Add(new CpuSensor(L.F("CCD{0} (Tdie)", i + 1), v, "temp"));
            }
            ReadEnergies(list, dt);
            var clocks = new List<CpuSensor>();
            var powers = new List<CpuSensor>();
            RunPinned(delegate (Core c)
            {
                ReadClock(c, AMD_APERF, AMD_MPERF, clocks);
                if (coreEnergyUnit > 0 && !denied.Contains(MSR_CORE_ENERGY_STAT))
                {
                    ulong e;
                    if (ReadMsr(MSR_CORE_ENERGY_STAT, out e))
                    {
                        uint cur = (uint)e;
                        if (c.HaveE && dt > 0.05)
                        {
                            uint d = unchecked(cur - c.LastE);
                            if (d != 0) c.EnergyMoved = true;
                            double w = d * coreEnergyUnit / dt;
                            if (c.EnergyMoved && w < 1000) powers.Add(new CpuSensor(c.Name, w, "power"));
                        }
                        c.LastE = cur; c.HaveE = true;
                    }
                }
            });
            list.AddRange(powers);
            list.AddRange(clocks);
        }

        void ReadEnergies(List<CpuSensor> list, double dt)
        {
            if (energyUnit <= 0) { packagePower = null; return; }
            foreach (var e in energies)
            {
                if (e.Dead) continue;
                ulong v;
                if (!ReadMsr(e.Msr, out v)) { if (denied.Contains(e.Msr)) e.Dead = true; if (e.IsPackage) packagePower = null; continue; }
                uint cur = (uint)v;
                if (e.Have && dt > 0.05)
                {
                    uint d = unchecked(cur - e.Last);       // 32-bit counter, wraps
                    if (d != 0) e.Moved = true;
                    double w = d * energyUnit / dt;
                    if ((e.Moved || e.IsPackage) && w < 2000)
                    {
                        list.Add(new CpuSensor(e.Name, w, "power"));
                        if (e.IsPackage) packagePower = w;
                    }
                }
                e.Last = cur; e.Have = true;
            }
        }

        void ReadClock(Core c, uint aperf, uint mperf, List<CpuSensor> outList)
        {
            if (baseMhz <= 0) return;
            ulong a, m;
            if (!ReadMsr(aperf, out a) || !ReadMsr(mperf, out m)) return;
            if (c.HaveAM)
            {
                ulong da = unchecked(a - c.LastA), dm = unchecked(m - c.LastM);
                if (dm > 1000)
                {
                    double f = baseMhz * da / dm;
                    if (f > 50 && f < 9000) outList.Add(new CpuSensor(c.Name, f, "clock"));
                }
            }
            c.LastA = a; c.LastM = m; c.HaveAM = true;
        }

        // ================================================================ low level
        bool ReadMsr(uint msr, out ulong value)
        {
            value = 0;
            if (mod == null || denied.Contains(msr)) return false;
            if (Array.IndexOf(intel ? IntelAllowed : AmdAllowed, msr) < 0) { denied.Add(msr); return false; }
            int err;
            var r = mod.Execute("ioctl_read_msr", new ulong[] { msr }, 1, out err);
            if (r == null || r.Length < 1)
            {
                if (err == 5) denied.Add(msr);   // ERROR_ACCESS_DENIED: the module refuses this MSR
                return false;
            }
            value = r[0];
            return true;
        }

        bool ReadSmn(uint addr, out uint value)
        {
            uint v = 0; bool ok = false;
            PawnIo.WithMutex(PawnIo.PciMutex, 200, delegate { ok = ReadSmnRaw(addr, out v); });
            value = v;
            return ok;
        }

        bool ReadSmnRaw(uint addr, out uint value)
        {
            value = 0;
            if (mod == null) return false;
            var r = mod.Execute("ioctl_read_smn", new ulong[] { addr }, 1);
            if (r == null || r.Length < 1) return false;
            value = (uint)r[0];
            return true;
        }

        // ---------------------------------------------------------------- cores / affinity
        [StructLayout(LayoutKind.Sequential)]
        struct GROUP_AFFINITY { public UIntPtr Mask; public ushort Group; public ushort R0, R1, R2; }

        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetThreadGroupAffinity(IntPtr thread, ref GROUP_AFFINITY aff, out GROUP_AFFINITY prev);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetThreadGroupAffinity(IntPtr thread, out GROUP_AFFINITY aff);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetLogicalProcessorInformationEx(int relation, IntPtr buffer, ref int length);

        void EnumCores()
        {
            var found = new List<KeyValuePair<Core, int>>();   // core, efficiency class
            int len = 0;
            GetLogicalProcessorInformationEx(0 /*RelationProcessorCore*/, IntPtr.Zero, ref len);
            if (len > 0)
            {
                IntPtr buf = Marshal.AllocHGlobal(len);
                try
                {
                    if (GetLogicalProcessorInformationEx(0, buf, ref len))
                    {
                        int pos = 0;
                        while (pos + 8 <= len)
                        {
                            IntPtr p = new IntPtr(buf.ToInt64() + pos);
                            int rel = Marshal.ReadInt32(p, 0), size = Marshal.ReadInt32(p, 4);
                            if (size <= 0) break;
                            if (rel == 0)
                            {
                                int eff = Marshal.ReadByte(p, 9);
                                int groups = Marshal.ReadInt16(p, 30);
                                if (groups > 0)
                                {
                                    ulong mask = IntPtr.Size == 8 ? (ulong)Marshal.ReadInt64(p, 32) : (uint)Marshal.ReadInt32(p, 32);
                                    ushort grp = (ushort)Marshal.ReadInt16(p, 32 + IntPtr.Size);
                                    if (mask != 0) found.Add(new KeyValuePair<Core, int>(new Core { Group = grp, Mask = mask & (~mask + 1) }, eff));
                                }
                            }
                            pos += size;
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            if (found.Count == 0)
            {
                int n = Math.Min(Environment.ProcessorCount, 64);
                for (int i = 0; i < n; i++) found.Add(new KeyValuePair<Core, int>(new Core { Group = 0, Mask = 1UL << i }, 0));
            }
            int minEff = int.MaxValue, maxEff = int.MinValue;
            foreach (var kv in found) { minEff = Math.Min(minEff, kv.Value); maxEff = Math.Max(maxEff, kv.Value); }
            bool hybrid = intel && minEff != maxEff;
            int pN = 0, eN = 0, n2 = 0;
            foreach (var kv in found)
            {
                var c = kv.Key;
                if (hybrid)
                {
                    if (kv.Value == maxEff) c.Name = L.F("P-ядро #{0}", ++pN);
                    else c.Name = L.F("E-ядро #{0}", ++eN);
                }
                else c.Name = L.F("Ядро #{0}", ++n2);
                cores.Add(c);
            }
        }

        /// <summary>Run action on every physical core with the calling thread pinned to it; restores the affinity.</summary>
        void RunPinned(Action<Core> action)
        {
            Thread.BeginThreadAffinity();
            IntPtr th = GetCurrentThread();
            GROUP_AFFINITY orig;
            bool haveOrig = GetThreadGroupAffinity(th, out orig);
            try
            {
                foreach (var c in cores)
                {
                    var ga = new GROUP_AFFINITY { Mask = new UIntPtr(c.Mask), Group = c.Group };
                    GROUP_AFFINITY prev;
                    if (!SetThreadGroupAffinity(th, ref ga, out prev)) continue;
                    try { action(c); } catch { }
                }
            }
            finally
            {
                if (haveOrig) { GROUP_AFFINITY prev; SetThreadGroupAffinity(th, ref orig, out prev); }
                Thread.EndThreadAffinity();
            }
        }

        // ---------------------------------------------------------------- CPU identification (registry, no CPUID needed)
        static void ReadCpuId(out string vendor, out string ident, out string name, out double mhz)
        {
            vendor = ident = name = ""; mhz = 0;
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    if (k == null) return;
                    vendor = (k.GetValue("VendorIdentifier") as string ?? "").Trim();
                    ident = (k.GetValue("Identifier") as string ?? "").Trim();
                    name = (k.GetValue("ProcessorNameString") as string ?? "").Trim();
                    object o = k.GetValue("~MHz");
                    if (o is int) mhz = (int)o;
                }
            }
            catch { }
        }

        static void ParseFamilyModel(string ident, out int family, out int model)
        {
            family = model = 0;
            // "AMD64 Family 23 Model 24 Stepping 1" / "Intel64 Family 6 Model 141 Stepping 1"
            var parts = (ident ?? "").Split(' ');
            for (int i = 0; i + 1 < parts.Length; i++)
            {
                if (parts[i] == "Family") int.TryParse(parts[i + 1], out family);
                else if (parts[i] == "Model") int.TryParse(parts[i + 1], out model);
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (mod != null) { try { mod.Dispose(); } catch { } mod = null; }
            }
        }
    }
}
