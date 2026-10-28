namespace LiteShot.Overlay;

internal sealed partial class CaptureOverlay
{
    // ───────────────────────────── mouse ─────────────────────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (closing) return;
        var p = e.Location;
        mouse = p;
        if (e.Button == MouseButtons.Right) { RightClick(); return; }
        if (e.Button != MouseButtons.Left) return;
        HideTooltip();

        if (aiMode != null)
        {
            if (AiOpen && aiRect.Contains(p)) { AiClick(p); return; }
            bool onAiButton = (aiMode == AiKind.Edit ? aiEditButton : aiVideoButton).Bounds.Contains(p);
            CloseAiPrompt();
            if (onAiButton) return;
        }

        if (paletteOpen)
        {
            if (paletteRect.Contains(p)) { PaletteClick(PaletteHit(p)); return; }
            bool onAnchor = paletteAnchor != null && paletteAnchor.Bounds.Contains(p);
            ClosePalette();
            if (onAnchor) return;
        }

        if (HitButton(p) is { } button)
        {
            if (!button.Enabled) return;
            pressedButton = button;
            InvalidateGroupOf(button);
            Capture = true;
            return;
        }
        if (OnChrome(p)) return;

        if (editing != null)
        {
            if (OnEditorFrame(editing, p))
            {
                // Grab the frame around the text to move it while editing.
                dragStart = lastMouse = p;
                dragMoved = false;
                movingShape = editing;
                dragSnapshot = null;
                drag = DragMode.MoveShape;
                return;
            }
            if (EditorBox(editing).Contains(p))
            {
                caret = editing.IndexAt(p);
                caretOn = true;
                InvalidateRect(EditorBox(editing), 2);
                return;
            }
            CommitText();
            if (tool == Tool.Text) return;
        }

        dragStart = lastMouse = p;
        dragMoved = false;
        if (hasSel)
        {
            int h = HitHandle(p);
            if (h >= 0)
            {
                drag = DragMode.Resize;
                resizeHandle = h;
                selAtStart = sel;
                return;
            }
            if (tool != Tool.None) { BeginDraw(p); return; }
            var hit = HitShape(p);
            if (hit != null)
            {
                SelectShape(hit);
                movingShape = hit;
                drag = DragMode.MoveShape;
                dragSnapshot = Snapshot();
                UpdateView(true);
                return;
            }
            SelectShape(null);
            if (sel.Contains(p))
            {
                drag = DragMode.MoveSelection;
                selAtStart = sel;
                return;
            }
        }
        prevSel = sel;
        prevHasSel = hasSel;
        drag = DragMode.NewSelection;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (closing) return;
        var p = e.Location;
        if (p == mouse && drag == DragMode.None) return;
        mouse = p;
        mouseInside = true;
        switch (drag)
        {
            case DragMode.NewSelection:
                if (!dragMoved && Math.Abs(p.X - dragStart.X) < 4 && Math.Abs(p.Y - dragStart.Y) < 4) break;
                if (!dragMoved)
                {
                    dragMoved = true;
                    SelectShape(null);
                    hoverWindow = null;
                }
                hasSel = true;
                sel = RectFromPoints(dragStart, p, (ModifierKeys & Keys.Shift) != 0);
                UpdateScale();
                break;

            case DragMode.MoveSelection:
                if (!dragMoved && p == dragStart) break;
                dragMoved = true;
                sel = ClampInside(new Rectangle(selAtStart.X + p.X - dragStart.X, selAtStart.Y + p.Y - dragStart.Y, selAtStart.Width, selAtStart.Height));
                UpdateScale();
                break;

            case DragMode.Resize:
                dragMoved = true;
                sel = ResizeRect(selAtStart, resizeHandle, p.X - dragStart.X, p.Y - dragStart.Y);
                UpdateScale();
                break;

            case DragMode.Draw:
                UpdateDraw(p);
                break;

            case DragMode.MoveShape:
                if (movingShape != null && p != lastMouse)
                {
                    var before = movingShape == editing ? EditorBox(editing) : movingShape.Bounds;
                    movingShape.Move(p.X - lastMouse.X, p.Y - lastMouse.Y);
                    dragMoved = true;
                    InvalidateShape(before, movingShape == editing ? EditorBox(editing) : movingShape.Bounds);
                }
                break;

            default:
                if (pressedButton == null) UpdateHover(p);
                break;
        }
        lastMouse = p;
        UpdateView();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (closing || e.Button != MouseButtons.Left) return;
        Capture = false;
        if (pressedButton != null)
        {
            var b = pressedButton;
            pressedButton = null;
            InvalidateGroupOf(b);
            if (b.Bounds.Contains(e.Location) && b.Enabled) b.OnClick();
            if (!closing) UpdateHover(e.Location);
            return;
        }

