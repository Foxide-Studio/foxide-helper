// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
//
// Register maps (chip IDs, fan tachometer / PWM / extended PWM registers, per-chip channel counts) are taken from
// LibreHardwareMonitor, Hardware/Motherboard/Lpc/LpcIO.cs and IT87XX.cs:
// Copyright (C) LibreHardwareMonitor and Contributors.
// Partial Copyright (C) Michael Möller <mmoeller@openhardwaremonitor.org> and Contributors.
// Adapted for Foxide Helper (PawnIO LpcIO module, C# 5) by Foxide Studio.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace FoxideDisk
{
    /// <summary>ITE IT86xx / IT87xx environment controller (through base+5/base+6).</summary>
    internal sealed class IteChip : BoardChip
    {
        const int MutexWait = 300;
        const int EcLdn = 0x04;
        const int MainCtrl = 0x13;

        static readonly int[] TachLo = { 0x0d, 0x0e, 0x0f, 0x80, 0x82, 0x4c };
        static readonly int[] TachHi = { 0x18, 0x19, 0x1a, 0x81, 0x83, 0x4d };
        static readonly int[] TachLoAlt = { 0x0d, 0x0e, 0x0f, 0x80, 0x82, 0x93 };
        static readonly int[] TachHiAlt = { 0x18, 0x19, 0x1a, 0x81, 0x83, 0x94 };
        static readonly int[] PwmExt = { 0x63, 0x6b, 0x73, 0x7b, 0xa3, 0xab };

        readonly LpcSlot s;
        readonly ushort addrReg, dataReg;
        readonly int chip;          // 0x8686 etc. (0x8733 = IT8792E, 0x8695 = IT87952E)
        readonly int[] pwmCtrl;
        readonly int fanCount, tempCount, pwmCount;
        readonly bool hasExt, altFan6, bankSelect, fan16;
        readonly bool[] fanOff;
        readonly bool controlOk;
        readonly byte[] savedCtrl, savedExt;
        readonly bool[] savedMain, saved;
        string lastNote;

        public override int FanCount { get { return fanCount; } }

        IteChip(LpcSlot slot, int id, string name, ushort addr, int version)
        {
            s = slot; chip = id; Name = name;
            addrReg = (ushort)(addr + 5); dataReg = (ushort)(addr + 6);
            pwmCtrl = id == 0x8665 || id == 0x8625 ? new[] { 0x15, 0x16, 0x17, 0x1e, 0x1f, 0x92 }
                : id == 0x8733 ? new[] { 0x15, 0x16, 0x17 }
                : new[] { 0x15, 0x16, 0x17, 0x7f, 0xa7, 0xaf };
            hasExt = Array.IndexOf(new[] { 0x8721, 0x8728, 0x8665, 0x8686, 0x8688, 0x8689, 0x8695, 0x8628, 0x8625, 0x8620,
                0x8613, 0x8733, 0x8655, 0x8631, 0x8638, 0x8696, 0x8772 }, id) >= 0;
            altFan6 = id == 0x8665 || id == 0x8625;
            bankSelect = id == 0x8665 || id == 0x8655;
            int f, t, p;
            switch (id)
            {
                case 0x8613: f = 5; t = 4; p = 4; break;
                case 0x8625: f = 6; t = 3; p = 6; break;
                case 0x8628: f = 6; t = 6; p = 6; break;
                case 0x8631: case 0x8638: f = 2; t = 2; p = 2; break;
                case 0x8665: f = 6; t = 6; p = 6; break;
                case 0x8686: f = 6; t = 7; p = 5; break;
                case 0x8688: f = 6; t = 6; p = 5; break;
                case 0x8689: case 0x8696: f = 6; t = 6; p = 6; break;
                case 0x8695: case 0x8733: case 0x8705: f = 3; t = 3; p = 3; break;
                case 0x8655: f = 3; t = 6; p = 3; break;
                case 0x8620: f = 5; t = 3; p = 5; break;
                default: f = 5; t = 3; p = 3; break;
            }
            fanCount = f; tempCount = t; pwmCount = Math.Min(p, pwmCtrl.Length);
            fan16 = (id != 0x8705 || version >= 3) && (id != 0x8712 || version >= 8);
            fanOff = new bool[fanCount];
            // The second ITE chip of Gigabyte boards (IT8790E/IT8792E/IT87952E at 0x4E) is driven by the board EC;
            // LibreHardwareMonitor has to switch that EC off to control it — we only read it.
            controlOk = !(slot.RegPort == 0x4E && (id == 0x8790 || id == 0x8733 || id == 0x8695));
            savedCtrl = new byte[pwmCount]; savedExt = new byte[pwmCount]; savedMain = new bool[pwmCount]; saved = new bool[pwmCount];
        }

        static string ChipName(int id)
        {
            switch (id)
            {
                case 0x8613: return "IT8613E"; case 0x8620: return "IT8620E"; case 0x8625: return "IT8625E";
                case 0x8628: return "IT8628E"; case 0x8631: return "IT8631E"; case 0x8638: return "IT8638E";
                case 0x8655: return "IT8655E"; case 0x8665: return "IT8665E"; case 0x8686: return "IT8686E";
                case 0x8688: return "IT8688E"; case 0x8689: return "IT8689E"; case 0x8696: return "IT8696E";
                case 0x8705: return "IT8705F"; case 0x8712: return "IT8712F"; case 0x8716: return "IT8716F";
                case 0x8718: return "IT8718F"; case 0x8720: return "IT8720F"; case 0x8721: return "IT8721F";
                case 0x8726: return "IT8726F"; case 0x8728: return "IT8728F"; case 0x8771: return "IT8771E";
                case 0x8772: return "IT8772E"; case 0x8790: return "IT8790E"; case 0x8733: return "IT8792E";
                case 0x8695: return "IT87952E";
            }
            return null;
        }

        // ------------------------------------------------------------------------------------------------ detection
        /// <summary>Called with the ISA mutex held.</summary>
        public static BoardChip TryDetect(LpcSlot s, out string note)
        {
            note = null;
            int id = 0xFFFF;
            // At 0x4E a chip that answers before the enter sequence is already in configuration mode; entering it
            // again makes an IT8792E report a wrong ID (LibreHardwareMonitor).
            if (s.RegPort == 0x4E) id = s.SioInw(0x20);
            if (s.RegPort != 0x4E || id == 0xFFFF) { s.IteEnter(); id = s.SioInw(0x20); }
            string name = ChipName(id);
            if (name == null)
            {
                if (id != 0 && id != 0xFFFF)
                {
                    s.IteExit();
                    note = "ITE chip 0x" + id.ToString("X4") + " is not supported";
                }
                return null;
            }
            s.FindBars();
            s.Select(EcLdn);
            ushort addr = s.SioInw(0x60);
            Thread.Sleep(1);
            ushort verify = s.SioInw(0x60);
            int version = s.SioIn(0x22) & 0x0F;
            s.IteExit();
            if (addr != verify || addr < 0x100 || (addr & 0xF007) != 0)
            {
                note = "ITE " + name + ": invalid environment controller address 0x" + addr.ToString("X4");
                return null;
            }
            var c = new IteChip(s, id, "ITE " + name, addr, version);
            c.lastNote = "id 0x" + id.ToString("X4") + " ver " + version + " base 0x" + addr.ToString("X4");
            string why;
            if (!c.Init(out why)) { note = "ITE " + name + ": " + why; return null; }
            return c;
        }

        bool Init(out string why)
        {
            why = null;
            bool valid;
            int vendor = Rd(0x58, out valid);
            if (!valid || (vendor != 0x90 && vendor != 0x7F)) { why = "vendor ID 0x" + vendor.ToString("X2"); return false; }
            int cfg = Rd(0x00, out valid);
            if (!valid || ((cfg & 0x10) == 0 && chip != 0x8655 && chip != 0x8665)) { why = "monitoring is not started"; return false; }
            if (fan16)
            {
                int modes = Rd(0x0C, out valid);
                if (!valid) { why = "fan mode register"; return false; }
                if (fanCount >= 5) { fanOff[3] = (modes & 0x10) == 0; fanOff[4] = (modes & 0x20) == 0; }
                if (fanCount >= 6)
                {
                    if (chip == 0x8665) { int m2 = Rd(0x0B, out valid); if (valid) fanOff[5] = (m2 & 0x08) == 0; }
                    else fanOff[5] = (modes & 0x04) == 0;
                }
            }
            return true;
        }

        // ------------------------------------------------------------------------------------------------ register access (mutex held)
        byte Rd(int reg, out bool valid)
        {
            s.Outb(addrReg, reg);
            byte v = s.Inb(dataReg);
            valid = s.Inb(addrReg) == reg || chip == 0x8688;
            return v;
        }

        void Wr(int reg, int value)
        {
            s.Outb(addrReg, reg);
            s.Outb(dataReg, value);
            s.Inb(addrReg);
        }

        void Bank0()
        {
            if (!bankSelect) return;
            bool valid;
            int v = Rd(0x06, out valid);
            if (valid && (v & 0x60) != 0) Wr(0x06, v & 0x9F);
        }

        bool Locked(Action a) { return PawnIo.WithMutex(PawnIo.IsaBusMutex, MutexWait, a); }

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
                    Bank0();
                    bool valid;
                    for (int i = 0; i < tempCount; i++)
                    {
                        int v = (sbyte)Rd(0x29 + i, out valid);
                        if (valid && v > 0 && v < 127)
                            nv.Add(new BoardValue { Name = L.F("Датчик платы {0}", i + 1), Value = v, Kind = "temp" });
                    }
                    for (int i = 0; i < fanCount; i++)
                    {
                        int? rpm = null;
                        if (fan16)
                        {
                            if (!fanOff[i])
                            {
                                bool v1, v2;
                                int lo = Rd(altFan6 ? TachLoAlt[i] : TachLo[i], out v1);
                                int hi = Rd(altFan6 ? TachHiAlt[i] : TachHi[i], out v2);
                                int count = lo | (hi << 8);
                                if (v1 && v2 && count > 0x3F) rpm = count < 0xFFFF ? (int)Math.Round(1.35e6 / (count * 2)) : 0;
                            }
                        }
                        else
                        {
                            int count = Rd(TachLo[i], out valid);
                            if (valid && count > 0)
                            {
                                int div = 2;
                                if (i < 2)
                                {
                                    int d = Rd(0x0B, out valid);
                                    div = 1 << ((d >> (3 * i)) & 7);
                                }
                                if (valid) rpm = count < 0xFF ? (int)Math.Round(1.35e6 / (count * div)) : 0;
                            }
                        }
                        var f = new BoardFan { Index = FirstIndex + i, Name = FanName(i + 1), Rpm = rpm };
                        if (i < pwmCount)
                        {
                            int ctrl = Rd(pwmCtrl[i], out valid);
                            if (valid && (ctrl & 0x80) == 0)
                            {
                                if (hasExt) { int e = Rd(PwmExt[i], out valid); if (valid) f.Percent = Math.Round(e * 100.0 / 255.0, 1); }
                                else f.Percent = Math.Round((ctrl & 0x7F) * 100.0 / 127.0, 1);
                            }
                            f.CanControl = controlOk;
                            f.Manual = saved[i];
                        }
                        nf.Add(f);
                    }
                }
                catch (Exception ex) { err = ex; }
            });
            if (!got || err != null) { if (err != null) lastNote = err.Message; return; }
            Fans = nf;
            Values = nv;
        }

        // ------------------------------------------------------------------------------------------------ control
        public override bool SetPercent(int i, int percent, out string why)
        {
            why = null;
            if (i < 0 || i >= pwmCount || !controlOk) { why = L.T("Этот канал не управляется."); return false; }
            int value = (int)Math.Round(percent * 255.0 / 100.0);
            Exception err = null;
            bool got = Locked(delegate
            {
                try
                {
                    bool valid;
                    Bank0();
                    if (!saved[i])
                    {
                        savedCtrl[i] = Rd(pwmCtrl[i], out valid);
                        if (i < 3) savedMain[i] = (Rd(MainCtrl, out valid) & (1 << i)) != 0;
                        if (hasExt) savedExt[i] = Rd(PwmExt[i], out valid);
                        saved[i] = true;
                    }
                    if (i < 3 && !savedMain[i]) Wr(MainCtrl, Rd(MainCtrl, out valid) | (1 << i));
                    if (hasExt)
                    {
                        Wr(pwmCtrl[i], chip == 0x8689 ? 0x7F : savedCtrl[i] & 0x7F);
                        Wr(PwmExt[i], value);
                    }
                    else Wr(pwmCtrl[i], value >> 1);
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
                    bool valid;
                    Bank0();
                    Wr(pwmCtrl[i], savedCtrl[i]);
                    if (i < 3)
                    {
                        int m = Rd(MainCtrl, out valid);
                        if (((m & (1 << i)) != 0) != savedMain[i]) Wr(MainCtrl, m ^ (1 << i));
                    }
                    if (hasExt) Wr(PwmExt[i], savedExt[i]);
                    ok = Rd(pwmCtrl[i], out valid) == savedCtrl[i];
                    if (ok) saved[i] = false;
                }
                catch (Exception ex) { err = ex; }
            });
            if (!got) { why = L.T("Шина занята другой программой, попробуйте ещё раз."); return false; }
            if (err != null) { why = err.Message; return false; }
            if (!ok) why = L.T("Чип не принял прежний режим вентилятора.");
            return ok;
        }

        public string PwmState(int i)
        {
            string r = null;
            Locked(delegate
            {
                try
                {
                    bool v;
                    r = "ctrl=0x" + Rd(pwmCtrl[i], out v).ToString("X2") + (hasExt ? " ext=0x" + Rd(PwmExt[i], out v).ToString("X2") : "")
                        + " main=0x" + Rd(MainCtrl, out v).ToString("X2");
                }
                catch (Exception ex) { r = ex.Message; }
            });
            return r ?? "mutex busy";
        }

        public override string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Name + " slot " + s.Slot + " " + lastNote + (controlOk ? "" : " (read-only)"));
            for (int i = 0; i < pwmCount; i++) sb.AppendLine("  pwm" + (i + 1) + " " + PwmState(i));
            return sb.ToString();
        }

        public override void Close() { s.Dispose(); }
    }
}
