// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
//
// Register maps (chip IDs, fan count / PWM / mode registers, temperature and source registers) are taken from
// LibreHardwareMonitor, Hardware/Motherboard/Lpc/LpcIO.cs and Nct677X.cs:
// Copyright (C) LibreHardwareMonitor and Contributors.
// Partial Copyright (C) Michael Möller <mmoeller@openhardwaremonitor.org> and Contributors.
// Adapted for Foxide Helper (PawnIO LpcIO module, C# 5) by Foxide Studio.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace FoxideDisk
{
    /// <summary>Nuvoton NCT6775F/6776F/6779D/6791D…6799D hardware monitor (bank-switched, through base+5/base+6).</summary>
    internal sealed class NuvotonChip : BoardChip
    {
        const int MutexWait = 300;
        const int AddrOff = 5, DataOff = 6, BankSel = 0x4E;
        const int HwmLdn = 0x0B;

        readonly LpcSlot s;
        readonly ushort baseAddr;
        readonly bool old;          // NCT6775F / NCT6776F (16-bit RPM registers, other temperature sources)
        readonly bool hasLock;      // NCT679x: hardware-monitor I/O space lock
        readonly int fanCount, pwmCount;
        readonly int[] pwmOut, pwmCmd, pwmMode;
        readonly int[][] temps;     // { reg, halfReg, halfBit, srcReg }
        readonly int[][] alts;      // { source, reg } fixed integer registers
        readonly int minRpm16;
        readonly byte[] savedMode, savedCmd;
        readonly bool[] saved;
        int bank = -1;
        string lastNote;

        public override int FanCount { get { return fanCount; } }

        NuvotonChip(LpcSlot slot, string name, ushort addr, int id)
        {
            s = slot; Name = name; baseAddr = addr;
            old = id == 6775 || id == 6776;
            hasLock = id >= 6791;
            pwmCmd = new[] { 0x109, 0x209, 0x309, 0x809, 0x909, 0xA09, 0xB09 };
            pwmMode = new[] { 0x102, 0x202, 0x302, 0x802, 0x902, 0xA02, 0xB02 };
            pwmOut = id == 6797 || id == 6798 || id == 6799
                ? new[] { 0x001, 0x003, 0x011, 0x013, 0x015, 0xA09, 0xB09 }
                : new[] { 0x001, 0x003, 0x011, 0x013, 0x015, 0x017, 0x029 };
            var t = new List<int[]>();
            var a = new List<int[]>();
            if (old)
            {
                fanCount = id == 6775 ? 4 : 5;
                pwmCount = 3;
                minRpm16 = id == 6775 ? (int)(1.35e6 / 0xFFFF) : (int)(1.35e6 / 0x1FFF);
                t.Add(new[] { 0x027, 0, -1, 0x621 });
                t.Add(new[] { 0x073, 0x074, 7, 0x100 });
                t.Add(new[] { 0x075, 0x076, 7, 0x200 });
                t.Add(new[] { 0x077, 0x078, 7, 0x300 });
                t.Add(new[] { 0x150, 0x151, 7, 0x622 });
                t.Add(new[] { 0x250, 0x251, 7, 0x623 });
            }
            else
            {
                fanCount = id == 6779 ? 5 : (id >= 6797 ? 7 : 6);   // 16796 = NCT6796D-R
                pwmCount = fanCount;
                t.Add(new[] { 0x073, 0x074, 7, 0x100 });
                t.Add(new[] { 0x075, 0x076, 7, 0x200 });
                t.Add(new[] { 0x077, 0x078, 7, 0x300 });
                t.Add(new[] { 0x079, 0x07A, 7, 0x800 });
                t.Add(new[] { 0x07B, 0x07C, 7, 0x900 });
                if (id != 6779)
                {
                    t.Add(new[] { 0x07D, 0x07E, 7, 0xA00 });
                    t.Add(new[] { 0x4A0, 0x49E, 6, 0xB00 });
                }
                if (id == 6798 || id == 6799) t.Add(new[] { 0x4A2, 0x4A1, 7, 0xC00 });
                t.Add(new[] { 0x027, 0, -1, 0x621 });
                t.Add(new[] { 0x150, 0x151, 7, 0x622 });
                if (id != 6779)
                {
                    t.Add(new[] { 0x670, 0, -1, 0xC26 });
                    t.Add(new[] { 0x672, 0, -1, 0xC27 });
                    t.Add(new[] { 0x674, 0, -1, 0xC28 });
                    t.Add(new[] { 0x676, 0, -1, 0xC29 });
                    t.Add(new[] { 0x678, 0, -1, 0xC2A });
                    t.Add(new[] { 0x67A, 0, -1, 0xC2B });
                }
                // fixed registers (integer °C) for sources not selected into a monitoring register
                a.Add(new[] { 1, 0x490 }); a.Add(new[] { 2, 0x491 });
                a.Add(new[] { 3, 0x492 }); a.Add(new[] { 4, 0x493 }); a.Add(new[] { 5, 0x494 }); a.Add(new[] { 6, 0x495 });
                if (id == 6798 || id == 6799) a.Add(new[] { 10, 0x496 });
                if (id != 6779)
                {
                    a.Add(new[] { 18, 0x400 }); a.Add(new[] { 19, 0x401 }); a.Add(new[] { 20, 0x402 }); a.Add(new[] { 21, 0x404 });
                    a.Add(new[] { 22, 0x405 }); a.Add(new[] { 23, 0x406 }); a.Add(new[] { 24, 0x407 }); a.Add(new[] { 25, 0x408 });
                }
            }
            if (pwmCount > fanCount) pwmCount = fanCount;
            temps = t.ToArray();
            alts = a.ToArray();
            savedMode = new byte[pwmCount]; savedCmd = new byte[pwmCount]; saved = new bool[pwmCount];
        }

        // ------------------------------------------------------------------------------------------------ detection
        /// <summary>Called with the ISA mutex held. Null when the slot has no supported Nuvoton chip (note says why).</summary>
        public static BoardChip TryDetect(LpcSlot s, out string note)
        {
            note = null;
            s.NuvotonEnter();
            int id = s.SioIn(0x20), rev = s.SioIn(0x21);
            int model = 0; string name = null;
            switch (id)
            {
                case 0xB4: if ((rev & 0xF0) == 0x70) { model = 6775; name = "NCT6775F"; } break;
                case 0xC3: if ((rev & 0xF0) == 0x30) { model = 6776; name = "NCT6776F"; } break;
                case 0xC5: if ((rev & 0xF0) == 0x60) { model = 6779; name = "NCT6779D"; } break;
                case 0xC8: if (rev == 0x03) { model = 6791; name = "NCT6791D"; } break;
                case 0xC9: if (rev == 0x11 || rev == 0x13) { model = 6792; name = rev == 0x13 ? "NCT6792D-A" : "NCT6792D"; } break;
                case 0xD1: if (rev == 0x21) { model = 6793; name = "NCT6793D"; } break;
                case 0xD3: if (rev == 0x52) { model = 6795; name = "NCT6795D"; } break;
                case 0xD4:
                    if (rev == 0x23) { model = 6796; name = "NCT6796D"; }
                    else if (rev == 0x2A) { model = 16796; name = "NCT6796D-R"; }
                    else if (rev == 0x51) { model = 6797; name = "NCT6797D"; }
                    else if (rev == 0x2B) { model = 6798; name = "NCT6798D"; }
                    break;
                case 0xD8: if (rev == 0x02) { model = 6799; name = "NCT6799D"; } break;
            }
            if (model == 0)
            {
                if (id != 0 && id != 0xFF)
                {
                    s.NuvotonExit();
                    note = "Winbond/Nuvoton/Fintek chip 0x" + ((id << 8) | rev).ToString("X4") + " is not supported";
                }
                return null;
            }
            s.FindBars();
            s.Select(HwmLdn);
            ushort addr = s.SioInw(0x60);
            Thread.Sleep(1);
            ushort verify = s.SioInw(0x60);
            bool lockCleared = false;
            if (addr == verify && model >= 6791) lockCleared = ClearIoLock(s);
            s.NuvotonExit();
            if (addr != verify || addr < 0x100 || (addr & 0xF007) != 0)
            {
                note = "Nuvoton " + name + ": invalid hardware monitor address 0x" + addr.ToString("X4");
                return null;
            }
            var c = new NuvotonChip(s, "Nuvoton " + name, addr, model);
            c.lastNote = "id 0x" + ((id << 8) | rev).ToString("X4") + " base 0x" + addr.ToString("X4") + (lockCleared ? " (I/O lock cleared)" : "");
            c.bank = -1;
            if (!c.VendorOk())
            {
                note = "Nuvoton " + name + ": vendor ID check failed";
                return null;
            }
            return c;
        }

        /// <summary>In configuration mode with the HWM LDN selected: clear the hardware-monitor I/O space lock (CR28 bit 4).</summary>
        static bool ClearIoLock(LpcSlot s)
        {
            int opt = s.SioIn(0x28);
            if ((opt & 0x10) == 0) return false;
            s.SioOut(0x28, opt & ~0x10);
            return true;
        }

        // ------------------------------------------------------------------------------------------------ register access (mutex held)
        byte Rd(int reg)
        {
            int b = (reg >> 8) & 0xFF;
            if (b != bank) { s.Outb(baseAddr + AddrOff, BankSel); s.Outb(baseAddr + DataOff, b); bank = b; }
            s.Outb(baseAddr + AddrOff, reg & 0xFF);
            return s.Inb(baseAddr + DataOff);
        }

        void Wr(int reg, int value)
        {
            int b = (reg >> 8) & 0xFF;
            if (b != bank) { s.Outb(baseAddr + AddrOff, BankSel); s.Outb(baseAddr + DataOff, b); bank = b; }
            s.Outb(baseAddr + AddrOff, reg & 0xFF);
            s.Outb(baseAddr + DataOff, value);
        }

        bool VendorOk()
        {
            return ((Rd(0x804F) << 8) | Rd(0x004F)) == 0x5CA3;
        }

        /// <summary>Run a register sequence holding the ISA mutex. The bank cache is reset, since another program may
        /// have switched the bank between our sequences.</summary>
        bool Locked(Action a)
        {
            return PawnIo.WithMutex(PawnIo.IsaBusMutex, MutexWait, delegate { bank = -1; a(); });
        }

        void EnsureUnlocked()
        {
            if (!hasLock || VendorOk()) return;
            // the BIOS re-enabled the lock (e.g. after sleep): clear it again in configuration mode
            s.NuvotonEnter();
            try { s.Select(HwmLdn); ClearIoLock(s); }
            finally { s.NuvotonExit(); }
            bank = -1;
        }

        // ------------------------------------------------------------------------------------------------ reading
        public override void Update()
        {
            var nf = new List<BoardFan>();
            var nv = new List<BoardValue>();
            Exception err = null;
            bool got = Locked(delegate
            {
                try
                {
                    EnsureUnlocked();
                    ReadFans(nf);
                    ReadTemps(nv);
                    if (!old) ReadVolts(nv);
                }
                catch (Exception ex) { err = ex; }
            });
            if (!got || err != null) { if (err != null) lastNote = err.Message; return; }
            Fans = nf;
            Values = nv;
        }

        void ReadFans(List<BoardFan> list)
        {
            int[] cnt = { 0x4B0, 0x4B2, 0x4B4, 0x4B6, 0x4B8, 0x4BA, 0x4CC };
            for (int i = 0; i < fanCount; i++)
            {
                int? rpm;
                if (old)
                {
                    int v = (Rd(0x656 + (i << 1)) << 8) | Rd(0x657 + (i << 1));
                    rpm = v > minRpm16 ? v : 0;
                }
                else
                {
                    int hi = Rd(cnt[i]), lo = Rd(cnt[i] + 1);
                    int count = (hi << 5) | (lo & 0x1F);
                    if (count >= 0x1FFF) rpm = 0;
                    else if (count >= 0x15) rpm = (int)Math.Round(1.35e6 / count);
                    else rpm = null;
                }
                var f = new BoardFan { Index = FirstIndex + i, Name = FanName(i + 1), Rpm = rpm };
                if (i < pwmCount)
                {
                    f.Percent = Math.Round(Rd(pwmOut[i]) * 100.0 / 255.0, 1);
                    f.CanControl = true;
                    f.Manual = saved[i];
                }
                list.Add(f);
            }
        }

        void ReadTemps(List<BoardValue> list)
        {
            var seen = new HashSet<int>();
            foreach (var t in temps)
            {
                int src = Rd(t[3]) & 0x1F;
                if (src == 0 || seen.Contains(src)) continue;
                int v = (sbyte)Rd(t[0]) << 1;
                if (t[2] >= 0) v |= (Rd(t[1]) >> t[2]) & 1;
                double c = v * 0.5;
                if (c > 125 || c < -55 || OpenInput(src, c)) continue;
                // the "seen" mark first: later registers for the same source have no fractional bit
                seen.Add(src);
                list.Add(new BoardValue { Name = SourceName(src), Value = c, Kind = "temp" });
            }
            foreach (var a in alts)
            {
                if (seen.Contains(a[0])) continue;
                int c = (sbyte)Rd(a[1]);
                if (c <= 0 || c > 125 || OpenInput(a[0], c)) continue;
                seen.Add(a[0]);
                list.Add(new BoardValue { Name = SourceName(a[0]), Value = c, Kind = "temp" });
            }
        }

        /// <summary>A thermistor input with nothing connected reads 100…127 °C (seen on ASUS H110M-K / B360M-K:
        /// 108–115 °C) — such readings are dropped.</summary>
        bool OpenInput(int src, double c)
        {
            bool thermistor = old ? src >= 1 && src <= 3 : (src >= 1 && src <= 7) || src == 10;
            return thermistor && c >= 100;
        }

        void ReadVolts(List<BoardValue> list)
        {
            AddVolt(list, L.T("Напряжение ядра процессора"), Rd(0x480) * 0.008);
            AddVolt(list, "+3.3 V", Rd(0x483) * 0.016);
            AddVolt(list, L.T("+3.3 V дежурное"), Rd(0x487) * 0.016);
            if ((Rd(0x005D) & 1) != 0) AddVolt(list, L.T("Батарейка CMOS"), Rd(0x488) * 0.016);
        }

        static void AddVolt(List<BoardValue> list, string name, double v)
        {
            if (v > 0) list.Add(new BoardValue { Name = name, Value = Math.Round(v, 3), Kind = "voltage" });
        }

        string SourceName(int src)
        {
            if (old)
            {
                switch (src)
                {
                    case 1: return L.T("Системная плата (SYSTIN)");
                    case 2: return L.T("Процессор, сокет (CPUTIN)");
                    case 3: return L.T("Доп. датчик (AUXTIN)");
                    case 12: return L.T("Процессор (PECI)");
                }
                return L.F("Датчик {0}", src);
            }
            switch (src)
            {
                case 1: return L.T("Системная плата (SYSTIN)");
                case 2: return L.T("Процессор, сокет (CPUTIN)");
                case 3: case 4: case 5: case 6: case 7: return L.F("Доп. датчик {0} (AUXTIN{0})", src - 3);
                case 8: return "SMBus 0";
                case 9: return "SMBus 1";
                case 10: return L.T("Термистор (TSENSOR)");
                case 16: return L.T("Процессор (PECI)");
                case 17: return L.T("Процессор 2 (PECI)");
                case 18: return L.T("Процессор, максимум (PCH)");
                case 19: return L.T("Чипсет (PCH)");
                case 20: return L.T("Процессор (PCH)");
                case 21: return "MCH (PCH)";
                case 26: case 27: return L.F("Датчик BYTE_TEMP{0}", src - 26);
                case 28: return L.T("Процессор (PECI, калибровка)");
                case 29: return L.T("Процессор 2 (PECI, калибровка)");
                case 31: return L.T("Виртуальный датчик");
                case 22: case 23: case 24: case 25: return L.F("Память DIMM {0}", src - 21);
            }
            return L.F("Датчик {0}", src);
        }

        // ------------------------------------------------------------------------------------------------ control
        public override bool SetPercent(int i, int percent, out string why)
        {
            why = null;
            if (i < 0 || i >= pwmCount) { why = L.T("Этот канал не управляется."); return false; }
            int value = (int)Math.Round(percent * 255.0 / 100.0);
            Exception err = null;
            bool got = Locked(delegate
            {
                try
                {
                    EnsureUnlocked();
                    if (!saved[i])
                    {
                        savedMode[i] = Rd(pwmMode[i]);
                        savedCmd[i] = Rd(pwmCmd[i]);
                        saved[i] = true;
                    }
                    Wr(pwmMode[i], 0);              // manual mode
                    Wr(pwmCmd[i], value);
                }
                catch (Exception ex) { err = ex; }
            });
            if (!got) { why = L.T("Шина занята другой программой, попробуйте ещё раз."); return false; }
            if (err != null) { why = err.Message; return false; }
            return true;
        }

        public override bool Restore(int i, out string why)
        {
            why = null;
            if (i < 0 || i >= pwmCount || !saved[i]) return true;
            Exception err = null;
            bool ok = false;
            bool got = Locked(delegate
            {
                try
                {
                    Wr(pwmMode[i], savedMode[i]);
                    Wr(pwmCmd[i], savedCmd[i]);
                    ok = Rd(pwmMode[i]) == savedMode[i];
                    if (ok) saved[i] = false;
                }
                catch (Exception ex) { err = ex; }
            });
            if (!got) { why = L.T("Шина занята другой программой, попробуйте ещё раз."); return false; }
            if (err != null) { why = err.Message; return false; }
            if (!ok) why = L.T("Чип не принял прежний режим вентилятора.");
            return ok;
        }

        /// <summary>For tests: mode / command / output registers of a channel.</summary>
        public string PwmState(int i)
        {
            string r = null;
            Locked(delegate
            {
                try { r = "mode=0x" + Rd(pwmMode[i]).ToString("X2") + " cmd=0x" + Rd(pwmCmd[i]).ToString("X2") + " out=0x" + Rd(pwmOut[i]).ToString("X2"); }
                catch (Exception ex) { r = ex.Message; }
            });
            return r ?? "mutex busy";
        }

        public override string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Name + " slot " + s.Slot + " " + lastNote);
            for (int i = 0; i < pwmCount; i++) sb.AppendLine("  pwm" + (i + 1) + " " + PwmState(i));
            Locked(delegate
            {
                try
                {
                    sb.Append("  temp sources:");
                    foreach (var t in temps) sb.Append(" " + t[0].ToString("X3") + "<" + (Rd(t[3]) & 0x1F) + ">=" + (sbyte)Rd(t[0]));
                    sb.AppendLine();
                    sb.Append("  fixed:");
                    foreach (var a in alts) sb.Append(" " + a[1].ToString("X3") + "=" + (sbyte)Rd(a[1]));
                    sb.AppendLine();
                }
                catch (Exception ex) { sb.AppendLine(ex.Message); }
            });
            return sb.ToString();
        }

        public override void Close() { s.Dispose(); }
    }
}
