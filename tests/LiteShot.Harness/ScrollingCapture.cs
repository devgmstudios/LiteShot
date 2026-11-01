using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Reflection;
using LiteShot.Overlay;

namespace LiteShot.Harness;

/// <summary>
/// Scrolling capture: <see cref="ScrollStitcher"/> on synthetic tall pages seen through a window that scrolls (with a
/// moving scrollbar, a sticky header and footer, an early end, identical frames), and the overlay's page mode.
/// </summary>
internal static class ScrollingCapture
{
    const int W = 900, H = 640;

    public static void Run(string outDir, Action<bool, string> check)
    {
        using var article = Article(W, 5200);

        // A plain page, scrolled by uneven steps.
        {
            var s = new ScrollStitcher();
            var offsets = new[] { 0, 230, 610, 905, 1390, 1700 };
            foreach (int y in offsets)
                using (var f = Frame(article, y, header: 0, footer: 0))
                    check(s.Add(f) == StitchStep.Added, $"scroll: frame at {y} is added");
            check(s.LastShift == 310, $"scroll: last step measured as {s.LastShift} px (310)");
            using var result = s.Compose();
            using var expected = Expected(article, 1700 + H, header: 0, footer: 0);
            check(result.Size == expected.Size, $"scroll: plain page stitched to {result.Size.Height} px ({expected.Height})");
            check(SameIgnoringScrollbar(result, expected), "scroll: plain page matches the page pixel for pixel");
            result.Save(Path.Combine(outDir, "scroll-01-plain.png"), ImageFormat.Png);
        }

        // A sticky header and a cookie bar at the bottom: each appears once, at the top and at the bottom.
        {
            const int header = 72, footer = 54;
            var s = new ScrollStitcher();
            int[] offsets = { 0, 300, 640, 900, 1215 };
            foreach (int y in offsets)
                using (var f = Frame(article, y, header, footer))
                    check(s.Add(f) == StitchStep.Added, $"scroll: sticky frame at {y} is added");
            check(s.Header >= header && s.Footer >= footer, $"scroll: header {s.Header} px and footer {s.Footer} px stay put");
            using var result = s.Compose();
            using var expected = Expected(article, 1215 + H - header - footer, header, footer);
            check(result.Size == expected.Size, $"scroll: sticky page stitched to {result.Size.Height} px ({expected.Height})");
            check(SameIgnoringScrollbar(result, expected), "scroll: sticky header and footer appear once, the content in between whole");
            check(CountRuns(result, HeaderColor) == 1 && CountRuns(result, FooterColor) == 1, "scroll: one header band and one footer band in the image");
            result.Save(Path.Combine(outDir, "scroll-02-sticky.png"), ImageFormat.Png);
        }

        // A web app: a fixed side menu (with a badge that keeps changing), a details panel, and the list between them
        // scrolling with its own scrollbar. Only the list lines up; the menu and panel appear once, not repeated.
        {
            using var list = Article(AppList, 5200, seed: 3);
            var s = new ScrollStitcher();
            int[] offsets = { 0, 280, 610, 900, 1240 };
            for (int i = 0; i < offsets.Length; i++)
                using (var f = AppFrame(list, offsets[i], badge: i))
                    check(s.Add(f) == StitchStep.Added, $"scroll: web app frame at {offsets[i]} is added");
            using var result = s.Compose();
            result.Save(Path.Combine(outDir, "scroll-03-web-app.png"), ImageFormat.Png);
            check(result.Height == offsets[^1] + H, $"scroll: web app stitched to {result.Height} px ({offsets[^1] + H})");
            using var listPart = Crop(result, AppNav, 0, AppList - 14, result.Height);
            using var expected = Crop(list, 0, 0, AppList - 14, result.Height);
            check(Pixels(listPart).AsSpan().SequenceEqual(Pixels(expected)), "scroll: the list between the menu and the panel is whole, in order");
            using var first = AppFrame(list, 0, badge: 0);
            bool menuOnce = true;
            for (int x = 0; x < AppNav && menuOnce; x += 3)
                for (int y = H; y < result.Height && menuOnce; y += 5)
                    menuOnce = result.GetPixel(x, y) == result.GetPixel(x, H);
            check(menuOnce, "scroll: the side menu isn't repeated down the page");
            check(Enumerable.Range(0, 60).All(y => result.GetPixel(40, 100 + y) == first.GetPixel(40, 100 + y)), "scroll: the side menu shows as on screen at the top");
        }

        // A page that ends early: the last step moves less than asked, then nothing moves.
        {
            using var shortPage = Article(W, 1500);
            int end = 1500 - H;
            var s = new ScrollStitcher();
            StitchStep last = StitchStep.Added;
            int frames = 0;
            for (int y = 0; ; y += 380)
            {
                using var f = Frame(shortPage, Math.Min(y, end), 0, 0);
                last = s.Add(f, expectedShift: 380);
                frames++;
                if (last != StitchStep.Added || frames > 10) break;
            }
            check(last == StitchStep.Same, $"scroll: the end of the page is noticed ({last})");
            check(s.LastShift == end - 760, $"scroll: the last step is the short one ({s.LastShift} px)");
            using var result = s.Compose();
            check(result.Height == 1500, $"scroll: short page is its full {result.Height} px, no more");
            using var expected = Expected(shortPage, 1500, 0, 0);
            check(SameIgnoringScrollbar(result, expected), "scroll: short page matches pixel for pixel");
        }

        // Identical frames: nothing scrolled, so nothing is added; a blinking caret doesn't count as scrolling.
        {
            var s = new ScrollStitcher();
            using var a = Frame(article, 400, 0, 0);
            using var b = Frame(article, 400, 0, 0);
            s.Add(a);
            check(s.Add(b) == StitchStep.Same, "scroll: an identical frame is the end");
            using (var caret = Frame(article, 400, 0, 0))
            {
                using (var g = Graphics.FromImage(caret)) g.FillRectangle(Brushes.Black, 300, 200, 2, 6);
                check(s.Add(caret) == StitchStep.Same, "scroll: a blinking caret isn't scrolling");
            }
            check(s.Frames == 1 && s.Height == H, $"scroll: identical frames leave one frame ({s.Frames}, {s.Height} px)");
            using var result = s.Compose();
            check(SameIgnoringScrollbar(result, a), "scroll: one frame stitches to itself");
        }

        // Frames that don't line up are refused without spoiling what's there; a fresh frame still fits.
        {
            var s = new ScrollStitcher();
            using (var f = Frame(article, 0, 0, 0)) s.Add(f);
            using (var other = Article(W, H, seed: 7)) check(s.Add(other) == StitchStep.Lost, "scroll: an unrelated frame is refused");
            using (var far = Frame(article, 2000, 0, 0)) check(s.Add(far) == StitchStep.Lost, "scroll: a frame scrolled past the last one's bottom is refused");
            using (var f = Frame(article, 300, 0, 0)) check(s.Add(f) == StitchStep.Added && s.Height == H + 300, "scroll: the next good frame still fits");
        }

        // The height limit: long pages stop early, with what fit.
        {
            var s = new ScrollStitcher(maxHeight: 2000);
            StitchStep step = StitchStep.Added;
            for (int y = 0; y <= 4000 && step == StitchStep.Added; y += 400)
                using (var f = Frame(article, y, 0, 0)) step = s.Add(f);
            check(step == StitchStep.Full, $"scroll: the height limit stops it ({step})");
            using var result = s.Compose();
            check(result.Height == 2000, $"scroll: capped at {result.Height} px");
            using var expected = Expected(article, 2000, 0, 0);
            check(SameIgnoringScrollbar(result, expected), "scroll: what fit under the limit is right");
        }

        // Speed: a 1,000-row frame against the last one, as on a big monitor.
        {
            using var tall = Article(1600, 6000);
            var s = new ScrollStitcher();
            using (var f = Frame(tall, 0, 0, 0, 1600, 1000)) s.Add(f);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using (var f = Frame(tall, 600, 0, 0, 1600, 1000)) s.Add(f);
            check(s.LastShift == 600 && watch.ElapsedMilliseconds < 400, $"scroll: lining up 1600×1000 frames took {watch.ElapsedMilliseconds} ms");
        }

        PageMode(outDir, article, check);
    }

