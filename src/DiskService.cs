using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;

namespace FoxideDisk
{
    public enum DiskKind { SSD, HDD, Removable, Virtual, Other }
    public enum HealthState { Good, Warning, Bad, Unknown }

    public class PartInfo
    {
        public int Number;
        public ulong Offset, Size;
        public char Letter;
        public string Type = "";
        public string Label = "", FileSystem = "";
        public ulong Free;
        public bool HasFree;
        public bool IsService;
        public bool IsSystem, IsBoot;
        public string VolumePath;      // \\?\Volume{guid}\ if the partition has a volume
    }

    public class Reliability
    {
        public long? Temperature, TemperatureMax, Wear, PowerOnHours, StartStopCycles, LoadUnloadCycles;
        public long? ReadErrors, ReadErrorsUncorrected, WriteErrors, WriteErrorsUncorrected;
        public long? ReadLatencyMax, WriteLatencyMax;
    }

    public class DiskInfo
    {
        public int Number;
        public string Model = "", Serial = "", Firmware = "", UniqueId = "", Location = "";
        public ulong Size;
        public int BusType, MediaType, PartitionStyle, HealthStatus = 5;
        public uint SpindleSpeed;
        public uint LogicalSector, PhysicalSector;
        public bool IsSystem, IsBoot, IsOffline, IsReadOnly;
        public bool? PredictFailure;
        public Reliability Rel;
        public List<PartInfo> Partitions = new List<PartInfo>();
        public DiskKind Kind;
        public HealthState Health = HealthState.Unknown;
        public List<string> HealthNotes = new List<string>();

        public bool IsProtected
        {
            get
            {
                if (IsSystem || IsBoot) return true;
                string winDrive = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                char w = string.IsNullOrEmpty(winDrive) ? 'C' : char.ToUpperInvariant(winDrive[0]);
                return Partitions.Any(p => char.ToUpperInvariant(p.Letter) == w);
            }
        }

        public string Letters
        {
            get { return string.Join(" ", Partitions.Where(p => p.Letter != '\0').Select(p => p.Letter + ":")); }
        }

        public string BusName { get { return DiskService.BusName(BusType); } }
        public SmartReport Smart;
        public int? HealthPercent;
        public string HealthBasis;
        public long? TempC;

        public int TempWarn { get { return IsRotating ? 50 : 70; } }
        public int TempBad { get { return IsRotating ? 60 : 80; } }

        /// <summary>Volumes with a file system (the ones whose fill level is known).</summary>
        public List<PartInfo> Volumes { get { return Partitions.Where(p => p.HasFree && p.Size > 0).ToList(); } }
        public ulong VolumesSize { get { return (ulong)Volumes.Sum(p => (double)p.Size); } }
        public ulong VolumesUsed { get { return (ulong)Volumes.Sum(p => (double)(p.Size > p.Free ? p.Size - p.Free : 0)); } }
        public double UsedFraction { get { var s = VolumesSize; return s > 0 ? (double)VolumesUsed / s : 0; } }
        public int Rpm;                 // 0 unknown, 1 non-rotating, else RPM
        public string RpmSource;
        public bool IsRotating { get { return Rpm > 1 || (Kind == DiskKind.HDD); } }
        public string RpmText { get { return Rpm > 1 ? L.F("{0} об/мин", Rpm.ToString("N0", CultureInfo.CurrentCulture)) : ""; } }

        public string KindName
        {
            get { return DiskService.KindName(Kind, BusType, Rpm > 1 ? 3 : Rpm == 1 ? 4 : MediaType); }
        }
        public string StyleName
        {
            get { return PartitionStyle == 1 ? "MBR" : PartitionStyle == 2 ? "GPT" : PartitionStyle == 3 ? L.T("Не инициализирован (RAW)") : L.T("Неизвестно"); }
        }
        public string Signature { get { return UniqueId + "|" + Size + "|" + Serial; } }
    }

