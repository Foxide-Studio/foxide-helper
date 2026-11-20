using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;

namespace FoxideDisk
{
    public class RamModule
    {
        public string Slot = "", Bank = "", Manufacturer = "", PartNumber = "", Serial = "", Type = "", FormFactor = "";
        public ulong Capacity;
        public uint Speed, ConfiguredSpeed, Voltage;
    }

    public class RamInfo
    {
        public List<RamModule> Modules = new List<RamModule>();
        public int Slots;
        public ulong MaxCapacity;
        public ulong Installed { get { return (ulong)Modules.Sum(m => (double)m.Capacity); } }
        public string Summary
        {
            get
            {
                if (Modules.Count == 0) return "";
                var types = Modules.Select(m => m.Type).Where(t => t.Length > 0).Distinct().ToList();
                uint spd = Modules.Max(m => m.ConfiguredSpeed > 0 ? m.ConfiguredSpeed : m.Speed);
                return DiskService.FormatSize(Installed) + (types.Count > 0 ? " · " + string.Join("/", types) : "") +
                       (spd > 0 ? L.F(" · {0} МТ/с", spd) : "") + L.F(" · модулей: {0}", Modules.Count) + (Slots > 0 ? L.F(" из {0} слотов", Slots) : "");
            }
        }
    }

    public struct RamStatus
    {
        public ulong Total, Available, CommitTotal, CommitLimit;
        public int Load;
    }

    public class MemDiagResult
    {
        public DateTime When;
        public bool Errors;
        public string Message;
    }

    public static class RamService
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        public static RamStatus Status()
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            GlobalMemoryStatusEx(ref m);
            return new RamStatus
            {
                Total = m.ullTotalPhys, Available = m.ullAvailPhys, Load = (int)m.dwMemoryLoad,
                CommitLimit = m.ullTotalPageFile, CommitTotal = m.ullTotalPageFile - m.ullAvailPageFile,
            };
        }

        static string TypeName(int smbios, int legacy)
        {
            switch (smbios)
            {
                case 20: return "DDR"; case 21: return "DDR2"; case 24: return "DDR3"; case 26: return "DDR4";
                case 27: return "LPDDR"; case 28: return "LPDDR2"; case 29: return "LPDDR3"; case 30: return "LPDDR4";
                case 34: return "DDR5"; case 35: return "LPDDR5";
            }
            switch (legacy)
            {
                case 20: return "DDR"; case 21: return "DDR2"; case 24: return "DDR3"; case 26: return "DDR4";
            }
            return "";
        }

        static string Vendor(string raw)
        {
            string s = (raw ?? "").Trim();
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {"80CE","Samsung"},{"CE00","Samsung"},{"802C","Micron"},{"2C00","Micron"},{"80AD","SK hynix"},{"AD00","SK hynix"},
                {"0198","Kingston"},{"9801","Kingston"},{"859B","Crucial"},{"9B85","Crucial"},{"04CB","ADATA"},{"CB04","ADATA"},
                {"029E","Corsair"},{"9E02","Corsair"},{"04CD","G.Skill"},{"CD04","G.Skill"},{"0443","Ramaxel"},{"4304","Ramaxel"},
                {"8551","Qimonda"},{"80B3","Inphi"},{"0B03","Elpida"},{"02FE","Elpida"},{"8304","Patriot"},{"0215","Transcend"},
            };
            foreach (var kv in map) if (s.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return s;
        }

        public static RamInfo Load()
        {
            var info = new RamInfo();
            try
            {
                using (var s = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_PhysicalMemory"))
                    foreach (ManagementObject o in s.Get())
                    {
                        Func<string, string> str = p => { try { var v = o[p]; return v == null ? "" : v.ToString().Trim(); } catch { return ""; } };
                        Func<string, long> num = p => { try { var v = o[p]; return v == null ? 0 : Convert.ToInt64(v); } catch { return 0; } };
                        int ff = (int)num("FormFactor");
                        info.Modules.Add(new RamModule
                        {
                            Slot = str("DeviceLocator"), Bank = str("BankLabel"),
                            Manufacturer = Vendor(str("Manufacturer")), PartNumber = str("PartNumber"), Serial = str("SerialNumber"),
                            Capacity = (ulong)num("Capacity"),
                            Speed = (uint)num("Speed"), ConfiguredSpeed = (uint)num("ConfiguredClockSpeed"),
                            Voltage = (uint)num("ConfiguredVoltage"),
                            Type = TypeName((int)num("SMBIOSMemoryType"), (int)num("MemoryType")),
                            FormFactor = ff == 8 ? "DIMM" : ff == 12 ? "SO-DIMM" : ff == 13 ? L.T("Встроенная") : "",
                        });
                    }
                using (var s = new ManagementObjectSearcher("root\\CIMV2", "SELECT MemoryDevices, MaxCapacityEx, MaxCapacity FROM Win32_PhysicalMemoryArray"))
                    foreach (ManagementObject o in s.Get())
                    {
                        try { info.Slots += Convert.ToInt32(o["MemoryDevices"]); } catch { }
                        try
                        {
                            ulong kb = o["MaxCapacityEx"] != null ? Convert.ToUInt64(o["MaxCapacityEx"]) : Convert.ToUInt64(o["MaxCapacity"]);
                            info.MaxCapacity += kb * 1024;
                        }
                        catch { }
                    }
            }
            catch { }
            return info;
        }

        /// <summary>Last result of Windows Memory Diagnostic (mdsched) from the System event log.</summary>
        public static MemDiagResult LastDiagnostic()
        {
            try
            {
                var q = new EventLogQuery("System", PathType.LogName,
                    "*[System[Provider[@Name='Microsoft-Windows-MemoryDiagnostics-Results']]]") { ReverseDirection = true };
                using (var r = new EventLogReader(q))
                {
                    var e = r.ReadEvent();
                    if (e == null) return null;
                    using (e)
                    {
                        string msg = null;
                        try { msg = e.FormatDescription(); } catch { }
                        // 1101 / 1201: no errors; 1102 / 1202: errors detected
                        bool errors = e.Id == 1102 || e.Id == 1202;
                        return new MemDiagResult
                        {
                            When = e.TimeCreated ?? DateTime.MinValue,
                            Errors = errors,
                            Message = string.IsNullOrEmpty(msg) ? (errors ? L.T("Обнаружены ошибки оборудования.") : L.T("Ошибки памяти не обнаружены.")) : msg.Trim(),
                        };
                    }
                }
            }
            catch { return null; }
        }

        /// <summary>Opens Windows Memory Diagnostic (asks to restart now or on next boot).</summary>
        public static void StartWindowsDiagnostic()
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "mdsched.exe")) { UseShellExecute = true });
        }
    }
}
