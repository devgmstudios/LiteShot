using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using static LiteShot.Native;

namespace LiteShot.Overlay;

/// <summary>
/// Scrolling capture: starting one from the overlay (S, or its button), and editing what it stitched. The page is
/// taller than the screen, so the overlay opens it as a document: the screen as it was, with the area that scrolled
/// grown downwards to the page's full height, and the mouse wheel scrolls the view. Everything stays in the
/// capture's coordinates, as in a normal capture; only painting, invalidation and mouse messages are shifted by
/// <see cref="scrollY"/>.
/// </summary>
internal sealed partial class CaptureOverlay
{
    const int PageTile = 3000, PageTileOverlap = 240;

    ToolButton scrollButton = null!;
    /// <summary>The stitched page, in the capture's coordinates, when this overlay edits one.</summary>
    Rectangle? page;
    int scrollY, scrollMax, pageMonitor;
    Rectangle[] screenMonitors = Array.Empty<Rectangle>();
    bool pickForScrolling;

    /// <summary>Opens the result of a scrolling capture of <paramref name="area"/> (screen pixels) for editing.</summary>
    public static CaptureOverlay ForPage(TrayApp host, Bitmap pageImage, Rectangle area, Bitmap backdrop, Rectangle virt, string? note)
    {
        int left = area.X - virt.X, top = area.Y - virt.Y;
        int extra = Math.Max(0, pageImage.Height - area.Height);
        var canvas = new Bitmap(virt.Width, virt.Height + extra, PixelFormat.Format32bppPArgb);
        canvas.SetResolution(96, 96);
        using (var g = Graphics.FromImage(canvas))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.Clear(Color.FromArgb(24, 24, 27));
            // The screen above the bottom of the area stays where it was; below it moves down by the page's extra height.
            int split = Math.Min(virt.Height, top + area.Height);
            g.DrawImage(backdrop, new Rectangle(0, 0, virt.Width, split), 0, 0, virt.Width, split, GraphicsUnit.Pixel);
            g.DrawImage(backdrop, new Rectangle(0, split + extra, virt.Width, virt.Height - split), 0, split, virt.Width, virt.Height - split, GraphicsUnit.Pixel);
            g.DrawImage(pageImage, new Rectangle(left, top, pageImage.Width, pageImage.Height), 0, 0, pageImage.Width, pageImage.Height, GraphicsUnit.Pixel);
        }
        var o = new CaptureOverlay(host, canvas, virt, Array.Empty<Rectangle>(), new Rectangle(area.X, area.Y, pageImage.Width, pageImage.Height));
        o.page = new Rectangle(left, top, pageImage.Width, pageImage.Height);
        o.scrollMax = extra;
        o.screenMonitors = o.monitors.ToArray();
        o.pageMonitor = o.MonitorIndexAt(o.page.Value.Location);
        // A page can't be scrolled again; its own button would only confuse.
        if (o.actions.GroupOf(o.scrollButton) is { } group) o.actions.Groups.Remove(group);
        // Inside the page's top, clear of the toolbars around it.
        o.flashAnchor = new Point(left + Math.Min(pageImage.Width / 3, 240), top + Math.Min(area.Height / 2, 140));
        o.LongFlash(note == null ? "Scroll to see the whole page" : $"{note} · scroll to see the page", o.flashAnchor);
        return o;
    }

    /// <summary>Opened from the tray menu: the scrolling starts as soon as an area or a window is picked.</summary>
    public void PickForScrolling()
    {
        pickForScrolling = true;
        LongFlash("Select the area to scroll, or click a window", mouse);
    }

    /// <summary>A flash that stays up long enough to read a sentence.</summary>
    void LongFlash(string text, Point anchor)
    {
        ShowFlash(text);
        flashAnchor = anchor;
        flashTimer.Interval = 2600;
        flashTimer.Tick += Restore;
        void Restore(object? sender, EventArgs e)
        {
            flashTimer.Tick -= Restore;
            flashTimer.Interval = 1100;
        }
    }

    void StartScrollCapture()
    {
        if (closing || page != null) return;
        if (host == null) { ShowFlash("Scrolling capture needs the tray app"); return; }
        var area = hasSel ? sel : hoverWindow ?? Rectangle.Empty;
        if (area.IsEmpty) { ShowFlash("Select the area that scrolls first"); return; }
        if (area.Width < Px(80) || area.Height < Px(80)) { ShowFlash("Select a bigger area to scroll"); return; }
        var screen = new Rectangle(area.X + virt.X, area.Y + virt.Y, area.Width, area.Height);
        CloseOverlay();
        host.StartScrollCapture(screen);
    }

    // ───────────────────────────── scrolling the page ─────────────────────────────

    void ScrollTo(int y)
    {
        y = Math.Clamp(y, 0, scrollMax);
        if (y == scrollY || closing) return;
        int dy = y - scrollY;
        scrollY = y;
        for (int i = 0; i < monitors.Length && i < screenMonitors.Length; i++)
            monitors[i] = new Rectangle(screenMonitors[i].X, screenMonitors[i].Y + y, screenMonitors[i].Width, screenMonitors[i].Height);
        HideTooltip();
        Invalidate();
        lastFrame = lastMag = lastFlash = lastTip = lastBrush = Rectangle.Empty;
        // The pointer stays put on the screen, so it's somewhere else on the page now: hovering, drawing or dragging go on from there.
        OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, mouse.X, mouse.Y + dy, 0));
        UpdateView(true);
    }

    void ScrollBy(int dy) => ScrollTo(scrollY + dy);

    int ViewHeight => pageMonitor < screenMonitors.Length ? screenMonitors[pageMonitor].Height : client.Height - scrollMax;

    bool HandleScrollKey(Keys k)
    {
        int pageStep = (int)(ViewHeight * 0.85f);
        switch (k)
        {
            case Keys.PageDown: case Keys.Space: ScrollBy(pageStep); return true;
            case Keys.PageUp: case Keys.Shift | Keys.Space: ScrollBy(-pageStep); return true;
            case Keys.Home: case Keys.Control | Keys.Home: ScrollTo(0); return true;
            case Keys.End: case Keys.Control | Keys.End: ScrollTo(scrollMax); return true;
        }
        return false;
    }

    /// <summary>
    /// The window shows the capture from <see cref="scrollY"/> down, so mouse positions are moved onto the capture
    /// before WinForms sees them. The wheel scrolls; Ctrl+wheel still sizes the tool.
    /// </summary>
    bool ScrollMessage(ref Message m)
    {
        const int WM_MOUSEMOVE = 0x0200, WM_MOUSEWHEEL = 0x020A, WM_XBUTTONDBLCLK = 0x020D, MK_CONTROL = 0x0008;
        if (m.Msg == WM_MOUSEWHEEL && ((long)m.WParam & MK_CONTROL) == 0)
        {
            int delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
            ScrollBy(-delta * Px(100) / 120);
            m.Result = IntPtr.Zero;
            return true;
        }
        if (m.Msg >= WM_MOUSEMOVE && m.Msg <= WM_XBUTTONDBLCLK && m.Msg != WM_MOUSEWHEEL && scrollY != 0)
        {
            long lp = (long)m.LParam;
            int x = (short)(lp & 0xFFFF), y = (short)((lp >> 16) & 0xFFFF) + scrollY;
            m.LParam = (IntPtr)((y << 16) | (x & 0xFFFF));
        }
        return false;
    }

    /// <summary>A slim thumb at the right edge of the page's screen shows where the view is on the page.</summary>
    void DrawScrollBar(Graphics g)
    {
        if (scrollMax <= 0 || pageMonitor >= monitors.Length) return;
        var mon = monitors[pageMonitor];
        float track = mon.Height - 16 * S, thumb = Math.Max(40 * S, track * mon.Height / (mon.Height + scrollMax));
        float y = mon.Top + 8 * S + (track - thumb) * scrollY / scrollMax;
        var r = new RectangleF(mon.Right - 11 * S, y, 6 * S, thumb);
        using var path = Ui.RoundRect(r, r.Width / 2);
        using var fill = new SolidBrush(Color.FromArgb(215, 255, 255, 255));
        using var edge = new Pen(Color.FromArgb(110, 0, 0, 0), 1f);
        g.FillPath(fill, path);
        g.DrawPath(edge, path);
    }

    // ───────────────────────────── hiding personal info on a page ─────────────────────────────

    /// <summary>
    /// Looks for personal details on a stitched page a few thousand pixels at a time, since text recognition has a
    /// size limit. Tiles overlap, so a line cut by one tile is whole in the next; the same find twice counts once.
    /// </summary>
    Task<List<Redaction>> ScanPage(List<string> keywords)
    {
        var area = page!.Value;
        var tiles = new List<(Bitmap Tile, int Y)>();
        for (int y = area.Top; ; y += PageTile - PageTileOverlap)
        {
            int h = Math.Min(PageTile, area.Bottom - y);
            tiles.Add((shot.Clone(new Rectangle(area.X, y, area.Width, h), shot.PixelFormat), y));
            if (y + h >= area.Bottom) break;
        }
        return Task.Run(async () =>
        {
            var all = new List<Redaction>();
            foreach (var (tile, y) in tiles)
                using (tile)
                    foreach (var r in await PersonalInfo.FindAsync(tile, keywords, faces: true).ConfigureAwait(false))
                    {
                        var moved = r with { Area = new Rectangle(r.Area.X + area.X, r.Area.Y + y, r.Area.Width, r.Area.Height) };
                        if (!all.Any(a => a.Face == moved.Face && Overlap(a.Area, moved.Area) > 0.5f)) all.Add(moved);
                    }
            return all;
        });
    }
}
