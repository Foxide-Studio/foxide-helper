using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace FoxideDisk
{
    public static class Logo
    {
        static PointF[] Pts(RectangleF r, params float[] xy)
        {
            var p = new PointF[xy.Length / 2];
            for (int i = 0; i < p.Length; i++)
                p[i] = new PointF(r.X + xy[i * 2] * r.Width, r.Y + xy[i * 2 + 1] * r.Height);
            return p;
        }

        /// <summary>Stylised fox head sitting on a rounded "disk" tile.</summary>
        public static void Draw(Graphics g, RectangleF r)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var orange = Color.FromArgb(0xF2, 0x7A, 0x2E);
            var dark = Color.FromArgb(0xB8, 0x4A, 0x10);
            var cream = Color.FromArgb(0xFF, 0xF4, 0xE8);
            var ink = Color.FromArgb(0x2A, 0x1C, 0x14);

            // head
            using (var b = new LinearGradientBrush(r, Color.FromArgb(0xFF, 0x92, 0x45), Color.FromArgb(0xE2, 0x5E, 0x14), 90f))
                g.FillPolygon(b, Pts(r, .10f, .06f, .38f, .30f, .62f, .30f, .90f, .06f, .96f, .52f, .50f, .96f, .04f, .52f));
            // inner ears
            using (var b = new SolidBrush(dark))
            {
                g.FillPolygon(b, Pts(r, .17f, .17f, .33f, .33f, .21f, .40f));
                g.FillPolygon(b, Pts(r, .83f, .17f, .67f, .33f, .79f, .40f));
            }
            // cheeks / muzzle
            using (var b = new SolidBrush(cream))
                g.FillPolygon(b, Pts(r, .04f, .52f, .30f, .58f, .50f, .70f, .70f, .58f, .96f, .52f, .50f, .96f));
            // eyes
            using (var b = new SolidBrush(ink))
            {
                float er = r.Width * .055f;
                g.FillEllipse(b, r.X + r.Width * .34f - er, r.Y + r.Height * .50f - er, er * 2, er * 2);
                g.FillEllipse(b, r.X + r.Width * .66f - er, r.Y + r.Height * .50f - er, er * 2, er * 2);
                float nr = r.Width * .065f;
                g.FillEllipse(b, r.X + r.Width * .5f - nr, r.Y + r.Height * .86f - nr * .8f, nr * 2, nr * 1.6f);
            }
            GC.KeepAlive(orange);
        }

        public static Bitmap Render(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                float pad = size * 0.04f;
                Draw(g, new RectangleF(pad, pad, size - pad * 2, size - pad * 2));
            }
            return bmp;
        }

        /// <summary>Writes a multi-resolution PNG-compressed .ico.</summary>
        public static void WriteIco(string path)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            var pngs = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
                using (var bmp = Render(sizes[i]))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    pngs[i] = ms.ToArray();
                }
            using (var fs = File.Create(path))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (var p in pngs) w.Write(p);
            }
        }
    }
}
