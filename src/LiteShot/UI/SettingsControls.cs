using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace LiteShot;

/// <summary>Windows 11 Settings palette, following the system light/dark and high-contrast modes.</summary>
internal sealed class SettingsTheme
{
    public readonly bool Dark = SystemTheme.Dark;
    public readonly bool HighContrast = SystemTheme.HighContrast;

    Color Pick(Color light, Color dark, Color contrast) => HighContrast ? contrast : Dark ? dark : light;

    public Color Window => Pick(Color.FromArgb(243, 243, 243), Color.FromArgb(32, 32, 32), SystemColors.Window);
    public Color Card => Pick(Color.FromArgb(251, 251, 251), Color.FromArgb(43, 43, 43), SystemColors.Window);
    public Color CardBorder => Pick(Color.FromArgb(229, 229, 229), Color.FromArgb(27, 27, 27), SystemColors.WindowText);
    public Color Divider => Pick(Color.FromArgb(234, 234, 234), Color.FromArgb(30, 30, 30), SystemColors.WindowText);
    public Color Text => Pick(Color.FromArgb(26, 26, 26), Color.White, SystemColors.WindowText);
    public Color SubText => Pick(Color.FromArgb(96, 96, 96), Color.FromArgb(200, 200, 200), SystemColors.GrayText);
    public Color Field => Pick(Color.White, Color.FromArgb(52, 52, 52), SystemColors.Window);
    public Color FieldBorder => Pick(Color.FromArgb(212, 212, 212), Color.FromArgb(72, 72, 72), SystemColors.WindowText);
    public Color Accent => HighContrast ? SystemColors.Highlight : SystemTheme.Accent;
    public Color OnAccent => HighContrast ? SystemColors.HighlightText : Color.White;
    public Color Hover => Pick(Color.FromArgb(12, 0, 0, 0), Color.FromArgb(16, 255, 255, 255), SystemColors.Highlight);
    public Color Selected => Pick(Color.FromArgb(20, 0, 0, 0), Color.FromArgb(26, 255, 255, 255), SystemColors.Highlight);
    public Color Keycap => Pick(Color.FromArgb(238, 238, 238), Color.FromArgb(62, 62, 62), SystemColors.Control);
}

/// <summary>Base for owner-drawn controls: double buffered, DPI-aware through <see cref="DpiScale"/>.</summary>
internal abstract class ThemedControl : Control
{
    protected readonly SettingsTheme Theme;
    protected bool Hovered, Pressed;

    protected ThemedControl(SettingsTheme theme)
    {
        Theme = theme;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = theme.Card;
        ForeColor = theme.Text;
    }

    protected float DpiScale => DeviceDpi / 96f;

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Hovered = false; Pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { Pressed = true; Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); Pressed = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected Graphics Prepare(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        return g;
    }

    protected void DrawFocus(Graphics g, RectangleF r, float radius)
    {
        if (!Focused || !ShowFocusCues) return;
        using var pen = new Pen(Theme.Text, Math.Max(1.5f, 2f * DpiScale));
        using var path = Ui.RoundRect(RectangleF.Inflate(r, -1, -1), radius);
        g.DrawPath(pen, path);
    }
}

/// <summary>Toggle switch. The knob grows on hover and stretches while pressed.</summary>
internal sealed class ToggleSwitch : ThemedControl
{
    bool on;
    public event EventHandler? CheckedChanged;

    public ToggleSwitch(SettingsTheme theme) : base(theme)
    {
        SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
        Size = new Size(44, 22);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
    }

