using System.Drawing.Drawing2D;

namespace LiteShot.Overlay;

/// <summary>The text style bar and the text formatting commands behind it.</summary>
internal sealed partial class CaptureOverlay
{
    static readonly int[] FontSizeMenu = { 12, 14, 16, 18, 20, 24, 28, 32, 40, 48, 64, 80, 96, 128 };

    readonly Toolbar textBar;
    ToolButton textColorButton = null!;
    ToolButton? paletteAnchor;

    // Defaults for the next text box; changing the style of a text also updates them.
    string textFamily = FontRegistry.DefaultFamily;
    bool textBold, textItalic;
    TextAlign textAlign;
    TextLook textLook;

    /// <summary>The text that style commands apply to: the one being edited, else the selected one.</summary>
    TextShape? TextTarget => editing ?? (tool == Tool.None ? selectedShape as TextShape : null);

    bool TextBarVisible => ChromeVisible && (TextTarget != null || tool == Tool.Text);

    void LoadTextDefaults(AppSettings s)
    {
        textFamily = string.IsNullOrWhiteSpace(s.TextFamily) ? FontRegistry.DefaultFamily : s.TextFamily;
        textBold = s.TextBold;
        textItalic = s.TextItalic;
        textAlign = Enum.TryParse<TextAlign>(s.TextAlign, out var a) ? a : TextAlign.Left;
        textLook = Enum.TryParse<TextLook>(s.TextLook, out var l) ? l : TextLook.Plain;
    }

    void SaveTextDefaults(AppSettings s)
    {
        s.TextFamily = textFamily;
        s.TextBold = textBold;
        s.TextItalic = textItalic;
        s.TextAlign = textAlign.ToString();
        s.TextLook = textLook.ToString();
    }

    TextShape NewTextShape() => new()
    {
        Color = color,
        Size = LiveSize(Tool.Text),
        FamilyName = textFamily,
        Bold = textBold,
        Italic = textItalic,
        Align = textAlign,
        Look = textLook,
    };

    /// <summary>Current style: from the target text, or the defaults when there is none.</summary>
    TextShape StyleSource => TextTarget ?? NewTextShape();

    Toolbar BuildTextBar()
    {
        textColorButton = new ToolButton { Tip = "Text color", OnClick = () => TogglePalette(textColorButton), CustomPaint = PaintColorButton };
        return new Toolbar(vertical: false)
            .Group(new ToolButton { Tip = "Font", Shortcut = "Click to choose", Width = 168, PaintsFullButton = true, CustomPaint = PaintFontButton, OnClick = () => ShowFontMenu() })
            .Group(
                new ToolButton { Tip = "Smaller", Shortcut = "Ctrl+[", Glyph = MinusGlyph, OnClick = () => StepTextSize(-1) },
                new ToolButton { Tip = "Font size", Shortcut = "Click for sizes", Width = 46, PaintsFullButton = true, CustomPaint = PaintSizeValue, OnClick = ShowSizeMenu },
                new ToolButton { Tip = "Larger", Shortcut = "Ctrl+]", Glyph = PlusGlyph, OnClick = () => StepTextSize(1) })
            .Group(
                new ToolButton { Tip = "Bold", Shortcut = "Ctrl+B", CustomPaint = (g, r, t) => PaintLetter(g, r, t, "B", FontStyle.Bold), OnClick = () => ApplyTextStyle(x => x.Bold = !x.Bold), IsActive = () => StyleSource.Bold },
                new ToolButton { Tip = "Italic", Shortcut = "Ctrl+I", CustomPaint = (g, r, t) => PaintLetter(g, r, t, "I", FontStyle.Italic), OnClick = () => ApplyTextStyle(x => x.Italic = !x.Italic), IsActive = () => StyleSource.Italic })
            .Group(
                new ToolButton { Tip = "Align left", Shortcut = "Ctrl+L", Glyph = AlignGlyph(TextAlign.Left), OnClick = () => ApplyTextStyle(x => x.Align = TextAlign.Left), IsActive = () => StyleSource.Align == TextAlign.Left },
                new ToolButton { Tip = "Center", Shortcut = "Ctrl+E", Glyph = AlignGlyph(TextAlign.Center), OnClick = () => ApplyTextStyle(x => x.Align = TextAlign.Center), IsActive = () => StyleSource.Align == TextAlign.Center },
                new ToolButton { Tip = "Align right", Shortcut = "Ctrl+R", Glyph = AlignGlyph(TextAlign.Right), OnClick = () => ApplyTextStyle(x => x.Align = TextAlign.Right), IsActive = () => StyleSource.Align == TextAlign.Right })
            .Group(
                new ToolButton { Tip = "Text look", Shortcut = "Plain · Outline · Label · Shadow", CustomPaint = PaintLookButton, OnClick = () => ApplyTextStyle(x => x.Look = (TextLook)(((int)x.Look + 1) % 4)) },
                textColorButton);
    }

