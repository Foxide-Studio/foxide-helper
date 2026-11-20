using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace FoxideDisk
{
    public class SpeedResult
    {
        public double SeqRead, SeqWrite, RndRead, RndWrite;   // MB/s (decimal)
        public double RndReadIops, RndWriteIops;
        public string Volume;
        public int SizeMB;
        public DateTime When;
    }

    /// <summary>Unbuffered (FILE_FLAG_NO_BUFFERING) disk benchmark: sequential 1 MiB and random 4 KiB at QD1.</summary>
    public static class SpeedTest
    {
        const int SeqBlock = 1 << 20;
        const int RndBlock = 4096;

        public static SpeedResult Run(char letter, int sizeMB, int rndSeconds, Action<string, double> progress, CancellationToken ct)
        {
            string root = letter + ":\\";
            var di = new DriveInfo(root);
            long size = (long)sizeMB * SeqBlock;
            if (!di.IsReady) throw new IOException(L.F("Том {0} не готов.", root));
            if (di.AvailableFreeSpace < size + 64L * SeqBlock)
                throw new IOException(L.F("Недостаточно свободного места на {0}: нужно минимум {1}.", root,
                    DiskService.FormatSize((ulong)(size + 64L * SeqBlock))));
            if (di.DriveFormat.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) && di.DriveFormat != "exFAT" && size >= 4L * 1024 * SeqBlock)
                size = 4L * 1024 * SeqBlock - SeqBlock * 2;

            string file = Path.Combine(root, "FoxideDisk_speedtest.tmp");
            var res = new SpeedResult { Volume = root, SizeMB = (int)(size / SeqBlock), When = DateTime.Now };
            IntPtr buf = Native.VirtualAlloc(IntPtr.Zero, (UIntPtr)SeqBlock, Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_READWRITE);
            if (buf == IntPtr.Zero) throw new Win32Exception();
            try
            {
                var rnd = new Random();
                var tmp = new byte[SeqBlock];
                rnd.NextBytes(tmp);
                Marshal.Copy(tmp, 0, buf, SeqBlock);

                // 1. Sequential write
                using (var h = Open(file, true))
                {
                    var sw = Stopwatch.StartNew();
                    long done = 0;
                    while (done < size)
                    {
                        ct.ThrowIfCancellationRequested();
                        uint n;
                        if (!Native.WriteFile(h, buf, SeqBlock, out n, IntPtr.Zero)) throw new Win32Exception();
                        done += n;
                        if ((done / SeqBlock) % 16 == 0) progress(L.T("Последовательная запись"), 0.25 * done / size);
                    }
                    res.SeqWrite = done / sw.Elapsed.TotalSeconds / 1e6;
                }

                // 2. Sequential read
                using (var h = Open(file, false))
                {
                    var sw = Stopwatch.StartNew();
                    long done = 0;
                    while (done < size)
                    {
                        ct.ThrowIfCancellationRequested();
                        uint n;
                        if (!Native.ReadFile(h, buf, SeqBlock, out n, IntPtr.Zero)) throw new Win32Exception();
                        if (n == 0) break;
                        done += n;
                        if ((done / SeqBlock) % 16 == 0) progress(L.T("Последовательное чтение"), 0.25 + 0.25 * done / size);
                    }
                    res.SeqRead = done / sw.Elapsed.TotalSeconds / 1e6;
                }

                long blocks = size / RndBlock;
                // 3. Random 4K read
                using (var h = Open(file, false))
                {
                    double iops = Random4K(h, buf, blocks, rndSeconds, false, rnd, ct,
                        f => progress(L.T("Случайное чтение 4K"), 0.5 + 0.25 * f));
                    res.RndReadIops = iops; res.RndRead = iops * RndBlock / 1e6;
                }
                // 4. Random 4K write
                using (var h = Open(file, true, false))
                {
                    double iops = Random4K(h, buf, blocks, rndSeconds, true, rnd, ct,
                        f => progress(L.T("Случайная запись 4K"), 0.75 + 0.25 * f));
                    res.RndWriteIops = iops; res.RndWrite = iops * RndBlock / 1e6;
                }
                progress(L.T("Готово"), 1);
                return res;
            }
            finally
            {
                Native.VirtualFree(buf, UIntPtr.Zero, Native.MEM_RELEASE);
                try { File.Delete(file); } catch { }
            }
        }

        static SafeFileHandle Open(string file, bool write, bool create = true)
        {
            var h = Native.CreateFile(file,
                write ? (Native.GENERIC_READ | Native.GENERIC_WRITE) : Native.GENERIC_READ,
                Native.FILE_SHARE_READ, IntPtr.Zero,
                write && create ? Native.CREATE_ALWAYS : Native.OPEN_EXISTING,
                Native.FILE_FLAG_NO_BUFFERING | Native.FILE_FLAG_WRITE_THROUGH, IntPtr.Zero);
            if (h.IsInvalid) throw new Win32Exception();
            return h;
        }

        static double Random4K(SafeFileHandle h, IntPtr buf, long blocks, int seconds, bool write, Random rnd,
            CancellationToken ct, Action<double> progress)
        {
            var sw = Stopwatch.StartNew();
            long ops = 0;
            double limit = seconds;
            while (sw.Elapsed.TotalSeconds < limit)
            {
                ct.ThrowIfCancellationRequested();
                for (int i = 0; i < 64; i++)
                {
                    long off = (long)(rnd.NextDouble() * blocks) * RndBlock;
                    long np;
                    if (!Native.SetFilePointerEx(h, off, out np, 0)) throw new Win32Exception();
                    uint n;
                    bool ok = write ? Native.WriteFile(h, buf, RndBlock, out n, IntPtr.Zero)
                                    : Native.ReadFile(h, buf, RndBlock, out n, IntPtr.Zero);
                    if (!ok) throw new Win32Exception();
                    ops++;
                }
                progress(Math.Min(1, sw.Elapsed.TotalSeconds / limit));
            }
            return ops / sw.Elapsed.TotalSeconds;
        }
    }
}
