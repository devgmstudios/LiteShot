using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using static LiteShot.Native;

namespace LiteShot.Overlay;

internal enum Tool { None, Pen, Line, Arrow, Rect, Ellipse, Marker, Text, Counter, Pixelate, BlurBrush }

internal enum DragMode { None, NewSelection, MoveSelection, Resize, Draw, MoveShape }

/// <summary>
/// The full-screen capture surface: a frozen copy of every monitor, dimmed outside the selection.
/// Everything is painted on one window in physical pixels, so it stays pixel-exact on mixed-DPI setups;
/// UI chrome is scaled by the DPI of the monitor that hosts the selection.
/// </summary>
internal sealed partial class CaptureOverlay : Form
{
    const int DimAlpha = 115;

    readonly TrayApp host;
    readonly Bitmap shot, dimmed;
    readonly GlassMaterial glass;
    readonly Rectangle virt, client;
    readonly List<Rectangle> windows;
    readonly Rectangle[] monitors;
    readonly float[] monitorScales;

    Rectangle sel, prevSel;
    bool hasSel, prevHasSel;
    readonly List<Annotation> shapes = new();
    readonly Stack<List<Annotation>> undoStack = new(), redoStack = new();
    Tool tool = Tool.None;
    Color color;
    readonly Dictionary<Tool, float> sizes = new();

    DragMode drag;
    Point dragStart, lastMouse, mouse;
    Rectangle selAtStart;
    int resizeHandle = -1;
    bool dragMoved, mouseInside = true, straightStroke;
    Annotation? drawing, selectedShape, movingShape;
    List<Annotation>? dragSnapshot;
    Rectangle? hoverWindow;

    TextShape? editing;
    int caret;
    bool caretOn, editIsNew;
    List<Annotation>? editSnapshot;
    string editSignature = "";

    readonly Toolbar tools, actions;
    ToolButton colorButton = null!, shareButton = null!, blurFacesButton = null!;
    ToolButton? hoverButton, pressedButton;
    float hoverAnim = 1f;
    bool tooltipVisible, paletteOpen, closing;
    int paletteHover = -1;
    RectangleF paletteRect;

    string? flashText;
    Point flashAnchor;
    float S = 1f;
    Font labelFont = null!, smallFont = null!;
    Rectangle lastFrame, lastMag, lastFlash, lastTip;
    DateTime lastCoalescedUndo;
    object? coalesceTarget;

    readonly System.Windows.Forms.Timer caretTimer = new() { Interval = 530 };
    readonly System.Windows.Forms.Timer tooltipTimer = new() { Interval = 450 };
    readonly System.Windows.Forms.Timer flashTimer = new() { Interval = 1100 };
    readonly System.Windows.Forms.Timer animTimer = new() { Interval = 15 };

    public CaptureOverlay(TrayApp host, Bitmap shot, Rectangle virt, IReadOnlyList<Rectangle> windowRects, Rectangle? preselect)
    {
        this.host = host;
        this.shot = shot;
        this.virt = virt;
        client = new Rectangle(Point.Empty, virt.Size);
        dimmed = MakeDimmed(shot);
        glass = new GlassMaterial(shot, 1f - DimAlpha / 255f);
        windows = windowRects.Select(ToClient).Select(r => Rectangle.Intersect(r, client))
            .Where(r => r.Width > 4 && r.Height > 4).ToList();
        var screens = Screen.AllScreens;
        monitors = screens.Select(s => ToClient(s.Bounds)).ToArray();
        monitorScales = screens.Select(s => ScreenCapture.ScaleAt(new Point(s.Bounds.X + s.Bounds.Width / 2, s.Bounds.Y + s.Bounds.Height / 2))).ToArray();

        var settings = AppSettings.Current;
        color = Color.FromArgb(settings.AnnotationColor);
        foreach (var t in Enum.GetValues<Tool>())
            if (t != Tool.None)
                sizes[t] = settings.ToolSizes.TryGetValue(t.ToString(), out var v) ? v : DefaultToolSize(t);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        KeyPreview = true;
        Text = "LiteShot";
        Bounds = virt;
        Cursor = Cursors.Cross;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
        // Let WinForms keep one screen-sized back buffer instead of allocating a new one on every paint.
        BufferedGraphicsManager.Current.MaximumBuffer = new Size(virt.Width + 1, virt.Height + 1);

        tools = BuildToolsBar();
        actions = BuildActionsBar();
        textBar = BuildTextBar();
        LoadTextDefaults(settings);

        caretTimer.Tick += (_, _) => { caretOn = !caretOn; InvalidateCaret(); };
        tooltipTimer.Tick += (_, _) =>
        {
            tooltipTimer.Stop();
            if (hoverButton != null && !closing && drag == DragMode.None) { tooltipVisible = true; UpdateView(); }
        };
        flashTimer.Tick += (_, _) => { flashTimer.Stop(); flashText = null; UpdateView(); };
        animTimer.Tick += (_, _) => AnimateHover();

        var cursor = Cursor.Position;
        mouse = new Point(cursor.X - virt.X, cursor.Y - virt.Y);
        if (preselect is Rectangle p)
        {
            sel = Rectangle.Intersect(ToClient(p), client);
            hasSel = sel.Width > 0 && sel.Height > 0;
        }
        UpdateScale(force: true);
        if (!hasSel) UpdateHoverWindow();
    }

