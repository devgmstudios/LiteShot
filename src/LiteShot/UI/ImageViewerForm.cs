using System.Drawing.Drawing2D;

namespace LiteShot;

/// <summary>
/// A screenshot, big: it opens fitted to the window; the wheel zooms where the pointer is, dragging moves it,
/// double-click switches between fitted and actual size. + − 0 1 do the same from the keyboard, Ctrl+C copies, Esc closes.
/// </summary>
internal sealed class ImageViewerForm : Form
{
    readonly Bitmap image;
    readonly Canvas canvas;
    readonly Label hint;

    /// <param name="image">Shown, not owned: the caller keeps it.</param>
    public ImageViewerForm(Bitmap image, string title = "LiteShot — Screenshot")
    {
        this.image = image;
        var theme = new SettingsTheme();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10F);
        Text = title;
        Icon = TrayApp.AppIcon;
        KeyPreview = true;
        BackColor = theme.Dark ? Color.FromArgb(24, 24, 26) : Color.FromArgb(236, 236, 240);
        MinimumSize = new Size(360, 280);
        var work = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = new Size((int)(work.Width * 0.84), (int)(work.Height * 0.86));
        StartPosition = FormStartPosition.CenterScreen;

        canvas = new Canvas(image) { BackColor = BackColor };
        hint = new Label
        {
            AutoSize = false, Height = 30, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleCenter, UseMnemonic = false,
            ForeColor = theme.SubText, BackColor = theme.Window,
        };
        canvas.Changed += () => hint.Text = $"{canvas.Zoom * 100:0}%  ·  {image.Width} × {image.Height}  ·  scroll to zoom, drag to move, double-click for {(canvas.Fitted ? "actual size" : "fit")}  ·  Ctrl+C copies";
        Controls.Add(canvas);
        Controls.Add(hint);
        Shown += (_, _) => { canvas.Fit(); canvas.Focus(); };
    }

    public Canvas View => canvas;

    // Laid out by hand: the hint's text changes while the picture resizes, which upset docking.
    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (canvas == null || hint == null) return;
        int h = (int)(30 * DeviceDpi / 96f);
        hint.SetBounds(0, ClientSize.Height - h, ClientSize.Width, h);
        canvas.SetBounds(0, 0, ClientSize.Width, Math.Max(1, ClientSize.Height - h));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ApplyWindowChrome(Handle, new SettingsTheme().Dark);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape: Close(); return true;
            case Keys.Oemplus or Keys.Add or (Keys.Oemplus | Keys.Shift) or (Keys.Oemplus | Keys.Control): canvas.ZoomBy(1.25f, null); return true;
            case Keys.OemMinus or Keys.Subtract or (Keys.OemMinus | Keys.Control): canvas.ZoomBy(0.8f, null); return true;
            case Keys.D0 or Keys.NumPad0 or (Keys.D0 | Keys.Control): canvas.Fit(); return true;
            case Keys.D1 or Keys.NumPad1: canvas.Actual(null); return true;
            case Keys.Control | Keys.C:
                try { Clipboard.SetImage(image); hint.Text = "Copied the screenshot"; } catch { }
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>The zoomable picture.</summary>
    internal sealed class Canvas : Control
    {
        readonly Bitmap image;
        PointF offset;
        Point? dragFrom;
        PointF dragOffset;

        public float Zoom { get; private set; } = 1f;
        public bool Fitted { get; private set; } = true;
        public event Action? Changed;

        public Canvas(Bitmap image)
        {
            this.image = image;
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.SizeAll;
        }

        float FitZoom => Math.Max(0.02f, Math.Min((Width - 24f) / image.Width, (Height - 24f) / image.Height));

        public void Fit()
        {
            Zoom = FitZoom;
            Fitted = true;
            Center();
        }

        public void Actual(Point? at) => ZoomTo(1f, at);

        public void ZoomBy(float factor, Point? at) => ZoomTo(Zoom * factor, at);

        void ZoomTo(float zoom, Point? at)
        {
            zoom = Math.Clamp(zoom, Math.Min(0.05f, FitZoom), 16f);
            var p = at ?? new Point(Width / 2, Height / 2);
            // Keep the pixel under the pointer where it is.
            float ix = (p.X - offset.X) / Zoom, iy = (p.Y - offset.Y) / Zoom;
            Zoom = zoom;
            Fitted = Math.Abs(zoom - FitZoom) < 0.001f;
            offset = new PointF(p.X - ix * zoom, p.Y - iy * zoom);
            Clamp();
        }

        void Center()
        {
            offset = new PointF((Width - image.Width * Zoom) / 2, (Height - image.Height * Zoom) / 2);
            Clamp();
        }

        /// <summary>A picture smaller than the window stays centred; a bigger one can't be dragged off its edges.</summary>
        void Clamp()
        {
            float w = image.Width * Zoom, h = image.Height * Zoom;
            float x = w <= Width ? (Width - w) / 2 : Math.Clamp(offset.X, Width - w, 0);
            float y = h <= Height ? (Height - h) / 2 : Math.Clamp(offset.Y, Height - h, 0);
            offset = new PointF(x, y);
            Invalidate();
            Changed?.Invoke();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width < 10 || Height < 10) return;
            if (Fitted) Fit(); else Clamp();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ZoomBy(e.Delta > 0 ? 1.2f : 1 / 1.2f, e.Location);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Left) { dragFrom = e.Location; dragOffset = offset; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragFrom is not { } from) return;
            offset = new PointF(dragOffset.X + e.X - from.X, dragOffset.Y + e.Y - from.Y);
            Clamp();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragFrom = null;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (Fitted && Math.Abs(FitZoom - 1f) > 0.01f) Actual(e.Location); else Fit();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            // Smooth when shrinking, crisp pixels when zoomed well in.
            g.InterpolationMode = Zoom >= 2f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            var r = new RectangleF(offset.X, offset.Y, image.Width * Zoom, image.Height * Zoom);
            using (var shadow = new SolidBrush(Color.FromArgb(40, 0, 0, 0))) g.FillRectangle(shadow, r.X + 2, r.Y + 3, r.Width, r.Height);
            g.DrawImage(image, r);
        }
    }
}
