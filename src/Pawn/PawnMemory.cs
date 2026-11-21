using System;
using System.Collections.Generic;

namespace FoxideDisk
{
    /// <summary>One memory module temperature sensor found on the SMBus.</summary>
    public class DimmTemp
    {
        public int Address;      // 7-bit SMBus address (0x18–0x1F for DDR4 TS, 0x50–0x57 for a DDR5 SPD hub)
        public string Name;      // "Модуль памяти N" (localized)
        public double TempC;
        public string Type;      // "DDR4 TS" | "DDR5 SPD hub"
    }

    /// <summary>
    /// Memory module (DIMM / SODIMM) temperatures over the chipset SMBus through the PawnIO SmbusI801 (Intel PCH)
    /// or SmbusPIIX4 (AMD FCH) module. READ-ONLY: only SMBus read transactions (byte data / word data reads) are
    /// issued — no quick-write probes, no SPD page selects, no register writes. Register maps follow the JEDEC
    /// documents (TSE2004av for DDR4 thermal sensors, SPD5118 for the DDR5 SPD hub).
    /// Every transaction sequence holds the shared "Access_SMBUS.HTP.Method" mutex (HWiNFO, LibreHardwareMonitor…).
    /// </summary>
    public sealed class PawnMemory : IDisposable
    {
        const ulong SMBUS_READ = 1;
        const ulong PROTO_BYTE_DATA = 2, PROTO_WORD_DATA = 3;
        const int MutexWaitMs = 500;

        PawnModule mod;
        readonly object sync = new object();
        readonly List<DimmTemp> sensors = new List<DimmTemp>();
        List<DimmTemp> last = new List<DimmTemp>();
        bool piix4;
        int spdSeen;              // devices that answered at 0x50–0x57 during the scan (SPD EEPROMs / hubs)
        int port = -1;            // PIIX4 port the DIMMs answered on (0/2/3/4 = primary base 0xB00 mux, 1 = aux base 0xB20)

        /// <summary>Human-readable controller, e.g. "Intel i801 (PCH)", "AMD PIIX4/FCH (port 0)".</summary>
        public string Controller;

        PawnMemory() { }

        /// <summary>Find the SMBus controller and the module temperature sensors. Null + reason when there is nothing to read.</summary>
        public static PawnMemory Open(out string why)
        {
            why = null;
            try
            {
                string drv = PawnIo.Problem();
                if (drv != null) { why = drv; return null; }

                string e1, e2;
                var m = PawnModule.Load("SmbusI801", out e1);
                bool isPiix4 = false;
                if (m == null)
                {
                    m = PawnModule.Load("SmbusPIIX4", out e2);
                    isPiix4 = m != null;
                    if (m == null) { why = L.T("Контроллер SMBus не поддерживается (нужен Intel PCH или AMD FCH)."); return null; }
                }
                var pm = new PawnMemory { mod = m, piix4 = isPiix4 };
                string ctl = pm.Identity();
                bool any = false;
                bool locked = PawnIo.WithMutex(PawnIo.SmbusMutex, 3000, delegate { any = pm.Scan(); });
                if (!locked) { pm.Dispose(); why = L.T("Шина SMBus занята другой программой."); return null; }
                if (!any)
                {
                    pm.Dispose();
                    why = pm.spdSeen > 0
                        ? L.F("Модулей памяти на шине: {0}, но датчика температуры у них нет (у многих модулей его нет).", pm.spdSeen)
                        : L.T("Датчиков температуры на модулях памяти не найдено (у многих модулей их нет).");
                    return null;
                }
                pm.Controller = ctl + (isPiix4 ? " (" + L.F("порт {0}", pm.port) + ")" : "");
                return pm;
            }
            catch (Exception ex) { why = ex.Message; return null; }
        }

        string Identity()
        {
            string name = piix4 ? "AMD PIIX4/FCH" : "Intel i801 (PCH)";
            try
            {
                var r = mod.Execute("ioctl_identity", null, 3);
                if (r != null && r.Length == 3)
                {
                    uint ids = (uint)r[2];
                    name += " [" + (ids & 0xFFFF).ToString("X4") + ":" + (ids >> 16).ToString("X4") + "]";
                }
            }
            catch { }
            return name;
        }