        var mode = drag;
        drag = DragMode.None;
        switch (mode)
        {
            case DragMode.NewSelection:
                // A click without a drag captures the highlighted window.
                if (!dragMoved && !hasSel && hoverWindow is Rectangle hw)
                {
                    sel = hw;
                    hasSel = true;
                    hoverWindow = null;
                    UpdateScale();
                }
                break;
            case DragMode.Draw:
                FinishDraw();
                break;
            case DragMode.MoveShape:
                if (dragMoved && dragSnapshot != null)
                {
                    undoStack.Push(dragSnapshot);
                    redoStack.Clear();
                    InvalidateUndoButtons();
                }
                dragSnapshot = null;
                movingShape = null;
                break;
        }
        dragMoved = false;
        if (closing) return;
        UpdateHover(e.Location);
        UpdateView(frameDirty: true);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (closing || e.Button != MouseButtons.Left || !hasSel) return;
        if (OnChrome(e.Location)) return;
        var hit = HitShape(e.Location);
        if (hit is TextShape text && (tool == Tool.None || tool == Tool.Text))
        {
            drag = DragMode.None;
            dragSnapshot = null;
            BeginEdit(text, isNew: false);
            caret = text.IndexAt(e.Location);
            return;
        }
        // Double-click inside the selection: copy and close.
        if (tool == Tool.None && hit == null && sel.Contains(e.Location)) DoCopy();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        mouseInside = false;
        UpdateView();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (closing || !hasSel) return;
        int dir = Math.Sign(e.Delta);
        if (dir == 0) return;

        Tool t;
        Annotation? target = null;
        if (editing != null) { t = Tool.Text; target = editing; }
        else if (selectedShape != null && tool == Tool.None) { t = ToolOf(selectedShape); target = selectedShape; }
        else if (tool != Tool.None) t = tool;
        else return;

