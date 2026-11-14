using System.Drawing.Drawing2D;

namespace LiteShot.Overlay;

/// <summary>
/// Find on screen (Ctrl+F): Windows' OCR reads the selection once, then every place the typed text appears lights up
/// as you type, with the rest of the selection stepping back. Enter steps through the matches; Highlight marks them
/// all with the highlighter so they stay in the screenshot.
/// </summary>
internal sealed partial class CaptureOverlay
{
    const int FindPrev = 0, FindNext = 1, FindMark = 2;
    static readonly Color FindYellow = Color.FromArgb(255, 214, 10), FindOrange = Color.FromArgb(255, 149, 0);

    ToolButton findButton = null!;
    bool findOpen;
    TextBox? findBox;
    Font? findFont;
    RectangleF findRect, findFieldRect;
    readonly RectangleF[] findButtons = new RectangleF[3];
    int findHover = -1;
    Task<List<List<OcrWord>>>? findScan;
    Rectangle findScanArea;
    List<RectangleF> findHits = new();
    int findIndex;
    string findLayoutKey = "";

    bool FindVisible => findOpen && ChromeVisible;

    void ToggleFind()
    {
        if (findOpen) CloseFind();
        else OpenFind();
    }

    void OpenFind()
    {
        if (closing) return;
        if (findOpen) { findBox?.Focus(); findBox?.SelectAll(); return; }
        // With nothing selected yet, look through the whole screen under the pointer.
        if (!hasSel) SelectAll();
        CommitText();
        ClosePalette();
        CloseAiPrompt();
        HideTooltip();
        findOpen = true;
        findIndex = 0;
        findBox ??= CreateFindBox();
        findBox.Visible = true;
        findLayoutKey = "";
        StartFindScan();
        LayoutFind();
        InvalidateRect(findButton.Bounds, 3);
        UpdateView(true);
        findBox.Focus();
        findBox.SelectAll();
    }

    TextBox CreateFindBox()
    {
        var box = new TextBox { BorderStyle = BorderStyle.None, Visible = false };
        Controls.Add(box);
        Native.SetCueBanner(box, "Find on screen");
        box.TextChanged += (_, _) =>
        {
            findIndex = 0;
            UpdateFindHits();
        };
        return box;
    }

    void CloseFind()
    {
        if (!findOpen) return;
        findOpen = false;
        findHover = -1;
        if (findBox != null) findBox.Visible = false;
        findHits = new List<RectangleF>();
        InvalidateRect(findRect, Px(18));
        InvalidateRect(findButton.Bounds, 3);
        if (hasSel) InvalidateRect(Rectangle.Inflate(sel, 8, 8));
        Focus();
        UpdateView(true);
    }

    /// <summary>Reads the selection in the background, unless what was read already covers it.</summary>
    void StartFindScan()
    {
        if (findScan != null && findScanArea.Contains(sel)) return;
        var area = Rectangle.Intersect(sel, new Rectangle(Point.Empty, shot.Size));
        if (area.Width < 4 || area.Height < 4) return;
        findScanArea = area;
        // The UI thread keeps painting from the capture, so the scan gets its own copy.
        var crop = shot.Clone(area, shot.PixelFormat);
        var scan = Task.Run(async () =>
        {
            using (crop) return await OcrService.RecognizeWordsAsync(crop).ConfigureAwait(false);
        });
        findScan = scan;
        scan.ContinueWith(_ =>
        {
            try { if (!closing && IsHandleCreated) BeginInvoke(new Action(() => { if (findScan == scan) UpdateFindHits(); })); }
            catch (InvalidOperationException) { /* the overlay closed meanwhile */ }
        }, TaskScheduler.Default);
        UpdateFindHits();
    }

