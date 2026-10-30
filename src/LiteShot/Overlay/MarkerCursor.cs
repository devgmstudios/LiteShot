using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace LiteShot.Overlay;

/// <summary>
/// The Marker tool's pointer: a highlighter pen pointing down-left, white with a dark outline so it reads on light and
/// dark captures, its chisel nib and band in the current colour. The hotspot is the tip of the nib. Drawn for the
/// monitor's scale, so it stays crisp at any DPI.
/// </summary>
internal static class MarkerCursor
{
    const int Grid = 32; // design size at 100% scale
    static readonly PointF Tip = new(3.5f, 28.5f);
    // Each colour and size is made once and kept for the life of the app: a handful of small cursors at most.
    static readonly Dictionary<(int Argb, int Size), Cursor> Made = new();

    public static Cursor For(Color color, float scale)
    {
        int size = Math.Max(Grid, (int)MathF.Round(Grid * scale));
        var key = (color.ToArgb(), size);
        if (Made.TryGetValue(key, out var cursor)) return cursor;
        using var picture = Render(color, size);
        cursor = FromBitmap(picture, Point.Round(new PointF(Tip.X * size / Grid, Tip.Y * size / Grid)));
        Made[key] = cursor;
        return cursor;
    }

    /// <summary>The cursor's picture, <paramref name="size"/> pixels square, with the tip at <see cref="Tip"/> (scaled).</summary>
    public static Bitmap Render(Color color, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float s = size / (float)Grid;
        g.ScaleTransform(s, s);
        g.TranslateTransform(Tip.X, Tip.Y);
        g.RotateTransform(-45);

        // Laid out along +x from the tip: chisel nib, collar, barrel with a coloured band, rounded end.
        var ink = Color.FromArgb(255, color);
        using var outline = new Pen(Color.FromArgb(235, 24, 26, 32), 1.3f) { LineJoin = LineJoin.Round };
        using var body = new SolidBrush(Color.White);
        using var tint = new SolidBrush(ink);
        using var nib = new GraphicsPath();
        nib.AddPolygon(new PointF[] { new(0, -1.5f), new(6, -3.3f), new(6, 3.3f), new(0, 1.5f) });
        using var barrel = Ui.RoundRect(new RectangleF(9, -5, 21, 10), 2.6f);
        using var collar = new GraphicsPath();
        collar.AddRectangle(new RectangleF(5.6f, -4.1f, 3.8f, 8.2f));

        g.FillPath(body, barrel);
        g.FillRectangle(tint, 22, -5, 3.2f, 10);
        g.DrawPath(outline, barrel);
        g.FillPath(body, collar);
        g.DrawPath(outline, collar);
        g.FillPath(tint, nib);
        g.DrawPath(outline, nib);
        return bmp;
    }

    static Cursor FromBitmap(Bitmap picture, Point hotspot)
    {
        IntPtr icon = picture.GetHicon();
        try
        {
            if (!Native.GetIconInfo(icon, out var info)) return Cursors.Cross;
            try
            {
                info.fIcon = false;
                info.xHotspot = hotspot.X;
                info.yHotspot = hotspot.Y;
                IntPtr handle = Native.CreateIconIndirect(ref info);
                return handle == IntPtr.Zero ? Cursors.Cross : new Cursor(handle);
            }
            finally
            {
                Native.DeleteObject(info.hbmMask);
                Native.DeleteObject(info.hbmColor);
            }
        }
        finally { Native.DestroyIcon(icon); }
    }
}
