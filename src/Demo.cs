using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FoxideDisk
{
    /// <summary>
    /// Screenshot demo: invented hardware for marketing screenshots, so they do not depend on (or leak) the PC they are made on.
    /// Enabled only by "--shot … --demo" (Program.Main); every hook elsewhere is "if (Demo.On)". Nothing here touches real disks.
    ///   FoxideHelper.exe --shot out.png <section> <tab> <theme 1|2> [disk#] --demo   → 2560×1440 PNG at 1.5x UI scale
    /// </summary>
    public static class Demo
    {
        public static bool On;
        public static bool OpenSensors;
        public static string Lang;               // --lang=ru|en for screenshots          // --open-sensors: "All sensors" cards expanded for the shot
        public static int ScrollY;               // --scroll=N: scroll the page before the shot
        public const string GpuName = "NVIDIA GeForce RTX 4070";
        const ulong GB = 1000UL * 1000 * 1000, GiB = 1UL << 30, MiB = 1UL << 20;

        public static void PrepareSettings()
        {
            L.Lang = Lang == "ru" ? "ru" : "en";
            var en = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            System.Threading.Thread.CurrentThread.CurrentCulture = en;
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = en;
            Settings.FilePath = null;               // never write the user's ini
            Settings.HomeSensors = Environment.GetEnvironmentVariable("FH_HOME_SENSORS") ?? "";
            Settings.LiveTemp = true; Settings.TempWindowMin = 10; Settings.TempIntervalSec = 2;
            Settings.SidebarCollapsed = false; Settings.ShowVirtual = false; Settings.CollapsedCards.Clear();
            Settings.CheckUpdates = true; Settings.SendStats = true; Settings.TempAlerts = true; Settings.MinimizeToTray = false;
            Settings.TestSizeMB = 1024; Settings.RandomSeconds = 5; Settings.DefaultFs = "NTFS"; Settings.DefaultStyle = "GPT";
        }

        // ---------------------------------------------------------------- disks
        public static List<DiskInfo> Disks()
        {
            var list = new List<DiskInfo>();

            var d0 = new DiskInfo
            {
                Number = 0, Model = "Samsung SSD 990 PRO 2TB", Serial = "S73WNJ0W412873K", Firmware = "4B2QJXD7", UniqueId = "demo-0",
                Size = 2000398934016UL, BusType = 17, MediaType = 4, PartitionStyle = 2, HealthStatus = 0, LogicalSector = 512, PhysicalSector = 512,
                IsSystem = true, IsBoot = true, Kind = DiskKind.SSD, Rpm = 1, PredictFailure = false,
                Rel = new Reliability { Temperature = 41, TemperatureMax = 63, Wear = 2, PowerOnHours = 3912, ReadErrors = 0, WriteErrors = 0, ReadLatencyMax = 41, WriteLatencyMax = 78 },
            };
            ulong off = MiB;
            off = Part(d0, 1, off, 100 * MiB, L.T("Системный EFI"), true, '\0', "FAT32", "", 0);
            off = Part(d0, 2, off, 16 * MiB, L.T("Зарезервировано (MSR)"), true, '\0', "", "", 0);
            ulong c = d0.Size - off - 750 * MiB - MiB;
            off = Part(d0, 3, off, c, L.T("Основной раздел"), false, 'C', "NTFS", "Windows", (ulong)(c * 0.58));
            Part(d0, 4, off, 750 * MiB, L.T("Восстановление"), true, '\0', "NTFS", "", 0);
            d0.Partitions[0].IsSystem = true; d0.Partitions[2].IsBoot = true;
            d0.Smart = SmartReader.ParseNvme(NvmeLog(41, 100, 2, 75812301, 43190544, 1284, 3912, 37, 46));
            list.Add(d0);

            var d1 = new DiskInfo
            {
                Number = 1, Model = "WD_BLACK SN850X 1000GB", Serial = "23094R801562", Firmware = "620311WD", UniqueId = "demo-1",
                Size = 1000204886016UL, BusType = 17, MediaType = 4, PartitionStyle = 2, HealthStatus = 0, LogicalSector = 512, PhysicalSector = 512,
                Kind = DiskKind.SSD, Rpm = 1, PredictFailure = false,
                Rel = new Reliability { Temperature = 44, TemperatureMax = 68, Wear = 5, PowerOnHours = 2271, ReadErrors = 0, WriteErrors = 0, ReadLatencyMax = 52, WriteLatencyMax = 96 },
            };
            off = MiB;
            off = Part(d1, 1, off, 16 * MiB, L.T("Зарезервировано (MSR)"), true, '\0', "", "", 0);
            ulong g = d1.Size - off - MiB;
            Part(d1, 2, off, g, L.T("Основной раздел"), false, 'D', "NTFS", "Games", (ulong)(g * 0.34));
            d1.Smart = SmartReader.ParseNvme(NvmeLog(44, 100, 5, 92114008, 60233712, 412, 2271, 9, 51));
            list.Add(d1);

            var d2 = new DiskInfo
            {
                Number = 2, Model = "ST8000VN004-3CP101", Serial = "WWZ4K7QX", Firmware = "SC60", UniqueId = "demo-2",
                Size = 8001563222016UL, BusType = 11, MediaType = 3, PartitionStyle = 2, HealthStatus = 0, LogicalSector = 512, PhysicalSector = 4096,
                Kind = DiskKind.HDD, Rpm = 7200, RpmSource = "Windows", SpindleSpeed = 7200, PredictFailure = false,
                Rel = new Reliability { Temperature = 38, TemperatureMax = 47, PowerOnHours = 7214, StartStopCycles = 412, LoadUnloadCycles = 2104, ReadErrors = 0, WriteErrors = 0, ReadLatencyMax = 1210, WriteLatencyMax = 640 },
            };
            off = MiB;
            off = Part(d2, 1, off, 16 * MiB, L.T("Зарезервировано (MSR)"), true, '\0', "", "", 0);
            ulong st = d2.Size - off - MiB;
            Part(d2, 2, off, st, L.T("Основной раздел"), false, 'E', "NTFS", "Storage", (ulong)(st * 0.39));
            d2.Smart = SmartReader.ParseAta(AtaData(), AtaThresholds(), d2);
            if (d2.Smart != null) d2.Smart.Source = L.T("SMART (ATA), через Windows");
            list.Add(d2);

            var d3 = new DiskInfo
            {
                Number = 3, Model = "Kingston DataTraveler Max", Serial = "E0D55EA5741BF4C1A9C3", Firmware = "PMAP", UniqueId = "demo-3",
                Size = 61872793600UL, BusType = 7, MediaType = 0, PartitionStyle = 1, HealthStatus = 0, LogicalSector = 512, PhysicalSector = 512,
                Kind = DiskKind.Removable, Rpm = 1,
            };
            Part(d3, 1, MiB, d3.Size - MiB, L.T("Основной раздел"), false, 'F', "exFAT", "KINGSTON", (ulong)((d3.Size - MiB) * 0.71));
            list.Add(d3);

            foreach (var d in list) DiskService.Evaluate(d);
            return list;
        }

        static ulong Part(DiskInfo d, int n, ulong off, ulong size, string type, bool service, char letter, string fs, string label, ulong free)
        {
            d.Partitions.Add(new PartInfo
            {
                Number = n, Offset = off, Size = size, Type = type, IsService = service, Letter = letter, FileSystem = fs, Label = label,
                Free = free, HasFree = fs.Length > 0 && (letter != '\0' || !service), VolumePath = @"\\?\Volume{00000000-0000-0000-0000-00000000000" + n + @"}\",
            });
            return off + size;
        }

        /// <summary>NVMe SMART / Health log page 02h.</summary>
        static byte[] NvmeLog(int tempC, int spare, int used, long unitsRead, long unitsWritten, long powerCycles, long hours, long unsafeOff, int sensor2)
        {
            var b = new byte[512];
            int k = tempC + 273; b[1] = (byte)k; b[2] = (byte)(k >> 8);
            b[3] = (byte)spare; b[4] = 10; b[5] = (byte)used;
            U64(b, 32, unitsRead); U64(b, 48, unitsWritten);
            U64(b, 64, unitsRead * 9); U64(b, 80, unitsWritten * 6);
            U64(b, 96, hours * 4); U64(b, 112, powerCycles); U64(b, 128, hours); U64(b, 144, unsafeOff);
            U64(b, 176, 0);
            BitConverter.GetBytes((uint)3).CopyTo(b, 192);
            int s1 = tempC + 273, s2 = sensor2 + 273;
            b[200] = (byte)s1; b[201] = (byte)(s1 >> 8); b[202] = (byte)s2; b[203] = (byte)(s2 >> 8);
            return b;
        }

        static void U64(byte[] b, int o, long v) { BitConverter.GetBytes(v).CopyTo(b, o); }

        // id, current, worst, threshold, raw
        static readonly long[][] Ata =
        {
            new long[] { 0x01, 82, 64, 44, 162318452 }, new long[] { 0x03, 91, 90, 0, 0 }, new long[] { 0x04, 100, 100, 20, 412 },
            new long[] { 0x05, 100, 100, 10, 0 }, new long[] { 0x07, 87, 60, 45, 498215337 }, new long[] { 0x09, 92, 92, 0, 7214 },
            new long[] { 0x0A, 100, 100, 97, 0 }, new long[] { 0x0C, 100, 100, 20, 398 }, new long[] { 0xBB, 100, 100, 0, 0 },
            new long[] { 0xBC, 100, 100, 0, 0 }, new long[] { 0xBE, 62, 53, 40, 38 }, new long[] { 0xC0, 100, 100, 0, 61 },
            new long[] { 0xC1, 99, 99, 0, 2104 }, new long[] { 0xC2, 38, 47, 0, 38 }, new long[] { 0xC5, 100, 100, 0, 0 },
            new long[] { 0xC6, 100, 100, 0, 0 }, new long[] { 0xC7, 200, 200, 0, 0 },
        };

        static byte[] AtaData()
        {
            var b = new byte[512];
            for (int i = 0; i < Ata.Length; i++)
            {
                int o = 2 + i * 12;
                b[o] = (byte)Ata[i][0]; b[o + 1] = 0x0F; b[o + 3] = (byte)Ata[i][1]; b[o + 4] = (byte)Ata[i][2];
                long raw = Ata[i][4];
                for (int k = 0; k < 6; k++) b[o + 5 + k] = (byte)(raw >> (8 * k));
            }
            return b;
        }

        static byte[] AtaThresholds()
        {
            var b = new byte[512];
            for (int i = 0; i < Ata.Length; i++) { int o = 2 + i * 12; b[o] = (byte)Ata[i][0]; b[o + 1] = (byte)Ata[i][3]; }
            return b;
        }

        /// <summary>Live-temperature history (last 10 min) and a previous speed-test result.</summary>
        public static void Seed(List<DiskInfo> disks, Dictionary<string, List<TempPoint>> temps, Dictionary<string, SpeedResult> speed)
        {
            var now = DateTime.Now;
            for (int i = 0; i < disks.Count; i++)
            {
                var d = disks[i];
                if (!d.TempC.HasValue) continue;
                var l = new List<TempPoint>();
                var rnd = new Random(7 + i);
                double baseT = d.TempC.Value;
                for (int s = 600; s >= 0; s -= 2)
                {
                    double t = 600 - s;
                    // a short load spike in the middle, otherwise a gentle drift around the current value
                    double v = baseT - 1 + 1.4 * Math.Sin(t / 110.0) + 0.6 * Math.Sin(t / 37.0 + i) + 6.5 * Math.Exp(-Math.Pow((t - 330) / 45.0, 2));
                    if (s == 0) v = baseT;
                    l.Add(new TempPoint { T = now.AddSeconds(-s), V = Math.Round(v * 2) / 2 });
                }
                temps[d.Signature] = l;
            }
            Speed(speed, disks[0], 7012, 6431, 92.4, 238.7, "C:\\");
            Speed(speed, disks[1], 7105, 6188, 88.1, 221.5, "D:\\");
            Speed(speed, disks[2], 268, 261, 0.82, 2.41, "E:\\");
            Speed(speed, disks[3], 412, 286, 18.6, 9.7, "F:\\");
        }

        static void Speed(Dictionary<string, SpeedResult> speed, DiskInfo d, double sr, double sw, double rr, double rw, string vol)
        {
            speed[d.Signature] = new SpeedResult
            {
                SeqRead = sr, SeqWrite = sw, RndRead = rr, RndWrite = rw, RndReadIops = rr * 1e6 / 4096, RndWriteIops = rw * 1e6 / 4096,
                Volume = vol, SizeMB = 1024, When = DateTime.Now.AddMinutes(-3),
            };
        }

        // ---------------------------------------------------------------- memory
        public static RamInfo Ram()
        {
            var r = new RamInfo { Slots = 4, MaxCapacity = 192 * GiB };
            string[] slots = { "DIMM A1", "DIMM A2", "DIMM B1", "DIMM B2" };
            string[] serials = { "4A1E09C3", "4A1E09D7", "4A1E0A02", "4A1E0A15" };
            for (int i = 0; i < 4; i++)
                r.Modules.Add(new RamModule
                {
                    Slot = slots[i], Bank = "BANK " + (i / 2), Manufacturer = "Kingston", PartNumber = "KF560C36-16", Serial = serials[i],
                    Type = "DDR5", FormFactor = "DIMM", Capacity = 16 * GiB, Speed = 6000, ConfiguredSpeed = 6000, Voltage = 1350,
                });
            return r;
        }

        public static RamStatus MemStatus()
        {
            ulong total = 64 * GiB - 412 * MiB;
            return new RamStatus { Total = total, Available = (ulong)(total * 0.63), Load = 37, CommitTotal = 29 * GiB, CommitLimit = 72 * GiB };
        }

        public static MemDiagResult MemDiag()
        {
            return new MemDiagResult
            {
                When = DateTime.Now.AddDays(-12).Date.AddHours(21).AddMinutes(14), Errors = false,
                Message = "The Windows Memory Diagnostic tested the computer's memory and detected no errors.",
            };
        }

        // ---------------------------------------------------------------- stress test
        public static CpuSensors Cpu()
        {
            var s = new CpuSensors();
            s.Info.Name = "AMD Ryzen 7 7800X3D 8-Core Processor"; s.Info.Cores = 8; s.Info.Threads = 16; s.Info.MaxMHz = 4200;
            return s;
        }

        /// <summary>A warming-up curve ending now: one point per second.</summary>
        public static List<TempPoint> Curve(int seconds, double from, double to, double noise, int seed)
        {
            var rnd = new Random(seed); var now = DateTime.Now; var l = new List<TempPoint>();
            for (int s = seconds; s >= 0; s--)
            {
                double t = seconds - s;
                double v = to - (to - from) * Math.Exp(-t / 45.0) + Math.Sin(t / 9.0) * noise * 0.6 + (rnd.NextDouble() - 0.5) * noise;
                l.Add(new TempPoint { T = now.AddSeconds(-s), V = Math.Round(v, 1) });
            }
            return l;
        }

        static Bitmap gpuFrame;
        /// <summary>Stands in for the OpenGL picture, which DrawToBitmap cannot capture: a Mandelbrot frame.</summary>
        public static void PaintGpu(Graphics g, Rectangle r)
        {
            int w = Math.Max(1, r.Width / 2), h = Math.Max(1, r.Height / 2);
            if (gpuFrame == null || gpuFrame.Width != w || gpuFrame.Height != h)
            {
                if (gpuFrame != null) gpuFrame.Dispose();
                gpuFrame = new Bitmap(w, h, PixelFormat.Format32bppRgb);
                var data = gpuFrame.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                var px = new int[w * h];
                double cx = -0.7435669, cy = 0.1314023, scale = 0.0032 / w;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        double x0 = cx + (x - w / 2.0) * scale, y0 = cy + (y - h / 2.0) * scale, zx = 0, zy = 0;
                        int it = 0;
                        while (it < 300 && zx * zx + zy * zy < 16) { double t = zx * zx - zy * zy + x0; zy = 2 * zx * zy + y0; zx = t; it++; }
                        double f = it >= 300 ? 0 : (it + 1 - Math.Log(Math.Log(Math.Sqrt(zx * zx + zy * zy))) / Math.Log(2)) / 60.0;
                        double q = 0.5 + 0.5 * Math.Sin(6.2832 * f * 0.7 - 1.2);   // navy -> orange -> warm white, brand colours
                        int R = (int)(14 + q * 230 + (q > 0.8 ? (q - 0.8) * 50 : 0)), G = (int)(18 + q * q * 150 + (q > 0.8 ? (q - 0.8) * 300 : 0)), B = (int)(40 + (1 - q) * 70 + (q > 0.85 ? (q - 0.85) * 600 : 0));
                        if (it >= 300) { R = 10; G = 8; B = 18; }
                        px[y * w + x] = (Clamp(R) << 16) | (Clamp(G) << 8) | Clamp(B);
                    }
                Marshal.Copy(px, 0, data.Scan0, px.Length);
                gpuFrame.UnlockBits(data);
            }
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(gpuFrame, r);
        }

        static int Clamp(int v) { return v < 0 ? 0 : v > 255 ? 255 : v; }

        // ---------------------------------------------------------------- window larger than the screen
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        public const int Width = 2560, Height = 1440;

        /// <summary>Client area exactly Width × Height (the frame is cropped away in Save).</summary>
        public static void Attach(Form f)
        {
            f.Shown += (s, e) =>
            {
                int bw = f.Width - f.ClientSize.Width, bh = f.Height - f.ClientSize.Height;
                SetWindowPos(f.Handle, IntPtr.Zero, 0, 0, Width + bw, Height + bh, 0x0002 | 0x0004 | 0x0010);   // NOMOVE | NOZORDER | NOACTIVATE
            };
        }

        public static void Save(Form f, string path)
        {
            using (var full = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(full, new Rectangle(0, 0, f.Width, f.Height));
                var o = f.PointToScreen(Point.Empty);
                var crop = new Rectangle(o.X - f.Left, o.Y - f.Top, f.ClientSize.Width, f.ClientSize.Height);
                using (var bmp = full.Clone(crop, full.PixelFormat)) bmp.Save(path, ImageFormat.Png);
            }
        }

        /// <summary>WM_GETMINMAXINFO: let the window grow past the screen size.</summary>
        public static void FixMinMax(ref Message m)
        {
            if (m.Msg != 0x0024 || m.LParam == IntPtr.Zero) return;
            Marshal.WriteInt32(m.LParam, 8, 8000); Marshal.WriteInt32(m.LParam, 12, 8000);     // ptMaxSize
            Marshal.WriteInt32(m.LParam, 32, 8000); Marshal.WriteInt32(m.LParam, 36, 8000);    // ptMaxTrackSize
        }
    }
}
