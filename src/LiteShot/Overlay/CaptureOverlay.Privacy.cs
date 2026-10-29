namespace LiteShot.Overlay;

/// <summary>
/// Privacy: "Hide personal info" (H, remembered between captures) blurs names, faces, emails, phone and card numbers
/// and the user's keywords automatically; faces can also be blurred with one click, or painted over with the brush.
/// </summary>
internal sealed partial class CaptureOverlay
{
    Task<List<Redaction>>? privacyScan;
    bool privacyApplied, findingFaces;

    static bool HidingPersonal => AppSettings.Current.HidePersonalInfo;

    /// <summary>Starts looking for personal details in the background as soon as the overlay is up.</summary>
    void StartPrivacyScan()
    {
        if (!HidingPersonal || privacyScan != null) return;
        // The UI thread keeps painting from the capture, so the scan gets its own copy.
        var copy = shot.Clone(new Rectangle(Point.Empty, shot.Size), shot.PixelFormat);
        var keywords = AppSettings.Current.RedactKeywords.ToList();
        privacyScan = Task.Run(async () =>
        {
            using (copy) return await PersonalInfo.FindAsync(copy, keywords, faces: true).ConfigureAwait(false);
        });
        privacyScan.ContinueWith(_ =>
        {
            try { if (!closing && IsHandleCreated) BeginInvoke(new Action(() => ApplyPrivacy(wait: false))); }
            catch (InvalidOperationException) { /* the overlay closed meanwhile */ }
        }, TaskScheduler.Default);
    }

    /// <summary>Adds the automatic blurs once the scan is done. Output paths pass <paramref name="wait"/> so a quick Ctrl+C never leaks anything.</summary>
    void ApplyPrivacy(bool wait)
    {
        if (!HidingPersonal || privacyScan == null || privacyApplied || closing) return;
        if (!privacyScan.IsCompleted)
        {
            if (!wait) return;
            try { privacyScan.Wait(TimeSpan.FromSeconds(10)); } catch { /* reported below */ }
            if (!privacyScan.IsCompleted) return;
        }
        privacyApplied = true;
        if (!privacyScan.IsCompletedSuccessfully)
        {
            if (!wait) ShowFlash("Couldn't look for personal info");
            return;
        }
        var blurs = privacyScan.Result.Select(PersonalInfo.BlurFor).ToList();
        InvalidateRect(hidePersonalButton.Bounds, 3);
        if (blurs.Count == 0) return;
        AddShapes(blurs, underneath: true);
        int visible = hasSel ? blurs.Count(b => Rectangle.Round(b.Rect).IntersectsWith(sel)) : 0;
        if (!wait && visible > 0) ShowFlash(visible == 1 ? "Hid 1 personal detail" : $"Hid {visible} personal details");
    }

    /// <summary>The "Hide personal info" button: on adds the automatic blurs (and keeps them on for next time), off takes them away.</summary>
    void ToggleHidePersonal()
    {
        if (closing) return;
        CommitText();
        var settings = AppSettings.Current;
        settings.HidePersonalInfo = !settings.HidePersonalInfo;
        settings.Save();
        InvalidateRect(hidePersonalButton.Bounds, 3);
        if (settings.HidePersonalInfo)
        {
            privacyApplied = false;
            if (privacyScan == null) StartPrivacyScan();
            if (privacyScan!.IsCompleted) ApplyPrivacy(wait: false);
            else ShowFlash("Hiding personal info…");
            return;
        }
        var automatic = shapes.OfType<BlurShape>().Where(b => b.Auto).ToList();
        if (automatic.Count > 0)
        {
            PushUndo();
            shapes.RemoveAll(a => a is BlurShape { Auto: true });
            if (selectedShape is BlurShape { Auto: true }) selectedShape = null;
            if (hasSel) InvalidateRect(Rectangle.Inflate(sel, 4, 4));
        }
        privacyApplied = false;
        ShowFlash("Personal info shown · blur anything by hand with U");
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
