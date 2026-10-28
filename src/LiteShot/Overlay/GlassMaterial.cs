using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LiteShot.Overlay;

/// <summary>
/// The glyph palette for one piece of glass. Like Liquid Glass, each panel adapts to what is behind it:
/// light content gets light glass with dark glyphs, dark content gets smoky glass with white glyphs.
/// </summary>
internal readonly record struct GlassTone(bool Light)
{
    public Color Glyph => SystemTheme.HighContrast ? SystemColors.WindowText
        : Light ? Color.FromArgb(28, 28, 30) : Color.White;
    public Color Secondary => SystemTheme.HighContrast ? SystemColors.GrayText : Ui.WithAlpha(Glyph, 150);
    public Color Disabled => SystemTheme.HighContrast ? SystemColors.GrayText : Ui.WithAlpha(Glyph, 85);
    public Color Hover => SystemTheme.HighContrast ? SystemColors.Highlight
        : Light ? Color.FromArgb(26, 0, 0, 0) : Color.FromArgb(40, 255, 255, 255);
}

/// <summary>
/// Renders the glass material for the overlay's functional layer (toolbars, popovers, labels, loupe).
/// The backdrop is real: a blurred, vibrancy-boosted copy of the frozen screen, slightly magnified to
/// read as the lensing of thick glass, topped with a tint, a soft top glow and a specular rim.
/// </summary>
internal sealed class GlassMaterial : IDisposable
{
    const int Down = 8;
    readonly Bitmap blurBright, blurDim;
    readonly byte[] brightPx, dimPx;
    readonly int sw, sh;
    readonly ImageAttributes clampEdges = new();

    public GlassMaterial(Bitmap shot, float dimBrightness)
    {
        clampEdges.SetWrapMode(WrapMode.TileFlipXY);
        sw = Math.Max(1, shot.Width / Down);
        sh = Math.Max(1, shot.Height / Down);
        blurBright = Build(shot, 1f);
        blurDim = Build(shot, dimBrightness);
        brightPx = Pixels(blurBright);
        dimPx = Pixels(blurDim);
    }