    /// <summary>The overlay opens a stitched page as a document taller than the screen, and scrolls it.</summary>
    static void PageMode(string outDir, Bitmap article, Action<bool, string> check)
    {
        var virt = SystemInformation.VirtualScreen;
        var area = new Rectangle(virt.X + virt.Width / 2 - W / 2, virt.Y + Math.Max(40, virt.Height / 6), W, Math.Min(H, virt.Height / 2));
        using var backdrop = Program.SyntheticDesktop(virt.Size);
        using var pageImage = Expected(article, 3600, 0, 0);
        var o = CaptureOverlay.ForPage(null!, pageImage, area, backdrop, virt, null);
        try
        {
            var page = Get<Rectangle?>(o, "page")!.Value;
            int scrollMax = Get<int>(o, "scrollMax");
            check(Get<Rectangle>(o, "sel") == page && page.Height == 3600, $"page: the whole page is selected ({page.Size})");
            check(scrollMax == 3600 - area.Height, $"page: scrolls {scrollMax} px");
            Snap(o, Path.Combine(outDir, "scroll-10-page-top.png"));
            var actions = Get<Toolbar>(o, "actions");
            var mons = Get<Rectangle[]>(o, "monitors");
            int pm = Get<int>(o, "pageMonitor");
            check(mons[pm].Contains(Rectangle.Round(actions.Bounds)), "page: the actions stay on screen at the top of a tall page");
            check(!actions.Buttons.Any(b => b.Tip == "Scrolling capture"), "page: no scrolling-capture button on a page");

            // The wheel scrolls; mouse positions land on the page where it now is.
            var wheel = Message.Create(IntPtr.Zero, 0x020A, (IntPtr)(-120 << 16), IntPtr.Zero);
            var args = new object[] { wheel };
            Invoke(o, "ScrollMessage", args);
            int scrolled = Get<int>(o, "scrollY");
            check(scrolled > 0, $"page: the wheel scrolls down ({scrolled} px)");
            var move = Message.Create(IntPtr.Zero, 0x0200, IntPtr.Zero, (IntPtr)((50 << 16) | 100));
            args = new object[] { move };
            Invoke(o, "ScrollMessage", args);
            var lp = (long)((Message)args[0]).LParam;
            check((short)(lp >> 16) == 50 + scrolled && (short)(lp & 0xFFFF) == 100, "page: the pointer lands where the page is now");

            Key(o, Keys.End);
            check(Get<int>(o, "scrollY") == scrollMax, "page: End goes to the bottom");
            actions = Get<Toolbar>(o, "actions");
            check(mons[pm].Contains(Rectangle.Round(actions.Bounds)), "page: the actions are on screen at the bottom too");

            // Draw near the bottom of the page, where only scrolling can reach.
            Key(o, Keys.R);
            var from = new Point(page.X + 120, page.Bottom - 300);
            var to = new Point(page.X + 520, page.Bottom - 120);
            Drag(o, from, to);
            var shapes = Get<List<Annotation>>(o, "shapes");
            check(shapes.Count == 1 && Rectangle.Round(shapes[0].Bounds).IntersectsWith(new Rectangle(from, new Size(400, 180))), "page: a rectangle drawn at the bottom of the page");
            Snap(o, Path.Combine(outDir, "scroll-11-page-bottom.png"));

            using (var result = (Bitmap)Invoke(o, "RenderResult")!)
            {
                check(result.Size == pageImage.Size, $"page: the result is the whole page ({result.Size})");
                check(SameIgnoringScrollbar(Crop(result, 0, 0, W, 2000), Crop(pageImage, 0, 0, W, 2000)), "page: the result keeps the page's pixels");
                result.Save(Path.Combine(outDir, "scroll-12-page-result.png"), ImageFormat.Png);
            }

            Key(o, Keys.Home);
            check(Get<int>(o, "scrollY") == 0, "page: Home goes back to the top");
            Key(o, Keys.Control | Keys.A);
            check(Get<Rectangle>(o, "sel") == page, "page: Ctrl+A selects the whole page");
        }
        finally { o.Dispose(); }
    }