    /// <summary>Matches the typed text against what was read, keeping the ones inside the selection.</summary>
    void UpdateFindHits()
    {
        if (!findOpen) return;
        var hits = new List<RectangleF>();
        string query = findBox?.Text ?? "";
        if (findScan is { IsCompletedSuccessfully: true } scan && query.Trim().Length > 0)
        {
            var area = findScanArea;
            hits = TextFinder.Find(scan.Result, query)
                .Select(h => new RectangleF(h.X + area.X, h.Y + area.Y, h.Width, h.Height))
                .Where(h => sel.Contains(Point.Round(Center(h))))
                .ToList();
        }
        findHits = hits;
        findIndex = hits.Count == 0 ? 0 : Math.Clamp(findIndex, 0, hits.Count - 1);
        RevealFindHit();
        if (hasSel) InvalidateRect(Rectangle.Inflate(sel, 8, 8));
        InvalidateRect(findRect, 2);
        UpdateView(true);
    }

    string FindStatus
    {
        get
        {
            if (findScan == null) return "";
            if (!findScan.IsCompleted) return "Reading…";
            if (!findScan.IsCompletedSuccessfully)
                return findScan.Exception?.InnerException is InvalidOperationException ? "No OCR language" : "Couldn't read";
            if ((findBox?.Text.Trim().Length ?? 0) == 0) return "";
            return findHits.Count == 0 ? "No matches" : $"{findIndex + 1} of {findHits.Count}";
        }
    }

    void StepFind(int delta)
    {
        if (findHits.Count == 0) return;
        findIndex = ((findIndex + delta) % findHits.Count + findHits.Count) % findHits.Count;
        RevealFindHit();
        if (hasSel) InvalidateRect(Rectangle.Inflate(sel, 8, 8));
        InvalidateRect(findRect, 2);
        UpdateView(true);
    }

    /// <summary>On a page longer than the screen, scrolls the current match into view, a third of the way down.</summary>
    void RevealFindHit()
    {
        if (page == null || findHits.Count == 0) return;
        var h = findHits[findIndex];
        var mon = MonitorAt(Point.Round(Center(sel)));
        if (h.Top >= mon.Top + 60 * S && h.Bottom <= mon.Bottom - 140 * S) return;
        int screenTop = mon.Top - scrollY;
        ScrollTo((int)(h.Top - screenTop - mon.Height / 3f));
    }

    /// <summary>Highlight: a highlighter stroke over every match, as one undo step, and the bar closes.</summary>
    void MarkFindHits()
    {
        if (findHits.Count == 0) return;
        var marks = findHits.Select(h =>
        {
            float size = h.Height * 1.15f, cy = h.Top + h.Height / 2;
            var stroke = new StrokeShape { Highlighter = true, Color = FindYellow, Size = size };
            // Round caps reach half the stroke past each end, so the ends sit that far inside the match.
            float l = h.Left + size / 2, r = h.Right - size / 2;
            if (r <= l) stroke.Points.Add(new PointF(h.Left + h.Width / 2, cy));
            else { stroke.Points.Add(new PointF(l, cy)); stroke.Points.Add(new PointF(r, cy)); }
            return (Annotation)stroke;
        }).ToList();
        AddShapes(marks, underneath: false);
        CloseFind();
        ShowFlash(marks.Count == 1 ? "Highlighted 1 match" : $"Highlighted {marks.Count} matches");
    }

    int FindHitAt(Point p)
    {
        for (int i = 0; i < findHits.Count; i++)
            if (RectangleF.Inflate(findHits[i], 3 * S, 2 * S).Contains(p)) return i;
        return -1;
    }

    int FindButtonAt(Point p)
    {
        for (int i = 0; i < findButtons.Length; i++)
            if (findButtons[i].Contains(p)) return i;
        return -1;
    }

    void FindClick(Point p)
    {
        switch (FindButtonAt(p))
        {
            case FindPrev: StepFind(-1); break;
            case FindNext: StepFind(1); break;
            case FindMark: MarkFindHits(); break;
        }
        if (findOpen) findBox?.Focus();
    }

