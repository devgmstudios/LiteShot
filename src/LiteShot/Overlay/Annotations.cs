using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace LiteShot.Overlay;

/// <summary>A drawn object. Coordinates are overlay pixels; <see cref="Size"/> is stroke width, font size, badge radius or block size.</summary>
internal abstract class Annotation
{
    public Color Color;
    public float Size;

    public abstract void Draw(Graphics g, Bitmap source);
    public abstract RectangleF Bounds { get; }
    public abstract bool Hit(PointF p, float tolerance);
    public abstract void Move(float dx, float dy);
    public virtual bool IsDegenerate => false;

    public Annotation Clone()
    {
        var copy = (Annotation)MemberwiseClone();
        copy.DeepCopy();
        return copy;
    }

    protected virtual void DeepCopy() { }

    protected Pen StrokePen(Color color) =>
        new(color, Size) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

    protected static float DistToSegment(PointF p, PointF a, PointF b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
        float t = l2 == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2, 0, 1);
        float px = a.X + t * dx - p.X, py = a.Y + t * dy - p.Y;
        return MathF.Sqrt(px * px + py * py);
    }
}

internal abstract class TwoPointShape : Annotation
{
    public PointF A, B;

    public RectangleF Rect => RectangleF.FromLTRB(Math.Min(A.X, B.X), Math.Min(A.Y, B.Y), Math.Max(A.X, B.X), Math.Max(A.Y, B.Y));
    public override RectangleF Bounds => RectangleF.Inflate(Rect, Size + 2, Size + 2);
    public override bool IsDegenerate => Math.Abs(A.X - B.X) < 3 && Math.Abs(A.Y - B.Y) < 3;

    public override void Move(float dx, float dy)
    {
        A = new PointF(A.X + dx, A.Y + dy);
        B = new PointF(B.X + dx, B.Y + dy);
    }
}

internal sealed class LineShape : TwoPointShape
{
    public override void Draw(Graphics g, Bitmap source)
    {
        using var pen = StrokePen(Color);
        g.DrawLine(pen, A, B);
    }

    public override bool Hit(PointF p, float tol) => DistToSegment(p, A, B) <= Size / 2 + tol;
}

internal sealed class ArrowShape : TwoPointShape
{
    float HeadLength(float len) => MathF.Min(MathF.Max(12f, Size * 3.6f + 6f), len * 0.75f);

    public override RectangleF Bounds => RectangleF.Inflate(Rect, Size * 3f + 8, Size * 3f + 8);

    public override void Draw(Graphics g, Bitmap source)
    {
        float dx = B.X - A.X, dy = B.Y - A.Y, len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1) return;
        float ux = dx / len, uy = dy / len;
        float head = HeadLength(len), half = head * 0.58f;
        var basePt = new PointF(B.X - ux * head, B.Y - uy * head);
        var left = new PointF(basePt.X - uy * half, basePt.Y + ux * half);
        var right = new PointF(basePt.X + uy * half, basePt.Y - ux * half);

        using (var pen = StrokePen(Color))
        {
            pen.EndCap = LineCap.Flat;
            // Tuck the shaft under the head so the joint never shows a gap.
            g.DrawLine(pen, A, new PointF(B.X - ux * head * 0.7f, B.Y - uy * head * 0.7f));
        }
        using var path = new GraphicsPath();
        path.AddPolygon(new[] { B, left, right });
        using var brush = new SolidBrush(Color);
        g.FillPath(brush, path);
        using var soft = new Pen(Color, Math.Max(1f, Size * 0.45f)) { LineJoin = LineJoin.Round };
        g.DrawPath(soft, path);
    }

    public override bool Hit(PointF p, float tol)
    {
        float len = MathF.Sqrt((B.X - A.X) * (B.X - A.X) + (B.Y - A.Y) * (B.Y - A.Y));
        return DistToSegment(p, A, B) <= MathF.Max(Size / 2, len > 0 ? HeadLength(len) * 0.4f : 0) + tol;
    }
}

internal sealed class RectShape : TwoPointShape
{
    public override void Draw(Graphics g, Bitmap source)
    {
        var r = Rect;
        using var pen = new Pen(Color, Size) { LineJoin = LineJoin.Miter };
        g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
    }

    public override bool Hit(PointF p, float tol)
    {
        float band = Size / 2 + tol;
        var outer = RectangleF.Inflate(Rect, band, band);
        var inner = RectangleF.Inflate(Rect, -band, -band);
        return outer.Contains(p) && !(inner.Width > 0 && inner.Height > 0 && inner.Contains(p));
    }
}

