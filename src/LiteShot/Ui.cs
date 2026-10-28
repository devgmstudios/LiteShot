using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace LiteShot;

/// <summary>Shared drawing helpers: fonts, text measuring and the rounded shapes the glass design is built from.</summary>
internal static class Ui
{
    public static readonly FontFamily RegularFamily = Family("Segoe UI");
    public static readonly FontFamily SemiboldFamily = Family("Segoe UI Semibold", "Segoe UI");
    public static readonly StringFormat Typographic = CreateTypographic();

    static readonly Bitmap measureBitmap = new(1, 1);
    static readonly Graphics measure = CreateMeasure();

    public static FontFamily Family(params string[] names)
    {
        foreach (var name in names)
        {
            try { return new FontFamily(name); }
            catch (ArgumentException) { }
        }
        return FontFamily.GenericSansSerif;
    }

    public static Font MakeFont(float px, bool semibold = false) =>
        new(semibold ? SemiboldFamily : RegularFamily, Math.Max(1f, px), FontStyle.Regular, GraphicsUnit.Pixel);

    static StringFormat CreateTypographic()
    {
        var f = (StringFormat)StringFormat.GenericTypographic.Clone();
        f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
        return f;
    }

    static Graphics CreateMeasure()
    {
        var g = Graphics.FromImage(measureBitmap);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        return g;
    }

    public static SizeF Measure(string text, Font font)
    {
        if (text.Length == 0) return new SizeF(0, font.GetHeight());
        lock (measure) return measure.MeasureString(text, font, PointF.Empty, Typographic);
    }

    public static void DrawText(Graphics g, string text, Font font, Color color, PointF at)
    {
        using var b = new SolidBrush(color);
        g.DrawString(text, font, b, at, Typographic);
    }

    public static void DrawTextCentered(Graphics g, string text, Font font, Color color, RectangleF r)
    {
        var s = Measure(text, font);
        DrawText(g, text, font, color, new PointF(r.X + (r.Width - s.Width) / 2f, r.Y + (r.Height - s.Height) / 2f));
    }

    /// <summary>Rounded rectangle; a radius of half the height gives a capsule, of half the size a circle.</summary>
    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2f);
        if (radius <= 0.5f)
        {
            p.AddRectangle(r);
            return p;
        }
        float d = radius * 2f;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Color WithAlpha(Color c, int alpha) => Color.FromArgb(Math.Clamp(alpha, 0, 255), c.R, c.G, c.B);

    public static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    public static Rectangle Outer(RectangleF r, int pad) => Rectangle.FromLTRB(
        (int)MathF.Floor(r.Left) - pad, (int)MathF.Floor(r.Top) - pad,
        (int)MathF.Ceiling(r.Right) + pad, (int)MathF.Ceiling(r.Bottom) + pad);

    public static Rectangle Union(Rectangle a, Rectangle b)
    {
        if (a.Width <= 0 || a.Height <= 0) return b;
        if (b.Width <= 0 || b.Height <= 0) return a;
        return Rectangle.Union(a, b);
    }
}
