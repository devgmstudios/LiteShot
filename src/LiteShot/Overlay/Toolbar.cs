using System.Drawing.Drawing2D;

namespace LiteShot.Overlay;

internal sealed class ToolButton
{
    public required string Tip { get; init; }
    /// <summary>The name shown with the icon when labels are on; the tip up to its first " · " when not given.</summary>
    public string? Label { get; init; }
    public string? Shortcut { get; init; }
    public Icons.Glyph? Glyph { get; init; }
    public required Action OnClick { get; init; }
    public Func<bool>? IsActive { get; init; }
    public Func<bool>? IsEnabled { get; init; }
    public Action<Graphics, RectangleF, GlassTone>? CustomPaint { get; init; }
    /// <summary>Logical width; wider than <see cref="Toolbar.ButtonSize"/> makes a capsule button (horizontal bars only).</summary>
    public float Width { get; init; } = Toolbar.ButtonSize;
    /// <summary>CustomPaint receives the whole button instead of the icon square.</summary>
    public bool PaintsFullButton { get; init; }
    public RectangleF Bounds;

    public bool Enabled => IsEnabled?.Invoke() ?? true;
    public bool Active => IsActive?.Invoke() ?? false;
    public string Name => Label ?? Tip.Split(" · ")[0];
}

/// <summary>Buttons that share one piece of glass. Groups are separated by a fixed gap, never by lines.</summary>
internal sealed class ToolGroup
{
    public readonly List<ToolButton> Buttons = new();
    public RectangleF Bounds;
    public GlassTone Tone;
}

/// <summary>A floating glass toolbar: a row or column of capsule-shaped groups.</summary>
internal sealed class Toolbar
{
    public const float ButtonSize = 34, Padding = 4, Spacing = 2, GroupGap = 10, IconSize = 19, BrightIconSize = 22;

    public readonly List<ToolGroup> Groups = new();
    public readonly bool Vertical;
    public RectangleF Bounds { get; private set; }
    /// <summary>What the bar belongs to; tooltips open on the side away from it.</summary>
    public RectangleF Anchor;
    /// <summary>When set, every button shows its name: beside the icon in a column, under it in a row.</summary>
    public Font? LabelFont;

    Font? cellsFont;
    float cellsScale;
    SizeF[] cells = Array.Empty<SizeF>();

    public Toolbar(bool vertical) => Vertical = vertical;

    public IEnumerable<ToolButton> Buttons => Groups.SelectMany(g => g.Buttons);

    public Toolbar Group(params ToolButton[] buttons)
    {
        var group = new ToolGroup();
        group.Buttons.AddRange(buttons);
        Groups.Add(group);
        cells = Array.Empty<SizeF>();
        return this;
    }

    /// <summary>Each button's logical size, in the order of <see cref="Buttons"/>. Labels make a column wider and a row taller.</summary>
    SizeF[] Cells(float s)
    {
        if (cells.Length > 0 && cellsFont == LabelFont && cellsScale == s) return cells;
        var all = Buttons.ToList();
        if (LabelFont == null)
            cells = all.Select(b => new SizeF(Vertical ? ButtonSize : b.Width, ButtonSize)).ToArray();
        else if (Vertical)
        {
            float widest = all.Max(b => Ui.Measure(b.Name, LabelFont).Width) / s;
            cells = all.Select(_ => new SizeF(MathF.Ceiling(ButtonSize + widest + 12), ButtonSize)).ToArray();
        }
        else
        {
            float tall = MathF.Ceiling(ButtonSize + LabelFont.GetHeight() / s + 2);
            cells = all.Select(b => new SizeF(MathF.Ceiling(MathF.Max(b.Width, Ui.Measure(b.Name, LabelFont).Width / s + 20)), tall)).ToArray();
        }
        cellsFont = LabelFont;
        cellsScale = s;
        return cells;
    }

    public SizeF Measure(float s)
    {
        var c = Cells(s);
        float length = (Groups.Count - 1) * GroupGap, thickness = 0;
        int i = 0;
        foreach (var g in Groups)
        {
            length += Padding * 2 + (g.Buttons.Count - 1) * Spacing;
            foreach (var _ in g.Buttons)
            {
                length += Vertical ? c[i].Height : c[i].Width;
                thickness = Math.Max(thickness, Vertical ? c[i].Width : c[i].Height);
                i++;
            }
        }
        thickness += Padding * 2;
        return Vertical ? new SizeF(thickness * s, length * s) : new SizeF(length * s, thickness * s);
    }

    public void Layout(PointF origin, float s)
    {
        var c = Cells(s);
        Bounds = new RectangleF(origin, Measure(s));
        float pos = 0, thickness = Vertical ? Bounds.Width : Bounds.Height;
        int i = 0;
        foreach (var g in Groups)
        {
            float bp = Padding * s;
            foreach (var b in g.Buttons)
            {
                float w = c[i].Width * s, h = c[i].Height * s;
                b.Bounds = Vertical
                    ? new RectangleF(origin.X + Padding * s, origin.Y + pos + bp, w, h)
                    : new RectangleF(origin.X + pos + bp, origin.Y + Padding * s, w, h);
                bp += (Vertical ? h : w) + Spacing * s;
                i++;
            }
            float length = bp - Spacing * s + Padding * s;
            g.Bounds = Vertical
                ? new RectangleF(origin.X, origin.Y + pos, thickness, length)
                : new RectangleF(origin.X + pos, origin.Y, length, thickness);
            pos += length + GroupGap * s;
        }
    }