internal sealed class EllipseShape : TwoPointShape
{
    public override void Draw(Graphics g, Bitmap source)
    {
        using var pen = StrokePen(Color);
        g.DrawEllipse(pen, Rect);
    }

    public override bool Hit(PointF p, float tol)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(Rect);
        using var pen = new Pen(Color.Black, Size + tol * 2);
        return path.IsOutlineVisible(p, pen);
    }
}

/// <summary>Pixelates the screen under a rectangle. Reads the original capture, never other annotations.</summary>
internal sealed class PixelateShape : TwoPointShape
{
    Bitmap? cache;
    Rectangle cacheRect;
    int cacheBlock;

    public override RectangleF Bounds => RectangleF.Inflate(Rect, 2, 2);

    public override void Draw(Graphics g, Bitmap source)
    {
        var r = Rectangle.Intersect(Rectangle.Round(Rect), new Rectangle(Point.Empty, source.Size));
        if (r.Width < 1 || r.Height < 1) return;
        int block = Math.Max(2, (int)MathF.Round(Size));
        if (cache == null || cacheRect != r || cacheBlock != block)
        {
            cache = Build(source, r, block);
            cacheRect = r;
            cacheBlock = block;
        }
        var state = g.Save();
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(cache, r, 0, 0, r.Width, r.Height, GraphicsUnit.Pixel);
        g.Restore(state);
    }

    static Bitmap Build(Bitmap src, Rectangle r, int block)
    {
        int w = Math.Max(1, (r.Width + block - 1) / block), h = Math.Max(1, (r.Height + block - 1) / block);
        using var small = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(small))
        using (var attr = new ImageAttributes())
        {
            attr.SetWrapMode(WrapMode.TileFlipXY);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(src, new Rectangle(0, 0, w, h), r.X, r.Y, w * block, h * block, GraphicsUnit.Pixel, attr);
        }
        var result = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(result))
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(small, new Rectangle(0, 0, w * block, h * block), 0, 0, w, h, GraphicsUnit.Pixel);
        }
        return result;
    }

    public override bool Hit(PointF p, float tol) => RectangleF.Inflate(Rect, tol, tol).Contains(p);
}

/// <summary>
/// Blurs the screen under a rounded rectangle (names), or an oval when <see cref="Oval"/> is set (faces).
/// The area is shrunk to a few pixels per letter before it's smoothed, so the blur can't be reversed.
/// </summary>
internal sealed class BlurShape : TwoPointShape
{
    public bool Oval;
    /// <summary>Added by "Hide personal info"; switching it off removes these and keeps the user's own blurs.</summary>
    public bool Auto;
    Bitmap? cache;
    Rectangle cacheRect;
    int cacheBlock;

    public override RectangleF Bounds => RectangleF.Inflate(Rect, 2, 2);

    GraphicsPath Outline()
    {
        var r = Rect;
        if (!Oval) return Ui.RoundRect(r, Math.Min(Math.Min(r.Width, r.Height) / 2, Math.Max(3, Size)));
        var path = new GraphicsPath();
        path.AddEllipse(r);
        return path;
    }

    public override void Draw(Graphics g, Bitmap source)
    {
        var r = Rectangle.Intersect(Rectangle.Round(Rect), new Rectangle(Point.Empty, source.Size));
        if (r.Width < 2 || r.Height < 2) return;
        int block = Math.Max(2, (int)MathF.Round(Size));
        if (cache == null || cacheRect != r || cacheBlock != block)
        {
            cache = Build(source, r, block);
            cacheRect = r;
            cacheBlock = block;
        }
        var state = g.Save();
        using (var outline = Outline()) g.SetClip(outline, CombineMode.Intersect);
        g.DrawImage(cache, r.X, r.Y, r.Width, r.Height);
        g.Restore(state);
    }

