using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text;
using System.Text.RegularExpressions;

namespace LiteShot.Harness;

/// <summary>
/// Scrolling capture for real (--live-scroll): it turns the wheel over a long text window and a long page in Edge,
/// on the actual screen. Then it reads the stitched images back with OCR, to check every numbered line is there
/// exactly once and in order, with the sticky header and the cookie bar shown once. It moves the mouse while it runs.
/// </summary>
internal static class LiveScrollCheck
{
    public static void Run(string outDir, Action<bool, string> check)
    {
        var form = new Form
        {
            Text = "LiteShot scroll test · text",
            StartPosition = FormStartPosition.Manual,
            FormBorderStyle = FormBorderStyle.FixedSingle,
            TopMost = true,   // above whatever else is open, so the wheel reaches it
            Bounds = new Rectangle(Screen.PrimaryScreen!.WorkingArea.Right - 1000, Screen.PrimaryScreen.WorkingArea.Top + 40, 960, 900),
        };
        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 12f), BackColor = Color.White,
            Text = string.Join("\r\n", Enumerable.Range(1, 300).Select(i => $"Line {i:000} {Words(i, 9)}")),
        };
        form.Controls.Add(box);
        form.Shown += async (_, _) =>
        {
            try
            {
                await Task.Delay(500);
                box.Select(0, 0);
                var area = box.RectangleToScreen(box.ClientRectangle);
                var r = await ScrollCapture.RunAsync(area);
                using (r.Page)
                {
                    r.Page.Save(Path.Combine(outDir, "live-scroll-text.png"), ImageFormat.Png);
                    Console.WriteLine($"text window: {r.Page.Width}×{r.Page.Height}, note: {r.Note ?? "-"}");
                    await CheckLines(r.Page, "Line ", 300, "text window", check);
                    r.Backdrop.Dispose();
                }
                form.WindowState = FormWindowState.Minimized;
                await EdgeAsync(outDir, check);
            }
            catch (Exception ex) { check(false, $"live scroll: {ex}"); }
            finally { form.Close(); }
        };
        Application.Run(form);
    }

    static async Task EdgeAsync(string outDir, Action<bool, string> check)
    {
        string edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
        if (!File.Exists(edge)) { check(false, "live scroll: Edge isn't installed"); return; }
        string dir = Path.Combine(outDir, "edge-scroll");
        Directory.CreateDirectory(dir);
        string html = Path.Combine(dir, "long.html");
        File.WriteAllText(html, Page(), Encoding.UTF8);
        var psi = new ProcessStartInfo(edge, $"--user-data-dir=\"{Path.Combine(dir, "profile")}\" --no-first-run --no-default-browser-check " +
            $"--window-position=40,40 --window-size=1000,900 --app=\"{new Uri(html).AbsoluteUri}\"") { UseShellExecute = false };
        using var process = Process.Start(psi)!;
        try
        {
            IntPtr window = IntPtr.Zero;
            // The app window may belong to another Edge process than the one started.
            for (int i = 0; i < 200 && window == IntPtr.Zero; i++)
            {
                await Task.Delay(150);
                foreach (var p in Process.GetProcessesByName("msedge"))
                    using (p)
                        if (window == IntPtr.Zero && p.MainWindowTitle.Contains("Scroll test")) window = p.MainWindowHandle;
            }
            if (window == IntPtr.Zero) { check(false, "live scroll: Edge's window didn't open"); return; }
            Native.SetWindowPos(window, Native.HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 /* SWP_NOSIZE | SWP_NOMOVE */);
            Native.SetForegroundWindow(window);
            await Task.Delay(1500);   // the page lays out and the fonts load
            Native.GetWindowRect(window, out var wr);
            var rect = wr.ToRectangle();
            float s = ScreenCapture.ScaleAt(new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
            // The page inside the window, below its title bar.
            var area = Rectangle.FromLTRB(rect.Left + (int)(10 * s), rect.Top + (int)(40 * s), rect.Right - (int)(10 * s), rect.Bottom - (int)(10 * s));
            var r = await ScrollCapture.RunAsync(area);
            using var pageImage = r.Page;
            r.Page.Save(Path.Combine(outDir, "live-scroll-edge.png"), ImageFormat.Png);
            Console.WriteLine($"edge: {r.Page.Width}×{r.Page.Height}, note: {r.Note ?? "-"}");
            var text = await CheckLines(r.Page, "Item ", 160, "Edge page", check);
            int headers = Regex.Matches(text, "Pinned header").Count, bars = Regex.Matches(text, "cookie bar").Count;
            check(headers == 1 && bars == 1, $"live scroll: Edge's sticky header ({headers}) and cookie bar ({bars}) appear once");
            r.Backdrop.Dispose();
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
    }

    /// <summary>OCR, a slice at a time: each numbered line once, in order, with no more than a few unread.</summary>
    static async Task<string> CheckLines(Bitmap page, string prefix, int count, string what, Action<bool, string> check)
    {
        var all = new StringBuilder();
        for (int y = 0; y < page.Height; y += 1200)
        {
            using var slice = page.Clone(new Rectangle(0, y, page.Width, Math.Min(1200, page.Height - y)), PixelFormat.Format32bppArgb);
            all.AppendLine(await Task.Run(() => OcrService.RecognizeAsync(slice)));
        }
        string text = all.ToString();
        var numbers = Regex.Matches(text, @"\b" + Regex.Escape(prefix.Trim()) + @"\s?(\d{3})\b").Select(m => int.Parse(m.Groups[1].Value)).Where(n => n >= 1 && n <= count).ToList();
        var seen = numbers.Distinct().ToList();
        int dupes = numbers.Count - seen.Count, outOfOrder = numbers.Zip(numbers.Skip(1)).Count(p => p.Second <= p.First);
        check(seen.Count >= count * 0.95 && dupes == 0 && outOfOrder == 0,
            $"live scroll: {what} reads {seen.Count}/{count} lines, {dupes} twice, {outOfOrder} out of order");
        return text;
    }

    static string Words(int seed, int n)
    {
        string[] words = { "orbit", "lantern", "harbor", "quartz", "meadow", "signal", "copper", "ember", "glacier", "willow", "cobalt", "summit", "ribbon", "pixel", "canyon", "velvet" };
        var rnd = new Random(seed);
        return string.Join(" ", Enumerable.Range(0, n).Select(_ => words[rnd.Next(words.Length)]));
    }

    static string Page()
    {
        var html = new StringBuilder("""
            <!doctype html><html><head><meta charset="utf-8"><title>Scroll test</title><style>
            body { margin: 0; font: 17px/1.5 'Segoe UI', sans-serif; color: #222; }
            header { position: sticky; top: 0; background: #1757d3; color: #fff; padding: 14px 24px; font-weight: 600; }
            main { padding: 8px 28px 90px; }
            .item { margin: 14px 0; }
            .box { height: 60px; border-radius: 10px; margin-top: 8px; }
            footer { position: fixed; bottom: 0; left: 0; right: 0; background: #2e2e34; color: #fff; padding: 12px 24px; }
            </style></head><body><header>Pinned header · Northwind Docs</header><main>
            """);
        for (int i = 1; i <= 160; i++)
        {
            html.Append($"<div class=item><b>Item {i:000}</b> {Words(i, 12)}");
            if (i % 7 == 0) html.Append($"<div class=box style=\"background:linear-gradient(90deg,hsl({i * 37 % 360},70%,60%),hsl({i * 53 % 360},70%,45%))\"></div>");
            html.Append("</div>");
        }
        html.Append("</main><footer>This cookie bar stays at the bottom · Accept · Decline</footer></body></html>");
        return html.ToString();
    }
}