    static float DefaultToolSize(Tool t) => t switch
    {
        Tool.Marker => 16,
        Tool.Text => 22,
        Tool.Counter => 14,
        Tool.Pixelate => 10,
        Tool.BlurBrush => 40,
        _ => 3,
    };

    static (float Min, float Max, float Step) SizeRange(Tool t) => t switch
    {
        Tool.BlurBrush => (10, 160, 4),
        Tool.Text => (8, 160, 2),
        Tool.Marker => (4, 80, 2),
        Tool.Counter => (8, 48, 2),
        Tool.Pixelate => (3, 40, 1),
        _ => (1, 40, 1),
    };

    static float[] SizePresets(Tool t) => t switch
    {
        Tool.Text => new[] { 14f, 18, 24, 32, 48 },
        Tool.Marker => new[] { 8f, 12, 16, 24, 32 },
        Tool.Counter => new[] { 10f, 12, 14, 18, 24 },
        Tool.Pixelate => new[] { 5f, 8, 10, 14, 20 },
        Tool.BlurBrush => new[] { 24f, 32, 40, 60, 90 },
        _ => new[] { 2f, 3, 5, 8, 12 },
    };

    static string SizeName(Tool t) => t switch
    {
        Tool.Text => "Font size",
        Tool.Counter => "Badge size",
        Tool.Pixelate => "Block size",
        Tool.BlurBrush => "Brush size",
        Tool.Marker => "Marker width",
        _ => "Stroke width",
    };

    static Tool ToolOf(Annotation a) => a switch
    {
        StrokeShape s => s.Highlighter ? Tool.Marker : Tool.Pen,
        ArrowShape => Tool.Arrow,
        LineShape => Tool.Line,
        RectShape => Tool.Rect,
        EllipseShape => Tool.Ellipse,
        PixelateShape or BlurShape => Tool.Pixelate,
        BlurBrushShape => Tool.BlurBrush,
        TextShape => Tool.Text,
        CounterShape => Tool.Counter,
        _ => Tool.Pen,
    };

    Tool SizeTool => editing != null ? Tool.Text
        : selectedShape != null && tool == Tool.None ? ToolOf(selectedShape)
        : tool == Tool.None ? Tool.Pen : tool;

    static Bitmap MakeDimmed(Bitmap src)
    {
        var d = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(d);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
        g.CompositingMode = CompositingMode.SourceOver;
        using var shade = new SolidBrush(Color.FromArgb(DimAlpha, 0, 0, 0));
        g.FillRectangle(shade, 0, 0, src.Width, src.Height);
        return d;
    }

