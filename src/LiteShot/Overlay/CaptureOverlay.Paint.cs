using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace LiteShot.Overlay;

internal sealed partial class CaptureOverlay
{
    const int MagCells = 15;

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (closing) return;
        var g = e.Graphics;
        var clip = Rectangle.Intersect(e.ClipRectangle, client);
        if (clip.Width <= 0 || clip.Height <= 0) return;

        // Content layer: the frozen screen, dimmed outside the selection. No glass here.
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(dimmed, clip, clip.X, clip.Y, clip.Width, clip.Height, GraphicsUnit.Pixel);
        var bright = Rectangle.Intersect(Lit, clip);
        if (bright.Width > 0 && bright.Height > 0)
            g.DrawImage(shot, bright, bright.X, bright.Y, bright.Width, bright.Height, GraphicsUnit.Pixel);
        g.CompositingMode = CompositingMode.SourceOver;
        g.PixelOffsetMode = PixelOffsetMode.None;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        if (hasSel)
        {
            if (sel.IntersectsWith(clip)) DrawAnnotations(g);
            DrawBrushCursor(g);
            DrawSelectionFrame(g, sel, handles: !(dragMoved && drag is DragMode.NewSelection or DragMode.MoveSelection));
            DrawSizeLabel(g, sel);

            // Functional layer: glass floats above the content.
            if (ChromeVisible)
            {
                LayoutChrome();
                tools.Draw(g, glass, sel, S, hoverButton, hoverAnim, pressedButton);
                actions.Draw(g, glass, sel, S, hoverButton, hoverAnim, pressedButton);
                if (TextBarVisible) textBar.Draw(g, glass, sel, S, hoverButton, hoverAnim, pressedButton);
                if (AiOpen) DrawAiPrompt(g);
                if (paletteOpen) DrawPalette(g);
                if (tooltipVisible && hoverButton != null) DrawTooltip(g, hoverButton);
            }
        }
        else if (hoverWindow is Rectangle hw)
        {
            DrawWindowHighlight(g, hw);
            DrawSizeLabel(g, hw);
        }