    void LayoutTextBar()
    {
        var size = textBar.Measure(S);
        var mon = MonitorAt(Center(sel));
        float gap = 10 * S, x, y;
        RectangleF anchor;
        if (TextTarget is TextShape t)
        {
            anchor = EditorBox(t);
            x = anchor.X;
            y = anchor.Top - gap - size.Height;
            if (y < mon.Top + 4) y = anchor.Bottom + gap;
        }
        else
        {
            // Text tool armed: preset the style before clicking. Sits above the selection's right edge.
            anchor = sel;
            x = sel.Right - size.Width;
            y = sel.Top - gap - size.Height;
            if (y < mon.Top + 4) y = sel.Top + gap;
        }
        // Slide left, clear of the tools bar, when the two would overlap.
        var tb = tools.Bounds;
        if (new RectangleF(x, y, size.Width, size.Height).IntersectsWith(RectangleF.Inflate(tb, gap, gap)))
            x = tb.Left > anchor.Left ? tb.Left - gap - size.Width : tb.Right + gap;
        x = Math.Clamp(x, mon.Left + 4, Math.Max(mon.Left + 4, mon.Right - size.Width - 4));
        y = Math.Clamp(y, mon.Top + 4, Math.Max(mon.Top + 4, mon.Bottom - size.Height - 4));
        textBar.Anchor = anchor;
        textBar.Layout(new PointF(MathF.Round(x), MathF.Round(y)), S);
    }

    /// <summary>Applies a style change to the target text (with undo) and remembers it for new text.</summary>
    void ApplyTextStyle(Action<TextShape> change)
    {
        var t = TextTarget;
        if (t == null)
        {
            var probe = NewTextShape();
            change(probe);
            RememberStyle(probe);
        }
        else
        {
            var before = t == editing ? EditorBox(t) : t.Bounds;
            if (t != editing) PushUndo();
            change(t);
            RememberStyle(t);
            var after = t == editing ? EditorBox(t) : t.Bounds;
            InvalidateRect(RectangleF.Union(before, after), Px(10));
        }
        InvalidateRect(textBar.Bounds, Px(4));
        UpdateView(true);
    }

    void RememberStyle(TextShape t)
    {
        textFamily = t.FamilyName;
        textBold = t.Bold;
        textItalic = t.Italic;
        textAlign = t.Align;
        textLook = t.Look;
    }

    void StepTextSize(int dir)
    {
        var (min, max, step) = SizeRange(Tool.Text);
        if (TextTarget is TextShape t)
        {
            float value = Math.Clamp(MathF.Round(t.Size / S / step) * step + dir * step, min, max);
            ApplySize(t, value);
            sizes[Tool.Text] = value;
        }
        else sizes[Tool.Text] = Math.Clamp(sizes[Tool.Text] + dir * step, min, max);
        InvalidateRect(textBar.Bounds, Px(4));
        UpdateView(true);
    }

    void SetTextSize(float logical)
    {
        sizes[Tool.Text] = logical;
        if (TextTarget is TextShape t) ApplySize(t, logical);
        InvalidateRect(textBar.Bounds, Px(4));
        UpdateView(true);
    }

    bool HandleTextStyleKey(Keys k)
    {
        switch (k)
        {
            case Keys.Control | Keys.B: ApplyTextStyle(x => x.Bold = !x.Bold); return true;
            case Keys.Control | Keys.I: ApplyTextStyle(x => x.Italic = !x.Italic); return true;
            case Keys.Control | Keys.L: ApplyTextStyle(x => x.Align = TextAlign.Left); return true;
            case Keys.Control | Keys.E: ApplyTextStyle(x => x.Align = TextAlign.Center); return true;
            case Keys.Control | Keys.R: ApplyTextStyle(x => x.Align = TextAlign.Right); return true;
            case Keys.Control | Keys.OemCloseBrackets:
            case Keys.Control | Keys.Shift | Keys.OemPeriod:
                StepTextSize(1);
                return true;
            case Keys.Control | Keys.OemOpenBrackets:
            case Keys.Control | Keys.Shift | Keys.Oemcomma:
                StepTextSize(-1);
                return true;
        }
        return false;
    }