    public static class DiskService
    {
        const string NS = @"\\.\root\Microsoft\Windows\Storage";

        static IEnumerable<ManagementObject> Query(ManagementScope s, string q)
        {
            using (var searcher = new ManagementObjectSearcher(s, new ObjectQuery(q)))
            using (var res = searcher.Get())
                foreach (ManagementObject o in res) yield return o;
        }

        static object Raw(ManagementBaseObject o, string p) { try { return o[p]; } catch { return null; } }
        static string Str(ManagementBaseObject o, string p) { var v = Raw(o, p); return v == null ? "" : v.ToString().Trim(); }
        static long? Lng(ManagementBaseObject o, string p)
        {
            var v = Raw(o, p);
            if (v == null) return null;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return null; }
        }
        static ulong ULng(ManagementBaseObject o, string p)
        {
            var v = Raw(o, p);
            if (v == null) return 0;
            try { return Convert.ToUInt64(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }
        static int Int(ManagementBaseObject o, string p, int def = 0) { var l = Lng(o, p); return l.HasValue ? (int)l.Value : def; }
        static bool Bool(ManagementBaseObject o, string p) { var v = Raw(o, p); return v is bool && (bool)v; }
        static char Chr(ManagementBaseObject o, string p)
        {
            var v = Raw(o, p);
            if (v == null) return '\0';
            if (v is char) return char.ToUpperInvariant((char)v);
            try { int i = Convert.ToInt32(v); return i > 32 ? char.ToUpperInvariant((char)i) : '\0'; } catch { return '\0'; }
        }

        public static List<DiskInfo> Load()
        {
            var scope = new ManagementScope(NS);
            scope.Connect();

            var phys = new Dictionary<string, ManagementObject>();
            foreach (var o in Query(scope, "SELECT * FROM MSFT_PhysicalDisk"))
                phys[Str(o, "DeviceId")] = o;

            var vols = new Dictionary<string, ManagementObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in Query(scope, "SELECT * FROM MSFT_Volume"))
            {
                var path = Str(v, "Path");
                if (path.Length > 0) vols[path] = v;
            }

            var parts = new Dictionary<int, List<PartInfo>>();
            foreach (var p in Query(scope, "SELECT * FROM MSFT_Partition"))
            {
                var pi = new PartInfo
                {
                    Number = Int(p, "PartitionNumber"),
                    Offset = ULng(p, "Offset"),
                    Size = ULng(p, "Size"),
                    Letter = Chr(p, "DriveLetter"),
                    IsSystem = Bool(p, "IsSystem"),
                    IsBoot = Bool(p, "IsBoot"),
                };
                string gpt = Str(p, "GptType");
                int mbr = Int(p, "MbrType");
                pi.Type = PartType(gpt, mbr, out pi.IsService);

                var paths = Raw(p, "AccessPaths") as string[];
                if (paths != null)
                {
                    foreach (var ap in paths)
                        if (ap.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase)) { pi.VolumePath = ap; break; }
                    foreach (var ap in paths)
                    {
                        ManagementObject v;
                        if (vols.TryGetValue(ap, out v))
                        {
                            pi.FileSystem = Str(v, "FileSystem");
                            pi.Label = Str(v, "FileSystemLabel");
                            var rem = Lng(v, "SizeRemaining");
                            if (rem.HasValue && pi.FileSystem.Length > 0) { pi.Free = (ulong)rem.Value; pi.HasFree = true; }
                            break;
                        }
                    }
                }
                int dn = Int(p, "DiskNumber", -1);
                List<PartInfo> l;
                if (!parts.TryGetValue(dn, out l)) parts[dn] = l = new List<PartInfo>();
                l.Add(pi);
            }

            var predict = LoadPredictFailure();
            var wmiAta = SmartReader.LoadWmiAta();
            var list = new List<DiskInfo>();

            foreach (var d in Query(scope, "SELECT * FROM MSFT_Disk"))
            {
                var di = new DiskInfo
                {
                    Number = Int(d, "Number", -1),
                    Model = Str(d, "FriendlyName"),
                    Serial = Str(d, "SerialNumber"),
                    Firmware = Str(d, "FirmwareVersion"),
                    UniqueId = Str(d, "UniqueId"),
                    Location = Str(d, "Location"),
                    Size = ULng(d, "Size"),
                    BusType = Int(d, "BusType"),
                    PartitionStyle = Int(d, "PartitionStyle"),
                    HealthStatus = Int(d, "HealthStatus", 5),
                    LogicalSector = (uint)ULng(d, "LogicalSectorSize"),
                    PhysicalSector = (uint)ULng(d, "PhysicalSectorSize"),
                    IsSystem = Bool(d, "IsSystem"),
                    IsBoot = Bool(d, "IsBoot"),
                    IsOffline = Bool(d, "IsOffline"),
                    IsReadOnly = Bool(d, "IsReadOnly"),
                };
                if (di.Model.Length == 0) di.Model = Str(d, "Model");

                ManagementObject pd;
                if (phys.TryGetValue(di.Number.ToString(CultureInfo.InvariantCulture), out pd))
                {
                    di.MediaType = Int(pd, "MediaType");
                    di.SpindleSpeed = (uint)ULng(pd, "SpindleSpeed");
                    int ph = Int(pd, "HealthStatus", 5);
                    if (ph != 5 && ph > (di.HealthStatus == 5 ? -1 : di.HealthStatus)) di.HealthStatus = ph;
                    if (di.Firmware.Length == 0) di.Firmware = Str(pd, "FirmwareVersion");
                    if (di.Serial.Length == 0) di.Serial = Str(pd, "SerialNumber");
                    di.Rel = LoadReliability(pd);
                }

                List<PartInfo> pl;
                if (parts.TryGetValue(di.Number, out pl)) di.Partitions = pl.OrderBy(x => x.Offset).ToList();

                bool pf;
                if (predict.TryGetValue(di.Number, out pf)) di.PredictFailure = pf;

                di.Kind = Classify(di.BusType, di.MediaType);

                if (di.SpindleSpeed > 0 && di.SpindleSpeed < 30000) { di.Rpm = (int)di.SpindleSpeed; di.RpmSource = "Windows"; }
                else if (di.Kind == DiskKind.SSD || di.MediaType == 4) di.Rpm = 1;
                if (di.Rpm == 0 && di.Kind != DiskKind.Virtual)
                    di.Rpm = Rpm.Detect(di.Number, di.Model, di.Signature, out di.RpmSource);

                if (di.Kind != DiskKind.Virtual)
                    di.Smart = SmartReader.Read(di, wmiAta) ?? SmartReader.FromReliability(di);

                Evaluate(di);
                list.Add(di);
            }
            return list.OrderBy(x => x.Number).ToList();
        }

