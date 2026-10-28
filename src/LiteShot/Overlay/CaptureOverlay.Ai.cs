using System.Drawing.Drawing2D;

namespace LiteShot.Overlay;

/// <summary>The "Edit with AI" / "Make a video" prompt popover.</summary>
internal sealed partial class CaptureOverlay
{
    const int AiGo = 100, AiDuration = 200;

    // Settings can't open over the capture screen, so without a key the popover only explains where to go.
    const string AiNoKeyMessage =
        "Add your SyncNode API key to use AI. Press Esc to leave the screenshot, then right-click the LiteShot icon " +
        "in the system tray and open Settings › Cloud & AI.";

    ToolButton aiEditButton = null!, aiVideoButton = null!;
    AiKind? aiMode;
    TextBox? aiBox;
    Font? aiFont;
    RectangleF aiRect, aiFieldRect, aiGoRect, aiMessageRect;
    readonly List<(RectangleF Rect, string Text)> aiChips = new();
    readonly List<(RectangleF Rect, int Seconds)> aiDurations = new();
    int aiHover = -1;

    bool AiOpen => aiMode != null && ChromeVisible;

    void OpenAiPrompt(AiKind kind)
    {
        if (aiMode == kind) { CloseAiPrompt(); return; }
        CommitText();
        ClosePalette();
        HideTooltip();
        aiMode = kind;
        aiHover = -1;
        aiLayoutKey = "";
        if (AiService.HasKey)
        {
            aiBox ??= CreateAiBox();
            aiBox.Text = "";
            Native.SetCueBanner(aiBox, kind == AiKind.Edit ? "Describe the change, e.g. “remove the background”" : "Describe the motion (optional)");
            aiBox.Visible = true;
        }
        else if (aiBox != null) aiBox.Visible = false;
        LayoutAiPrompt();
        UpdateView(true);
        aiBox?.Focus();
    }

    TextBox CreateAiBox()
    {
        var box = new TextBox { BorderStyle = BorderStyle.None, Visible = false };
        Controls.Add(box);
        return box;
    }

    void CloseAiPrompt()
    {
        if (aiMode == null) return;
        aiMode = null;
        if (aiBox != null) aiBox.Visible = false;
        InvalidateRect(aiRect, Px(18));
        Focus();
        UpdateView(true);
    }

    string aiLayoutKey = "";

