using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace LiteShot;

/// <summary>
/// Vector icons on a 24×24 grid, so they stay crisp at every DPI. Drawn in the spirit of SF Symbols:
/// one stroke weight, round caps and joins, soft corners, and Apple's shapes for familiar actions.
/// </summary>
internal static class Icons
{
    public delegate void Glyph(Graphics g, Pen pen, Brush fill);

    public static void Draw(Graphics g, RectangleF r, Color color, Glyph glyph, float weight = 1.8f)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TranslateTransform(r.X, r.Y);
        g.ScaleTransform(r.Width / 24f, r.Height / 24f);
        using var pen = new Pen(color, weight) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var fill = new SolidBrush(color);
        glyph(g, pen, fill);
        g.Restore(state);
    }

    public static Bitmap Render(Glyph glyph, int size, Color color)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        Draw(g, new RectangleF(0, 0, size, size), color, glyph, size <= 20 ? 2f : 1.8f);
        return bmp;
    }

    static PointF[] P(params float[] v)
    {
        var pts = new PointF[v.Length / 2];
        for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(v[i * 2], v[i * 2 + 1]);
        return pts;
    }

    /// <summary>Draws <paramref name="body"/> rotated about the center of the grid.</summary>
    static void Rotated(Graphics g, float degrees, Action body)
    {
        var state = g.Save();
        g.TranslateTransform(12, 12);
        g.RotateTransform(degrees);
        g.TranslateTransform(-12, -12);
        body();
        g.Restore(state);
    }

    static GraphicsPath Round(float x, float y, float w, float h, float r) => Ui.RoundRect(new RectangleF(x, y, w, h), r);

    /// <summary>A glyph from Segoe UI, filled and centered on (<paramref name="cx"/>, <paramref name="cy"/>).</summary>
    static void Letters(Graphics g, Brush f, string text, float em, float cx, float baseline, FontStyle style = FontStyle.Regular)
    {
        using var family = new FontFamily("Segoe UI");
        using var path = new GraphicsPath();
        path.AddString(text, family, (int)style, em, PointF.Empty, StringFormat.GenericTypographic);
        var b = path.GetBounds();
        using var m = new Matrix();
        m.Translate(cx - b.X - b.Width / 2, baseline - b.Bottom);
        path.Transform(m);
        g.FillPath(f, path);
    }

    // ───────────────────────────── drawing tools ─────────────────────────────

    /// <summary>pencil</summary>
    public static void Pen(Graphics g, Pen p, Brush f) => Rotated(g, -45, () =>
    {
        using var body = new GraphicsPath();
        body.AddLine(7.6f, 9.6f, 18.2f, 9.6f);
        body.AddArc(15.8f, 9.6f, 4.8f, 4.8f, 270, 180);
        body.AddLine(18.2f, 14.4f, 7.6f, 14.4f);
        body.AddLine(7.6f, 14.4f, 2.6f, 12f);
        body.CloseFigure();
        g.DrawPath(p, body);
        g.DrawLine(p, 7.6f, 9.6f, 7.6f, 14.4f);
        g.DrawLine(p, 16.6f, 9.8f, 16.6f, 14.2f);
    });

    /// <summary>line.diagonal</summary>
    public static void Line(Graphics g, Pen p, Brush f) => g.DrawLine(p, 5, 19, 19, 5);

    /// <summary>arrow.up.right</summary>
    public static void Arrow(Graphics g, Pen p, Brush f)
    {
        g.DrawLine(p, 5.5f, 18.5f, 17.5f, 6.5f);
        g.DrawLines(p, P(9, 6, 18, 6, 18, 15));
    }

    /// <summary>rectangle</summary>
    public static void Rect(Graphics g, Pen p, Brush f)
    {
        using var path = Round(3.5f, 5.5f, 17, 13, 3.2f);
        g.DrawPath(p, path);
    }

    /// <summary>circle</summary>
    public static void Ellipse(Graphics g, Pen p, Brush f) => g.DrawEllipse(p, 3.5f, 3.5f, 17, 17);

    /// <summary>highlighter: a chisel-tip marker over a line of ink.</summary>
    public static void Marker(Graphics g, Pen p, Brush f)
    {
        Rotated(g, -45, () =>
        {
            using var body = new GraphicsPath();
            body.AddLine(9.5f, 8.6f, 17.6f, 8.6f);
            body.AddArc(14.8f, 8.6f, 6.8f, 6.8f, 270, 180);
            body.AddLine(17.6f, 15.4f, 9.5f, 15.4f);
            body.CloseFigure();
            g.DrawPath(p, body);
            g.DrawPolygon(p, P(9.5f, 10.2f, 5.8f, 10.2f, 3.6f, 11.6f, 3.6f, 13.8f, 9.5f, 13.8f));
        });
        using var ink = Round(12.5f, 19.4f, 8.5f, 2.2f, 1.1f);
        g.FillPath(f, ink);
    }

    /// <summary>textformat: "Aa"</summary>
    public static void Text(Graphics g, Pen p, Brush f)
    {
        Letters(g, f, "A", 17.5f, 8.2f, 19.5f);
        Letters(g, f, "a", 13f, 17.4f, 19.5f);
    }

    /// <summary>1.circle</summary>
    public static void Counter(Graphics g, Pen p, Brush f)
    {
        g.DrawEllipse(p, 3, 3, 18, 18);
        Letters(g, f, "1", 12.5f, 12.2f, 16.6f, FontStyle.Bold);
    }

    /// <summary>checkerboard in a rounded square</summary>
    public static void Pixelate(Graphics g, Pen p, Brush f)
    {
        var box = new RectangleF(3.5f, 3.5f, 17, 17);
        using var path = Ui.RoundRect(box, 4f);
        var state = g.Save();
        g.SetClip(path, CombineMode.Intersect);
        float c = box.Width / 4f;
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                if ((x + y) % 2 == 0) g.FillRectangle(f, box.X + x * c, box.Y + y * c, c, c);
        g.Restore(state);
        g.DrawPath(p, path);
    }

    /// <summary>Blur faces: a head whose face has turned into a mosaic, over shoulders.</summary>
    public static void FaceBlur(Graphics g, Pen p, Brush f)
    {
        g.DrawEllipse(p, 6.6f, 3.8f, 10.8f, 10.8f);
        using (var shoulders = new GraphicsPath())
        {
            shoulders.AddBezier(4.6f, 21, 5.2f, 17.4f, 8.2f, 15.1f, 12, 15.1f);
            shoulders.AddBezier(12, 15.1f, 15.8f, 15.1f, 18.8f, 17.4f, 19.4f, 21);
            g.DrawPath(p, shoulders);
        }
        var color = p.Color;
        using var faint = new SolidBrush(Color.FromArgb(color.A / 2, color));
        foreach (var (x, y, solid) in new[] { (9.3f, 6.5f, true), (12.2f, 9.4f, true), (12.2f, 6.5f, false), (9.3f, 9.4f, false) })
            using (var tile = Round(x, y, 2.5f, 2.5f, 0.5f)) g.FillPath(solid ? f : faint, tile);
    }

    /// <summary>eye.slash: hide personal info</summary>
    public static void HidePersonal(Graphics g, Pen p, Brush f)
    {
        using (var eye = new GraphicsPath())
        {
            eye.AddBezier(2.8f, 12, 5.2f, 7.6f, 8.4f, 5.6f, 12, 5.6f);
            eye.AddBezier(12, 5.6f, 15.6f, 5.6f, 18.8f, 7.6f, 21.2f, 12);
            eye.AddBezier(21.2f, 12, 18.8f, 16.4f, 15.6f, 18.4f, 12, 18.4f);
            eye.AddBezier(12, 18.4f, 8.4f, 18.4f, 5.2f, 16.4f, 2.8f, 12);
            g.DrawPath(p, eye);
        }
        g.DrawEllipse(p, 9.2f, 9.2f, 5.6f, 5.6f);
        g.DrawLine(p, 4.5f, 3.8f, 19.5f, 20.2f);
    }

    /// <summary>A round brush leaving a soft trail: FacePrivacy.ai's blur brush.</summary>
    public static void BlurBrush(Graphics g, Pen p, Brush f)
    {
        Rotated(g, 45, () =>
        {
            using var handle = new GraphicsPath();
            handle.AddLine(10.4f, 1.8f, 13.6f, 1.8f);
            handle.AddLine(13.6f, 1.8f, 13.2f, 9.6f);
            handle.AddLine(13.2f, 9.6f, 10.8f, 9.6f);
            handle.CloseFigure();
            g.DrawPath(p, handle);
            using var tip = new GraphicsPath();
            tip.AddLine(9.6f, 9.6f, 14.4f, 9.6f);
            tip.AddBezier(14.4f, 9.6f, 15.6f, 13.6f, 14.2f, 16.6f, 12f, 18.8f);
            tip.AddBezier(12f, 18.8f, 9.8f, 16.6f, 8.4f, 13.6f, 9.6f, 9.6f);
            tip.CloseFigure();
            g.FillPath(f, tip);
        });
        foreach (var (x, y, r) in new[] { (4.2f, 20.4f, 1.5f), (8.2f, 21.2f, 1.1f), (2.6f, 16.6f, 1.1f) })
            g.FillEllipse(f, x - r, y - r, r * 2, r * 2);
    }

    /// <summary>arrow.uturn.backward</summary>
    public static void Undo(Graphics g, Pen p, Brush f)
    {
        g.DrawLines(p, P(8.6f, 4.6f, 4.2f, 9, 8.6f, 13.4f));
        using var path = new GraphicsPath();
        path.AddLine(4.2f, 9, 14.5f, 9);
        path.AddArc(9.25f, 9, 10.5f, 10.5f, 270, 180);
        path.AddLine(14.5f, 19.5f, 10.5f, 19.5f);
        g.DrawPath(p, path);
    }

    /// <summary>arrow.uturn.forward</summary>
    public static void Redo(Graphics g, Pen p, Brush f)
    {
        g.DrawLines(p, P(15.4f, 4.6f, 19.8f, 9, 15.4f, 13.4f));
        using var path = new GraphicsPath();
        path.AddLine(19.8f, 9, 9.5f, 9);
        path.AddArc(4.25f, 9, 10.5f, 10.5f, 270, -180);
        path.AddLine(9.5f, 19.5f, 13.5f, 19.5f);
        g.DrawPath(p, path);
    }

    // ───────────────────────────── actions ─────────────────────────────

    /// <summary>icloud.and.arrow.up</summary>
    public static void Upload(Graphics g, Pen p, Brush f)
    {
        using var cloud = new GraphicsPath();
        cloud.AddArc(2.6f, 9.6f, 8.8f, 8.8f, 90, 175);
        cloud.AddArc(6.8f, 4.6f, 11.2f, 11.2f, 190, 172);
        cloud.AddArc(13.4f, 10.2f, 8f, 8f, 285, 165);
        cloud.CloseFigure();
        g.DrawPath(p, cloud);
        g.DrawLine(p, 12.2f, 16.2f, 12.2f, 10.4f);
        g.DrawLines(p, P(9.6f, 12.8f, 12.2f, 10.2f, 14.8f, 12.8f));
    }

    /// <summary>The open box under share and save arrows.</summary>
    static void OpenBox(Graphics g, Pen p)
    {
        using var box = new GraphicsPath();
        box.AddLine(9, 9.5f, 7.5f, 9.5f);
        box.AddArc(5, 9.5f, 5, 5, 270, -90);
        box.AddArc(5, 16, 5, 5, 180, -90);
        box.AddArc(14, 16, 5, 5, 90, -90);
        box.AddArc(14, 9.5f, 5, 5, 0, -90);
        box.AddLine(16.5f, 9.5f, 15, 9.5f);
        g.DrawPath(p, box);
    }

    /// <summary>square.and.arrow.up</summary>
    public static void Share(Graphics g, Pen p, Brush f)
    {
        OpenBox(g, p);
        g.DrawLine(p, 12, 14.5f, 12, 2.8f);
        g.DrawLines(p, P(8.4f, 6.4f, 12, 2.8f, 15.6f, 6.4f));
    }

    /// <summary>square.and.arrow.down</summary>
    public static void Save(Graphics g, Pen p, Brush f)
    {
        OpenBox(g, p);
        g.DrawLine(p, 12, 2.8f, 12, 14.6f);
        g.DrawLines(p, P(8.4f, 11, 12, 14.6f, 15.6f, 11));
    }

    /// <summary>magnifyingglass</summary>
    public static void Search(Graphics g, Pen p, Brush f)
    {
        g.DrawEllipse(p, 3.5f, 3.5f, 13, 13);
        using var handle = new Pen(p.Color, p.Width * 1.25f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(handle, 15, 15, 20.2f, 20.2f);
    }

    /// <summary>printer</summary>
    public static void Print(Graphics g, Pen p, Brush f)
    {
        using (var top = new GraphicsPath())
        {
            top.AddLine(7, 8, 7, 5.5f);
            top.AddArc(7, 3.5f, 3, 3, 180, 90);
            top.AddArc(14, 3.5f, 3, 3, 270, 90);
            top.AddLine(17, 5.5f, 17, 8);
            g.DrawPath(p, top);
        }
        using (var body = new GraphicsPath())
        {
            body.AddLine(6.8f, 17, 5.5f, 17);
            body.AddArc(3, 12, 5, 5, 90, 90);
            body.AddArc(3, 8, 5, 5, 180, 90);
            body.AddArc(16, 8, 5, 5, 270, 90);
            body.AddArc(16, 12, 5, 5, 0, 90);
            body.AddLine(18.5f, 17, 17.2f, 17);
            g.DrawPath(p, body);
        }
        using (var paper = Round(7, 13.5f, 10, 7, 1.4f)) g.DrawPath(p, paper);
        g.FillEllipse(f, 16.2f, 10.2f, 1.9f, 1.9f);
    }

    /// <summary>pin, tilted like Apple's</summary>
    public static void Pin(Graphics g, Pen p, Brush f) => Rotated(g, 40, () =>
    {
        using var head = new GraphicsPath();
        head.AddLine(9.6f, 4.8f, 9.6f, 9.4f);
        head.AddLine(9.6f, 9.4f, 7, 12.2f);
        head.AddLine(7, 12.2f, 7, 14.2f);
        head.AddLine(7, 14.2f, 17, 14.2f);
        head.AddLine(17, 14.2f, 17, 12.2f);
        head.AddLine(17, 12.2f, 14.4f, 9.4f);
        head.AddLine(14.4f, 9.4f, 14.4f, 4.8f);
        g.DrawPath(p, head);
        g.DrawLine(p, 8.2f, 3.6f, 15.8f, 3.6f);
        g.DrawLine(p, 12, 14.2f, 12, 21.2f);
    });

    static void Corners(Graphics g, Pen p)
    {
        g.DrawLines(p, P(3.8f, 9, 3.8f, 6.3f));
        g.DrawArc(p, 3.8f, 3.8f, 5, 5, 180, 90);
        g.DrawLines(p, P(6.3f, 3.8f, 9, 3.8f));
        g.DrawLines(p, P(15, 3.8f, 17.7f, 3.8f));
        g.DrawArc(p, 15.2f, 3.8f, 5, 5, 270, 90);
        g.DrawLines(p, P(20.2f, 6.3f, 20.2f, 9));
        g.DrawLines(p, P(20.2f, 15, 20.2f, 17.7f));
        g.DrawArc(p, 15.2f, 15.2f, 5, 5, 0, 90);
        g.DrawLines(p, P(17.7f, 20.2f, 15, 20.2f));
        g.DrawLines(p, P(9, 20.2f, 6.3f, 20.2f));
        g.DrawArc(p, 3.8f, 15.2f, 5, 5, 90, 90);
        g.DrawLines(p, P(3.8f, 17.7f, 3.8f, 15));
    }

    /// <summary>text.viewfinder</summary>
    public static void Ocr(Graphics g, Pen p, Brush f)
    {
        Corners(g, p);
        g.DrawLine(p, 8.2f, 9, 15.8f, 9);
        g.DrawLine(p, 8.2f, 12, 15.8f, 12);
        g.DrawLine(p, 8.2f, 15, 12.6f, 15);
    }

    /// <summary>bubble.left with a sparkle: Ask AI</summary>
    public static void AskAi(Graphics g, Pen p, Brush f)
    {
        using (var bubble = new GraphicsPath())
        {
            bubble.AddArc(3, 4, 6, 6, 180, 90);
            bubble.AddArc(15, 4, 6, 6, 270, 90);
            bubble.AddArc(15, 11, 6, 6, 0, 90);
            bubble.AddLine(18, 17, 10.5f, 17);
            bubble.AddLine(10.5f, 17, 6.5f, 20.5f);
            bubble.AddLine(6.5f, 20.5f, 6.8f, 17);
            bubble.AddArc(3, 11, 6, 6, 90, 90);
            bubble.CloseFigure();
            g.DrawPath(p, bubble);
        }
        using var star = new GraphicsPath();
        star.AddPolygon(P(12, 7.2f, 12.9f, 9.8f, 15.5f, 10.7f, 12.9f, 11.6f, 12, 14.2f, 11.1f, 11.6f, 8.5f, 10.7f, 11.1f, 9.8f));
        g.FillPath(f, star);
    }

    /// <summary>text.magnifyingglass</summary>
    public static void FindText(Graphics g, Pen p, Brush f)
    {
        g.DrawLine(p, 3.5f, 5.5f, 16.5f, 5.5f);
        g.DrawLine(p, 3.5f, 10.5f, 9, 10.5f);
        g.DrawLine(p, 3.5f, 15.5f, 7.5f, 15.5f);
        g.DrawEllipse(p, 10.6f, 9.6f, 7.6f, 7.6f);
        using var handle = new Pen(p.Color, p.Width * 1.25f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(handle, 17.2f, 16.2f, 20.6f, 19.6f);
    }

    /// <summary>plus.viewfinder</summary>
    public static void Capture(Graphics g, Pen p, Brush f)
    {
        Corners(g, p);
        g.DrawLine(p, 12, 8.5f, 12, 15.5f);
        g.DrawLine(p, 8.5f, 12, 15.5f, 12);
    }

    /// <summary>doc.on.doc</summary>
    public static void Copy(Graphics g, Pen p, Brush f)
    {
        using var front = Round(8.5f, 8.5f, 12, 12, 2.8f);
        g.DrawPath(p, front);
        using var back = new GraphicsPath();
        back.AddLine(15.5f, 8.4f, 15.5f, 6.3f);
        back.AddArc(9.9f, 3.5f, 5.6f, 5.6f, 0, -90);
        back.AddArc(3.5f, 3.5f, 5.6f, 5.6f, 270, -90);
        back.AddArc(3.5f, 9.9f, 5.6f, 5.6f, 180, -90);
        back.AddLine(6.3f, 15.5f, 8.4f, 15.5f);
        g.DrawPath(p, back);
    }

    /// <summary>checkmark</summary>
    public static void Check(Graphics g, Pen p, Brush f) => g.DrawLines(p, P(5.5f, 12.5f, 10, 17, 18.5f, 7.5f));

    /// <summary>xmark</summary>
    public static void Close(Graphics g, Pen p, Brush f)
    {
        g.DrawLine(p, 6.8f, 6.8f, 17.2f, 17.2f);
        g.DrawLine(p, 17.2f, 6.8f, 6.8f, 17.2f);
    }

    // ───────────────────────────── menus and settings ─────────────────────────────

    /// <summary>display</summary>
    public static void Monitor(Graphics g, Pen p, Brush f)
    {
        using var path = Round(2.5f, 3.5f, 19, 13.5f, 2.8f);
        g.DrawPath(p, path);
        g.DrawLine(p, 12, 17, 12, 20.5f);
        g.DrawLine(p, 8, 20.5f, 16, 20.5f);
    }

    /// <summary>A page running on below the screen, and the way it scrolls.</summary>
    public static void ScrollCapture(Graphics g, Pen p, Brush f)
    {
        using var path = new GraphicsPath();
        path.AddLine(4.5f, 14.5f, 4.5f, 6.5f);
        path.AddArc(4.5f, 3, 6, 6, 180, 90);
        path.AddArc(13.5f, 3, 6, 6, 270, 90);
        path.AddLine(19.5f, 6.5f, 19.5f, 14.5f);
        g.DrawPath(p, path);
        g.DrawLine(p, 12, 8, 12, 20.5f);
        g.DrawLines(p, P(8.5f, 17, 12, 20.5f, 15.5f, 17));
    }

    /// <summary>arrow.clockwise</summary>
    public static void Repeat(Graphics g, Pen p, Brush f)
    {
        // Open at the top right; the head sits at the top, pointing into the gap.
        g.DrawArc(p, 4.5f, 5.2f, 15, 15, -30, 300);
        var tip = new PointF(13.2f, 5.2f);
        g.DrawLines(p, new[] { new PointF(tip.X - 3.4f, tip.Y - 3.4f), tip, new PointF(tip.X - 3.4f, tip.Y + 3.4f) });
    }

    /// <summary>folder</summary>
    public static void Folder(Graphics g, Pen p, Brush f)
    {
        using var path = new GraphicsPath();
        path.AddArc(2.8f, 4.5f, 4, 4, 180, 90);
        path.AddLine(4.8f, 4.5f, 8.8f, 4.5f);
        path.AddLine(8.8f, 4.5f, 11, 7);
        path.AddArc(17.2f, 7, 4, 4, 270, 90);
        path.AddArc(17.2f, 16, 4, 4, 0, 90);
        path.AddArc(2.8f, 16, 4, 4, 90, 90);
        path.CloseFigure();
        g.DrawPath(p, path);
        g.DrawLine(p, 2.8f, 10, 21.2f, 10);
    }

    /// <summary>gearshape</summary>
    public static void Gear(Graphics g, Pen p, Brush f)
    {
        var pts = new List<PointF>();
        for (int k = 0; k < 8; k++)
        {
            double a = k * 45;
            foreach (var (da, rad) in new[] { (-17.0, 7.1f), (-9.5, 9.4f), (9.5, 9.4f), (17.0, 7.1f) })
            {
                double t = (a + da) * Math.PI / 180;
                pts.Add(new PointF(12 + (float)Math.Cos(t) * rad, 12 + (float)Math.Sin(t) * rad));
            }
        }
        g.DrawPolygon(p, pts.ToArray());
        g.DrawEllipse(p, 9, 9, 6, 6);
    }

    /// <summary>keyboard</summary>
    public static void Keyboard(Graphics g, Pen p, Brush f)
    {
        using var path = Round(2.5f, 5.5f, 19, 13, 2.8f);
        g.DrawPath(p, path);
        foreach (float x in new[] { 6.3f, 9.4f, 12.5f, 15.6f })
            g.FillEllipse(f, x - 0.1f, 8.6f, 2, 2);
        foreach (float x in new[] { 7.8f, 10.9f, 14f })
            g.FillEllipse(f, x - 0.1f, 11.6f, 2, 2);
        g.FillEllipse(f, 17.1f, 11.6f, 2, 2);
        g.DrawLine(p, 8.5f, 15.4f, 15.5f, 15.4f);
    }

    /// <summary>power</summary>
    public static void Power(Graphics g, Pen p, Brush f)
    {
        g.DrawArc(p, 4.5f, 5, 15, 15, -50, 280);
        g.DrawLine(p, 12, 3, 12, 11);
    }

    static void Sparkle(Graphics g, Pen p, Brush f, float cx, float cy, float r, bool filled)
    {
        // Four points joined by inward curves, like Apple's sparkles.
        var tips = new[] { new PointF(cx, cy - r), new PointF(cx + r, cy), new PointF(cx, cy + r), new PointF(cx - r, cy) };
        using var path = new GraphicsPath();
        for (int i = 0; i < 4; i++)
        {
            var a = tips[i];
            var b = tips[(i + 1) % 4];
            var c = new PointF(cx + (a.X - cx + b.X - cx) * 0.12f, cy + (a.Y - cy + b.Y - cy) * 0.12f);
            path.AddBezier(a, c, c, b);
        }
        path.CloseFigure();
        if (filled) g.FillPath(f, path);
        else g.DrawPath(p, path);
    }

    /// <summary>sparkles</summary>
    public static void Sparkles(Graphics g, Pen p, Brush f)
    {
        Sparkle(g, p, f, 10, 13.5f, 8, filled: false);
        Sparkle(g, p, f, 18.6f, 5.4f, 3.6f, filled: true);
        Sparkle(g, p, f, 19, 18.4f, 2.6f, filled: true);
    }

    /// <summary>video</summary>
    public static void Video(Graphics g, Pen p, Brush f)
    {
        using var body = Round(2.5f, 6, 13.5f, 12, 3);
        g.DrawPath(p, body);
        g.DrawPolygon(p, P(16, 10.6f, 20.4f, 7.9f, 21.5f, 8.6f, 21.5f, 15.4f, 20.4f, 16.1f, 16, 13.4f));
    }

    /// <summary>clock</summary>
    public static void Clock(Graphics g, Pen p, Brush f)
    {
        g.DrawEllipse(p, 3.5f, 3.5f, 17, 17);
        g.DrawLines(p, P(12, 7.5f, 12, 12, 15.2f, 14));
    }

    /// <summary>arrow.down.circle: updates</summary>
    public static void Update(Graphics g, Pen p, Brush f)
    {
        g.DrawEllipse(p, 3, 3, 18, 18);
        g.DrawLine(p, 12, 7.5f, 12, 16);
        g.DrawLines(p, P(8.4f, 12.6f, 12, 16.2f, 15.6f, 12.6f));
    }
}
