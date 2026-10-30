using System.Drawing.Imaging;
using System.Reflection;
using LiteShot.Overlay;

namespace LiteShot.Harness;

/// <summary>Text styling, bundled fonts, the AI popover and windows, and SyncNode error handling.</summary>
internal static class NewFeatures
{
    public static void Run(string outDir, Func<CaptureOverlay> newOverlay, Rectangle lightWindow, Action<bool, string> check)
    {
        // Bundled fonts are embedded and registered.
        foreach (var family in new[] { "Permanent Marker", "Bebas Neue", "Pacifico", "Patrick Hand", "Anton", "Archivo Black", "Poppins SemiBold" })
            check(FontRegistry.IsAvailable(family), $"bundled font available: {family}");

        // Text looks, fonts, bold/italic and alignment, rendered straight from the shapes.
        using (var sheet = new Bitmap(1100, 760, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(235, 238, 245));
            using (var stripe = new SolidBrush(Color.FromArgb(40, 44, 52))) g.FillRectangle(stripe, 550, 0, 550, 760);
            var samples = new (string Family, TextLook Look, bool Bold, bool Italic, TextAlign Align, Color Color)[]
            {
                ("Segoe UI Semibold", TextLook.Plain, false, false, TextAlign.Left, Color.FromArgb(255, 59, 48)),
                ("Permanent Marker", TextLook.Outline, false, false, TextAlign.Left, Color.FromArgb(255, 204, 0)),
                ("Bebas Neue", TextLook.Label, false, false, TextAlign.Center, Color.FromArgb(10, 132, 255)),
                ("Pacifico", TextLook.Shadow, false, false, TextAlign.Left, Color.White),
                ("Patrick Hand", TextLook.Plain, true, true, TextAlign.Right, Color.FromArgb(52, 199, 89)),
                ("Anton", TextLook.Label, false, false, TextAlign.Left, Color.FromArgb(255, 45, 146)),
                ("Georgia", TextLook.Outline, true, true, TextAlign.Center, Color.FromArgb(175, 82, 222)),
            };
            for (int i = 0; i < samples.Length; i++)
            {
                var s = samples[i];
                var t = new TextShape
                {
                    Pos = new PointF(i % 2 == 0 ? 40 : 590, 30 + (i / 2) * 180),
                    Text = $"{s.Family}\n{s.Look} · {s.Align}{(s.Bold ? " · bold" : "")}{(s.Italic ? " · italic" : "")}",
                    FamilyName = s.Family, Look = s.Look, Bold = s.Bold, Italic = s.Italic, Align = s.Align, Color = s.Color, Size = 44,
                };
                t.Draw(g, sheet);
            }
            sheet.Save(Path.Combine(outDir, "11-text-looks.png"), ImageFormat.Png);
        }

        // Editing text shows the style bar; its buttons restyle the text.
        var o = newOverlay();
        Drag(o, new Point(lightWindow.X + 60, lightWindow.Y + 80), new Point(lightWindow.X + 820, lightWindow.Y + 620));
        var sel = Get<Rectangle>(o, "sel");
        Key(o, Keys.T);
        Check(check, Get<Toolbar>(o, "textBar") is var _ && (bool)Prop(o, "TextBarVisible"), "style bar shows when the Text tool is armed");
        Click(o, new Point(sel.X + 80, sel.Y + 470));
        foreach (char c in "Big glass label") Invoke(o, "OnKeyPress", new KeyPressEventArgs(c));
        Key(o, Keys.Control | Keys.B);
        Key(o, Keys.Control | Keys.E);
        for (int i = 0; i < 4; i++) Key(o, Keys.Control | Keys.OemCloseBrackets);
        var editing = Get<TextShape>(o, "editing");
        check(editing.Bold && editing.Align == TextAlign.Center, "Ctrl+B and Ctrl+E style the text being edited");
        check(Math.Abs(editing.Size / Get<float>(o, "S") - 30) < 0.5f, $"Ctrl+] steps the size up ({editing.Size / Get<float>(o, "S"):0} px)");
        var bar = Get<Toolbar>(o, "textBar");
        var lookButton = bar.Groups[4].Buttons[0];
        Click(o, Point.Round(Center(lookButton.Bounds)));
        Click(o, Point.Round(Center(lookButton.Bounds)));
        check(editing.Look == TextLook.Label, "the look button cycles Plain → Outline → Label");
        Snap(o, Path.Combine(outDir, "12-text-bar.png"), Rectangle.Inflate(Rectangle.Union(sel, Rectangle.Round(bar.Bounds)), 60, 60));
        Key(o, Keys.Escape);
        check(Get<List<Annotation>>(o, "shapes").OfType<TextShape>().Any(t => t.Text == "Big glass label" && t.Look == TextLook.Label), "the styled text is kept");

        // The Marker tool: a highlighter pen cursor, hotspot at the nib, over a dot of the ink it will lay down.
        Key(o, Keys.M);
        var spot = new Point(sel.X + 420, sel.Y + 300);
        Move(o, spot);
        var cursor = o.Cursor;
        float scale = Get<float>(o, "S");
        check(cursor != Cursors.Cross && Math.Abs(cursor.HotSpot.X - 3.5f * scale) <= 1.5f && Math.Abs(cursor.HotSpot.Y - 28.5f * scale) <= 1.5f,
            $"the Marker tool shows a marker cursor, its tip on the hotspot ({cursor.HotSpot} at {scale:0.##}×)");
        check(!((Rectangle)Invoke(o, "BrushCursorRect")!).IsEmpty, "a dot of the marker's ink follows the pointer");
        int size = Math.Max(32, (int)MathF.Round(32 * scale));
        using (var picture = MarkerCursor.Render(Get<Color>(o, "color"), size))
        {
            var crop = new Rectangle(spot.X - 90, spot.Y - 90, 180, 180);
            Snap(o, Path.Combine(outDir, "18-marker-cursor-overlay.png"), crop);
            using var snap = new Bitmap(Path.Combine(outDir, "18-marker-cursor-overlay.png"));
            using var withPointer = new Bitmap(snap);
            using (var g = Graphics.FromImage(withPointer))
                g.DrawImage(picture, spot.X - crop.X - cursor.HotSpot.X, spot.Y - crop.Y - cursor.HotSpot.Y, size, size);
            snap.Dispose();
            withPointer.Save(Path.Combine(outDir, "18-marker-cursor-overlay.png"), ImageFormat.Png);
        }
        MarkerCursorSheet(outDir);

        // AI popover without a key: explains how to connect.
        string? savedKey = AppSettings.Current.SyncNodeKey;
        AppSettings.Current.SyncNodeKey = null;
        var aiButton = Get<ToolButton>(o, "aiEditButton");
        Click(o, Point.Round(Center(aiButton.Bounds)));
        check(Get<AiKind?>(o, "aiMode") == AiKind.Edit, "Edit with AI opens its popover");
        Snap(o, Path.Combine(outDir, "13-ai-no-key.png"), Rectangle.Inflate(Rectangle.Round(Get<RectangleF>(o, "aiRect")), 60, 60));
        Key(o, Keys.Escape);

        // With a key: prompt box, suggestions and the Generate button. (A placeholder key; nothing is sent.)
        AiService.ApiKey = "sn_test_placeholder";
        var videoButton = Get<ToolButton>(o, "aiVideoButton");
        Click(o, Point.Round(Center(videoButton.Bounds)));
        Snap(o, Path.Combine(outDir, "14-ai-video-prompt.png"), Rectangle.Inflate(Rectangle.Union(Rectangle.Round(Get<RectangleF>(o, "aiRect")), sel), 40, 40));
        Key(o, Keys.Escape);
        Click(o, Point.Round(Center(aiButton.Bounds)));
        var chips = Get<List<(RectangleF Rect, string Text)>>(o, "aiChips");
        check(chips.Count > 0, $"suggestion chips shown ({chips.Count})");
        Click(o, Point.Round(Center(chips[0].Rect)));
        check(Get<TextBox>(o, "aiBox").Text == chips[0].Text, "clicking a chip fills the prompt");
        Snap(o, Path.Combine(outDir, "15-ai-edit-prompt.png"), Rectangle.Inflate(Rectangle.Union(Rectangle.Round(Get<RectangleF>(o, "aiRect")), sel), 40, 40));
        AppSettings.Current.SyncNodeKey = savedKey;
        o.Dispose();

        // SyncNode's error comes back readable with a bad key (no charge: the call is rejected).
        try
        {
            var balance = Task.Run(() => SyncNodeClient.BalanceAsync("liteshot-invalid-key")).GetAwaiter().GetResult();
            check(false, $"invalid key was accepted?! balance={balance}");
        }
        catch (Exception ex)
        {
            check(ex is SyncNodeException, $"SyncNode rejects a bad key: \"{AiService.Friendly(ex)}\" ({ex.Message})");
        }

        // AI result window and progress card.
        var shot = new Bitmap(Path.Combine(outDir, "05-result.png"));
        var edited = (Bitmap)shot.Clone();
        using (var gg = Graphics.FromImage(edited))
        using (var tint = new SolidBrush(Color.FromArgb(70, 10, 132, 255)))
            gg.FillRectangle(tint, 0, 0, edited.Width, edited.Height);
        using (var result = new AiResultForm(null!, shot, edited, "Make it dark mode"))
        {
            result.StartPosition = FormStartPosition.Manual;
            result.Location = new Point(-10000, -10000);
            result.Show();
            Application.DoEvents();
            using var bmp = new Bitmap(result.Width, result.Height);
            result.DrawToBitmap(bmp, new Rectangle(Point.Empty, result.Size));
            bmp.Save(Path.Combine(outDir, "16-ai-result.png"), ImageFormat.Png);
            result.Close();
        }
        using (var card = new AiProgressForm("Making a video", "Seedance 1 Lite", new CancellationTokenSource()))
        {
            card.SetStatus("Generating…");
            card.CreateControl();
            using var bmp = new Bitmap(card.Width, card.Height);
            card.DrawToBitmap(bmp, new Rectangle(Point.Empty, card.Size));
            bmp.Save(Path.Combine(outDir, "17-ai-progress.png"), ImageFormat.Png);
        }

        // Upload lifetimes map to the host's expiry options.
        check(Uploader.Lifetime("1h") == TimeSpan.FromHours(1) && Uploader.Lifetime("24h") == TimeSpan.FromHours(24) && Uploader.Normalize("never") == "24h",
            "links last 1, 12 or 24 hours; an old \"never\" setting becomes 24 hours");
    }

