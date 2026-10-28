using System.Drawing.Drawing2D;

namespace LiteShot.Overlay;

internal sealed class ToolButton
{
    public required string Tip { get; init; }
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
    public const float ButtonSize = 34, Padding = 4, Spacing = 2, GroupGap = 10, IconSize = 19;

    public readonly List<ToolGroup> Groups = new();
    public readonly bool Vertical;
    public RectangleF Bounds { get; private set; }
    /// <summary>What the bar belongs to; tooltips open on the side away from it.</summary>
    public RectangleF Anchor;

    public Toolbar(bool vertical) => Vertical = vertical;

    public IEnumerable<ToolButton> Buttons => Groups.SelectMany(g => g.Buttons);

    public Toolbar Group(params ToolButton[] buttons)
    {
        var group = new ToolGroup();
        group.Buttons.AddRange(buttons);
        Groups.Add(group);
        return this;
    }

    float Length(ToolButton b) => Vertical ? ButtonSize : b.Width;

    float GroupLength(ToolGroup g) => Padding * 2 + g.Buttons.Sum(Length) + (g.Buttons.Count - 1) * Spacing;

    public SizeF Measure(float s)
    {
        float length = Groups.Sum(GroupLength) + (Groups.Count - 1) * GroupGap;
        float thickness = ButtonSize + Padding * 2;
        return Vertical ? new SizeF(thickness * s, length * s) : new SizeF(length * s, thickness * s);
    }

    public void Layout(PointF origin, float s)
    {
        Bounds = new RectangleF(origin, Measure(s));
        float pos = 0, thickness = (ButtonSize + Padding * 2) * s;
        foreach (var g in Groups)
        {
            float length = GroupLength(g) * s;
            g.Bounds = Vertical
                ? new RectangleF(origin.X, origin.Y + pos, thickness, length)
                : new RectangleF(origin.X + pos, origin.Y, length, thickness);
            float bp = Padding * s;
            foreach (var b in g.Buttons)
            {
                b.Bounds = Vertical
                    ? new RectangleF(g.Bounds.X + Padding * s, g.Bounds.Y + bp, ButtonSize * s, ButtonSize * s)
                    : new RectangleF(g.Bounds.X + bp, g.Bounds.Y + Padding * s, Length(b) * s, ButtonSize * s);
                bp += (Length(b) + Spacing) * s;
            }
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
            float radius = Math.Min(group.Bounds.Width, group.Bounds.Height) / 2f;
            glass.Draw(g, group.Bounds, radius, lit, group.Tone, s);
            foreach (var b in group.Buttons) DrawButton(g, b, group.Tone, s, hover, hoverAnim, pressed);
        }
    }

    static void DrawButton(Graphics g, ToolButton b, GlassTone tone, float s, ToolButton? hover, float hoverAnim, ToolButton? pressed)
    {
        var r = b.Bounds;
        bool enabled = b.Enabled, active = b.Active;
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;

        if (active)
        {
            // Concentric with the capsule: the button circle is the capsule radius minus its padding.
            float d = r.Height * (b == pressed ? 0.92f : 1f);
            var circle = new RectangleF(cx - (r.Width - r.Height + d) / 2, cy - d / 2, r.Width - r.Height + d, d);
            var accent = SystemTheme.HighContrast ? SystemColors.Highlight : SystemTheme.Accent;
            using var pill = Ui.RoundRect(circle, d / 2);
            using (var fill = new SolidBrush(accent)) g.FillPath(fill, pill);
            if (!SystemTheme.HighContrast)
            {
                using var sheen = new LinearGradientBrush(RectangleF.Inflate(circle, 0, 1), Color.FromArgb(80, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical);
                using var top = Ui.RoundRect(new RectangleF(circle.X, circle.Y, circle.Width, circle.Height * 0.6f), d * 0.3f);
                g.FillPath(sheen, top);
                GlassMaterial.DrawRim(g, circle, d / 2, s, 0.7f);
            }
        }
        else if (enabled && (b == hover || b == pressed))
        {
            float k = b == pressed ? 0.88f : 0.7f + 0.3f * hoverAnim;
            float d = r.Height * k, w = r.Width - r.Height + d;
            using var fill = new SolidBrush(Ui.WithAlpha(tone.Hover, (int)(tone.Hover.A * (b == pressed ? 1.6f : Math.Max(0.35f, hoverAnim)))));
            using var pill = Ui.RoundRect(new RectangleF(cx - w / 2, cy - d / 2, w, d), d / 2);
            g.FillPath(fill, pill);
        }

        var color = !enabled ? tone.Disabled
            : active ? (SystemTheme.HighContrast ? SystemColors.HighlightText : Color.White)
            : tone.Glyph;
        float icon = IconSize * s;
        var ir = new RectangleF(cx - icon / 2, cy - icon / 2, icon, icon);
        if (b.CustomPaint != null) b.CustomPaint(g, b.PaintsFullButton ? r : ir, tone);
        else if (b.Glyph != null) Icons.Draw(g, ir, color, b.Glyph);
    }
}
