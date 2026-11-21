using System;
using System.Collections.Generic;
using System.IO;

namespace FoxideDisk
{
    /// <summary>One fan channel of the motherboard chip (or a Dell laptop fan).</summary>
    public class BoardFan
    {
        public int Index;            // global index for SetFanPercent / RestoreFan
        public string Name;
        public int? Rpm;             // null — the channel has no tachometer / reading is invalid
        public double? Percent;      // current PWM duty (0..100) or the Dell speed level as a percentage
        public bool CanControl;      // the program can set a manual PWM on this channel
        public bool Manual;          // the program has put the channel into manual mode
    }

    /// <summary>A temperature (°C, Kind = "temp") or a voltage (V, Kind = "voltage") of the motherboard chip.</summary>
    public class BoardValue
    {
        public string Name;
        public double Value;
        public string Kind;
    }

    /// <summary>
    /// Motherboard Super I/O chip (Nuvoton NCT67xx, ITE IT87xx) through the PawnIO LpcIO module, or Dell laptop fans
    /// through the DellSMM module: fan RPM, PWM, temperatures, manual PWM with restore of the chip's own control mode.
    /// Every access sequence to the ISA bus is done while holding the standard global mutex shared with
    /// LibreHardwareMonitor / HWiNFO / FanControl.
    /// </summary>
    public sealed class PawnBoard : IDisposable
    {
        public string ChipName;
        readonly List<BoardChip> chips = new List<BoardChip>();
        readonly object sync = new object();
        List<BoardFan> fans = new List<BoardFan>();
        List<BoardValue> values = new List<BoardValue>();
        bool disposed;
        EventHandler exitHook;

        public List<BoardFan> Fans { get { lock (sync) return new List<BoardFan>(fans); } }
        public List<BoardValue> Values { get { lock (sync) return new List<BoardValue>(values); } }

        PawnBoard() { }

        /// <summary>Find a supported chip. Null with a reason when there is none or the driver can't be used.</summary>
        public static PawnBoard Open(out string why)
        {
            why = null;
            try
            {
                string problem = PawnIo.Problem();
                if (problem != null) { why = problem; return null; }
                var b = new PawnBoard();
                var notes = new List<string>();
                for (int slot = 0; slot < 2; slot++)
                {
                    string note;
                    BoardChip c = SuperIoProbe.Detect(slot, out note);
                    if (c != null) b.chips.Add(c);
                    else if (note != null) notes.Add(note);
                }
                if (b.chips.Count == 0 && DellSmm.LooksLikeDell())
                {
                    string note;
                    BoardChip d = DellSmm.Open(out note);
                    if (d != null) b.chips.Add(d);
                    else if (note != null) notes.Add(note);
                }
                if (b.chips.Count == 0)
                {
                    why = L.T("Поддерживаемый чип мониторинга материнской платы не найден.");
                    if (notes.Count > 0) why += " (" + string.Join("; ", notes.ToArray()) + ")";
                    return null;
                }
                var names = new List<string>();
                foreach (var c in b.chips) names.Add(c.Name);
                b.ChipName = string.Join(" + ", names.ToArray());
                int idx = 0;
                foreach (var c in b.chips) { c.FirstIndex = idx; idx += c.FanCount; }
                b.exitHook = delegate { try { b.RestoreAll(); } catch { } };
                try { AppDomain.CurrentDomain.ProcessExit += b.exitHook; } catch { }
                b.Update();
                return b;
            }
            catch (Exception ex)
            {
                why = ex.Message;
                return null;
            }
        }

        /// <summary>Read all chips. Never throws; on a failed read the previous values stay.</summary>
        public void Update()
        {
            lock (sync)
            {
                if (disposed) return;
                var nf = new List<BoardFan>();
                var nv = new List<BoardValue>();
                foreach (var c in chips)
                {
                    try { c.Update(); } catch { }
                    nf.AddRange(c.Fans);
                    nv.AddRange(c.Values);
                }
                fans = nf;
                values = nv;
            }
        }

        BoardChip ChipOf(int index, out int local)
        {
            local = -1;
            foreach (var c in chips)
                if (index >= c.FirstIndex && index < c.FirstIndex + c.FanCount) { local = index - c.FirstIndex; return c; }
            return null;
        }

        /// <summary>Manual PWM (clamped to 20..100 %). The chip's own mode and value are saved the first time.</summary>
        public bool SetFanPercent(int index, int percent, out string why)
        {
            why = null;
            try
            {
                lock (sync)
                {
                    if (disposed) { why = "disposed"; return false; }
                    int local;
                    var c = ChipOf(index, out local);
                    if (c == null) { why = L.T("Нет такого вентилятора."); return false; }
                    if (percent < 20) percent = 20;
                    if (percent > 100) percent = 100;
                    return c.SetPercent(local, percent, out why);
                }
            }
            catch (Exception ex) { why = ex.Message; return false; }
        }

        /// <summary>Give the channel back to the chip: the original control mode and PWM value.</summary>
        public bool RestoreFan(int index, out string why)
        {
            why = null;
            try
            {
                lock (sync)
                {
                    int local;
                    var c = ChipOf(index, out local);
                    if (c == null) { why = L.T("Нет такого вентилятора."); return false; }
                    return c.Restore(local, out why);
                }
            }
            catch (Exception ex) { why = ex.Message; return false; }
        }

        public void RestoreAll()
        {
            lock (sync)
            {
                foreach (var c in chips)
                {
                    for (int i = 0; i < c.FanCount; i++)
                    {
                        try { string w; c.Restore(i, out w); } catch { }
                    }
                }
            }
        }

