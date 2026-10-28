using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using LiteShot.Overlay;
using static LiteShot.Native;

namespace LiteShot;

/// <summary>
/// A small glass card in the corner while an AI job runs: spinner, status, elapsed time and cancel.
/// It never takes focus. Its glass is built from a capture of what's behind it, refreshed every few
/// seconds; the card excludes itself from screen capture so it can see through itself.
/// </summary>
internal sealed class AiProgressForm : Form
{
    static readonly List<AiProgressForm> stack = new();
    const int Pad = 24;

    readonly string title, model;
    readonly CancellationTokenSource cts;
    readonly DateTime started = DateTime.Now;
    readonly System.Windows.Forms.Timer timer;
    readonly float s;
    string status = "Starting…";
    float angle;
    GlassMaterial? glass;
    DateTime lastBackdrop;
    bool canSeeThrough, closeHover;

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    public AiProgressForm(string title, string model, CancellationTokenSource cts)
    {
        this.title = title;
        this.model = model;
        this.cts = cts;
        var area = Screen.PrimaryScreen!.WorkingArea;
        s = ScreenCapture.ScaleAt(new Point(area.X + area.Width / 2, area.Y + area.Height / 2));
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        Text = title;
        Size = new Size((int)(380 * s), (int)(70 * s));
        int gap = (int)(12 * s);
        Location = new Point(area.Right - Width - (int)(16 * s), area.Bottom - Height - (int)(16 * s) - stack.Count * (Height + gap));
        stack.Add(this);
        RefreshBackdrop();
        timer = new System.Windows.Forms.Timer { Interval = SystemTheme.AnimationsEnabled ? 33 : 1000 };
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= (int)WS_EX_TOOLWINDOW | 0x08000000 /* WS_EX_NOACTIVATE */ | 0x8 /* WS_EX_TOPMOST */;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowChrome(Handle, SystemTheme.Dark, DWMWCP_ROUND, mica: false);
        try { canSeeThrough = SetWindowDisplayAffinity(Handle, 0x11 /* WDA_EXCLUDEFROMCAPTURE */); }
        catch (EntryPointNotFoundException) { }
    }

    public void SetStatus(string text)
    {
        status = text;
        Invalidate();
    }

    void Tick()
    {
        angle = (angle + 9) % 360;
        if (canSeeThrough && (DateTime.Now - lastBackdrop).TotalSeconds > 2.5) RefreshBackdrop();
        Invalidate();
    }

    void RefreshBackdrop()
    {
        try
        {
            var region = Rectangle.Inflate(Bounds, Pad, Pad);
            using var behind = ScreenCapture.Capture(region, false);
            glass?.Dispose();
            glass = new GlassMaterial(behind, 1f);
            lastBackdrop = DateTime.Now;
        }
        catch { /* keep the previous glass */ }
    }

    RectangleF CloseRect => new(Width - 46 * s, (Height - 30 * s) / 2, 30 * s, 30 * s);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        var full = new RectangleF(0, 0, Width, Height);
        GlassTone tone;
        if (glass != null)
        {
            var inImage = new RectangleF(Pad, Pad, Width, Height);
            var lit = new Rectangle(0, 0, Width + Pad * 2, Height + Pad * 2);
            tone = glass.ToneAt(inImage, lit);
            g.Clear(tone.Light ? Color.FromArgb(240, 240, 242) : Color.FromArgb(32, 32, 36));
            var state = g.Save();
            g.TranslateTransform(-Pad, -Pad);
            glass.Draw(g, inImage, 8 * s, lit, tone, s, shadow: false);
            g.Restore(state);
        }
        else
        {
            tone = new GlassTone(!SystemTheme.Dark);
            g.Clear(tone.Light ? Color.FromArgb(246, 246, 248) : Color.FromArgb(38, 38, 42));
        }

        // Spinner: a track and a rotating arc in the accent color.
        float d = 26 * s;
        var spin = new RectangleF(20 * s, (Height - d) / 2, d, d);
        using (var track = new Pen(Ui.WithAlpha(tone.Glyph, 45), 3 * s)) g.DrawEllipse(track, spin);
        using (var arc = new Pen(SystemTheme.Accent, 3 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(arc, spin, angle, 100);

        float x = 60 * s;
        using var titleFont = Ui.MakeFont(14 * s, semibold: true);
        using var lineFont = Ui.MakeFont(12.5f * s);
        var elapsed = DateTime.Now - started;
        string line = $"{model}  ·  {status}  ·  {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
        float th = titleFont.GetHeight(g), lh = lineFont.GetHeight(g), top = (Height - th - lh - 2 * s) / 2;
        Ui.DrawText(g, title, titleFont, tone.Glyph, new PointF(x, top));
        using (var brush = new SolidBrush(tone.Secondary))
        using (var format = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter })
            g.DrawString(line, lineFont, brush, new RectangleF(x, top + th + 2 * s, CloseRect.Left - x - 6 * s, lh + 2), format);

        var cr = CloseRect;
        if (closeHover)
        {
            using var hover = new SolidBrush(tone.Hover);
            g.FillEllipse(hover, cr);
        }
        Icons.Draw(g, RectangleF.Inflate(cr, -7 * s, -7 * s), tone.Glyph, Icons.Close);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool hover = CloseRect.Contains(e.Location);
        if (hover != closeHover) { closeHover = hover; Cursor = hover ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        closeHover = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && CloseRect.Contains(e.Location))
        {
            cts.Cancel();
            Close();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            stack.Remove(this);
            timer.Dispose();
            glass?.Dispose();
        }
        base.Dispose(disposing);
    }
}
