namespace LiteShot.Overlay;

internal sealed partial class CaptureOverlay
{
    // ───────────────────────────── toolbars ─────────────────────────────

    Toolbar BuildToolsBar()
    {
        ToolButton T(Tool t, string tip, string key, Icons.Glyph glyph, string? label = null) => new()
        {
            Tip = tip,
            Label = label,
            Shortcut = key,
            Glyph = glyph,
            OnClick = () => SetTool(t),
            IsActive = () => tool == t,
        };

        colorButton = new ToolButton { Tip = "Color and size", Label = "Color", OnClick = () => TogglePalette(colorButton), CustomPaint = PaintColorButton };
        blurFacesButton = new ToolButton { Tip = $"Blur faces · {Core.ShieldBlur.Credit}", Shortcut = "F", Glyph = Icons.FaceBlur, OnClick = BlurFaces, IsActive = () => findingFaces };
        hidePersonalButton = new ToolButton { Tip = "Hide personal info", Label = "Hide info", Shortcut = "H", Glyph = Icons.HidePersonal, OnClick = ToggleHidePersonal, IsActive = () => HidingPersonal };
        return new Toolbar(vertical: true)
            .Group(
                T(Tool.Pen, "Pen", "P", Icons.Pen),
                T(Tool.Line, "Line", "L", Icons.Line),
                T(Tool.Arrow, "Arrow", "A", Icons.Arrow),
                T(Tool.Rect, "Rectangle", "R", Icons.Rect),
                T(Tool.Ellipse, "Ellipse", "E", Icons.Ellipse),
                T(Tool.Marker, "Marker", "M", Icons.Marker),
                T(Tool.Text, "Text", "T", Icons.Text),
                T(Tool.Counter, "Step number", "N", Icons.Counter, "Number"))
            .Group(
                hidePersonalButton,
                T(Tool.Pixelate, "Pixelate", "B", Icons.Pixelate),
                T(Tool.BlurBrush, "Blur brush · FacePrivacy.ai", "U", Icons.BlurBrush),
                blurFacesButton)
            .Group(colorButton)
            .Group(
                new ToolButton { Tip = "Undo", Shortcut = "Ctrl+Z", Glyph = Icons.Undo, OnClick = Undo, IsEnabled = () => undoStack.Count > 0 },
                new ToolButton { Tip = "Redo", Shortcut = "Ctrl+Y", Glyph = Icons.Redo, OnClick = Redo, IsEnabled = () => redoStack.Count > 0 });
    }

    Toolbar BuildActionsBar()
    {
        static ToolButton B(string tip, string? key, Icons.Glyph glyph, Action click, string? label = null) =>
            new() { Tip = tip, Label = label, Shortcut = key, Glyph = glyph, OnClick = click };

        shareButton = B("Share", null, Icons.Share, ShowShareMenu);
        scrollButton = B("Scrolling capture", "S", Icons.ScrollCapture, StartScrollCapture, "Scroll");
        return new Toolbar(vertical: false)
            .Group(scrollButton)
            .Group(
                B("Upload and copy link", "Ctrl+D", Icons.Upload, () => DoUpload(UploadKind.Link, null), "Upload"),
                shareButton,
                B("Search similar images", "Ctrl+K", Icons.Search, () => DoUpload(UploadKind.Search, null), "Search"),
                B("Print", "Ctrl+P", Icons.Print, DoPrint))
            .Group(
                aiEditButton = B("Edit with AI", "SyncNode", Icons.Sparkles, () => OpenAiPrompt(AiKind.Edit), "AI edit"),
                aiVideoButton = B("Make a video", "SyncNode", Icons.Video, () => OpenAiPrompt(AiKind.Video), "Video"),
                B("Ask AI about it", "Ctrl+Shift+A", Icons.AskAi, AskAi, "Ask AI"))
            .Group(
                B("Pin to screen", "Ctrl+T", Icons.Pin, DoPin, "Pin"),
                B("Copy text (OCR)", "Ctrl+Shift+C", Icons.Ocr, DoOcr, "Copy text"),
                findButton = new ToolButton { Tip = "Find on screen", Label = "Find", Shortcut = "Ctrl+F", Glyph = Icons.FindText, OnClick = ToggleFind, IsActive = () => findOpen })
            .Group(
                B("Copy", "Ctrl+C", Icons.Copy, DoCopy),
                B("Save", "Ctrl+S", Icons.Save, DoSave))
            .Group(B("Close", "Esc", Icons.Close, CloseOverlay));
    }

    void InvalidateUndoButtons()
    {
        if (tools.Groups.Count > 0) InvalidateRect(tools.Groups[^1].Bounds, 2);
    }