        static Reliability LoadReliability(ManagementObject pd)
        {
            try
            {
                foreach (ManagementObject r in pd.GetRelated("MSFT_StorageReliabilityCounter"))
                {
                    return new Reliability
                    {
                        Temperature = Lng(r, "Temperature"),
                        TemperatureMax = Lng(r, "TemperatureMax"),
                        Wear = Lng(r, "Wear"),
                        PowerOnHours = Lng(r, "PowerOnHours"),
                        StartStopCycles = Lng(r, "StartStopCycleCount"),
                        LoadUnloadCycles = Lng(r, "LoadUnloadCycleCount"),
                        ReadErrors = Lng(r, "ReadErrorsTotal"),
                        ReadErrorsUncorrected = Lng(r, "ReadErrorsUncorrected"),
                        WriteErrors = Lng(r, "WriteErrorsTotal"),
                        WriteErrorsUncorrected = Lng(r, "WriteErrorsUncorrected"),
                        ReadLatencyMax = Lng(r, "ReadLatencyMax"),
                        WriteLatencyMax = Lng(r, "WriteLatencyMax"),
                    };
                }
            }
            catch { }
            return null;
        }

        static Dictionary<int, bool> LoadPredictFailure()
        {
            var res = new Dictionary<int, bool>();
            try
            {
                var pnp = new Dictionary<int, string>();
                using (var s = new ManagementObjectSearcher("root\\CIMV2", "SELECT Index, PNPDeviceID FROM Win32_DiskDrive"))
                    foreach (ManagementObject o in s.Get())
                    {
                        var id = Str(o, "PNPDeviceID");
                        if (id.Length > 0) pnp[Int(o, "Index", -1)] = id;
                    }
                using (var s = new ManagementObjectSearcher("root\\WMI", "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus"))
                    foreach (ManagementObject o in s.Get())
                    {
                        var inst = Str(o, "InstanceName");
                        foreach (var kv in pnp)
                            if (inst.StartsWith(kv.Value, StringComparison.OrdinalIgnoreCase))
                                res[kv.Key] = Bool(o, "PredictFailure");
                    }
            }
            catch { }
            return res;
        }