    internal static Bitmap Build(Bitmap src, Rectangle r, int block)
    {
        int w = Math.Max(1, (r.Width + block - 1) / block), h = Math.Max(1, (r.Height + block - 1) / block);
        using var small = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        using (var attr = new ImageAttributes())
        {
            attr.SetWrapMode(WrapMode.TileFlipXY);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(src, new Rectangle(0, 0, w, h), r.X, r.Y, r.Width, r.Height, GraphicsUnit.Pixel, attr);
        }
        BoxBlur(small);
        var result = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(result))
        using (var attr = new ImageAttributes())
        {
            // Bicubic upscaling of the tiny copy is what turns the blocks into a smooth blur.
            attr.SetWrapMode(WrapMode.TileFlipXY);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(small, new Rectangle(0, 0, r.Width, r.Height), 0, 0, w, h, GraphicsUnit.Pixel, attr);
        }
        return result;
    }

    /// <summary>One 3×3 box blur pass, in place, so neighboring blocks blend.</summary>
    static unsafe void BoxBlur(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        if (w < 2 && h < 2) return;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var copy = new byte[data.Stride * h];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, copy, 0, copy.Length);
            byte* dst = (byte*)data.Scan0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        int sum = 0, n = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int yy = y + dy;
                            if (yy < 0 || yy >= h) continue;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int xx = x + dx;
                                if (xx < 0 || xx >= w) continue;
                                sum += copy[yy * data.Stride + xx * 4 + c];
                                n++;
                            }
                        }
                        dst[y * data.Stride + x * 4 + c] = (byte)(sum / n);
                    }
        }
        finally { bmp.UnlockBits(data); }
    }

    public override bool Hit(PointF p, float tol)
    {
        if (!Oval) return RectangleF.Inflate(Rect, tol, tol).Contains(p);
        var r = RectangleF.Inflate(Rect, tol, tol);
        float nx = (p.X - (r.X + r.Width / 2)) / (r.Width / 2), ny = (p.Y - (r.Y + r.Height / 2)) / (r.Height / 2);
        return nx * nx + ny * ny <= 1;
    }
}

/// <summary>
/// FacePrivacy.ai's blur brush: wherever the round brush goes, a strong blur of the picture underneath shows through —
/// the same approach as their free face blur tool. <see cref="Annotation.Size"/> is the brush diameter.
/// </summary>
internal sealed class BlurBrushShape : Annotation
{
    public List<PointF> Points = new();
    public int Strength = 12;

    // One blurred copy of the whole capture per strength, shared by every stroke: painting stays fast.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Bitmap, Dictionary<int, Bitmap>> Blurred = new();

    protected override void DeepCopy() => Points = new List<PointF>(Points);

    public override RectangleF Bounds => BoundsOf(0, Points.Count);

    /// <summary>The area the last few points touched, for repainting while the brush moves.</summary>
    public RectangleF TailBounds(int count) => BoundsOf(Math.Max(0, Points.Count - count), Points.Count);

    RectangleF BoundsOf(int from, int to)
    {
        if (to <= from) return RectangleF.Empty;
        float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
        for (int i = from; i < to; i++)
        {
            var p = Points[i];
            l = Math.Min(l, p.X); t = Math.Min(t, p.Y); r = Math.Max(r, p.X); b = Math.Max(b, p.Y);
        }
        float pad = Size / 2 + 2;
        return RectangleF.FromLTRB(l - pad, t - pad, r + pad, b + pad);
    }

    GraphicsPath Outline()
    {
        var path = new GraphicsPath(FillMode.Winding);
        if (Points.Count == 1)
        {
            path.AddEllipse(Points[0].X - Size / 2, Points[0].Y - Size / 2, Size, Size);
            return path;
        }
        path.AddLines(Points.ToArray());
        using var pen = new Pen(Color.Black, Size) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        path.Widen(pen);
        return path;
    }

    public override void Draw(Graphics g, Bitmap source)
    {
        if (Points.Count == 0) return;
        var all = new Rectangle(Point.Empty, source.Size);
        var cache = Blurred.GetOrCreateValue(source);
        if (!cache.TryGetValue(Strength, out var blurred))
            cache[Strength] = blurred = BlurShape.Build(source, all, Strength);
        var state = g.Save();
        using (var outline = Outline()) g.SetClip(outline, CombineMode.Intersect);
        var r = Rectangle.Intersect(Rectangle.Ceiling(Bounds), all);
        if (r.Width > 0 && r.Height > 0) g.DrawImage(blurred, r, r, GraphicsUnit.Pixel);
        g.Restore(state);
    }

    public override bool Hit(PointF p, float tol)
    {
        float limit = Size / 2 + tol;
        if (Points.Count == 1) return DistToSegment(p, Points[0], Points[0]) <= limit;
        for (int i = 1; i < Points.Count; i++)
            if (DistToSegment(p, Points[i - 1], Points[i]) <= limit) return true;
        return false;
    }

