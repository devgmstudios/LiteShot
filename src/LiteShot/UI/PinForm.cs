using System.Drawing.Drawing2D;
using static LiteShot.Native;

namespace LiteShot;

/// <summary>A screenshot pinned above all windows. Drag to move, wheel to zoom, Ctrl+wheel for opacity, double-click to close.</summary>
internal sealed class PinForm : Form
{
    readonly Bitmap image;
    readonly ContextMenuStrip menu;
    readonly System.Windows.Forms.Timer badgeTimer = new() { Interval = 900 };
    float zoom = 1f;
    string? badge;

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
        Bounds = new Rectangle(screenLocation, image.Size);
        menu = BuildMenu();
        badgeTimer.Tick += (_, _) => { badgeTimer.Stop(); badge = null; Invalidate(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000;            // CS_DROPSHADOW
            cp.ExStyle |= (int)WS_EX_TOOLWINDOW;
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
        m.Items.Add(new ToolStripMenuItem("Save as…", null, (_, _) => SaveAs()) { ShortcutKeyDisplayString = "Ctrl+S" });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Actual size", null, (_, _) => SetZoom(1f, new Point(0, 0))) { ShortcutKeyDisplayString = "0" });
        var opacity = new ToolStripMenuItem("Opacity");
        foreach (int pct in new[] { 100, 80, 60, 40 })
            opacity.DropDownItems.Add(new ToolStripMenuItem($"{pct}%", null, (_, _) => { Opacity = pct / 100.0; ShowBadge($"{pct}%"); }));
        m.Items.Add(opacity);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Close", null, (_, _) => Close()) { ShortcutKeyDisplayString = "Esc" });
        m.Items.Add(new ToolStripMenuItem("Close all pins", null, (_, _) => TrayApp.Instance.CloseAllPins()));
        foreach (ToolStripItem item in m.Items) item.Padding = new Padding(4, 3, 4, 3);
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
                m.Result = (IntPtr)HTCAPTION;   // the whole window drags
                return;
            case WM_NCLBUTTONDBLCLK:
                Close();
                return;
            case WM_NCRBUTTONUP:
                menu.Show(Cursor.Position);
                return;
        }
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.InterpolationMode = Math.Abs(zoom - 1f) < 0.001f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(image, ClientRectangle, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
        g.PixelOffsetMode = PixelOffsetMode.None;
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

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) != 0)
        {
            Opacity = Math.Clamp(Opacity + (e.Delta > 0 ? 0.1 : -0.1), 0.2, 1.0);
            ShowBadge($"{Math.Round(Opacity * 100)}%");
            return;
        }
        SetZoom(Math.Clamp(zoom * (e.Delta > 0 ? 1.15f : 1 / 1.15f), 0.1f, 8f), e.Location);
    }

    void SetZoom(float z, Point anchor)
    {
        float fx = ClientSize.Width > 0 ? anchor.X / (float)ClientSize.Width : 0;
        float fy = ClientSize.Height > 0 ? anchor.Y / (float)ClientSize.Height : 0;
        var screenAnchor = PointToScreen(anchor);
        zoom = Math.Abs(z - 1f) < 0.04f ? 1f : z;
        int w = Math.Max(16, (int)Math.Round(image.Width * zoom)), h = Math.Max(16, (int)Math.Round(image.Height * zoom));
        Bounds = new Rectangle(screenAnchor.X - (int)(fx * w), screenAnchor.Y - (int)(fy * h), w, h);
        ShowBadge($"{Math.Round(zoom * 100)}%");
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape: Close(); return true;
            case Keys.Control | Keys.C: Copy(); return true;
            case Keys.Control | Keys.S: SaveAs(); return true;
            case Keys.D0:
            case Keys.NumPad0:
            case Keys.Control | Keys.D0:
                SetZoom(1f, Point.Empty);
                return true;
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