    // ───────────────────────────── menus ─────────────────────────────

    ContextMenuStrip NewMenu()
    {
        var menu = new ContextMenuStrip { Renderer = new ModernMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = true };
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        return menu;
    }

    void ShowFontMenu()
    {
        HideTooltip();
        var menu = NewMenu();
        string current = StyleSource.FamilyName;

        ToolStripMenuItem Item(string family, string? note)
        {
            var item = new ToolStripMenuItem(family, null, (_, _) => ApplyTextStyle(x => x.FamilyName = family))
            {
                Checked = string.Equals(family, current, StringComparison.OrdinalIgnoreCase),
                ShortcutKeyDisplayString = note,
                Padding = new Padding(2, 3, 2, 3),
            };
            try { item.Font = new Font(FontRegistry.Resolve(family), 12f, FontStyle.Regular); }
            catch (ArgumentException) { /* the family has no regular style; keep the menu font */ }
            return item;
        }

        ToolStripMenuItem Header(string text) => new(text) { Enabled = false, Font = new Font("Segoe UI Semibold", 8.5f) };

        menu.Items.Add(Header("Built in"));
        foreach (var choice in FontRegistry.Curated.Where(c => FontRegistry.IsAvailable(c.Family)))
            menu.Items.Add(Item(choice.Family, choice.Label));

        var users = FontRegistry.UserFamilies;
        if (users.Count > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Header("Your fonts"));
            foreach (var family in users) menu.Items.Add(Item(family, null));
        }

        menu.Items.Add(new ToolStripSeparator());
        var all = new ToolStripMenuItem("All installed fonts") { Padding = new Padding(2, 3, 2, 3) };
        all.DropDown.Renderer = new ModernMenuRenderer();
        all.DropDownItems.Add("…");
        all.DropDownOpening += (_, _) =>
        {
            if (all.DropDownItems.Count > 1) return;
            all.DropDownItems.Clear();
            foreach (var family in FontRegistry.InstalledFamilies) all.DropDownItems.Add(Item(family, null));
        };
        menu.Items.Add(all);
        menu.Items.Add(new ToolStripMenuItem("Add a font file…", null, (_, _) => AddFontFile()) { Padding = new Padding(2, 3, 2, 3) });
        ModernMenuRenderer.Attach(menu);
        ModernMenuRenderer.Attach(all.DropDown);