    void SetTool(Tool t)
    {
        if (tool == t) t = Tool.None;
        CommitText();
        SelectShape(null);
        tool = t;
        InvalidateRect(tools.Bounds, 2);
        if (paletteOpen) InvalidateRect(paletteRect, 2);
        UpdateHover(mouse);
    }

    // ───────────────────────────── color & size ─────────────────────────────

    void TogglePalette(ToolButton anchor)
    {
        if (paletteOpen && paletteAnchor == anchor) { ClosePalette(); return; }
        if (paletteOpen) ClosePalette();
        HideTooltip();
        paletteAnchor = anchor;
        paletteOpen = true;
        paletteHover = -1;
        LayoutPalette();
        UpdateView(true);
    }

    void ClosePalette()
    {
        if (!paletteOpen) return;
        paletteOpen = false;
        InvalidateRect(paletteRect, Px(18));
        UpdateView();
    }

    void PaletteClick(int index)
    {
        if (index < 0) return;
        if (index < Palette.Length)
        {
            SetColor(Palette[index]);
            ClosePalette();
        }
        else if (index == CustomColorIndex)
        {
            PickCustomColor();
        }
        else if (index >= SizeIndexBase)
        {
            var t = SizeTool;
            float value = SizePresets(t)[index - SizeIndexBase];
            sizes[t] = value;
            if (editing != null) ApplySize(editing, value);
            else if (selectedShape != null && tool == Tool.None) ApplySize(selectedShape, value);
            InvalidateRect(paletteRect, 2);
        }
    }

    void PickCustomColor()
    {
        ClosePalette();
        using var dialog = new ColorDialog { Color = color, FullOpen = true, AnyColor = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) SetColor(dialog.Color);
        Invalidate();
    }

    void SetColor(Color c)
    {
        color = c;
        if (editing != null)
        {
            editing.Color = c;
            InvalidateRect(EditorBox(editing), 2);
        }
        else if (selectedShape != null)
        {
            PushUndo();
            selectedShape.Color = c;
            InvalidateRect(selectedShape.Bounds, Px(8));
        }
        InvalidateRect(colorButton.Bounds, 3);
    }

    // ───────────────────────────── drawing ─────────────────────────────

    float LiveSize(Tool t) => sizes[t] * S;

    void BeginDraw(Point p)
    {
        SelectShape(null);
        if (tool == Tool.Text)
        {
            StartText(p);
            return;
        }
        straightStroke = false;
        drawing = tool switch
        {
            Tool.Pen => new StrokeShape { Points = { p } },
            Tool.Marker => new StrokeShape { Points = { p }, Highlighter = true },
            Tool.Line => new LineShape { A = p, B = p },
            Tool.Arrow => new ArrowShape { A = p, B = p },
            Tool.Rect => new RectShape { A = p, B = p },
            Tool.Ellipse => new EllipseShape { A = p, B = p },
            Tool.Pixelate => new PixelateShape { A = p, B = p },
            Tool.BlurBrush => new BlurBrushShape { Points = { p }, Strength = Math.Max(6, (int)MathF.Round(12 * S)) },
            Tool.Counter => new CounterShape { Center = p, Number = NextNumber() },
            _ => null,
        };
        if (drawing == null) return;
        drawing.Color = color;
        drawing.Size = LiveSize(tool);
        drag = DragMode.Draw;
        InvalidateRect(drawing.Bounds, Px(8));
    }

    int NextNumber() => shapes.OfType<CounterShape>().Select(c => c.Number).DefaultIfEmpty(0).Max() + 1;

    void UpdateDraw(Point p)
    {
        if (drawing == null) return;
        bool shift = (ModifierKeys & Keys.Shift) != 0;
        RectangleF before, after;
        switch (drawing)
        {
            case StrokeShape s:
                before = s.TailBounds(4);
                if (shift)
                {
                    // Shift draws a straight segment from the last committed point.
                    if (!straightStroke) { s.Points.Add(p); straightStroke = true; }
                    else s.Points[^1] = p;
                }
                else
                {
                    straightStroke = false;
                    var last = s.Points[^1];
                    if (Math.Abs(last.X - p.X) + Math.Abs(last.Y - p.Y) >= 2) s.Points.Add(p);
                }
                after = s.TailBounds(4);
                break;
            case BlurBrushShape brush:
                before = brush.TailBounds(3);
                var tip = brush.Points[^1];
                if (Math.Abs(tip.X - p.X) + Math.Abs(tip.Y - p.Y) >= 2) brush.Points.Add(p);
                after = brush.TailBounds(3);
                break;
            case CounterShape c:
                before = c.Bounds;
                c.Center = p;
                after = c.Bounds;
                break;
            case TwoPointShape t:
                before = t.Bounds;
                t.B = shift ? Constrain(t, p) : p;
                after = t.Bounds;
                break;
            default:
                return;
        }
        dragMoved = true;
        InvalidateShape(before, after);
    }

