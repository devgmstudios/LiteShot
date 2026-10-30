using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using static LiteShot.Native;

namespace LiteShot;

/// <summary>
/// Scrolling capture: turns the mouse wheel over an area of the screen, grabs a frame each time the page comes to
/// rest and stitches the frames into one tall image (<see cref="ScrollStitcher"/>). It stops at the end of the page,
/// at the height limit, or when the user presses Esc or clicks. Keys and clicks are held back from the page meanwhile,
/// so the click that stops it can't follow a link.
/// </summary>
internal static class ScrollCapture
{
    /// <summary>The stitched page, the screen as it was when the capture began, and how the capture ended.</summary>
    public sealed record Result(Bitmap Page, Bitmap Backdrop, string? Note);

    const int Poll = 40;

    public static async Task<Result> RunAsync(Rectangle area)
    {
        var virt = SystemInformation.VirtualScreen;
        // The editor shows the page in a window as tall as the screen plus the page's extra height, and mouse
        // positions in it must fit in 16 bits.
        var stitcher = new ScrollStitcher(Math.Min(ScrollStitcher.DefaultMaxHeight, 32_000 - virt.Height + area.Height));
        Bitmap? backdrop = null;
        var home = Cursor.Position;
        var center = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
        bool stopped = false;
        var previousInterceptor = KeyboardHook.Interceptor;
        // While it runs, keys don't reach the page being captured; Esc stops it.
        KeyboardHook.Interceptor = k => { if ((k & Keys.KeyCode) == Keys.Escape) stopped = true; };
        LowLevelKeyboardProc? clicks = null;
        IntPtr hook = IntPtr.Zero;
        using var pill = new ScrollPill(area);
        string? note = null;
        try
        {
            // A click that started this may still be held; it mustn't count as the click that stops it.
            for (int i = 0; i < 50 && MouseDown(); i++) await Task.Delay(20);
            await Task.Delay(200);   // the overlay is off the screen
            backdrop = ScreenCapture.Capture(virt, false);
            clicks = (code, message, info) =>
            {
                if (code >= 0 && (int)message is >= 0x0201 and <= 0x020D and not 0x020A and not 0x020E)
                {
                    stopped = true;
                    return 1;
                }
                return CallNextHookEx(hook, code, message, info);
            };
            hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, clicks, GetModuleHandle(null), 0);
            Cursor.Position = center;
            pill.Show();
            await Task.Delay(150);   // the page has reacted to the pointer
            Bitmap frame = ScreenCapture.Capture(area, false);
            stitcher.Add(frame);

            int notches = 1;
            float perNotch = 0;
            bool Stop() => stopped || MouseDown();
            while (!Stop())
            {
                int expected = perNotch > 0 ? (int)(perNotch * notches) : 0;
                var (next, step) = await StepAsync(area, center, frame, notches, stitcher, expected, Stop);
                if (step == StitchStep.Same)
                {
                    // Pages that load more as you scroll need a moment, and another turn of the wheel.
                    next.Dispose();
                    await Task.Delay(400);
                    (next, step) = await StepAsync(area, center, frame, notches, stitcher, expected, Stop);
                }
                if (step == StitchStep.Lost && !Stop())
                {
                    // Perhaps still moving: look again once it has had time to settle.
                    next.Dispose();
                    await Task.Delay(300);
                    next = ScreenCapture.Capture(area, false);
                    step = stitcher.Add(next, expected);
                }
                if (step == StitchStep.Lost && notches > 1 && !Stop())
                {
                    // Scrolled past the last frame's bottom: go back and take smaller steps.
                    next.Dispose();
                    Wheel(center, notches);
                    using (await SettleAsync(area, frame, Stop)) { }
                    notches = Math.Max(1, notches / 2);
                    continue;
                }

                frame.Dispose();
                frame = next;
                if (step is StitchStep.Added or StitchStep.Full)
                {
                    perNotch = (float)stitcher.LastShift / notches;
                    int moving = area.Height - stitcher.Header - stitcher.Footer;
                    notches = Math.Clamp((int)(moving * 0.6f / Math.Max(1f, perNotch)), 1, 12);
                    pill.SetHeight(stitcher.Height);
                }
                if (step == StitchStep.Full) { note = $"Stopped at the {stitcher.MaxHeight:N0} px limit"; break; }
                if (step == StitchStep.Same) { note = stitcher.Frames > 1 ? null : "This area didn't scroll"; break; }
                if (step == StitchStep.Lost) { note = "Stopped where the page changed as it scrolled"; break; }
            }
            frame.Dispose();
            if (note == null && Stop()) note = "Stopped";
            return new Result(stitcher.Compose(), backdrop!, note);
        }
        catch
        {
            backdrop?.Dispose();
            throw;
        }
        finally
        {
            KeyboardHook.Interceptor = previousInterceptor;
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            GC.KeepAlive(clicks);
            pill.Close();
            Cursor.Position = home;
        }
    }

    /// <summary>Scrolls down by <paramref name="notches"/>, waits for the page to come to rest and adds the frame.</summary>
    static async Task<(Bitmap Frame, StitchStep Step)> StepAsync(Rectangle area, Point center, Bitmap last, int notches,
        ScrollStitcher stitcher, int expected, Func<bool> stop)
    {
        Wheel(center, -notches);
        var frame = await SettleAsync(area, last, stop);
        return (frame, stitcher.Add(frame, expected));
    }

    /// <summary>
    /// Grabs frames until two in a row match, after the page has moved away from <paramref name="before"/>. Smooth
    /// scrolling takes a few hundred milliseconds; a page that doesn't move at all is taken as it is after a short wait.
    /// </summary>
    static async Task<Bitmap> SettleAsync(Rectangle area, Bitmap before, Func<bool> stop)
    {
        var started = Environment.TickCount64;
        var last = ScreenCapture.Capture(area, false);
        bool moved = !ScrollStitcher.SamePixels(last, before);
        while (Environment.TickCount64 - started < 1500 && !stop())
        {
            await Task.Delay(Poll);
            var next = ScreenCapture.Capture(area, false);
            bool still = ScrollStitcher.SamePixels(next, last);
            moved |= !ScrollStitcher.SamePixels(next, before);
            last.Dispose();
            last = next;
            if (still && (moved || Environment.TickCount64 - started > 350)) break;
        }
        return last;
    }

    static bool MouseDown() =>
        (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0 || (GetAsyncKeyState(0x04) & 0x8000) != 0;

    // ───────────────────────────── the wheel ─────────────────────────────

    const uint INPUT_MOUSE = 0, MOUSEEVENTF_WHEEL = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public MOUSEINPUT mi; }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);

    /// <summary>Turns the wheel over <paramref name="at"/>: negative notches scroll down. Windows sends it to whatever is under the pointer.</summary>
    static void Wheel(Point at, int notches)
    {
        if (Cursor.Position != at) Cursor.Position = at;
        var inputs = new INPUT[Math.Abs(notches)];
        for (int i = 0; i < inputs.Length; i++)
            inputs[i] = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { mouseData = unchecked((uint)(Math.Sign(notches) * 120)), dwFlags = MOUSEEVENTF_WHEEL } };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
}