    // ───────────────────────────── synthetic pages ─────────────────────────────

    static readonly Color HeaderColor = Color.FromArgb(23, 92, 211), FooterColor = Color.FromArgb(46, 46, 52);

    /// <summary>A long article: headings, paragraphs of made-up text, coloured figures and rules.</summary>
    static Bitmap Article(int width, int height, int seed = 1)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var rnd = new Random(seed);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var body = new Font("Segoe UI", 15f, GraphicsUnit.Pixel);
        using var head = new Font("Segoe UI Semibold", 24f, GraphicsUnit.Pixel);
        string[] words = { "orbit", "lantern", "harbor", "quartz", "meadow", "signal", "copper", "ember", "glacier", "willow", "cobalt", "summit", "ribbon", "pixel", "canyon", "velvet" };
        int y = 24, section = 1;
        while (y < height)
        {
            if (rnd.Next(5) == 0)
            {
                g.DrawString($"Section {section++}: {words[rnd.Next(words.Length)]} notes", head, Brushes.Black, 40, y);
                y += 44;
            }
            else if (rnd.Next(6) == 0)
            {
                int h = rnd.Next(80, 180);
                using var fill = new LinearGradientBrush(new Rectangle(40, y, width - 120, h), Color.FromArgb(rnd.Next(256), rnd.Next(256), rnd.Next(256)), Color.FromArgb(rnd.Next(256), rnd.Next(256), rnd.Next(256)), 30f);
                g.FillRectangle(fill, 40, y, width - 120, h);
                y += h + 18;
            }
            else
            {
                for (int line = rnd.Next(2, 6); line > 0; line--)
                {
                    string text = string.Join(" ", Enumerable.Range(0, rnd.Next(6, 13)).Select(_ => words[rnd.Next(words.Length)]));
                    g.DrawString($"{y,5}  {text}", body, Brushes.DimGray, 40, y);
                    y += 24;
                }
                y += 14;
            }
        }
        return bmp;
    }

    /// <summary>The window at scroll position <paramref name="offset"/>: header, the page, footer, and a scrollbar that moves.</summary>
    static Bitmap Frame(Bitmap page, int offset, int header, int footer, int width = W, int height = H)
    {
        var frame = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(frame);
        int content = height - header - footer;
        g.DrawImage(page, new Rectangle(0, header, width, content), new Rectangle(0, offset, width, content), GraphicsUnit.Pixel);
        DrawBars(g, width, height, header, footer);
        int track = height - header - footer, thumb = Math.Max(30, track * content / page.Height);
        int ty = header + (track - thumb) * offset / Math.Max(1, page.Height - content);
        g.FillRectangle(Brushes.Gainsboro, width - 14, header, 14, track);
        g.FillRectangle(Brushes.Gray, width - 12, ty, 10, thumb);
        return frame;
    }

    const int AppNav = 200, AppList = 540;

    /// <summary>A web app window: side menu, the list at <paramref name="offset"/> with its scrollbar, and a details panel.</summary>
    static Bitmap AppFrame(Bitmap list, int offset, int badge)
    {
        var frame = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(frame);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe UI", 15f, GraphicsUnit.Pixel);
        g.Clear(Color.FromArgb(243, 244, 247));
        string[] menu = { "Inbox", "Leads", "Contacts", "Deals", "Reports", "Calendar", "Files", "Settings" };
        for (int i = 0; i < menu.Length; i++) g.DrawString(menu[i], font, Brushes.DimGray, 24, 24 + i * 44);
        using (var red = new SolidBrush(Color.FromArgb(220, 40 + badge * 30, 60))) g.FillRectangle(red, 150, 74, 26, 9);
        g.DrawLine(Pens.Silver, AppNav - 1, 0, AppNav - 1, H);
        g.DrawImage(list, new Rectangle(AppNav, 0, AppList, H), new Rectangle(0, offset, AppList, H), GraphicsUnit.Pixel);
        int track = H, thumb = Math.Max(30, track * H / list.Height), ty = (track - thumb) * offset / (list.Height - H);
        g.FillRectangle(Brushes.Gainsboro, AppNav + AppList - 14, 0, 14, track);
        g.FillRectangle(Brushes.Gray, AppNav + AppList - 12, ty, 10, thumb);
        int px = AppNav + AppList;
        g.FillRectangle(Brushes.White, px, 0, W - px, H);
        g.DrawLine(Pens.Silver, px, 0, px, H);
        string[] details = { "Owner", "Stage: Proposal", "Value: 12,400", "Next step", "Send the quote", "Tags", "renewal, q4" };
        for (int i = 0; i < details.Length; i++) g.DrawString(details[i], font, Brushes.Black, px + 14, 24 + i * 36);
        return frame;
    }

    /// <summary>What the stitched image should be: header, the page's first <paramref name="content"/> rows, footer.</summary>
    static Bitmap Expected(Bitmap page, int content, int header, int footer)
    {
        var bmp = new Bitmap(page.Width, header + content + footer, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.DrawImage(page, new Rectangle(0, header, page.Width, content), new Rectangle(0, 0, page.Width, content), GraphicsUnit.Pixel);
        DrawBars(g, page.Width, bmp.Height, header, footer);
        return bmp;
    }

    static void DrawBars(Graphics g, int width, int height, int header, int footer)
    {
        using var font = new Font("Segoe UI Semibold", 15f, GraphicsUnit.Pixel);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        if (header > 0)
        {
            using var fill = new SolidBrush(HeaderColor);
            g.FillRectangle(fill, 0, 0, width, header);
            g.DrawString("Northwind Docs     Guides     Reference     Changelog", font, Brushes.White, 24, header / 2 - 10);
        }
        if (footer > 0)
        {
            using var fill = new SolidBrush(FooterColor);
            g.FillRectangle(fill, 0, height - footer, width, footer);
            g.DrawString("This site uses cookies to remember your settings.   Accept   Decline", font, Brushes.White, 24, height - footer / 2 - 10);
        }
    }

    // ───────────────────────────── comparing ─────────────────────────────

    static int[] Pixels(Bitmap b)
    {
        var data = b.LockBits(new Rectangle(Point.Empty, b.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var px = new int[b.Width * b.Height];
            for (int y = 0; y < b.Height; y++) System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, px, y * b.Width, b.Width);
            return px;
        }
        finally { b.UnlockBits(data); }
    }

    /// <summary>Pixel for pixel, apart from the scrollbar strip at the right, which the stitcher pieces together from every frame.</summary>
    static bool SameIgnoringScrollbar(Bitmap a, Bitmap b)
    {
        if (a.Size != b.Size) return false;
        var pa = Pixels(a);
        var pb = Pixels(b);
        int cols = a.Width - 32;
        for (int y = 0; y < a.Height; y++)
            if (!pa.AsSpan(y * a.Width, cols).SequenceEqual(pb.AsSpan(y * a.Width, cols))) return false;
        return true;
    }

    /// <summary>How many separate bands of rows start with <paramref name="color"/>.</summary>
    static int CountRuns(Bitmap b, Color color)
    {
        int runs = 0;
        bool inside = false;
        for (int y = 0; y < b.Height; y++)
        {
            bool hit = b.GetPixel(2, y).ToArgb() == color.ToArgb();
            if (hit && !inside) runs++;
            inside = hit;
        }
        return runs;
    }

    static Bitmap Crop(Bitmap b, int x, int y, int w, int h) => b.Clone(new Rectangle(x, y, w, h), PixelFormat.Format32bppArgb);

    // ───────────────────────────── driving the overlay ─────────────────────────────

    static void Snap(CaptureOverlay o, string path)
    {
        var size = o.Bounds.Size;
        using var full = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(full))
            Invoke(o, "OnPaint", new PaintEventArgs(g, new Rectangle(Point.Empty, size)));
        full.Save(path, ImageFormat.Png);
    }

    static void Key(Control o, Keys k) => FindMethod(o.GetType(), "ProcessCmdKey", 2)!.Invoke(o, new object[] { new Message(), k });

    static void Drag(Control o, Point from, Point to)
    {
        Invoke(o, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, from.X, from.Y, 0));
        for (int i = 1; i <= 8; i++)
            Invoke(o, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, from.X + (to.X - from.X) * i / 8, from.Y + (to.Y - from.Y) * i / 8, 0));
        Invoke(o, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, to.X, to.Y, 0));
    }

    static MethodInfo? FindMethod(Type t, string name, int parameters)
    {
        for (var type = t; type != null; type = type.BaseType)
        {
            var m = type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .FirstOrDefault(x => x.Name == name && x.GetParameters().Length == parameters);
            if (m != null) return m;
        }
        return null;
    }

    static object? Invoke(object o, string name, params object[] args) =>
        (FindMethod(o.GetType(), name, args.Length) ?? throw new MissingMethodException(name)).Invoke(o, args);

    static T Get<T>(object o, string field) =>
        (T)o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o)!;
}
