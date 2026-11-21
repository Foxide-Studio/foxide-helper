using System;
using System.Collections.Generic;
using System.Text;

namespace FoxideDisk
{
    /// <summary>
    /// Dell laptop / desktop fans and temperatures through the BIOS SMM interface (PawnIO module DellSMM, which refuses
    /// to load unless the firmware answers the Dell signature). Request codes are those of the i8k interface
    /// (documented by the Linux dell-smm-hwmon driver). Read-only: the BIOS owns the fans, so CanControl = false.
    /// GET_FAN_TYPE is never issued — on some Dell models it makes the BIOS fan control misbehave.
    /// </summary>
    internal sealed class DellSmm : BoardChip
    {
        const uint GetFan = 0x00a3, GetSpeed = 0x02a3, GetNomSpeed = 0x04a3, GetTemp = 0x10a3, GetTempType = 0x11a3;
        const int MaxFans = 4, MaxTemps = 10;

        readonly PawnModule m;
        readonly List<int> fanIds = new List<int>();
        readonly List<int> tempIds = new List<int>();
        readonly List<int> tempTypes = new List<int>();
        readonly Dictionary<int, int> maxState = new Dictionary<int, int>();
        int mult = 30;
        string info = "";

        public override int FanCount { get { return fanIds.Count; } }

        DellSmm(PawnModule module) { m = module; Name = "Dell SMM"; }

        public static bool LooksLikeDell()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
                {
                    string v = k == null ? null : k.GetValue("SystemManufacturer") as string;
                    if (v == null) return false;
                    return v.IndexOf("Dell", StringComparison.OrdinalIgnoreCase) >= 0 || v.IndexOf("Alienware", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        public static BoardChip Open(out string note)
        {
            note = null;
            string err;
            var module = PawnModule.Load("DellSMM", out err);
            if (module == null) { note = "Dell SMM: " + err; return null; }
            var d = new DellSmm(module);
            try
            {
                var sb = new StringBuilder();
                for (int f = 0; f < MaxFans; f++)
                {
                    uint st;
                    if (!d.Call(GetFan, (uint)f, out st)) continue;
                    d.fanIds.Add(f);
                    int max = 2;
                    sb.Append("fan" + f + " state=" + (st & 0xFF) + " nominal:");
                    for (int lv = 0; lv <= 3; lv++)
                    {
                        uint nom;
                        if (!d.Call(GetNomSpeed, (uint)(f | (lv << 8)), out nom)) { sb.Append(" -"); continue; }
                        int n = (int)(nom & 0xFFFF);
                        sb.Append(" " + n);
                        if (n > 1000) d.mult = 1;
                        if (lv == 3 && n > 0) max = 3;
                    }
                    d.maxState[f] = max;
                    sb.Append("; ");
                }
                for (int t = 0; t < MaxTemps; t++)
                {
                    uint ty;
                    if (!d.Call(GetTempType, (uint)t, out ty)) continue;
                    d.tempIds.Add(t);
                    d.tempTypes.Add((int)(ty & 0xFF));
                    sb.Append("t" + t + ":type" + (ty & 0xFF) + " ");
                }
                d.info = sb.ToString() + "mult=" + d.mult;
                if (d.fanIds.Count == 0 && d.tempIds.Count == 0) { d.Close(); note = "Dell SMM: no fans or sensors"; return null; }
                return d;
            }
            catch (Exception ex) { d.Close(); note = "Dell SMM: " + ex.Message; return null; }
        }

        /// <summary>One SMM request: eax = code, ebx = argument. False when the BIOS rejects it.</summary>
        bool Call(uint eax, uint ebx, out uint result)
        {
            result = 0;
            var r = m.Execute("ioctl_query_smm", new ulong[] { eax, 0, 0, ebx, 0, 0 }, 6);
            if (r == null || r.Length < 1) return false;
            result = (uint)(r[0] & 0xFFFFFFFF);
            if ((result & 0xFFFF) == 0xFFFF || result == eax) return false;
            return true;
        }

        public override void Update()
        {
            var nf = new List<BoardFan>();
            var nv = new List<BoardValue>();
            for (int i = 0; i < fanIds.Count; i++)
            {
                int f = fanIds[i];
                uint st, sp;
                var bf = new BoardFan { Index = FirstIndex + i, Name = FanName(i + 1), CanControl = false };
                if (Call(GetSpeed, (uint)f, out sp)) bf.Rpm = (int)(sp & 0xFFFF) * mult;
                if (Call(GetFan, (uint)f, out st))
                {
                    int state = (int)(st & 0xFF), max = maxState[f];
                    if (state > max) max = state;
                    bf.Percent = max > 0 ? Math.Round(state * 100.0 / max) : 0;
                }
                nf.Add(bf);
            }
            var counts = new Dictionary<int, int>();
            for (int i = 0; i < tempIds.Count; i++)
            {
                uint v;
                if (!Call(GetTemp, (uint)tempIds[i], out v)) continue;
                int c = (int)(v & 0xFF);
                if (c > 127 || c <= 0) continue;   // 0x99 glitch, 0xC1 = GPU powered off
                int ty = tempTypes[i];
                int n;
                counts.TryGetValue(ty, out n);
                counts[ty] = ++n;
                nv.Add(new BoardValue { Name = TempName(ty, n), Value = c, Kind = "temp" });
            }
            Fans = nf;
            Values = nv;
        }

        static string TempName(int type, int n)
        {
            string s;
            switch (type)
            {
                case 0: s = L.T("Процессор"); break;
                case 1: s = L.T("Видеокарта"); break;
                case 2: s = L.T("Память (SODIMM)"); break;
                case 4: s = L.T("Окружающий воздух"); break;
                default: s = L.T("Другой датчик"); break;
            }
            return n > 1 ? s + " " + n : s;
        }

        public override bool SetPercent(int local, int percent, out string why)
        {
            why = L.T("Вентиляторами этого ноутбука Dell управляет BIOS — ручное управление не поддерживается.");
            return false;
        }

        public override bool Restore(int local, out string why) { why = null; return true; }

        public override string Report() { return Name + ": " + info; }

        public override void Close() { try { m.Dispose(); } catch { } }
    }
}