        var anchor = textBar.Groups[0].Bounds;
        menu.Show(this, new Point((int)anchor.Left, (int)anchor.Bottom + Px(6) - scrollY));
    }

    void AddFontFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Add a font",
            Filter = "Fonts (*.ttf, *.otf)|*.ttf;*.otf",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) { Invalidate(); return; }
        try
        {
            string? family = FontRegistry.AddUserFont(dialog.FileName);
            if (family == null) ShowFlash("That file isn't a font LiteShot can use");
            else
            {
                ApplyTextStyle(x => x.FamilyName = family);
                ShowFlash($"Added {family}");
            }
        }
        catch (Exception ex) { ShowFlash(ex.Message); }
        Invalidate();
    }

    void ShowSizeMenu()
    {
        HideTooltip();
        var menu = NewMenu();
        float current = MathF.Round(StyleSource.Size / S);
        foreach (int size in FontSizeMenu)
        {
            int value = size;
            menu.Items.Add(new ToolStripMenuItem($"{size} px", null, (_, _) => SetTextSize(value)) { Checked = Math.Abs(current - size) < 0.5f, Padding = new Padding(2, 2, 2, 2) });
        }
        ModernMenuRenderer.Attach(menu);
        var anchor = textBar.Groups[1].Bounds;
        menu.Show(this, new Point((int)anchor.Left, (int)anchor.Bottom + Px(6) - scrollY));
    }

    // ───────────────────────────── painting ─────────────────────────────

    void PaintFontButton(Graphics g, RectangleF r, GlassTone tone)
    {
        string family = StyleSource.FamilyName;
        float pad = 14 * S, chevron = 10 * S;
        var textArea = new RectangleF(r.X + pad, r.Y, r.Width - pad * 2 - chevron - 4 * S, r.Height);
        Font? font = null;
        try { font = new Font(FontRegistry.Resolve(family), 14f * S, FontStyle.Regular, GraphicsUnit.Pixel); }
        catch (ArgumentException) { }
        font ??= Ui.MakeFont(14f * S);
        using (font)
        using (var brush = new SolidBrush(tone.Glyph))
        using (var format = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center })
            g.DrawString(family, font, brush, textArea, format);
        float cx = r.Right - pad - chevron / 2, cy = r.Y + r.Height / 2;
        using var pen = new Pen(tone.Secondary, Math.Max(1.2f, 1.6f * S)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[] { new PointF(cx - chevron / 2, cy - chevron / 4), new PointF(cx, cy + chevron / 4), new PointF(cx + chevron / 2, cy - chevron / 4) });
    }

    void PaintSizeValue(Graphics g, RectangleF r, GlassTone tone) =>
        Ui.DrawTextCentered(g, $"{MathF.Round(StyleSource.Size / S):0}", labelFont, tone.Glyph, r);

    void PaintLetter(Graphics g, RectangleF r, GlassTone tone, string letter, FontStyle style)
    {
        bool active = style == FontStyle.Bold ? StyleSource.Bold : StyleSource.Italic;
        var color = active ? (SystemTheme.HighContrast ? SystemColors.HighlightText : Color.White) : tone.Glyph;
        using var font = new Font(style == FontStyle.Italic ? Ui.Family("Georgia", "Segoe UI") : Ui.RegularFamily, r.Height * 0.95f, style, GraphicsUnit.Pixel);
        Ui.DrawTextCentered(g, letter, font, color, r);
    }

    void PaintLookButton(Graphics g, RectangleF r, GlassTone tone)
    {
        var look = StyleSource.Look;
        var area = RectangleF.Inflate(r, 2 * S, 2 * S);
        using var path = new GraphicsPath();
        float em = area.Height * 0.9f;
        path.AddString("A", Ui.SemiboldFamily, (int)FontStyle.Bold, em, PointF.Empty, StringFormat.GenericTypographic);
        var bb = path.GetBounds();
        using (var m = new Matrix())
        {
            m.Translate(area.X + (area.Width - bb.Width) / 2 - bb.X, area.Y + (area.Height - bb.Height) / 2 - bb.Y);
            path.Transform(m);
        }
        var ink = tone.Glyph;
        switch (look)
        {
            case TextLook.Label:
                using (var box = Ui.RoundRect(area, 4 * S))
                using (var fill = new SolidBrush(ink))
                    g.FillPath(fill, box);
                ink = tone.Light ? Color.White : Color.FromArgb(28, 28, 30);
                break;
            case TextLook.Outline:
                using (var halo = new Pen(tone.Glyph, 1.4f * S) { LineJoin = LineJoin.Round })
                    g.DrawPath(halo, path);
                return;
            case TextLook.Shadow:
                using (var shadow = (GraphicsPath)path.Clone())
                using (var shift = new Matrix())
                {
                    shift.Translate(1.6f * S, 1.6f * S);
                    shadow.Transform(shift);
                    using var shade = new SolidBrush(tone.Secondary);
                    g.FillPath(shade, shadow);
                }
                break;
        }
        using var brush = new SolidBrush(ink);
        g.FillPath(brush, path);
    }

    static void MinusGlyph(Graphics g, Pen p, Brush f) => g.DrawLine(p, 6, 12, 18, 12);

    static void PlusGlyph(Graphics g, Pen p, Brush f)
    {
        g.DrawLine(p, 6, 12, 18, 12);
        g.DrawLine(p, 12, 6, 12, 18);
    }

    static Icons.Glyph AlignGlyph(TextAlign align) => (g, p, f) =>
    {
        float[] widths = { 16, 10, 16, 10 };
        for (int i = 0; i < 4; i++)
        {
            float w = widths[i], y = 6 + i * 4;
            float x = align switch { TextAlign.Center => 12 - w / 2, TextAlign.Right => 20 - w, _ => 4 };
            g.DrawLine(p, x, y, x + w, y);
        }
    };
}
