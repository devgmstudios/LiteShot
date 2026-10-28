using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace LiteShot.Overlay;

internal enum TextAlign { Left, Center, Right }

/// <summary>Plain text, text with a contrasting outline, text on a filled label, or text with a drop shadow.</summary>
internal enum TextLook { Plain, Outline, Label, Shadow }

/// <summary>
/// Multi-line text drawn as vector outlines, so outline, label and shadow looks are exact, and bold or
/// italic can be simulated for fonts that don't include those styles.
/// </summary>
internal sealed class TextShape : Annotation
{
    static readonly Bitmap measureBitmap = new(1, 1);
    static readonly Graphics measure = CreateMeasure();

    public PointF Pos;
    public string Text = "";
    public string FamilyName = FontRegistry.DefaultFamily;
    public bool Bold, Italic;
    public TextAlign Align;
    public TextLook Look;

    FontFamily? family;
    string familyKey = "";
    Font? font;
    string fontKey = "";

    static Graphics CreateMeasure()
    {
        var g = Graphics.FromImage(measureBitmap);
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        return g;
    }

    FontFamily Family
    {
        get
        {
            if (family == null || familyKey != FamilyName)
            {
                family = FontRegistry.Resolve(FamilyName);
                familyKey = FamilyName;
            }
            return family;
        }
    }

    /// <summary>The styles the font really has; whatever is missing is drawn synthetically.</summary>
    FontStyle RealStyle
    {
        get
        {
            var f = Family;
            var want = (Bold ? FontStyle.Bold : FontStyle.Regular) | (Italic ? FontStyle.Italic : FontStyle.Regular);
            foreach (var s in new[] { want, want & FontStyle.Bold, want & FontStyle.Italic, FontStyle.Regular, FontStyle.Bold, FontStyle.Italic, FontStyle.Bold | FontStyle.Italic })
                if (f.IsStyleAvailable(s)) return s;
            return FontStyle.Regular;
        }
    }

    bool FauxBold => Bold && (RealStyle & FontStyle.Bold) == 0;
    bool FauxItalic => Italic && (RealStyle & FontStyle.Italic) == 0;

    public Font Font
    {
        get
        {
            var style = RealStyle;
            string key = $"{FamilyName}|{Size}|{(int)style}";
            if (font == null || key != fontKey)
            {
                font = new Font(Family, Math.Max(4f, Size), style, GraphicsUnit.Pixel);
                fontKey = key;
            }
            return font;
        }
    }

    public string[] Lines => Text.Split('\n');
    public float LineHeight => Font.GetHeight(measure);

    float Width(string s)
    {
        if (s.Length == 0) return 0;
        lock (measure) return measure.MeasureString(s, Font, PointF.Empty, Ui.Typographic).Width;
    }

    float BoxWidth(string[] lines) => Math.Max(Size * 0.5f, lines.Max(Width));

    float LineX(string[] lines, int i, float box) => Pos.X + (box - Width(lines[i])) * Align switch
    {
        TextAlign.Center => 0.5f,
        TextAlign.Right => 1f,
        _ => 0f,
    };

    /// <summary>The text block itself, without the padding a label or outline adds.</summary>
    public RectangleF TextRect
    {
        get
        {
            var lines = Lines;
            return new RectangleF(Pos.X, Pos.Y, BoxWidth(lines), lines.Length * LineHeight);
        }
    }

    RectangleF LabelRect => RectangleF.Inflate(TextRect, Size * 0.32f, Size * 0.12f);

    public override RectangleF Bounds => Look switch
    {
        TextLook.Label => LabelRect,
        TextLook.Outline => RectangleF.Inflate(TextRect, Size * 0.12f, Size * 0.12f),
        TextLook.Shadow => RectangleF.Inflate(TextRect, Size * 0.1f, Size * 0.1f),
        _ => TextRect,
    };

    static Color Contrast(Color c) => Ui.Luminance(c) > 0.6 ? Color.FromArgb(28, 28, 30) : Color.White;

    GraphicsPath BuildPath()
    {
        var path = new GraphicsPath();
        var lines = Lines;
        float lh = LineHeight, box = BoxWidth(lines);
        var f = Family;
        int style = (int)RealStyle;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            var origin = new PointF(LineX(lines, i, box), Pos.Y + i * lh);
            if (!FauxItalic)
            {
                path.AddString(lines[i], f, style, Size, origin, Ui.Typographic);
                continue;
            }
            using var line = new GraphicsPath();
            line.AddString(lines[i], f, style, Size, origin, Ui.Typographic);
            float baseline = origin.Y + lh * 0.78f;
            using var slant = new Matrix(1, 0, -0.2f, 1, 0.2f * baseline, 0);
            line.Transform(slant);
            path.AddPath(line, false);
        }
        return path;
    }

    public override void Draw(Graphics g, Bitmap source)
    {
        if (Text.Length == 0) return;
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = BuildPath();
        var ink = Color;
        if (Look == TextLook.Label)
        {
            using var label = Ui.RoundRect(LabelRect, Size * 0.3f);
            using var fill = new SolidBrush(Color);
            g.FillPath(fill, label);
            ink = Contrast(Color);
        }
        else if (Look == TextLook.Outline)
        {
            using var halo = new Pen(Contrast(Color), Math.Max(2f, Size * 0.16f)) { LineJoin = LineJoin.Round };
            g.DrawPath(halo, path);
        }
        else if (Look == TextLook.Shadow)
        {
            using var shadow = (GraphicsPath)path.Clone();
            using var offset = new Matrix();
            offset.Translate(Size * 0.05f, Size * 0.07f);
            shadow.Transform(offset);
            using var shade = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
            g.FillPath(shade, shadow);
        }
        using (var brush = new SolidBrush(ink)) g.FillPath(brush, path);
        if (FauxBold)
        {
            using var thicken = new Pen(ink, Math.Max(0.8f, Size * 0.045f)) { LineJoin = LineJoin.Round };
            g.DrawPath(thicken, path);
        }
        g.Restore(state);
    }

    public override bool Hit(PointF p, float tol) => RectangleF.Inflate(Bounds, tol, tol).Contains(p);

    public override void Move(float dx, float dy) => Pos = new PointF(Pos.X + dx, Pos.Y + dy);

    public PointF CaretPoint(int index)
    {
        var lines = Lines;
        int line = 0, start = 0;
        for (int i = 0; i < index && i < Text.Length; i++)
            if (Text[i] == '\n') { line++; start = i + 1; }
        float box = BoxWidth(lines);
        return new PointF(LineX(lines, line, box) + Width(Text[start..Math.Min(index, Text.Length)]), Pos.Y + line * LineHeight);
    }

    public int IndexAt(PointF p)
    {
        var lines = Lines;
        int line = Math.Clamp((int)MathF.Floor((p.Y - Pos.Y) / LineHeight), 0, lines.Length - 1);
        int start = 0;
        for (int i = 0; i < line; i++) start += lines[i].Length + 1;
        string text = lines[line];
        float x0 = LineX(lines, line, BoxWidth(lines));
        int best = 0;
        float bestDist = float.MaxValue;
        for (int c = 0; c <= text.Length; c++)
        {
            float d = Math.Abs(x0 + Width(text[..c]) - p.X);
            if (d < bestDist) { bestDist = d; best = c; }
        }
        return start + best;
    }
}
