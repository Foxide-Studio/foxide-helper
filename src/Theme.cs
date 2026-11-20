using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FoxideDisk
{
    public class Palette
    {
        public bool Dark;
        public Color Back, Sidebar, ListBack, Surface, Surface2, Border, Text, Muted;
        public Color Accent, AccentHover, AccentText, Danger, Warning, Success, Info;
        public Color[] Series;
    }

    public interface IThemed { void ApplyTheme(); }
    public interface IAutoHeight { int HeightFor(int width); }

    public static class Theme
    {
        public static Palette P = Light();
        public static float Scale = 1f;
        public static string GlyphFamily = "Segoe MDL2 Assets";
        public static Font Body, Small, Bold, Title, Subtitle, Big, Section, Mono;
        static readonly Dictionary<float, Font> glyphCache = new Dictionary<float, Font>();

        public static event EventHandler Changed;

        public static void Init(float dpiScale)
        {
            Scale = dpiScale <= 0 ? 1f : dpiScale;
            Body = new Font("Segoe UI", 9.5f);
            Small = new Font("Segoe UI", 8.5f);
            Bold = new Font("Segoe UI Semibold", 9.5f);
            Section = new Font("Segoe UI Semibold", 11f);
            Subtitle = new Font("Segoe UI", 10f);
            Title = new Font("Segoe UI Semibold", 17f);
            Big = new Font("Segoe UI Semibold", 18f);
            Mono = new Font("Consolas", 9f);
            try
            {
                using (var fonts = new InstalledFontCollection())
                    foreach (var f in fonts.Families)
                        if (f.Name == "Segoe Fluent Icons") { GlyphFamily = f.Name; break; }
            }
            catch { }
        }

        public static Font Glyph(float size)
        {
            Font f;
            if (!glyphCache.TryGetValue(size, out f))
            {
                f = new Font(GlyphFamily, size);
                glyphCache[size] = f;
            }
            return f;
        }

        public static int Dp(int v) { return (int)Math.Round(v * Scale); }
        public static float Dpf(float v) { return v * Scale; }

        public static bool SystemIsDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("AppsUseLightTheme");
                        if (v is int) return (int)v == 0;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>mode: 0 = system, 1 = light, 2 = dark</summary>
        public static void Set(int mode)
        {
            bool dark = mode == 2 || (mode == 0 && SystemIsDark());
            P = dark ? Dark() : Light();
            if (Changed != null) Changed(null, EventArgs.Empty);
        }

        static Color H(string hex) { return ColorTranslator.FromHtml(hex); }

        public static Palette Light()
        {
            return new Palette
            {
                Dark = false,
                Back = H("#F5F6F8"), Sidebar = H("#ECEEF2"), ListBack = H("#F0F2F5"),
                Surface = H("#FFFFFF"), Surface2 = H("#ECEEF2"), Border = H("#DCE0E6"),
                Text = H("#1B1E24"), Muted = H("#6B7280"),
                Accent = H("#E8661C"), AccentHover = H("#D45A13"), AccentText = Color.White,
                Danger = H("#D93C3C"), Warning = H("#C98A10"), Success = H("#2E9E5B"), Info = H("#2F6FDB"),
                Series = new[] { H("#E8661C"), H("#2F6FDB"), H("#14A38B"), H("#8B5CF6"), H("#D9468F") }
            };
        }

        public static Palette Dark()
        {
            return new Palette
            {
                Dark = true,
                Back = H("#16181D"), Sidebar = H("#1B1E24"), ListBack = H("#191C21"),
                Surface = H("#22262D"), Surface2 = H("#2C3139"), Border = H("#353B45"),
                Text = H("#ECEEF2"), Muted = H("#9AA2AE"),
                Accent = H("#F27A2E"), AccentHover = H("#FF8C45"), AccentText = Color.White,
                Danger = H("#F05252"), Warning = H("#E8B04A"), Success = H("#3DBE74"), Info = H("#5B9BFF"),
                Series = new[] { H("#F27A2E"), H("#5B9BFF"), H("#2CC4A8"), H("#A47BFF"), H("#F06BAE") }
            };
        }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float rad)
        {
            using (var p = Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void DrawRound(Graphics g, Color c, RectangleF r, float rad)
        {
            using (var p = Round(r, rad)) using (var pen = new Pen(c, 1f)) g.DrawPath(pen, p);
        }

        public static void Apply(Control c)
        {
            var t = c as IThemed;
            if (t != null) t.ApplyTheme();
            else if (c is TextBox || c is NumericUpDown)
            {
                c.BackColor = P.Surface2;
                c.ForeColor = P.Text;
                DarkScrollbars(c);
                var nud = c as NumericUpDown;
                if (nud != null && nud.Controls.Count > 0) nud.Controls[0].Visible = false;   // light spin buttons; the slider / keyboard is used instead
            }
            else if (c is Form)
            {
                c.BackColor = P.Back;
                c.ForeColor = P.Text;
            }
            else if (c.Parent != null)
            {
                c.BackColor = c.Parent.BackColor;
                c.ForeColor = P.Text;
            }
            foreach (Control ch in c.Controls) Apply(ch);
            c.Invalidate();
        }

        /// <summary>Dark scrollbars for native controls (Windows 10 1809+).</summary>
        public static void DarkScrollbars(Control c)
        {
            if (c.IsHandleCreated) Native.SetWindowTheme(c.Handle, P.Dark ? "DarkMode_Explorer" : "Explorer", null);
            else if (c.Tag as string != "darkhook")
            {
                c.Tag = "darkhook";
                c.HandleCreated += (s, e) => Native.SetWindowTheme(((Control)s).Handle, P.Dark ? "DarkMode_Explorer" : "Explorer", null);
            }
        }

        public static void ApplyTitleBar(Form f)
        {
            if (!f.IsHandleCreated) return;
            try
            {
                int v = P.Dark ? 1 : 0;
                if (Native.DwmSetWindowAttribute(f.Handle, 20, ref v, 4) != 0)
                    Native.DwmSetWindowAttribute(f.Handle, 19, ref v, 4);
            }
            catch { }
        }
    }
}
