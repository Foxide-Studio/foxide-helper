using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace FoxideDisk
{
    /// <summary>One line of the "All sensors" table. Numeric rows keep session min / max.</summary>
    public class SensorRow
    {
        public string Group, Name, Unit, Text, Id;   // Id — language-independent key
        public double? Value;
        public string Format = "0.#";
        public SensorRow(string group, string name, double? value, string unit, string format = "0.#") { Group = group; Name = name; Value = value; Unit = unit; Format = format; }
        public SensorRow(string group, string name, string text) { Group = group; Name = name; Text = text; }
    }

    /// <summary>Compact table: group headers, then name · value · min · max.</summary>
    public class SensorTable : FxControl, IAutoHeight
    {
        public class Line { public bool Header; public string Name, Value, Min, Max; }
        public List<Line> Lines = new List<Line>();
        int RowH { get { return Theme.Dp(26); } }
        public int HeightFor(int width) { return RowH * (Lines.Count + 1) + Theme.Dp(4); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e); var P = Theme.P;
            int w = Width, rh = RowH;
            int cMax = w - Theme.Dp(4), cw = Theme.Dp(110);
            var cols = new[] { new Rectangle(0, 0, w - cw * 3, rh), new Rectangle(w - cw * 3, 0, cw, rh), new Rectangle(w - cw * 2, 0, cw, rh), new Rectangle(w - cw, 0, cw - Theme.Dp(4), rh) };
            Action<int, string, string, string, string, Font, Color, Color> row = (y, a, b, c, d, f, fg, mut) =>
            {
                DrawT(g, a, f, new Rectangle(Theme.Dp(4), y, cols[0].Width - Theme.Dp(8), rh), fg, LeftMid);
                DrawT(g, b, f, new Rectangle(cols[1].X, y, cols[1].Width, rh), fg, RightMid);
                DrawT(g, c, Theme.Small, new Rectangle(cols[2].X, y, cols[2].Width, rh), mut, RightMid);
                DrawT(g, d, Theme.Small, new Rectangle(cols[3].X, y, cols[3].Width, rh), mut, RightMid);
            };
            row(0, L.T("Датчик"), L.T("Значение"), L.T("Мин."), L.T("Макс."), Theme.Small, P.Muted, P.Muted);
            using (var pen = new Pen(P.Border)) g.DrawLine(pen, 0, rh - 1, w, rh - 1);
            int yy = rh;
            foreach (var l in Lines)
            {
                if (l.Header)
                {
                    using (var b = new SolidBrush(Theme.Mix(P.Surface, P.Text, .04f))) g.FillRectangle(b, 0, yy + 2, w, rh - 4);
                    DrawT(g, l.Name, Theme.Bold, new Rectangle(Theme.Dp(6), yy, w - Theme.Dp(12), rh), P.Text, LeftMid);
                }
                else if (string.IsNullOrEmpty(l.Min) && string.IsNullOrEmpty(l.Max) && !string.IsNullOrEmpty(l.Value))
                {
                    DrawT(g, l.Name, Theme.Body, new Rectangle(Theme.Dp(4), yy, cols[0].Width - Theme.Dp(8), rh), P.Text, LeftMid);
                    DrawT(g, l.Value, Theme.Body, new Rectangle(cols[1].X, yy, w - cols[1].X - Theme.Dp(4), rh), P.Muted, LeftMid);
                }
                else row(yy, l.Name, l.Value, l.Min, l.Max, Theme.Body, P.Text, P.Muted);
                yy += rh;
            }
        }
    }

    public partial class MainForm
    {
        static readonly Dictionary<string, double[]> sensorMinMax = new Dictionary<string, double[]>();

        /// <summary>Foldable "All sensors" card at the bottom of a component page; collapsed by default, state remembered.</summary>
        Control AllSensorsCard(string key, Func<List<SensorRow>> provider)
        {
            string openKey = "+sensors." + key;
            var s = new Stack { Card = true, Pad = 18, Gap = 10 };
            var h = new CardHeader(L.T("Все датчики")) { Collapsed = !Settings.CollapsedCards.Contains(openKey) && !Demo.OpenSensors };
            var sub = new TextBlock(L.T("Полный список значений с минимумом и максимумом за время работы программы"), TStyle.Muted);
            var table = new SensorTable();
            s.Controls.Add(h); s.Controls.Add(sub); s.Controls.Add(table);
            sub.Visible = table.Visible = !h.Collapsed;
            Action refresh = () =>
            {
                List<SensorRow> rows;
                try { rows = provider() ?? new List<SensorRow>(); } catch { rows = new List<SensorRow>(); }
                var lines = new List<SensorTable.Line>();
                string grp = null;
                foreach (var r in rows)
                {
                    if (r.Group != grp) { grp = r.Group; lines.Add(new SensorTable.Line { Header = true, Name = grp }); }
                    if (r.Value.HasValue)
                    {
                        string mk = key + "|" + (r.Id ?? r.Group + "|" + r.Name);
                        double v = r.Value.Value;
                        double[] mm;
                        if (!sensorMinMax.TryGetValue(mk, out mm)) sensorMinMax[mk] = mm = new[] { v, v };
                        mm[0] = Math.Min(mm[0], v); mm[1] = Math.Max(mm[1], v);
                        string u = string.IsNullOrEmpty(r.Unit) ? "" : " " + r.Unit;
                        lines.Add(new SensorTable.Line { Name = r.Name, Value = v.ToString(r.Format) + u, Min = mm[0].ToString(r.Format) + u, Max = mm[1].ToString(r.Format) + u });
                    }
                    else lines.Add(new SensorTable.Line { Name = r.Name, Value = r.Text ?? "—", Min = "", Max = "" });
                }
                if (lines.Count == 0) lines.Add(new SensorTable.Line { Name = L.T("Нет данных"), Value = "", Min = "", Max = "" });
                bool grow = lines.Count != table.Lines.Count;
                table.Lines = lines;
                if (grow) { s.PerformLayout(); if (page != null) page.PerformLayout(); }
                table.Invalidate();
            };
            h.Click += (o, e) =>
            {
                h.Collapsed = !h.Collapsed;
                if (h.Collapsed) Settings.CollapsedCards.Remove(openKey); else Settings.CollapsedCards.Add(openKey);
                Settings.Save();
                sub.Visible = table.Visible = !h.Collapsed;
                if (!h.Collapsed) refresh();
                page.PerformLayout(); page.Invalidate(true);
            };
            var timer = new Timer { Interval = 2000 };
            timer.Tick += (o, e) => { if (Visible && !h.Collapsed) refresh(); };
            timer.Start();
            s.Disposed += (o, e) => timer.Dispose();
            StartHwPoller();
            if (!h.Collapsed) refresh();
            return s;
        }

        // ---------------------------------------------------------------- providers
        // Every row gets a language-independent Id (Home → "My sensors" and min / max are keyed by it).
        static string UnitOf(string kind) { return kind == "temp" ? "°C" : kind == "power" ? L.T("Вт") : kind == "clock" ? L.T("МГц") : kind == "voltage" ? L.T("В") : ""; }

        static SensorRow Id(SensorRow r, string id) { r.Id = id; return r; }

        List<SensorRow> CpuSensorRows()
        {
            var r = new List<SensorRow>();
            string g = cpuNameCache ?? CpuName();
            var pc = pawnCpu;
            if (PawnOn && pc != null) r.Add(Id(new SensorRow(g, L.T("Источник"), "PawnIO · " + pc.Method), "cpu.src"));
            var counts = new Dictionary<string, int>();
            foreach (var s in PawnOn ? pawnCpuValues : new List<CpuSensor>())
            {
                int n; counts.TryGetValue(s.Kind, out n); counts[s.Kind] = n + 1;
                r.Add(Id(new SensorRow(g + " · " + (s.Kind == "temp" ? L.T("температуры") : s.Kind == "power" ? L.T("мощность") : L.T("частоты")), s.Name, s.Value, UnitOf(s.Kind), s.Kind == "clock" ? "0" : "0.#"), "cpu." + s.Kind + "." + n));
            }
            var h = hw;
            if (h != null)
            {
                if (h.CpuTempC.HasValue) r.Add(Id(new SensorRow(L.T("Windows (без драйвера)"), h.CpuTempSource == "APU" ? L.T("датчик графики APU") : L.T("датчик ACPI"), h.CpuTempC, "°C"), "cpu.win"));
                foreach (var z in h.CpuZones) r.Add(Id(new SensorRow(L.T("Windows (без драйвера)"), L.T("Термозона") + " " + z.Name, z.TempC, "°C"), "zone." + z.Name));
            }
            return r;
        }

        List<SensorRow> GpuSensorRows()
        {
            var r = new List<SensorRow>();
            var h = hw;
            foreach (var a in HwInfo.Gpus())
            {
                string g = a.Name, p = "gpu" + a.Index + ".";
                var live = h != null ? h.Gpus.FirstOrDefault(x => x.Index == a.Index) : null;
                if (live != null)
                {
                    var rows = new List<SensorRow>
                    {
                        Id(new SensorRow(g, L.T("Температура"), live.TempC, "°C"), p + "temp"),
                        Id(new SensorRow(g, L.T("Загрузка"), live.LoadPct, "%", "0"), p + "load"),
                        Id(new SensorRow(g, L.T("Частота ядра"), live.ClockMHz, L.T("МГц"), "0"), p + "clock"),
                        Id(new SensorRow(g, L.T("Частота памяти"), live.MemClockMHz, L.T("МГц"), "0"), p + "memclock"),
                        Id(new SensorRow(g, L.T("Потребление"), live.PowerW, L.T("Вт")), p + "power"),
                        Id(new SensorRow(g, L.T("Вентилятор"), live.FanPct, "%", "0"), p + "fan"),
                        Id(new SensorRow(g, L.T("Обороты вентилятора"), live.FanRpm, L.T("об/мин"), "0"), p + "fanrpm"),
                        Id(new SensorRow(g, L.T("Видеопамять занято"), live.MemUsedMB, L.T("МБ"), "0"), p + "memused"),
                        Id(new SensorRow(g, L.T("Видеопамять всего"), live.MemTotalMB, L.T("МБ"), "0"), p + "memtotal"),
                    };
                    r.AddRange(rows.Where(x => x.Value.HasValue));
                    if (!string.IsNullOrEmpty(live.Source)) r.Add(Id(new SensorRow(g, L.T("Источник"), live.Source), p + "src"));
                }
                r.Add(Id(new SensorRow(g, L.T("Драйвер"), !string.IsNullOrEmpty(a.VendorDriver) ? a.VendorDriver + " (" + a.DriverVersion + ")" : a.DriverVersion), p + "driver"));
                if (a.VramBytes > 0) r.Add(Id(new SensorRow(g, L.T("Видеопамять"), a.VramText + " · " + a.VramSource), p + "vram"));
                if (!string.IsNullOrEmpty(a.PcieText)) r.Add(Id(new SensorRow(g, L.T("Шина"), a.PcieText), p + "pcie"));
                if (a.PowerLimitW.HasValue) r.Add(Id(new SensorRow(g, L.T("Лимит мощности"), a.PowerLimitW, L.T("Вт"), "0"), p + "plimit"));
                if (a.MaxCoreMHz.HasValue) r.Add(Id(new SensorRow(g, L.T("Макс. частота"), a.MaxCoreMHz, L.T("МГц"), "0"), p + "maxclock"));
                if (!string.IsNullOrEmpty(a.Vbios)) r.Add(Id(new SensorRow(g, "VBIOS", a.Vbios), p + "vbios"));
                r.Add(Id(new SensorRow(g, "PCI", string.Format("{0:X4}:{1:X4}", a.VendorId, a.DeviceId) + (string.IsNullOrEmpty(a.PnpId) ? "" : " · " + a.PnpId)), p + "pci"));
            }
            return r;
        }

        List<SensorRow> RamSensorRows()
        {
            var r = new List<SensorRow>();
            var st = RamService.Status();
            string g = L.T("Оперативная память");
            const double GB = 1073741824.0;
            r.Add(Id(new SensorRow(g, L.T("Загрузка"), st.Load, "%", "0"), "ram.load"));
            r.Add(Id(new SensorRow(g, L.T("Занято"), (st.Total - st.Available) / GB, L.T("ГБ"), "0.00"), "ram.used"));
            r.Add(Id(new SensorRow(g, L.T("Свободно"), st.Available / GB, L.T("ГБ"), "0.00"), "ram.free"));
            r.Add(Id(new SensorRow(g, L.T("Всего"), st.Total / GB, L.T("ГБ"), "0.00"), "ram.total"));
            r.Add(Id(new SensorRow(g, L.T("Выделено (commit)"), st.CommitTotal / GB, L.T("ГБ"), "0.00"), "ram.commit"));
            r.Add(Id(new SensorRow(g, L.T("Предел выделения"), st.CommitLimit / GB, L.T("ГБ"), "0.00"), "ram.commitlimit"));
            foreach (var d in PawnOn ? pawnDimms : new List<DimmTemp>())
                r.Add(Id(new SensorRow(L.T("Температура модулей"), d.Name + " · " + d.Type + " · 0x" + d.Address.ToString("X2"), d.TempC, "°C", "0.0"), "dimm." + d.Address.ToString("X2")));
            var pm = pawnMem;
            if (PawnOn && pm != null) r.Add(Id(new SensorRow(L.T("Температура модулей"), L.T("Контроллер SMBus"), pm.Controller), "dimm.ctrl"));
            else if (PawnOn && !string.IsNullOrEmpty(pawnMemWhy)) r.Add(Id(new SensorRow(L.T("Температура модулей"), L.T("Состояние"), pawnMemWhy), "dimm.state"));
            return r;
        }

        List<SensorRow> CoolingSensorRows()
        {
            var r = new List<SensorRow>();
            var pb = pawnBoard;
            if (PawnOn && pb != null)
            {
                string g = L.T("Плата") + " · " + pb.ChipName;
                foreach (var f in pawnFans)
                {
                    if (f.Rpm.HasValue) r.Add(Id(new SensorRow(g, f.Name + " · " + L.T("обороты"), f.Rpm, L.T("об/мин"), "0"), "board.fan" + f.Index + ".rpm"));
                    if (f.Percent.HasValue) r.Add(Id(new SensorRow(g, f.Name + " · " + L.T("ШИМ"), f.Percent, "%", "0"), "board.fan" + f.Index + ".pwm"));
                }
                int i = 0;
                foreach (var v in pawnBoardValues)
                    r.Add(Id(new SensorRow(g, v.Name, v.Value, UnitOf(v.Kind), v.Kind == "voltage" ? "0.000" : "0.#"), "board." + v.Kind + "." + (i++)));
            }
            else if (PawnOn && !string.IsNullOrEmpty(pawnBoardWhy)) r.Add(Id(new SensorRow(L.T("Плата"), L.T("Состояние"), pawnBoardWhy), "board.state"));
            var h = hw;
            if (h != null)
            {
                var seen = new Dictionary<string, int>();
                foreach (var f in h.Fans.OrderBy(x => x.Source))
                {
                    string g = L.T("Вентиляторы") + " · " + f.Source;
                    int n; seen.TryGetValue(f.Source + f.Name, out n); seen[f.Source + f.Name] = ++n;
                    int same = h.Fans.Count(x => x.Source == f.Source && x.Name == f.Name);
                    string nm = same > 1 ? f.Name + " #" + n : f.Name;
                    string id = "fan." + f.Source + "." + f.GpuIndex + "." + f.FanIndex + "." + n;
                    if (f.Rpm.HasValue) r.Add(Id(new SensorRow(g, nm + " · " + L.T("обороты"), f.Rpm, L.T("об/мин"), "0"), id + ".rpm"));
                    if (f.Percent.HasValue) r.Add(Id(new SensorRow(g, nm + " · " + L.T("скорость"), f.Percent, "%", "0"), id + ".pct"));
                    if (!string.IsNullOrEmpty(f.Status)) r.Add(Id(new SensorRow(g, nm, f.Status), id + ".status"));
                }
                foreach (var z in h.CpuZones) r.Add(Id(new SensorRow(L.T("Термозоны ACPI"), z.Name, z.TempC, "°C"), "zone." + z.Name));
            }
            return r;
        }

        List<SensorRow> BatterySensorRows()
        {
            var r = new List<SensorRow>();
            foreach (var b in BatteryService.Query())
            {
                string g = (string.IsNullOrEmpty(b.Name) ? L.T("Аккумулятор") : b.Name) + (string.IsNullOrEmpty(b.Manufacturer) ? "" : " · " + b.Manufacturer);
                string p = "bat" + b.Index + ".";
                var rows = new List<SensorRow>
                {
                    Id(new SensorRow(g, L.T("Заряд"), b.Percent, "%", "0.#"), p + "pct"),
                    Id(new SensorRow(g, L.T("Остаток"), b.RemainingMWh > 0 ? b.RemainingMWh / 1000.0 : (double?)null, L.T("Вт·ч"), "0.00"), p + "remain"),
                    Id(new SensorRow(g, L.T("Ёмкость сейчас"), b.FullMWh > 0 ? b.FullMWh / 1000.0 : (double?)null, L.T("Вт·ч"), "0.00"), p + "full"),
                    Id(new SensorRow(g, L.T("Заводская ёмкость"), b.DesignMWh > 0 ? b.DesignMWh / 1000.0 : (double?)null, L.T("Вт·ч"), "0.00"), p + "design"),
                    Id(new SensorRow(g, L.T("Здоровье"), b.HealthPercent, "%", "0.0"), p + "health"),
                    Id(new SensorRow(g, L.T("Мощность"), b.RateW, L.T("Вт"), "0.00"), p + "rate"),
                    Id(new SensorRow(g, L.T("Напряжение"), b.VoltageMV.HasValue ? b.VoltageMV.Value / 1000.0 : (double?)null, L.T("В"), "0.000"), p + "volt"),
                    Id(new SensorRow(g, L.T("Температура"), b.TemperatureC, "°C"), p + "temp"),
                    Id(new SensorRow(g, L.T("Циклы заряда"), b.CycleCount, "", "0"), p + "cycles"),
                };
                r.AddRange(rows.Where(x => x.Value.HasValue));
                r.Add(Id(new SensorRow(g, L.T("Состояние"), b.StatusText), p + "status"));
                if (!string.IsNullOrEmpty(b.Chemistry)) r.Add(Id(new SensorRow(g, L.T("Химия"), b.Chemistry), p + "chem"));
                r.Add(Id(new SensorRow(g, L.T("Источник"), b.Source), p + "src"));
            }
            return r;
        }
    }
}
