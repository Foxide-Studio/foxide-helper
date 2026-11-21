using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace FoxideDisk
{
    public class CpuInfo
    {
        public string Name = L.T("Процессор");
        public int Cores, Threads;
        public uint MaxMHz;
    }

    /// <summary>CPU load, clock and temperature without a kernel driver.</summary>
    public class CpuSensors : IDisposable
    {
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        long pIdle, pKernel, pUser;
        PerformanceCounter perf;      // % Processor Performance (current clock vs base clock)
        readonly List<PerformanceCounter> zones = new List<PerformanceCounter>();
        public readonly CpuInfo Info = new CpuInfo();

        public CpuSensors()
        {
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
                    foreach (ManagementObject o in s.Get())
                    {
                        Info.Name = Convert.ToString(o["Name"]).Trim();
                        Info.Cores += Convert.ToInt32(o["NumberOfCores"]);
                        Info.Threads += Convert.ToInt32(o["NumberOfLogicalProcessors"]);
                        Info.MaxMHz = Math.Max(Info.MaxMHz, Convert.ToUInt32(o["MaxClockSpeed"]));
                    }
            }
            catch { }
            if (Info.Threads == 0) Info.Threads = Environment.ProcessorCount;
            try { perf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total"); perf.NextValue(); } catch { perf = null; }
            try
            {
                var cat = new PerformanceCounterCategory("Thermal Zone Information");
                foreach (var inst in cat.GetInstanceNames())
                {
                    string counter = cat.CounterExists("High Precision Temperature") ? "High Precision Temperature" : "Temperature";
                    zones.Add(new PerformanceCounter("Thermal Zone Information", counter, inst));
                }
            }
            catch { }
            GetSystemTimes(out pIdle, out pKernel, out pUser);
        }

        /// <summary>Total CPU load in percent since the previous call.</summary>
        public double Load()
        {
            long i, k, u;
            if (!GetSystemTimes(out i, out k, out u)) return 0;
            long di = i - pIdle, dk = k - pKernel, du = u - pUser;
            pIdle = i; pKernel = k; pUser = u;
            long total = dk + du;   // kernel time includes idle
            return total <= 0 ? 0 : Math.Max(0, Math.Min(100, 100.0 * (total - di) / total));
        }

        /// <summary>Effective clock in MHz (base clock × % Processor Performance), 0 if unknown.</summary>
        public double ClockMHz()
        {
            try { if (perf != null && Info.MaxMHz > 0) return Info.MaxMHz * perf.NextValue() / 100.0; } catch { }
            return 0;
        }

        /// <summary>Highest ACPI thermal-zone temperature (°C) — usually the CPU package / board sensor.</summary>
        /// <summary>Set by the UI when the PawnIO driver gives a real CPU temperature; preferred over ACPI zones.</summary>
        public static Func<double?> ExternalTemp;

        public double? Temperature()
        {
            try { var f = ExternalTemp; if (f != null) { var v = f(); if (v.HasValue) return v; } } catch { }
            double best = double.NaN;
            foreach (var z in zones)
            {
                try
                {
                    double v = z.NextValue();
                    double c = z.CounterName.StartsWith("High") ? v / 10.0 - 273.15 : v - 273.15;
                    // many laptops expose a placeholder zone stuck at ~20 °C (293 K) — not a real CPU reading
                    if (c > 26 && c < 125 && (double.IsNaN(best) || c > best)) best = c;
                }
                catch { }
            }
            if (!double.IsNaN(best)) return best;
            try
            {
                using (var s = new ManagementObjectSearcher("root\\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                    foreach (ManagementObject o in s.Get())
                    {
                        double c = Convert.ToDouble(o["CurrentTemperature"]) / 10.0 - 273.15;
                        if (c > 26 && c < 125 && (double.IsNaN(best) || c > best)) best = c;
                    }
            }
            catch { }
            return double.IsNaN(best) ? (double?)null : best;
        }

        public void Dispose()
        {
            if (perf != null) perf.Dispose();
            foreach (var z in zones) z.Dispose();
        }
    }

    /// <summary>
    /// CPU torture test: every thread multiplies 128×128 double matrices and compares the checksum with a reference
    /// computed before the test. A mismatch means the CPU produced a wrong result (instability).
    /// </summary>
    public class CpuStress
    {
        const int N = 128;
        long iterations, errors;
        volatile bool stop;
        readonly List<Thread> threads = new List<Thread>();
        double reference;
        public long Iterations { get { return Interlocked.Read(ref iterations); } }
        public long Errors { get { return Interlocked.Read(ref errors); } }
        public int ThreadCount { get; private set; }
        /// <summary>Floating point operations per iteration.</summary>
        public static double FlopsPerIteration { get { return 2.0 * N * N * N; } }

        static void Fill(double[] a, double[] b)
        {
            for (int i = 0; i < N * N; i++) { a[i] = ((i * 7919) % 1000) / 997.0 - 0.5; b[i] = ((i * 104729) % 1000) / 991.0 - 0.5; }
        }

        static double Multiply(double[] a, double[] b, double[] c)
        {
            Array.Clear(c, 0, c.Length);
            for (int i = 0; i < N; i++)
                for (int k = 0; k < N; k++)
                {
                    double aik = a[i * N + k];
                    int ci = i * N, bk = k * N;
                    for (int j = 0; j < N; j++) c[ci + j] += aik * b[bk + j];
                }
            double sum = 0;
            for (int i = 0; i < c.Length; i++) sum += c[i] * ((i & 7) + 1);
            return sum;
        }

        public void Start(int threadCount)
        {
            var a = new double[N * N]; var b = new double[N * N]; var c = new double[N * N];
            Fill(a, b);
            reference = Multiply(a, b, c);
            stop = false; iterations = 0; errors = 0;
            ThreadCount = threadCount;
            for (int t = 0; t < threadCount; t++)
            {
                var th = new Thread(Worker) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "cpu-stress-" + t };
                threads.Add(th);
                th.Start();
            }
        }

        void Worker()
        {
            var a = new double[N * N]; var b = new double[N * N]; var c = new double[N * N];
            Fill(a, b);
            while (!stop)
            {
                double r = Multiply(a, b, c);
                if (r != reference) Interlocked.Increment(ref errors);
                Interlocked.Increment(ref iterations);
            }
        }

        public void Stop()
        {
            stop = true;
            foreach (var t in threads) t.Join(2000);
            threads.Clear();
        }
    }
}
