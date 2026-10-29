using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text;
using LiteShot;
using LiteShot.Overlay;

namespace LiteShot.Harness;

/// <summary>Name and face blurring, and the updater against a local update server (server/, `npm run dev`).</summary>
internal static class PrivacyAndUpdates
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Run(string outDir, Func<Bitmap, CaptureOverlay> newOverlay, Bitmap desktop, Action<bool, string> check, string? updateServer)
    {
        Names(check);
        PersonalDetails(check);
        PersonalOnScreen(outDir, check);
        NamesOnScreen(outDir, newOverlay, desktop, check);
        BlurBrush(outDir, newOverlay, desktop, check);
        Faces(outDir, check);
        if (updateServer != null) Updates(updateServer, check);
        else Console.WriteLine("  (skipped updater checks: start the local server with `npm run dev` in server/ and pass --update-server)");
    }

    // ───────────────────────────── names ─────────────────────────────

    static void Names(Action<bool, string> check)
    {
        void Expect(string line, params string[] names)
        {
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var found = NameFinder.Find(words).Select(s => string.Join(" ", words.Skip(s.Start).Take(s.Count)).Trim(',', '.', ':', '?')).ToList();
            bool ok = found.Count == names.Length && found.Zip(names).All(p => p.First == p.Second);
            check(ok, $"names in \"{line}\" → [{string.Join(" | ", found)}]");
        }
        Expect("Meeting with Sarah Johnson tomorrow", "Sarah Johnson");
        Expect("From: John Smith <john@example.com>", "John Smith");
        Expect("Hi Priya, thanks for the notes", "Priya");
        Expect("Reply to Dr. Patel about it", "Dr. Patel");
        Expect("Smith, John", "Smith, John");
        Expect("Xiaoming Zhang joined the call", "Xiaoming Zhang");
        Expect("Zhang Wei is typing", "Zhang Wei");
        Expect("Will you join? Mark as read", new string[0]);
        Expect("Settings Visual Studio Code Total Price", new string[0]);
        Expect("May Update Grace Period", new string[0]);
        Expect("JOHN SMITH SAVE CHANGES", "JOHN SMITH");
        Expect("On it. Marcus Lee already shared the draft.", "Marcus Lee");
    }

    internal static Bitmap ChatImage(out Dictionary<string, RectangleF> where)
    {
        var bmp = new Bitmap(1000, 330, PixelFormat.Format32bppArgb);
        where = new();
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var font = new Font("Segoe UI", 21, GraphicsUnit.Pixel);
        using var bold = new Font("Segoe UI Semibold", 21, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Color.FromArgb(30, 30, 30));
        var format = StringFormat.GenericTypographic;
        string[][] lines =
        {
            new[] { "*Sarah Johnson", "  10:42 AM" },
            new[] { "Can you send the quarterly numbers to ", "*Dr. Patel", " before Friday?" },
            new[] { "Meeting notes from the design review" },
            new[] { "Thanks ", "*Priya", ", I added ", "*Wang Wei", " to the thread." },
            new[] { "Revenue grew 24% year over year." },
        };
        for (int i = 0; i < lines.Length; i++)
        {
            float x = 30, y = 30 + i * 58;
            foreach (var part in lines[i])
            {
                bool name = part.StartsWith('*');
                string text = name ? part[1..] : part;
                var f = i == 0 && name ? bold : font;
                var size = g.MeasureString(text.Replace(' ', ' '), f, PointF.Empty, format);
                g.DrawString(text, f, ink, x, y, format);
                where[name ? text : $"line{i}:{text.Trim()}"] = new RectangleF(x, y, size.Width, size.Height);
                x += size.Width;
            }
        }
        return bmp;
    }

    static void NamesOnScreen(string outDir, Func<Bitmap, CaptureOverlay> newOverlay, Bitmap desktop, Action<bool, string> check)
    {
        using var chat = ChatImage(out var where);
        var found = Task.Run(() => NameFinder.FindAsync(chat)).GetAwaiter().GetResult();
        bool Covered(RectangleF r) => found.Any(f => f.Contains(Point.Round(new PointF(r.X + r.Width / 2, r.Y + r.Height / 2))));
        foreach (var name in new[] { "Sarah Johnson", "Dr. Patel", "Priya", "Wang Wei" })
            check(Covered(where[name]), $"OCR finds and covers the name \"{name}\"");
        check(!found.Any(f => f.IntersectsWith(Rectangle.Round(where["line2:Meeting notes from the design review"]))), "a line without names stays sharp");
        check(!found.Any(f => f.IntersectsWith(Rectangle.Round(where["line4:Revenue grew 24% year over year."]))), "numbers stay sharp");

        using (var blurred = (Bitmap)chat.Clone())
        {
            int n = Task.Run(() => NameFinder.BlurAsync(blurred)).GetAwaiter().GetResult();
            check(n == found.Count && n >= 4, $"full-screen capture path blurs {n} names in place");
            blurred.Save(Path.Combine(outDir, "20-names-blurred.png"), ImageFormat.Png);
        }

        // In the editor: the scan runs when the overlay opens; copying waits for it; Ctrl+Z brings the names back.
        var settings = AppSettings.Current;
        bool before = settings.HidePersonalInfo;
        settings.HidePersonalInfo = true;
        try
        {
            using var screen = (Bitmap)desktop.Clone();
            var at = new Point(120, 140);
            using (var g = Graphics.FromImage(screen)) g.DrawImage(chat, at.X, at.Y, chat.Width, chat.Height);
            var o = newOverlay(screen);
            var sel = new Rectangle(at.X, at.Y, chat.Width, chat.Height);
            Set(o, "sel", sel);
            Set(o, "hasSel", true);
            Call(o, "StartPrivacyScan");
            using (var result = (Bitmap)Call(o, "RenderResult")!)
            {
                var shapes = Get<List<Annotation>>(o, "shapes");
                int blurs = shapes.OfType<BlurShape>().Count();
                check(blurs >= 4, $"editor blurs names before copying ({blurs} blurs)");
                check(shapes.Count > 0 && shapes[0] is BlurShape, "name blurs sit under the user's drawings");
                result.Save(Path.Combine(outDir, "21-editor-names.png"), ImageFormat.Png);
                // The blurred area really changed: compare a name's pixels with the original.
                var r = Rectangle.Round(where["Sarah Johnson"]);
                check(Difference(chat, result, r) > 8, "the name's pixels are actually blurred");
            }
            Call(o, "Undo");
            check(!Get<List<Annotation>>(o, "shapes").OfType<BlurShape>().Any(), "Ctrl+Z removes the automatic blurs");
            Call(o, "Redo");
            check(Get<List<Annotation>>(o, "shapes").OfType<BlurShape>().Count() >= 4, "Ctrl+Y puts them back");

            // The toolbar toggle: off removes only the automatic blurs (a hand-made one stays), on brings them back.
            var mine = new BlurShape { A = new PointF(sel.X + 10, sel.Bottom - 40), B = new PointF(sel.X + 120, sel.Bottom - 10), Size = 8 };
            Get<List<Annotation>>(o, "shapes").Add(mine);
            Call(o, "ToggleHidePersonal");
            var afterOff = Get<List<Annotation>>(o, "shapes").OfType<BlurShape>().ToList();
            check(!settings.HidePersonalInfo && afterOff.Count == 1 && afterOff[0] == mine, "Hide personal info off: automatic blurs go, your own blur stays");
            Call(o, "ToggleHidePersonal");
            check(settings.HidePersonalInfo && Get<List<Annotation>>(o, "shapes").OfType<BlurShape>().Count(b => b.Auto) >= 4, "Hide personal info on again: the blurs come back");
            o.Dispose();
        }
        finally
        {
            settings.HidePersonalInfo = before;
            settings.Save();
        }
    }

    /// <summary>FacePrivacy.ai's blur brush: a stroke over a name blurs what's under it, and undo takes it away.</summary>
    static void BlurBrush(string outDir, Func<Bitmap, CaptureOverlay> newOverlay, Bitmap desktop, Action<bool, string> check)
    {
        using var chat = ChatImage(out var where);
        using var screen = (Bitmap)desktop.Clone();
        var at = new Point(120, 140);
        using (var g = Graphics.FromImage(screen)) g.DrawImage(chat, at.X, at.Y, chat.Width, chat.Height);
        var o = newOverlay(screen);
        var sel = new Rectangle(at.X, at.Y, chat.Width, chat.Height);
        Set(o, "sel", sel);
        Set(o, "hasSel", true);
        Call(o, "SetTool", Tool.BlurBrush);
        var name = where["Sarah Johnson"];
        var from = new Point(at.X + (int)name.Left + 4, at.Y + (int)(name.Top + name.Height / 2));
        var to = new Point(at.X + (int)name.Right - 4, from.Y);
        Call(o, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, from.X, from.Y, 0));
        for (int i = 1; i <= 10; i++)
            Call(o, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, from.X + (to.X - from.X) * i / 10, from.Y, 0));
        Call(o, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, to.X, to.Y, 0));
        var shapes = Get<List<Annotation>>(o, "shapes");
        var brush = shapes.OfType<BlurBrushShape>().FirstOrDefault();
        check(brush != null && brush.Points.Count >= 5, $"blur brush stroke recorded ({brush?.Points.Count ?? 0} points)");
        using (var result = (Bitmap)Call(o, "RenderResult")!)
        {
            check(Difference(chat, result, Rectangle.Round(name)) > 8, "the brush blurs the name under it");
            var clean = Rectangle.Round(where["line2:Meeting notes from the design review"]);
            check(Difference(chat, result, clean) < 1, "pixels away from the brush stay sharp");
            result.Save(Path.Combine(outDir, "23-blur-brush.png"), ImageFormat.Png);
        }
        Call(o, "Undo");
        check(!Get<List<Annotation>>(o, "shapes").OfType<BlurBrushShape>().Any(), "undo removes the brush stroke");
        o.Dispose();
    }

    /// <summary>Emails, phone, card and social security numbers and keywords; dates, times and amounts left alone.</summary>
    internal static void PersonalDetails(Action<bool, string> check)
    {
        void Expect(string line, string[] keywords, params string[] wanted)
        {
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var found = LiteShot.PersonalInfo.FindInLine(words, keywords)
                .Select(s => string.Join(" ", words.Skip(s.Start).Take(s.Count)).Trim(',', '.', ':', '?')).Distinct().ToList();
            bool ok = wanted.All(w => found.Contains(w)) && found.Count == wanted.Length;
            check(ok, $"personal info in \"{line}\" → [{string.Join(" | ", found)}]");
        }
        string[] none = Array.Empty<string>();
        Expect("Write to sarah.j@acme.com today", none, "sarah.j@acme.com");
        Expect("Call (555) 123-4567 or +1 555 987 6543", none, "(555) 123-4567", "+1 555 987 6543");
        Expect("Card 4111 1111 1111 1111 expires", none, "4111 1111 1111 1111");
        Expect("SSN 123-45-6789 on file", none, "123-45-6789");
        Expect("Ticket for Project Falcon at Acme Corp", new[] { "project falcon", "Acme Corp" }, "Project Falcon", "Acme Corp");
        Expect("Meeting 10:42 AM on 2026-10-28, revenue up 24% to $4.2M", none);
    }

    static void PersonalOnScreen(string outDir, Action<bool, string> check)
    {
        using var bmp = new Bitmap(900, 120, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using var font = new Font("Segoe UI", 22, GraphicsUnit.Pixel);
            g.DrawString("Reach me at jordan.miles@example.com or (555) 201-7788.", font, Brushes.Black, 20, 40);
        }
        var found = Task.Run(() => LiteShot.PersonalInfo.FindAsync(bmp, Array.Empty<string>(), faces: false)).GetAwaiter().GetResult();
        check(found.Count >= 2 && found.All(f => !f.Face), $"OCR finds the email and phone number ({found.Count} areas)");
        using var copy = (Bitmap)bmp.Clone();
        int n = Task.Run(() => LiteShot.PersonalInfo.BlurAsync(copy, Array.Empty<string>())).GetAwaiter().GetResult();
        copy.Save(Path.Combine(outDir, "24-personal-info.png"), ImageFormat.Png);
        check(n >= 2, "personal details are blurred in place");
    }

    static double Difference(Bitmap a, Bitmap b, Rectangle r)
    {
        double sum = 0;
        int n = 0;
        for (int y = r.Top; y < r.Bottom; y += 2)
            for (int x = r.Left; x < r.Right; x += 2)
            {
                var p = a.GetPixel(x, y);
                var q = b.GetPixel(x, y);
                sum += Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B);
                n++;
            }
        return sum / Math.Max(1, n) / 3;
    }

    // ───────────────────────────── faces ─────────────────────────────

    static void Faces(string outDir, Action<bool, string> check)
    {
        check(FaceFinder.Supported, "Windows face detection is available");
        if (!FaceFinder.Supported) return;
        using var none = new Bitmap(600, 400, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(none)) g.Clear(Color.FromArgb(240, 240, 240));
        var empty = Task.Run(() => FaceFinder.FindAsync(none)).GetAwaiter().GetResult();
        check(empty.Count == 0, "no faces in a blank image");

        // An oval blur over a face-sized area, as the face button makes.
        using var chat = ChatImage(out _);
        var blur = new BlurShape { A = new PointF(560, 20), B = new PointF(700, 190), Oval = true, Size = 20 };
        using (var g = Graphics.FromImage(chat))
        {
            using var source = (Bitmap)chat.Clone();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            blur.Draw(g, source);
        }
        chat.Save(Path.Combine(outDir, "22-oval-blur.png"), ImageFormat.Png);
        check(blur.Hit(new PointF(630, 105), 0) && !blur.Hit(new PointF(562, 22), 0), "oval blur hit-tests as an oval");
    }

    // ───────────────────────────── updates ─────────────────────────────

    static void Updates(string server, Action<bool, string> check)
    {
        var settings = AppSettings.Current;
        string? savedServer = settings.UpdateServer;
        string savedChannel = settings.UpdateChannel;
        settings.UpdateServer = server;
        string updates = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteShot", "Updates", "9.9.9");
        try
        {
            check(Updater.Configured, $"updater configured (server {Updater.Server}, version {Updater.CurrentVersion})");

            settings.UpdateChannel = "stable";
            var info = Run(() => Updater.CheckAsync());
            check(info?.Version == new Version(9, 9, 9), $"signed release found: {info?.Version}");
            if (info != null)
            {
                string file = Run(() => Updater.DownloadAsync(info, installed: false));
                var expected = Updater.FileFor(info, installed: false)!;
                check(File.Exists(file) && new FileInfo(file).Length == expected.Size, $"portable download verified ({expected.Size} bytes)");
                string again = Run(() => Updater.DownloadAsync(info, installed: false));
                check(again == file, "a verified download is reused");
            }

            settings.UpdateChannel = "tamper";
            check(Throws<UpdateException>(() => Updater.CheckAsync(), "signed"), "a release with a broken signature is refused");
            settings.UpdateChannel = "other-key";
            check(Throws<UpdateException>(() => Updater.CheckAsync(), "signed"), "a release signed with another key is refused");
            settings.UpdateChannel = "tamper-files";
            var swapped = Run(() => Updater.CheckAsync());
            check(swapped != null && Throws<UpdateException>(() => Updater.DownloadAsync(swapped, installed: false), "match"),
                "a file changed on the server after signing is refused");
            settings.UpdateChannel = "no-such-channel";
            check(Run(() => Updater.CheckAsync()) == null, "an empty channel means no update");
            check(Throws<UpdateException>(() => Task.FromResult(Updater.Parse("{\"payload\":\"x\"}", server)), ""), "garbage from the server is refused");

            // The portable swap: the running exe moves aside and the new one takes its name.
            string dir = Path.Combine(Path.GetTempPath(), "liteshot-swap-test");
            Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, "LiteShot.exe"), next = Path.Combine(dir, "next.bin");
            File.WriteAllText(exe, "old");
            File.WriteAllText(next, "new");
            File.Delete(exe + ".old");
            Updater.SwapExe(next, exe);
            check(File.ReadAllText(exe) == "new" && File.ReadAllText(exe + ".old") == "old", "portable update swaps the exe and keeps the old one aside");
            Directory.Delete(dir, recursive: true);
        }
        finally
        {
            settings.UpdateServer = savedServer;
            settings.UpdateChannel = savedChannel;
            try { if (Directory.Exists(updates)) Directory.Delete(updates, recursive: true); } catch { }
        }
    }

    static T Run<T>(Func<Task<T>> f) => Task.Run(f).GetAwaiter().GetResult();

    static bool Throws<T>(Func<Task> f, string messagePart) where T : Exception
    {
        try { Task.Run(f).GetAwaiter().GetResult(); return false; }
        catch (T ex) { return ex.Message.Contains(messagePart, StringComparison.OrdinalIgnoreCase); }
    }

    // ───────────────────────────── reflection ─────────────────────────────

    static T Get<T>(object o, string name) => (T)o.GetType().GetField(name, Any)!.GetValue(o)!;
    static void Set(object o, string name, object value) => o.GetType().GetField(name, Any)!.SetValue(o, value);
    static object? Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, Any)!.Invoke(o, args);
}