        static string PartType(string gpt, int mbr, out bool service)
        {
            service = false;
            switch ((gpt ?? "").Trim('{', '}').ToLowerInvariant())
            {
                case "c12a7328-f81f-11d2-ba4b-00a0c93ec93b": service = true; return L.T("Системный EFI");
                case "e3c9e316-0b5c-4db8-817d-f92df00215ae": service = true; return L.T("Зарезервировано (MSR)");
                case "de94bba4-06d1-4d40-a16a-bfd50179d6ac": service = true; return L.T("Восстановление");
                case "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7": return L.T("Основной раздел");
                case "5808c8aa-7e8f-42e0-85d2-e1e90434cfb3": service = true; return L.T("Метаданные LDM");
                case "af9b60a0-1431-4f62-bc68-3311714a69ad": return L.T("Данные LDM");
                case "0fc63daf-8483-4772-8e79-3d69d8477de4": return "Linux";
                case "0657fd6d-a4ab-43c4-84e5-0933c84b4f4f": service = true; return "Linux swap";
                case "": break;
                default: service = true; return L.T("Другой (GPT)");
            }
            switch (mbr)
            {
                case 0: return L.T("Основной раздел");
                case 7: return L.T("Основной раздел");
                case 6: case 11: case 12: case 14: return L.T("Основной (FAT)");
                case 0x27: service = true; return L.T("Восстановление");
                case 0x83: return "Linux";
                case 0x82: service = true; return "Linux swap";
                case 5: case 15: return L.T("Расширенный");
                default: return L.F("Тип 0x{0}", mbr.ToString("X2"));
            }
        }

        public static DiskKind Classify(int bus, int media)
        {
            if (bus == 7 || bus == 12 || bus == 13) return DiskKind.Removable;
            if (bus == 14 || bus == 15 || bus == 16) return DiskKind.Virtual;
            if (bus == 17 || media == 4 || media == 5) return DiskKind.SSD;
            if (media == 3) return DiskKind.HDD;
            return DiskKind.Other;
        }

        public static string BusName(int b)
        {
            switch (b)
            {
                case 1: return "SCSI"; case 2: return "ATAPI"; case 3: return "ATA"; case 4: return "IEEE 1394";
                case 5: return "SSA"; case 6: return "Fibre Channel"; case 7: return "USB"; case 8: return "RAID";
                case 9: return "iSCSI"; case 10: return "SAS"; case 11: return "SATA"; case 12: return "SD";
                case 13: return "MMC"; case 14: return L.T("Виртуальная"); case 15: return L.T("Виртуальный файл (VHD)");
                case 16: return "Storage Spaces"; case 17: return "NVMe"; case 18: return "SCM"; case 19: return "UFS";
                default: return L.T("Неизвестно");
            }
        }

