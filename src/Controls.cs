using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FoxideDisk
{
    // ---------- Base ----------
    public class FxControl : Control, IThemed
    {
        public FxControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Font = Theme.Body;
        }
        public virtual void ApplyTheme()
        {
            if (Parent != null) BackColor = Parent.BackColor;
            Invalidate();
        }
        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (Parent != null) BackColor = Parent.BackColor;
        }
        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }
        protected static Graphics Prep(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            return e.Graphics;
        }
        protected static void DrawT(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, s, f, r, c, flags | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
        protected const TextFormatFlags LeftMid = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
        protected const TextFormatFlags CenterMid = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
        protected const TextFormatFlags RightMid = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
    }

    public static class LayoutUtil
    {
        // Control.Visible is false for every child until the parent form is shown; read the control's own flag instead.
        static readonly System.Reflection.MethodInfo getState = typeof(Control).GetMethod("GetState",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new[] { typeof(int) }, null);
        public static bool Shown(Control c)
        {
            if (getState == null) return c.Visible;
            try { return (bool)getState.Invoke(c, new object[] { 2 }); } catch { return c.Visible; }
        }
    }

    // ---------- Layout containers ----------
    public class Stack : Panel, IThemed, IAutoHeight
    {
        public int Pad = 0, Gap = 10;
        public bool Card, Scrolls;
        public Func<Color> Bg;

        public Stack()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        public void ApplyTheme()
        {
            BackColor = Card ? Theme.P.Surface : (Bg != null ? Bg() : (Parent != null ? Parent.BackColor : Theme.P.Back));
            if (Scrolls && IsHandleCreated)
                Native.SetWindowTheme(Handle, Theme.P.Dark ? "DarkMode_Explorer" : "Explorer", null);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (Scrolls) Native.SetWindowTheme(Handle, Theme.P.Dark ? "DarkMode_Explorer" : "Explorer", null);
        }

        public bool AutoScrollEnabled { set { Scrolls = value; AutoScroll = value; } }

        public int HeightFor(int width)
        {
            int pad = Theme.Dp(Pad), gap = Theme.Dp(Gap), w = width - pad * 2, y = pad, n = 0;
            foreach (Control c in Controls)
            {
                if (!LayoutUtil.Shown(c)) continue;
                var ah = c as IAutoHeight;
                y += (ah != null ? ah.HeightFor(w) : c.Height) + gap;
                n++;
            }
            if (n > 0) y -= gap;
            return y + pad;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            int pad = Theme.Dp(Pad), gap = Theme.Dp(Gap);
            int w = ClientSize.Width - pad * 2;
            int y = pad + AutoScrollPosition.Y, n = 0;
            foreach (Control c in Controls)
            {
                if (!LayoutUtil.Shown(c)) continue;
                var ah = c as IAutoHeight;
                int h = ah != null ? ah.HeightFor(w) : c.Height;
                c.SetBounds(pad, y, Math.Max(10, w), h);
                y += h + gap; n++;
            }
            if (n > 0) y -= gap;
            if (Scrolls)
            {
                AutoScrollMinSize = new Size(0, y - AutoScrollPosition.Y + pad);
                base.OnLayout(e);   // lets ScrollableControl show/hide the scrollbar
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (!Card) { base.OnPaintBackground(e); return; }
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.P.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.FillRound(g, Theme.P.Surface, r, Theme.Dpf(10));
            Theme.DrawRound(g, Theme.P.Border, r, Theme.Dpf(10));
        }

        public void ClearAll()
        {
            var list = Controls.Cast<Control>().ToList();
            Controls.Clear();
            foreach (var c in list) c.Dispose();
            if (Scrolls) AutoScrollPosition = new Point(0, 0);
        }
    }

    /// <summary>Horizontal row. Width per child in dp, -1 = fill.</summary>
    public class Row : Panel, IThemed, IAutoHeight
    {
        public int Gap = 8, MinHeight = 34;
        readonly Dictionary<Control, int> widths = new Dictionary<Control, int>();

        public Row() { DoubleBuffered = true; }
        public void ApplyTheme() { if (Parent != null) BackColor = Parent.BackColor; }

        public Row Add(Control c, int widthDp)
        {
            widths[c] = widthDp;
            Controls.Add(c);
            return this;
        }

        int[] Widths(int total)
        {
            var vis = Controls.Cast<Control>().Where(c => LayoutUtil.Shown(c)).ToList();
            int gap = Theme.Dp(Gap), fixedSum = 0, fills = 0;
            foreach (var c in vis)
            {
                int w; widths.TryGetValue(c, out w);
                if (w < 0) fills++; else fixedSum += Theme.Dp(w);
            }
            int rest = Math.Max(0, total - fixedSum - gap * Math.Max(0, vis.Count - 1));
            var res = new int[vis.Count];
            for (int i = 0; i < vis.Count; i++)
            {
                int w; widths.TryGetValue(vis[i], out w);
                res[i] = w < 0 ? (fills > 0 ? rest / fills : 0) : Theme.Dp(w);
            }
            return res;
        }

        public int HeightFor(int width)
        {
            var vis = Controls.Cast<Control>().Where(c => LayoutUtil.Shown(c)).ToList();
            var ws = Widths(width);
            int h = Theme.Dp(MinHeight);
            for (int i = 0; i < vis.Count; i++)
            {
                if (IsSpacer(vis[i])) continue;
                var ah = vis[i] as IAutoHeight;
                h = Math.Max(h, ah != null ? ah.HeightFor(ws[i]) : vis[i].Height);
            }
            return h;
        }

        static bool IsSpacer(Control c) { return c.GetType() == typeof(Panel) && c.Controls.Count == 0; }

        protected override void OnLayout(LayoutEventArgs e)
        {
            var vis = Controls.Cast<Control>().Where(c => LayoutUtil.Shown(c)).ToList();
            var ws = Widths(ClientSize.Width);
            int x = 0, gap = Theme.Dp(Gap);
            for (int i = 0; i < vis.Count; i++)
            {
                var ah = vis[i] as IAutoHeight;
                int h = ah != null ? Math.Min(Height, ah.HeightFor(ws[i])) : vis[i].Height;
                vis[i].SetBounds(x, (Height - h) / 2, ws[i], h);
                x += ws[i] + gap;
            }
        }
    }

    public class TileGrid : Panel, IThemed, IAutoHeight
    {
        public int MinTile = 180, TileH = 96, Gap = 10;
        public TileGrid() { DoubleBuffered = true; }
        public void ApplyTheme() { if (Parent != null) BackColor = Parent.BackColor; }

        int Cols(int w)
        {
            int min = Theme.Dp(MinTile), gap = Theme.Dp(Gap);
            return Math.Max(1, Math.Min(Controls.Count, (w + gap) / (min + gap)));
        }
        public int HeightFor(int width)
        {
            int n = Controls.Count; if (n == 0) return 0;
            int cols = Cols(width), rows = (n + cols - 1) / cols;
            return rows * Theme.Dp(TileH) + (rows - 1) * Theme.Dp(Gap);
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            int n = Controls.Count; if (n == 0) return;
            int gap = Theme.Dp(Gap), cols = Cols(ClientSize.Width), th = Theme.Dp(TileH);
            int tw = (ClientSize.Width - gap * (cols - 1)) / cols;
            for (int i = 0; i < n; i++)
                Controls[i].SetBounds((i % cols) * (tw + gap), (i / cols) * (th + gap), tw, th);
        }
    }

    // ---------- Text ----------
    public enum TStyle { Body, Muted, Small, Bold, Section, Title, Subtitle, Mono }

    public class TextBlock : FxControl, IAutoHeight
    {
        public TStyle Style;
        public Func<Color> ColorFn;
        public bool Single;

        public TextBlock(string text, TStyle style = TStyle.Body)
        {
            Text = text; Style = style;
        }

        Font F
        {
            get
            {
                switch (Style)
                {
                    case TStyle.Small: return Theme.Small;
                    case TStyle.Bold: return Theme.Bold;
                    case TStyle.Section: return Theme.Section;
                    case TStyle.Title: return Theme.Title;
                    case TStyle.Subtitle: return Theme.Subtitle;
                    case TStyle.Mono: return Theme.Mono;
                    default: return Theme.Body;
                }
            }
        }
        Color C
        {
            get
            {
                if (ColorFn != null) return ColorFn();
                return (Style == TStyle.Muted || Style == TStyle.Small || Style == TStyle.Subtitle) ? Theme.P.Muted : Theme.P.Text;
            }
        }
        TextFormatFlags Flags
        {
            get
            {
                return Single ? (TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding)
                              : (TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            }
        }

        public int HeightFor(int width)
        {
            if (string.IsNullOrEmpty(Text)) return 0;
            var s = TextRenderer.MeasureText(Text, F, new Size(Math.Max(1, width), 100000), Flags);
            return s.Height + Theme.Dp(2);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
            if (Parent != null) Parent.PerformLayout();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            TextRenderer.DrawText(e.Graphics, Text, F, ClientRectangle, C, Flags | TextFormatFlags.Left | TextFormatFlags.Top);
        }
    }

    // ---------- Buttons ----------
    public enum BtnKind { Primary, Secondary, Danger, Ghost, Nav, Tab }

    public class FlatButton : FxControl
    {
        public BtnKind Kind;
        public string Glyph;
        public string Badge;
        bool iconOnly;
        public bool IconOnly { get { return iconOnly; } set { iconOnly = value; Invalidate(); } }
        bool selected, hover, down;

        public bool Selected { get { return selected; } set { selected = value; Invalidate(); } }

        public FlatButton(string text, BtnKind kind, string glyph = null)
        {
            Text = text; Kind = kind; Glyph = glyph;
            Height = Theme.Dp(kind == BtnKind.Nav ? 38 : 36);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
        }

        public int PreferredWidth()
        {
            int w = TextRenderer.MeasureText(Text ?? "", Kind == BtnKind.Tab ? Theme.Bold : Theme.Body).Width + Theme.Dp(32);
            if (Glyph != null) w += Theme.Dp(26);
            return w;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) OnClick(EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e);
            var P = Theme.P;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float rad = Theme.Dpf(8);
            Color bg = Color.Empty, fg = P.Text, border = Color.Empty;

            switch (Kind)
            {
                case BtnKind.Primary:
                    bg = down ? Theme.Mix(P.Accent, Color.Black, .12f) : hover ? P.AccentHover : P.Accent; fg = P.AccentText; break;
                case BtnKind.Danger:
                    bg = down ? Theme.Mix(P.Danger, Color.Black, .15f) : hover ? Theme.Mix(P.Danger, Color.White, .1f) : P.Danger; fg = Color.White; break;
                case BtnKind.Secondary:
                    bg = hover ? Theme.Mix(P.Surface2, P.Text, .06f) : P.Surface2; border = P.Border; break;
                case BtnKind.Ghost:
                    if (hover) bg = P.Surface2; break;
                case BtnKind.Nav:
                    if (selected) bg = Theme.Mix(BackColor, P.Accent, P.Dark ? .16f : .12f);
                    else if (hover) bg = Theme.Mix(BackColor, P.Text, .06f);
                    fg = selected ? (P.Dark ? P.AccentHover : P.AccentHover) : P.Text; break;
                case BtnKind.Tab:
                    if (hover && !selected) bg = Theme.Mix(BackColor, P.Text, .05f);
                    fg = selected ? P.Text : P.Muted; break;
            }
            if (!Enabled)
            {
                if (bg != Color.Empty) bg = Theme.Mix(bg, BackColor, .55f);
                fg = Theme.Mix(fg, BackColor, .5f);
            }
            if (bg != Color.Empty) Theme.FillRound(g, bg, r, rad);
            if (border != Color.Empty) Theme.DrawRound(g, border, r, rad);
            if (Focused && ShowFocusCues) Theme.DrawRound(g, P.Accent, new RectangleF(1.5f, 1.5f, Width - 3.5f, Height - 3.5f), rad);

            if (Kind == BtnKind.Nav && selected)
                Theme.FillRound(g, P.Accent, new RectangleF(Theme.Dpf(2), Height / 2f - Theme.Dpf(9), Theme.Dpf(3), Theme.Dpf(18)), Theme.Dpf(2));
            if (Kind == BtnKind.Tab && selected)
                Theme.FillRound(g, P.Accent, new RectangleF(Theme.Dpf(10), Height - Theme.Dpf(3), Width - Theme.Dpf(20), Theme.Dpf(3)), Theme.Dpf(1.5f));

            if (Glyph != null && string.IsNullOrEmpty(Text) && !iconOnly)
            {   // icon-only button (refresh etc.): glyph exactly in the middle, no text gap
                DrawT(g, Glyph, Theme.Glyph(11f), new Rectangle(0, 0, Width, Height), fg, CenterMid);
                return;
            }
            if (iconOnly && Glyph != null)
            {
                Color gc = (Kind == BtnKind.Nav && selected) ? P.Accent : fg;
                DrawT(g, Glyph, Theme.Glyph(12f), new Rectangle(0, 0, Width, Height), gc, CenterMid);
                return;
            }
            bool left = Kind == BtnKind.Nav;
            var font = Kind == BtnKind.Tab ? Theme.Bold : (Kind == BtnKind.Nav && selected ? Theme.Bold : Theme.Body);
            int gsz = Theme.Dp(16);
            int textW = TextRenderer.MeasureText(Text ?? "", font).Width;
            int contentW = textW + (Glyph != null ? gsz + Theme.Dp(10) : 0);
            int x = left ? Theme.Dp(14) : (Width - contentW) / 2;
            if (Glyph != null)
            {
                var gr = new Rectangle(x, 0, gsz + Theme.Dp(2), Height);
                Color gc = (Kind == BtnKind.Nav && selected) ? P.Accent : fg;
                DrawT(g,Glyph, Theme.Glyph(11f), gr, gc, CenterMid);
                x += gsz + Theme.Dp(10);
            }
            int right = Width - Theme.Dp(10);
            if (!string.IsNullOrEmpty(Badge))
            {
                int bw = TextRenderer.MeasureText(Badge, Theme.Small).Width + Theme.Dp(12);
                var br = new RectangleF(right - bw, Height / 2f - Theme.Dpf(10), bw, Theme.Dpf(20));
                Theme.FillRound(g, selected ? Theme.Mix(BackColor, P.Accent, .25f) : Theme.Mix(BackColor, P.Text, .08f), br, Theme.Dpf(10));
                DrawT(g,Badge, Theme.Small, Rectangle.Round(br), selected ? P.Text : P.Muted, CenterMid);
                right -= bw + Theme.Dp(6);
            }
            DrawT(g,Text, font, new Rectangle(x, 0, Math.Max(0, right - x + (left ? 0 : Theme.Dp(10))), Height), fg, left ? LeftMid : LeftMid);
        }
    }

    // ---------- Toggle switch ----------
    public class Toggle : FxControl
    {
        bool on, hover;
        public string Hint;
        public event EventHandler CheckedChanged;
        public bool Checked
        {
            get { return on; }
            set { if (on != value) { on = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); } }
        }
        public Toggle(string text, bool value)
        {
            Text = text; on = value; Height = Theme.Dp(34); Cursor = Cursors.Hand;
            SetStyle(ControlStyles.Selectable, true); TabStop = true;
        }
        protected override void OnClick(EventArgs e) { base.OnClick(e); if (Enabled) Checked = !Checked; }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Space) Checked = !Checked; }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e); var P = Theme.P;
            float w = Theme.Dpf(40), h = Theme.Dpf(20);
            var r = new RectangleF(1, (Height - h) / 2f, w, h);
            Color track = on ? P.Accent : (hover ? Theme.Mix(P.Surface2, P.Text, .12f) : Theme.Mix(P.Surface2, P.Text, .06f));
            if (!Enabled) track = Theme.Mix(track, BackColor, .5f);
            Theme.FillRound(g, track, r, h / 2);
            if (!on) Theme.DrawRound(g, P.Border, r, h / 2);
            float k = h - Theme.Dpf(8);
            float kx = on ? r.Right - k - Theme.Dpf(4) : r.X + Theme.Dpf(4);
            using (var b = new SolidBrush(on ? Color.White : P.Muted)) g.FillEllipse(b, kx, r.Y + Theme.Dpf(4), k, k);
            var tc = Enabled ? P.Text : P.Muted;
            DrawT(g,Text, Theme.Body, new Rectangle((int)(w + Theme.Dp(12)), 0, Width - (int)w - Theme.Dp(12), Height), tc, LeftMid);
            if (Focused && ShowFocusCues) Theme.DrawRound(g, P.Accent, new RectangleF(r.X - 2, r.Y - 2, r.Width + 3, r.Height + 3), h / 2 + 2);
        }
    }

    // ---------- Slider ----------
    public class Slider : FxControl
    {
        long min = 0, max = 100, val = 0;
        public long Step = 1;
        bool drag, hover;
        public event EventHandler ValueChanged;

        public Slider() { Height = Theme.Dp(26); Cursor = Cursors.Hand; SetStyle(ControlStyles.Selectable, true); TabStop = true; }

        public long Minimum { get { return min; } set { min = value; if (max < min) max = min; Invalidate(); } }
        public long Maximum { get { return max; } set { max = Math.Max(min, value); Invalidate(); } }
        public long Value
        {
            get { return val; }
            set
            {
                long v = Math.Max(min, Math.Min(max, value));
                if (v == val) return;
                val = v; Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }
        /// <summary>Sets the position without raising ValueChanged (for syncing from another input).</summary>
        public void SetSilently(long v) { val = Math.Max(min, Math.Min(max, v)); Invalidate(); }

        float Pad { get { return Theme.Dpf(9); } }
        float Frac { get { return max > min ? (float)(val - min) / (max - min) : 0f; } }

        void FromX(int x)
        {
            float f = Math.Max(0, Math.Min(1, (x - Pad) / Math.Max(1f, Width - Pad * 2)));
            long v = min + (long)Math.Round(f * (max - min));
            if (Step > 1) v = (long)Math.Round((double)v / Step) * Step;
            Value = v;
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (!Enabled) return; Focus(); drag = true; FromX(e.X); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (drag) FromX(e.X); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); drag = false; Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!Focused) { base.OnMouseWheel(e); return; }   // let the page scroll unless the slider is focused
            Value += Math.Sign(e.Delta) * Math.Max(1, Step);
        }
        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            long s = Math.Max(1, Step) * (e.Shift ? 10 : 1);
            if (e.KeyCode == Keys.Left) Value -= s;
            if (e.KeyCode == Keys.Right) Value += s;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e); var P = Theme.P;
            float th = Theme.Dpf(4), cy = Height / 2f, w = Width - Pad * 2;
            Color accent = Enabled ? P.Accent : P.Muted;
            Theme.FillRound(g, Theme.Mix(P.Surface2, P.Text, .08f), new RectangleF(Pad, cy - th / 2, w, th), th / 2);
            float x = Pad + w * Frac;
            if (x > Pad) Theme.FillRound(g, accent, new RectangleF(Pad, cy - th / 2, x - Pad, th), th / 2);
            float r = Theme.Dpf(drag || hover ? 8 : 7);
            using (var b = new SolidBrush(accent)) g.FillEllipse(b, x - r, cy - r, r * 2, r * 2);
            using (var b = new SolidBrush(P.Surface)) g.FillEllipse(b, x - r * .45f, cy - r * .45f, r * .9f, r * .9f);
            if (Focused && ShowFocusCues)
                using (var pen = new Pen(Theme.Mix(accent, BackColor, .5f), 2)) g.DrawEllipse(pen, x - r - 2, cy - r - 2, r * 2 + 4, r * 2 + 4);
        }
    }

    // ---------- Combo ----------
    public class ThemedCombo : ComboBox, IThemed
    {
        public ThemedCombo(params string[] items)
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            FlatStyle = FlatStyle.Flat;
            DrawMode = DrawMode.OwnerDrawFixed;
            ItemHeight = Theme.Dp(24);
            Font = Theme.Body;
            Items.AddRange(items);
        }
        public void ApplyTheme() { BackColor = Theme.P.Surface2; ForeColor = Theme.P.Text; Invalidate(); }

        // Windows draws the drop-down button and border with light system colours even in Flat style, and repaints them
        // on hover — painting our chrome over it afterwards made a light frame flash in the dark theme. The closed
        // DropDownList is therefore painted entirely by us (double-buffered), the system paint is skipped.
        [StructLayout(LayoutKind.Sequential)]
        struct PAINTSTRUCT { public IntPtr hdc; public bool fErase; public int l, t, r, b; public bool fRestore, fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved; }
        [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
        [DllImport("user32.dll")] static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
        bool hover;
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x000F && DropDownStyle == ComboBoxStyle.DropDownList && IsHandleCreated)   // WM_PAINT
            {
                PAINTSTRUCT ps;
                IntPtr hdc = BeginPaint(Handle, out ps);
                try
                {
                    using (var g = Graphics.FromHdc(hdc))
                    using (var buf = BufferedGraphicsManager.Current.Allocate(g, ClientRectangle))
                    {
                        PaintAll(buf.Graphics);
                        buf.Render(g);
                    }
                }
                finally { EndPaint(Handle, ref ps); }
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == 0x0014 && DropDownStyle == ComboBoxStyle.DropDownList) { m.Result = (IntPtr)1; return; }   // WM_ERASEBKGND
            base.WndProc(ref m);
            if (m.Msg == 0x0085) PaintChrome();
        }

        void PaintAll(Graphics g)
        {
            var P = Theme.P;
            var r = ClientRectangle;
            Color bg = Enabled ? (hover && !DroppedDown ? Theme.Mix(P.Surface2, P.Text, .04f) : P.Surface2) : Theme.Mix(P.Surface2, P.Surface, .5f);
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, r);
            int bw = SystemInformation.VerticalScrollBarWidth + 2;
            var tr = new Rectangle(Theme.Dp(6), 0, Math.Max(0, r.Width - bw - Theme.Dp(8)), r.Height);
            TextRenderer.DrawText(g, SelectedIndex >= 0 ? (Items[SelectedIndex] ?? "").ToString() : Text, Font, tr,
                Enabled ? P.Text : Theme.Mix(P.Text, P.Surface, .5f),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "", Theme.Glyph(8f), new Rectangle(r.Width - bw - 1, 0, bw, r.Height),
                Enabled ? P.Muted : Theme.Mix(P.Muted, P.Surface, .5f), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            using (var pen = new Pen(Focused || DroppedDown ? P.Accent : hover && Enabled ? Theme.Mix(P.Border, P.Text, .25f) : P.Border))
                g.DrawRectangle(pen, 0, 0, r.Width - 1, r.Height - 1);
        }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        void PaintChrome()
        {
            if (!IsHandleCreated) return;
            using (var g = Graphics.FromHwnd(Handle))
            {
                var P = Theme.P;
                var r = ClientRectangle;
                int bw = SystemInformation.VerticalScrollBarWidth + 2;
                Color bg = Enabled ? P.Surface2 : Theme.Mix(P.Surface2, P.Surface, .5f);
                using (var b = new SolidBrush(bg)) g.FillRectangle(b, r.Width - bw - 1, 1, bw, r.Height - 2);
                TextRenderer.DrawText(g, "", Theme.Glyph(8f), new Rectangle(r.Width - bw - 1, 0, bw, r.Height),
                    Enabled ? P.Muted : Theme.Mix(P.Muted, P.Surface, .5f), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                using (var pen = new Pen(Focused || DroppedDown ? P.Accent : P.Border)) g.DrawRectangle(pen, 0, 0, r.Width - 1, r.Height - 1);
                using (var pen = new Pen(bg)) g.DrawRectangle(pen, 1, 1, r.Width - 3, r.Height - 3);
            }
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var b = new SolidBrush(sel ? Theme.P.Accent : Theme.P.Surface2)) e.Graphics.FillRectangle(b, e.Bounds);
            var r = e.Bounds; r.X += Theme.Dp(6);
            TextRenderer.DrawText(e.Graphics, Items[e.Index].ToString(), Font, r, sel ? Theme.P.AccentText : Theme.P.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    // ---------- Visual blocks ----------
    public class StatTile : FxControl
    {
        public string Title, Value, Sub, Glyph;
        public Func<Color> ValueColor;
        public StatTile(string title, string value, string sub = null, string glyph = null)
        { Title = title; Value = value; Sub = sub; Glyph = glyph; Height = Theme.Dp(96); }

        public void Set(string value, string sub) { Value = value; Sub = sub; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e); var P = Theme.P;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.FillRound(g, P.Surface, r, Theme.Dpf(10));
            Theme.DrawRound(g, P.Border, r, Theme.Dpf(10));
            int pad = Theme.Dp(14);
            int tx = pad;
            if (Glyph != null)
            {
                DrawT(g,Glyph, Theme.Glyph(10f), new Rectangle(pad, pad, Theme.Dp(16), Theme.Dp(18)), P.Muted, CenterMid);
                tx += Theme.Dp(22);
            }
            DrawT(g,Title, Theme.Small, new Rectangle(tx, pad, Width - tx - pad, Theme.Dp(18)), P.Muted, LeftMid);
            DrawT(g,Value ?? "—", Theme.Big, new Rectangle(pad, pad + Theme.Dp(20), Width - pad * 2, Theme.Dp(36)),
                ValueColor != null ? ValueColor() : P.Text, LeftMid);
            if (!string.IsNullOrEmpty(Sub))
                DrawT(g,Sub, Theme.Small, new Rectangle(pad, Height - pad - Theme.Dp(16), Width - pad * 2, Theme.Dp(16)), P.Muted, LeftMid);
        }
    }

    public enum BannerKind { Info, Success, Warning, Danger }

    public class Banner : FxControl, IAutoHeight
    {
        public BannerKind Kind; public string Title, Body, Glyph;
        public Banner(BannerKind kind, string title, string body, string glyph = null)
        { Kind = kind; Title = title; Body = body; Glyph = glyph; }

        Color KC
        {
            get
            {
                switch (Kind)
                {
                    case BannerKind.Success: return Theme.P.Success;
                    case BannerKind.Warning: return Theme.P.Warning;
                    case BannerKind.Danger: return Theme.P.Danger;
                    default: return Theme.P.Info;
                }
            }
        }
        string G
        {
            get
            {
                if (Glyph != null) return Glyph;
                switch (Kind)
                {
                    case BannerKind.Success: return "";
                    case BannerKind.Warning: return "";
                    case BannerKind.Danger: return "";
                    default: return "";
                }
            }
        }
        int TextLeft { get { return Theme.Dp(64); } }

        public int HeightFor(int width)
        {
            int w = Math.Max(1, width - TextLeft - Theme.Dp(16));
            int h = Theme.Dp(16) + TextRenderer.MeasureText(Title ?? "", Theme.Section).Height;
            if (!string.IsNullOrEmpty(Body))
                h += Theme.Dp(4) + TextRenderer.MeasureText(Body, Theme.Body, new Size(w, 100000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
            return Math.Max(Theme.Dp(68), h + Theme.Dp(16));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e); var P = Theme.P; var kc = KC;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.FillRound(g, Theme.Mix(P.Surface, kc, P.Dark ? .14f : .08f), r, Theme.Dpf(10));
            Theme.DrawRound(g, Theme.Mix(P.Border, kc, .45f), r, Theme.Dpf(10));
            float cs = Theme.Dpf(36);
            var cr = new RectangleF(Theme.Dpf(16), Theme.Dpf(16), cs, cs);
            using (var b = new SolidBrush(Theme.Mix(P.Surface, kc, P.Dark ? .3f : .18f))) g.FillEllipse(b, cr);
            DrawT(g,G, Theme.Glyph(13f), Rectangle.Round(cr), kc, CenterMid);
            int x = TextLeft, y = Theme.Dp(16), w = Width - x - Theme.Dp(16);
            int th = TextRenderer.MeasureText(Title ?? "", Theme.Section).Height;
            DrawT(g,Title, Theme.Section, new Rectangle(x, y, w, th), P.Text, TextFormatFlags.Left | TextFormatFlags.Top);
            if (!string.IsNullOrEmpty(Body))
                DrawT(g,Body, Theme.Body, new Rectangle(x, y + th + Theme.Dp(4), w, Height), P.Muted, TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.Top);
        }
    }

    public class ProgressLine : FxControl
    {
        double v;
        public double Value { get { return v; } set { v = Math.Max(0, Math.Min(1, value)); Invalidate(); } }
        public ProgressLine() { Height = Theme.Dp(8); }

        // "working" shimmer for steps without a known percentage
        bool busyAnim;
        float phase;
        Timer anim;
        public bool Busy
        {
            get { return busyAnim; }
            set
            {
                if (busyAnim == value) return;
                busyAnim = value;
                if (value)
                {
                    if (anim == null) { anim = new Timer { Interval = 30 }; anim.Tick += (s, e) => { phase = (phase + 0.012f) % 1.4f; Invalidate(); }; }
                    anim.Start();
                }
                else if (anim != null) anim.Stop();
                Invalidate();
            }
        }
        protected override void Dispose(bool disposing) { if (disposing && anim != null) anim.Dispose(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prep(e); var P = Theme.P;
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, Theme.Mix(P.Surface2, P.Text, .05f), r, Height / 2f);
            float fw = (float)((Width - 1) * v);
            if (v > 0)
                Theme.FillRound(g, P.Accent, new RectangleF(0, 0, Math.Max(Height, fw), Height - 1), Height / 2f);
            if (busyAnim)
            {
                float w = Width * 0.22f, x = (phase - 0.2f) * Width;
                using (var path = Theme.Round(r, Height / 2f))
                {
                    var old = g.Clip;
                    g.SetClip(path);
                    using (var br = new LinearGradientBrush(new RectangleF(x - 1, 0, w + 2, Height), Color.Transparent, Color.Transparent, 0f))
                    {
                        var blend = new ColorBlend
                        {
                            Colors = new[] { Color.FromArgb(0, P.Accent), Color.FromArgb(P.Dark ? 200 : 170, Theme.Mix(P.Accent, Color.White, .35f)), Color.FromArgb(0, P.Accent) },
                            Positions = new[] { 0f, .5f, 1f },
                        };
                        br.InterpolationColors = blend;
                        g.FillRectangle(br, x, 0, w, Height);
                    }
                    g.Clip = old;
                }
            }
        }
    }

    public class KVGrid : FxControl, IAutoHeight
    {
        public readonly List<KeyValuePair<string, string>> Items = new List<KeyValuePair<string, string>>();
        public void Add(string k, string v) { Items.Add(new KeyValuePair<string, string>(k, string.IsNullOrEmpty(v) ? "—" : v)); }

        int Cols(int w) { return w > Theme.Dp(620) ? 2 : 1; }
        int RowH { get { return Theme.Dp(30); } }

        public int HeightFor(int width)
        {
            int c = Cols(width);
            return ((Items.Count + c - 1) / c) * RowH;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; var P = Theme.P;
            int cols = Cols(Width), gap = Theme.Dp(28);
            int cw = (Width - gap * (cols - 1)) / cols;
            int rows = (Items.Count + cols - 1) / cols;
            for (int i = 0; i < Items.Count; i++)
            {
                int col = i / rows, row = i % rows;
                int x = col * (cw + gap), y = row * RowH;
                int kw = Math.Min(Theme.Dp(170), cw * 45 / 100);
                DrawT(g,Items[i].Key, Theme.Body, new Rectangle(x, y, kw, RowH), P.Muted, LeftMid);
                DrawT(g,Items[i].Value, Theme.Body, new Rectangle(x + kw, y, cw - kw, RowH), P.Text, LeftMid);
                if (row < rows - 1)
                    using (var pen = new Pen(Theme.Mix(P.Surface, P.Border, .7f)))
                        g.DrawLine(pen, x, y + RowH - 1, x + cw, y + RowH - 1);
            }
        }
    }
}