    public bool Checked
    {
        get => on;
        set
        {
            if (on == value) return;
            on = value;
            AccessibleDescription = on ? "On" : "Off";
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnClick(EventArgs e) { base.OnClick(e); Focus(); Checked = !Checked; }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Prepare(e);
        float s = DpiScale, h = Height - 3 * s;
        var track = new RectangleF(1.5f * s, (Height - h) / 2, Width - 3 * s, h);
        using var path = Ui.RoundRect(track, h / 2);
        if (on)
        {
            using var fill = new SolidBrush(Theme.Accent);
            g.FillPath(fill, path);
        }
        else
        {
            using var pen = new Pen(Theme.SubText, Math.Max(1f, 1.2f * s));
            if (Hovered) { using var hover = new SolidBrush(Theme.Hover); g.FillPath(hover, path); }
            g.DrawPath(pen, path);
        }
        float pad = h * 0.2f, d = h - pad * 2 + (Hovered ? h * 0.08f : 0) - (on ? 0 : h * 0.06f);
        float stretch = Pressed ? h * 0.3f : 0;
        float cx = on ? track.Right - pad - d / 2 : track.Left + pad + d / 2;
        var knob = new RectangleF(cx - d / 2 - (on ? stretch : 0), track.Y + (h - d) / 2, d + stretch, d);
        using var knobPath = Ui.RoundRect(knob, d / 2);
        using var knobFill = new SolidBrush(on ? Theme.OnAccent : Theme.SubText);
        g.FillPath(knobFill, knobPath);
        DrawFocus(g, new RectangleF(0, 0, Width, Height), Height / 2f);
    }
}

/// <summary>Capsule button: <c>Primary</c> is filled with the accent color, otherwise a bordered secondary button.</summary>
internal sealed class PillButton : ThemedControl
{
    readonly bool primary;

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    public PillButton(SettingsTheme theme, string text, int width, bool primary = false) : base(theme)
    {
        this.primary = primary;
        SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
        Text = text;
        Size = new Size(width, 32);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = text;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Prepare(e);
        float s = DpiScale;
        var r = new RectangleF(0.5f * s, 0.5f * s, Width - 1.5f * s, Height - 1.5f * s);
        using var path = Ui.RoundRect(r, r.Height / 2);
        Color fill = primary && Enabled ? Theme.Accent : Theme.Field;
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        if (Enabled && (Hovered || Pressed))
        {
            using var overlay = new SolidBrush(Pressed ? Color.FromArgb(30, 0, 0, 0) : primary ? Color.FromArgb(28, 255, 255, 255) : Theme.Hover);
            g.FillPath(overlay, path);
        }
        if (!primary || !Enabled)
        {
            using var border = new Pen(Theme.FieldBorder, 1f);
            g.DrawPath(border, path);
        }
        else if (!Theme.HighContrast)
        {
            Overlay.GlassMaterial.DrawRim(g, r, r.Height / 2, s, 0.45f);
        }
        TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), !Enabled ? Theme.SubText : primary ? Theme.OnAccent : Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        DrawFocus(g, new RectangleF(0, 0, Width, Height), Height / 2f);
    }
}

/// <summary>Segmented control with a raised accent pill on the chosen option.</summary>
internal sealed class Segmented : ThemedControl
{
    readonly string[] options;
    int selected, hover = -1;
    public event EventHandler? SelectedChanged;

    public Segmented(SettingsTheme theme, int segmentWidth, params string[] options) : base(theme)
    {
        this.options = options;
        SetStyle(ControlStyles.Selectable, true);
        Size = new Size(segmentWidth * options.Length + 8, 34);
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PageTabList;
    }