/// <summary>"Scrolling… Esc to stop": a small pill beside the area being captured. It never takes focus and keeps itself out of the frames.</summary>
internal sealed class ScrollPill : Form
{
    readonly float s;
    readonly Font font;
    string text = "Scrolling…  Esc to stop";

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    public ScrollPill(Rectangle area)
    {
        s = ScreenCapture.ScaleAt(new Point(area.X + area.Width / 2, area.Y + area.Height / 2));
        font = Ui.MakeFont(13 * s, semibold: true);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        Text = "LiteShot";
        var size = Ui.Measure("Scrolling…  12,345 px  ·  Esc to stop", font);
        Size = new Size((int)(size.Width + 48 * s), (int)(size.Height + 18 * s));
        // Above the area if there's room on its screen, else below it, else just inside its top edge.
        var screen = Screen.FromRectangle(area).WorkingArea;
        int gap = (int)(10 * s), x = Math.Clamp(area.X + area.Width / 2 - Width / 2, screen.Left + gap, Math.Max(screen.Left + gap, screen.Right - Width - gap));
        int y = area.Top - gap - Height >= screen.Top ? area.Top - gap - Height
            : area.Bottom + gap + Height <= screen.Bottom ? area.Bottom + gap
            : area.Top + gap;
        Location = new Point(x, y);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= (int)WS_EX_TOOLWINDOW | 0x08000000 /* WS_EX_NOACTIVATE */ | 0x8 /* WS_EX_TOPMOST */ | (int)WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowChrome(Handle, dark: true, DWMWCP_ROUND, mica: false);
        try { SetWindowDisplayAffinity(Handle, 0x11 /* WDA_EXCLUDEFROMCAPTURE */); }
        catch (EntryPointNotFoundException) { }
    }

    public void SetHeight(int px)
    {
        text = $"Scrolling…  {px:N0} px  ·  Esc to stop";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.FromArgb(32, 32, 36));
        float d = 8 * s;
        using (var dot = new SolidBrush(SystemTheme.Accent)) g.FillEllipse(dot, 14 * s, (Height - d) / 2, d, d);
        var size = Ui.Measure(text, font);
        Ui.DrawText(g, text, font, Color.White, new PointF(14 * s + d + 8 * s, (Height - size.Height) / 2));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) font.Dispose();
        base.Dispose(disposing);
    }
}
