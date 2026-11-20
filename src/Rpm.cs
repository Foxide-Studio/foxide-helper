using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace FoxideDisk
{
    /// <summary>
    /// Spindle speed detection for disks where Windows does not report it (typically HDDs in USB enclosures):
    ///  1) ATA IDENTIFY word 217 through SCSI/ATA Translation (ATA PASS-THROUGH 12 / 16),
    ///  2) SCSI VPD page B1 "Block Device Characteristics",
    ///  3) known model-number patterns (marked as an estimate).
    /// Returns 0 = unknown, 1 = non-rotating (SSD), otherwise RPM.
    /// </summary>
    public static class Rpm
    {
        static readonly Dictionary<string, KeyValuePair<int, string>> cache = new Dictionary<string, KeyValuePair<int, string>>();

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr ovl);

        const uint IOCTL_SCSI_PASS_THROUGH = 0x4D004;

        public static int Detect(int diskNumber, string model, string cacheKey, out string source)
        {
            KeyValuePair<int, string> hit;
            lock (cache)
                if (cache.TryGetValue(cacheKey, out hit)) { source = hit.Value; return hit.Key; }

            int rpm = 0; source = null;
            try { rpm = FromDevice(diskNumber, out source); } catch { rpm = 0; }
            if (rpm == 0)
            {
                rpm = FromModel(model);
                if (rpm > 0) source = L.T("оценка по модели");
            }
            lock (cache) cache[cacheKey] = new KeyValuePair<int, string>(rpm, source);
            return rpm;
        }

        static int FromDevice(int n, out string source)
        {
            source = null;
            using (var h = Native.CreateFile(@"\\.\PhysicalDrive" + n, Native.GENERIC_READ | Native.GENERIC_WRITE, 3, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return 0;
                byte[] data;
                // ATA PASS-THROUGH (12): PIO data-in, IDENTIFY DEVICE (0xEC)
                data = Scsi(h, new byte[] { 0xA1, 0x08, 0x0E, 0, 1, 0, 0, 0, 0, 0xEC, 0, 0 }, 512);
                int r = ParseIdentify(data);
                if (r == 0)
                {
                    // ATA PASS-THROUGH (16)
                    data = Scsi(h, new byte[] { 0x85, 0x08, 0x0E, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0xEC, 0 }, 512);
                    r = ParseIdentify(data);
                }
                if (r != 0) { source = "ATA IDENTIFY"; return r; }

                // INQUIRY, EVPD=1, page B1
                data = Scsi(h, new byte[] { 0x12, 0x01, 0xB1, 0x00, 0x40, 0x00 }, 64);
                if (data != null && data[1] == 0xB1)
                {
                    int v = (data[4] << 8) | data[5];
                    if (v == 1 || (v >= 0x401 && v < 0xFFFF)) { source = "SCSI VPD"; return v; }
                }
            }
            return 0;
        }

        static int ParseIdentify(byte[] d)
        {
            if (d == null) return 0;
            bool allZero = true;
            for (int i = 0; i < 512; i++) if (d[i] != 0) { allZero = false; break; }
            if (allZero) return 0;
            if ((d[1] & 0x80) != 0) return 0;                      // word 0 bit 15: not an ATA device
            if (d[510] == 0xA5)                                    // integrity word present -> verify checksum
            {
                int sum = 0;
                for (int i = 0; i < 512; i++) sum += d[i];
                if ((sum & 0xFF) != 0) return 0;
            }
            int v = d[434] | (d[435] << 8);                        // word 217: nominal media rotation rate
            if (v == 1) return 1;
            if (v >= 0x401 && v < 0xFFFF && v <= 20000) return v;
            return 0;
        }

        /// <summary>Sends a CDB with data-in via IOCTL_SCSI_PASS_THROUGH; returns the data or null.</summary>
        internal static byte[] Scsi(SafeFileHandle h, byte[] cdb, int dataLen)
        {
            bool x64 = IntPtr.Size == 8;
            int hdr = x64 ? 56 : 44, senseOff = hdr, senseLen = 32, dataOff = hdr + senseLen;
            int total = dataOff + dataLen;
            var buf = new byte[total];
            BitConverter.GetBytes((ushort)hdr).CopyTo(buf, 0);   // Length
            buf[6] = (byte)cdb.Length;                           // CdbLength
            buf[7] = (byte)senseLen;                             // SenseInfoLength
            buf[8] = 1;                                          // DataIn = SCSI_IOCTL_DATA_IN
            BitConverter.GetBytes((uint)dataLen).CopyTo(buf, 12);
            BitConverter.GetBytes((uint)5).CopyTo(buf, 16);      // timeout, s
            if (x64)
            {
                BitConverter.GetBytes((ulong)dataOff).CopyTo(buf, 24);
                BitConverter.GetBytes((uint)senseOff).CopyTo(buf, 32);
                Array.Copy(cdb, 0, buf, 36, cdb.Length);
            }
            else
            {
                BitConverter.GetBytes((uint)dataOff).CopyTo(buf, 20);
                BitConverter.GetBytes((uint)senseOff).CopyTo(buf, 24);
                Array.Copy(cdb, 0, buf, 28, cdb.Length);
            }
            var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                int ret;
                IntPtr p = pin.AddrOfPinnedObject();
                if (!DeviceIoControl(h, IOCTL_SCSI_PASS_THROUGH, p, total, p, total, out ret, IntPtr.Zero)) return null;
            }
            finally { pin.Free(); }
            if (buf[2] != 0) return null;                        // ScsiStatus != GOOD
            var data = new byte[dataLen];
            Array.Copy(buf, dataOff, data, 0, dataLen);
            return data;
        }

        // ---------------------------------------------------------------- model heuristics
        static readonly KeyValuePair<string, int>[] Patterns =
        {
            // Hitachi / HGST
            P(@"\bHT[SE]42", 4200), P(@"\bHT[SE]54", 5400), P(@"\bHT[SE]72", 7200), P(@"\bHT[SE]7\d", 7200), P(@"\bHT[SE]5\d", 5400),
            P(@"\bHDS72", 7200), P(@"\bHUA72", 7200), P(@"\bHDS5C", 5700), P(@"\bHDN72", 7200), P(@"\bHUS72", 7200),
            // Toshiba
            P(@"\bMQ0\dAB[DF]", 5400), P(@"\bMQ01ACF", 7200), P(@"\bMQ0\dUBB", 5400), P(@"\bDT01ACA", 7200), P(@"\bDT01ABA", 5700),
            P(@"\bMK\d{4}G[SA]X", 5400), P(@"\bMK\d{4}G[SA]Y", 7200), P(@"\bHDWD1", 7200), P(@"\bHDWE1", 7200), P(@"\bHDWG", 7200), P(@"\bHDWL1", 5400),
            // Seagate
            P(@"\bST\d+DM00[0-3]", 7200), P(@"\bST\d+DM00[4-5]", 5400), P(@"\bST\d+LM021", 7200),
            P(@"\bST\d+LM0(12|14|15|24|30|35|48|49)", 5400), P(@"\bST\d+LT0\d\d", 5400), P(@"\bST\d+VX", 5900),
            P(@"\bST\d+NM", 7200), P(@"\bST\d+NE", 7200),
            // Western Digital
            P(@"\bWD\d+[A-Z]{0,2}(LPVX|LPCX|LPVT|BPVT|BPVX|BEVT|BEVS|LPSX|BMVV|BMVW|SPZX|SPRX|BPZX)", 5400),
            P(@"\bWD\d+[A-Z]{0,2}(LPLX|BEKT|BPKX|BEKX|BPKT|BHTZ)", 7200),
            P(@"\bWD\d+(EZRX|EZRZ|EFRX|EFZX|EZAZ|EFAX|EARX|EARS|EADS|EURX|PURZ|PURX)", 5400),
            P(@"\bWD\d+(EZEX|AAKX|FZEX|FZBX|FAEX|AZEX|AAKS|AALS|FAEX|EZEZ|FYYZ|FRYZ|FBYZ|FFWX|EFZZ)", 7200),
            // Samsung (legacy HDD)
            P(@"\bHN-M\d{3}MBB", 5400), P(@"\bHD\d{3}(LJ|HJ|KJ)", 7200), P(@"\bST\d+LM", 5400),
        };

        static KeyValuePair<string, int> P(string re, int rpm) { return new KeyValuePair<string, int>(re, rpm); }

        public static int FromModel(string model)
        {
            if (string.IsNullOrEmpty(model)) return 0;
            string m = model.ToUpperInvariant();
            foreach (var p in Patterns)
                if (Regex.IsMatch(m, p.Key)) return p.Value;
            return 0;
        }
    }
}