        if (MagnifierVisible) DrawMagnifier(g);
        if (flashText != null) DrawFlash(g);
    }

    void DrawAnnotations(Graphics g)
    {
        var state = g.Save();
        g.SetClip(sel, CombineMode.Intersect);
        g.PixelOffsetMode = PixelOffsetMode.Half;
        foreach (var a in shapes) a.Draw(g, shot);
        drawing?.Draw(g, shot);
        if (editing != null) DrawTextEditor(g, editing);
        g.Restore(state);

        if (selectedShape != null && editing == null && shapes.Contains(selectedShape))
        {
            var r = RectangleF.Inflate(selectedShape.Bounds, 3 * S, 3 * S);
            using var shade = new Pen(Color.FromArgb(110, 0, 0, 0), 1f);
            using var pen = new Pen(Color.White, 1f) { DashPattern = new[] { 4f, 3f } };
            using var path = Ui.RoundRect(r, 4 * S);
            g.DrawPath(shade, path);
            g.DrawPath(pen, path);
        }
    }

    void DrawSelectionFrame(Graphics g, Rectangle r, bool handles)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.None;
        using (var shade = new Pen(Color.FromArgb(90, 0, 0, 0)))
            g.DrawRectangle(shade, r.X - 2, r.Y - 2, r.Width + 3, r.Height + 3);
        using (var line = new Pen(SystemTheme.HighContrast ? SystemColors.Highlight : Color.FromArgb(240, 255, 255, 255)))
            g.DrawRectangle(line, r.X - 1, r.Y - 1, r.Width + 1, r.Height + 1);
        g.Restore(state);
        if (!handles) return;

        // Small glass knobs, like the thumbs of the system controls.
        float d = 9f * S;
        using var knobShadow = new SolidBrush(Color.FromArgb(55, 0, 0, 0));
        using var knob = new SolidBrush(SystemTheme.HighContrast ? SystemColors.Highlight : Color.White);
        using var edge = new Pen(Color.FromArgb(70, 0, 0, 0), 1f);
        foreach (var c in HandleCenters(r))
        {
            var kr = new RectangleF(c.X - d / 2, c.Y - d / 2, d, d);
            g.FillEllipse(knobShadow, kr.X - 0.5f * S, kr.Y + 0.5f * S, d + S, d + S);
            g.FillEllipse(knob, kr);
            g.DrawEllipse(edge, kr);
        }
    }

    static string SizeText(Rectangle r) => $"{r.Width} × {r.Height}";

    RectangleF SizeLabelRect(Rectangle r)
    {
        var ts = Ui.Measure(SizeText(r), labelFont);
        float w = MathF.Ceiling(ts.Width + 22 * S), h = MathF.Ceiling(ts.Height + 11 * S);
        var mon = MonitorAt(r.Location);
        float x = r.X, y = r.Y - h - 9 * S;
        if (y < mon.Top + 2)
        {
            x = r.X + 9 * S;
            y = r.Y + 9 * S;
        }
        x = Math.Clamp(x, mon.Left + 2, Math.Max(mon.Left + 2, mon.Right - w - 2));
        return new RectangleF(MathF.Round(x), MathF.Round(y), w, h);
    }

    void DrawSizeLabel(Graphics g, Rectangle r)
    {
        var lr = SizeLabelRect(r);
        var tone = glass.ToneAt(lr, Lit);
        glass.Draw(g, lr, lr.Height / 2, Lit, tone, S);
        Ui.DrawTextCentered(g, SizeText(r), labelFont, tone.Glyph, lr);
    }

    void DrawWindowHighlight(Graphics g, Rectangle r)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.None;
        using var pen = new Pen(SystemTheme.HighContrast ? SystemColors.Highlight : SystemTheme.Accent, Math.Max(2, Px(2))) { Alignment = PenAlignment.Inset };
        g.DrawRectangle(pen, r);
        g.Restore(state);
    }

    void DrawTextEditor(Graphics g, TextShape t)
    {
        var box = EditorBox(t);
        using (var shade = new Pen(Color.FromArgb(120, 0, 0, 0), 1f))
            g.DrawRectangle(shade, box.X, box.Y, box.Width, box.Height);
        using (var pen = new Pen(Color.FromArgb(220, 255, 255, 255), 1f) { DashPattern = new[] { 4f, 3f } })
            g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
        if (caretOn)
        {
            var cp = t.CaretPoint(caret);
            using var brush = new SolidBrush(t.Color);
            g.FillRectangle(brush, cp.X, cp.Y, Math.Max(1.5f, 2f * S), t.LineHeight);
        }
    }

    RectangleF EditorBox(TextShape t)
    {
        var b = t.Bounds;
        return new RectangleF(b.X - 9 * S, b.Y - 7 * S, Math.Max(b.Width, t.Size * 0.6f) + 18 * S, b.Height + 14 * S);
    }

    /// <summary>The padding ring between the text and its dashed frame.</summary>
    bool OnEditorFrame(TextShape t, Point p) =>
        EditorBox(t).Contains(p) && !RectangleF.Inflate(t.TextRect, 2 * S, 1 * S).Contains(p);

    void InvalidateCaret()
    {
        if (editing == null) return;
        var cp = editing.CaretPoint(caret);
        InvalidateRect(new RectangleF(cp.X - 2, cp.Y - 2, 4 * S + 4, editing.LineHeight + 4), 1);
    }

    // ───────────────────────────── loupe ─────────────────────────────

    bool MagnifierVisible => AppSettings.Current.ShowMagnifier && mouseInside && !closing && !paletteOpen
        && (!hasSel || drag is DragMode.NewSelection or DragMode.Resize) && client.Contains(mouse);

    Color PixelAt(Point p) => shot.GetPixel(Math.Clamp(p.X, 0, shot.Width - 1), Math.Clamp(p.Y, 0, shot.Height - 1));

    string CoordText => $"{mouse.X + virt.X}, {mouse.Y + virt.Y}";

    static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    (RectangleF Lens, RectangleF Info) MagnifierLayout()
    {
        float cell = MathF.Max(6, MathF.Round(8 * S));
        float ring = 6 * S, d = MagCells * cell + ring * 2, gap = 8 * S;
        var coords = Ui.Measure(CoordText, smallFont);
        var hex = Ui.Measure("#FFFFFF", labelFont);
        float infoH = MathF.Ceiling(hex.Height + 12 * S);
        float infoW = MathF.Ceiling(14 * S + coords.Width + 12 * S + 12 * S + 6 * S + hex.Width + 14 * S);
        float totalW = Math.Max(d, infoW), totalH = d + gap + infoH;
        var mon = MonitorAt(mouse);
        float off = 24 * S;
        float x = mouse.X + off, y = mouse.Y + off;
        if (x + totalW > mon.Right) x = mouse.X - off - totalW;
        if (y + totalH > mon.Bottom) y = mouse.Y - off - totalH;
        var lens = new RectangleF(MathF.Round(x + (totalW - d) / 2), MathF.Round(y), MathF.Round(d), MathF.Round(d));
        var info = new RectangleF(MathF.Round(x + (totalW - infoW) / 2), MathF.Round(lens.Bottom + gap), infoW, infoH);
        return (lens, info);
    }

    Rectangle MagnifierBounds()
    {
        var (lens, info) = MagnifierLayout();
        return Ui.Outer(RectangleF.Union(lens, info), Px(14));
    }

    void DrawMagnifier(Graphics g)
    {
        var (outer, info) = MagnifierLayout();
        var lit = Lit;
        glass.Draw(g, outer, outer.Width / 2, lit, glass.ToneAt(outer, lit), S);

        float ring = 6 * S;
        var lens = RectangleF.Inflate(outer, -ring, -ring);
        float cell = lens.Width / MagCells;
        int half = MagCells / 2;
        var state = g.Save();
        using (var round = new GraphicsPath())
        {
            round.AddEllipse(lens);
            g.SetClip(round, CombineMode.Intersect);
        }
        g.FillRectangle(Brushes.Black, lens);
        var src = new Rectangle(mouse.X - half, mouse.Y - half, MagCells, MagCells);
        var valid = Rectangle.Intersect(src, client);
        if (valid.Width > 0 && valid.Height > 0)
        {
            var dest = new RectangleF(lens.X + (valid.X - src.X) * cell, lens.Y + (valid.Y - src.Y) * cell, valid.Width * cell, valid.Height * cell);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(shot, dest, valid, GraphicsUnit.Pixel);
            g.PixelOffsetMode = PixelOffsetMode.None;
        }
        using (var grid = new Pen(Color.FromArgb(26, 0, 0, 0), 1f))
            for (int i = 1; i < MagCells; i++)
            {
                g.DrawLine(grid, lens.X + i * cell, lens.Y, lens.X + i * cell, lens.Bottom);
                g.DrawLine(grid, lens.X, lens.Y + i * cell, lens.Right, lens.Y + i * cell);
            }
        using (var band = new SolidBrush(Ui.WithAlpha(SystemTheme.Accent, 50)))
        {
            g.FillRectangle(band, lens.X, lens.Y + half * cell, lens.Width, cell);
            g.FillRectangle(band, lens.X + half * cell, lens.Y, cell, lens.Height);
        }
        var cc = new RectangleF(lens.X + half * cell, lens.Y + half * cell, cell, cell);
        g.SmoothingMode = SmoothingMode.None;
        using (var dark = new Pen(Color.Black, 1f)) g.DrawRectangle(dark, cc.X - 1, cc.Y - 1, cc.Width + 1, cc.Height + 1);
        using (var light = new Pen(Color.White, 1f)) g.DrawRectangle(light, cc.X, cc.Y, cc.Width - 1, cc.Height - 1);
        g.Restore(state);
        using (var inner = new Pen(Color.FromArgb(70, 0, 0, 0), 1f)) g.DrawEllipse(inner, lens);

        var pixel = PixelAt(mouse);
        var tone = glass.ToneAt(info, lit);
        glass.Draw(g, info, info.Height / 2, lit, tone, S);
        float x = info.X + 14 * S;
        var coords = Ui.Measure(CoordText, smallFont);
        Ui.DrawText(g, CoordText, smallFont, tone.Secondary, new PointF(x, info.Y + (info.Height - coords.Height) / 2));
        x += coords.Width + 12 * S;
        float sw = 12 * S;
        var swatch = new RectangleF(x, info.Y + (info.Height - sw) / 2, sw, sw);
        using (var fill = new SolidBrush(pixel)) g.FillEllipse(fill, swatch);
        using (var ringPen = new Pen(Ui.WithAlpha(tone.Glyph, 110), 1f)) g.DrawEllipse(ringPen, swatch);
        x += sw + 6 * S;
        var hexSize = Ui.Measure(Hex(pixel), labelFont);
        Ui.DrawText(g, Hex(pixel), labelFont, tone.Glyph, new PointF(x, info.Y + (info.Height - hexSize.Height) / 2));
    }

    // ───────────────────────────── flash & tooltip ─────────────────────────────

    void ShowFlash(string text)
    {
        InvalidateRect(lastFlash);
        lastFlash = Rectangle.Empty;
        flashText = text;
        flashAnchor = mouse;
        flashTimer.Stop();
        flashTimer.Start();
        UpdateView();
    }

    RectangleF FlashRect()
    {
        var ts = Ui.Measure(flashText ?? "", labelFont);
        float w = MathF.Ceiling(ts.Width + 26 * S), h = MathF.Ceiling(ts.Height + 14 * S);
        var mon = MonitorAt(flashAnchor);
        float x = Math.Clamp(flashAnchor.X + 18 * S, mon.Left + 4, mon.Right - w - 4);
        float y = Math.Clamp(flashAnchor.Y - h - 16 * S, mon.Top + 4, mon.Bottom - h - 4);
        return new RectangleF(MathF.Round(x), MathF.Round(y), w, h);
    }

    void DrawFlash(Graphics g)
    {
        var r = FlashRect();
        var tone = glass.ToneAt(r, Lit);
        glass.Draw(g, r, r.Height / 2, Lit, tone, S);
        Ui.DrawTextCentered(g, flashText!, labelFont, tone.Glyph, r);
    }

    RectangleF TooltipRect(ToolButton b)
    {
        var tip = Ui.Measure(b.Tip, smallFont);
        float keyW = b.Shortcut != null ? 10 * S + Ui.Measure(b.Shortcut, smallFont).Width : 0;
        float w = MathF.Ceiling(tip.Width + keyW + 26 * S), h = MathF.Ceiling(tip.Height + 13 * S);
        var mon = MonitorAt(Point.Round(Center(b.Bounds)));
        float gap = 10 * S, x, y;
        var bar = BarOf(b) ?? actions;
        var bb = bar.Bounds;
        if (bar.Vertical)
        {
            bool outsideRight = bb.Left >= bar.Anchor.Right - 1;
            x = outsideRight ? bb.Right + gap : bb.Left - gap - w;
            if (x + w > mon.Right) x = bb.Left - gap - w;
            if (x < mon.Left) x = bb.Right + gap;
            y = b.Bounds.Top + b.Bounds.Height / 2 - h / 2;
        }
        else
        {
            bool below = bb.Top >= bar.Anchor.Bottom - 1;
            y = below ? bb.Bottom + gap : bb.Top - gap - h;
            if (y + h > mon.Bottom) y = bb.Top - gap - h;
            if (y < mon.Top) y = bb.Bottom + gap;
            x = b.Bounds.Left + b.Bounds.Width / 2 - w / 2;
        }
        x = Math.Clamp(x, mon.Left + 4, Math.Max(mon.Left + 4, mon.Right - w - 4));
        y = Math.Clamp(y, mon.Top + 4, Math.Max(mon.Top + 4, mon.Bottom - h - 4));
        return new RectangleF(MathF.Round(x), MathF.Round(y), w, h);
    }

    void DrawTooltip(Graphics g, ToolButton b)
    {
        var r = TooltipRect(b);
        var tone = glass.ToneAt(r, sel);
        glass.Draw(g, r, r.Height / 2, sel, tone, S);
        var tip = Ui.Measure(b.Tip, smallFont);
        float y = r.Y + (r.Height - tip.Height) / 2;
        Ui.DrawText(g, b.Tip, smallFont, tone.Glyph, new PointF(r.X + 13 * S, y));
        if (b.Shortcut != null)
            Ui.DrawText(g, b.Shortcut, smallFont, tone.Secondary, new PointF(r.X + 13 * S + tip.Width + 10 * S, y));
    }

    // ───────────────────────────── color & size popover ─────────────────────────────

    static readonly Color[] Palette =
    {
        Color.FromArgb(0xFF, 0x3B, 0x30), Color.FromArgb(0xFF, 0x95, 0x00), Color.FromArgb(0xFF, 0xCC, 0x00),
        Color.FromArgb(0x34, 0xC7, 0x59), Color.FromArgb(0x00, 0xC7, 0xBE), Color.FromArgb(0x0A, 0x84, 0xFF),
        Color.FromArgb(0xAF, 0x52, 0xDE), Color.FromArgb(0xFF, 0x2D, 0x92), Color.FromArgb(0xA2, 0x84, 0x5E),
        Color.White, Color.FromArgb(0xC7, 0xC7, 0xCC), Color.FromArgb(0x8E, 0x8E, 0x93), Color.Black,
    };
    const int PaletteCols = 7, CustomColorIndex = 13, SizeIndexBase = 100;

    float PalPad => 14 * S;
    float PalCell => 26 * S;
    float PalGap => 9 * S;

    void LayoutPalette()
    {
        float w = PalPad * 2 + PaletteCols * PalCell + (PaletteCols - 1) * PalGap;
        float h = PalPad * 2 + 3 * PalCell + 2 * PalGap + 12 * S;
        var mon = MonitorAt(Center(sel));
        var cb = (paletteAnchor ?? colorButton).Bounds;
        float x, y;
        if (paletteAnchor == textColorButton)
        {
            var tb = textBar.Bounds;
            bool above = tb.Bottom <= textBar.Anchor.Top + 1;
            x = cb.Left + cb.Width / 2 - w / 2;
            y = above ? tb.Top - 12 * S - h : tb.Bottom + 12 * S;
            if (y < mon.Top + 4) y = tb.Bottom + 12 * S;
            if (y + h > mon.Bottom - 4) y = tb.Top - 12 * S - h;
            x = Math.Clamp(x, mon.Left + 4, Math.Max(mon.Left + 4, mon.Right - w - 4));
        }
        else
        {
            var vb = tools.Bounds;
            bool outsideRight = vb.Left >= sel.Right - 1;
            x = outsideRight ? vb.Right + 12 * S : vb.Left - 12 * S - w;
            if (x + w > mon.Right - 4) x = vb.Left - 12 * S - w;
            if (x < mon.Left + 4) x = vb.Right + 12 * S;
            y = Math.Clamp(cb.Top + cb.Height / 2 - h / 2, mon.Top + 8 * S, Math.Max(mon.Top + 8 * S, mon.Bottom - h - 8 * S));
        }
        paletteRect = new RectangleF(MathF.Round(x), MathF.Round(y), MathF.Ceiling(w), MathF.Ceiling(h));
    }

    RectangleF PaletteCell(int i)
    {
        if (i < SizeIndexBase)
        {
            int row = i / PaletteCols, col = i % PaletteCols;
            return new RectangleF(paletteRect.X + PalPad + col * (PalCell + PalGap), paletteRect.Y + PalPad + row * (PalCell + PalGap), PalCell, PalCell);
        }
        int j = i - SizeIndexBase;
        float y = paletteRect.Y + PalPad + 2 * (PalCell + PalGap) + 12 * S;
        return new RectangleF(paletteRect.X + PalPad + j * (PalCell + PalGap), y, PalCell, PalCell);
    }

    int PaletteHit(Point p)
    {
        for (int i = 0; i <= CustomColorIndex; i++)
            if (RectangleF.Inflate(PaletteCell(i), PalGap / 2, PalGap / 2).Contains(p)) return i;
        for (int j = 0; j < 5; j++)
            if (RectangleF.Inflate(PaletteCell(SizeIndexBase + j), PalGap / 2, PalGap / 2).Contains(p)) return SizeIndexBase + j;
        return -1;
    }

    void DrawPalette(Graphics g)
    {
        var tone = glass.ToneAt(paletteRect, sel);
        glass.Draw(g, paletteRect, 22 * S, sel, tone, S);

        for (int i = 0; i <= CustomColorIndex; i++)
        {
            var r = PaletteCell(i);
            float grow = paletteHover == i ? 2 * S : 0;
            var c = RectangleF.Inflate(r, grow - 2 * S, grow - 2 * S);
            if (i < Palette.Length)
            {
                using (var fill = new SolidBrush(Palette[i])) g.FillEllipse(fill, c);
                using (var edge = new Pen(Color.FromArgb(60, 0, 0, 0), 1f)) g.DrawEllipse(edge, c);
                if (Palette[i].ToArgb() == color.ToArgb())
                {
                    using var ring = new Pen(tone.Glyph, 2f * S);
                    g.DrawEllipse(ring, RectangleF.Inflate(r, 2 * S, 2 * S));
                }
            }
            else
            {
                using (var rainbow = new LinearGradientBrush(RectangleF.Inflate(c, 1, 1), Color.Red, Color.Blue, 45f))
                {
                    rainbow.InterpolationColors = new ColorBlend
                    {
                        Colors = new[] { Color.FromArgb(255, 59, 48), Color.FromArgb(255, 204, 0), Color.FromArgb(52, 199, 89), Color.FromArgb(10, 132, 255), Color.FromArgb(175, 82, 222) },
                        Positions = new[] { 0f, 0.25f, 0.5f, 0.75f, 1f },
                    };
                    g.FillEllipse(rainbow, c);
                }
                Icons.Draw(g, RectangleF.Inflate(c, -c.Width * 0.22f, -c.Height * 0.22f), Color.White,
                    (gg, p, _) => { gg.DrawLine(p, 12, 5, 12, 19); gg.DrawLine(p, 5, 12, 19, 12); }, 3f);
                bool custom = Array.FindIndex(Palette, p => p.ToArgb() == color.ToArgb()) < 0;
                if (custom)
                {
                    using var ring = new Pen(tone.Glyph, 2f * S);
                    g.DrawEllipse(ring, RectangleF.Inflate(r, 2 * S, 2 * S));
                }
            }
        }

        float sepY = paletteRect.Y + PalPad + 2 * (PalCell + PalGap) + 6 * S - PalGap / 2;
        using (var sep = new Pen(Ui.WithAlpha(tone.Glyph, 40), 1f))
            g.DrawLine(sep, paletteRect.X + PalPad, sepY, paletteRect.Right - PalPad, sepY);

        var t = SizeTool;
        var presets = SizePresets(t);
        float current = sizes[t];
        for (int j = 0; j < presets.Length; j++)
        {
            var r = PaletteCell(SizeIndexBase + j);
            bool selected = Math.Abs(presets[j] - current) < 0.01f;
            if (selected || paletteHover == SizeIndexBase + j)
            {
                using var bg = new SolidBrush(selected ? Ui.WithAlpha(tone.Glyph, 46) : tone.Hover);
                g.FillEllipse(bg, r);
            }
            float d = 4 * S + j * 3.4f * S;
            using var dot = new SolidBrush(tone.Glyph);
            g.FillEllipse(dot, r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
        }
        string label = $"{current:0.#} px";
        var ls = Ui.Measure(label, smallFont);
        var last = PaletteCell(SizeIndexBase + 4);
        Ui.DrawText(g, label, smallFont, tone.Secondary, new PointF(paletteRect.Right - PalPad - ls.Width, last.Y + (last.Height - ls.Height) / 2));
    }

    void PaintColorButton(Graphics g, RectangleF r, GlassTone tone)
    {
        var c = RectangleF.Inflate(r, -1.5f * S, -1.5f * S);
        using (var fill = new SolidBrush(color)) g.FillEllipse(fill, c);
        using (var ring = new Pen(Color.White, 2f * S)) g.DrawEllipse(ring, c);
        using (var edge = new Pen(Color.FromArgb(80, 0, 0, 0), 1f)) g.DrawEllipse(edge, RectangleF.Inflate(c, 1.5f * S, 1.5f * S));
    }
}