    void LayoutAiPrompt()
    {
        if (aiMode is not AiKind kind) return;
        // Layout runs on every repaint; only redo it (and touch the text box) when something moved.
        string key = $"{sel}|{S}|{kind}|{AiService.HasKey}|{actions.Bounds}";
        if (key == aiLayoutKey) return;
        aiLayoutKey = key;
        var mon = MonitorAt(Center(sel));
        float pad = 16 * S, w = 500 * S, inner = w - pad * 2;
        float titleH = 22 * S, fieldH = 42 * S, chipH = 30 * S, gap = 8 * S, rowGap = 12 * S, goH = 36 * S;
        bool hasKey = AiService.HasKey;

        // Measure chips into rows first; the popover height depends on them.
        var chips = new List<(SizeF Size, string Text)>();
        if (hasKey)
            foreach (var text in AiService.Suggestions(kind).Take(6))
            {
                var ts = Ui.Measure(text, smallFont);
                chips.Add((new SizeF(Math.Min(inner, ts.Width + 26 * S), chipH), text));
            }
        int rows = 0;
        float rowW = inner + 1;
        foreach (var c in chips)
        {
            if (rowW + c.Size.Width > inner) { rows++; rowW = 0; }
            rowW += c.Size.Width + gap;
        }
        rows = Math.Min(rows, 2);
        float messageH = hasKey ? 0 : MathF.Ceiling(MeasureWrapped(AiNoKeyMessage, smallFont, inner).Height);
        float h = hasKey
            ? pad + titleH + rowGap + fieldH + rowGap + rows * chipH + Math.Max(0, rows - 1) * gap + (rows > 0 ? rowGap : 0) + goH + pad
            : pad + titleH + rowGap + messageH + pad;

        var anchorButton = kind == AiKind.Edit ? aiEditButton : aiVideoButton;
        var bar = actions.Bounds;
        bool below = bar.Top >= sel.Bottom - 1;
        float x = anchorButton.Bounds.Left + anchorButton.Bounds.Width / 2 - w / 2;
        float y = below ? bar.Bottom + 12 * S : bar.Top - 12 * S - h;
        if (y + h > mon.Bottom - 4) y = bar.Top - 12 * S - h;
        if (y < mon.Top + 4) y = bar.Bottom + 12 * S;
        x = Math.Clamp(x, mon.Left + 4, Math.Max(mon.Left + 4, mon.Right - w - 4));
        y = Math.Clamp(y, mon.Top + 4, Math.Max(mon.Top + 4, mon.Bottom - h - 4));
        aiRect = new RectangleF(MathF.Round(x), MathF.Round(y), MathF.Ceiling(w), MathF.Ceiling(h));

        float cy = aiRect.Y + pad + titleH + rowGap;
        aiChips.Clear();
        aiDurations.Clear();
        if (hasKey)
        {
            aiFieldRect = new RectangleF(aiRect.X + pad, cy, inner, fieldH);
            cy += fieldH + rowGap;
            float cx = aiRect.X + pad;
            int row = 0;
            foreach (var c in chips)
            {
                if (cx + c.Size.Width > aiRect.X + pad + inner + 0.5f)
                {
                    row++;
                    cx = aiRect.X + pad;
                }
                if (row >= rows) break;
                aiChips.Add((new RectangleF(cx, cy + row * (chipH + gap), c.Size.Width, chipH), c.Text));
                cx += c.Size.Width + gap;
            }
            if (rows > 0) cy += rows * chipH + (rows - 1) * gap + rowGap;
            if (kind == AiKind.Video && AiService.ModelFor(AiKind.Video).SupportsDuration)
            {
                float dx = aiRect.X + pad;
                foreach (int seconds in new[] { 5, 10 })
                {
                    float dw = Ui.Measure($"{seconds} s", smallFont).Width + 26 * S;
                    aiDurations.Add((new RectangleF(dx, cy + (goH - chipH) / 2, dw, chipH), seconds));
                    dx += dw + gap;
                }
            }
            if (aiBox != null)
            {
                // Never dispose aiBox.Font itself: until set, it is WinForms' shared default font.
                var font = Ui.MakeFont(15 * S);
                aiBox.Font = font;
                aiFont?.Dispose();
                aiFont = font;
                var tone = glass.ToneAt(aiRect, sel);
                aiBox.BackColor = FieldColor(tone);
                aiBox.ForeColor = tone.Glyph;
                int bh = aiBox.PreferredHeight;
                aiBox.SetBounds((int)(aiFieldRect.X + 14 * S), (int)(aiFieldRect.Y + (aiFieldRect.Height - bh) / 2), (int)(aiFieldRect.Width - 28 * S), bh);
            }
        }
        else
        {
            aiFieldRect = RectangleF.Empty;
            aiGoRect = RectangleF.Empty;
            aiMessageRect = new RectangleF(aiRect.X + pad, cy, inner, messageH);
            return;
        }
        string label = kind == AiKind.Edit ? "Generate" : "Make video";
        float gw = Ui.Measure(label, labelFont).Width + 40 * S;
        aiGoRect = new RectangleF(aiRect.Right - pad - gw, cy, gw, goH);
    }

    static SizeF MeasureWrapped(string text, Font font, float width)
    {
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        return g.MeasureString(text, font, (int)width);
    }

    static Color FieldColor(GlassTone tone) => SystemTheme.HighContrast ? SystemColors.Window
        : tone.Light ? Color.FromArgb(250, 250, 252) : Color.FromArgb(44, 44, 50);

    int AiHit(Point p)
    {
        if (AiService.HasKey && aiGoRect.Contains(p)) return AiGo;
        for (int i = 0; i < aiChips.Count; i++) if (aiChips[i].Rect.Contains(p)) return i;
        for (int i = 0; i < aiDurations.Count; i++) if (aiDurations[i].Rect.Contains(p)) return AiDuration + i;
        return -1;
    }

    void AiClick(Point p)
    {
        int hit = AiHit(p);
        if (hit == AiGo) SubmitAi();
        else if (hit >= AiDuration)
        {
            AppSettings.Current.AiVideoDuration = aiDurations[hit - AiDuration].Seconds;
            AppSettings.Current.Save();
            InvalidateRect(aiRect, 2);
        }
        else if (hit >= 0 && aiBox != null)
        {
            aiBox.Text = aiChips[hit].Text;
            aiBox.SelectionStart = aiBox.Text.Length;
            aiBox.Focus();
        }
        else aiBox?.Focus();
    }

    void SubmitAi()
    {
        if (aiMode is not AiKind kind || closing || !AiService.HasKey) return;
        string prompt = aiBox?.Text.Trim() ?? "";
        if (kind == AiKind.Edit && prompt.Length == 0)
        {
            ShowFlash("Describe the change you want first");
            aiBox?.Focus();
            return;
        }
        if (prompt.Length > 0) AiService.RememberPrompt(kind, prompt);
        if (prompt.Length == 0) prompt = AiService.DefaultVideoPrompt;
        var img = RenderResult();
        Finish();
        host.RunAi(img, kind, prompt);
    }