        // ---------------------------------------------------------------- SMBus primitives (read only)
        int ReadByte(int addr, int reg)
        {
            int err;
            var r = mod.Execute("ioctl_smbus_xfer", new ulong[] { (ulong)addr, SMBUS_READ, (ulong)reg, PROTO_BYTE_DATA }, 1, out err);
            return r != null && r.Length == 1 ? (int)(r[0] & 0xFF) : -1;
        }

        /// <summary>SMBus word read: the first byte on the wire is returned in bits 7:0.</summary>
        int ReadWord(int addr, int reg)
        {
            int err;
            var r = mod.Execute("ioctl_smbus_xfer", new ulong[] { (ulong)addr, SMBUS_READ, (ulong)reg, PROTO_WORD_DATA }, 1, out err);
            return r != null && r.Length == 1 ? (int)(r[0] & 0xFFFF) : -1;
        }

        /// <summary>JC42 registers are big-endian on the wire.</summary>
        int ReadWordBE(int addr, int reg)
        {
            int v = ReadWord(addr, reg);
            return v < 0 ? -1 : ((v & 0xFF) << 8) | (v >> 8);
        }

        /// <summary>PIIX4 port mux. -1 = query only. Returns previous port or -2 on failure. Must hold the SMBus mutex.</summary>
        int PortSel(int p)
        {
            var r = mod.Execute("ioctl_piix4_port_sel", new ulong[] { unchecked((ulong)(long)p) }, 1);
            return r != null && r.Length == 1 ? (int)(long)r[0] : -2;
        }

        // ---------------------------------------------------------------- sensors
        static double Temp13(int raw)   // bits 12:0, two's complement, 0.0625 °C
        {
            raw &= 0x1FFF;
            if ((raw & 0x1000) != 0) raw -= 0x2000;
            return raw * 0.0625;
        }

        double? ReadDdr4(int addr)
        {
            int v = ReadWordBE(addr, 0x05);
            if (v < 0) v = ReadWordBE(addr, 0x05);
            if (v < 0) return null;
            return Temp13(v);
        }

        double? ReadDdr5(int addr)
        {
            int v = ReadWord(addr, 0x31);            // MR49 = low byte, MR50 = high byte
            if (v < 0) v = ReadWord(addr, 0x31);
            if (v < 0) return null;
            return Temp13(v & 0x1FFC);               // bits 1:0 are reserved, resolution 0.25 °C
        }

        static bool Plausible(double t) { return t > -40 && t < 125 && t != 0; }

        /// <summary>DDR4: JEDEC TSE2004 thermal sensor at 0x18–0x1F.</summary>
        void ScanDdr4(List<DimmTemp> found)
        {
            for (int n = 0; n < 8; n++)
            {
                int a = 0x18 + n;
                int cap = ReadWordBE(a, 0x00);
                if (cap < 0) continue;                                   // NAK — no sensor
                int cfg = ReadWordBE(a, 0x01);
                int man = ReadWordBE(a, 0x06);
                if (cfg < 0 || man < 0) continue;
                if ((cap & 0xFF00) != 0 || (cfg & 0xF800) != 0) continue; // reserved bits must read 0
                if ((cap & 0x01) == 0) continue;                         // "has temperature alarm" — mandatory for JC42
                if (man == 0 || man == 0xFFFF) continue;
                if ((cfg & 0x0100) != 0) continue;                       // SHDN: sensor is shut down
                var t = ReadDdr4(a);
                if (!t.HasValue || !Plausible(t.Value)) continue;
                found.Add(new DimmTemp { Address = a, Name = L.T("Модуль памяти") + " " + (n + 1), TempC = t.Value, Type = "DDR4 TS" });
            }
        }

