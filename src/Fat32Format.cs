using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace FoxideDisk
{
    /// <summary>Where the formatter writes: a real volume or (for tests) an image file.</summary>
    public interface ISectorTarget
    {
        void Write(long offset, byte[] buffer, int count);
    }

    public class Fat32Layout
    {
        public int BytesPerSector, SectorsPerCluster, Reserved, NumFats = 2;
        public long TotalSectors, HiddenSectors;
        public uint FatSize, ClusterCount;
        public long ClusterBytes { get { return (long)BytesPerSector * SectorsPerCluster; } }
        public long DataStartSector { get { return Reserved + (long)NumFats * FatSize; } }
    }

    /// <summary>
    /// FAT32 formatter without Windows' 32 GB limit (the limit is in format.com, not in FAT32 itself).
    /// Up to 2 TB with 512-byte sectors, 16 TB with 4K sectors. Always a "quick" format.
    /// </summary>
    public static class Fat32Format
    {
        public const long WindowsLimit = 32L * 1024 * 1024 * 1024;

        public static long MaxBytes(int bytesPerSector) { return (long)uint.MaxValue * bytesPerSector; }

        public static Fat32Layout Plan(long bytes, int bps, long hiddenSectors)
        {
            const long GB = 1024L * 1024 * 1024;
            long total = bytes / bps;
            if (total > uint.MaxValue) throw new InvalidOperationException(FoxideDisk.L.F("FAT32 поддерживает тома до {0} при секторе {1} байт.", DiskService.FormatSize((ulong)MaxBytes(bps)), bps));
            int clusterBytes = bytes <= 8 * GB ? 4096 : bytes <= 16 * GB ? 8192 : bytes <= 32 * GB ? 16384 : 32768;
            int spc = Math.Max(1, clusterBytes / bps);

            var L = new Fat32Layout { BytesPerSector = bps, SectorsPerCluster = spc, Reserved = 32, TotalSectors = total, HiddenSectors = hiddenSectors };
            for (int attempt = 0; attempt < 4; attempt++)
            {
                // FAT size in sectors (fat32format / Microsoft formula, rounded up)
                ulong num = 4UL * (ulong)(total - L.Reserved);
                ulong den = (ulong)spc * (ulong)bps + 4UL * (ulong)L.NumFats;
                L.FatSize = (uint)(num / den + 1);
                // align the data area to 1 MiB so clusters line up with flash pages / 4K sectors
                long align = Math.Max(1, (1024 * 1024) / bps);
                long sys = L.Reserved + (long)L.NumFats * L.FatSize;
                long aligned = (sys + align - 1) / align * align;
                L.Reserved += (int)(aligned - sys);
                long clusters = (total - L.DataStartSector) / spc;
                if (clusters < 65525)
                    throw new InvalidOperationException(FoxideDisk.L.T("Том слишком мал для FAT32 с таким размером кластера."));
                if (clusters >= 0x0FFFFFF5)
                {
                    spc *= 2; L.SectorsPerCluster = spc; L.Reserved = 32;
                    if ((long)spc * bps > 65536) throw new InvalidOperationException(FoxideDisk.L.T("Том слишком большой для FAT32."));
                    continue;
                }
                L.ClusterCount = (uint)clusters;
                return L;
            }
            throw new InvalidOperationException(FoxideDisk.L.T("Не удалось подобрать параметры FAT32."));
        }

        static string FatLabel(string label)
        {
            var sb = new StringBuilder();
            foreach (char c0 in (label ?? "").ToUpperInvariant())
            {
                char c = c0;
                if (c < 32 || c > 126 || "\"*+,./:;<=>?[\\]|".IndexOf(c) >= 0) continue;
                sb.Append(c);
                if (sb.Length == 11) break;
            }
            return sb.ToString();
        }

        static void Put16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        static void Put32(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }

        public static void Write(ISectorTarget t, Fat32Layout L, string label, Action<string> log)
        {
            int bps = L.BytesPerSector;
            string lbl = FatLabel(label);
            uint volId = (uint)(DateTime.Now.Ticks ^ (DateTime.Now.Ticks >> 32));

            // 1) zero reserved area, both FATs and the root directory cluster
            long sysBytes = (L.DataStartSector + L.SectorsPerCluster) * bps;
            log(FoxideDisk.L.F("Очистка служебной области: {0}", DiskService.FormatSize((ulong)sysBytes)));
            var zero = new byte[1024 * 1024];
            for (long off = 0; off < sysBytes; off += zero.Length)
                t.Write(off, zero, (int)Math.Min(zero.Length, sysBytes - off));

            // 2) boot sector
            var boot = new byte[bps];
            boot[0] = 0xEB; boot[1] = 0x58; boot[2] = 0x90;
            Encoding.ASCII.GetBytes("MSWIN4.1").CopyTo(boot, 3);
            Put16(boot, 11, bps);
            boot[13] = (byte)L.SectorsPerCluster;
            Put16(boot, 14, L.Reserved);
            boot[16] = (byte)L.NumFats;
            boot[21] = 0xF8;
            Put16(boot, 24, 63);
            Put16(boot, 26, 255);
            Put32(boot, 28, (uint)Math.Min(uint.MaxValue, L.HiddenSectors));
            Put32(boot, 32, (uint)L.TotalSectors);
            Put32(boot, 36, L.FatSize);
            Put32(boot, 44, 2);                 // root directory cluster
            Put16(boot, 48, 1);                 // FSInfo sector
            Put16(boot, 50, 6);                 // backup boot sector
            boot[64] = 0x80; boot[66] = 0x29;
            Put32(boot, 67, volId);
            Encoding.ASCII.GetBytes((lbl.Length > 0 ? lbl : "NO NAME").PadRight(11)).CopyTo(boot, 71);
            Encoding.ASCII.GetBytes("FAT32   ").CopyTo(boot, 82);
            // tiny boot code: print nothing, halt (the volume is not bootable)
            boot[0x5A] = 0xF4; boot[0x5B] = 0xEB; boot[0x5C] = 0xFD;
            boot[510] = 0x55; boot[511] = 0xAA;

            // 3) FSInfo
            var fsi = new byte[bps];
            Put32(fsi, 0, 0x41615252);
            Put32(fsi, 484, 0x61417272);
            Put32(fsi, 488, L.ClusterCount - 1);   // root dir uses one cluster
            Put32(fsi, 492, 3);
            Put32(fsi, 508, 0xAA550000);

            var sig = new byte[bps];                // 3rd boot sector: signature only
            sig[510] = 0x55; sig[511] = 0xAA;

            foreach (int baseSec in new[] { 0, 6 })
            {
                t.Write((long)(baseSec + 0) * bps, boot, bps);
                t.Write((long)(baseSec + 1) * bps, fsi, bps);
                t.Write((long)(baseSec + 2) * bps, sig, bps);
            }

            // 4) first FAT sector of every copy: media, EOC, root dir EOC
            var fat = new byte[bps];
            Put32(fat, 0, 0x0FFFFFF8);
            Put32(fat, 4, 0x0FFFFFFF);
            Put32(fat, 8, 0x0FFFFFFF);
            for (int i = 0; i < L.NumFats; i++)
                t.Write(((long)L.Reserved + (long)i * L.FatSize) * bps, fat, bps);

            // 5) root directory: volume label entry
            if (lbl.Length > 0)
            {
                var root = new byte[bps];
                Encoding.ASCII.GetBytes(lbl.PadRight(11)).CopyTo(root, 0);
                root[11] = 0x08;
                var now = DateTime.Now;
                int time = (now.Hour << 11) | (now.Minute << 5) | (now.Second / 2);
                int date = ((now.Year - 1980) << 9) | (now.Month << 5) | now.Day;
                Put16(root, 22, time); Put16(root, 24, date);
                t.Write(L.DataStartSector * bps, root, bps);
            }
            log(FoxideDisk.L.F("FAT32: кластер {0}, кластеров {1}, FAT {2} × 2", DiskService.FormatSize((ulong)L.ClusterBytes), L.ClusterCount.ToString("N0"),
                DiskService.FormatSize((ulong)L.FatSize * (ulong)bps)));
        }

        // ---------------------------------------------------------------- real volume
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr ovl);

        const uint FSCTL_LOCK_VOLUME = 0x90018, FSCTL_UNLOCK_VOLUME = 0x9001C, FSCTL_DISMOUNT_VOLUME = 0x90020;
        const uint IOCTL_DISK_GET_PARTITION_INFO_EX = 0x70048, IOCTL_DISK_GET_DRIVE_GEOMETRY = 0x70000;

        class VolumeTarget : ISectorTarget
        {
            readonly SafeFileHandle h;
            public VolumeTarget(SafeFileHandle h) { this.h = h; }
            public void Write(long offset, byte[] buffer, int count)
            {
                long np;
                if (!Native.SetFilePointerEx(h, offset, out np, 0)) throw new Win32Exception();
                var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    uint written;
                    if (!Native.WriteFile(h, pin.AddrOfPinnedObject(), (uint)count, out written, IntPtr.Zero) || written != count)
                        throw new Win32Exception();
                }
                finally { pin.Free(); }
            }
        }

        static bool Ioctl(SafeFileHandle h, uint code, byte[] outBuf)
        {
            int ret;
            return DeviceIoControl(h, code, IntPtr.Zero, 0, outBuf, outBuf == null ? 0 : outBuf.Length, out ret, IntPtr.Zero);
        }

        /// <summary>Formats a volume (path like \\?\Volume{guid}) as FAT32.</summary>
        public static void FormatVolume(string volumePath, string label, Action<string> log)
        {
            string path = volumePath.TrimEnd('\\');
            using (var h = Native.CreateFile(path, Native.GENERIC_READ | Native.GENERIC_WRITE, 3, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), FoxideDisk.L.T("Не удалось открыть том"));
                bool locked = false;
                for (int i = 0; i < 20 && !locked; i++) { locked = Ioctl(h, FSCTL_LOCK_VOLUME, null); if (!locked) Thread.Sleep(250); }
                if (!locked) throw new IOException(FoxideDisk.L.T("Том используется другой программой — не удалось заблокировать его для форматирования."));
                try
                {
                    var geo = new byte[24];
                    int bps = Ioctl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY, geo) ? BitConverter.ToInt32(geo, 20) : 512;
                    if (bps < 512 || bps > 4096) bps = 512;
                    var pi = new byte[144];
                    if (!Ioctl(h, IOCTL_DISK_GET_PARTITION_INFO_EX, pi)) throw new Win32Exception(Marshal.GetLastWin32Error(), FoxideDisk.L.T("Не удалось узнать размер тома"));
                    long start = BitConverter.ToInt64(pi, 8), length = BitConverter.ToInt64(pi, 16);
                    var L = Plan(length, bps, start / bps);
                    log(FoxideDisk.L.F("Форматирование FAT32 (встроенный форматировщик): {0}, сектор {1} Б", DiskService.FormatSize((ulong)length), bps));
                    Write(new VolumeTarget(h), L, label, log);
                    Ioctl(h, FSCTL_DISMOUNT_VOLUME, null);   // Windows remounts it as FAT32
                }
                finally { Ioctl(h, FSCTL_UNLOCK_VOLUME, null); }
            }
        }
    }
}