    bool HandleFindKey(Keys k)
    {
        bool typing = findBox is { Focused: true };
        switch (k)
        {
            case Keys.Escape: CloseFind(); return true;
            case Keys.Enter:
            case Keys.F3:
            case Keys.Down when typing:
                StepFind(1);
                return true;
            case Keys.Shift | Keys.Enter:
            case Keys.Shift | Keys.F3:
            case Keys.Up when typing:
                StepFind(-1);
                return true;
            case Keys.Control | Keys.Enter: MarkFindHits(); return true;
            case Keys.Control | Keys.F: OpenFind(); return true;
            case Keys.Control | Keys.A when typing: findBox!.SelectAll(); return true;
        }
        return false;
    }

    // ───────────────────────────── layout & drawing ─────────────────────────────

    void LayoutFind()
    {
        if (!findOpen) return;
        // The selection grew past what was read: read it again.
        if (findScan != null && !findScanArea.Contains(sel)) StartFindScan();
        string key = $"{sel}|{S}|{actions.Bounds}|{scrollY}";
        if (key == findLayoutKey) return;
        findLayoutKey = key;

        var mon = MonitorAt(Center(sel));
        float pad = 8 * S, h = 50 * S, btn = 34 * S, gap = 4 * S, iconArea = 34 * S;
        float fieldW = 220 * S + Ui.Measure("888 of 888", smallFont).Width + 16 * S;
        float markW = Ui.Measure("Highlight", smallFont).Width + 52 * S;
        float w = iconArea + fieldW + gap + btn * 2 + gap * 2 + markW + pad + 4 * S;

        var bar = actions.Bounds;
        bool below = bar.Top >= sel.Bottom - 1;
        float x = findButton.Bounds.Left + findButton.Bounds.Width / 2 - w / 2;
        float y = below ? bar.Bottom + 12 * S : bar.Top - 12 * S - h;
        if (y + h > mon.Bottom - 4) y = bar.Top - 12 * S - h;
        if (y < mon.Top + 4) y = bar.Bottom + 12 * S;
        x = Math.Clamp(x, mon.Left + 4, Math.Max(mon.Left + 4, mon.Right - w - 4));
        y = Math.Clamp(y, mon.Top + 4, Math.Max(mon.Top + 4, mon.Bottom - h - 4));
        findRect = new RectangleF(MathF.Round(x), MathF.Round(y), MathF.Ceiling(w), MathF.Ceiling(h));

        findFieldRect = new RectangleF(findRect.X + iconArea, findRect.Y + pad, fieldW, h - pad * 2);
        float cx = findFieldRect.Right + gap * 2, by = findRect.Y + (h - btn) / 2;
        findButtons[FindPrev] = new RectangleF(cx, by, btn, btn);
        cx += btn + gap;
        findButtons[FindNext] = new RectangleF(cx, by, btn, btn);
        cx += btn + gap;
        findButtons[FindMark] = new RectangleF(cx, by, markW, btn);

        if (findBox == null) return;
        // Never dispose findBox.Font itself: until set, it is WinForms' shared default font.
        var font = Ui.MakeFont((AppSettings.Current.BrightMode ? 15.5f : 14.5f) * S);
        findBox.Font = font;
        findFont?.Dispose();
        findFont = font;
        var tone = glass.ToneAt(findRect, sel);
        findBox.BackColor = FieldColor(tone);
        findBox.ForeColor = tone.Glyph;
        int bh = findBox.PreferredHeight;
        float statusW = Ui.Measure("888 of 888", smallFont).Width + 16 * S;
        findBox.SetBounds((int)(findFieldRect.X + 14 * S), (int)(findFieldRect.Y + (findFieldRect.Height - bh) / 2) - scrollY,
            (int)(findFieldRect.Width - 14 * S - statusW), bh);
    }