        public static string KindName(DiskKind k, int bus, int media)
        {
            switch (k)
            {
                case DiskKind.SSD: return bus == 17 ? "NVMe SSD" : "SSD";
                case DiskKind.HDD: return "HDD";
                case DiskKind.Removable:
                    return bus == 7 ? (media == 4 ? "USB SSD" : media == 3 ? "USB HDD" : L.T("USB-накопитель")) : L.T("Карта памяти");
                case DiskKind.Virtual: return L.T("Виртуальный диск");
                default: return L.T("Диск");
            }
        }

        public static string KindGlyph(DiskKind k)
        {
            switch (k)
            {
                case DiskKind.SSD: return "\uE945";
                case DiskKind.Removable: return "\uE88E";
                case DiskKind.Virtual: return "\uE753";
                default: return "\uEDA2";
            }
        }

        internal static void Evaluate(DiskInfo d)
        {
            var n = d.HealthNotes;
            var st = HealthState.Good;
            bool any = false;

            if (d.PredictFailure.HasValue)
            {
                any = true;
                if (d.PredictFailure.Value) { st = HealthState.Bad; n.Add(L.T("SMART сообщает о скором отказе диска. Срочно сохраните данные.")); }
            }
            if (d.HealthStatus == 2) { any = true; st = HealthState.Bad; n.Add(L.T("Windows помечает диск как неисправный.")); }
            else if (d.HealthStatus == 1) { any = true; if (st == HealthState.Good) st = HealthState.Warning; n.Add(L.T("Windows сообщает о предупреждении состояния диска.")); }
            else if (d.HealthStatus == 0) any = true;

            var r = d.Rel;
            if (r != null)
            {
                if ((r.ReadErrorsUncorrected ?? 0) > 0 || (r.WriteErrorsUncorrected ?? 0) > 0)
                {
                    if (st == HealthState.Good) st = HealthState.Warning;
                    n.Add(L.T("Обнаружены неисправленные ошибки чтения/записи."));
                }
                if ((r.Wear ?? 0) >= 90)
                {
                    if (st == HealthState.Good) st = HealthState.Warning;
                    n.Add(L.F("Ресурс записи SSD почти исчерпан ({0}% износа).", r.Wear));
                }
                long t = r.Temperature ?? 0;
                int limit = d.Kind == DiskKind.HDD ? 55 : 70;
                if (t > limit)
                {
                    if (st == HealthState.Good) st = HealthState.Warning;
                    n.Add(L.F("Высокая температура: {0} °C.", t));
                }
            }
            if (d.Smart != null && d.Smart.Rows.Count > 0)
            {
                any = true;
                foreach (var row in d.Smart.Rows.Where(x => x.State >= SmartState.Warning))
                {
                    if (row.State == SmartState.Bad) st = HealthState.Bad;
                    else if (st == HealthState.Good) st = HealthState.Warning;
                    string line = (row.Id.Length > 0 ? row.Id + " " : "") + row.Name + ": " + row.Value + (row.Note.Length > 0 ? " — " + row.Note : "");
                    if (!n.Contains(line)) n.Add(line);
                }
            }
            if (d.IsReadOnly) n.Add(L.T("Диск доступен только для чтения."));
            if (d.IsOffline) n.Add(L.T("Диск отключён (offline) в Windows."));

            d.Health = any ? st : (st == HealthState.Good ? HealthState.Unknown : st);

            // temperature: Windows counters first, then SMART
            if ((d.Rel != null ? d.Rel.Temperature ?? 0 : 0) > 0) d.TempC = d.Rel.Temperature;
            else if (d.Smart != null && d.Smart.Temperature.HasValue && d.Smart.Temperature > 0) d.TempC = d.Smart.Temperature;

            string basis;
            d.HealthPercent = ComputePercent(d, out basis);
            d.HealthBasis = basis;
        }

