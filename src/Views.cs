using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace FoxideDisk
{
    public class RolePanel : Panel, IThemed
    {
        public Func<Color> Bg;
        public bool BorderRight, BorderBottom;
        public RolePanel(Func<Color> bg) { Bg = bg; DoubleBuffered = true; ResizeRedraw = true; }
        public void ApplyTheme() { BackColor = Bg(); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var p = new Pen(Theme.P.Border))
            {
                if (BorderRight) e.Graphics.DrawLine(p, Width - 1, 0, Width - 1, Height);
                if (BorderBottom) e.Graphics.DrawLine(p, 0, Height - 1, Width, Height - 1);
            }
        }
    }

    public class LogoHeader : FxControl
    {
        public LogoHeader() { Height = Theme.Dp(66); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e);
            int s = Theme.Dp(36);
            if (Width < Theme.Dp(120))
            {
                Logo.Draw(g, new RectangleF((Width - s) / 2f, (Height - s) / 2f, s, s));
                return;
            }
            Logo.Draw(g, new RectangleF(Theme.Dp(8), (Height - s) / 2f, s, s));
            int x = Theme.Dp(8) + s + Theme.Dp(12);
            DrawT(g, "Foxide Helper", Theme.Section, new Rectangle(x, Height / 2 - Theme.Dp(20), Width - x, Theme.Dp(22)), Theme.P.Text, LeftMid);
            DrawT(g, L.T("Диски, память, нагрузка"), Theme.Small, new Rectangle(x, Height / 2 + Theme.Dp(2), Width - x, Theme.Dp(18)), Theme.P.Muted, LeftMid);
        }
    }

    public static class Look
    {
        public static Color HealthColor(HealthState h)
        {
            switch (h)
            {
                case HealthState.Good: return Theme.P.Success;
                case HealthState.Warning: return Theme.P.Warning;
                case HealthState.Bad: return Theme.P.Danger;
                default: return Theme.P.Muted;
            }
        }

        public static Color TempColor(DiskInfo d, long t)
        {
            return t >= d.TempBad ? Theme.P.Danger : t >= d.TempWarn ? Theme.P.Warning : Theme.P.Success;
        }

        public static Color PercentColor(int pct)
        {
            return pct >= 70 ? Theme.P.Success : pct >= 40 ? Theme.P.Warning : Theme.P.Danger;
        }

        public static Color UsageColor(double f)
        {
            return f >= .95 ? Theme.P.Danger : f >= .85 ? Theme.P.Warning : Theme.P.Accent;
        }

        public static void DrawUsageBar(Graphics g, RectangleF r, double f)
        {
            float rad = r.Height / 2;
            Theme.FillRound(g, Theme.Mix(Theme.P.Surface2, Theme.P.Text, .07f), r, rad);
            if (f > 0)
                Theme.FillRound(g, UsageColor(f), new RectangleF(r.X, r.Y, Math.Max(r.Height, (float)(r.Width * Math.Min(1, f))), r.Height), rad);
        }

        public static Color[] PartColors(IList<PartInfo> parts)
        {
            var res = new Color[parts.Count];
            int k = 0;
            for (int i = 0; i < parts.Count; i++)
                res[i] = parts[i].IsService
                    ? Theme.Mix(Theme.P.Muted, Theme.P.Surface, Theme.P.Dark ? .35f : .25f)
                    : Theme.P.Series[k++ % Theme.P.Series.Length];
            return res;
        }
    }

    public class DiskCard : FxControl
    {
        public readonly DiskInfo Disk;
        bool selected, hover;
        public bool Selected { get { return selected; } set { selected = value; Invalidate(); } }

        public bool CanEject;
        public bool Ejecting;
        public event EventHandler EjectClicked;
        bool iconHover, downOnIcon;

        public DiskCard(DiskInfo d)
        {
            Disk = d; Height = Theme.Dp(d.VolumesSize > 0 ? 108 : 82); Cursor = Cursors.Hand;
            CanEject = d.Kind == DiskKind.Removable && !d.IsProtected;
        }

        RectangleF IconRect
        {
            get { float cs = Theme.Dpf(40); return new RectangleF(Theme.Dpf(14), (Height - cs) / 2f, cs, cs); }
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; iconHover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool ih = CanEject && IconRect.Contains(e.Location);
            if (ih != iconHover) { iconHover = ih; Invalidate(); }
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            downOnIcon = CanEject && IconRect.Contains(e.Location);
            base.OnMouseDown(e);
        }
        protected override void OnClick(EventArgs e)
        {
            if (downOnIcon && CanEject)
            {
                downOnIcon = false;
                if (!Ejecting && EjectClicked != null) EjectClicked(this, EventArgs.Empty);
                return;
            }
            base.OnClick(e);
        }

        /// <summary>Eject symbol: triangle over a bar.</summary>
        static void DrawEject(Graphics g, RectangleF r, Color c)
        {
            float w = r.Width, x = r.X, y = r.Y;
            using (var b = new SolidBrush(c))
            {
                g.FillPolygon(b, new[] { new PointF(x + w * .5f, y + w * .12f), new PointF(x + w * .9f, y + w * .58f), new PointF(x + w * .1f, y + w * .58f) });
                g.FillRectangle(b, x + w * .1f, y + w * .7f, w * .8f, w * .14f);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e); var P = Theme.P; var d = Disk;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color bg = selected ? Theme.Mix(P.Surface, P.Accent, P.Dark ? .14f : .07f)
                     : hover && Enabled ? Theme.Mix(P.Surface, P.Text, .04f) : P.Surface;
            Theme.FillRound(g, bg, r, Theme.Dpf(10));
            Theme.DrawRound(g, selected ? P.Accent : P.Border, r, Theme.Dpf(10));

            var cr = IconRect;
            if (iconHover && Enabled)
            {
                Theme.FillRound(g, P.Accent, cr, Theme.Dpf(10));
                float es = Theme.Dpf(18);
                DrawEject(g, new RectangleF(cr.X + (cr.Width - es) / 2, cr.Y + (cr.Height - es) / 2 + Theme.Dpf(1), es, es), P.AccentText);
            }
            else
            {
                Theme.FillRound(g, Theme.Mix(P.Surface, P.Accent, P.Dark ? .22f : .13f), cr, Theme.Dpf(10));
                DrawT(g, DiskService.KindGlyph(d.Kind), Theme.Glyph(14f), Rectangle.Round(cr), P.Accent, CenterMid);
                if (CanEject)
                {
                    // small eject badge in the corner so the action is discoverable
                    float bs = Theme.Dpf(17);
                    var br = new RectangleF(cr.Right - bs + Theme.Dpf(4), cr.Bottom - bs + Theme.Dpf(4), bs, bs);
                    using (var b = new SolidBrush(bg)) g.FillEllipse(b, br.X - 2, br.Y - 2, br.Width + 4, br.Height + 4);
                    using (var b = new SolidBrush(Ejecting ? P.Muted : P.Accent)) g.FillEllipse(b, br);
                    float es = bs * .56f;
                    DrawEject(g, new RectangleF(br.X + (bs - es) / 2, br.Y + (bs - es) / 2 + 0.5f, es, es), P.AccentText);
                }
            }

            int x = Theme.Dp(66), w = Width - x - Theme.Dp(12);
            Color fg = Enabled ? P.Text : P.Muted;
            DrawT(g, d.Model.Length > 0 ? d.Model : L.F("Диск {0}", d.Number), Theme.Bold, new Rectangle(x, Theme.Dp(12), w, Theme.Dp(20)), fg, LeftMid);
            DrawT(g, L.F("Диск {0}", d.Number) + " · " + DiskService.FormatSize(d.Size) + " · " + d.KindName + (d.Rpm > 1 ? " · " + L.F("{0} об/мин", d.Rpm) : ""),
                Theme.Small, new Rectangle(x, Theme.Dp(33), w, Theme.Dp(18)), P.Muted, LeftMid);

            float dot = Theme.Dpf(8);
            int y3 = Theme.Dp(54);
            using (var b = new SolidBrush(Look.HealthColor(d.Health)))
                g.FillEllipse(b, x, y3 + Theme.Dpf(9) - dot / 2, dot, dot);
            // "Исправен 94 %" (health colour) · "35 °C" (temperature colour) · rest muted
            int lx = x + Theme.Dp(14), lw = w - Theme.Dp(14);
            var segs = new List<KeyValuePair<string, Color>>();
            if (Ejecting) segs.Add(new KeyValuePair<string, Color>(L.T("Извлечение…"), P.Muted));
            else
            {
                segs.Add(new KeyValuePair<string, Color>(DiskService.HealthName(d.Health) + (d.HealthPercent.HasValue ? " " + d.HealthPercent + " %" : ""),
                    d.Health == HealthState.Good || d.Health == HealthState.Unknown ? P.Muted : Look.HealthColor(d.Health)));
                if (d.TempC.HasValue)
                    segs.Add(new KeyValuePair<string, Color>("  ·  " + d.TempC + " °C", d.TempC >= d.TempWarn ? Look.TempColor(d, d.TempC.Value) : P.Muted));
                string tail = (d.IsProtected ? L.T("  ·  системный") : "") + (d.Letters.Length > 0 ? "  ·  " + d.Letters : "");
                if (tail.Length > 0) segs.Add(new KeyValuePair<string, Color>(tail, P.Muted));
            }
            foreach (var sg in segs)
            {
                int sw = TextRenderer.MeasureText(g, sg.Key, Theme.Small, new Size(int.MaxValue, 100), TextFormatFlags.NoPadding).Width;
                if (lw <= 0) break;
                DrawT(g, sg.Key, Theme.Small, new Rectangle(lx, y3, lw, Theme.Dp(18)), sg.Value, LeftMid);
                lx += sw; lw -= sw;
            }

            if (d.VolumesSize > 0)
            {
                double f = d.UsedFraction;
                int y4 = Theme.Dp(76), pw = Theme.Dp(40);
                DrawT(g, L.F("Занято {0} из {1}", DiskService.FormatSize(d.VolumesUsed), DiskService.FormatSize(d.VolumesSize)),
                    Theme.Small, new Rectangle(x, y4, w - pw, Theme.Dp(16)), P.Muted, LeftMid);
                DrawT(g, (int)Math.Round(f * 100) + " %", Theme.Small, new Rectangle(x + w - pw, y4, pw, Theme.Dp(16)), Look.UsageColor(f), RightMid);
                Look.DrawUsageBar(g, new RectangleF(x, y4 + Theme.Dpf(19), w, Theme.Dpf(5)), f);
            }
        }
    }

    public class PartitionBar : FxControl
    {
        readonly DiskInfo disk;
        public PartitionBar(DiskInfo d) { disk = d; Height = Theme.Dp(58); }

        class Seg { public PartInfo Part; public ulong Size; public Color Color; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e); var P = Theme.P;
            var segs = new List<Seg>();
            var colors = Look.PartColors(disk.Partitions);
            ulong cursor = 0, slack = 16UL << 20;
            Color freeC = Theme.Mix(P.Surface2, P.Surface, .2f);
            for (int i = 0; i < disk.Partitions.Count; i++)
            {
                var p = disk.Partitions[i];
                if (p.Offset > cursor + slack) segs.Add(new Seg { Size = p.Offset - cursor, Color = freeC });
                segs.Add(new Seg { Part = p, Size = p.Size, Color = colors[i] });
                cursor = Math.Max(cursor, p.Offset + p.Size);
            }
            if (disk.Size > cursor + slack) segs.Add(new Seg { Size = disk.Size - cursor, Color = freeC });
            if (segs.Count == 0) return;

            int gap = Theme.Dp(3);
            float avail = Width - gap * (segs.Count - 1) - 1;
            double total = segs.Sum(s => (double)s.Size);
            var weights = segs.Select(s => Math.Max(s.Size / total, 0.055)).ToArray();
            double wsum = weights.Sum();
            float x = 0;
            for (int i = 0; i < segs.Count; i++)
            {
                float w = (float)(avail * weights[i] / wsum);
                var r = new RectangleF(x, 0.5f, w, Height - 1.5f);
                var s = segs[i];
                Theme.FillRound(g, s.Color, r, Theme.Dpf(7));
                if (s.Part == null) Theme.DrawRound(g, P.Border, r, Theme.Dpf(7));
                bool onColor = s.Part != null && !s.Part.IsService;
                Color tc = onColor ? Color.White : P.Text;
                string t1 = s.Part == null ? L.T("Свободно") : s.Part.Letter != '\0' ? s.Part.Letter + ":" : ShortType(s.Part);
                string t2 = DiskService.FormatSize(s.Size);
                var rr = Rectangle.Round(r);
                if (w > Theme.Dp(46))
                {
                    DrawT(g, t1, Theme.Bold, new Rectangle(rr.X + 4, rr.Y + Theme.Dp(9), rr.Width - 8, Theme.Dp(20)), tc, CenterMid | TextFormatFlags.EndEllipsis);
                    DrawT(g, t2, Theme.Small, new Rectangle(rr.X + 4, rr.Y + Theme.Dp(29), rr.Width - 8, Theme.Dp(18)), onColor ? Color.FromArgb(230, 255, 255, 255) : P.Muted, CenterMid | TextFormatFlags.EndEllipsis);
                }
                x += w + gap;
            }
        }

        static string ShortType(PartInfo p)
        {
            // p.Type may already be translated (DiskService / MainForm) — match both forms
            if (p.Type.StartsWith("Системный") || p.Type == L.T("Системный EFI")) return "EFI";
            if (p.Type.StartsWith("Зарезерв") || p.Type == L.T("Зарезервировано (MSR)")) return "MSR";
            if (p.Type.StartsWith("Восстанов") || p.Type == L.T("Восстановление")) return "Recovery";
            return "#" + p.Number;
        }
    }

    public class PartitionTable : FxControl, IAutoHeight
    {
        readonly DiskInfo disk;
        /// <summary>Show per-row action buttons (open / change letter / delete).</summary>
        public bool Actions;
        /// <summary>Raised with "open", "letter" or "delete".</summary>
        public event Action<PartInfo, string> ActionClicked;

        class Hit { public Rectangle R; public PartInfo P; public string Action; public bool Enabled; public string Tip; }
        readonly List<Hit> hits = new List<Hit>();
        Hit hover;
        readonly ToolTip tip = new ToolTip();

        public PartitionTable(DiskInfo d) { disk = d; }
        int RowH { get { return Theme.Dp(34); } }

        public int HeightFor(int width) { return RowH * (Math.Max(1, disk.Partitions.Count) + 1); }

        protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var h = hits.FirstOrDefault(x => x.R.Contains(e.Location));
            if (h != hover)
            {
                hover = h;
                Cursor = h != null && h.Enabled ? Cursors.Hand : Cursors.Default;
                tip.SetToolTip(this, h != null ? h.Tip : null);
                Invalidate();
            }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = null; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            var h = hits.FirstOrDefault(x => x.R.Contains(e.Location));
            if (h != null && h.Enabled && ActionClicked != null) ActionClicked(h.P, h.Action);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e); var P = Theme.P;
            hits.Clear();
            string[] head = { "", "№", L.T("Тип"), L.T("Буква"), L.T("Метка"), L.T("ФС"), L.T("Размер"), L.T("Свободно"), "" };
            int[] fixedW = { 20, 24, -3, 44, -2, 54, 74, 74, Actions ? 102 : 0 };
            int fixedSum = fixedW.Where(v => v > 0).Sum(v => Theme.Dp(v));
            int fillUnits = -fixedW.Where(v => v < 0).Sum();
            int rest = Math.Max(0, Width - fixedSum);
            var ws = fixedW.Select(v => v > 0 ? Theme.Dp(v) : v == 0 ? 0 : rest * -v / Math.Max(1, fillUnits)).ToArray();

            int x = 0;
            for (int c = 0; c < head.Length; c++)
            {
                if (ws[c] > 0) DrawT(g, head[c], Theme.Small, new Rectangle(x, 0, ws[c] - 6, RowH), P.Muted, c >= 6 ? RightMid : LeftMid);
                x += ws[c];
            }
            using (var pen = new Pen(P.Border)) g.DrawLine(pen, 0, RowH - 1, Width, RowH - 1);

            if (disk.Partitions.Count == 0)
            {
                DrawT(g, L.T("Разделов нет — диск пуст или не инициализирован."), Theme.Body, new Rectangle(0, RowH, Width, RowH), P.Muted, LeftMid);
                return;
            }
            var colors = Look.PartColors(disk.Partitions);
            for (int i = 0; i < disk.Partitions.Count; i++)
            {
                var p = disk.Partitions[i];
                int y = RowH * (i + 1);
                string[] cells =
                {
                    "", p.Number.ToString(CultureInfo.InvariantCulture), p.Type,
                    p.Letter != '\0' ? p.Letter + ":" : "—",
                    p.Label.Length > 0 ? p.Label : "—",
                    p.FileSystem.Length > 0 ? p.FileSystem : "—",
                    DiskService.FormatSize(p.Size),
                    p.HasFree ? DiskService.FormatSize(p.Free) : "—",
                    ""
                };
                x = 0;
                for (int c = 0; c < cells.Length; c++)
                {
                    if (c == 0)
                        Theme.FillRound(g, colors[i], new RectangleF(x + 2, y + RowH / 2f - Theme.Dpf(6), Theme.Dpf(12), Theme.Dpf(12)), Theme.Dpf(3));
                    else if (c == 8 && Actions)
                        DrawActions(g, p, new Rectangle(x, y, ws[c], RowH));
                    else if (ws[c] > 0)
                        DrawT(g, cells[c], Theme.Body, new Rectangle(x, y, ws[c] - 6, RowH), p.IsService && c == 2 ? P.Muted : P.Text, c >= 6 ? RightMid : LeftMid);
                    x += ws[c];
                }
                if (i < disk.Partitions.Count - 1)
                    using (var pen = new Pen(Theme.Mix(P.Surface, P.Border, .6f))) g.DrawLine(pen, 0, y + RowH - 1, Width, y + RowH - 1);
            }
        }

        void DrawActions(Graphics g, PartInfo p, Rectangle cell)
        {
            var P = Theme.P;
            string why;
            bool modify = VolumeOps.CanModify(disk, p, out why);
            bool canOpen = p.Letter != '\0' && p.FileSystem.Length > 0;
            var acts = new[]
            {
                new { A = "open", G = "", On = canOpen, Tip = canOpen ? L.F("Открыть {0}: в Проводнике", p.Letter) : L.T("У тома нет буквы — открыть нельзя") },
                new { A = "letter", G = "", On = modify && (p.FileSystem.Length > 0 || p.Letter != '\0'),
                      Tip = !modify ? why : p.FileSystem.Length == 0 && p.Letter == '\0' ? L.T("Букву можно назначить только тому с файловой системой") : L.T("Изменить букву") },
                new { A = "delete", G = "", On = modify, Tip = modify ? L.T("Удалить том") : why },
            };
            int bs = Theme.Dp(30), gap = Theme.Dp(4);
            int bx = cell.Right - acts.Length * bs - (acts.Length - 1) * gap;
            foreach (var a in acts)
            {
                var r = new Rectangle(bx, cell.Y + (cell.Height - Theme.Dp(28)) / 2, bs, Theme.Dp(28));
                var hit = new Hit { R = r, P = p, Action = a.A, Enabled = a.On, Tip = a.Tip };
                hits.Add(hit);
                bool hov = hover != null && hover.R == r;
                if (hov && a.On)
                    Theme.FillRound(g, a.A == "delete" ? Theme.Mix(P.Surface, P.Danger, .2f) : Theme.Mix(P.Surface, P.Text, .08f), r, Theme.Dpf(6));
                Color c = !a.On ? Theme.Mix(P.Muted, P.Surface, .55f) : hov && a.A == "delete" ? P.Danger : hov ? P.Accent : P.Muted;
                DrawT(g, a.G, Theme.Glyph(10.5f), r, c, CenterMid);
                bx += bs + gap;
            }
        }
    }

    /// <summary>Clickable card title with a collapse chevron.</summary>
    public class CardHeader : FxControl
    {
        bool collapsed, hover;
        public bool Collapsed { get { return collapsed; } set { collapsed = value; Invalidate(); } }
        public CardHeader(string title) { Text = title; Height = Theme.Dp(28); Cursor = Cursors.Hand; }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e); var P = Theme.P;
            string hint = collapsed ? L.T("Развернуть") : L.T("Свернуть");
            int hw = TextRenderer.MeasureText(hint, Theme.Small).Width;
            int gw = Theme.Dp(28);
            var hr = new RectangleF(Width - hw - gw - Theme.Dp(10), 0, hw + gw + Theme.Dp(10), Height);
            if (hover) Theme.FillRound(g, Theme.Mix(BackColor, P.Text, .06f), hr, Theme.Dpf(6));
            DrawT(g, Text, Theme.Section, new Rectangle(0, 0, (int)hr.X - Theme.Dp(8), Height), P.Text, LeftMid);
            DrawT(g, hint, Theme.Small, new Rectangle((int)hr.X + Theme.Dp(8), 0, hw, Height), hover ? P.Text : P.Muted, LeftMid);
            DrawT(g, collapsed ? "" : "", Theme.Glyph(9f), new Rectangle(Width - gw, 0, gw, Height), hover ? P.Accent : P.Muted, CenterMid);
        }
    }

    /// <summary>Plain read-only table. Column widths in dp; negative = proportional fill.</summary>
    public class SimpleTable : FxControl, IAutoHeight
    {
        readonly string[] head;
        readonly int[] spec;
        public readonly List<string[]> Rows = new List<string[]>();
        public string Empty = L.T("Нет данных.");
        public SimpleTable(string[] head, int[] spec) { this.head = head; this.spec = spec; }
        int RowH { get { return Theme.Dp(32); } }
        public int HeightFor(int width) { return RowH * (Math.Max(1, Rows.Count) + 1); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e); var P = Theme.P;
            int fixedSum = spec.Where(v => v > 0).Sum(v => Theme.Dp(v));
            int units = Math.Max(1, -spec.Where(v => v < 0).Sum());
            int rest = Math.Max(0, Width - fixedSum);
            var ws = spec.Select(v => v > 0 ? Theme.Dp(v) : rest * -v / units).ToArray();
            int x = 0;
            for (int c = 0; c < head.Length; c++) { DrawT(g, head[c], Theme.Small, new Rectangle(x, 0, ws[c] - 8, RowH), P.Muted, LeftMid); x += ws[c]; }
            using (var pen = new Pen(P.Border)) g.DrawLine(pen, 0, RowH - 1, Width, RowH - 1);
            if (Rows.Count == 0) { DrawT(g, Empty, Theme.Body, new Rectangle(0, RowH, Width, RowH), P.Muted, LeftMid); return; }
            for (int r = 0; r < Rows.Count; r++)
            {
                int y = RowH * (r + 1);
                x = 0;
                for (int c = 0; c < head.Length && c < Rows[r].Length; c++)
                {
                    DrawT(g, Rows[r][c], c == 0 ? Theme.Bold : Theme.Body, new Rectangle(x, y, ws[c] - 8, RowH), P.Text, LeftMid);
                    x += ws[c];
                }
                if (r < Rows.Count - 1)
                    using (var pen = new Pen(Theme.Mix(P.Surface, P.Border, .6f))) g.DrawLine(pen, 0, y + RowH - 1, Width, y + RowH - 1);
            }
        }
    }

    /// <summary>RAM load: big bar + numbers, refreshed by a timer.</summary>
    public class RamUsageView : FxControl, IAutoHeight
    {
        RamStatus st;
        public void Set(RamStatus s) { st = s; Invalidate(); }
        public int HeightFor(int width) { return Theme.Dp(78); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e); var P = Theme.P;
            if (st.Total == 0) { DrawT(g, L.T("Загрузка…"), Theme.Body, ClientRectangle, P.Muted, LeftMid); return; }
            ulong used = st.Total - st.Available;
            double f = (double)used / st.Total;
            DrawT(g, L.F("Занято {0} из {1}", DiskService.FormatSize(used), DiskService.FormatSize(st.Total)), Theme.Section,
                new Rectangle(0, 0, Width - Theme.Dp(70), Theme.Dp(26)), P.Text, LeftMid);
            DrawT(g, st.Load + " %", Theme.Section, new Rectangle(Width - Theme.Dp(70), 0, Theme.Dp(70), Theme.Dp(26)), Look.UsageColor(f), RightMid);
            Look.DrawUsageBar(g, new RectangleF(0, Theme.Dpf(32), Width - 1, Theme.Dpf(12)), f);
            DrawT(g, L.F("Свободно {0}   ·   выделено программам (commit) {1} из {2} с учётом файла подкачки",
                DiskService.FormatSize(st.Available), DiskService.FormatSize(st.CommitTotal), DiskService.FormatSize(st.CommitLimit)),
                Theme.Small, new Rectangle(0, Theme.Dp(52), Width, Theme.Dp(20)), P.Muted, LeftMid);
        }
    }

    public struct TempPoint { public DateTime T; public double V; }

    /// <summary>Live temperature line chart with warning / critical lines and hover read-out.</summary>
    public class TempChart : FxControl, IAutoHeight
    {
        public List<TempPoint> Points = new List<TempPoint>();
        public double Warn = 70, Bad = 80;
        public string Unit = " °C", Axis = "°", Format = "0";
        public TimeSpan Window = TimeSpan.FromMinutes(10);
        int hoverX = -1;

        public TempChart() { Height = Theme.Dp(220); }
        public int HeightFor(int width) { return Theme.Dp(220); }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); hoverX = e.X; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverX = -1; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e); var P = Theme.P;
            int left = Theme.Dp(40), right = Theme.Dp(8), top = Theme.Dp(8), bottom = Theme.Dp(24);
            var plot = new Rectangle(left, top, Math.Max(10, Width - left - right), Math.Max(10, Height - top - bottom));
            DateTime now = DateTime.Now, start = now - Window;
            var pts = Points.Where(p => p.T >= start).ToList();

            double lo, hi;
            if (pts.Count > 0) { lo = pts.Min(p => p.V); hi = pts.Max(p => p.V); }
            else { lo = 30; hi = 50; }
            lo = Math.Floor((lo - 4) / 5) * 5; hi = Math.Ceiling((hi + 4) / 5) * 5;
            if (!double.IsNaN(Warn) && Warn <= hi + 8) hi = Math.Max(hi, Math.Ceiling((Warn + 3) / 5) * 5);
            if (hi - lo < 10) hi = lo + 10;
            lo = Math.Max(0, lo);
            Func<double, float> Y = v => (float)(plot.Bottom - (v - lo) / (hi - lo) * plot.Height);
            Func<DateTime, float> X = t => (float)(plot.Left + (t - start).TotalMilliseconds / Window.TotalMilliseconds * plot.Width);

            // grid + y labels
            double step = (hi - lo) > 40 ? 10 : 5;
            using (var pen = new Pen(Theme.Mix(P.Surface, P.Border, .7f)))
                for (double v = lo; v <= hi + 0.01; v += step)
                {
                    float y = Y(v);
                    g.DrawLine(pen, plot.Left, y, plot.Right, y);
                    DrawT(g, v.ToString("0") + Axis, Theme.Small, new Rectangle(0, (int)y - Theme.Dp(9), left - Theme.Dp(6), Theme.Dp(18)), P.Muted, RightMid);
                }
            // x labels
            int mins = (int)Window.TotalMinutes;
            for (int m = mins; m >= 0; m -= Math.Max(1, mins / 5))
            {
                float x = X(now.AddMinutes(-m));
                string lbl = m == 0 ? L.T("сейчас") : L.F("−{0} мин", m);
                var tr = new Rectangle((int)x - Theme.Dp(40), plot.Bottom + Theme.Dp(4), Theme.Dp(80), Theme.Dp(18));
                if (m == mins) tr.X = plot.Left;
                if (m == 0) tr.X = plot.Right - tr.Width;
                DrawT(g, lbl, Theme.Small, tr, P.Muted, m == mins ? LeftMid : m == 0 ? RightMid : CenterMid);
                if (m == 0) break;
            }
            // threshold lines
            foreach (var th in new[] { new { V = Warn, C = P.Warning, L = L.T("повышенная") }, new { V = Bad, C = P.Danger, L = L.T("перегрев") } })
            {
                if (double.IsNaN(th.V) || th.V < lo || th.V > hi) continue;
                float y = Y(th.V);
                using (var pen = new Pen(Theme.Mix(P.Surface, th.C, .8f), 1) { DashStyle = DashStyle.Dash }) g.DrawLine(pen, plot.Left, y, plot.Right, y);
                DrawT(g, th.L + " " + th.V + Axis, Theme.Small, new Rectangle(plot.Right - Theme.Dp(160), (int)y - Theme.Dp(17), Theme.Dp(156), Theme.Dp(16)), th.C, RightMid);
            }

            if (pts.Count == 0)
            {
                DrawT(g, L.T("Сбор данных…"), Theme.Body, plot, P.Muted, CenterMid);
                return;
            }
            var line = pts.Select(p => new PointF(X(p.T), Y(p.V))).ToArray();
            if (line.Length >= 2)
            {
                var poly = new List<PointF>(line) { new PointF(line[line.Length - 1].X, plot.Bottom), new PointF(line[0].X, plot.Bottom) };
                using (var br = new LinearGradientBrush(new RectangleF(plot.X, plot.Y - 1, plot.Width, plot.Height + 2),
                    Color.FromArgb(P.Dark ? 90 : 70, P.Accent), Color.FromArgb(0, P.Accent), 90f))
                    g.FillPolygon(br, poly.ToArray());
                using (var pen = new Pen(P.Accent, Theme.Dpf(2)) { LineJoin = LineJoin.Round }) g.DrawLines(pen, line);
            }
            var last = line[line.Length - 1];
            float r = Theme.Dpf(4);
            using (var b = new SolidBrush(P.Accent)) g.FillEllipse(b, last.X - r, last.Y - r, r * 2, r * 2);

            // hover read-out
            if (hoverX >= plot.Left && hoverX <= plot.Right)
            {
                int idx = 0; float best = float.MaxValue;
                for (int i = 0; i < line.Length; i++) { float dx = Math.Abs(line[i].X - hoverX); if (dx < best) { best = dx; idx = i; } }
                var hp = line[idx];
                using (var pen = new Pen(Theme.Mix(P.Surface, P.Text, .3f)) { DashStyle = DashStyle.Dot }) g.DrawLine(pen, hp.X, plot.Top, hp.X, plot.Bottom);
                using (var b = new SolidBrush(P.Surface)) g.FillEllipse(b, hp.X - r - 2, hp.Y - r - 2, r * 2 + 4, r * 2 + 4);
                using (var b = new SolidBrush(P.Accent)) g.FillEllipse(b, hp.X - r, hp.Y - r, r * 2, r * 2);
                string txt = pts[idx].V.ToString(Format) + Unit + "  ·  " + pts[idx].T.ToString("HH:mm:ss");
                var sz = TextRenderer.MeasureText(txt, Theme.Small);
                var box = new RectangleF(Math.Min(plot.Right - sz.Width - Theme.Dp(14), hp.X + Theme.Dp(8)), Math.Max(plot.Top, hp.Y - Theme.Dp(34)), sz.Width + Theme.Dp(12), Theme.Dp(24));
                Theme.FillRound(g, P.Surface2, box, Theme.Dpf(6));
                Theme.DrawRound(g, P.Border, box, Theme.Dpf(6));
                DrawT(g, txt, Theme.Small, Rectangle.Round(box), P.Text, CenterMid);
            }
        }
    }

    /// <summary>Health ring (remaining life %) + status text + thermometer.</summary>
    public class HealthGauge : FxControl, IAutoHeight
    {
        readonly DiskInfo d;
        readonly string title, body;
        public HealthGauge(DiskInfo disk, string title, string body) { d = disk; this.title = title; this.body = body; }

        int Pad { get { return Theme.Dp(20); } }
        int Ring { get { return Theme.Dp(112); } }
        int RightW { get { return Width > Theme.Dp(620) ? Theme.Dp(200) : 0; } }
        int TextX { get { return Pad + Ring + Theme.Dp(24); } }
        int TextW(int width) { return Math.Max(Theme.Dp(80), width - TextX - (width > Theme.Dp(620) ? Theme.Dp(200) : 0) - Pad); }

        int TextHeight(int width)
        {
            int h = TextRenderer.MeasureText(title, Theme.Section).Height + Theme.Dp(6);
            if (!string.IsNullOrEmpty(body))
                h += TextRenderer.MeasureText(body, Theme.Body, new Size(TextW(width), 100000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
            if (width <= Theme.Dp(620) && d.TempC.HasValue) h += Theme.Dp(64);   // thermometer goes under the text
            return h;
        }

        public int HeightFor(int width) { return Math.Max(Ring + Pad * 2, TextHeight(width) + Pad * 2); }

        Color StateColor
        {
            get
            {
                if (d.Health == HealthState.Bad) return Theme.P.Danger;
                if (d.HealthPercent.HasValue) return Look.PercentColor(d.HealthPercent.Value);
                return d.Health == HealthState.Unknown ? Theme.P.Muted : Look.HealthColor(d.Health);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e); var P = Theme.P;
            var sc = Look.HealthColor(d.Health);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.FillRound(g, Theme.Mix(P.Surface, sc, P.Dark ? .10f : .05f), r, Theme.Dpf(12));
            Theme.DrawRound(g, Theme.Mix(P.Border, sc, .35f), r, Theme.Dpf(12));

            // ring
            float th = Theme.Dpf(11);
            var rr = new RectangleF(Pad + th / 2, (Height - Ring) / 2f + th / 2, Ring - th, Ring - th);
            using (var pen = new Pen(Theme.Mix(P.Surface2, P.Text, .06f), th)) g.DrawArc(pen, rr, 135, 270);
            var rc = StateColor;
            if (d.HealthPercent.HasValue && d.HealthPercent > 0)
                using (var pen = new Pen(rc, th) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, rr, 135, 270f * d.HealthPercent.Value / 100f);
            var center = Rectangle.Round(new RectangleF(rr.X, rr.Y + rr.Height / 2 - Theme.Dpf(24), rr.Width, Theme.Dpf(34)));
            DrawT(g, d.HealthPercent.HasValue ? d.HealthPercent + " %" : "—", Theme.Big, center, d.HealthPercent.HasValue ? P.Text : P.Muted, CenterMid);
            DrawT(g, d.HealthPercent.HasValue ? L.T("здоровье") : L.T("нет данных"), Theme.Small,
                new Rectangle(center.X, center.Bottom, center.Width, Theme.Dp(18)), P.Muted, CenterMid);

            // text
            int x = TextX, w = TextW(Width), y = Math.Max(Pad, (Height - TextHeight(Width)) / 2);
            int tth = TextRenderer.MeasureText(title, Theme.Section).Height;
            DrawT(g, title, Theme.Section, new Rectangle(x, y, w, tth), P.Text, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
            y += tth + Theme.Dp(6);
            if (!string.IsNullOrEmpty(body))
            {
                int bh = TextRenderer.MeasureText(body, Theme.Body, new Size(w, 100000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
                DrawT(g, body, Theme.Body, new Rectangle(x, y, w, bh + 2), P.Muted, TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.Top);
                y += bh;
            }

            // thermometer (right column on wide layouts, under the text otherwise)
            if (d.TempC.HasValue)
            {
                Rectangle box = RightW > 0
                    ? new Rectangle(Width - RightW - Pad, (Height - Theme.Dp(84)) / 2, RightW, Theme.Dp(84))
                    : new Rectangle(x, y + Theme.Dp(10), Math.Min(w, Theme.Dp(260)), Theme.Dp(54));
                DrawThermo(g, box);
            }
        }

        void DrawThermo(Graphics g, Rectangle box)
        {
            var P = Theme.P;
            long t = d.TempC.Value;
            var tc = Look.TempColor(d, t);
            bool tall = box.Height > Theme.Dp(60);
            DrawT(g, L.T("Температура"), Theme.Small, new Rectangle(box.X, box.Y, box.Width, Theme.Dp(16)), P.Muted, LeftMid);
            int vy = box.Y + Theme.Dp(tall ? 18 : 14);
            DrawT(g, t + " °C", tall ? Theme.Big : Theme.Section, new Rectangle(box.X, vy, box.Width, Theme.Dp(tall ? 34 : 24)), tc, LeftMid);
            string st = t >= d.TempBad ? L.T("перегрев") : t >= d.TempWarn ? L.T("повышенная") : L.T("в норме");
            DrawT(g, st, Theme.Small, new Rectangle(box.X, vy, box.Width, Theme.Dp(tall ? 34 : 24)), tc, RightMid);

            // scale 0 … bad+15 with warn / bad zones
            float max = d.TempBad + 15;
            var bar = new RectangleF(box.X, box.Bottom - Theme.Dpf(10), box.Width, Theme.Dpf(6));
            Theme.FillRound(g, Theme.Mix(P.Surface2, P.Text, .06f), bar, bar.Height / 2);
            float wx = bar.X + bar.Width * d.TempWarn / max, bx = bar.X + bar.Width * d.TempBad / max;
            using (var b = new SolidBrush(Theme.Mix(P.Surface2, P.Warning, .35f))) g.FillRectangle(b, wx, bar.Y, bx - wx, bar.Height);
            Theme.FillRound(g, Theme.Mix(P.Surface2, P.Danger, .35f), new RectangleF(bx, bar.Y, bar.Right - bx, bar.Height), bar.Height / 2);
            float mx = bar.X + bar.Width * Math.Max(0, Math.Min(max, t)) / max;
            float mr = Theme.Dpf(6);
            using (var b = new SolidBrush(P.Surface)) g.FillEllipse(b, mx - mr - 2, bar.Y + bar.Height / 2 - mr - 2, mr * 2 + 4, mr * 2 + 4);
            using (var b = new SolidBrush(tc)) g.FillEllipse(b, mx - mr, bar.Y + bar.Height / 2 - mr, mr * 2, mr * 2);
        }
    }

    /// <summary>Fill level of the disk: a total line plus one bar per volume.</summary>
    public class UsageView : FxControl, IAutoHeight
    {
        class Item { public string Name; public ulong Used, Size; public bool Total; public char Letter; public Rectangle Btn; }
        readonly List<Item> items = new List<Item>();
        Item hover;
        readonly ToolTip tip = new ToolTip();
        public event Action<char> OpenClicked;

        protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }
        Item At(Point pt) { return items.FirstOrDefault(i => i.Letter != '\0' && i.Btn.Contains(pt)); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var h = At(e.Location);
            if (h != hover)
            {
                hover = h; Cursor = h != null ? Cursors.Hand : Cursors.Default;
                tip.SetToolTip(this, h != null ? L.F("Открыть {0}: в Проводнике", h.Letter) : null);
                Invalidate();
            }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = null; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            var h = At(e.Location);
            if (h != null && OpenClicked != null) OpenClicked(h.Letter);
        }

        public UsageView(DiskInfo d)
        {
            var vols = d.Volumes;
            if (vols.Count > 1)
                items.Add(new Item { Name = L.F("Весь диск ({0} тома)", vols.Count), Used = d.VolumesUsed, Size = d.VolumesSize, Total = true });
            foreach (var v in vols)
                items.Add(new Item
                {
                    Name = (v.Letter != '\0' ? v.Letter + ":  " : L.T("Без буквы  ·  ")) + (v.Label.Length > 0 ? v.Label : v.IsService ? v.Type : L.T("Без метки")) + "  ·  " + v.FileSystem,
                    Used = v.Size > v.Free ? v.Size - v.Free : 0,
                    Size = v.Size,
                    Letter = v.Letter,
                });
        }

        int RowH { get { return Theme.Dp(48); } }
        public int HeightFor(int width) { return items.Count * RowH; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e); var P = Theme.P;
            int y = 0;
            foreach (var it in items)
            {
                double f = it.Size > 0 ? (double)it.Used / it.Size : 0;
                string right = L.F("{0} из {1}  ·  свободно {2}", DiskService.FormatSize(it.Used), DiskService.FormatSize(it.Size),
                               DiskService.FormatSize(it.Size - it.Used));
                int rw = TextRenderer.MeasureText(right, Theme.Small).Width + Theme.Dp(4);
                int pw = Theme.Dp(46), ob = Theme.Dp(40);
                int W = Width - ob;
                DrawT(g, it.Name, it.Total ? Theme.Bold : Theme.Body, new Rectangle(0, y + Theme.Dp(4), W - rw - pw - Theme.Dp(8), Theme.Dp(20)), P.Text, LeftMid);
                DrawT(g, right, Theme.Small, new Rectangle(W - rw - pw, y + Theme.Dp(4), rw, Theme.Dp(20)), P.Muted, RightMid);
                DrawT(g, (int)Math.Round(f * 100) + " %", Theme.Bold, new Rectangle(W - pw, y + Theme.Dp(4), pw, Theme.Dp(20)), Look.UsageColor(f), RightMid);
                Look.DrawUsageBar(g, new RectangleF(0, y + Theme.Dpf(28), W - 1, Theme.Dpf(it.Total ? 10 : 7)), f);
                if (it.Letter != '\0')
                {
                    it.Btn = new Rectangle(Width - Theme.Dp(32), y + Theme.Dp(8), Theme.Dp(32), Theme.Dp(32));
                    bool hov = hover == it;
                    Theme.FillRound(g, hov ? Theme.Mix(P.Surface, P.Accent, .18f) : Theme.Mix(P.Surface, P.Text, .05f), it.Btn, Theme.Dpf(8));
                    DrawT(g, "", Theme.Glyph(11f), it.Btn, hov ? P.Accent : P.Muted, CenterMid);
                }
                y += RowH;
            }
        }
    }

    /// <summary>Extended SMART / health table: every attribute with its norm and deviation state.</summary>
    public class SmartTable : FxControl, IAutoHeight
    {
        readonly SmartReport rep;
        bool onlyIssues;
        public bool OnlyIssues { get { return onlyIssues; } set { onlyIssues = value; Invalidate(); if (Parent != null) Parent.PerformLayout(); } }
        public SmartTable(SmartReport r) { rep = r; }

        bool Ata { get { return rep.Rows.Any(x => x.Id.Length > 0); } }
        List<SmartRow> Rows { get { return rep.Rows.Where(x => !onlyIssues || x.State >= SmartState.Warning).ToList(); } }
        int RowH { get { return Theme.Dp(30); } }
        int NoteH { get { return Theme.Dp(18); } }
        int H(SmartRow r) { return RowH + (r.Note.Length > 0 ? NoteH : 0); }

        public int HeightFor(int width)
        {
            var rows = Rows;
            return RowH + (rows.Count == 0 ? RowH : rows.Sum(r => H(r)));
        }

        public static Color StateColor(SmartState s)
        {
            switch (s)
            {
                case SmartState.Bad: return Theme.P.Danger;
                case SmartState.Warning: return Theme.P.Warning;
                case SmartState.Info: return Theme.P.Info;
                default: return Theme.P.Success;
            }
        }
        static string StateName(SmartState s)
        {
            switch (s)
            {
                case SmartState.Bad: return L.T("Плохо");
                case SmartState.Warning: return L.T("Внимание");
                case SmartState.Info: return L.T("Инфо");
                default: return L.T("Норма");
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e); var P = Theme.P;
            bool ata = Ata;
            // columns: dot | ID | name | value | cur/worst/thr | norm | state
            int[] spec = { 18, ata ? 34 : 0, -3, -3, ata ? 112 : 0, -2, 92 };
            int fixedSum = spec.Where(v => v > 0).Sum(v => Theme.Dp(v));
            int units = -spec.Where(v => v < 0).Sum();
            int rest = Math.Max(0, Width - fixedSum);
            var ws = spec.Select(v => v > 0 ? Theme.Dp(v) : v == 0 ? 0 : rest * -v / units).ToArray();
            string[] head = { "", "ID", L.T("Параметр"), L.T("Значение"), L.T("Тек. / Худш. / Порог"), L.T("Норма"), L.T("Состояние") };

            int x = 0;
            for (int c = 0; c < head.Length; c++)
            {
                if (ws[c] > 0) DrawT(g, head[c], Theme.Small, new Rectangle(x, 0, ws[c] - 6, RowH), P.Muted, LeftMid);
                x += ws[c];
            }
            using (var pen = new Pen(P.Border)) g.DrawLine(pen, 0, RowH - 1, Width, RowH - 1);

            var rows = Rows;
            if (rows.Count == 0)
            {
                DrawT(g, onlyIssues ? L.T("Отклонений от нормы не найдено.") : L.T("Нет данных."), Theme.Body, new Rectangle(0, RowH, Width, RowH), P.Muted, LeftMid);
                return;
            }
            int y = RowH;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                int h = H(r);
                var sc = StateColor(r.State);
                if (r.State >= SmartState.Warning)
                    Theme.FillRound(g, Theme.Mix(P.Surface, sc, P.Dark ? .12f : .07f), new RectangleF(0, y + 1, Width - 1, h - 2), Theme.Dpf(6));

                string[] cells = { "", r.Id, r.Name, r.Value, r.Normalized, r.Norm, StateName(r.State) };
                x = 0;
                for (int c = 0; c < cells.Length; c++)
                {
                    var cell = new Rectangle(x + (c == 0 ? 0 : 0), y, Math.Max(0, ws[c] - 6), RowH);
                    if (c == 0)
                        using (var b = new SolidBrush(sc)) g.FillEllipse(b, x + Theme.Dp(5), y + RowH / 2f - Theme.Dpf(4), Theme.Dpf(8), Theme.Dpf(8));
                    else if (ws[c] > 0)
                    {
                        Color col = c == 6 ? sc : c == 1 || c == 4 || c == 5 ? P.Muted : P.Text;
                        DrawT(g, cells[c], c == 6 && r.State >= SmartState.Warning ? Theme.Bold : Theme.Body, cell, col, LeftMid);
                    }
                    x += ws[c];
                }
                if (r.Note.Length > 0)
                {
                    int nx = ws[0] + ws[1] + ws[2];
                    if (ws[1] == 0) nx = ws[0];
                    DrawT(g, "↳ " + r.Note, Theme.Small, new Rectangle(ws[0] + ws[1], y + RowH - Theme.Dp(6), Width - ws[0] - ws[1] - Theme.Dp(8), NoteH), sc, LeftMid);
                }
                if (i < rows.Count - 1)
                    using (var pen = new Pen(Theme.Mix(P.Surface, P.Border, .55f))) g.DrawLine(pen, 0, y + h - 1, Width, y + h - 1);
                y += h;
            }
        }
    }

    public class Divider : FxControl
    {
        public Divider() { Height = 1; }
        protected override void OnPaint(PaintEventArgs e) { e.Graphics.Clear(Theme.P.Border); }
    }

    /// <summary>"Type the disk number" confirmation before erasing.</summary>
    public class ConfirmDialog : Form
    {
        readonly TextBox box;
        readonly FlatButton ok;
        readonly string expected;
        readonly Stack root;

        public ConfirmDialog(DiskInfo d, WipeOptions o)
        {
            expected = d.Number.ToString(CultureInfo.InvariantCulture);
            Text = L.T("Подтверждение — Foxide Helper");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.Body;

            root = new Stack { Pad = 20, Gap = 12, Dock = DockStyle.Fill, Bg = () => Theme.P.Back };
            string parts = d.Partitions.Count == 0 ? L.T("разделов нет")
                : string.Join(", ", d.Partitions.Select(p => (p.Letter != '\0' ? p.Letter + ": " : "") + p.Type + " " + DiskService.FormatSize(p.Size)));
            string what = o.Describe();
            if (o.ZeroFill) what += " " + L.T("Диск будет полностью перезаписан нулями — это может занять несколько часов.");

            root.Controls.Add(new Banner(BannerKind.Danger, L.F("Стереть диск {0}?", d.Number),
                L.F("{0}, {1}.\nРазделы: {2}.\n\nВСЕ данные будут удалены без возможности восстановления. {3}", d.Model, DiskService.FormatSize(d.Size), parts, what)));
            root.Controls.Add(new TextBlock(L.F("Для подтверждения введите номер диска: {0}", expected), TStyle.Bold));
            box = new TextBox { Font = Theme.Subtitle, BorderStyle = BorderStyle.FixedSingle };
            box.TextChanged += (s, e) => ok.Enabled = box.Text.Trim() == expected;
            box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && ok.Enabled) { e.SuppressKeyPress = true; DialogResult = DialogResult.OK; }
                if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; DialogResult = DialogResult.Cancel; }
            };
            root.Controls.Add(box);

            var row = new Row { MinHeight = 38 };
            var cancel = new FlatButton(L.T("Отмена"), BtnKind.Secondary);
            cancel.Click += (s, e) => DialogResult = DialogResult.Cancel;
            ok = new FlatButton(L.T("Стереть диск"), BtnKind.Danger, "") { Enabled = false };
            ok.Click += (s, e) => { if (ok.Enabled) DialogResult = DialogResult.OK; };
            row.Add(new Panel(), -1).Add(cancel, 120).Add(ok, 170);
            root.Controls.Add(row);
            Controls.Add(root);

            int w = Theme.Dp(540);
            ClientSize = new Size(w, root.HeightFor(w));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.Apply(this);
            Theme.ApplyTitleBar(this);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            box.Focus();
        }
    }
}