    Bitmap Build(Bitmap shot, float brightness)
    {
        var small = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(small))
        using (var attr = new ImageAttributes())
        {
            attr.SetColorMatrix(Vibrancy(1.5f, brightness));
            attr.SetWrapMode(WrapMode.TileFlipXY);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(shot, new Rectangle(0, 0, sw, sh), 0, 0, shot.Width, shot.Height, GraphicsUnit.Pixel, attr);
        }
        BoxBlur(small, 2);
        BoxBlur(small, 2);
        return small;
    }

    static ColorMatrix Vibrancy(float saturation, float k)
    {
        const float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
        float r = (1 - saturation) * lr, g = (1 - saturation) * lg, b = (1 - saturation) * lb;
        return new ColorMatrix(new[]
        {
            new[] { (r + saturation) * k, r * k, r * k, 0f, 0f },
            new[] { g * k, (g + saturation) * k, g * k, 0f, 0f },
            new[] { b * k, b * k, (b + saturation) * k, 0f, 0f },
            new[] { 0f, 0f, 0f, 1f, 0f },
            new[] { 0f, 0f, 0f, 0f, 1f },
        });
    }

    static byte[] Pixels(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var buffer = new byte[bmp.Width * bmp.Height * 4];
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, buffer, y * bmp.Width * 4, bmp.Width * 4);
            return buffer;
        }
        finally { bmp.UnlockBits(data); }
    }

    static unsafe void BoxBlur(Bitmap bmp, int radius)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        try
        {
            int w = bmp.Width, h = bmp.Height, stride = data.Stride;
            byte* basePtr = (byte*)data.Scan0;
            var line = new byte[Math.Max(w, h) * 4];
            for (int y = 0; y < h; y++) BlurLine(basePtr + y * stride, 4, w, radius, line);
            for (int x = 0; x < w; x++) BlurLine(basePtr + x * 4, stride, h, radius, line);
        }
        finally { bmp.UnlockBits(data); }
    }

    static unsafe void BlurLine(byte* start, int step, int count, int r, byte[] line)
    {
        for (int i = 0; i < count; i++)
        {
            byte* p = start + i * step;
            line[i * 4] = p[0]; line[i * 4 + 1] = p[1]; line[i * 4 + 2] = p[2]; line[i * 4 + 3] = p[3];
        }
        int window = r * 2 + 1;
        for (int c = 0; c < 4; c++)
        {
            int sum = 0;
            for (int k = -r; k <= r; k++) sum += line[Math.Clamp(k, 0, count - 1) * 4 + c];
            for (int i = 0; i < count; i++)
            {
                start[i * step + c] = (byte)(sum / window);
                sum += line[Math.Min(i + r + 1, count - 1) * 4 + c] - line[Math.Max(i - r, 0) * 4 + c];
            }
        }
    }

    /// <summary>Chooses light or dark glass from the average luminance of the backdrop under <paramref name="r"/>.</summary>
    public GlassTone ToneAt(RectangleF r, Rectangle lit)
    {
        if (SystemTheme.HighContrast) return new GlassTone(!SystemTheme.Dark);
        if (!SystemTheme.TransparencyEnabled) return new GlassTone(SystemTheme.AppsUseLightTheme);
        double sum = 0;
        int n = 0;
        for (int iy = 0; iy < 4; iy++)
            for (int ix = 0; ix < 8; ix++)
            {
                float x = r.X + (ix + 0.5f) * r.Width / 8f, y = r.Y + (iy + 0.5f) * r.Height / 4f;
                var px = lit.Contains((int)x, (int)y) ? brightPx : dimPx;
                int sx = Math.Clamp((int)(x / Down), 0, sw - 1), sy = Math.Clamp((int)(y / Down), 0, sh - 1);
                int i = (sy * sw + sx) * 4;
                sum += (0.0722 * px[i] + 0.7152 * px[i + 1] + 0.2126 * px[i + 2]) / 255.0;
                n++;
            }
        return new GlassTone(sum / n > 0.6);
    }

    /// <summary>Draws a glass panel shaped as a rounded rectangle (capsule or circle when the radius allows).</summary>
    public void Draw(Graphics g, RectangleF r, float radius, Rectangle lit, GlassTone tone, float s, bool shadow = true)
    {
        if (r.Width < 1 || r.Height < 1) return;
        using var shape = Ui.RoundRect(r, radius);
        if (SystemTheme.HighContrast)
        {
            using var bg = new SolidBrush(SystemColors.Window);
            using var border = new Pen(SystemColors.WindowText, Math.Max(1f, 2f * s));
            g.FillPath(bg, shape);
            g.DrawPath(border, shape);
            return;
        }

        if (shadow) DrawShadow(g, r, radius, s);

        var outer = g.Save();
        g.SetClip(shape, CombineMode.Intersect);
        if (SystemTheme.TransparencyEnabled)
        {
            Backdrop(g, blurDim, r);
            if (lit.IntersectsWith(Rectangle.Ceiling(r)))
            {
                var inner = g.Save();
                g.SetClip(lit, CombineMode.Intersect);
                Backdrop(g, blurBright, r);
                g.Restore(inner);
            }
        }
        using (var tint = new SolidBrush(Tint(tone))) g.FillRectangle(tint, r);
        var glowRect = RectangleF.Inflate(r, 0, 1);
        using (var glow = new LinearGradientBrush(glowRect, Color.White, Color.White, LinearGradientMode.Vertical))
        {
            glow.InterpolationColors = new ColorBlend
            {
                Colors = new[] { Color.FromArgb(tone.Light ? 70 : 34, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) },
                Positions = new[] { 0f, 0.55f, 1f },
            };
            g.FillRectangle(glow, r);
        }
        g.Restore(outer);

        DrawRim(g, r, radius, s, tone.Light ? 1f : 0.85f);
        using var edge = new Pen(Color.FromArgb(tone.Light ? 36 : 80, 0, 0, 0), 1f);
        g.DrawPath(edge, shape);
    }

    /// <summary>Specular edge light: bright where light enters (top-left), fainter where it exits (bottom-right).</summary>
    public static void DrawRim(Graphics g, RectangleF r, float radius, float s, float strength = 1f)
    {
        float inset = 0.8f * s;
        var rr = RectangleF.Inflate(r, -inset, -inset);
        if (rr.Width <= 1 || rr.Height <= 1) return;
        using var path = Ui.RoundRect(rr, Math.Max(0, radius - inset));
        using var brush = new LinearGradientBrush(RectangleF.Inflate(r, 1, 1), Color.White, Color.White, 55f);
        brush.InterpolationColors = new ColorBlend
        {
            Colors = new[]
            {
                Color.FromArgb((int)(210 * strength), 255, 255, 255),
                Color.FromArgb((int)(45 * strength), 255, 255, 255),
                Color.FromArgb((int)(18 * strength), 255, 255, 255),
                Color.FromArgb((int)(120 * strength), 255, 255, 255),
            },
            Positions = new[] { 0f, 0.38f, 0.66f, 1f },
        };
        using var pen = new Pen(brush, Math.Max(1f, 1.15f * s));
        g.DrawPath(pen, path);
    }

    void Backdrop(Graphics g, Bitmap src, RectangleF r)
    {
        // A slight magnification of what is behind reads as the lensing of thick glass.
        float lens = Math.Min(r.Width, r.Height) * 0.08f;
        var source = new RectangleF((r.X + lens) / Down, (r.Y + lens) / Down,
            Math.Max(0.5f, (r.Width - 2 * lens) / Down), Math.Max(0.5f, (r.Height - 2 * lens) / Down));
        var dest = new[] { new PointF(r.Left, r.Top), new PointF(r.Right, r.Top), new PointF(r.Left, r.Bottom) };
        var state = g.Save();
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(src, dest, source, GraphicsUnit.Pixel, clampEdges);
        g.Restore(state);
    }

    static Color Tint(GlassTone tone)
    {
        if (!SystemTheme.TransparencyEnabled)
            // Reduced transparency: frosted, near-opaque glass that keeps the same shape language.
            return tone.Light ? Color.FromArgb(244, 246, 246, 248) : Color.FromArgb(244, 38, 38, 42);
        return tone.Light ? Color.FromArgb(118, 255, 255, 255) : Color.FromArgb(70, 22, 22, 26);
    }

    public static void DrawShadow(Graphics g, RectangleF r, float radius, float s)
    {
        using var brush = new SolidBrush(Color.FromArgb(10, 0, 0, 0));
        for (int i = 5; i >= 1; i--)
        {
            float grow = i * 1.7f * s;
            var sr = new RectangleF(r.X - grow, r.Y - grow + 3f * s, r.Width + 2 * grow, r.Height + 2 * grow);
            using var path = Ui.RoundRect(sr, radius + grow);
            g.FillPath(brush, path);
        }
    }

    public void Dispose()
    {
        blurBright.Dispose();
        blurDim.Dispose();
        clampEdges.Dispose();
    }
}
