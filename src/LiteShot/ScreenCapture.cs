using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using static LiteShot.Native;

namespace LiteShot;

internal static class ScreenCapture
{
    /// <summary>Captures a screen rectangle (physical pixels) into a premultiplied bitmap, which GDI+ draws fastest.</summary>
    public static Bitmap Capture(Rectangle screen, bool includeCursor)
    {
        var bmp = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppPArgb);
        bmp.SetResolution(96, 96);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(screen.Location, Point.Empty, screen.Size, CopyPixelOperation.SourceCopy);
        if (includeCursor) DrawCursor(g, screen.Location);
        return bmp;
    }

    static void DrawCursor(Graphics g, Point origin)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & 1) == 0) return;
        int hx = 0, hy = 0;
        if (GetIconInfo(ci.hCursor, out var ii))
        {
            hx = ii.xHotspot; hy = ii.yHotspot;
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
        var hdc = g.GetHdc();
        try { DrawIconEx(hdc, ci.ptScreenPos.X - origin.X - hx, ci.ptScreenPos.Y - origin.Y - hy, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3); }
        finally { g.ReleaseHdc(hdc); }
    }

    /// <summary>Visible top-level windows in z-order (topmost first), followed by the monitors as a fallback.</summary>
    public static List<Rectangle> GetWindowRects()
    {
        var list = new List<Rectangle>();
        var cls = new StringBuilder(256);
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0) return true;

            cls.Clear();
            GetClassName(h, cls, cls.Capacity);
            string name = cls.ToString();
            if (name is "Progman" or "WorkerW") return true;
            bool shell = name is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "#32768";
            if (!shell && GetWindowTextLength(h) == 0) return true;

            if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0)
                GetWindowRect(h, out r);
            var rect = r.ToRectangle();
            if (rect.Width >= 8 && rect.Height >= 8) list.Add(rect);
            return true;
        }, IntPtr.Zero);

        foreach (var s in Screen.AllScreens) list.Add(s.Bounds);
        return list;
    }

    public static float ScaleAt(Point screenPoint)
    {
        try
        {
            var mon = MonitorFromPoint(new POINT { X = screenPoint.X, Y = screenPoint.Y }, 2);
            if (GetDpiForMonitor(mon, 0, out uint dpi, out _) == 0) return dpi / 96f;
        }
        catch (DllNotFoundException) { }
        return 1f;
    }
}
