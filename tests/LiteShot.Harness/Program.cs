using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Reflection;
using LiteShot;
using LiteShot.Overlay;

// Drives the real capture overlay offscreen: synthetic desktop in, simulated mouse/keyboard events,
// every state rendered through the overlay's own OnPaint to PNG. Nothing touches the real screen or input.
namespace LiteShot.Harness;

internal static class Program
{
    static string outDir = "";
    static int failures;

    [STAThread]
    static int Main(string[] args)
    {
        outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "liteshot-harness");
        Directory.CreateDirectory(outDir);
        if (args.Contains("--icons")) { IconSheet.Render(outDir); return 0; }
        int linkArg = Array.IndexOf(args, "--fortefile-link");
        if (linkArg >= 0) { Fortefile.PrintLink(args[linkArg + 1], args[linkArg + 2]); return 0; }
        if (args.Contains("--live")) { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); LiveCaptureCheck.Run(outDir); return 0; }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        var virt = SystemInformation.VirtualScreen;
        using var desktop = SyntheticDesktop(virt.Size);
        desktop.Save(Path.Combine(outDir, "00-desktop.png"), ImageFormat.Png);

        var windows = new List<Rectangle> { DarkWindow(virt.Size), LightWindow(virt.Size) };
        windows = windows.Select(r => new Rectangle(r.X + virt.X, r.Y + virt.Y, r.Width, r.Height)).ToList();
        windows.Add(virt);
        if (args.Contains("--site")) { SiteShots(desktop, virt, windows); HowShots(virt); return 0; }

        // 1. Hover over a window: highlight, size label and loupe.
        var o = NewOverlay(desktop, virt, windows);
        var lw = LightWindow(virt.Size);
        Move(o, lw.X + 400, lw.Y + 300);
        Snap(o, "01-hover-window", Inflate(lw, 40));

        // 2. Drag a selection; toolbars appear. Hover a tool for its tooltip.
        var a = new Point(lw.X + 60, lw.Y + 80);
        var b = new Point(lw.X + 820, lw.Y + 620);
        Drag(o, a, b);
        Check(Get<bool>(o, "hasSel"), "selection exists after drag");
        var sel = Get<Rectangle>(o, "sel");
        Check(sel.Width == b.X - a.X + 1 && sel.Height == b.Y - a.Y + 1, $"selection size {sel.Size}");
        var tools = Get<Toolbar>(o, "tools");
        var arrowButton = tools.Buttons.ElementAt(2);
        Move(o, (int)(arrowButton.Bounds.X + 10), (int)(arrowButton.Bounds.Y + 10));
        Set(o, "tooltipVisible", true);
        Set(o, "hoverAnim", 1f);
        Snap(o, "02-selected", Inflate(Rectangle.Union(sel, Rectangle.Round(tools.Bounds)), 120));

        // 3. Annotate with every tool through the keyboard shortcuts and mouse.
        Key(o, Keys.A); Drag(o, P(sel, 90, 420), P(sel, 330, 250));
        Key(o, Keys.R); Drag(o, P(sel, 360, 210), P(sel, 700, 300));
        Key(o, Keys.E); Drag(o, P(sel, 420, 360), P(sel, 600, 460));
        Key(o, Keys.M); Drag(o, P(sel, 40, 118), P(sel, 420, 118));
        Key(o, Keys.B); Drag(o, P(sel, 40, 330), P(sel, 300, 380));
        Key(o, Keys.N); Click(o, P(sel, 60, 200)); Click(o, P(sel, 60, 260)); Click(o, P(sel, 640, 470));
        Key(o, Keys.P); Squiggle(o, P(sel, 480, 500), 60);
        Key(o, Keys.T); Click(o, P(sel, 380, 520));
        foreach (char c in "Ship it! Glass UI") Type(o, c);
        Key(o, Keys.Escape);
        Check(Get<List<Annotation>>(o, "shapes").Count == 10, $"annotation count {Get<List<Annotation>>(o, "shapes").Count}");
        Key(o, Keys.Control | Keys.Z);
        Check(Get<List<Annotation>>(o, "shapes").Count == 9, "undo removes the text");
        Key(o, Keys.Control | Keys.Y);
        Check(Get<List<Annotation>>(o, "shapes").Count == 10, "redo restores the text");
        Key(o, Keys.T); // toggle text tool off
        Move(o, sel.Right - 5, sel.Bottom + 400);
        Snap(o, "03-annotated", Inflate(Rectangle.Union(sel, Rectangle.Round(tools.Bounds)), 120));

        // Move a shape with no tool active, then undo it.
        var counter = Get<List<Annotation>>(o, "shapes").OfType<CounterShape>().First();
        var before = counter.Center;
        Drag(o, Point.Round(counter.Center), Point.Round(new PointF(counter.Center.X + 40, counter.Center.Y)));
        Check(Math.Abs(Get<List<Annotation>>(o, "shapes").OfType<CounterShape>().First().Center.X - before.X - 40) < 0.5f, "dragging moves a shape");
        Key(o, Keys.Control | Keys.Z);
        Check(Get<List<Annotation>>(o, "shapes").OfType<CounterShape>().First().Center == before, "undo restores the move");

        // 4. Color and size popover.
        var colorButton = Get<ToolButton>(o, "colorButton");
        Click(o, Point.Round(new PointF(colorButton.Bounds.X + colorButton.Bounds.Width / 2, colorButton.Bounds.Y + colorButton.Bounds.Height / 2)));
        Check(Get<bool>(o, "paletteOpen"), "palette opens");
        Snap(o, "04-palette", Inflate(Rectangle.Union(sel, Rectangle.Round(tools.Bounds)), 360));
        Key(o, Keys.Escape);

        // 5. The final image.
        using (var result = (Bitmap)Invoke(o, "RenderResult")!)
        {
            Check(result.Size == sel.Size, "rendered image matches the selection");
            result.Save(Path.Combine(outDir, "05-result.png"), ImageFormat.Png);
        }

        // 6. Whole screen selected: chrome moves inside and turns light over the bright document.
        Key(o, Keys.Control | Keys.A);
        Check(Get<Rectangle>(o, "sel") == new Rectangle(Point.Empty, virt.Size), "Ctrl+A selects the monitor");
        Move(o, virt.Width / 2, virt.Height / 2);
        Snap(o, "06-fullscreen", new Rectangle(virt.Width - 900, virt.Height - 700, 900, 700));
        o.Dispose();

        // 7. Reduced transparency: frosted, near-opaque glass.
        SetStatic(typeof(SystemTheme), "TransparencyEnabled", false);
        o = NewOverlay(desktop, virt, windows);
        Drag(o, a, b);
        Snap(o, "07-reduced-transparency", Inflate(Rectangle.Union(Get<Rectangle>(o, "sel"), Rectangle.Round(Get<Toolbar>(o, "tools").Bounds)), 120));
        o.Dispose();
        SetStatic(typeof(SystemTheme), "TransparencyEnabled", true);

        // 7b. Output paths: OCR with the Windows engine, saving, file naming, pinning.
        using (var doc = desktop.Clone(LightWindow(virt.Size), PixelFormat.Format32bppArgb))
        {
            // Off the UI thread: blocking on the task here would deadlock its WinForms continuation.
            string text = Task.Run(() => OcrService.RecognizeAsync(doc)).GetAwaiter().GetResult();
            Check(text.Contains("Quarterly report") && text.Contains("Revenue grew"), $"OCR reads the document ({text.Split('\n')[0].Trim()} …)");

            string name = ImageOutput.BuildFileName("Shot_{yyyy-MM-dd}_{HH-mm}", new DateTime(2026, 10, 28, 9, 5, 0));
            Check(name == "Shot_2026-10-28_09-05", $"file name pattern → {name}");
            foreach (var ext in new[] { ".png", ".jpg", ".bmp" })
            {
                string path = Path.Combine(outDir, "save-test" + ext);
                ImageOutput.Save(doc, path);
                using var back = new Bitmap(path);
                Check(back.Size == doc.Size, $"saved and reloaded {ext} ({new FileInfo(path).Length / 1024} KB)");
            }

            using var pin = new PinForm((Bitmap)doc.Clone(), new Point(-5000, -5000));
            pin.Show();
            using var pinBmp = new Bitmap(pin.Width, pin.Height);
            pin.DrawToBitmap(pinBmp, new Rectangle(Point.Empty, pin.Size));
            Check(pin.Size == doc.Size, "pin window opens at the screenshot's size");
            pin.Close();
        }

        // 7c. New: text styling, fonts, AI and uploads.
        NewFeatures.Run(outDir, () => NewOverlay(desktop, virt, windows), LightWindow(virt.Size), Check);

        // 7d. Privacy (names, faces) and updates.
        int serverArg = Array.IndexOf(args, "--update-server");
        PrivacyAndUpdates.Run(outDir, d => NewOverlay(d, virt, windows), desktop, Check, serverArg >= 0 && serverArg + 1 < args.Length ? args[serverArg + 1] : null);
        int forteArg = Array.IndexOf(args, "--fortefile-server");
        Fortefile.Run(Check, forteArg >= 0 && forteArg + 1 < args.Length ? args[forteArg + 1] : null);

        // 8. Settings window pages.
        using (var settings = new SettingsForm(null!))
        {
            settings.StartPosition = FormStartPosition.Manual;
            settings.Location = new Point(-10000, -10000);
            settings.Show();
            for (int page = 0; page < 5; page++)
            {
                Invoke(settings, "ShowPage", page);
                Application.DoEvents();
                using var bmp = new Bitmap(settings.Width, settings.Height);
                settings.DrawToBitmap(bmp, new Rectangle(Point.Empty, settings.Size));
                bmp.Save(Path.Combine(outDir, $"08-settings-{page}.png"), ImageFormat.Png);
            }
            settings.Close();
        }

        // 9. Toolbar icons at a large size, for a close look.
        using (var sheet = new Bitmap(1200, 160))
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(30, 30, 34));
            Icons.Glyph[] glyphs = { Icons.Pen, Icons.Line, Icons.Arrow, Icons.Rect, Icons.Ellipse, Icons.Marker, Icons.Text, Icons.Counter, Icons.Pixelate,
                Icons.Undo, Icons.Redo, Icons.Upload, Icons.Share, Icons.Search, Icons.Print, Icons.Pin, Icons.Ocr, Icons.Copy, Icons.Save, Icons.Close,
                Icons.Capture, Icons.Monitor, Icons.Repeat, Icons.Folder, Icons.Gear, Icons.Power, Icons.Clock };
            for (int i = 0; i < glyphs.Length; i++)
                Icons.Draw(g, new RectangleF(12 + (i % 14) * 84, 12 + (i / 14) * 76, 56, 56), Color.White, glyphs[i]);
            sheet.Save(Path.Combine(outDir, "09-icons.png"), ImageFormat.Png);
        }

        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        Console.WriteLine($"Output: {outDir}");
        return failures == 0 ? 0 : 1;
    }

    // ───────────────────────────── scene ─────────────────────────────

    static Rectangle DarkWindow(Size s) => new((int)(s.Width * 0.05), (int)(s.Height * 0.09), (int)(s.Width * 0.38), (int)(s.Height * 0.48));
    static Rectangle LightWindow(Size s) => new((int)(s.Width * 0.47), (int)(s.Height * 0.24), (int)(s.Width * 0.48), (int)(s.Height * 0.70));

    static Bitmap SyntheticDesktop(Size size)
    {
        var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using (var wall = new LinearGradientBrush(new Rectangle(Point.Empty, size), Color.FromArgb(18, 42, 110), Color.FromArgb(120, 40, 140), 35f))
            g.FillRectangle(wall, 0, 0, size.Width, size.Height);
        var rnd = new Random(7);
        for (int i = 0; i < 14; i++)
        {
            int r = rnd.Next(120, 420);
            using var blob = new SolidBrush(Color.FromArgb(70, rnd.Next(80, 255), rnd.Next(60, 200), rnd.Next(120, 255)));
            g.FillEllipse(blob, rnd.Next(0, size.Width), rnd.Next(0, size.Height), r, r);
        }

        var dark = DarkWindow(size);
        using (var bg = new SolidBrush(Color.FromArgb(30, 30, 32))) g.FillRectangle(bg, dark);
        using (var bar = new SolidBrush(Color.FromArgb(45, 45, 48))) g.FillRectangle(bar, dark.X, dark.Y, dark.Width, 44);
        using (var mono = new Font("Consolas", 22, GraphicsUnit.Pixel))
        {
            string[] code = { "public void Capture()", "{", "    var shot = Screen.Grab();", "    overlay.Show(shot);", "    // glass toolbars", "}" };
            Color[] colors = { Color.FromArgb(86, 156, 214), Color.Gainsboro, Color.FromArgb(156, 220, 254), Color.FromArgb(220, 220, 170), Color.FromArgb(106, 153, 85), Color.Gainsboro };
            for (int i = 0; i < code.Length; i++)
                using (var b = new SolidBrush(colors[i])) g.DrawString(code[i], mono, b, dark.X + 30, dark.Y + 70 + i * 36);
        }

        var light = LightWindow(size);
        using (var bg = new SolidBrush(Color.White)) g.FillRectangle(bg, light);
        using (var bar = new SolidBrush(Color.FromArgb(243, 243, 243))) g.FillRectangle(bar, light.X, light.Y, light.Width, 44);
        using (var title = new Font("Segoe UI Semibold", 40, GraphicsUnit.Pixel))
        using (var body = new Font("Segoe UI", 24, GraphicsUnit.Pixel))
        using (var ink = new SolidBrush(Color.FromArgb(30, 30, 30)))
        using (var gray = new SolidBrush(Color.FromArgb(90, 90, 90)))
        {
            g.DrawString("Quarterly report", title, ink, light.X + 40, light.Y + 80);
            string[] lines = { "Revenue grew 24% year over year, led by the new product line.", "Customer satisfaction reached an all-time high of 92%.",
                "Account numbers 4417-1234-5678 and 9921-7765-1102 are redacted.", "Next quarter we focus on onboarding and performance.",
                "The design team shipped the glass interface refresh.", "Support tickets dropped by a third after the update." };
            for (int i = 0; i < lines.Length; i++) g.DrawString(lines[i], body, gray, light.X + 40, light.Y + 170 + i * 46);
            using var chartBrush = new SolidBrush(Color.FromArgb(10, 132, 255));
            for (int i = 0; i < 8; i++)
            {
                int h = 60 + (i * 37 % 140);
                g.FillRectangle(chartBrush, light.X + 60 + i * 70, light.Bottom - 80 - h, 44, h);
            }
        }
        using (var task = new SolidBrush(Color.FromArgb(230, 32, 32, 36))) g.FillRectangle(task, 0, size.Height - 48, size.Width, 48);
        return bmp;
    }

    // ───────────────────────────── driving the overlay ─────────────────────────────

    static CaptureOverlay NewOverlay(Bitmap desktop, Rectangle virt, List<Rectangle> windows)
    {
        var copy = new Bitmap(desktop.Width, desktop.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(copy)) g.DrawImage(desktop, new Rectangle(Point.Empty, desktop.Size), 0, 0, desktop.Width, desktop.Height, GraphicsUnit.Pixel);
        return new CaptureOverlay(null!, copy, virt, windows, null);
    }

    static Point P(Rectangle sel, int dx, int dy) => new(sel.X + dx, sel.Y + dy);
    static Rectangle Inflate(Rectangle r, int by) => Rectangle.Inflate(r, by, by);

    static void Down(Control o, Point p) => Invoke(o, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0));
    static void Up(Control o, Point p) => Invoke(o, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0));
    static void Move(Control o, int x, int y) => Invoke(o, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, x, y, 0));

    static void Drag(Control o, Point from, Point to)
    {
        Down(o, from);
        for (int i = 1; i <= 12; i++)
            Move(o, from.X + (to.X - from.X) * i / 12, from.Y + (to.Y - from.Y) * i / 12);
        Up(o, to);
    }

    static void Click(Control o, Point p)
    {
        Down(o, p);
        Up(o, p);
    }

    static void Squiggle(Control o, Point start, int amplitude)
    {
        Down(o, start);
        for (int i = 0; i <= 40; i++)
            Move(o, start.X + i * 6, start.Y + (int)(Math.Sin(i / 4.0) * amplitude));
        Up(o, new Point(start.X + 240, start.Y));
    }

    static void Key(Control o, Keys k)
    {
        var args = new object[] { new Message(), k };
        var m = FindMethod(o.GetType(), "ProcessCmdKey", 2)!;
        m.Invoke(o, args);
    }

    static void Type(Control o, char c) => Invoke(o, "OnKeyPress", new KeyPressEventArgs(c));

    static void Snap(CaptureOverlay o, string name, Rectangle crop)
    {
        var size = o.ClientSize.Width > 0 ? o.Bounds.Size : SystemInformation.VirtualScreen.Size;
        using var full = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(full))
            Invoke(o, "OnPaint", new PaintEventArgs(g, new Rectangle(Point.Empty, size)));
        crop.Intersect(new Rectangle(Point.Empty, size));
        using var part = full.Clone(crop, PixelFormat.Format32bppArgb);
        part.Save(Path.Combine(outDir, name + ".png"), ImageFormat.Png);
    }

    // ───────────────────────────── website shots ─────────────────────────────

    /// <summary>Clean product shots for the website, rendered by the real overlay: the editor, privacy, AI and text styling.</summary>
    static void SiteShots(Bitmap desktop, Rectangle virt, List<Rectangle> windows)
    {
        var settings = AppSettings.Current;
        var lw = LightWindow(virt.Size);
        Rectangle Chrome(CaptureOverlay o, params RectangleF[] extra)
        {
            var r = Rectangle.Union(Get<Rectangle>(o, "sel"), Rectangle.Round(Get<Toolbar>(o, "tools").Bounds));
            r = Rectangle.Union(r, Rectangle.Round(Get<Toolbar>(o, "actions").Bounds));
            foreach (var e in extra) r = Rectangle.Union(r, Rectangle.Round(e));
            return Inflate(r, 44);
        }

        // 1. The editor: a report marked up the way people actually do it.
        var o = NewOverlay(desktop, virt, windows);
        Drag(o, new Point(lw.X + 22, lw.Y + 58), new Point(lw.Right - 26, lw.Bottom - 34));
        var sel = Get<Rectangle>(o, "sel");
        Set(o, "color", Color.FromArgb(255, 214, 10));
        Key(o, Keys.M); Drag(o, new Point(lw.X + 40, lw.Y + 186), new Point(lw.X + 395, lw.Y + 186));
        Set(o, "color", Color.FromArgb(255, 149, 0));
        Key(o, Keys.B); Drag(o, new Point(lw.X + 222, lw.Y + 260), new Point(lw.X + 660, lw.Y + 296));
        Key(o, Keys.R); Drag(o, new Point(lw.X + 46, lw.Bottom - 236), new Point(lw.X + 612, lw.Bottom - 64));
        Key(o, Keys.A); Drag(o, new Point(lw.X + 900, lw.Y + 140), new Point(lw.X + 668, lw.Y + 222));
        Key(o, Keys.N); Click(o, new Point(lw.X + 46, lw.Bottom - 236)); Click(o, new Point(lw.X + 900, lw.Y + 140));
        Key(o, Keys.T); Click(o, new Point(lw.X + 660, lw.Bottom - 160));
        foreach (char c in "Best quarter yet!") Type(o, c);
        var label = Get<TextShape>(o, "editing");
        label.Look = TextLook.Label;
        label.FamilyName = "Poppins SemiBold";
        Key(o, Keys.Escape);
        Key(o, Keys.T);
        Move(o, sel.X + 5, sel.Bottom + 400);
        Snap(o, "site-editor", Chrome(o));

        // 2. AI: the same capture with the Edit with AI prompt open.
        string? savedKey = settings.SyncNodeKey;
        AiService.ApiKey = "sn_placeholder_for_rendering";
        var ai = Get<ToolButton>(o, "aiEditButton");
        Click(o, Point.Round(new PointF(ai.Bounds.X + ai.Bounds.Width / 2, ai.Bounds.Y + ai.Bounds.Height / 2)));
        var chips = Get<List<(RectangleF Rect, string Text)>>(o, "aiChips");
        if (chips.Count > 1) Click(o, Point.Round(new PointF(chips[1].Rect.X + 6, chips[1].Rect.Y + 6)));
        Move(o, sel.X + 5, sel.Bottom + 400);
        var aiRect = Get<RectangleF>(o, "aiRect");
        var actions = Get<Toolbar>(o, "actions").Bounds;
        Snap(o, "site-ai", Inflate(Rectangle.Round(RectangleF.Union(aiRect, RectangleF.FromLTRB(actions.Left, sel.Bottom - 260, actions.Right, actions.Bottom))), 40));
        settings.SyncNodeKey = savedKey;
        o.Dispose();

        // 3. Text styling: the style bar over a label in a bundled font.
        o = NewOverlay(desktop, virt, windows);
        Drag(o, new Point(lw.X + 22, lw.Y + 58), new Point(lw.Right - 26, lw.Bottom - 34));
        sel = Get<Rectangle>(o, "sel");
        Set(o, "color", Color.FromArgb(255, 149, 0));
        Key(o, Keys.T); Click(o, new Point(lw.X + 120, lw.Y + 470));
        foreach (char c in "Ship it on Friday") Type(o, c);
        var text = Get<TextShape>(o, "editing");
        text.Look = TextLook.Label;
        text.FamilyName = "Pacifico";
        text.Size = 34 * Get<float>(o, "S");
        Move(o, sel.X + 5, sel.Bottom + 400);
        var bar = Get<Toolbar>(o, "textBar").Bounds;
        Snap(o, "site-text", Inflate(Rectangle.Round(RectangleF.Union(bar, text.Bounds)), 36));
        o.Dispose();

        // 4. Privacy: a chat with names blurred automatically, and the face button's tooltip.
        using var chat = PrivacyAndUpdates.ChatImage(out _);
        using var screen = (Bitmap)desktop.Clone();
        var at = new Point(lw.X + 30, lw.Y + 110);
        using (var g = Graphics.FromImage(screen)) g.DrawImage(chat, at.X, at.Y, chat.Width, chat.Height);
        bool before = settings.HidePersonalInfo;
        settings.HidePersonalInfo = true;
        o = NewOverlay(screen, virt, windows);
        Drag(o, new Point(at.X - 6, at.Y - 6), new Point(at.X + chat.Width - 60, at.Y + chat.Height + 4));
        Invoke(o, "StartPrivacyScan");
        Invoke(o, "ApplyPrivacy", true);
        var face = Get<ToolButton>(o, "blurFacesButton");
        Move(o, (int)(face.Bounds.X + face.Bounds.Width / 2), (int)(face.Bounds.Y + face.Bounds.Height / 2));
        Set(o, "tooltipVisible", true);
        Set(o, "hoverAnim", 1f);
        Snap(o, "site-privacy", Chrome(o, Rectangle.Inflate(Rectangle.Round(face.Bounds), 220, 20)));
        settings.HidePersonalInfo = before;
        o.Dispose();

        // 5. Settings, Capture page.
        using (var form = new SettingsForm(null!))
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-10000, -10000);
            form.Show();
            Application.DoEvents();
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
            bmp.Save(Path.Combine(outDir, "site-settings.png"), ImageFormat.Png);
            form.Close();
        }
        Console.WriteLine($"Wrote website shots to {outDir}");
    }

    /// <summary>
    /// "See it work" shots for the website: a staged desktop (SiteScene) with the real overlay over it, step by step,
    /// plus names before and after the automatic blur, real OCR output and a pinned screenshot.
    /// </summary>
    static void HowShots(Rectangle virt)
    {
        var settings = AppSettings.Current;
        float s = ScreenCapture.ScaleAt(new Point(virt.X + virt.Width / 2, virt.Y + virt.Height / 2));
        using var scene = SiteScene.Draw(virt.Size, s, out var L);
        var screen = new Rectangle(Point.Empty, virt.Size);
        var windows = new List<Rectangle> { L.Chat, L.Dashboard }.Select(r => new Rectangle(r.X + virt.X, r.Y + virt.Y, r.Width, r.Height)).ToList();
        windows.Add(virt);
        Point At(Rectangle r, float fx, float fy) => new((int)(r.X + r.Width * fx), (int)(r.Y + r.Height * fy));
        Rectangle Chrome(CaptureOverlay o) => Inflate(Rectangle.Union(Rectangle.Union(Get<Rectangle>(o, "sel"),
            Rectangle.Round(Get<Toolbar>(o, "tools").Bounds)), Rectangle.Round(Get<Toolbar>(o, "actions").Bounds)), (int)(30 * s));
        bool magnifier = settings.ShowMagnifier, autoBlur = settings.HidePersonalInfo;
        try
        {
            // 1. PrtScn: the screen freezes and dims, the window under the cursor is outlined, and the loupe follows.
            settings.ShowMagnifier = true;
            settings.HidePersonalInfo = false;
            var o = NewOverlay(scene, virt, windows);
            Move(o, At(L.Chat, 0.66f, 0.30f).X, At(L.Chat, 0.66f, 0.30f).Y);
            Snap(o, "how-1-capture", screen);
            o.Dispose();
            settings.ShowMagnifier = false;

            // 2. One click captures exactly that window; the tools appear beside it.
            o = NewOverlay(scene, virt, windows);
            var inChat = At(L.Chat, 0.66f, 0.30f);
            Move(o, inChat.X, inChat.Y);
            Click(o, inChat);
            Move(o, virt.Width - 8, 8);
            Snap(o, "how-2-select", screen);
            o.Dispose();

            // 3. Mark it up: the dashboard with an arrow, a label, numbered steps, a box and pixelated account numbers.
            o = NewOverlay(scene, virt, windows);
            Drag(o, new Point(L.Dashboard.X + (int)(12 * s), L.Dashboard.Y + (int)(88 * s)), new Point(L.Dashboard.Right - (int)(12 * s), L.Dashboard.Bottom - (int)(12 * s)));
            var K = L.Kpi;
            Set(o, "color", Color.FromArgb(255, 149, 0));
            Key(o, Keys.A); Drag(o, At(L.Chart, 0.20f, 0.10f), At(K, 0.125f, 1.02f));
            Key(o, Keys.T); Click(o, At(L.Chart, 0.03f, 0.26f));
            foreach (char c in "Best quarter yet!") Type(o, c);
            var label = Get<TextShape>(o, "editing");
            label.Look = TextLook.Label;
            label.FamilyName = "Poppins SemiBold";
            Key(o, Keys.Escape);
            Key(o, Keys.T);
            Key(o, Keys.R); Drag(o, At(L.Chart, 0.6f, -0.04f), At(L.Chart, 1.0f, 1.04f));
            Key(o, Keys.N); Click(o, At(L.Chart, 0.6f, -0.04f)); Click(o, new Point(L.Accounts.Right + (int)(20 * s), L.Accounts.Y + L.Accounts.Height / 2));
            Key(o, Keys.B); Drag(o, At(L.Accounts, 0f, 0f), At(L.Accounts, 1f, 1f));
            Key(o, Keys.B);
            Move(o, virt.Width - 8, 8);
            Snap(o, "how-3-markup", Chrome(o));
            using (var result = (Bitmap)Invoke(o, "RenderResult")!) result.Save(Path.Combine(outDir, "how-3-result.png"), ImageFormat.Png);
            o.Dispose();

            // 4. Names: the chat thread before and after the automatic blur.
            Bitmap Thread(bool blur)
            {
                settings.HidePersonalInfo = blur;
                var ov = NewOverlay(scene, virt, windows);
                Set(ov, "sel", L.ChatMessages);
                Set(ov, "hasSel", true);
                if (blur) Invoke(ov, "StartPrivacyScan");
                var img = (Bitmap)Invoke(ov, "RenderResult")!;
                Check(!blur || Get<List<Annotation>>(ov, "shapes").OfType<BlurShape>().Count() >= 6,
                    $"site: names blurred in the chat ({Get<List<Annotation>>(ov, "shapes").OfType<BlurShape>().Count()})");
                ov.Dispose();
                return img;
            }
            using var before = Thread(false);
            using var after = Thread(true);
            settings.HidePersonalInfo = false;
            before.Save(Path.Combine(outDir, "how-names-before.png"), ImageFormat.Png);
            after.Save(Path.Combine(outDir, "how-names-after.png"), ImageFormat.Png);

            // 5. Copy text: what Windows' OCR reads from that thread.
            string text = Task.Run(() => OcrService.RecognizeAsync(before)).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(outDir, "how-ocr.txt"), text);
            Check(text.Contains("glass toolbar"), "site: OCR reads the chat");

            // 6. Pin: the KPI cards pinned over the chat app.
            using var kpi = scene.Clone(Rectangle.Inflate(L.Kpi, (int)(10 * s), (int)(10 * s)), PixelFormat.Format32bppArgb);
            using var pin = new PinForm((Bitmap)kpi.Clone(), new Point(-10000, -10000));
            pin.Show();
            Application.DoEvents();
            using var pinned = new Bitmap(pin.Width, pin.Height);
            pin.DrawToBitmap(pinned, new Rectangle(Point.Empty, pin.Size));
            pin.Close();
            using var desk = (Bitmap)scene.Clone();
            using (var g = Graphics.FromImage(desk))
            {
                var at = new Point(L.Chat.X + (int)(190 * s), L.Chat.Y + (int)(372 * s));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                for (int i = 8; i >= 1; i--)
                {
                    using var shade = new SolidBrush(Color.FromArgb(12, 0, 0, 0));
                    g.FillRectangle(shade, Rectangle.Inflate(new Rectangle(at.X, at.Y + (int)(6 * s), pinned.Width, pinned.Height), i * 3, i * 3));
                }
                g.DrawImage(pinned, at.X, at.Y, pinned.Width, pinned.Height);
            }
            var pinRect = new Rectangle(L.Chat.X + (int)(190 * s), L.Chat.Y + (int)(372 * s), pinned.Width, pinned.Height);
            using (var crop = desk.Clone(Rectangle.Intersect(Inflate(Rectangle.Union(L.Chat, pinRect), (int)(36 * s)), screen), PixelFormat.Format32bppArgb))
                crop.Save(Path.Combine(outDir, "how-pin.png"), ImageFormat.Png);
        }
        finally
        {
            settings.ShowMagnifier = magnifier;
            settings.HidePersonalInfo = autoBlur;
        }
        Console.WriteLine("Wrote \"see it work\" shots");
    }

    // ───────────────────────────── reflection ─────────────────────────────

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

    static void Set(object o, string field, object value) =>
        o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, value);

    static void SetStatic(Type t, string property, object value) =>
        t.GetProperty(property, BindingFlags.Static | BindingFlags.Public)!.SetValue(null, value);

    static void Check(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
        if (!ok) failures++;
    }
}
