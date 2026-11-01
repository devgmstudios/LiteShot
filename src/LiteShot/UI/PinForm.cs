using System.Drawing.Drawing2D;
using static LiteShot.Native;

namespace LiteShot;

/// <summary>
/// A screenshot pinned above all windows. Drag to move, drag an edge or corner to resize, wheel to zoom,
/// Ctrl+wheel for opacity, arrow keys to nudge, R/L to rotate, H/V to flip, T for click-through, double-click to close.
/// </summary>
internal sealed class PinForm : Form
{
    const float MinZoom = 0.1f, MaxZoom = 8f;
    const int MinSide = 16;

    readonly Bitmap image;
    readonly ContextMenuStrip menu;
    readonly System.Windows.Forms.Timer badgeTimer = new() { Interval = 900 };
    float zoom = 1f;
    string? badge;
    bool active, clickThrough;
    // The WMSZ_ edge being dragged (0 when none), and where the drag began.
    int sizing;
    Rectangle sizingFrom;
    Point sizingStart;
    double opacityBeforeClickThrough = 1;

    public PinForm(Bitmap image, Point screenLocation)
    {
        this.image = image;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        MaximizeBox = MinimizeBox = false;
        KeyPreview = true;
        Text = "LiteShot pin";
        DoubleBuffered = true;
        ResizeRedraw = true;
        MinimumSize = new Size(MinSide, MinSide);   // lets an edge drag go below Windows' usual minimum window size
        Bounds = new Rectangle(screenLocation, image.Size);
        menu = BuildMenu();
        badgeTimer.Tick += (_, _) => { badgeTimer.Stop(); badge = null; Invalidate(); };
    }

    /// <summary>How much the screenshot is scaled: 1 is pixel-exact.</summary>
    public float Zoom => zoom;