    public override void Move(float dx, float dy)
    {
        for (int i = 0; i < Points.Count; i++) Points[i] = new PointF(Points[i].X + dx, Points[i].Y + dy);
    }
}

/// <summary>Freehand pen, or a translucent highlighter when <see cref="Highlighter"/> is set.</summary>
internal sealed class StrokeShape : Annotation
{
    public List<PointF> Points = new();
    public bool Highlighter;

    protected override void DeepCopy() => Points = new List<PointF>(Points);

    public override RectangleF Bounds => BoundsOf(0, Points.Count);
    public RectangleF TailBounds(int count) => BoundsOf(Math.Max(0, Points.Count - count), Points.Count);

    RectangleF BoundsOf(int from, int to)
    {
        if (to <= from) return RectangleF.Empty;
        float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
        for (int i = from; i < to; i++)
        {
            var p = Points[i];
            l = Math.Min(l, p.X); t = Math.Min(t, p.Y); r = Math.Max(r, p.X); b = Math.Max(b, p.Y);
        }
        float pad = Size / 2 + 3;
        return RectangleF.FromLTRB(l - pad, t - pad, r + pad, b + pad);
    }

    public override void Draw(Graphics g, Bitmap source)
    {
        if (Points.Count == 0) return;
        var color = Highlighter ? Color.FromArgb(105, Color) : Color;
        if (Points.Count == 1)
        {
            using var dot = new SolidBrush(color);
            var p = Points[0];
            g.FillEllipse(dot, p.X - Size / 2, p.Y - Size / 2, Size, Size);
            return;
        }
        using var pen = StrokePen(color);
        if (Points.Count == 2) g.DrawLine(pen, Points[0], Points[1]);
        else g.DrawCurve(pen, Points.ToArray(), 0.4f);
    }

    public override bool Hit(PointF p, float tol)
    {
        float limit = Size / 2 + tol;
        if (Points.Count == 1) return DistToSegment(p, Points[0], Points[0]) <= limit;
        for (int i = 1; i < Points.Count; i++)
            if (DistToSegment(p, Points[i - 1], Points[i]) <= limit) return true;
        return false;
    }

    public override void Move(float dx, float dy)
    {
        for (int i = 0; i < Points.Count; i++) Points[i] = new PointF(Points[i].X + dx, Points[i].Y + dy);
    }
}

/// <summary>Numbered step badge (1, 2, 3…) for walkthrough screenshots.</summary>
internal sealed class CounterShape : Annotation
{
    static readonly FontFamily NumberFamily = Ui.Family("Segoe UI", "Arial");

    public PointF Center;
    public int Number;

    public override RectangleF Bounds => new(Center.X - Size - 4, Center.Y - Size - 4, Size * 2 + 8, Size * 2 + 10);

    public override void Draw(Graphics g, Bitmap source)
    {
        float r = Size;
        var rect = new RectangleF(Center.X - r, Center.Y - r, r * 2, r * 2);
        using (var shadow = new SolidBrush(Color.FromArgb(55, 0, 0, 0)))
            g.FillEllipse(shadow, rect.X, rect.Y + r * 0.1f, rect.Width, rect.Height);
        using (var fill = new SolidBrush(Color)) g.FillEllipse(fill, rect);
        using (var ring = new Pen(Color.FromArgb(230, 255, 255, 255), Math.Max(1.5f, r * 0.12f))) g.DrawEllipse(ring, rect);

        string text = Number.ToString();
        using var path = new GraphicsPath();
        path.AddString(text, NumberFamily, (int)FontStyle.Bold, r * (text.Length > 1 ? 0.95f : 1.15f), PointF.Empty, StringFormat.GenericTypographic);
        var bb = path.GetBounds();
        using (var m = new Matrix())
        {
            m.Translate(Center.X - bb.X - bb.Width / 2, Center.Y - bb.Y - bb.Height / 2);
            path.Transform(m);
        }
        using var ink = new SolidBrush(Ui.Luminance(Color) > 0.65 ? Color.FromArgb(28, 28, 30) : Color.White);
        g.FillPath(ink, path);
    }

    public override bool Hit(PointF p, float tol) =>
        (p.X - Center.X) * (p.X - Center.X) + (p.Y - Center.Y) * (p.Y - Center.Y) <= (Size + tol) * (Size + tol);

    public override void Move(float dx, float dy) => Center = new PointF(Center.X + dx, Center.Y + dy);
}