        /// <summary>
        /// Remaining-life estimate in percent:
        ///  NVMe: 100 − "Percentage Used" (limited by the spare area);
        ///  ATA SSD: normalized value of the life / wear attribute (E7, E9, B1, AD, CA);
        ///  HDD: 100 minus penalties for reallocated / pending / uncorrectable sectors and similar errors.
        /// </summary>
        static int? ComputePercent(DiskInfo d, out string basis)
        {
            basis = null;
            int? pct = null;
            var s = d.Smart;
            if (s != null && s.Nvme)
            {
                long used = s.Wear ?? 0;
                pct = (int)Math.Max(0, 100 - used);
                basis = L.T("по израсходованному ресурсу NVMe");
                if (s.Spare.HasValue && s.SpareThreshold.HasValue && s.Spare < 100)
                {
                    pct = Math.Min(pct.Value, s.Spare.Value);
                    basis = L.T("по ресурсу и резерву NVMe");
                }
                if (s.MediaErrors > 0) pct = Math.Min(pct.Value, 60);
            }
            else if (s != null && s.Rows.Any(r => r.AttrId >= 0))
            {
                bool ssd = !d.IsRotating;
                SmartRow life = null;
                if (ssd)
                    foreach (int id in new[] { 0xE7, 0xE9, 0xB1, 0xAD, 0xCA })
                    {
                        var r = s.Attr(id);
                        if (r != null && r.Current > 0 && r.Current <= 100) { life = r; break; }
                    }
                if (life != null)
                {
                    pct = life.Current;
                    basis = L.F("по атрибуту износа {0}", life.Id);
                }
                else
                {
                    double pen = 0;
                    Func<int, long> raw = id => { var r = s.Attr(id); return r == null ? 0 : r.Raw & 0xFFFFFFFF; };
                    pen += Math.Min(60, raw(0x05) * 0.2);     // reallocated sectors
                    pen += Math.Min(60, raw(0xC5) * 1.0);     // pending
                    pen += Math.Min(60, raw(0xC6) * 1.0);     // offline uncorrectable
                    pen += Math.Min(40, raw(0xBB) * 0.5);     // reported uncorrectable
                    pen += Math.Min(20, raw(0x0A) * 2.0);     // spin retry
                    pen += Math.Min(10, raw(0xC7) * 0.05);    // interface CRC (cable)
                    pen += Math.Min(15, raw(0xB8) * 1.0);     // end-to-end
                    long lc = raw(0xC1);
                    if (lc > 300000) pen += Math.Min(20, (lc - 300000) / 30000.0);
                    pct = (int)Math.Round(Math.Max(1, 100 - pen));
                    basis = ssd ? L.T("по ошибкам и переназначенным секторам") : L.T("по сбойным секторам и ошибкам SMART");
                }
                if (s.Rows.Any(r => r.AttrId >= 0 && r.Threshold > 0 && r.Threshold < 254 && r.Current > 0 && r.Current <= r.Threshold))
                    pct = Math.Min(pct.Value, 10);
            }
            else if (d.Rel != null && d.Rel.Wear.HasValue && !d.IsRotating && d.Kind == DiskKind.SSD)
            {
                pct = (int)Math.Max(0, 100 - d.Rel.Wear.Value);
                basis = L.T("по износу (счётчики Windows)");
            }
            if (pct.HasValue && (d.PredictFailure == true || d.HealthStatus == 2)) pct = Math.Min(pct.Value, 20);
            return pct.HasValue ? Math.Max(0, Math.Min(100, pct.Value)) : (int?)null;
        }

        public static string FormatSize(ulong b)
        {
            string[] u = { L.T("Б"), L.T("КБ"), L.T("МБ"), L.T("ГБ"), L.T("ТБ"), L.T("ПБ") };
            double v = b; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v.ToString(i == 0 ? "0" : v >= 100 ? "0.#" : "0.##", CultureInfo.CurrentCulture) + " " + u[i];
        }

        public static string HealthName(HealthState h)
        {
            switch (h)
            {
                case HealthState.Good: return L.T("Исправен");
                case HealthState.Warning: return L.T("Внимание");
                case HealthState.Bad: return L.T("Неисправен");
                default: return L.T("Нет данных");
            }
        }
    }
}
