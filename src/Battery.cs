using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace FoxideDisk
{
    public enum BatteryHealth { Unknown, Good, Fair, Poor }

    /// <summary>
    /// One battery. Capacities are in mWh (0 = unknown). When <see cref="Relative"/> is true the controller reports
    /// capacity in relative units instead of mWh (rare, old/UPS batteries) — the numbers are only good for percentages.
    /// </summary>
    public class BatteryInfo
    {
        public int Index;
        public string Source = "";            // "IOCTL" | "WMI" | "Win32_Battery" | "Demo"
        public string Name = "", Manufacturer = "", Serial = "", Chemistry = "";
        public long DesignMWh, FullMWh, RemainingMWh;
        public bool Relative;
        public double? Percent;
        public int? VoltageMV;
        public int? RateMW;                   // + charging, − discharging
        public bool PowerOnline, Charging, Discharging, Critical;
        public int? CycleCount;
        public double? TemperatureC;
        public string ManufactureDate = "";   // yyyy-MM-dd or ""
        public int? EstimatedMinutes;

        /// <summary>Full / Design × 100 (null when either is unknown).</summary>
        public double? HealthPercent
        {
            get
            {
                if (DesignMWh <= 0 || FullMWh <= 0) return null;
                return Math.Min(100.0, 100.0 * FullMWh / DesignMWh);
            }
        }

        public double? WearPercent
        {
            get { var h = HealthPercent; if (!h.HasValue) return null; return Math.Max(0, 100.0 - h.Value); }
        }

        public BatteryHealth Health
        {
            get
            {
                var h = HealthPercent;
                if (!h.HasValue) return BatteryHealth.Unknown;
                if (h.Value >= 80) return BatteryHealth.Good;
                if (h.Value >= 60) return BatteryHealth.Fair;
                return BatteryHealth.Poor;
            }
        }

        public string HealthText { get { return BatteryService.HealthText(Health); } }

        public double? RateW { get { if (!RateMW.HasValue) return null; return RateMW.Value / 1000.0; } }

        /// <summary>"Заряжается" / "Разряжается" / "Подключено, не заряжается" / "Полностью заряжен".</summary>
        public string StatusText
        {
            get
            {
                if (Charging) return L.T("Заряжается");
                if (Discharging || !PowerOnline) return L.T("Разряжается");
                if (Percent.HasValue && Percent.Value >= 99.5) return L.T("Полностью заряжен");
                return L.T("Подключено, не заряжается");
            }
        }
    }

    /// <summary>Whole-system power state (GetSystemPowerStatus — instant).</summary>
    public class SystemPower
    {
        public bool? AcOnline;          // null = unknown
        public bool BatterySaver;
        public bool NoBattery;          // the system reports no battery at all
        public int? Percent;
        public int? SecondsLeft;
    }

    /// <summary>
    /// Batteries without a driver: SetupAPI enumeration of the battery device interface + IOCTL_BATTERY_QUERY_* (as Microsoft's
    /// "Enumerating Battery Devices" sample); fallback WMI root\wmi Battery* classes and Win32_Battery. Nothing here throws.
    /// Demo mode (Demo.On) returns an invented laptop battery.
    /// </summary>
    public static class BatteryService
    {
        // ---------------------------------------------------------------- native
        static readonly Guid GUID_DEVCLASS_BATTERY = new Guid("72631e54-78a4-11d0-bcf7-00aa00b7b35d");
        const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
        const uint IOCTL_BATTERY_QUERY_TAG = 0x294040, IOCTL_BATTERY_QUERY_INFORMATION = 0x294044, IOCTL_BATTERY_QUERY_STATUS = 0x29404C;
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_ATTRIBUTE_NORMAL = 0x80;
        const uint BATTERY_POWER_ON_LINE = 1, BATTERY_DISCHARGING = 2, BATTERY_CHARGING = 4, BATTERY_CRITICAL = 8;
        const uint BATTERY_UNKNOWN = 0xFFFFFFFF;          // capacity / voltage / time
        const int BATTERY_UNKNOWN_RATE = unchecked((int)0x80000000);
        const uint BATTERY_CAPACITY_RELATIVE = 0x40000000;

        // BATTERY_QUERY_INFORMATION_LEVEL
        const int BatteryInformation = 0, BatteryTemperature = 2, BatteryEstimatedTime = 3, BatteryDeviceName = 4,
                  BatteryManufactureDate = 5, BatteryManufactureName = 6, BatterySerialNumber = 8;

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        struct BATTERY_QUERY_INFORMATION { public uint BatteryTag; public int InformationLevel; public int AtRate; }

        [StructLayout(LayoutKind.Sequential)]
        struct BATTERY_INFORMATION
        {
            public uint Capabilities;
            public byte Technology;
            public byte Reserved0, Reserved1, Reserved2;
            public byte Chem0, Chem1, Chem2, Chem3;
            public uint DesignedCapacity, FullChargedCapacity, DefaultAlert1, DefaultAlert2, CriticalBias, CycleCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BATTERY_WAIT_STATUS { public uint BatteryTag; public uint Timeout; public uint PowerState; public uint LowCapacity; public uint HighCapacity; }

        [StructLayout(LayoutKind.Sequential)]
        struct BATTERY_STATUS { public uint PowerState; public uint Capacity; public uint Voltage; public int Rate; }

        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_POWER_STATUS { public byte ACLineStatus; public byte BatteryFlag; public byte BatteryLifePercent; public byte SystemStatusFlag; public int BatteryLifeTime; public int BatteryFullLifeTime; }

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr devs, IntPtr devInfo, ref Guid guid, int index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devs, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int detailSize, out int required, IntPtr devInfo);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr devs);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref uint inBuf, int inSize, out uint outBuf, int outSize, out int returned, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref BATTERY_QUERY_INFORMATION inBuf, int inSize, out BATTERY_INFORMATION outBuf, int outSize, out int returned, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref BATTERY_QUERY_INFORMATION inBuf, int inSize, [Out] byte[] outBuf, int outSize, out int returned, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref BATTERY_WAIT_STATUS inBuf, int inSize, out BATTERY_STATUS outBuf, int outSize, out int returned, IntPtr ov);
        [DllImport("kernel32.dll")]
        static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

        // ---------------------------------------------------------------- public API
        /// <summary>Fast check (GetSystemPowerStatus, no enumeration unless the flag is "unknown"). Desktop PCs → false.</summary>
        public static bool HasBattery
        {
            get
            {
                if (Demo.On) return true;
                try
                {
                    SYSTEM_POWER_STATUS s;
                    if (GetSystemPowerStatus(out s))
                    {
                        if ((s.BatteryFlag & 128) != 0 && s.BatteryFlag != 255) return false;   // 128 = no system battery
                        if (s.BatteryFlag != 255) return true;
                    }
                    return DevicePaths().Count > 0;
                }
                catch { return false; }
            }
        }

        /// <summary>AC line, battery saver and the system-wide percent. Never throws.</summary>
        public static SystemPower Power()
        {
            var p = new SystemPower();
            if (Demo.On) { p.AcOnline = false; p.Percent = 76; p.SecondsLeft = 251 * 60; return p; }
            try
            {
                SYSTEM_POWER_STATUS s;
                if (!GetSystemPowerStatus(out s)) return p;
                if (s.ACLineStatus == 0) p.AcOnline = false; else if (s.ACLineStatus == 1) p.AcOnline = true;
                p.BatterySaver = (s.SystemStatusFlag & 1) != 0;
                p.NoBattery = s.BatteryFlag != 255 && (s.BatteryFlag & 128) != 0;
                if (s.BatteryLifePercent <= 100) p.Percent = s.BatteryLifePercent;
                if (s.BatteryLifeTime >= 0) p.SecondsLeft = s.BatteryLifeTime;
            }
            catch { }
            return p;
        }

        /// <summary>Every battery in the system (empty list on desktops). Never throws.</summary>
        public static List<BatteryInfo> Query()
        {
            if (Demo.On) return new List<BatteryInfo> { DemoBattery() };
            var list = new List<BatteryInfo>();
            try { list = QueryIoctl(); } catch { list = new List<BatteryInfo>(); }
            if (list.Count == 0)
            {
                try { list = QueryWmi(); } catch { list = new List<BatteryInfo>(); }
            }
            if (list.Count == 0)
            {
                try { list = QueryWin32(); } catch { list = new List<BatteryInfo>(); }
            }
            else
            {
                try { FillMissingFromWmi(list); } catch { }
            }
            try
            {
                if (list.Count == 1 && !list[0].Percent.HasValue)
                {
                    var p = Power();
                    if (p.Percent.HasValue) list[0].Percent = p.Percent.Value;
                }
            }
            catch { }
            for (int i = 0; i < list.Count; i++) list[i].Index = i;
            return list;
        }

        /// <summary>All batteries summed (capacities, rate), percent from the sums. Null for an empty list.</summary>
        public static BatteryInfo Combined(List<BatteryInfo> list)
        {
            if (list == null || list.Count == 0) return null;
            if (list.Count == 1) return list[0];
            var c = new BatteryInfo { Name = L.T("Все аккумуляторы"), Source = list[0].Source };
            int rate = 0; bool haveRate = false; double pctSum = 0; int pctN = 0;
            foreach (var b in list)
            {
                c.DesignMWh += b.DesignMWh; c.FullMWh += b.FullMWh; c.RemainingMWh += b.RemainingMWh;
                if (b.RateMW.HasValue) { rate += b.RateMW.Value; haveRate = true; }
                if (b.Percent.HasValue) { pctSum += b.Percent.Value; pctN++; }
                c.PowerOnline |= b.PowerOnline; c.Charging |= b.Charging; c.Discharging |= b.Discharging; c.Critical |= b.Critical;
                if (!c.VoltageMV.HasValue && b.VoltageMV.HasValue) c.VoltageMV = b.VoltageMV;
                if (b.TemperatureC.HasValue && (!c.TemperatureC.HasValue || b.TemperatureC.Value > c.TemperatureC.Value)) c.TemperatureC = b.TemperatureC;
                c.Relative |= b.Relative;
            }
            if (haveRate) c.RateMW = rate;
            if (c.FullMWh > 0) c.Percent = Clamp(100.0 * c.RemainingMWh / c.FullMWh);
            else if (pctN > 0) c.Percent = pctSum / pctN;
            if (c.RateMW.HasValue && c.RateMW.Value < 0 && c.RemainingMWh > 0)
                c.EstimatedMinutes = (int)(c.RemainingMWh * 60L / -c.RateMW.Value);
            return c;
        }

        public static string HealthText(BatteryHealth h)
        {
            switch (h)
            {
                case BatteryHealth.Good: return L.T("Хорошее");
                case BatteryHealth.Fair: return L.T("Среднее");
                case BatteryHealth.Poor: return L.T("Плохое");
                default: return L.T("Неизвестно");
            }
        }

        /// <summary>"Аккумулятор 76 % · разряжается 12.4 Вт · здоровье 91 %" or null when there is no battery.</summary>
        public static string TrayLine()
        {
            try
            {
                if (!HasBattery) return null;
                var b = Combined(Query());
                if (b == null) return null;
                var parts = new List<string>();
                parts.Add(b.Percent.HasValue ? L.F("Аккумулятор {0} %", Math.Round(b.Percent.Value)) : L.T("Аккумулятор"));
                double w = b.RateMW.HasValue ? Math.Abs(b.RateMW.Value) / 1000.0 : 0;
                string ws = w.ToString("0.0", CultureInfo.CurrentCulture);
                if (b.Charging) parts.Add(w > 0.05 ? L.F("заряжается {0} Вт", ws) : L.T("заряжается"));
                else if (b.Discharging || !b.PowerOnline) parts.Add(w > 0.05 ? L.F("разряжается {0} Вт", ws) : L.T("разряжается"));
                else parts.Add(L.T("от сети"));
                var h = b.HealthPercent;
                if (h.HasValue) parts.Add(L.F("здоровье {0} %", Math.Round(h.Value)));
                return string.Join(" · ", parts.ToArray());
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- IOCTL
        static List<string> DevicePaths()
        {
            var paths = new List<string>();
            Guid g = GUID_DEVCLASS_BATTERY;
            IntPtr devs = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (devs == IntPtr.Zero || devs == new IntPtr(-1)) return paths;
            try
            {
                for (int i = 0; i < 16; i++)
                {
                    var did = new SP_DEVICE_INTERFACE_DATA();
                    did.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(devs, IntPtr.Zero, ref g, i, ref did)) break;
                    int need;
                    SetupDiGetDeviceInterfaceDetail(devs, ref did, IntPtr.Zero, 0, out need, IntPtr.Zero);
                    if (need <= 0 || need > 4096) continue;
                    IntPtr buf = Marshal.AllocHGlobal(need);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);   // sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W)
                        int dummy;
                        if (SetupDiGetDeviceInterfaceDetail(devs, ref did, buf, need, out dummy, IntPtr.Zero))
                        {
                            string p = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                            if (!string.IsNullOrEmpty(p)) paths.Add(p);
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(devs); }
            return paths;
        }

        static List<BatteryInfo> QueryIoctl()
        {
            var list = new List<BatteryInfo>();
            foreach (var path in DevicePaths())
            {
                try
                {
                    var b = QueryOne(path);
                    if (b != null) list.Add(b);
                }
                catch { }
            }
            return list;
        }

        static BatteryInfo QueryOne(string path)
        {
            using (var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero))
            {
                if (h.IsInvalid) return null;
                uint wait = 0, tag; int ret;
                if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_TAG, ref wait, 4, out tag, 4, out ret, IntPtr.Zero) || tag == 0) return null;

                var b = new BatteryInfo { Source = "IOCTL" };
                var q = new BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = BatteryInformation };
                BATTERY_INFORMATION bi;
                if (DeviceIoControl(h, IOCTL_BATTERY_QUERY_INFORMATION, ref q, Marshal.SizeOf(q), out bi, Marshal.SizeOf(typeof(BATTERY_INFORMATION)), out ret, IntPtr.Zero))
                {
                    if ((bi.Capabilities & 0x80000000) == 0) return null;   // BATTERY_SYSTEM_BATTERY not set: UPS etc.
                    b.Relative = (bi.Capabilities & BATTERY_CAPACITY_RELATIVE) != 0;
                    if (bi.DesignedCapacity != BATTERY_UNKNOWN) b.DesignMWh = bi.DesignedCapacity;
                    if (bi.FullChargedCapacity != BATTERY_UNKNOWN) b.FullMWh = bi.FullChargedCapacity;
                    if (bi.CycleCount > 0 && bi.CycleCount < 100000) b.CycleCount = (int)bi.CycleCount;
                    b.Chemistry = ChemistryName(Encoding.ASCII.GetString(new[] { bi.Chem0, bi.Chem1, bi.Chem2, bi.Chem3 }));
                }

                b.Name = QueryString(h, tag, BatteryDeviceName);
                b.Manufacturer = QueryString(h, tag, BatteryManufactureName);
                b.Serial = QueryString(h, tag, BatterySerialNumber);

                var buf = new byte[8];
                q.InformationLevel = BatteryManufactureDate;
                if (DeviceIoControl(h, IOCTL_BATTERY_QUERY_INFORMATION, ref q, Marshal.SizeOf(q), buf, 4, out ret, IntPtr.Zero) && ret >= 4)
                {
                    int day = buf[0], month = buf[1], year = BitConverter.ToUInt16(buf, 2);
                    if (year > 1990 && year < 2100 && month >= 1 && month <= 12 && day >= 1 && day <= 31)
                        b.ManufactureDate = year.ToString("0000") + "-" + month.ToString("00") + "-" + day.ToString("00");
                }
                q.InformationLevel = BatteryTemperature;
                if (DeviceIoControl(h, IOCTL_BATTERY_QUERY_INFORMATION, ref q, Marshal.SizeOf(q), buf, 4, out ret, IntPtr.Zero) && ret >= 4)
                {
                    double c = BitConverter.ToUInt32(buf, 0) / 10.0 - 273.15;
                    if (c > -20 && c < 100) b.TemperatureC = Math.Round(c, 1);
                }
                q.InformationLevel = BatteryEstimatedTime; q.AtRate = 0;
                if (DeviceIoControl(h, IOCTL_BATTERY_QUERY_INFORMATION, ref q, Marshal.SizeOf(q), buf, 4, out ret, IntPtr.Zero) && ret >= 4)
                {
                    uint sec = BitConverter.ToUInt32(buf, 0);
                    if (sec != BATTERY_UNKNOWN && sec < 100 * 3600) b.EstimatedMinutes = (int)(sec / 60);
                }

                var ws = new BATTERY_WAIT_STATUS { BatteryTag = tag };
                BATTERY_STATUS st;
                if (DeviceIoControl(h, IOCTL_BATTERY_QUERY_STATUS, ref ws, Marshal.SizeOf(ws), out st, Marshal.SizeOf(typeof(BATTERY_STATUS)), out ret, IntPtr.Zero))
                {
                    b.PowerOnline = (st.PowerState & BATTERY_POWER_ON_LINE) != 0;
                    b.Discharging = (st.PowerState & BATTERY_DISCHARGING) != 0;
                    b.Charging = (st.PowerState & BATTERY_CHARGING) != 0;
                    b.Critical = (st.PowerState & BATTERY_CRITICAL) != 0;
                    if (st.Capacity != BATTERY_UNKNOWN) b.RemainingMWh = st.Capacity;
                    if (st.Voltage != BATTERY_UNKNOWN && st.Voltage > 0) b.VoltageMV = (int)st.Voltage;
                    if (st.Rate != BATTERY_UNKNOWN_RATE) b.RateMW = st.Rate;
                }
                if (b.FullMWh > 0) b.Percent = Clamp(100.0 * b.RemainingMWh / b.FullMWh);
                if (!b.EstimatedMinutes.HasValue && b.RateMW.HasValue && b.RateMW.Value < 0 && b.RemainingMWh > 0 && !b.Relative)
                    b.EstimatedMinutes = (int)(b.RemainingMWh * 60L / -b.RateMW.Value);
                if (b.Name.Length == 0) b.Name = L.T("Аккумулятор");
                return b;
            }
        }

        static string QueryString(SafeFileHandle h, uint tag, int level)
        {
            try
            {
                var q = new BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = level };
                var buf = new byte[512]; int ret;
                if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_INFORMATION, ref q, Marshal.SizeOf(q), buf, buf.Length, out ret, IntPtr.Zero) || ret <= 0) return "";
                return Encoding.Unicode.GetString(buf, 0, Math.Min(ret, buf.Length) & ~1).TrimEnd('\0').Trim();
            }
            catch { return ""; }
        }

        static string ChemistryName(string code)
        {
            string c = (code ?? "").TrimEnd('\0').Trim();
            string u = c.ToUpperInvariant();
            if (u.Length == 0) return "";
            if (u == "LION" || u == "LI-I" || u == "LI I") return "Li-ion";
            if (u.StartsWith("LIP") || u == "LIPO") return "LiPo";
            if (u == "PBAC") return L.T("Свинцово-кислотный");
            if (u == "NICD") return "NiCd";
            if (u == "NIMH") return "NiMH";
            if (u == "NIZN") return "NiZn";
            if (u == "RAM") return "RAM";
            return c;
        }

        static double Clamp(double v) { return Math.Max(0, Math.Min(100, v)); }

        // ---------------------------------------------------------------- WMI fallback
        class WmiRow { public string Instance; public Dictionary<string, object> V = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase); }

        static List<WmiRow> Wmi(string ns, string query)
        {
            var rows = new List<WmiRow>();
            try
            {
                using (var s = new ManagementObjectSearcher(ns, query))
                {
                    s.Options.Timeout = TimeSpan.FromSeconds(5);
                    foreach (ManagementObject o in s.Get())
                    {
                        var r = new WmiRow();
                        foreach (PropertyData p in o.Properties) r.V[p.Name] = p.Value;
                        object inst; r.Instance = r.V.TryGetValue("InstanceName", out inst) ? Convert.ToString(inst) : "";
                        rows.Add(r);
                        o.Dispose();
                    }
                }
            }
            catch { }
            return rows;
        }

        static long Num(WmiRow r, string key)
        {
            object v;
            if (r == null || !r.V.TryGetValue(key, out v) || v == null) return -1;
            try { return Convert.ToInt64(v); } catch { return -1; }
        }

        static bool Flag(WmiRow r, string key)
        {
            object v;
            if (r == null || !r.V.TryGetValue(key, out v) || v == null) return false;
            try { return Convert.ToBoolean(v); } catch { return false; }
        }

        static string Str(WmiRow r, string key)
        {
            object v;
            if (r == null || !r.V.TryGetValue(key, out v) || v == null) return "";
            return Convert.ToString(v).Trim();
        }

        static WmiRow Find(List<WmiRow> rows, string instance, int index)
        {
            foreach (var r in rows) if (!string.IsNullOrEmpty(instance) && r.Instance == instance) return r;
            return index < rows.Count ? rows[index] : null;
        }

        static List<BatteryInfo> QueryWmi()
        {
            var list = new List<BatteryInfo>();
            var stat = Wmi("root\\wmi", "SELECT * FROM BatteryStaticData");
            var full = Wmi("root\\wmi", "SELECT * FROM BatteryFullChargedCapacity");
            var st = Wmi("root\\wmi", "SELECT * FROM BatteryStatus");
            var cyc = Wmi("root\\wmi", "SELECT * FROM BatteryCycleCount");
            var main = st.Count > 0 ? st : stat;
            for (int i = 0; i < main.Count; i++)
            {
                string inst = main[i].Instance;
                var s = Find(stat, inst, i); var f = Find(full, inst, i); var t = Find(st, inst, i); var c = Find(cyc, inst, i);
                var b = new BatteryInfo { Source = "WMI" };
                b.Name = Str(s, "DeviceName"); b.Manufacturer = Str(s, "ManufactureName"); b.Serial = Str(s, "SerialNumber");
                long chem = Num(s, "Chemistry");
                if (chem > 0) b.Chemistry = ChemistryName(Encoding.ASCII.GetString(BitConverter.GetBytes((uint)chem)));
                long v = Num(s, "DesignedCapacity"); if (v > 0 && v != BATTERY_UNKNOWN) b.DesignMWh = v;
                v = Num(f, "FullChargedCapacity"); if (v > 0 && v != BATTERY_UNKNOWN) b.FullMWh = v;
                v = Num(t, "RemainingCapacity"); if (v >= 0 && v != BATTERY_UNKNOWN) b.RemainingMWh = v;
                v = Num(t, "Voltage"); if (v > 0 && v != BATTERY_UNKNOWN) b.VoltageMV = (int)v;
                b.PowerOnline = Flag(t, "PowerOnline"); b.Charging = Flag(t, "Charging"); b.Discharging = Flag(t, "Discharging"); b.Critical = Flag(t, "Critical");
                long cr = Num(t, "ChargeRate"), dr = Num(t, "DischargeRate");
                if (b.Charging && cr > 0 && cr < int.MaxValue) b.RateMW = (int)cr;
                else if (b.Discharging && dr > 0 && dr < int.MaxValue) b.RateMW = -(int)dr;
                else if (t != null && !b.Charging && !b.Discharging) b.RateMW = 0;
                v = Num(c, "CycleCount"); if (v > 0 && v < 100000) b.CycleCount = (int)v;
                if (b.FullMWh > 0) b.Percent = Clamp(100.0 * b.RemainingMWh / b.FullMWh);
                if (b.RateMW.HasValue && b.RateMW.Value < 0 && b.RemainingMWh > 0) b.EstimatedMinutes = (int)(b.RemainingMWh * 60L / -b.RateMW.Value);
                if (b.Name.Length == 0) b.Name = L.T("Аккумулятор");
                list.Add(b);
            }
            return list;
        }

        static void FillMissingFromWmi(List<BatteryInfo> list)
        {
            bool needCycles = false;
            foreach (var b in list) if (!b.CycleCount.HasValue) needCycles = true;
            if (!needCycles) return;
            var cyc = Wmi("root\\wmi", "SELECT * FROM BatteryCycleCount");
            if (cyc.Count != list.Count) return;
            for (int i = 0; i < list.Count; i++)
            {
                long v = Num(cyc[i], "CycleCount");
                if (!list[i].CycleCount.HasValue && v > 0 && v < 100000) list[i].CycleCount = (int)v;
            }
        }

        static List<BatteryInfo> QueryWin32()
        {
            var list = new List<BatteryInfo>();
            foreach (var r in Wmi("root\\cimv2", "SELECT * FROM Win32_Battery"))
            {
                var b = new BatteryInfo { Source = "Win32_Battery" };
                b.Name = Str(r, "Name"); if (b.Name.Length == 0) b.Name = L.T("Аккумулятор");
                b.Serial = Str(r, "DeviceID");
                long v = Num(r, "EstimatedChargeRemaining"); if (v >= 0 && v <= 100) b.Percent = v;
                v = Num(r, "DesignCapacity"); if (v > 0) b.DesignMWh = v;
                v = Num(r, "FullChargeCapacity"); if (v > 0) b.FullMWh = v;
                if (b.FullMWh > 0 && b.Percent.HasValue) b.RemainingMWh = (long)(b.FullMWh * b.Percent.Value / 100);
                v = Num(r, "EstimatedRunTime"); if (v > 0 && v < 100 * 60) b.EstimatedMinutes = (int)v;
                long status = Num(r, "BatteryStatus");
                b.Discharging = status == 1 || status == 4 || status == 5;
                b.PowerOnline = status == 2 || status == 3 || (status >= 6 && status <= 9) || status == 11;
                b.Charging = status >= 6 && status <= 9;
                b.Critical = status == 5;
                switch ((int)Num(r, "Chemistry"))
                {
                    case 3: b.Chemistry = L.T("Свинцово-кислотный"); break;
                    case 4: b.Chemistry = "NiCd"; break;
                    case 5: b.Chemistry = "NiMH"; break;
                    case 6: b.Chemistry = "Li-ion"; break;
                    case 8: b.Chemistry = "LiPo"; break;
                }
                list.Add(b);
            }
            return list;
        }

        // ---------------------------------------------------------------- demo
        static BatteryInfo DemoBattery()
        {
            return new BatteryInfo
            {
                Source = "Demo", Name = "DELL M59JH96", Manufacturer = "SMP", Serial = "3142", Chemistry = "LiPo",
                DesignMWh = 86000, FullMWh = 78260, RemainingMWh = 59478, Percent = 76, VoltageMV = 12148, RateMW = -14200,
                PowerOnline = false, Charging = false, Discharging = true, CycleCount = 214, TemperatureC = 38,
                ManufactureDate = "2023-03-14", EstimatedMinutes = 251,
            };
        }
    }

    // ==================================================================== discharge / stress test

    public enum BatteryLoad { None, Cpu, CpuGpu }

    public enum BatteryStopReason { None, Duration, TargetPercent, Cancelled, AcPlugged, Critical, NoBattery, Error }

    public class BatteryTestOptions
    {
        public int Minutes = 30;               // 0 = no time limit (stops only at StopPercent)
        public int StopPercent = 20;           // clamped to 5…95
        public BatteryLoad Load = BatteryLoad.Cpu;
        public int SampleSeconds = 5;
    }

    public class BatterySample
    {
        public double Seconds;                 // since start
        public double Percent;
        public long RemainingMWh;
        public int? RateMW;                    // − while discharging
        public int? VoltageMV;
        public double? CpuTempC;
        public double DischargeW { get { return RateMW.HasValue && RateMW.Value < 0 ? -RateMW.Value / 1000.0 : 0; } }
    }

    public class BatteryTestResult
    {
        public BatteryStopReason Reason;
        public string ReasonText = "";
        public BatteryLoad Load;
        public double Seconds;
        public double StartPercent, EndPercent, PercentUsed;
        public long MWhUsed;
        public double AvgW, PeakW;
        public long FullMWh, DesignMWh;
        public double? RuntimeHours;           // FullMWh / AvgW
        public long? MeasuredCapacityMWh;      // MWhUsed / PercentUsed × 100 (null if < 3 % used)
        public double? CapacityDeltaPercent;   // measured vs reported FullMWh, + = more than reported
        public int? StartVoltageMV, MinVoltageMV, VoltageSagMV;
        public double? MaxCpuTempC;
        public string Verdict = "";
        public List<BatterySample> Samples = new List<BatterySample>();
    }

    /// <summary>
    /// Discharge test on a background thread. Events fire on that thread — the UI must marshal (BeginInvoke).
    /// Stops at Minutes, at StopPercent, on Cancel(), when AC is plugged in, or when a battery goes critical.
    /// The CPU load (CpuStress on all threads) is always stopped in finally. The GPU load for BatteryLoad.CpuGpu is the
    /// UI's job — here it is only recorded. In Demo.On the test replays DemoSamples() quickly instead of reading hardware.
    /// </summary>
    public class BatteryTest
    {
        public event Action<BatterySample> SampleAdded;
        public event Action<BatteryTestResult> Finished;

        readonly object sync = new object();
        readonly List<BatterySample> samples = new List<BatterySample>();
        readonly ManualResetEvent cancel = new ManualResetEvent(false);
        Thread thread;
        volatile bool running;
        BatteryTestOptions opt;

        public bool Running { get { return running; } }
        public BatteryTestOptions Options { get { return opt; } }

        /// <summary>Snapshot of the samples collected so far.</summary>
        public List<BatterySample> Samples { get { lock (sync) return new List<BatterySample>(samples); } }

        /// <summary>Null if a test can start now, otherwise the reason (localized).</summary>
        public static string CheckPreconditions(int stopPercent)
        {
            try
            {
                if (Demo.On) return null;
                if (!BatteryService.HasBattery) return L.T("Аккумулятор не найден");
                var p = BatteryService.Power();
                if (p.AcOnline != false) return L.T("Отключите зарядное устройство: тест идёт только от аккумулятора");
                var b = BatteryService.Combined(BatteryService.Query());
                if (b == null || !b.Percent.HasValue) return L.T("Не удалось прочитать заряд аккумулятора");
                if (b.Critical) return L.T("Заряд аккумулятора критический");
                int stop = Math.Max(5, Math.Min(95, stopPercent));
                if (b.Percent.Value < 20) return L.T("Заряд ниже 20 % — сначала зарядите аккумулятор");
                if (b.Percent.Value <= stop + 1) return L.F("Заряд уже на уровне остановки ({0} %)", stop);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>Starts the test. Returns false with a localized error when preconditions fail or a test is running.</summary>
        public bool Start(BatteryTestOptions options, out string error)
        {
            error = null;
            if (running) { error = L.T("Тест уже идёт"); return false; }
            opt = options ?? new BatteryTestOptions();
            opt.StopPercent = Math.Max(5, Math.Min(95, opt.StopPercent));
            opt.SampleSeconds = Math.Max(1, Math.Min(60, opt.SampleSeconds));
            if (opt.Minutes < 0) opt.Minutes = 0;
            error = CheckPreconditions(opt.StopPercent);
            if (error != null) return false;
            lock (sync) samples.Clear();
            cancel.Reset();
            running = true;
            thread = new Thread(Run) { IsBackground = true, Name = "battery-test" };
            thread.Start();
            return true;
        }

        public void Cancel() { try { cancel.Set(); } catch { } }

        static BatterySample Read(double seconds, out bool online, out bool critical, out bool any)
        {
            online = false; critical = false; any = false;
            var list = BatteryService.Query();
            var b = BatteryService.Combined(list);
            var s = new BatterySample { Seconds = seconds };
            if (b == null) return s;
            any = true;
            online = b.PowerOnline || b.Charging;
            critical = b.Critical;
            s.Percent = b.Percent.HasValue ? b.Percent.Value : 0;
            s.RemainingMWh = b.RemainingMWh;
            s.RateMW = b.RateMW;
            s.VoltageMV = b.VoltageMV;
            return s;
        }

        void Add(BatterySample s)
        {
            lock (sync) samples.Add(s);
            var h = SampleAdded;
            if (h != null) try { h(s); } catch { }
        }

        void Run()
        {
            var reason = BatteryStopReason.None; string errorText = null;
            CpuStress stress = null; CpuSensors sensors = null;
            long full = 0, design = 0;
            var start = DateTime.UtcNow;
            try
            {
                if (Demo.On) { reason = RunDemo(); return; }
                var info = BatteryService.Combined(BatteryService.Query());
                if (info != null) { full = info.FullMWh; design = info.DesignMWh; }
                try { sensors = new CpuSensors(); } catch { sensors = null; }
                if (opt.Load != BatteryLoad.None)
                {
                    stress = new CpuStress();
                    stress.Start(Environment.ProcessorCount);
                }
                double nextSample = 0;
                while (true)
                {
                    double t = (DateTime.UtcNow - start).TotalSeconds;
                    var p = BatteryService.Power();
                    if (p.AcOnline == true) { reason = BatteryStopReason.AcPlugged; break; }
                    if (t >= nextSample)
                    {
                        bool online, critical, any;
                        var s = Read(t, out online, out critical, out any);
                        if (!any) { reason = BatteryStopReason.NoBattery; break; }
                        if (sensors != null) { try { s.CpuTempC = sensors.Temperature(); } catch { } }
                        Add(s);
                        if (online) { reason = BatteryStopReason.AcPlugged; break; }
                        if (critical) { reason = BatteryStopReason.Critical; break; }
                        if (s.Percent <= opt.StopPercent) { reason = BatteryStopReason.TargetPercent; break; }
                        nextSample += opt.SampleSeconds;
                    }
                    if (opt.Minutes > 0 && t >= opt.Minutes * 60.0) { reason = BatteryStopReason.Duration; break; }
                    if (cancel.WaitOne(1000)) { reason = BatteryStopReason.Cancelled; break; }
                }
                // closing sample so the result covers the whole run
                if (reason == BatteryStopReason.Duration || reason == BatteryStopReason.Cancelled)
                {
                    bool o, c, a;
                    var last = Read((DateTime.UtcNow - start).TotalSeconds, out o, out c, out a);
                    if (a && !o) { if (sensors != null) { try { last.CpuTempC = sensors.Temperature(); } catch { } } Add(last); }
                }
            }
            catch (Exception ex) { reason = BatteryStopReason.Error; errorText = ex.Message; }
            finally
            {
                if (stress != null) { try { stress.Stop(); } catch { } }
                if (sensors != null) { try { sensors.Dispose(); } catch { } }
                BatteryTestResult r;
                try
                {
                    if (Demo.On) { full = 78260; design = 86000; }
                    r = Analyze(Samples, full, design, opt.Load, reason);
                    if (errorText != null) r.ReasonText = r.ReasonText + ": " + errorText;
                }
                catch (Exception ex) { r = new BatteryTestResult { Reason = BatteryStopReason.Error, ReasonText = ex.Message }; }
                running = false;
                var h = Finished;
                if (h != null) try { h(r); } catch { }
            }
        }

        BatteryStopReason RunDemo()
        {
            foreach (var s in DemoSamples())
            {
                Add(s);
                if (opt.Minutes > 0 && s.Seconds >= opt.Minutes * 60.0) return BatteryStopReason.Duration;
                if (s.Percent <= opt.StopPercent) return BatteryStopReason.TargetPercent;
                if (cancel.WaitOne(40)) return BatteryStopReason.Cancelled;
            }
            return BatteryStopReason.Duration;
        }

        public static string ReasonText(BatteryStopReason r)
        {
            switch (r)
            {
                case BatteryStopReason.Duration: return L.T("Время теста истекло");
                case BatteryStopReason.TargetPercent: return L.T("Достигнут заданный уровень заряда");
                case BatteryStopReason.Cancelled: return L.T("Тест остановлен");
                case BatteryStopReason.AcPlugged: return L.T("Подключено зарядное устройство — тест прерван");
                case BatteryStopReason.Critical: return L.T("Критический заряд аккумулятора — тест прерван");
                case BatteryStopReason.NoBattery: return L.T("Аккумулятор пропал — тест прерван");
                case BatteryStopReason.Error: return L.T("Ошибка теста");
                default: return "";
            }
        }

        /// <summary>Computes the result from samples (also used for demo results). Never throws for any list.</summary>
        public static BatteryTestResult Analyze(List<BatterySample> s, long fullMWh, long designMWh, BatteryLoad load, BatteryStopReason reason)
        {
            var r = new BatteryTestResult { Reason = reason, ReasonText = ReasonText(reason), Load = load, FullMWh = fullMWh, DesignMWh = designMWh };
            r.Samples = s ?? new List<BatterySample>();
            if (r.Samples.Count == 0) { r.Verdict = L.T("Нет данных: тест остановился до первого замера"); return r; }
            var a = r.Samples[0]; var z = r.Samples[r.Samples.Count - 1];
            r.Seconds = z.Seconds - a.Seconds;
            r.StartPercent = a.Percent; r.EndPercent = z.Percent;
            r.PercentUsed = Math.Max(0, a.Percent - z.Percent);
            r.MWhUsed = Math.Max(0, a.RemainingMWh - z.RemainingMWh);

            double rateSum = 0; int rateN = 0;
            foreach (var x in r.Samples)
            {
                double w = x.DischargeW;
                if (w > 0) { rateSum += w; rateN++; if (w > r.PeakW) r.PeakW = w; }
                if (x.VoltageMV.HasValue)
                {
                    if (!r.StartVoltageMV.HasValue) r.StartVoltageMV = x.VoltageMV;
                    if (!r.MinVoltageMV.HasValue || x.VoltageMV.Value < r.MinVoltageMV.Value) r.MinVoltageMV = x.VoltageMV;
                }
                if (x.CpuTempC.HasValue && (!r.MaxCpuTempC.HasValue || x.CpuTempC.Value > r.MaxCpuTempC.Value)) r.MaxCpuTempC = x.CpuTempC;
            }
            // energy-based average is more honest than the averaged instantaneous rate once enough was used
            if (r.MWhUsed > 0 && r.Seconds >= 60) r.AvgW = r.MWhUsed / 1000.0 / (r.Seconds / 3600.0);
            else if (rateN > 0) r.AvgW = rateSum / rateN;
            if (r.PeakW < r.AvgW) r.PeakW = r.AvgW;
            if (r.StartVoltageMV.HasValue && r.MinVoltageMV.HasValue) r.VoltageSagMV = r.StartVoltageMV.Value - r.MinVoltageMV.Value;
            if (fullMWh > 0 && r.AvgW > 0.1) r.RuntimeHours = fullMWh / 1000.0 / r.AvgW;
            if (r.PercentUsed >= 3 && r.MWhUsed > 0)
            {
                r.MeasuredCapacityMWh = (long)(r.MWhUsed / r.PercentUsed * 100);
                if (fullMWh > 0) r.CapacityDeltaPercent = 100.0 * (r.MeasuredCapacityMWh.Value - fullMWh) / fullMWh;
            }

            var v = new List<string>();
            if (r.RuntimeHours.HasValue)
            {
                int mins = (int)Math.Round(r.RuntimeHours.Value * 60);
                v.Add(L.F("При такой нагрузке ({0} Вт) полного заряда хватит примерно на {1} ч {2:00} мин.",
                    r.AvgW.ToString("0.0", CultureInfo.CurrentCulture), mins / 60, mins % 60));
            }
            if (!r.CapacityDeltaPercent.HasValue) v.Add(L.T("Слишком мало израсходовано для оценки ёмкости — продлите тест."));
            else if (r.CapacityDeltaPercent.Value >= -10) v.Add(L.T("Измеренная ёмкость соответствует данным контроллера."));
            else v.Add(L.F("Измеренная ёмкость на {0} % ниже, чем сообщает контроллер: износ или контроллеру нужна калибровка.",
                Math.Round(-r.CapacityDeltaPercent.Value)));
            if (r.VoltageSagMV.HasValue && r.StartVoltageMV.HasValue && r.StartVoltageMV.Value > 0 && r.VoltageSagMV.Value > r.StartVoltageMV.Value * 0.12)
                v.Add(L.T("Сильная просадка напряжения под нагрузкой — признак износа элементов."));
            if (designMWh > 0 && fullMWh > 0)
            {
                double health = 100.0 * fullMWh / designMWh;
                if (health < 60) v.Add(L.T("Аккумулятор сильно изношен, стоит подумать о замене."));
                else if (health < 80) v.Add(L.T("Аккумулятор заметно изношен."));
            }
            r.Verdict = string.Join(" ", v.ToArray());
            return r;
        }

        /// <summary>
        /// Demo discharge curve for screenshots / charts in demo mode: 30 min under CPU load, one sample per 5 s (361 samples),
        /// 76 % → ~58 %, ~28 W with ripple, voltage 12.15 → ~11.6 V, CPU 46 → ~80 °C. Deterministic.
        /// </summary>
        public static List<BatterySample> DemoSamples()
        {
            var list = new List<BatterySample>();
            const double full = 78260;
            double mwh = full * 0.76;
            for (int i = 0; i <= 360; i++)
            {
                double t = i * 5.0;
                double w = 27.5 + 2.2 * Math.Sin(i * 0.37) + 1.1 * Math.Sin(i * 1.91) + (i < 6 ? -6 + i : 0);
                if (i > 0) mwh -= w * 1000.0 * 5.0 / 3600.0;
                double pct = 100.0 * mwh / full;
                list.Add(new BatterySample
                {
                    Seconds = t, Percent = Math.Round(pct, 2), RemainingMWh = (long)mwh, RateMW = -(int)(w * 1000),
                    VoltageMV = (int)(12150 - (76 - pct) * 22 - w * 8 + 30 * Math.Sin(i * 0.5)),
                    CpuTempC = Math.Round(80 - 34 * Math.Exp(-t / 240.0) + 1.5 * Math.Sin(i * 0.8), 1),
                });
            }
            return list;
        }

        /// <summary>Finished-test result built from DemoSamples() (demo screenshots of the result card).</summary>
        public static BatteryTestResult DemoResult()
        {
            return Analyze(DemoSamples(), 78260, 86000, BatteryLoad.Cpu, BatteryStopReason.Duration);
        }
    }
}