        public void Dispose()
        {
            try { RestoreAll(); } catch { }
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                foreach (var c in chips) try { c.Close(); } catch { }
                chips.Clear();
            }
            try { if (exitHook != null) AppDomain.CurrentDomain.ProcessExit -= exitHook; } catch { }
        }

        /// <summary>Diagnostic dump of what the chips read (for tests / the report).</summary>
        public string Report()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                lock (sync) foreach (var c in chips) sb.AppendLine(c.Report());
                return sb.ToString();
            }
            catch (Exception ex) { return ex.Message; }
        }
    }

    // ==================================================================================================================
    /// <summary>A source of fans / values (one Super I/O chip or the Dell SMM interface).</summary>
    internal abstract class BoardChip
    {
        public string Name;
        public int FirstIndex;
        public abstract int FanCount { get; }
        public List<BoardFan> Fans = new List<BoardFan>();
        public List<BoardValue> Values = new List<BoardValue>();
        public abstract void Update();
        public abstract bool SetPercent(int local, int percent, out string why);
        public abstract bool Restore(int local, out string why);
        public virtual string Report() { return Name; }
        public abstract void Close();

        protected static string FanName(int n) { return L.F("Вентилятор {0}", n); }
    }

    internal sealed class PawnIoFailed : IOException
    {
        public PawnIoFailed(string what) : base(what) { }
    }

    /// <summary>
    /// One Super I/O slot through the LpcIO module (slot 0 = ports 0x2E/0x2F, slot 1 = 0x4E/0x4F). The module only lets
    /// us touch the index/data ports of the slot and, after FindBars(), the 8-byte I/O ranges (BARs) of its logical devices.
    /// Callers hold PawnIo.IsaBusMutex around every sequence.
    /// </summary>
    internal sealed class LpcSlot : IDisposable
    {
        public readonly PawnModule M;
        public readonly int Slot;
        public readonly ushort RegPort;

        LpcSlot(PawnModule m, int slot) { M = m; Slot = slot; RegPort = (ushort)(slot == 0 ? 0x2E : 0x4E); }

        public static LpcSlot Open(int slot, out string error)
        {
            var m = PawnModule.Load("LpcIO", out error);
            if (m == null) return null;
            var s = new LpcSlot(m, slot);
            if (!m.Call("ioctl_select_slot", (ulong)slot)) { m.Dispose(); error = "select_slot failed"; return null; }
            return s;
        }

        public byte Inb(int port)
        {
            var r = M.Execute("ioctl_pio_inb", new[] { (ulong)port }, 1);
            if (r == null || r.Length < 1) throw new PawnIoFailed("pio_inb 0x" + port.ToString("X"));
            return (byte)r[0];
        }

        public void Outb(int port, int value)
        {
            if (M.Execute("ioctl_pio_outb", new[] { (ulong)port, (ulong)(value & 0xFF) }, 0) == null)
                throw new PawnIoFailed("pio_outb 0x" + port.ToString("X"));
        }

        public byte SioIn(int reg)
        {
            var r = M.Execute("ioctl_superio_inb", new[] { (ulong)reg }, 1);
            if (r == null || r.Length < 1) throw new PawnIoFailed("superio_inb");
            return (byte)r[0];
        }

        public ushort SioInw(int reg)
        {
            var r = M.Execute("ioctl_superio_inw", new[] { (ulong)reg }, 1);
            if (r == null || r.Length < 1) throw new PawnIoFailed("superio_inw");
            return (ushort)r[0];
        }

        public void SioOut(int reg, int value)
        {
            if (M.Execute("ioctl_superio_outb", new[] { (ulong)reg, (ulong)(value & 0xFF) }, 0) == null)
                throw new PawnIoFailed("superio_outb");
        }

        public bool FindBars() { return M.Execute("ioctl_find_bars", null, 0) != null; }

        public void Select(int ldn) { SioOut(0x07, ldn); }

        public void NuvotonEnter() { Outb(RegPort, 0x87); Outb(RegPort, 0x87); }
        public void NuvotonExit() { Outb(RegPort, 0xAA); }

        public void IteEnter()
        {
            Outb(RegPort, 0x87); Outb(RegPort, 0x01); Outb(RegPort, 0x55);
            Outb(RegPort, RegPort == 0x4E ? 0xAA : 0x55);
        }

        public void IteExit()
        {
            // The secondary ITE chip at 0x4E is left in configuration mode (as LibreHardwareMonitor does): exiting
            // and re-entering it confuses some IT8792E.
            if (RegPort != 0x4E) SioOut(0x02, 0x02);
        }

        public void Dispose() { try { M.Dispose(); } catch { } }
    }

    /// <summary>Chip detection on one slot. Mirrors the probe order of LibreHardwareMonitor: Nuvoton/Winbond, then ITE.</summary>
    internal static class SuperIoProbe
    {
        const int MutexWait = 1000;

        public static BoardChip Detect(int slot, out string note)
        {
            note = null;
            string err;
            var s = LpcSlot.Open(slot, out err);
            if (s == null) { note = "slot " + slot + ": " + err; return null; }
            BoardChip chip = null;
            string n = null;
            bool got = PawnIo.WithMutex(PawnIo.IsaBusMutex, MutexWait, delegate
            {
                try
                {
                    string n1, n2 = null;
                    chip = NuvotonChip.TryDetect(s, out n1);
                    if (chip == null) chip = IteChip.TryDetect(s, out n2);
                    if (chip == null) n = n1 != null && n2 != null ? n1 + "; " + n2 : (n1 ?? n2);
                }
                catch (Exception ex) { n = ex.Message; chip = null; }
            });
            if (!got) n = "ISA bus mutex busy";
            if (chip == null) { s.Dispose(); note = n == null ? null : "slot " + slot + ": " + n; }
            return chip;
        }
    }
}