    // ───────────────────────────── window lifecycle ─────────────────────────────

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= (int)WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SetWindowPos(Handle, HWND_TOPMOST, virt.X, virt.Y, virt.Width, virt.Height, SWP_SHOWWINDOW);
        BringToForeground();
        UpdateView(true);
        StartNameScan();
    }

    void BringToForeground()
    {
        var fg = GetForegroundWindow();
        if (fg == Handle) return;
        uint fgThread = GetWindowThreadProcessId(fg, out _), me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        BringWindowToTop(Handle);
        SetForegroundWindow(Handle);
        if (attached) AttachThreadInput(me, fgThread, false);
        Activate();
        Focus();
    }

    protected override void WndProc(ref Message m)
    {
        // The overlay maps 1:1 onto physical pixels; never let Windows rescale it between monitors.
        if (m.Msg == WM_DPICHANGED) { m.Result = IntPtr.Zero; return; }
        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        var settings = AppSettings.Current;
        settings.AnnotationColor = color.ToArgb();
        foreach (var kv in sizes) settings.ToolSizes[kv.Key.ToString()] = kv.Value;
        SaveTextDefaults(settings);
        settings.Save();
        BufferedGraphicsManager.Current.Invalidate();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            caretTimer.Dispose();
            tooltipTimer.Dispose();
            flashTimer.Dispose();
            animTimer.Dispose();
            shot.Dispose();
            dimmed.Dispose();
            glass.Dispose();
            labelFont?.Dispose();
            smallFont?.Dispose();
        }
        base.Dispose(disposing);
        if (disposing) aiFont?.Dispose();   // after the prompt box that uses it is gone
    }

    void CloseOverlay()
    {
        if (closing) return;
        closing = true;
        Hide();
        BeginInvoke(new Action(Close));
    }

    // ───────────────────────────── geometry ─────────────────────────────

    Rectangle ToClient(Rectangle r) => new(r.X - virt.X, r.Y - virt.Y, r.Width, r.Height);

    Rectangle ScreenSel => new(sel.X + virt.X, sel.Y + virt.Y, sel.Width, sel.Height);

    static Point Center(Rectangle r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    static PointF Center(RectangleF r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    int Px(float v) => (int)MathF.Round(v * S);

    int MonitorIndexAt(Point p)
    {
        for (int i = 0; i < monitors.Length; i++)
            if (monitors[i].Contains(p)) return i;
        int best = 0;
        long bestDist = long.MaxValue;
        for (int i = 0; i < monitors.Length; i++)
        {
            var c = Center(monitors[i]);
            long d = (long)(c.X - p.X) * (c.X - p.X) + (long)(c.Y - p.Y) * (c.Y - p.Y);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    Rectangle MonitorAt(Point p) => monitors.Length == 0 ? client : monitors[MonitorIndexAt(p)];

    Rectangle Lit => hasSel ? sel : hoverWindow ?? Rectangle.Empty;

    void UpdateScale(bool force = false)
    {
        float scale = monitorScales.Length == 0 ? 1f : monitorScales[MonitorIndexAt(hasSel ? Center(sel) : mouse)];
        if (!force && scale == S) return;
        S = scale;
        labelFont?.Dispose();
        smallFont?.Dispose();
        labelFont = Ui.MakeFont(12.5f * S, semibold: true);
        smallFont = Ui.MakeFont(12.5f * S);
    }

    Rectangle ClampInside(Rectangle r) => new(
        Math.Clamp(r.X, 0, Math.Max(0, client.Width - r.Width)),
        Math.Clamp(r.Y, 0, Math.Max(0, client.Height - r.Height)),
        Math.Min(r.Width, client.Width), Math.Min(r.Height, client.Height));

    Rectangle RectFromPoints(Point a, Point b, bool square)
    {
        int dx = b.X - a.X, dy = b.Y - a.Y;
        if (square)
        {
            int side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = Math.Sign(dx == 0 ? 1 : dx) * side;
            dy = Math.Sign(dy == 0 ? 1 : dy) * side;
        }
        int x1 = Math.Min(a.X, a.X + dx), y1 = Math.Min(a.Y, a.Y + dy);
        int x2 = Math.Max(a.X, a.X + dx) + 1, y2 = Math.Max(a.Y, a.Y + dy) + 1;
        return Rectangle.Intersect(Rectangle.FromLTRB(x1, y1, x2, y2), client);
    }

    Rectangle ResizeRect(Rectangle s, int handle, int dx, int dy)
    {
        int l = s.Left, t = s.Top, r = s.Right, b = s.Bottom;
        if (handle is 0 or 6 or 7) l += dx;
        if (handle is 2 or 3 or 4) r += dx;
        if (handle is 0 or 1 or 2) t += dy;
        if (handle is 4 or 5 or 6) b += dy;
        var rect = Rectangle.FromLTRB(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b));
        rect = Rectangle.Intersect(rect, client);
        if (rect.Width < 1) rect.Width = 1;
        if (rect.Height < 1) rect.Height = 1;
        return rect;
    }

    static PointF[] HandleCenters(Rectangle r)
    {
        float l = r.Left - 1, t = r.Top - 1, rr = r.Right, b = r.Bottom, cx = (l + rr) / 2f, cy = (t + b) / 2f;
        return new[] { new PointF(l, t), new PointF(cx, t), new PointF(rr, t), new PointF(rr, cy),
                       new PointF(rr, b), new PointF(cx, b), new PointF(l, b), new PointF(l, cy) };
    }

    int HitHandle(Point p)
    {
        float tol = Math.Max(6f * S, 5f);
        var pts = HandleCenters(sel);
        for (int i = 0; i < 8; i += 2)
            if (Math.Abs(p.X - pts[i].X) <= tol && Math.Abs(p.Y - pts[i].Y) <= tol) return i;
        for (int i = 1; i < 8; i += 2)
            if (Math.Abs(p.X - pts[i].X) <= tol && Math.Abs(p.Y - pts[i].Y) <= tol) return i;
        float edge = Math.Max(3f * S, 3f);
        bool inX = p.X > sel.Left && p.X < sel.Right, inY = p.Y > sel.Top && p.Y < sel.Bottom;
        if (inX && Math.Abs(p.Y - (sel.Top - 1)) <= edge) return 1;
        if (inX && Math.Abs(p.Y - sel.Bottom) <= edge) return 5;
        if (inY && Math.Abs(p.X - (sel.Left - 1)) <= edge) return 7;
        if (inY && Math.Abs(p.X - sel.Right) <= edge) return 3;
        return -1;
    }

    static Cursor HandleCursor(int h) => h switch
    {
        0 or 4 => Cursors.SizeNWSE,
        2 or 6 => Cursors.SizeNESW,
        1 or 5 => Cursors.SizeNS,
        _ => Cursors.SizeWE,
    };

    void UpdateHoverWindow()
    {
        Rectangle? found = null;
        if (AppSettings.Current.DetectWindows && !hasSel)
            foreach (var w in windows)
                if (w.Contains(mouse)) { found = w; break; }
        hoverWindow = found;
    }

    // ───────────────────────────── invalidation ─────────────────────────────

    bool ChromeVisible => hasSel && !(dragMoved && drag is DragMode.NewSelection or DragMode.MoveSelection or DragMode.Resize);

    void InvalidateRect(Rectangle r)
    {
        if (r.Width > 0 && r.Height > 0) Invalidate(r);
    }

    void InvalidateRect(RectangleF r, int pad) => InvalidateRect(Ui.Outer(r, pad));

    void InvalidateShape(RectangleF before, RectangleF after)
    {
        var r = Ui.Outer(RectangleF.Union(before, after), Px(8) + 2);
        InvalidateRect(Rectangle.Intersect(r, Rectangle.Inflate(sel, 2, 2)));
    }

    void InvalidateGroupOf(ToolButton b)
    {
        var group = tools.GroupOf(b) ?? actions.GroupOf(b) ?? textBar.GroupOf(b);
        if (group != null) InvalidateRect(group.Bounds, 2);
    }

    /// <summary>Repaints whatever moved: the selection frame and its chrome, the loupe, the flash and the tooltip.</summary>
    void UpdateView(bool frameDirty = false)
    {
        if (closing) return;
        var frame = FrameBounds();
        if (frameDirty || frame != lastFrame)
        {
            InvalidateRect(lastFrame);
            InvalidateRect(frame);
            lastFrame = frame;
        }
        var mag = MagnifierVisible ? MagnifierBounds() : Rectangle.Empty;
        if (mag != lastMag || !mag.IsEmpty)
        {
            InvalidateRect(lastMag);
            InvalidateRect(mag);
            lastMag = mag;
        }
        var flash = flashText != null ? Ui.Outer(FlashRect(), Px(14)) : Rectangle.Empty;
        if (flash != lastFlash)
        {
            InvalidateRect(lastFlash);
            InvalidateRect(flash);
            lastFlash = flash;
        }
        var tip = ChromeVisible && tooltipVisible && hoverButton != null ? Ui.Outer(TooltipRect(hoverButton), Px(14)) : Rectangle.Empty;
        if (tip != lastTip)
        {
            InvalidateRect(lastTip);
            InvalidateRect(tip);
            lastTip = tip;
        }
    }

    Rectangle FrameBounds()
    {
        if (hasSel)
        {
            LayoutChrome();
            var r = Rectangle.Inflate(sel, Px(10) + 2, Px(10) + 2);
            r = Ui.Union(r, Ui.Outer(SizeLabelRect(sel), Px(14)));
            if (ChromeVisible)
            {
                r = Ui.Union(r, Ui.Outer(tools.Bounds, Px(14)));
                r = Ui.Union(r, Ui.Outer(actions.Bounds, Px(14)));
                if (TextBarVisible) r = Ui.Union(r, Ui.Outer(textBar.Bounds, Px(14)));
                if (AiOpen) r = Ui.Union(r, Ui.Outer(aiRect, Px(16)));
                if (paletteOpen) r = Ui.Union(r, Ui.Outer(paletteRect, Px(16)));
            }
            return r;
        }
        if (hoverWindow is Rectangle hw)
            return Ui.Union(Rectangle.Inflate(hw, 4, 4), Ui.Outer(SizeLabelRect(hw), Px(14)));
        return Rectangle.Empty;
    }

    /// <summary>Lightshot's layout: tools to the right of the selection (bottom-aligned), actions below it (right-aligned).</summary>
    void LayoutChrome()
    {
        var mon = MonitorAt(Center(sel));
        float gap = 10 * S;
        var vs = tools.Measure(S);
        var hs = actions.Measure(S);

        float vx = sel.Right + gap, vy = sel.Bottom - vs.Height;
        if (vx + vs.Width > mon.Right - 2) vx = sel.Left - gap - vs.Width;
        if (vx < mon.Left + 2) vx = sel.Right - gap - vs.Width;
        vy = Math.Clamp(vy, mon.Top + gap, Math.Max(mon.Top + gap, mon.Bottom - vs.Height - gap));

        float hx = sel.Right - hs.Width, hy = sel.Bottom + gap;
        if (hy + hs.Height > mon.Bottom - 2) hy = sel.Top - gap - hs.Height;
        if (hy < mon.Top + 2) hy = sel.Bottom - gap - hs.Height;
        hx = Math.Clamp(hx, mon.Left + gap, Math.Max(mon.Left + gap, mon.Right - hs.Width - gap));

        var vr = new RectangleF(vx, vy, vs.Width, vs.Height);
        var hr = new RectangleF(hx, hy, hs.Width, hs.Height);
        if (vr.IntersectsWith(hr)) vy = Math.Max(mon.Top + gap, hr.Top - gap - vs.Height);

        tools.Layout(new PointF(MathF.Round(vx), MathF.Round(vy)), S);
        actions.Layout(new PointF(MathF.Round(hx), MathF.Round(hy)), S);
        tools.Anchor = actions.Anchor = sel;
        if (TextBarVisible) LayoutTextBar();
        if (aiMode != null) LayoutAiPrompt();
        foreach (var g in Bars.SelectMany(b => b.Groups)) g.Tone = glass.ToneAt(g.Bounds, sel);
        if (paletteOpen) LayoutPalette();
    }

    /// <summary>The toolbars currently on screen.</summary>
    IEnumerable<Toolbar> Bars
    {
        get
        {
            if (!ChromeVisible) yield break;
            yield return tools;
            yield return actions;
            if (TextBarVisible) yield return textBar;
        }
    }

    ToolButton? HitButton(Point p)
    {
        foreach (var bar in Bars)
            if (bar.HitTest(p) is { } b) return b;
        return null;
    }

    bool OnChrome(Point p) => Bars.Any(b => b.GroupAt(p) != null);

    Toolbar? BarOf(ToolButton b)
    {
        foreach (var bar in new[] { tools, actions, textBar })
            if (bar.GroupOf(b) != null) return bar;
        return null;
    }
}