        var (min, max, step) = SizeRange(t);
        float value = Math.Clamp(sizes[t] + dir * step, min, max);
        if (target != null)
        {
            value = Math.Clamp(MathF.Round(target.Size / S / step) * step + dir * step, min, max);
            ApplySize(target, value);
        }
        sizes[t] = value;
        ShowFlash($"{SizeName(t)}  {value:0.#} px");
        if (paletteOpen) InvalidateRect(paletteRect, Px(4));
    }

    void ApplySize(Annotation target, float logical)
    {
        var before = target == editing ? EditorBox(editing) : target.Bounds;
        if (target != editing) PushUndoCoalesced(target);
        target.Size = logical * S;
        var after = target == editing ? EditorBox(editing) : target.Bounds;
        InvalidateRect(RectangleF.Union(before, after), Px(8));
    }

    void RightClick()
    {
        if (aiMode != null) { CloseAiPrompt(); return; }
        if (paletteOpen) { ClosePalette(); return; }
        if (editing != null) { CommitText(); return; }
        if (drag != DragMode.None) { CancelDrag(); return; }
        if (tool != Tool.None) { SetTool(Tool.None); return; }
        if (selectedShape != null) { SelectShape(null); return; }
        if (hasSel) { ClearSelection(); return; }
        CloseOverlay();
    }

    void CancelDrag()
    {
        switch (drag)
        {
            case DragMode.Draw:
                if (drawing != null)
                {
                    var b = drawing.Bounds;
                    drawing = null;
                    InvalidateRect(b, Px(8));
                }
                break;
            case DragMode.MoveShape:
                if (dragMoved && dragSnapshot != null) Restore(dragSnapshot);
                movingShape = null;
                break;
            case DragMode.MoveSelection:
            case DragMode.Resize:
                sel = selAtStart;
                break;
            case DragMode.NewSelection:
                sel = prevSel;
                hasSel = prevHasSel;
                break;
        }
        drag = DragMode.None;
        dragMoved = false;
        dragSnapshot = null;
        Capture = false;
        UpdateScale();
        UpdateView(true);
    }

    void ClearSelection()
    {
        InvalidateRect(lastFrame);
        hasSel = false;
        sel = Rectangle.Empty;
        SelectShape(null);
        ClosePalette();
        UpdateScale();
        UpdateHoverWindow();
        UpdateView(true);
    }

    void UpdateHover(Point p)
    {
        if (AiOpen && aiRect.Contains(p))
        {
            SetHoverButton(null);
            int hit = AiHit(p);
            if (hit != aiHover) { aiHover = hit; InvalidateRect(aiRect, 2); }
            SetCursor(hit >= 0 ? Cursors.Hand : Cursors.Default);
            return;
        }
        if (aiHover != -1) { aiHover = -1; InvalidateRect(aiRect, 2); }
        var hb = HitButton(p);
        SetHoverButton(hb);
        if (paletteOpen && paletteRect.Contains(p))
        {
            SetPaletteHover(PaletteHit(p));
            SetCursor(Cursors.Default);
            return;
        }
        SetPaletteHover(-1);
        if (hb != null || OnChrome(p))
        {
            SetCursor(Cursors.Default);
            return;
        }
        if (!hasSel)
        {
            var old = hoverWindow;
            UpdateHoverWindow();
            if (old != hoverWindow) UpdateView(true);
            SetCursor(Cursors.Cross);
            return;
        }
        int h = HitHandle(p);
        if (h >= 0) { SetCursor(HandleCursor(h)); return; }
        if (editing != null && OnEditorFrame(editing, p)) { SetCursor(Cursors.SizeAll); return; }
        if (editing != null && EditorBox(editing).Contains(p)) { SetCursor(Cursors.IBeam); return; }
        if (tool == Tool.Text) { SetCursor(Cursors.IBeam); return; }
        if (tool != Tool.None) { SetCursor(Cursors.Cross); return; }
        if (HitShape(p) != null) { SetCursor(Cursors.Hand); return; }
        SetCursor(sel.Contains(p) ? Cursors.SizeAll : Cursors.Cross);
    }

    void SetCursor(Cursor c)
    {
        if (Cursor != c) Cursor = c;
    }

    void SetHoverButton(ToolButton? b)
    {
        if (b == hoverButton) return;
        var old = hoverButton;
        hoverButton = b;
        if (old != null) InvalidateGroupOf(old);
        if (b != null) InvalidateGroupOf(b);
        tooltipVisible = false;
        tooltipTimer.Stop();
        if (b != null) tooltipTimer.Start();
        hoverAnim = SystemTheme.AnimationsEnabled && b != null ? 0f : 1f;
        if (hoverAnim < 1f) animTimer.Start();
        UpdateView();
    }

    void AnimateHover()
    {
        hoverAnim = Math.Min(1f, hoverAnim + 0.16f);
        if (hoverButton != null) InvalidateGroupOf(hoverButton);
        if (hoverAnim >= 1f) animTimer.Stop();
    }

    void HideTooltip()
    {
        tooltipTimer.Stop();
        if (!tooltipVisible) return;
        tooltipVisible = false;
        UpdateView();
    }

    void SetPaletteHover(int i)
    {
        if (i == paletteHover) return;
        paletteHover = i;
        if (paletteOpen) InvalidateRect(paletteRect, 2);
    }

    Annotation? HitShape(Point p)
    {
        if (!sel.Contains(p)) return null;
        float tol = 5 * S;
        for (int i = shapes.Count - 1; i >= 0; i--)
            if (shapes[i].Hit(p, tol)) return shapes[i];
        return null;
    }

    void SelectShape(Annotation? shape)
    {
        if (shape == selectedShape) return;
        if (selectedShape != null) InvalidateRect(selectedShape.Bounds, Px(8));
        selectedShape = shape;
        if (shape != null) InvalidateRect(shape.Bounds, Px(8));
    }

    // ───────────────────────────── keyboard ─────────────────────────────

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (closing) return true;
        if (aiMode != null)
        {
            if (HandleAiKey(keyData)) return true;
            if (aiBox is { Focused: true }) return false;   // typing goes to the prompt box
        }
        if (TextTarget != null && HandleTextStyleKey(keyData)) return true;
        if (editing != null)
        {
            if (HandleEditKey(keyData)) return true;
            // Plain typing has to reach OnKeyPress, so only modified combinations fall through to shortcuts.
            if ((keyData & (Keys.Control | Keys.Alt)) == 0) return false;
        }
        if (HandleShortcut(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (editing != null && !char.IsControl(e.KeyChar))
        {
            InsertText(e.KeyChar.ToString());
            e.Handled = true;
        }
    }

    bool HandleEditKey(Keys k)
    {
        var t = editing!;
        string text = t.Text;
        switch (k)
        {
            case Keys.Escape:
            case Keys.Control | Keys.Enter:
                CommitText();
                return true;
            case Keys.Enter:
            case Keys.Shift | Keys.Enter:
                InsertText("\n");
                return true;
            case Keys.Back:
                if (caret > 0) EditText(text.Remove(caret - 1, 1), caret - 1);
                return true;
            case Keys.Control | Keys.Back:
                int start = caret;
                while (start > 0 && char.IsWhiteSpace(text[start - 1])) start--;
                while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
                if (start < caret) EditText(text.Remove(start, caret - start), start);
                return true;
            case Keys.Delete:
                if (caret < text.Length) EditText(text.Remove(caret, 1), caret);
                return true;
            case Keys.Left:
                MoveCaret(Math.Max(0, caret - 1));
                return true;
            case Keys.Right:
                MoveCaret(Math.Min(text.Length, caret + 1));
                return true;
            case Keys.Home:
                MoveCaret(caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1);
                return true;
            case Keys.End:
                int end = text.IndexOf('\n', caret);
                MoveCaret(end < 0 ? text.Length : end);
                return true;
            case Keys.Up:
            case Keys.Down:
                var cp = t.CaretPoint(caret);
                float dy = k == Keys.Up ? -t.LineHeight : t.LineHeight;
                MoveCaret(t.IndexAt(new PointF(cp.X, cp.Y + t.LineHeight / 2 + dy)));
                return true;
            case Keys.Control | Keys.V:
                if (Clipboard.ContainsText())
                    InsertText(Clipboard.GetText().Replace("\r\n", "\n").Replace('\r', '\n'));
                return true;
        }
        return false;
    }

    void InsertText(string s) => EditText(editing!.Text.Insert(caret, s), caret + s.Length);

    void EditText(string text, int newCaret)
    {
        var before = EditorBox(editing!);
        editing!.Text = text;
        caret = newCaret;
        RestartCaret();
        InvalidateRect(RectangleF.Union(before, EditorBox(editing)), 3);
    }

    void MoveCaret(int index)
    {
        InvalidateCaret();
        caret = index;
        RestartCaret();
        InvalidateCaret();
    }

    void RestartCaret()
    {
        caretOn = true;
        caretTimer.Stop();
        caretTimer.Start();
    }

    bool HandleShortcut(Keys k)
    {
        switch (k)
        {
            case Keys.Escape:
                if (paletteOpen) ClosePalette();
                else if (drag != DragMode.None) CancelDrag();
                else CloseOverlay();
                return true;
            case Keys.Enter:
            case Keys.Control | Keys.C:
                if (hasSel) DoCopy();
                return true;
            case Keys.Control | Keys.S:
                if (hasSel) DoSave();
                return true;
            case Keys.Control | Keys.D:
                if (hasSel) DoUpload(UploadKind.Link, null);
                return true;
            case Keys.Control | Keys.K:
                if (hasSel) DoUpload(UploadKind.Search, null);
                return true;
            case Keys.Control | Keys.P:
                if (hasSel) DoPrint();
                return true;
            case Keys.Control | Keys.T:
                if (hasSel) DoPin();
                return true;
            case Keys.Control | Keys.Shift | Keys.C:
                if (hasSel) DoOcr();
                return true;
            case Keys.Control | Keys.Z:
                Undo();
                return true;
            case Keys.Control | Keys.Y:
            case Keys.Control | Keys.Shift | Keys.Z:
                Redo();
                return true;
            case Keys.Control | Keys.A:
                SelectAll();
                return true;
            case Keys.Delete:
            case Keys.Back:
                DeleteSelectedShape();
                return true;
        }

        var code = k & Keys.KeyCode;
        var mods = k & Keys.Modifiers;
        if (code is Keys.Left or Keys.Right or Keys.Up or Keys.Down)
        {
            Nudge(code, mods);
            return true;
        }
        if (mods == Keys.None)
        {
            if (code == Keys.C && !hasSel) { CopyColor(); return true; }
            if (!hasSel) return false;
            if (code == Keys.V) { SetTool(Tool.None); return true; }
            if (code == Keys.F) { BlurFaces(); return true; }
            Tool? t = code switch
            {
                Keys.P => Tool.Pen,
                Keys.L => Tool.Line,
                Keys.A => Tool.Arrow,
                Keys.R => Tool.Rect,
                Keys.E => Tool.Ellipse,
                Keys.M => Tool.Marker,
                Keys.T => Tool.Text,
                Keys.N => Tool.Counter,
                Keys.B => Tool.Pixelate,
                _ => null,
            };
            if (t is Tool tt) { SetTool(tt); return true; }
        }
        return false;
    }

    void Nudge(Keys dir, Keys mods)
    {
        int step = (mods & Keys.Control) != 0 ? 10 : 1;
        int dx = dir == Keys.Left ? -step : dir == Keys.Right ? step : 0;
        int dy = dir == Keys.Up ? -step : dir == Keys.Down ? step : 0;
        bool resize = (mods & Keys.Shift) != 0;

        if (selectedShape != null && !resize)
        {
            PushUndoCoalesced(selectedShape);
            var before = selectedShape.Bounds;
            selectedShape.Move(dx, dy);
            InvalidateShape(before, selectedShape.Bounds);
            return;
        }
        if (!hasSel)
        {
            var c = Cursor.Position;
            Cursor.Position = new Point(c.X + dx, c.Y + dy);
            return;
        }
        sel = resize
            ? Rectangle.Intersect(new Rectangle(sel.X, sel.Y, Math.Max(1, sel.Width + dx), Math.Max(1, sel.Height + dy)), client)
            : ClampInside(new Rectangle(sel.X + dx, sel.Y + dy, sel.Width, sel.Height));
        UpdateScale();
        UpdateView();
    }

    void SelectAll()
    {
        CommitText();
        var mon = MonitorAt(hasSel ? Center(sel) : mouse);
        sel = hasSel && sel == mon ? client : mon;
        hasSel = true;
        hoverWindow = null;
        UpdateScale();
        UpdateView(true);
    }
}