    /// <summary>Clicks go through to the window underneath. The pin can't be right-clicked then, so the tray menu turns it off.</summary>
    public bool ClickThrough
    {
        get => clickThrough;
        set
        {
            if (value == clickThrough) return;
            clickThrough = value;
            // Only a layered window (opacity under 100%) lets clicks through, and the fade shows it's not clickable.
            if (value) { opacityBeforeClickThrough = Opacity; Opacity = Math.Min(Opacity, 0.7); }
            UpdateStyles();
            if (!value) Opacity = opacityBeforeClickThrough;
            ShowBadge(value ? "Click-through on" : "Click-through off");
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000;            // CS_DROPSHADOW
            cp.ExStyle |= (int)WS_EX_TOOLWINDOW;
            if (clickThrough) cp.ExStyle |= (int)WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowChrome(Handle, SystemTheme.Dark, DWMWCP_ROUNDSMALL, mica: false);
    }

    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip { Renderer = new ModernMenuRenderer(), ShowImageMargin = false };
        m.Items.Add(new ToolStripMenuItem("Copy", null, (_, _) => Copy()) { ShortcutKeyDisplayString = "Ctrl+C" });
        m.Items.Add(new ToolStripMenuItem("Copy text", null, (_, _) => CopyText()) { ShortcutKeyDisplayString = "Ctrl+Shift+C" });
        m.Items.Add(new ToolStripMenuItem("Save as…", null, (_, _) => SaveAs()) { ShortcutKeyDisplayString = "Ctrl+S" });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Actual size", null, (_, _) => SetZoom(1f, new Point(0, 0))) { ShortcutKeyDisplayString = "0" });
        var turn = new ToolStripMenuItem("Rotate and flip");
        turn.DropDownItems.Add(new ToolStripMenuItem("Rotate right", null, (_, _) => Turn(RotateFlipType.Rotate90FlipNone)) { ShortcutKeyDisplayString = "R" });
        turn.DropDownItems.Add(new ToolStripMenuItem("Rotate left", null, (_, _) => Turn(RotateFlipType.Rotate270FlipNone)) { ShortcutKeyDisplayString = "L" });
        turn.DropDownItems.Add(new ToolStripMenuItem("Flip horizontally", null, (_, _) => Turn(RotateFlipType.RotateNoneFlipX)) { ShortcutKeyDisplayString = "H" });
        turn.DropDownItems.Add(new ToolStripMenuItem("Flip vertically", null, (_, _) => Turn(RotateFlipType.RotateNoneFlipY)) { ShortcutKeyDisplayString = "V" });
        m.Items.Add(turn);
        var opacity = new ToolStripMenuItem("Opacity");
        foreach (int pct in new[] { 100, 80, 60, 40 })
            opacity.DropDownItems.Add(new ToolStripMenuItem($"{pct}%", null, (_, _) => { Opacity = pct / 100.0; ShowBadge($"{pct}%"); }));
        m.Items.Add(opacity);
        m.Items.Add(new ToolStripMenuItem("Click-through", null, (_, _) => SetClickThrough()) { ShortcutKeyDisplayString = "T" });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Close", null, (_, _) => Close()) { ShortcutKeyDisplayString = "Esc" });
        m.Items.Add(new ToolStripMenuItem("Close all pins", null, (_, _) => TrayApp.Instance.CloseAllPins()));
        foreach (ToolStripItem item in m.Items) item.Padding = new Padding(4, 3, 4, 3);
        foreach (ToolStripItem item in turn.DropDownItems) item.Padding = new Padding(4, 3, 4, 3);
        ModernMenuRenderer.Attach(m);
        return m;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_DPICHANGED:
                m.Result = IntPtr.Zero;   // keep the screenshot pixel-exact when dragged across monitors
                return;
            case WM_NCHITTEST:
                long lp = m.LParam.ToInt64();
                m.Result = (IntPtr)HitTest(PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF))));
                return;
            case WM_NCLBUTTONDOWN when m.WParam.ToInt32() is >= HTLEFT and <= HTBOTTOMRIGHT:
                // A borderless window has no sizing frame for Windows to drag, so the pin follows the mouse itself.
                long at = m.LParam.ToInt64();
                sizing = m.WParam.ToInt32() - HTLEFT + WMSZ_LEFT;
                sizingFrom = Bounds;
                sizingStart = new Point((short)(at & 0xFFFF), (short)((at >> 16) & 0xFFFF));
                Capture = true;
                return;
            case WM_NCLBUTTONDBLCLK:
                if (m.WParam.ToInt32() == HTCAPTION) Close();   // a double-click on an edge does nothing
                return;
            case WM_NCRBUTTONUP:
                menu.Show(Cursor.Position);
                return;
        }
        base.WndProc(ref m);
    }

    /// <summary>Edges and corners resize (corners are easier to grab), everywhere else drags.</summary>
    int HitTest(Point p)
    {
        int w = ClientSize.Width, h = ClientSize.Height;
        int band = (int)Math.Max(2, Math.Min(8 * DeviceDpi / 96f, Math.Min(w, h) / 4f)), corner = band * 2;
        bool l = p.X < band, r = p.X >= w - band, t = p.Y < band, b = p.Y >= h - band;
        bool nearL = p.X < corner, nearR = p.X >= w - corner, nearT = p.Y < corner, nearB = p.Y >= h - corner;
        if ((t && nearL) || (l && nearT)) return HTTOPLEFT;
        if ((t && nearR) || (r && nearT)) return HTTOPRIGHT;
        if ((b && nearL) || (l && nearB)) return HTBOTTOMLEFT;
        if ((b && nearR) || (r && nearB)) return HTBOTTOMRIGHT;
        return l ? HTLEFT : r ? HTRIGHT : t ? HTTOP : b ? HTBOTTOM : HTCAPTION;
    }

    /// <summary>Keeps the screenshot's shape while an edge or corner is dragged; the opposite side stays put.</summary>
    internal Rectangle KeepAspect(int edge, Rectangle proposed)
    {
        float zw = proposed.Width / (float)image.Width, zh = proposed.Height / (float)image.Height;
        bool byWidth = edge is WMSZ_LEFT or WMSZ_RIGHT || (edge is not (WMSZ_TOP or WMSZ_BOTTOM) && zw >= zh);
        zoom = Fit(Snap(Math.Clamp(byWidth ? zw : zh, MinZoom, MaxZoom)));
        var size = SizeAt(zoom);
        int left = edge is WMSZ_LEFT or WMSZ_TOPLEFT or WMSZ_BOTTOMLEFT ? proposed.Right - size.Width : proposed.Left;
        int top = edge is WMSZ_TOP or WMSZ_TOPLEFT or WMSZ_TOPRIGHT ? proposed.Bottom - size.Height : proposed.Top;
        ShowBadge($"{Math.Round(zoom * 100)}%");
        return new Rectangle(left, top, size.Width, size.Height);
    }

    // Close to 100% lands on 100%, where the screenshot is pixel-exact again.
    static float Snap(float z) => Math.Abs(z - 1f) < 0.04f ? 1f : z;

    // Windows won't make a window bigger than the screens allow; past that the pin would lose its shape.
    float Fit(float z)
    {
        var max = SystemInformation.MaxWindowTrackSize;
        return Math.Min(z, Math.Min(max.Width / (float)image.Width, max.Height / (float)image.Height));
    }

    Size SizeAt(float z) => new(Math.Max(MinSide, (int)Math.Round(image.Width * z)), Math.Max(MinSide, (int)Math.Round(image.Height * z)));

    protected override void OnActivated(EventArgs e) { base.OnActivated(e); active = true; Invalidate(); }
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); active = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.InterpolationMode = Math.Abs(zoom - 1f) < 0.001f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(image, ClientRectangle, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
        g.PixelOffsetMode = PixelOffsetMode.None;
        // A thin accent outline on the pin that keys go to.
        if (active && !clickThrough)
        {
            float w = Math.Max(1f, MathF.Round(1.5f * DeviceDpi / 96f));
            using var pen = new Pen(SystemTheme.Accent, w);
            g.DrawRectangle(pen, w / 2, w / 2, ClientSize.Width - w, ClientSize.Height - w);
        }
        if (badge != null) DrawBadge(g);
    }

    void DrawBadge(Graphics g)
    {
        float s = DeviceDpi / 96f;
        using var font = Ui.MakeFont(13 * s, semibold: true);
        var ts = Ui.Measure(badge!, font);
        var r = new RectangleF(ClientSize.Width - ts.Width - 30 * s, ClientSize.Height - ts.Height - 24 * s, ts.Width + 20 * s, ts.Height + 12 * s);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Ui.RoundRect(r, r.Height / 2);
        using var bg = new SolidBrush(Color.FromArgb(200, 28, 28, 30));
        g.FillPath(bg, path);
        Overlay.GlassMaterial.DrawRim(g, r, r.Height / 2, s, 0.6f);
        Ui.DrawTextCentered(g, badge!, font, Color.White, r);
    }

    void ShowBadge(string text)
    {
        badge = text;
        badgeTimer.Stop();
        badgeTimer.Start();
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (sizing == 0) return;
        var p = PointToScreen(e.Location);
        int dx = p.X - sizingStart.X, dy = p.Y - sizingStart.Y;
        var f = sizingFrom;
        int left = f.Left, top = f.Top, right = f.Right, bottom = f.Bottom;
        if (sizing is WMSZ_LEFT or WMSZ_TOPLEFT or WMSZ_BOTTOMLEFT) left += dx;
        if (sizing is WMSZ_RIGHT or WMSZ_TOPRIGHT or WMSZ_BOTTOMRIGHT) right += dx;
        if (sizing is WMSZ_TOP or WMSZ_TOPLEFT or WMSZ_TOPRIGHT) top += dy;
        if (sizing is WMSZ_BOTTOM or WMSZ_BOTTOMLEFT or WMSZ_BOTTOMRIGHT) bottom += dy;
        Bounds = KeepAspect(sizing, Rectangle.FromLTRB(left, top, Math.Max(left + 1, right), Math.Max(top + 1, bottom)));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (sizing != 0) Capture = false;
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        sizing = 0;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) != 0)
        {
            Opacity = Math.Clamp(Opacity + (e.Delta > 0 ? 0.1 : -0.1), 0.2, 1.0);
            ShowBadge($"{Math.Round(Opacity * 100)}%");
            return;
        }
        SetZoom(Math.Clamp(zoom * (e.Delta > 0 ? 1.15f : 1 / 1.15f), MinZoom, MaxZoom), e.Location);
    }

    void SetZoom(float z, Point anchor)
    {
        float fx = ClientSize.Width > 0 ? anchor.X / (float)ClientSize.Width : 0;
        float fy = ClientSize.Height > 0 ? anchor.Y / (float)ClientSize.Height : 0;
        var screenAnchor = PointToScreen(anchor);
        zoom = Fit(Snap(z));
        var size = SizeAt(zoom);
        Bounds = new Rectangle(screenAnchor.X - (int)(fx * size.Width), screenAnchor.Y - (int)(fy * size.Height), size.Width, size.Height);
        ShowBadge($"{Math.Round(zoom * 100)}%");
    }

    /// <summary>Rotates or flips the pinned screenshot (copies and saves get it too), turning about the pin's centre.</summary>
    internal void Turn(RotateFlipType how)
    {
        var center = new Point(Left + Width / 2, Top + Height / 2);
        image.RotateFlip(how);
        var size = SizeAt(zoom);
        Bounds = new Rectangle(center.X - size.Width / 2, center.Y - size.Height / 2, size.Width, size.Height);
        Invalidate();
    }

    void SetClickThrough()
    {
        ClickThrough = true;
        TrayApp.Instance?.Notify("Pin is click-through", "Clicks now go to the window underneath. To click the pin again, open the LiteShot tray menu and choose Make pins clickable.");
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape: Close(); return true;
            case Keys.Control | Keys.C: Copy(); return true;
            case Keys.Control | Keys.Shift | Keys.C: CopyText(); return true;
            case Keys.Control | Keys.S: SaveAs(); return true;
            case Keys.D0:
            case Keys.NumPad0:
            case Keys.Control | Keys.D0:
                SetZoom(1f, Point.Empty);
                return true;
            case Keys.R: Turn(RotateFlipType.Rotate90FlipNone); return true;
            case Keys.L: Turn(RotateFlipType.Rotate270FlipNone); return true;
            case Keys.H: Turn(RotateFlipType.RotateNoneFlipX); return true;
            case Keys.V: Turn(RotateFlipType.RotateNoneFlipY); return true;
            case Keys.T:
                // The pin still has the keyboard right after going click-through, so T turns it back.
                if (ClickThrough) ClickThrough = false; else SetClickThrough();
                return true;
        }
        // Arrow keys nudge the pin a pixel at a time; with Shift, ten.
        int step = (keyData & Keys.Shift) != 0 ? 10 : 1;
        switch (keyData & ~Keys.Shift)
        {
            case Keys.Left: Left -= step; return true;
            case Keys.Right: Left += step; return true;
            case Keys.Up: Top -= step; return true;
            case Keys.Down: Top += step; return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void Copy()
    {
        try
        {
            ImageOutput.CopyToClipboard(image);
            ShowBadge("Copied");
        }
        catch (Exception ex) { ShowBadge(ex.Message); }
    }

    /// <summary>Reads the text in the pin with Windows' OCR and copies it.</summary>
    void CopyText()
    {
        ShowBadge("Reading text…");
        TrayApp.Instance.Ocr((Bitmap)image.Clone());
    }

    void SaveAs()
    {
        try
        {
            if (ImageOutput.SaveWithDialog(this, image) is string path) TrayApp.Instance.NotifySaved(path);
        }
        catch (Exception ex) { TrayApp.Instance.Notify("Couldn't save the screenshot", ex.Message, error: true); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            image.Dispose();
            menu.Dispose();
            badgeTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