    /// <summary>The matches, over the capture: the rest of the selection steps back, the current match is ringed in orange.</summary>
    void DrawFindHits(Graphics g)
    {
        if (findHits.Count == 0 || !hasSel) return;
        bool bright = AppSettings.Current.BrightMode;
        var state = g.Save();
        g.SetClip(sel, CombineMode.Intersect);
        using (var dim = new Region(sel))
        {
            foreach (var h in findHits) dim.Exclude(RectangleF.Inflate(h, 3 * S, 2 * S));
            using var shade = new SolidBrush(Color.FromArgb(bright ? 125 : 85, 0, 0, 0));
            g.FillRegion(shade, dim);
        }
        for (int i = 0; i < findHits.Count; i++)
        {
            bool current = i == findIndex;
            var r = RectangleF.Inflate(findHits[i], 3 * S, 2 * S);
            using var path = Ui.RoundRect(r, 4 * S);
            using (var fill = new SolidBrush(Color.FromArgb(current ? 95 : 55, FindYellow))) g.FillPath(fill, path);
            using var pen = new Pen(current ? FindOrange : FindYellow, (current ? 3f : 1.6f) * S * (bright ? 1.4f : 1f));
            g.DrawPath(pen, path);
        }
        g.Restore(state);
    }

    void DrawFind(Graphics g)
    {
        var tone = glass.ToneAt(findRect, sel);
        glass.Draw(g, findRect, findRect.Height / 2, sel, tone, S);
        float icon = 19 * S;
        Icons.Draw(g, new RectangleF(findRect.X + 13 * S, findRect.Y + (findRect.Height - icon) / 2, icon, icon), tone.Glyph, Icons.FindText,
            tone.Bright ? 2.3f : 1.8f);

        bool focused = findBox is { Focused: true };
        using (var field = Ui.RoundRect(findFieldRect, findFieldRect.Height / 2))
        {
            using (var fill = new SolidBrush(FieldColor(tone))) g.FillPath(fill, field);
            using var edge = new Pen(focused ? (tone.Bright ? tone.On : SystemTheme.Accent) : Ui.WithAlpha(tone.Glyph, 50), focused ? 2 * S : 1f);
            g.DrawPath(edge, field);
        }
        string status = FindStatus;
        if (status.Length > 0)
        {
            var ss = Ui.Measure(status, smallFont);
            bool none = status is "No matches" or "Couldn't read" or "No OCR language";
            var ink = none ? (tone.Light ? Color.FromArgb(200, 40, 30) : Color.FromArgb(255, 120, 110)) : tone.Secondary;
            Ui.DrawText(g, status, smallFont, ink, new PointF(findFieldRect.Right - 14 * S - ss.Width, findFieldRect.Y + (findFieldRect.Height - ss.Height) / 2));
        }

        bool any = findHits.Count > 0;
        for (int i = 0; i < findButtons.Length; i++)
        {
            var r = findButtons[i];
            if (any && findHover == i)
            {
                using var hover = Ui.RoundRect(r, r.Height / 2);
                using var fill = new SolidBrush(tone.Hover);
                g.FillPath(fill, hover);
            }
            var ink = any ? tone.Glyph : tone.Disabled;
            if (i == FindMark)
            {
                using var pill = Ui.RoundRect(r, r.Height / 2);
                using (var edge = new Pen(Ui.WithAlpha(ink, tone.Bright ? 255 : 90), tone.Bright ? 2 * S : 1f)) g.DrawPath(edge, pill);
                float m = 16 * S, tw = Ui.Measure("Highlight", smallFont).Width, left = r.X + (r.Width - m - 6 * S - tw) / 2;
                Icons.Draw(g, new RectangleF(left, r.Y + (r.Height - m) / 2, m, m), ink, Icons.Marker);
                Ui.DrawTextCentered(g, "Highlight", smallFont, ink, new RectangleF(left + m + 6 * S, r.Y, tw, r.Height));
                continue;
            }
            float d = 18 * S;
            bool up = i == FindPrev;
            Icons.Draw(g, new RectangleF(r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d), ink,
                (gg, p, _) => gg.DrawLines(p, up ? new[] { new PointF(6.5f, 14.5f), new PointF(12, 9), new PointF(17.5f, 14.5f) }
                                                 : new[] { new PointF(6.5f, 9.5f), new PointF(12, 15), new PointF(17.5f, 9.5f) }),
                tone.Bright ? 2.4f : 2f);
        }
    }
}