    static PointF Constrain(TwoPointShape t, Point p)
    {
        float dx = p.X - t.A.X, dy = p.Y - t.A.Y;
        if (t is LineShape or ArrowShape)
        {
            // Snap to 45° steps.
            double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
            float len = MathF.Sqrt(dx * dx + dy * dy);
            return new PointF(t.A.X + (float)Math.Cos(angle) * len, t.A.Y + (float)Math.Sin(angle) * len);
        }
        float side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new PointF(t.A.X + Math.Sign(dx == 0 ? 1 : dx) * side, t.A.Y + Math.Sign(dy == 0 ? 1 : dy) * side);
    }

    void FinishDraw()
    {
        var shape = drawing;
        drawing = null;
        straightStroke = false;
        if (shape == null) return;
        if (shape.IsDegenerate)
        {
            InvalidateRect(shape.Bounds, Px(8));
            return;
        }
        PushUndo();
        shapes.Add(shape);
        InvalidateRect(shape.Bounds, Px(8));
    }

    // ───────────────────────────── text ─────────────────────────────

    void StartText(Point p)
    {
        var existing = shapes.OfType<TextShape>().LastOrDefault(t => t.Hit(p, 2));
        if (existing != null)
        {
            BeginEdit(existing, isNew: false);
            caret = existing.IndexAt(p);
            return;
        }
        var text = NewTextShape();
        text.Pos = new PointF(p.X, p.Y - text.LineHeight / 2);
        BeginEdit(text, isNew: true);
    }

    static string Signature(TextShape t) =>
        $"{t.Text}\u0001{t.Color.ToArgb()}\u0001{t.Size}\u0001{t.Pos}\u0001{t.FamilyName}\u0001{t.Bold}{t.Italic}{t.Align}{t.Look}";

    void BeginEdit(TextShape t, bool isNew)
    {
        CommitText();
        SelectShape(null);
        editSnapshot = Snapshot();
        editIsNew = isNew;
        editSignature = Signature(t);
        if (isNew) shapes.Add(t);
        editing = t;
        caret = t.Text.Length;
        RememberStyle(t);
        RestartCaret();
        InvalidateRect(EditorBox(t), 3);
        UpdateHover(mouse);
        UpdateView(true);
    }

    void CommitText()
    {
        if (editing == null) return;
        var t = editing;
        editing = null;
        caretTimer.Stop();
        bool empty = string.IsNullOrWhiteSpace(t.Text);
        if (empty) shapes.Remove(t);
        bool changed = editIsNew ? !empty : empty || Signature(t) != editSignature;
        if (changed && editSnapshot != null)
        {
            undoStack.Push(editSnapshot);
            redoStack.Clear();
            InvalidateUndoButtons();
        }
        editSnapshot = null;
        InvalidateRect(EditorBox(t), 4);
        UpdateView(true);
    }

    // ───────────────────────────── undo ─────────────────────────────

    List<Annotation> Snapshot() => shapes.Select(s => s.Clone()).ToList();

    void PushUndo()
    {
        undoStack.Push(Snapshot());
        redoStack.Clear();
        coalesceTarget = null;
        InvalidateUndoButtons();
    }

    /// <summary>Wheel and arrow-key edits on the same shape within a second share one undo step.</summary>
    void PushUndoCoalesced(object target)
    {
        if (target == coalesceTarget && (DateTime.UtcNow - lastCoalescedUndo).TotalSeconds < 1)
        {
            lastCoalescedUndo = DateTime.UtcNow;
            return;
        }
        PushUndo();
        coalesceTarget = target;
        lastCoalescedUndo = DateTime.UtcNow;
    }

    void Restore(List<Annotation> snapshot)
    {
        shapes.Clear();
        shapes.AddRange(snapshot);
        selectedShape = null;
        coalesceTarget = null;
        InvalidateRect(Rectangle.Inflate(sel, 4, 4));
        InvalidateUndoButtons();
    }

    void Undo()
    {
        CommitText();
        if (undoStack.Count == 0) return;
        redoStack.Push(Snapshot());
        Restore(undoStack.Pop());
    }

    void Redo()
    {
        CommitText();
        if (redoStack.Count == 0) return;
        undoStack.Push(Snapshot());
        Restore(redoStack.Pop());
    }

    void DeleteSelectedShape()
    {
        if (selectedShape == null) return;
        var shape = selectedShape;
        PushUndo();
        shapes.Remove(shape);
        selectedShape = null;
        InvalidateRect(shape.Bounds, Px(8));
    }
}