    public ToolButton? HitTest(Point p)
    {
        if (!Bounds.Contains(p)) return null;
        foreach (var g in Groups)
            if (g.Bounds.Contains(p))
                foreach (var b in g.Buttons)
                    if (RectangleF.Inflate(b.Bounds, 1, 1).Contains(p)) return b;
        return null;
    }

    public ToolGroup? GroupAt(Point p) => Groups.FirstOrDefault(g => g.Bounds.Contains(p));

    public ToolGroup? GroupOf(ToolButton button) => Groups.FirstOrDefault(g => g.Buttons.Contains(button));

    public void Draw(Graphics g, GlassMaterial glass, Rectangle lit, float s, ToolButton? hover, float hoverAnim, ToolButton? pressed)
    {
        foreach (var group in Groups)
        {
            // Concentric with the button pills; a labelled column is a rounded rectangle, not a long capsule.
            float radius = Math.Min(Math.Min(group.Bounds.Width, group.Bounds.Height) / 2f, (ButtonSize / 2 + Padding) * s);
            glass.Draw(g, group.Bounds, radius, lit, group.Tone, s);
            foreach (var b in group.Buttons) DrawButton(g, b, group.Tone, s, hover, hoverAnim, pressed);
        }
    }

    void DrawButton(Graphics g, ToolButton b, GlassTone tone, float s, ToolButton? hover, float hoverAnim, ToolButton? pressed)
    {
        var r = b.Bounds;
        bool enabled = b.Enabled, active = b.Active;

        if (active)
        {
            // Concentric with the capsule: the button circle is the capsule radius minus its padding.
            var circle = Highlight(r, b == pressed ? 0.92f : 1f);
            float d = Math.Min(circle.Width, circle.Height);
            using var pill = Ui.RoundRect(circle, Corner(circle, s));
            using (var fill = new SolidBrush(tone.On)) g.FillPath(fill, pill);
            if (!SystemTheme.HighContrast && !tone.Bright)
            {
                using var sheen = new LinearGradientBrush(RectangleF.Inflate(circle, 0, 1), Color.FromArgb(80, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical);
                using var top = Ui.RoundRect(new RectangleF(circle.X, circle.Y, circle.Width, circle.Height * 0.6f), d * 0.3f);
                g.FillPath(sheen, top);
                GlassMaterial.DrawRim(g, circle, d / 2, s, 0.7f);
            }
        }
        else if (enabled && (b == hover || b == pressed))
        {
            var area = Highlight(r, b == pressed ? 0.88f : 0.7f + 0.3f * hoverAnim);
            using var fill = new SolidBrush(Ui.WithAlpha(tone.Hover, (int)(tone.Hover.A * (b == pressed ? 1.6f : Math.Max(0.35f, hoverAnim)))));
            using var pill = Ui.RoundRect(area, Corner(area, s));
            g.FillPath(fill, pill);
        }

        var color = !enabled ? tone.Disabled : active ? tone.OnGlyph : tone.Glyph;
        float button = ButtonSize * s;
        // With labels the icon keeps its usual square: at the left of a column's row, at the top of a row's button.
        var square = LabelFont == null ? r
            : Vertical ? new RectangleF(r.X, r.Y, button, r.Height)
            : new RectangleF(r.X, r.Y + 1 * s, r.Width, button);
        float icon = (tone.Bright ? BrightIconSize : IconSize) * s;
        var ir = new RectangleF(square.X + square.Width / 2 - icon / 2, square.Y + square.Height / 2 - icon / 2, icon, icon);
        if (b.CustomPaint != null) b.CustomPaint(g, b.PaintsFullButton ? square : ir, tone);
        else if (b.Glyph != null) Icons.Draw(g, ir, color, b.Glyph, tone.Bright ? 2.3f : 1.8f);

        if (LabelFont == null) return;
        var size = Ui.Measure(b.Name, LabelFont);
        var at = Vertical
            ? new PointF(r.X + button, r.Y + (r.Height - size.Height) / 2)
            : new PointF(r.X + (r.Width - size.Width) / 2, r.Bottom - size.Height - 3 * s);
        Ui.DrawText(g, b.Name, LabelFont, color, at);
    }

    static float Corner(RectangleF r, float s) => Math.Min(Math.Min(r.Width, r.Height), ButtonSize * s) / 2;

    /// <summary>The pill behind a hovered or turned-on button, scaled by <paramref name="k"/> about its centre.</summary>
    RectangleF Highlight(RectangleF r, float k)
    {
        if (LabelFont != null)
        {
            float shrink = Math.Min(r.Width, r.Height) * (1 - k) / 2;
            return RectangleF.Inflate(r, -shrink, -shrink);
        }
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        float d = r.Height * k, w = r.Width - r.Height + d;
        return new RectangleF(cx - w / 2, cy - d / 2, w, d);
    }
}