    static void Check(Action<bool, string> check, bool ok, string what) => check(ok, what);

    /// <summary>The marker cursor at 100%, 150% and 200% in four colours, on a light and a dark capture, over its ink dot.</summary>
    static void MarkerCursorSheet(string outDir)
    {
        Color[] colors = { Color.FromArgb(255, 204, 0), Color.FromArgb(255, 59, 48), Color.FromArgb(52, 199, 89), Color.White };
        float[] scales = { 1f, 1.5f, 2f };
        const int cell = 110;
        using var sheet = new Bitmap(cell * colors.Length * scales.Length, cell * 2, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(sheet);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var light = new SolidBrush(Color.FromArgb(246, 247, 250))) g.FillRectangle(light, 0, 0, sheet.Width, cell);
        using (var dark = new SolidBrush(Color.FromArgb(30, 31, 36))) g.FillRectangle(dark, 0, cell, sheet.Width, cell);
        for (int row = 0; row < 2; row++)
            for (int i = 0; i < colors.Length * scales.Length; i++)
            {
                var color = colors[i % colors.Length];
                float scale = scales[i / colors.Length];
                int size = Math.Max(32, (int)MathF.Round(32 * scale));
                var tip = new PointF(i * cell + cell / 2f - 6, row * cell + cell / 2f + 10);
                float d = 16 * scale;
                var dot = new RectangleF(tip.X - d / 2, tip.Y - d / 2, d, d);
                using (var ink = new SolidBrush(Color.FromArgb(105, color))) g.FillEllipse(ink, dot);
                using (var shade = new Pen(Color.FromArgb(120, 0, 0, 0), 3f)) g.DrawEllipse(shade, dot);
                using (var ring = new Pen(Color.FromArgb(235, 255, 255, 255), 1.4f)) g.DrawEllipse(ring, dot);
                using var picture = MarkerCursor.Render(color, size);
                g.DrawImage(picture, tip.X - 3.5f * size / 32, tip.Y - 28.5f * size / 32, size, size);
            }
        sheet.Save(Path.Combine(outDir, "18-marker-cursor.png"), ImageFormat.Png);
    }

