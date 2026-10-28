namespace LiteShot.Overlay;

/// <summary>Privacy: names blurred automatically (Settings › Capture) and faces blurred with one click.</summary>
internal sealed partial class CaptureOverlay
{
    Task<List<Rectangle>>? nameScan;
    bool namesApplied, findingFaces;

    /// <summary>Starts reading the screen for names in the background as soon as the overlay is up.</summary>
    void StartNameScan()
    {
        if (!AppSettings.Current.AutoBlurNames || nameScan != null) return;
        // The UI thread keeps painting from the capture, so the scan gets its own copy.
        var copy = shot.Clone(new Rectangle(Point.Empty, shot.Size), shot.PixelFormat);
        nameScan = Task.Run(async () =>
        {
            using (copy) return await NameFinder.FindAsync(copy).ConfigureAwait(false);
        });
        nameScan.ContinueWith(_ =>
        {
            try { if (!closing && IsHandleCreated) BeginInvoke(new Action(() => ApplyNameBlurs(wait: false))); }
            catch (InvalidOperationException) { /* the overlay closed meanwhile */ }
        }, TaskScheduler.Default);
    }

    /// <summary>Adds the name blurs once the scan is done. Output paths pass <paramref name="wait"/> so a quick Ctrl+C never leaks a name.</summary>
    void ApplyNameBlurs(bool wait)
    {
        if (nameScan == null || namesApplied || closing) return;
        if (!nameScan.IsCompleted)
        {
            if (!wait) return;
            try { nameScan.Wait(TimeSpan.FromSeconds(10)); } catch { /* reported below */ }
            if (!nameScan.IsCompleted) return;
        }
        namesApplied = true;
        if (!nameScan.IsCompletedSuccessfully)
        {
            if (!wait) ShowFlash("Couldn't look for names");
            return;
        }
        var blurs = nameScan.Result.Select(NameFinder.BlurFor).ToList();
        if (blurs.Count == 0) return;
        AddShapes(blurs, underneath: true);
        int visible = hasSel ? blurs.Count(b => Rectangle.Round(b.Rect).IntersectsWith(sel)) : 0;
        if (!wait && visible > 0) ShowFlash(visible == 1 ? "Blurred 1 name · Ctrl+Z to undo" : $"Blurred {visible} names · Ctrl+Z to undo");
    }

    /// <summary>Finds faces in the selection and blurs each one. Runs off the UI thread; the button stays lit meanwhile.</summary>
    async void BlurFaces()
    {
        if (!hasSel || closing || findingFaces) return;
        CommitText();
        if (!FaceFinder.Supported) { ShowFlash("Face detection isn't available on this PC"); return; }
        findingFaces = true;
        InvalidateRect(blurFacesButton.Bounds, 3);
        var area = sel;
        var crop = shot.Clone(area, shot.PixelFormat);
        List<Rectangle> faces;
        try
        {
            faces = await Task.Run(async () =>
            {
                using (crop) return await FaceFinder.FindAsync(crop).ConfigureAwait(false);
            });
        }
        catch (Exception ex)
        {
            findingFaces = false;
            if (!closing) { InvalidateRect(blurFacesButton.Bounds, 3); ShowFlash(ex is NotSupportedException ? ex.Message : "Couldn't look for faces"); }
            return;
        }
        findingFaces = false;
        if (closing || IsDisposed) return;
        InvalidateRect(blurFacesButton.Bounds, 3);

        var existing = shapes.OfType<BlurShape>().Where(b => b.Oval).Select(b => b.Rect).ToList();
        var fresh = faces
            .Select(f => new Rectangle(f.X + area.X, f.Y + area.Y, f.Width, f.Height))
            .Where(f => !existing.Any(e => Overlap(e, f) > 0.5f))
            .Select(f => new BlurShape { A = f.Location, B = new PointF(f.Right, f.Bottom), Oval = true, Size = Math.Max(4, f.Width / 7f), Color = color })
            .ToList();
        if (faces.Count == 0) { ShowFlash("No faces found"); return; }
        if (fresh.Count == 0) { ShowFlash("Those faces are already blurred"); return; }
        AddShapes(fresh, underneath: false);
        ShowFlash(fresh.Count == 1 ? "Blurred 1 face" : $"Blurred {fresh.Count} faces");
    }

    // ───────────────────────────── blur brush cursor ─────────────────────────────

    Rectangle lastBrush;

    /// <summary>A circle the size of the blur brush follows the pointer inside the selection.</summary>
    Rectangle BrushCursorRect()
    {
        if (tool != Tool.BlurBrush || !hasSel || !sel.Contains(mouse) || drag is not (DragMode.None or DragMode.Draw)) return Rectangle.Empty;
        float r = LiveSize(Tool.BlurBrush) / 2;
        return Ui.Outer(new RectangleF(mouse.X - r, mouse.Y - r, r * 2, r * 2), 3);
    }

    void UpdateBrushCursor()
    {
        var r = BrushCursorRect();
        if (r == lastBrush) return;
        InvalidateRect(lastBrush);
        InvalidateRect(r);
        lastBrush = r;
    }

    void DrawBrushCursor(Graphics g)
    {
        var r = BrushCursorRect();
        if (r.IsEmpty) return;
        float d = LiveSize(Tool.BlurBrush);
        var circle = new RectangleF(mouse.X - d / 2, mouse.Y - d / 2, d, d);
        using var shade = new Pen(Color.FromArgb(120, 0, 0, 0), 3f);
        using var ring = new Pen(Color.FromArgb(235, 255, 255, 255), 1.4f);
        g.DrawEllipse(shade, circle);
        g.DrawEllipse(ring, circle);
    }

    static float Overlap(RectangleF a, RectangleF b)
    {
        var i = RectangleF.Intersect(a, b);
        return i.IsEmpty ? 0 : i.Width * i.Height / Math.Min(a.Width * a.Height, b.Width * b.Height);
    }

    /// <summary>
    /// Adds shapes the app made as one undo step. Blurs that arrive while a drag or text edit is in progress are
    /// also put into that edit's snapshot, so undoing the edit doesn't take them away.
    /// </summary>
    void AddShapes(List<BlurShape> added, bool underneath)
    {
        PushUndo();
        if (underneath) shapes.InsertRange(0, added);
        else shapes.AddRange(added);
        foreach (var snapshot in new[] { editSnapshot, dragSnapshot })
            if (snapshot != null)
            {
                if (underneath) snapshot.InsertRange(0, added.Select(a => a.Clone()));
                else snapshot.AddRange(added.Select(a => a.Clone()));
            }
        if (hasSel) InvalidateRect(Rectangle.Inflate(sel, 4, 4));
    }
}