    public int SelectedIndex
    {
        get => selected;
        set
        {
            value = Math.Clamp(value, 0, options.Length - 1);
            if (value == selected) return;
            selected = value;
            AccessibleDescription = options[value];
            Invalidate();
            SelectedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    RectangleF Segment(int i)
    {
        float pad = 3 * DpiScale, w = (Width - pad * 2) / options.Length;
        return new RectangleF(pad + i * w, pad, w, Height - pad * 2);
    }

    int IndexAt(Point p)
    {
        for (int i = 0; i < options.Length; i++) if (Segment(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = IndexAt(e.Location);
        if (h != hover) { hover = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { hover = -1; base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        Focus();
        int i = IndexAt(e.Location);
        if (i >= 0) SelectedIndex = i;
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Left) SelectedIndex--;
        else if (e.KeyCode == Keys.Right) SelectedIndex++;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Prepare(e);
        float s = DpiScale;
        var outer = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Ui.RoundRect(outer, outer.Height / 2))
        {
            using var fill = new SolidBrush(Theme.Field);
            using var border = new Pen(Theme.FieldBorder, 1f);
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }
        for (int i = 0; i < options.Length; i++)
        {
            var r = Segment(i);
            using var path = Ui.RoundRect(r, r.Height / 2);
            if (i == selected)
            {
                using var fill = new SolidBrush(Enabled ? Theme.Accent : Theme.FieldBorder);
                g.FillPath(fill, path);
                if (!Theme.HighContrast && Enabled) Overlay.GlassMaterial.DrawRim(g, r, r.Height / 2, s, 0.5f);
            }
            else if (i == hover && Enabled)
            {
                using var fill = new SolidBrush(Theme.Hover);
                g.FillPath(fill, path);
            }
            var color = !Enabled ? Theme.SubText : i == selected ? Theme.OnAccent : Theme.Text;
            TextRenderer.DrawText(g, options[i], Font, Rectangle.Round(r), color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
        DrawFocus(g, outer, outer.Height / 2);
    }

    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
}

/// <summary>Draws key combinations as keycaps, the way Windows and PowerToys display shortcuts.</summary>
internal static class Keycaps
{
    public static float Draw(Graphics g, IReadOnlyList<string> keys, Font font, float x, float centerY, float s, SettingsTheme theme, bool accent, bool measureOnly = false)
    {
        float start = x, h = font.GetHeight(g) + 8 * s;
        foreach (var key in keys)
        {
            if (key == "/")
            {
                var slash = TextRenderer.MeasureText(g, "or", font, Size.Empty, TextFormatFlags.NoPadding);
                if (!measureOnly)
                    TextRenderer.DrawText(g, "or", font, new Point((int)(x + 6 * s), (int)(centerY - slash.Height / 2f)), theme.SubText, TextFormatFlags.NoPadding);
                x += slash.Width + 12 * s;
                continue;
            }
            var ts = TextRenderer.MeasureText(g, key, font, Size.Empty, TextFormatFlags.NoPadding);
            float w = Math.Max(h, ts.Width + 16 * s);
            if (!measureOnly)
            {
                var r = new RectangleF(x, centerY - h / 2, w, h);
                using var path = Ui.RoundRect(r, 5 * s);
                using var fill = new SolidBrush(accent ? theme.Accent : theme.Keycap);
                g.FillPath(fill, path);
                if (!accent)
                {
                    using var border = new Pen(Ui.WithAlpha(theme.FieldBorder, 200), 1f);
                    g.DrawPath(border, path);
                }
                TextRenderer.DrawText(g, key, font, Rectangle.Round(r), accent ? theme.OnAccent : theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
            x += w + 5 * s;
        }
        return x - start;
    }
}

/// <summary>Click, then press a key combination. Backspace clears it; Esc leaves it unchanged.</summary>
internal sealed class HotkeyBox : ThemedControl
{
    readonly Action<Keys> interceptor;
    Keys hotkey;
    public event EventHandler? HotkeyChanged;

    public HotkeyBox(SettingsTheme theme) : base(theme)
    {
        SetStyle(ControlStyles.Selectable, true);
        Size = new Size(220, 36);
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.HotkeyField;
        interceptor = Intercept;
    }

    public Keys Hotkey
    {
        get => hotkey;
        set { hotkey = value; AccessibleDescription = HotkeyText.Format(value); Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); }

    protected override void OnGotFocus(EventArgs e) { KeyboardHook.Interceptor = interceptor; base.OnGotFocus(e); }

    protected override void OnLostFocus(EventArgs e)
    {
        if (KeyboardHook.Interceptor == interceptor) KeyboardHook.Interceptor = null;
        base.OnLostFocus(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (KeyboardHook.Interceptor == interceptor) KeyboardHook.Interceptor = null;
        base.Dispose(disposing);
    }

    void Intercept(Keys combo)
    {
        if (!Focused || IsDisposed) return;
        var code = combo & Keys.KeyCode;
        var mods = combo & Keys.Modifiers;
        if (mods == Keys.None && code == Keys.Escape)
        {
            Parent?.SelectNextControl(this, true, true, true, true);
            return;
        }
        Hotkey = mods == Keys.None && code is Keys.Back or Keys.Delete ? Keys.None : combo;
        HotkeyChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Prepare(e);
        float s = DpiScale;
        bool listening = Focused;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Ui.RoundRect(r, 7 * s);
        using (var fill = new SolidBrush(Theme.Field)) g.FillPath(fill, path);
        if (Hovered && !listening) { using var hover = new SolidBrush(Theme.Hover); g.FillPath(hover, path); }
        using (var border = new Pen(listening ? Theme.Accent : Theme.FieldBorder, listening ? 2f * s : 1f)) g.DrawPath(border, path);

        float cy = Height / 2f, x = 8 * s;
        if (hotkey == Keys.None)
        {
            string hint = listening ? "Press a shortcut…" : "None";
            var size = TextRenderer.MeasureText(g, hint, Font);
            TextRenderer.DrawText(g, hint, Font, new Point((int)(10 * s), (int)(cy - size.Height / 2f)), Theme.SubText);
            return;
        }
        Keycaps.Draw(g, HotkeyText.Format(hotkey).Split(" + "), Font, x, cy, s, Theme, accent: listening);
    }
}

/// <summary>Read-only keycap display for the shortcut reference.</summary>
internal sealed class KeycapLabel : ThemedControl
{
    readonly string[] keys;

    public KeycapLabel(SettingsTheme theme, params string[] keys) : base(theme)
    {
        this.keys = keys;
        Size = new Size(260, 30);
        AccessibleRole = AccessibleRole.StaticText;
        AccessibleName = string.Join(" ", keys);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Prepare(e);
        float s = DpiScale;
        float width = Keycaps.Draw(g, keys, Font, 0, Height / 2f, s, Theme, false, measureOnly: true);
        Keycaps.Draw(g, keys, Font, Width - width, Height / 2f, s, Theme, false);
    }
}

/// <summary>Borderless text box inside a rounded field with an accent underline when focused.</summary>
internal sealed class FieldBox : Panel
{
    readonly SettingsTheme theme;
    public readonly TextBox Box;

    public FieldBox(SettingsTheme theme)
    {
        this.theme = theme;
        Box = new TextBox { BorderStyle = BorderStyle.None, BackColor = theme.Field, ForeColor = theme.Text };
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = theme.Card;
        Height = 36;
        Controls.Add(Box);
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int pad = (int)(12 * DeviceDpi / 96f);
        Box.SetBounds(pad, (Height - Box.Height) / 2, Math.Max(10, Width - pad * 2), Box.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Ui.RoundRect(r, 7 * s);
        using (var fill = new SolidBrush(theme.Field)) g.FillPath(fill, path);
        using (var border = new Pen(theme.FieldBorder, 1f)) g.DrawPath(border, path);
        if (Box.Focused)
        {
            var state = g.Save();
            g.SetClip(path);
            using var accent = new SolidBrush(theme.Accent);
            g.FillRectangle(accent, r.X, r.Bottom - 2 * s, r.Width, 2 * s);
            g.Restore(state);
        }
    }
}

/// <summary>Rounded settings card; rows are stacked top to bottom.</summary>
internal sealed class Card : Panel
{
    readonly SettingsTheme theme;

    public Card(SettingsTheme theme)
    {
        this.theme = theme;
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = theme.Window;
        Padding = new Padding(1);
    }

    public Card Add(SettingRow row)
    {
        row.First = Controls.Count == 0;
        row.Dock = DockStyle.Top;
        Controls.Add(row);
        row.BringToFront();
        Height = Controls.Cast<Control>().Sum(c => c.Height) + Padding.Vertical;
        return this;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Ui.RoundRect(r, 10 * s);
        using (var fill = new SolidBrush(theme.Card)) g.FillPath(fill, path);
        using (var border = new Pen(theme.CardBorder, 1f)) g.DrawPath(border, path);
    }
}

/// <summary>One setting: title and description on the left, its control on the right (or below, for wide rows).</summary>
internal sealed class SettingRow : Panel
{
    readonly SettingsTheme theme;
    readonly string title;
    readonly Func<string?>? description;
    readonly Control? control;
    readonly bool wide;
    Font? smallFont;
    public bool First;

    public SettingRow(SettingsTheme theme, string title, string? description, Control? control, bool wide = false)
        : this(theme, title, description == null ? null : () => description, control, wide) { }

    public SettingRow(SettingsTheme theme, string title, Func<string?>? description, Control? control, bool wide = false)
    {
        this.theme = theme;
        this.title = title;
        this.description = description;
        this.control = control;
        this.wide = wide;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        bool hasDescription = description != null;
        Height = wide ? (hasDescription ? 108 : 90) : (hasDescription ? 68 : 54);
        Padding = new Padding(20, 0, 20, 0);
        AccessibleName = title;
        if (control != null)
        {
            Controls.Add(control);
            if (string.IsNullOrEmpty(control.AccessibleName)) control.AccessibleName = title;
        }
    }

    float S => DeviceDpi / 96f;

    Font SmallFont => smallFont ??= new Font(Font.FontFamily, Font.Size * 0.86f, FontStyle.Regular);

    protected override void OnFontChanged(EventArgs e)
    {
        smallFont?.Dispose();
        smallFont = null;
        base.OnFontChanged(e);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (control == null) return;
        if (wide)
            control.SetBounds(Padding.Left, Height - control.Height - (int)(16 * S), Width - Padding.Horizontal, control.Height);
        else
            control.Location = new Point(Width - Padding.Right - control.Width, (Height - control.Height) / 2);
    }

    public void RefreshText() => Invalidate();

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (!First)
        {
            using var pen = new Pen(theme.Divider);
            g.DrawLine(pen, 0, 0, Width, 0);
        }
        int right = wide || control == null ? Width - Padding.Right : control.Left - (int)(20 * S);
        int width = Math.Max(10, right - Padding.Left);
        string? desc = description?.Invoke();
        int th = TextRenderer.MeasureText(g, "Ag", Font).Height;
        int dh = desc == null ? 0 : TextRenderer.MeasureText(g, "Ag", SmallFont).Height;
        int top = wide ? (int)(14 * S) : (Height - th - dh) / 2;
        const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, title, Font, new Rectangle(Padding.Left, top, width, th), theme.Text, flags);
        if (desc != null)
            TextRenderer.DrawText(g, desc, SmallFont, new Rectangle(Padding.Left, top + th, width, dh), theme.SubText, flags);
    }
}

/// <summary>Sidebar navigation item with a rounded selection and an accent indicator.</summary>
internal sealed class NavButton : ThemedControl
{
    readonly Icons.Glyph glyph;
    bool selected;

    public NavButton(SettingsTheme theme, string text, Icons.Glyph glyph) : base(theme)
    {
        this.glyph = glyph;
        SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
        Text = text;
        Height = 40;
        BackColor = theme.Window;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PageTab;
        AccessibleName = text;
    }

    public bool Selected
    {
        get => selected;
        set { selected = value; Invalidate(); }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) OnClick(EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Prepare(e);
        float s = DpiScale;
        var r = new RectangleF(2 * s, 2 * s, Width - 4 * s, Height - 4 * s);
        using var path = Ui.RoundRect(r, 7 * s);
        if (selected || Hovered)
        {
            using var fill = new SolidBrush(selected ? Theme.Selected : Theme.Hover);
            g.FillPath(fill, path);
        }
        if (selected)
        {
            float ih = 16 * s;
            using var bar = Ui.RoundRect(new RectangleF(r.X + 1 * s, r.Y + (r.Height - ih) / 2, 3 * s, ih), 1.5f * s);
            using var accent = new SolidBrush(Theme.Accent);
            g.FillPath(accent, bar);
        }
        float icon = 18 * s;
        Icons.Draw(g, new RectangleF(r.X + 14 * s, r.Y + (r.Height - icon) / 2, icon, icon), Theme.Text, glyph, 1.7f);
        var tr = new Rectangle((int)(r.X + 44 * s), (int)r.Y, (int)(r.Width - 48 * s), (int)r.Height);
        TextRenderer.DrawText(g, Text, Font, tr, Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        DrawFocus(g, r, 7 * s);
    }
}
