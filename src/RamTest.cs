using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FoxideDisk
{
    public class RamTestResult
    {
        public long Bytes;
        public int Passes, Patterns;
        public long Errors;
        public TimeSpan Time;
        public double GBps;
        public bool Cancelled;
        public bool Locked;
    }

    /// <summary>
    /// In-Windows memory test (MemTest-style patterns) over the RAM this process can get.
    /// It cannot touch memory used by Windows and other programs — for that use Windows Memory Diagnostic.
    /// </summary>
    public static unsafe class RamTest
    {
        const long Chunk = 128L << 20;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetProcessWorkingSetSize(IntPtr proc, UIntPtr min, UIntPtr max);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool VirtualLock(IntPtr addr, UIntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool VirtualUnlock(IntPtr addr, UIntPtr size);

        public static readonly string[] PatternNames =
        {
            L.T("Нули"), L.T("Единицы"), L.T("Шахматка 0x55"), L.T("Шахматка 0xAA"), L.T("Адрес в ячейке"), L.T("Бегущая единица"), L.T("Случайные данные"),
        };

        static ulong Value(int pattern, ulong* p, ulong index, ref ulong rnd)
        {
            switch (pattern)
            {
                case 0: return 0UL;
                case 1: return ~0UL;
                case 2: return 0x5555555555555555UL;
                case 3: return 0xAAAAAAAAAAAAAAAAUL;
                case 4: return (ulong)p ^ 0x9E3779B97F4A7C15UL;
                case 5: return 1UL << (int)(index & 63);
                default:
                    rnd ^= rnd << 13; rnd ^= rnd >> 7; rnd ^= rnd << 17;   // xorshift64
                    return rnd;
            }
        }

        public static RamTestResult Run(long bytes, int passes, Action<string, double> progress, Action<string> log, CancellationToken ct)
        {
            var res = new RamTestResult { Passes = passes, Patterns = PatternNames.Length };
            var chunks = new List<IntPtr>();
            var proc = Process.GetCurrentProcess().Handle;
            var sw = Stopwatch.StartNew();
            try
            {
                // ask Windows to keep the test buffer resident (otherwise it may page it out to disk)
                try { SetProcessWorkingSetSize(proc, (UIntPtr)(ulong)(bytes + (256L << 20)), (UIntPtr)(ulong)(bytes + (512L << 20))); } catch { }

                long got = 0;
                int locked = 0;
                while (got < bytes)
                {
                    ct.ThrowIfCancellationRequested();
                    long size = Math.Min(Chunk, bytes - got);
                    var p = Native.VirtualAlloc(IntPtr.Zero, (UIntPtr)(ulong)size, Native.MEM_COMMIT | Native.MEM_RESERVE, Native.PAGE_READWRITE);
                    if (p == IntPtr.Zero) { log(L.F("Windows выделила только {0} — тест пойдёт на этом объёме.", DiskService.FormatSize((ulong)got))); break; }
                    if (VirtualLock(p, (UIntPtr)(ulong)size)) locked++;
                    chunks.Add(p);
                    got += size;
                    progress(L.T("Выделение памяти"), 0);
                }
                res.Bytes = got;
                res.Locked = locked == chunks.Count && chunks.Count > 0;
                if (got == 0) throw new OutOfMemoryException(L.T("Не удалось выделить память для теста."));
                log(L.F("Тестируется {0} в {1} блоках", DiskService.FormatSize((ulong)got), chunks.Count) +
                    (res.Locked ? L.T(", память закреплена в ОЗУ.") : L.T(" (часть памяти не удалось закрепить — Windows может временно выгрузить её на диск, тест будет медленнее).")));

                long errors = 0;
                int logged = 0;
                long steps = (long)passes * PatternNames.Length * 2 * chunks.Count, done = 0;
                var po = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct };
                double moved = 0;

                for (int pass = 0; pass < passes; pass++)
                    for (int pat = 0; pat < PatternNames.Length; pat++)
                    {
                        string name = L.F("Проход {0}/{1} · {2}", pass + 1, passes, PatternNames[pat]);
                        string nameWrite = L.F("{0} — запись", name), nameVerify = L.F("{0} — проверка", name);
                        int pt = pat, ps = pass;
                        // write
                        Parallel.For(0, chunks.Count, po, ci =>
                        {
                            ulong* b = (ulong*)chunks[ci];
                            long n = Math.Min(Chunk, res.Bytes - (long)ci * Chunk) / 8;
                            ulong rnd = 0x2545F4914F6CDD1DUL ^ ((ulong)ci << 32) ^ (ulong)ps;
                            for (long i = 0; i < n; i++) b[i] = Value(pt, b + i, (ulong)i, ref rnd);
                            progress(nameWrite, (double)Interlocked.Increment(ref done) / steps);
                        });
                        // verify
                        Parallel.For(0, chunks.Count, po, ci =>
                        {
                            ulong* b = (ulong*)chunks[ci];
                            long n = Math.Min(Chunk, res.Bytes - (long)ci * Chunk) / 8;
                            ulong rnd = 0x2545F4914F6CDD1DUL ^ ((ulong)ci << 32) ^ (ulong)ps;
                            for (long i = 0; i < n; i++)
                            {
                                ulong want = Value(pt, b + i, (ulong)i, ref rnd), got2 = b[i];
                                if (got2 != want)
                                {
                                    Interlocked.Increment(ref errors);
                                    if (Interlocked.Increment(ref logged) <= 50)
                                        log(L.F("ОШИБКА [{0}] адрес 0x{1}: ожидалось {2}, прочитано {3} (биты {4})", PatternNames[pt],
                                            ((ulong)(b + i)).ToString("X"), want.ToString("X16"), got2.ToString("X16"), (want ^ got2).ToString("X16")));
                                }
                            }
                            progress(nameVerify, (double)Interlocked.Increment(ref done) / steps);
                        });
                        moved += 2.0 * res.Bytes;
                        if (errors > 0 && pat == PatternNames.Length - 1)
                            log(L.F("После прохода {0}: ошибок {1}", ps + 1, errors));
                    }
                res.Errors = errors;
                res.Time = sw.Elapsed;
                res.GBps = moved / sw.Elapsed.TotalSeconds / (1L << 30);
                return res;
            }
            catch (OperationCanceledException)
            {
                res.Cancelled = true;
                res.Time = sw.Elapsed;
                return res;
            }
            finally
            {
                foreach (var p in chunks)
                {
                    VirtualUnlock(p, (UIntPtr)(ulong)Chunk);
                    Native.VirtualFree(p, UIntPtr.Zero, Native.MEM_RELEASE);
                }
                try { SetProcessWorkingSetSize(proc, (UIntPtr)ulong.MaxValue, (UIntPtr)ulong.MaxValue); } catch { }   // (SIZE_T)-1: trim back
            }
        }
    }
}