    static PointF Center(RectangleF r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    static MethodInfo? Find(Type t, string name, int n)
    {
        for (var type = t; type != null; type = type.BaseType)
        {
            var m = type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .FirstOrDefault(x => x.Name == name && x.GetParameters().Length == n);
            if (m != null) return m;
        }
        return null;
    }

    static object? Invoke(object o, string name, params object[] args) => Find(o.GetType(), name, args.Length)!.Invoke(o, args);
    static object Prop(object o, string name) => o.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o)!;
    static T Get<T>(object o, string field) => (T)o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o)!;

    static void Down(Control o, Point p) => Invoke(o, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0));
    static void Up(Control o, Point p) => Invoke(o, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0));
    static void Move(Control o, Point p) => Invoke(o, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, p.X, p.Y, 0));
    static void Click(Control o, Point p) { Move(o, p); Down(o, p); Up(o, p); }

    static void Drag(Control o, Point a, Point b)
    {
        Down(o, a);
        for (int i = 1; i <= 10; i++) Move(o, new Point(a.X + (b.X - a.X) * i / 10, a.Y + (b.Y - a.Y) * i / 10));
        Up(o, b);
    }

    static void Key(Control o, Keys k) => Find(o.GetType(), "ProcessCmdKey", 2)!.Invoke(o, new object[] { new Message(), k });

    static void Snap(CaptureOverlay o, string path, Rectangle crop)
    {
        var size = o.Bounds.Size;
        using var full = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(full))
            Invoke(o, "OnPaint", new PaintEventArgs(g, new Rectangle(Point.Empty, size)));
        crop.Intersect(new Rectangle(Point.Empty, size));
        using var part = full.Clone(crop, PixelFormat.Format32bppArgb);
        part.Save(path, ImageFormat.Png);
    }
}