    bool HandleAiKey(Keys k)
    {
        switch (k)
        {
            case Keys.Escape: CloseAiPrompt(); return true;
            case Keys.Enter: SubmitAi(); return true;
            case Keys.Control | Keys.A when aiBox is { Focused: true }: aiBox.SelectAll(); return true;
        }
        return false;
    }

    void DrawAiPrompt(Graphics g)
    {
        if (aiMode is not AiKind kind) return;
        var tone = glass.ToneAt(aiRect, sel);
        glass.Draw(g, aiRect, 22 * S, sel, tone, S);
        float pad = 16 * S;

        float icon = 20 * S;
        Icons.Draw(g, new RectangleF(aiRect.X + pad, aiRect.Y + pad + 1 * S, icon, icon), tone.Glyph, kind == AiKind.Edit ? Icons.Sparkles : Icons.Video);
        string title = kind == AiKind.Edit ? "Edit with AI" : "Make a video";
        var ts = Ui.Measure(title, labelFont);
        Ui.DrawText(g, title, labelFont, tone.Glyph, new PointF(aiRect.X + pad + icon + 8 * S, aiRect.Y + pad + (22 * S - ts.Height) / 2));
        string model = $"{AiService.ModelFor(kind).Label} · SyncNode";
        var ms = Ui.Measure(model, smallFont);
        Ui.DrawText(g, model, smallFont, tone.Secondary, new PointF(aiRect.Right - pad - ms.Width, aiRect.Y + pad + (22 * S - ms.Height) / 2));

        if (!aiFieldRect.IsEmpty)
        {
            using var field = Ui.RoundRect(aiFieldRect, 12 * S);
            using (var fill = new SolidBrush(FieldColor(tone))) g.FillPath(fill, field);
            using (var edge = new Pen(aiBox is { Focused: true } ? SystemTheme.Accent : Ui.WithAlpha(tone.Glyph, 50), aiBox is { Focused: true } ? 2 * S : 1f))
                g.DrawPath(edge, field);
        }
        else
        {
            using var brush = new SolidBrush(tone.Glyph);
            g.DrawString(AiNoKeyMessage, smallFont, brush, RectangleF.Inflate(aiMessageRect, 0, 2));
            return;
        }

        for (int i = 0; i < aiChips.Count; i++)
        {
            var (r, text) = aiChips[i];
            using var path = Ui.RoundRect(r, r.Height / 2);
            using (var fill = new SolidBrush(aiHover == i ? Ui.WithAlpha(tone.Glyph, 40) : tone.Hover)) g.FillPath(fill, path);
            // Same typographic format the chip was measured with, so the text fits exactly.
            using (var brush = new SolidBrush(tone.Glyph))
            using (var format = (StringFormat)Ui.Typographic.Clone())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(text, smallFont, brush, RectangleF.Inflate(r, -8 * S, 0), format);
            }
        }

        int duration = AppSettings.Current.AiVideoDuration >= 10 ? 10 : 5;
        for (int i = 0; i < aiDurations.Count; i++)
        {
            var (r, seconds) = aiDurations[i];
            bool on = seconds == duration;
            using var path = Ui.RoundRect(r, r.Height / 2);
            using (var fill = new SolidBrush(on ? Ui.WithAlpha(tone.Glyph, 60) : aiHover == AiDuration + i ? Ui.WithAlpha(tone.Glyph, 30) : tone.Hover))
                g.FillPath(fill, path);
            Ui.DrawTextCentered(g, $"{seconds} s", smallFont, tone.Glyph, r);
        }
        if (aiDurations.Count == 0 && AiService.HasKey)
        {
            string hint = "Enter to start · Esc to cancel";
            var hs = Ui.Measure(hint, smallFont);
            Ui.DrawText(g, hint, smallFont, tone.Secondary, new PointF(aiRect.X + pad, aiGoRect.Y + (aiGoRect.Height - hs.Height) / 2));
        }

        string label = kind == AiKind.Edit ? "Generate" : "Make video";
        using (var go = Ui.RoundRect(aiGoRect, aiGoRect.Height / 2))
        {
            var accent = SystemTheme.HighContrast ? SystemColors.Highlight : SystemTheme.Accent;
            using var fill = new SolidBrush(aiHover == AiGo ? ControlPaint.Light(accent, 0.15f) : accent);
            g.FillPath(fill, go);
            if (!SystemTheme.HighContrast) GlassMaterial.DrawRim(g, aiGoRect, aiGoRect.Height / 2, S, 0.6f);
        }
        Ui.DrawTextCentered(g, label, labelFont, SystemTheme.HighContrast ? SystemColors.HighlightText : Color.White, aiGoRect);
    }
}
