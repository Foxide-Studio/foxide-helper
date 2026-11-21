using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace FoxideDisk
{
    /// <summary>
    /// Access to the PawnIO driver (https://pawnio.eu, GPL-2.0 with the device-IOCTL exception) — the signed driver that
    /// LibreHardwareMonitor and FanControl use. The program talks to it only through DeviceIoControl, as the licence
    /// exception requires, and loads the official signed modules (PawnIO.Modules, LGPL-2.1) embedded as resources.
    /// Each loaded module is a separate handle; a module can only do what its signed code allows (e.g. read MSRs,
    /// talk to the Super I/O chip, SMBus transfers).
    /// </summary>
    public sealed class PawnModule : IDisposable
    {
        const uint IOCTL_PIO_LOAD_BINARY = (41394u << 16) | (0x821 << 2);
        const uint IOCTL_PIO_EXECUTE_FN = (41394u << 16) | (0x841 << 2);
        const int FnNameLength = 32;

        readonly SafeFileHandle h;
        public readonly string Name;
        readonly object sync = new object();

        PawnModule(SafeFileHandle handle, string name) { h = handle; Name = name; }

        /// <summary>Open the driver and load the embedded module (e.g. "IntelMSR"). Null with a reason on failure.</summary>
        public static PawnModule Load(string module, out string error)
        {
            error = null;
            byte[] blob = PawnIo.ModuleBlob(module);
            if (blob == null) { error = "module " + module + " is not embedded"; return null; }
            SafeFileHandle h;
            int st = PawnIo.OpenDevice(out h);
            if (st != 0) { error = PawnIo.StatusText(st); return null; }
            int ret;
            if (!DeviceIoControl(h, IOCTL_PIO_LOAD_BINARY, blob, blob.Length, null, 0, out ret, IntPtr.Zero))
            {
                int e = Marshal.GetLastWin32Error();
                h.Dispose();
                error = "load " + module + ": " + new System.ComponentModel.Win32Exception(e).Message;
                return null;
            }
            return new PawnModule(h, module);
        }

        /// <summary>Call a module function: name (≤ 31 chars), 64-bit inputs, number of 64-bit outputs.
        /// Returns null on failure (status in lastError).</summary>
        public ulong[] Execute(string fn, ulong[] input, int outCount)
        {
            int err;
            return Execute(fn, input, outCount, out err);
        }

        public ulong[] Execute(string fn, ulong[] input, int outCount, out int win32Error)
        {
            win32Error = 0;
            input = input ?? new ulong[0];
            var inBuf = new byte[FnNameLength + input.Length * 8];
            var nb = Encoding.ASCII.GetBytes(fn);
            if (nb.Length >= FnNameLength) throw new ArgumentException("function name too long");
            Buffer.BlockCopy(nb, 0, inBuf, 0, nb.Length);
            for (int i = 0; i < input.Length; i++) Buffer.BlockCopy(BitConverter.GetBytes(input[i]), 0, inBuf, FnNameLength + i * 8, 8);
            var outBuf = new byte[Math.Max(0, outCount) * 8];
            int ret;
            bool ok;
            lock (sync) ok = DeviceIoControl(h, IOCTL_PIO_EXECUTE_FN, inBuf, inBuf.Length, outBuf, outBuf.Length, out ret, IntPtr.Zero);
            if (!ok) { win32Error = Marshal.GetLastWin32Error(); return null; }
            var res = new ulong[Math.Min(outCount, ret / 8)];
            for (int i = 0; i < res.Length; i++) res[i] = BitConverter.ToUInt64(outBuf, i * 8);
            return res;
        }

        public ulong? Read1(string fn, ulong arg)
        {
            var r = Execute(fn, new[] { arg }, 1);
            return r != null && r.Length == 1 ? r[0] : (ulong?)null;
        }

        public bool Call(string fn, params ulong[] args)
        {
            return Execute(fn, args, 0) != null;
        }

        public void Dispose() { try { h.Dispose(); } catch { } }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);
    }

    public static class PawnIo
    {
        public const string SetupUrl = "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";
        public const string SetupApi = "https://api.github.com/repos/namazso/PawnIO.Setup/releases/latest";

        // ---------------------------------------------------------------- bus mutexes shared with other monitoring tools
        // The de-facto standard names used by HWiNFO, AIDA64, LibreHardwareMonitor, FanControl… Holding them while
        // talking to the Super I/O chip / SMBus keeps two programs from interleaving register accesses.
        public static readonly string IsaBusMutex = @"Global\Access_ISABUS.HTP.Method";
        public static readonly string SmbusMutex = @"Global\Access_SMBUS.HTP.Method";
        public static readonly string PciMutex = @"Global\Access_PCI";

        static readonly Dictionary<string, Mutex> mutexes = new Dictionary<string, Mutex>();

        /// <summary>Run action holding the named global mutex (waits up to timeoutMs). False if it could not be taken.</summary>
        public static bool WithMutex(string name, int timeoutMs, Action action)
        {
            Mutex m;
            lock (mutexes)
            {
                if (!mutexes.TryGetValue(name, out m))
                {
                    try { m = new Mutex(false, name); }
                    catch (UnauthorizedAccessException) { try { m = Mutex.OpenExisting(name); } catch { m = null; } }
                    catch { m = null; }
                    mutexes[name] = m;
                }
            }
            if (m == null) { action(); return true; }
            bool got = false;
            try
            {
                try { got = m.WaitOne(timeoutMs); } catch (AbandonedMutexException) { got = true; }
                if (!got) return false;
                action();
                return true;
            }
            finally { if (got) try { m.ReleaseMutex(); } catch { } }
        }

        // ---------------------------------------------------------------- driver state
        /// <summary>The PawnIO service is installed (it may still be stopped).</summary>
        public static bool Installed
        {
            get
            {
                try
                {
                    using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO"))
                        return k != null;
                }
                catch { return false; }
            }
        }

        /// <summary>The driver can be opened right now (installed, running and we are elevated).</summary>
        public static bool Available
        {
            get
            {
                SafeFileHandle h;
                int st = OpenDevice(out h);
                if (h != null) h.Dispose();
                return st == 0;
            }
        }

        /// <summary>Short reason why the driver can't be used, for the UI. Null when it works.</summary>
        public static string Problem()
        {
            SafeFileHandle h;
            int st = OpenDevice(out h);
            if (h != null) h.Dispose();
            if (st == 0) return null;
            if (!Installed) return L.T("Драйвер PawnIO не установлен.");
            if ((uint)st == 0xC0000022) return L.T("Нужны права администратора.");
            return L.T("Драйвер PawnIO установлен, но не запущен:") + " " + StatusText(st);
        }

        internal static string StatusText(int status)
        {
            switch ((uint)status)
            {
                case 0xC0000034: return L.T("драйвер не найден");
                case 0xC0000022: return L.T("доступ запрещён");
                case 0xC000003A: return L.T("драйвер не найден");
                default: return "NTSTATUS 0x" + ((uint)status).ToString("X8");
            }
        }

        // ---------------------------------------------------------------- embedded modules
        public static byte[] ModuleBlob(string module)
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("PawnIO." + module + ".bin"))
                {
                    if (s == null) return null;
                    var ms = new MemoryStream();
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- installation
        /// <summary>
        /// Download the official installer from GitHub (SHA-256 checked against the digest GitHub reports, Authenticode
        /// signature must be valid) and run it. Blocks until the installer exits. Returns null on success, else the reason.
        /// </summary>
        public static string Install(Action<string> progress)
        {
            try
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                if (progress != null) progress(L.T("Узнаю последнюю версию PawnIO…"));
                string url = SetupUrl, sha = null;
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(SetupApi);
                    req.UserAgent = "FoxideHelper/" + Updater.CurrentText; req.Accept = "application/vnd.github+json"; req.Timeout = 15000;
                    string json;
                    using (var r = req.GetResponse()) using (var rd = new StreamReader(r.GetResponseStream())) json = rd.ReadToEnd();
                    var d = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                    foreach (var o in (d != null && d.ContainsKey("assets") ? d["assets"] as object[] : null) ?? new object[0])
                    {
                        var a = o as Dictionary<string, object>;
                        if (a == null || !"PawnIO_setup.exe".Equals(a["name"] as string, StringComparison.OrdinalIgnoreCase)) continue;
                        url = a["browser_download_url"] as string ?? url;
                        var dg = a.ContainsKey("digest") ? a["digest"] as string : null;
                        if (dg != null && dg.StartsWith("sha256:")) sha = dg.Substring(7);
                    }
                }
                catch { }
                string dir = Path.Combine(Path.GetTempPath(), "FoxideHelper-PawnIO");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "PawnIO_setup.exe");
                if (progress != null) progress(L.T("Скачиваю установщик PawnIO…"));
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "FoxideHelper/" + Updater.CurrentText;
                    wc.DownloadFile(url, file);
                }
                if (sha != null)
                {
                    string got;
                    using (var f = File.OpenRead(file)) using (var h = SHA256.Create()) got = BitConverter.ToString(h.ComputeHash(f)).Replace("-", "");
                    if (!got.Equals(sha, StringComparison.OrdinalIgnoreCase)) return L.T("Контрольная сумма установщика не совпала — установка отменена.");
                }
                if (!SignatureValid(file)) return L.T("У установщика нет действительной цифровой подписи — установка отменена.");
                if (progress != null) progress(L.T("Запускаю установщик PawnIO…"));
                using (var p = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, WorkingDirectory = dir }))
                    p.WaitForExit();
                try { File.Delete(file); } catch { }
                return Installed ? null : L.T("Установка PawnIO не была завершена.");
            }
            catch (Exception ex) { return ex.Message; }
        }

        // ---------------------------------------------------------------- native
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct UNICODE_STRING { public ushort Length, MaximumLength; public string Buffer; }
        [StructLayout(LayoutKind.Sequential)]
        struct OBJECT_ATTRIBUTES { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes; public IntPtr SecurityDescriptor, SecurityQualityOfService; }
        [StructLayout(LayoutKind.Sequential)]
        struct IO_STATUS_BLOCK { public IntPtr Status, Information; }
        [DllImport("ntdll.dll")]
        static extern int NtOpenFile(out IntPtr handle, uint access, ref OBJECT_ATTRIBUTES attr, out IO_STATUS_BLOCK io, uint share, uint options);

        internal static int OpenDevice(out SafeFileHandle handle)
        {
            handle = null;
            const string path = @"\Device\PawnIO";
            var us = new UNICODE_STRING { Length = (ushort)(path.Length * 2), MaximumLength = (ushort)(path.Length * 2 + 2), Buffer = path };
            IntPtr pus = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(UNICODE_STRING)));
            try
            {
                Marshal.StructureToPtr(us, pus, false);
                var oa = new OBJECT_ATTRIBUTES { Length = Marshal.SizeOf(typeof(OBJECT_ATTRIBUTES)), ObjectName = pus, Attributes = 0x40 /*OBJ_CASE_INSENSITIVE*/ };
                IntPtr h; IO_STATUS_BLOCK io;
                int st = NtOpenFile(out h, 0xC0000000 /*GENERIC_READ|GENERIC_WRITE*/, ref oa, out io, 7, 0);
                if (st == 0) handle = new SafeFileHandle(h, true);
                return st;
            }
            finally { Marshal.DestroyStructure(pus, typeof(UNICODE_STRING)); Marshal.FreeHGlobal(pus); }
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WINTRUST_FILE_INFO { public int cbStruct; public string pcwszFilePath; public IntPtr hFile, pgKnownSubject; }
        [StructLayout(LayoutKind.Sequential)]
        struct WINTRUST_DATA { public int cbStruct; public IntPtr pPolicyCallbackData, pSIPClientData; public int dwUIChoice, fdwRevocationChecks, dwUnionChoice; public IntPtr pFile; public int dwStateAction; public IntPtr hWVTStateData, pwszURLReference; public int dwProvFlags, dwUIContext; public IntPtr pSignatureSettings; }

        static bool SignatureValid(string file)
        {
            var fi = new WINTRUST_FILE_INFO { cbStruct = Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)), pcwszFilePath = file };
            IntPtr pfi = Marshal.AllocHGlobal(fi.cbStruct);
            try
            {
                Marshal.StructureToPtr(fi, pfi, false);
                var wd = new WINTRUST_DATA { cbStruct = Marshal.SizeOf(typeof(WINTRUST_DATA)), dwUIChoice = 2 /*NONE*/, dwUnionChoice = 1 /*FILE*/, pFile = pfi };
                var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2
                return WinVerifyTrust(IntPtr.Zero, ref action, ref wd) == 0;
            }
            finally { Marshal.DestroyStructure(pfi, typeof(WINTRUST_FILE_INFO)); Marshal.FreeHGlobal(pfi); }
        }
    }
}