        /// <summary>DDR5: SPD5118 hub at 0x50–0x57, read in its default 1-byte addressing mode.</summary>
        void ScanDdr5(List<DimmTemp> found)
        {
            for (int n = 0; n < 8; n++)
            {
                int a = 0x50 + n;
                int mr0 = ReadByte(a, 0x00);
                if (mr0 < 0) continue;                       // NAK — empty slot
                spdSeen = Math.Max(spdSeen, CountSeen(n));
                int mr1 = ReadByte(a, 0x01);
                if (mr0 != 0x51 || mr1 != 0x18) continue;     // not an SPD5118 hub with thermal sensor (e.g. DDR4 SPD EEPROM)
                int mr11 = ReadByte(a, 0x0B);
                if (mr11 < 0 || (mr11 & 0x08) != 0) continue; // 2-byte addressing mode: would need a write to read — skip
                int mr26 = ReadByte(a, 0x1A);
                if (mr26 < 0 || (mr26 & 0x01) != 0) continue; // TS disabled
                var t = ReadDdr5(a);
                if (!t.HasValue || !Plausible(t.Value)) continue;
                found.Add(new DimmTemp { Address = a, Name = L.T("Модуль памяти") + " " + (n + 1), TempC = t.Value, Type = "DDR5 SPD hub" });
            }
        }

        int seenMask;
        int CountSeen(int n)
        {
            seenMask |= 1 << n;
            int c = 0;
            for (int i = 0; i < 8; i++) if ((seenMask & (1 << i)) != 0) c++;
            return c;
        }

        bool ScanBus(List<DimmTemp> found)
        {
            found.Clear();
            ScanDdr4(found);
            ScanDdr5(found);
            return found.Count > 0;
        }

        /// <summary>Called with the SMBus mutex held.</summary>
        bool Scan()
        {
            var found = new List<DimmTemp>();
            if (!piix4)
            {
                if (!ScanBus(found)) return false;
            }
            else
            {
                // The module starts on the primary base (0xB00). Query the current mux port without changing it.
                int orig = PortSel(-1);
                if (orig < 0) orig = 0;
                var tryPorts = new List<int> { orig, 1 };
                if (orig != 0) tryPorts.Add(0);
                bool ok = false;
                foreach (int p in tryPorts)
                {
                    if (PortSel(p) == -2) continue;
                    if (ScanBus(found)) { port = p; ok = true; break; }
                }
                // Leave the chipset mux the way we found it (port 1 is a separate base and never touched the mux).
                if (!ok || port != 1) PortSel(orig);
                if (!ok) return false;
            }
            sensors.Clear();
            sensors.AddRange(found);
            last = Copy(found);
            return true;
        }

        static List<DimmTemp> Copy(List<DimmTemp> src)
        {
            var r = new List<DimmTemp>();
            foreach (var d in src) r.Add(new DimmTemp { Address = d.Address, Name = d.Name, TempC = d.TempC, Type = d.Type });
            return r;
        }

        /// <summary>Current temperatures of the sensors found by Open(). Never throws; returns the previous values
        /// if the bus is busy and an empty list after Dispose.</summary>
        public List<DimmTemp> Read()
        {
            try
            {
                lock (sync)
                {
                    if (mod == null) return new List<DimmTemp>();
                    var res = new List<DimmTemp>();
                    bool locked = PawnIo.WithMutex(PawnIo.SmbusMutex, MutexWaitMs, delegate
                    {
                        int prev = -1;
                        if (piix4)
                        {
                            prev = PortSel(port);
                            if (prev == -2) return;
                        }
                        try
                        {
                            foreach (var s in sensors)
                            {
                                double? t = s.Type == "DDR4 TS" ? ReadDdr4(s.Address) : ReadDdr5(s.Address);
                                if (t.HasValue && t.Value > -40 && t.Value < 125)
                                    res.Add(new DimmTemp { Address = s.Address, Name = s.Name, TempC = t.Value, Type = s.Type });
                            }
                        }
                        finally
                        {
                            // Put the primary mux back if someone else had it on another port.
                            if (piix4 && port != 1 && prev >= 0 && prev != 1 && prev != port) PortSel(prev);
                        }
                    });
                    if (!locked) return Copy(last);
                    last = res;
                    return Copy(res);
                }
            }
            catch { return new List<DimmTemp>(); }
        }

        public void Dispose()
        {
            try
            {
                lock (sync)
                {
                    if (mod != null) { mod.Dispose(); mod = null; }
                }
            }
            catch { }
        }
    }
}
