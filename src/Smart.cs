using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FoxideDisk
{
    public enum SmartState { Ok, Info, Warning, Bad }

    public class SmartRow
    {
        public string Id = "";          // "05" for ATA, "" for NVMe / Windows
        public string Name = "";
        public string Value = "";       // human readable (raw interpreted)
        public string Normalized = "";  // "100 / 100 / 36" (current / worst / threshold)
        public string Norm = "";        // expected range
        public SmartState State = SmartState.Ok;
        public string Note = "";        // deviation explanation
        public int AttrId = -1, Current, Threshold;
        public long Raw;
    }

    public class SmartReport
    {
        public string Source = "";
        public List<SmartRow> Rows = new List<SmartRow>();
        public long? Temperature, PowerOnHours, Wear;
        public bool Nvme;
        public int? Spare, SpareThreshold;
        public long MediaErrors;
        public SmartRow Attr(int id) { return Rows.FirstOrDefault(r => r.AttrId == id); }
        public int Count(SmartState s) { return Rows.Count(r => r.State == s); }
    }

    /// <summary>Reads extended health data: ATA SMART attributes (WMI or SAT pass-through for USB bridges) and the NVMe SMART/Health log.</summary>
    public static class SmartReader
    {
        static CultureInfo C { get { return CultureInfo.CurrentCulture; } }
        static string N(long v) { return v.ToString("N0", C); }

        public static SmartReport Read(DiskInfo d, Dictionary<int, byte[][]> wmiAta)
        {
            try
            {
                if (d.BusType == 17)
                {
                    var r = ReadNvme(d.Number);
                    if (r != null) return r;
                }
                byte[][] ata;
                if (wmiAta != null && wmiAta.TryGetValue(d.Number, out ata))
                {
                    var r = ParseAta(ata[0], ata[1], d);
                    if (r != null) { r.Source = L.T("SMART (ATA), через Windows"); return r; }
                }
                if (d.Kind != DiskKind.Virtual && d.BusType != 17)
                {
                    var r = ReadAtaPassThrough(d);
                    if (r != null) return r;
                }
            }
            catch { }
            return null;
        }

        // ---------------------------------------------------------------- ATA via WMI (internal SATA)
        public static Dictionary<int, byte[][]> LoadWmiAta()
        {
            var res = new Dictionary<int, byte[][]>();
            try
            {
                var pnp = new Dictionary<int, string>();
                using (var s = new ManagementObjectSearcher("root\\CIMV2", "SELECT Index, PNPDeviceID FROM Win32_DiskDrive"))
                    foreach (ManagementObject o in s.Get())
                        if (o["PNPDeviceID"] != null) pnp[Convert.ToInt32(o["Index"])] = o["PNPDeviceID"].ToString();

                var data = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                var thr = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                using (var s = new ManagementObjectSearcher("root\\WMI", "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictData"))
                    foreach (ManagementObject o in s.Get()) data[o["InstanceName"].ToString()] = o["VendorSpecific"] as byte[];
                using (var s = new ManagementObjectSearcher("root\\WMI", "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictThresholds"))
                    foreach (ManagementObject o in s.Get()) thr[o["InstanceName"].ToString()] = o["VendorSpecific"] as byte[];

                foreach (var kv in data)
                    foreach (var p in pnp)
                        if (kv.Key.StartsWith(p.Value, StringComparison.OrdinalIgnoreCase))
                        {
                            byte[] t; thr.TryGetValue(kv.Key, out t);
                            res[p.Key] = new[] { kv.Value, t };
                        }
            }
            catch { }
            return res;
        }

        // ---------------------------------------------------------------- ATA via SCSI/ATA Translation (USB bridges)
        static SmartReport ReadAtaPassThrough(DiskInfo d)
        {
            using (var h = Native.CreateFile(@"\\.\PhysicalDrive" + d.Number, Native.GENERIC_READ | Native.GENERIC_WRITE, 3, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return null;
                // SMART READ DATA (B0h / D0h) and READ THRESHOLDS (D1h), LBA mid/high = 4F/C2
                byte[] data = Rpm.Scsi(h, new byte[] { 0xA1, 0x08, 0x0E, 0xD0, 1, 0, 0x4F, 0xC2, 0, 0xB0, 0, 0 }, 512);
                byte[] thr = null;
                if (Valid(data)) thr = Rpm.Scsi(h, new byte[] { 0xA1, 0x08, 0x0E, 0xD1, 1, 0, 0x4F, 0xC2, 0, 0xB0, 0, 0 }, 512);
                else
                {
                    data = Rpm.Scsi(h, new byte[] { 0x85, 0x08, 0x0E, 0, 0xD0, 0, 1, 0, 0, 0, 0x4F, 0, 0xC2, 0, 0xB0, 0 }, 512);
                    if (Valid(data)) thr = Rpm.Scsi(h, new byte[] { 0x85, 0x08, 0x0E, 0, 0xD1, 0, 1, 0, 0, 0, 0x4F, 0, 0xC2, 0, 0xB0, 0 }, 512);
                }
                if (!Valid(data)) return null;
                var r = ParseAta(data, Valid(thr) ? thr : null, d);
                if (r != null) r.Source = d.BusType == 7 ? L.T("SMART (ATA), через USB-мост") : L.T("SMART (ATA), прямой запрос");
                return r;
            }
        }

        static bool Valid(byte[] b)
        {
            if (b == null || b.Length < 512) return false;
            int sum = 0; bool any = false;
            for (int i = 0; i < 512; i++) { sum += b[i]; if (b[i] != 0) any = true; }
            if (!any) return false;
            if ((sum & 0xFF) != 0 && b[511] != 0) return false;   // checksum present but wrong
            for (int i = 0; i < 30; i++) if (b[2 + i * 12] != 0) return true;
            return false;
        }

        static readonly Dictionary<int, string> AtaNames = new Dictionary<int, string>
        {
            {0x01,L.T("Частота ошибок чтения")},{0x02,L.T("Производительность")},{0x03,L.T("Время раскрутки")},{0x04,L.T("Запуски/остановки шпинделя")},
            {0x05,L.T("Переназначенные сектора")},{0x07,L.T("Ошибки позиционирования")},{0x08,L.T("Скорость позиционирования")},{0x09,L.T("Часы работы")},
            {0x0A,L.T("Повторы раскрутки")},{0x0B,L.T("Повторы калибровки")},{0x0C,L.T("Циклы включения")},{0x0D,L.T("Программные ошибки чтения")},
            {0xAA,L.T("Резервные блоки")},{0xAB,L.T("Ошибки программирования")},{0xAC,L.T("Ошибки стирания")},{0xAD,L.T("Счётчик износа")},
            {0xAE,L.T("Неожиданные отключения питания")},{0xB1,L.T("Выравнивание износа")},{0xB3,L.T("Использовано резервных блоков")},
            {0xB5,L.T("Ошибки программирования")},{0xB6,L.T("Ошибки стирания")},{0xB7,L.T("Понижения скорости SATA")},{0xB8,L.T("Ошибки сквозной проверки")},
            {0xBB,L.T("Неисправимые ошибки")},{0xBC,L.T("Таймауты команд")},{0xBD,L.T("Запись на большой высоте головки")},{0xBE,L.T("Температура воздуха")},
            {0xBF,L.T("Ошибки от ударов")},{0xC0,L.T("Аварийные парковки")},{0xC1,L.T("Циклы парковки головок")},{0xC2,L.T("Температура")},
            {0xC3,L.T("Исправлено ECC")},{0xC4,L.T("События переназначения")},{0xC5,L.T("Нестабильные сектора")},{0xC6,L.T("Неисправимые сектора")},
            {0xC7,L.T("Ошибки CRC интерфейса")},{0xC8,L.T("Ошибки записи")},{0xC9,L.T("Программные ошибки чтения")},{0xDC,L.T("Смещение пластин")},
            {0xDD,L.T("Ошибки от ударов")},{0xDE,L.T("Время под нагрузкой")},{0xDF,L.T("Повторы загрузки")},{0xE0,L.T("Трение при загрузке")},
            {0xE2,L.T("Время загрузки головок")},{0xE7,L.T("Ресурс SSD / температура")},{0xE8,L.T("Оставшийся резерв")},{0xE9,L.T("Индикатор износа")},
            {0xF0,L.T("Время полёта головок")},{0xF1,L.T("Всего записано")},{0xF2,L.T("Всего прочитано")},{0xFE,L.T("Свободное падение")},
        };

        static SmartReport ParseAta(byte[] data, byte[] thr, DiskInfo d)
        {
            if (data == null || data.Length < 362) return null;
            var thresholds = new Dictionary<int, int>();
            if (thr != null && thr.Length >= 362)
                for (int i = 0; i < 30; i++)
                {
                    int o = 2 + i * 12;
                    if (thr[o] != 0) thresholds[thr[o]] = thr[o + 1];
                }

            bool hdd = d.Rpm > 1 || d.Kind == DiskKind.HDD;
            var rep = new SmartReport();
            for (int i = 0; i < 30; i++)
            {
                int o = 2 + i * 12;
                int id = data[o];
                if (id == 0) continue;
                int cur = data[o + 3], worst = data[o + 4];
                long raw = 0;
                for (int k = 5; k >= 0; k--) raw = (raw << 8) | data[o + 5 + k];
                int th; thresholds.TryGetValue(id, out th);

                string name;
                if (!AtaNames.TryGetValue(id, out name)) name = L.T("Атрибут производителя");
                var row = new SmartRow
                {
                    Id = id.ToString("X2"),
                    Name = name,
                    Normalized = cur + " / " + worst + (thresholds.ContainsKey(id) ? " / " + th : ""),
                    Value = N(raw),
                    AttrId = id, Current = cur, Threshold = th, Raw = raw,
                };

                long low16 = raw & 0xFFFF, low32 = raw & 0xFFFFFFFF;
                switch (id)
                {
                    case 0x05: case 0xC5: case 0xC6: case 0xC4: case 0xBB: case 0xB8: case 0x0A:
                        row.Value = N(low32);
                        row.Norm = "0";
                        if (low32 > 0)
                        {
                            row.State = (id == 0xC5 || id == 0xC6 || id == 0xBB || low32 >= 50) ? SmartState.Bad : SmartState.Warning;
                            row.Note = id == 0x05 || id == 0xC4 ? L.T("диск уже заменил повреждённые сектора")
                                     : id == 0xC5 ? L.T("есть сектора, которые не читаются стабильно")
                                     : id == 0xC6 ? L.T("есть сектора, которые не удалось исправить")
                                     : L.T("отклонение от нуля");
                        }
                        break;
                    case 0xC7:
                        row.Value = N(low32); row.Norm = "0";
                        if (low32 > 0) { row.State = SmartState.Warning; row.Note = L.T("ошибки передачи: проверьте кабель / USB-бокс"); }
                        break;
                    case 0xBC:
                        row.Value = N(low16); row.Norm = "0";
                        if (low16 > 0) { row.State = SmartState.Info; row.Note = L.T("были таймауты команд"); }
                        break;
                    case 0xC2: case 0xBE:
                        {
                            long t = raw & 0xFF;
                            long mn = (raw >> 16) & 0xFF, mx = (raw >> 32) & 0xFF;
                            row.Value = t + " °C" + (mx > t && mx < 120 ? L.F("  (мин {0}, макс {1})", mn, mx) : "");
                            int warn = hdd ? 50 : 70, bad = hdd ? 60 : 80;
                            row.Norm = "< " + warn + " °C";
                            if (t >= bad) { row.State = SmartState.Bad; row.Note = L.T("перегрев"); }
                            else if (t >= warn) { row.State = SmartState.Warning; row.Note = L.T("повышенная температура"); }
                            if (id == 0xC2 || rep.Temperature == null) rep.Temperature = t;
                        }
                        break;
                    case 0x09:
                        row.Value = L.F("{0} ч  (≈ {1} дн.)", N(low32), N(low32 / 24));
                        row.Norm = L.T("информационный"); row.State = SmartState.Info;
                        rep.PowerOnHours = low32;
                        break;
                    case 0x0C: case 0x04: case 0xC0: case 0xAE:
                        row.Value = N(low32); row.Norm = L.T("информационный"); row.State = SmartState.Info;
                        break;
                    case 0xC1:
                        row.Value = N(low32);
                        row.Norm = "< 300 000";
                        if (low32 > 600000) { row.State = SmartState.Warning; row.Note = L.T("превышен типичный ресурс парковок (600 000)"); }
                        else if (low32 > 300000) { row.State = SmartState.Info; row.Note = L.T("больше половины типичного ресурса"); }
                        break;
                    case 0xF1: case 0xF2:
                        row.Value = DiskService.FormatSize((ulong)low32 * 512UL) + "  (" + N(raw) + " LBA)";
                        row.Norm = L.T("информационный"); row.State = SmartState.Info;
                        break;
                    case 0xE7: case 0xE8: case 0xE9: case 0xAD: case 0xB1: case 0xAA:
                        row.Value = N(raw);
                        row.Norm = L.T("текущее > порога");
                        if (id == 0xE7 || id == 0xE9 || id == 0xB1) rep.Wear = Math.Max(0, 100 - cur);
                        break;
                    case 0x03:
                        row.Value = L.F("{0} мс", N(low16)); row.Norm = L.T("текущее > порога");
                        break;
                    default:
                        row.Norm = th > 0 ? L.T("текущее > порога") : L.T("информационный");
                        if (th == 0) row.State = SmartState.Info;
                        break;
                }

                // generic rule: normalized value fell to / below the vendor threshold
                if (th > 0 && th < 254 && cur <= th && cur > 0)
                {
                    row.State = SmartState.Bad;
                    row.Note = L.F("значение достигло порога производителя ({0} ≤ {1})", cur, th);
                }
                else if (th > 0 && th < 254 && worst <= th && worst > 0 && row.State == SmartState.Ok)
                {
                    row.State = SmartState.Warning;
                    row.Note = L.T("в прошлом значение опускалось до порога");
                }
                rep.Rows.Add(row);
            }
            return rep.Rows.Count > 0 ? rep : null;
        }

        // ---------------------------------------------------------------- NVMe SMART / Health log (log page 02h)
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr ovl);
        const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;

        static byte[] NvmeLog(int disk)
        {
            foreach (uint access in new[] { Native.GENERIC_READ | Native.GENERIC_WRITE, 0u })
            {
                using (var h = Native.CreateFile(@"\\.\PhysicalDrive" + disk, access, 3, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero))
                {
                    if (h.IsInvalid) continue;
                    const int specOff = 8, specLen = 40, dataLen = 512;
                    int total = specOff + specLen + dataLen;
                    var buf = new byte[total];
                    BitConverter.GetBytes(50).CopyTo(buf, 0);          // StorageDeviceProtocolSpecificProperty
                    BitConverter.GetBytes(0).CopyTo(buf, 4);           // PropertyStandardQuery
                    BitConverter.GetBytes(3).CopyTo(buf, 8);           // ProtocolTypeNvme
                    BitConverter.GetBytes(2).CopyTo(buf, 12);          // NVMeDataTypeLogPage
                    BitConverter.GetBytes(2).CopyTo(buf, 16);          // log page 02h: SMART / Health
                    BitConverter.GetBytes(0).CopyTo(buf, 20);
                    BitConverter.GetBytes(specLen).CopyTo(buf, 24);    // ProtocolDataOffset
                    BitConverter.GetBytes(dataLen).CopyTo(buf, 28);    // ProtocolDataLength
                    var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
                    try
                    {
                        int ret;
                        IntPtr p = pin.AddrOfPinnedObject();
                        if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, p, total, p, total, out ret, IntPtr.Zero)) continue;
                    }
                    finally { pin.Free(); }
                    // output: STORAGE_PROTOCOL_DATA_DESCRIPTOR { Version, Size, ProtocolSpecificData } ; data at 8 + ProtocolDataOffset
                    int off = BitConverter.ToInt32(buf, 8 + 16);
                    int len = BitConverter.ToInt32(buf, 8 + 20);
                    if (off <= 0 || len < 512 || 8 + off + 512 > total) continue;
                    var log = new byte[512];
                    Array.Copy(buf, 8 + off, log, 0, 512);
                    return log;
                }
            }
            return null;
        }

        static long U64(byte[] b, int o) { return (long)Math.Min((ulong)long.MaxValue, BitConverter.ToUInt64(b, o)); }

        static SmartReport ReadNvme(int disk)
        {
            var b = NvmeLog(disk);
            if (b == null) return null;
            var rep = new SmartReport { Source = L.T("NVMe SMART / Health (журнал 02h)"), Nvme = true, Spare = b[3], SpareThreshold = b[4], MediaErrors = U64(b, 160) };
            Func<string, string, string, SmartState, string, SmartRow> add = (name, val, norm, st, note) =>
            {
                var r = new SmartRow { Name = name, Value = val, Norm = norm, State = st, Note = note ?? "" };
                rep.Rows.Add(r);
                return r;
            };

            int cw = b[0];
            var flags = new List<string>();
            if ((cw & 1) != 0) flags.Add(L.T("резерв ниже порога"));
            if ((cw & 2) != 0) flags.Add(L.T("температура вне допуска"));
            if ((cw & 4) != 0) flags.Add(L.T("надёжность снижена"));
            if ((cw & 8) != 0) flags.Add(L.T("только чтение"));
            if ((cw & 16) != 0) flags.Add(L.T("сбой резервной памяти"));
            add(L.T("Критические предупреждения"), cw == 0 ? L.T("нет") : "0x" + cw.ToString("X2"), L.T("нет"),
                cw == 0 ? SmartState.Ok : SmartState.Bad, string.Join(", ", flags));

            int tK = b[1] | (b[2] << 8);
            if (tK > 0)
            {
                long t = tK - 273;
                rep.Temperature = t;
                add(L.T("Температура"), t + " °C", "< 70 °C", t >= 80 ? SmartState.Bad : t >= 70 ? SmartState.Warning : SmartState.Ok,
                    t >= 80 ? L.T("перегрев") : t >= 70 ? L.T("повышенная температура") : null);
            }
            for (int i = 0; i < 8; i++)
            {
                int k = b[200 + i * 2] | (b[201 + i * 2] << 8);
                if (k <= 0) continue;
                long t = k - 273;
                add(L.F("Датчик температуры {0}", i + 1), t + " °C", "< 80 °C", t >= 85 ? SmartState.Bad : t >= 80 ? SmartState.Warning : SmartState.Ok, null);
            }

            int spare = b[3], spareThr = b[4], used = b[5];
            add(L.T("Доступный резерв"), spare + " %", "≥ " + Math.Max(spareThr, 10) + " %",
                spare < spareThr ? SmartState.Bad : spare < spareThr + 10 ? SmartState.Warning : SmartState.Ok,
                spare < spareThr ? L.F("резерв ниже порога производителя ({0} %)", spareThr) : null);
            rep.Wear = used;
            add(L.T("Использовано ресурса"), used + " %", "< 80 %", used >= 100 ? SmartState.Bad : used >= 80 ? SmartState.Warning : SmartState.Ok,
                used >= 100 ? L.T("заявленный ресурс записи исчерпан") : used >= 80 ? L.T("ресурс подходит к концу") : null);

            long dr = U64(b, 32), dw = U64(b, 48);
            add(L.T("Прочитано данных"), DiskService.FormatSize((ulong)dr * 512000UL), L.T("информационный"), SmartState.Info, null);
            add(L.T("Записано данных"), DiskService.FormatSize((ulong)dw * 512000UL), L.T("информационный"), SmartState.Info, null);
            add(L.T("Команд чтения"), N(U64(b, 64)), L.T("информационный"), SmartState.Info, null);
            add(L.T("Команд записи"), N(U64(b, 80)), L.T("информационный"), SmartState.Info, null);
            add(L.T("Время занятости контроллера"), L.F("{0} мин", N(U64(b, 96))), L.T("информационный"), SmartState.Info, null);
            add(L.T("Циклы включения"), N(U64(b, 112)), L.T("информационный"), SmartState.Info, null);
            long poh = U64(b, 128);
            rep.PowerOnHours = poh;
            add(L.T("Часы работы"), L.F("{0} ч  (≈ {1} дн.)", N(poh), N(poh / 24)), L.T("информационный"), SmartState.Info, null);
            long unsafeOff = U64(b, 144);
            add(L.T("Небезопасные отключения"), N(unsafeOff), L.T("чем меньше, тем лучше"), SmartState.Info,
                unsafeOff > 0 ? L.T("питание пропадало без корректного завершения работы") : null);
            long media = U64(b, 160);
            add(L.T("Ошибки целостности данных"), N(media), "0", media > 0 ? SmartState.Bad : SmartState.Ok,
                media > 0 ? L.T("контроллер обнаружил неисправимые ошибки") : null);
            add(L.T("Записи в журнале ошибок"), N(U64(b, 176)), L.T("информационный"), SmartState.Info, null);
            long warnT = BitConverter.ToUInt32(b, 192), critT = BitConverter.ToUInt32(b, 196);
            // short accumulated periods are normal for laptops under load; flag only sustained overheating
            add(L.T("Время в режиме предупреждения t°"), L.F("{0} мин", N(warnT)), L.T("< 24 ч суммарно"),
                warnT > 24 * 60 ? SmartState.Warning : warnT > 0 ? SmartState.Info : SmartState.Ok,
                warnT > 24 * 60 ? L.T("диск долго работал при повышенной температуре — проверьте охлаждение")
                : warnT > 0 ? L.T("кратковременный нагрев под нагрузкой, это нормально") : null);
            add(L.T("Время при критической t°"), L.F("{0} мин", N(critT)), "0",
                critT > 60 ? SmartState.Bad : critT > 0 ? SmartState.Warning : SmartState.Ok,
                critT > 0 ? L.T("диск работал при критической температуре") : null);
            return rep;
        }

        // ---------------------------------------------------------------- live temperature (polled by the chart)
        static long? AtaTemp(byte[] data)
        {
            if (data == null || data.Length < 362) return null;
            long? alt = null;
            for (int i = 0; i < 30; i++)
            {
                int o = 2 + i * 12;
                int id = data[o];
                long t = data[o + 5];
                if (t <= 0 || t > 125) continue;
                if (id == 0xC2) return t;
                if (id == 0xBE) alt = t;
            }
            return alt;
        }

        /// <summary>Reads the current temperature of one disk using the cheapest source that worked for it.</summary>
        public static long? ReadTemp(DiskInfo d)
        {
            try
            {
                if (d.BusType == 17)
                {
                    var b = NvmeLog(d.Number);
                    if (b != null) { int k = b[1] | (b[2] << 8); if (k > 0) return k - 273; }
                }
                var sm = d.Smart;
                if (sm != null && !sm.Nvme && sm.Source.Contains("Windows") && sm.Rows.Any(r => r.AttrId >= 0))
                {
                    byte[][] a;
                    if (LoadWmiAta().TryGetValue(d.Number, out a)) { var t = AtaTemp(a[0]); if (t.HasValue) return t; }
                }
                if (sm != null && !sm.Nvme && sm.Rows.Any(r => r.AttrId >= 0))
                {
                    using (var h = Native.CreateFile(@"\\.\PhysicalDrive" + d.Number, Native.GENERIC_READ | Native.GENERIC_WRITE, 3, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero))
                    {
                        if (!h.IsInvalid)
                        {
                            var data = Rpm.Scsi(h, new byte[] { 0xA1, 0x08, 0x0E, 0xD0, 1, 0, 0x4F, 0xC2, 0, 0xB0, 0, 0 }, 512);
                            if (!Valid(data)) data = Rpm.Scsi(h, new byte[] { 0x85, 0x08, 0x0E, 0, 0xD0, 0, 1, 0, 0, 0, 0x4F, 0, 0xC2, 0, 0xB0, 0 }, 512);
                            if (Valid(data)) { var t = AtaTemp(data); if (t.HasValue) return t; }
                        }
                    }
                }
                // Windows reliability counters
                var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                scope.Connect();
                using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk WHERE DeviceId = '" + d.Number + "'")))
                    foreach (ManagementObject pd in s.Get())
                        foreach (ManagementObject r in pd.GetRelated("MSFT_StorageReliabilityCounter"))
                        {
                            var v = r["Temperature"];
                            if (v != null) { long t = Convert.ToInt64(v); if (t > 0) return t; }
                        }
            }
            catch { }
            return null;
        }

        // ---------------------------------------------------------------- fallback: Windows reliability counters
        public static SmartReport FromReliability(DiskInfo d)
        {
            var r = d.Rel;
            if (r == null) return null;
            var rep = new SmartReport { Source = L.T("Счётчики надёжности Windows") };
            Action<string, long?, string, string, Func<long, SmartState>, string> add = (name, v, unit, norm, rule, note) =>
            {
                if (!v.HasValue) return;
                var st = rule != null ? rule(v.Value) : SmartState.Info;
                rep.Rows.Add(new SmartRow { Name = name, Value = N(v.Value) + unit, Norm = norm, State = st, Note = st >= SmartState.Warning ? note : "" });
            };
            bool hdd = d.IsRotating;
            if ((r.Temperature ?? 0) > 0)
                add(L.T("Температура"), r.Temperature, " °C", hdd ? "< 50 °C" : "< 70 °C",
                    t => t >= (hdd ? 60 : 80) ? SmartState.Bad : t >= (hdd ? 50 : 70) ? SmartState.Warning : SmartState.Ok, L.T("повышенная температура"));
            if ((r.TemperatureMax ?? 0) > 0) add(L.T("Максимальная температура"), r.TemperatureMax, " °C", L.T("информационный"), null, null);
            if (r.Wear.HasValue && !hdd)
                add(L.T("Износ"), r.Wear, " %", "< 80 %", w => w >= 100 ? SmartState.Bad : w >= 80 ? SmartState.Warning : SmartState.Ok, L.T("ресурс подходит к концу"));
            add(L.T("Часы работы"), r.PowerOnHours, L.T(" ч"), L.T("информационный"), null, null);
            add(L.T("Циклы включения"), r.StartStopCycles, "", L.T("информационный"), null, null);
            add(L.T("Циклы парковки головок"), r.LoadUnloadCycles, "", "< 300 000", v => v > 600000 ? SmartState.Warning : SmartState.Ok, L.T("превышен типичный ресурс"));
            add(L.T("Ошибки чтения (всего)"), r.ReadErrors, "", L.T("информационный"), null, null);
            add(L.T("Неисправленные ошибки чтения"), r.ReadErrorsUncorrected, "", "0", v => v > 0 ? SmartState.Bad : SmartState.Ok, L.T("есть неисправимые ошибки чтения"));
            add(L.T("Ошибки записи (всего)"), r.WriteErrors, "", L.T("информационный"), null, null);
            add(L.T("Неисправленные ошибки записи"), r.WriteErrorsUncorrected, "", "0", v => v > 0 ? SmartState.Bad : SmartState.Ok, L.T("есть неисправимые ошибки записи"));
            add(L.T("Макс. задержка чтения"), r.ReadLatencyMax, L.T(" мс"), L.T("информационный"), null, null);
            add(L.T("Макс. задержка записи"), r.WriteLatencyMax, L.T(" мс"), L.T("информационный"), null, null);
            return rep.Rows.Count > 0 ? rep : null;
        }
    }
}
