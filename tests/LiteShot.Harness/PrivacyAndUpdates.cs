using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text;
using LiteShot;
using LiteShot.Core;
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
        NamesInTable(outDir, check);
        NamesInFeed(outDir, check);
        UsageCount(check);
        NamesOnScreen(outDir, newOverlay, desktop, check);
        BlurBrush(outDir, newOverlay, desktop, check);
        Faces(outDir, check);
        VersionChanges(check);
        if (updateServer != null)
        {
            Updates(updateServer, check);
            ManualUpdates(updateServer, outDir, check);
        }
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
        Expect("Press H to hide it. Can I help? Our San Francisco office", new string[0]);
        Expect("Thanks to Mark S. for the notes", "Mark S");
        Expect("JOHN SMITH SAVE CHANGES", "JOHN SMITH");
        Expect("MARTA KVELLESTAD", "MARTA KVELLESTAD");
        Expect("MARTA KVELLESTAD SAVE CHANGES", "MARTA KVELLESTAD");
        Expect("Ready: MARTA J KVELLESTAD is ready", "MARTA J KVELLESTAD");
        Expect("Name ROSALIND T. QUENNEVAL", "ROSALIND T. QUENNEVAL");
        Expect("Install Norton Security", new string[0]);
        Expect("SALES REPORT EXPORT", new string[0]);
        Expect("On it. Marcus Lee already shared the draft.", "Marcus Lee");
        // Particles, suffixes, longer names, and a first name the lists don't know before a known surname.
        // Every name in these checks and the tables below is made up.
        Expect("Valentina Robles de la Fuente joined", "Valentina Robles de la Fuente");
        Expect("Tavita Lua Aumalo Jr", "Tavita Lua Aumalo Jr");
        Expect("Assigned to Ronan Kilbride today", "Ronan Kilbride");
        Expect("Farhan Siddiqui and Noura Almutabbaq", "Farhan Siddiqui", "Noura Almutabbaq");
        Expect("Ngozi van der Berg", "Ngozi van der Berg");
        Expect("Henry III signed", "Henry III");
        Expect("Tavelu Nguyen replied", "Tavelu Nguyen");
        Expect("Visit Saudi Arabia or the United Kingdom", new string[0]);
        Expect("Austin Riverside Tour", new string[0]);
        // Names the lists don't know, given away by the words around them; OCR running a name into an ellipsis.
        Expect("Darvin Kesselring emailed Maelis Ondrako", "Darvin Kesselring", "Maelis Ondrako");
        Expect("emailed Tamsin Vellacourt", "Tamsin Vellacourt");
        Expect("Hi Ysolde, the draft is ready", "Ysolde");
        Expect("Hello World! Dear Customer, hi Everyone", new string[0]);
        Expect("Tamsk replied to your note", "Tamsk");
        Expect("No problem, Kevin...sounds like it could be a problem", "Kevin...sounds");
        Expect("Colleen Marbury", "Colleen Marbury");
        Expect("wrote a note on Brightwave Capital Group", new string[0]);
        Expect("Visit Jordan or install Norton Security", new string[0]);
        Expect("Emailed yesterday", new string[0]);
        Expect("We emailed Customer Support and Billing replied", new string[0]);
    }

    /// <summary>
    /// A CRM activity feed at small screen size: senders, recipients in link blue, a first name run into "...", and
    /// contacts under company names in a leads list. Every name must be hidden; companies and subjects must not.
    /// </summary>
    internal static void NamesInFeed(string outDir, Action<bool, string> check)
    {
        using var feed = new Bitmap(1100, 360, PixelFormat.Format32bppArgb);
        var names = new Dictionary<string, List<RectangleF>>();
        var others = new Dictionary<string, RectangleF>();
        using (var g = Graphics.FromImage(feed))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using var font = new Font("Segoe UI", 13, GraphicsUnit.Pixel);
            using var ink = new SolidBrush(Color.FromArgb(40, 44, 52));
            using var link = new SolidBrush(Color.FromArgb(37, 99, 235));
            using var green = new SolidBrush(Color.FromArgb(22, 150, 90));
            var format = StringFormat.GenericTypographic;
            float space = g.MeasureString("a a", font, PointF.Empty, format).Width - g.MeasureString("aa", font, PointF.Empty, format).Width;
            // Parts: "*Name" is a name, "~text" is text that must stay readable, anything else is filler. "!" marks link blue.
            void Line(float x, float y, params string[] parts)
            {
                foreach (var part in parts)
                {
                    bool name = part.StartsWith('*'), keep = part.StartsWith('~');
                    string text = name || keep ? part[1..] : part;
                    bool blue = text.StartsWith('!');
                    if (blue) text = text[1..];
                    if (text.StartsWith("...")) x -= space;   // runs on from the word before it, as in "Kevin...sounds"
                    var brush = blue ? link : keep && text.Contains("Capital") ? green : ink;
                    var boxes = new List<RectangleF>();
                    foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var size = g.MeasureString(word, font, PointF.Empty, format);
                        g.DrawString(word, font, brush, x, y, format);
                        boxes.Add(new RectangleF(x, y, size.Width, size.Height));
                        x += size.Width + space;
                    }
                    if (name) names[text] = boxes;
                    else if (keep) for (int i = 0; i < boxes.Count; i++) others[$"{text} ({i + 1})"] = boxes[i];
                }
            }
            Line(30, 24, "*Oriel Tamsk", "~wrote", "a note on", "~Brightwave Capital Group");
            Line(30, 46, "Is anyone available to assist on a live demo this Thursday at 2:00pm?");
            Line(30, 104, "*Darvin Pellicott", "emailed", "*!Maelis Ondrako");
            Line(30, 126, "Re:", "~Solar inverter issue");
            Line(30, 148, "No problem,", "*Kevin", "...sounds like it could be a problem with the charge controller.");
            Line(30, 206, "*Corvin Ashgrove", "emailed", "*!Tamsin Vellacourt");
            Line(30, 228, "Re:", "~Greetings from Northwind");
            Line(30, 250, "~Absolutely!", "I've attached a guide with the product specs and some cost estimates.");
            Line(720, 104, "~Northwind Solar");
            Line(720, 124, "*Colleen Marbury");
            Line(720, 164, "~Lumafield Labs");
            Line(720, 184, "*Gerald Thornquist");
        }
        var found = Task.Run(() => LiteShot.PersonalInfo.FindAsync(feed, Array.Empty<string>(), faces: false)).GetAwaiter().GetResult()
            .Select(r => r.Area).ToList();
        bool Covered(RectangleF word) => found.Any(f => f.Contains(Point.Round(Center(word))));
        var missed = names.Where(n => !n.Value.All(Covered)).Select(n => n.Key).ToList();
        check(missed.Count == 0, $"CRM feed: all {names.Count} names hidden, including ones in no list" + (missed.Count > 0 ? $" (still readable: {string.Join(", ", missed)})" : ""));
        var exposed = others.Where(o => Covered(o.Value)).Select(o => o.Key).ToList();
        check(exposed.Count == 0, "CRM feed: companies and subjects stay readable" + (exposed.Count > 0 ? $" (hidden: {string.Join(", ", exposed)})" : ""));
        using var shown = (Bitmap)feed.Clone();
        using (var g = Graphics.FromImage(shown))
        {
            using var source = (Bitmap)feed.Clone();
            foreach (var r in found) NameFinder.BlurFor(r).Draw(g, source);
        }
        shown.Save(Path.Combine(outDir, "34-crm-feed.png"), ImageFormat.Png);
    }

    /// <summary>
    /// "Hide personal info" on a web admin table: a NAME column (flag, name, sometimes a green verified tick) with made-up
    /// names from many cultures, beside email, country, role and status columns and buttons. Every word of every name
    /// must be covered; headers, countries (one of them also a first name), roles and buttons must not.
    /// </summary>
    internal static void NamesInTable(string outDir, Action<bool, string> check)
    {
        foreach (var (header, unfamiliar) in new[] { (true, false), (false, false), (true, true) })
        {
            string what = unfamiliar ? "a NAME column of names in none of LiteShot's lists" : header ? "admin table" : "the same table without its header row";
            using var table = AdminTable(header, unfamiliar, out var names, out var others);
            var found = Task.Run(() => LiteShot.PersonalInfo.FindAsync(table, Array.Empty<string>(), faces: false)).GetAwaiter().GetResult()
                .Select(r => r.Area).ToList();
            bool Covered(RectangleF word) => found.Any(f => f.Contains(Point.Round(Center(word))));
            var missed = names.Where(n => !n.Value.All(Covered)).Select(n => n.Key).ToList();
            check(missed.Count == 0, $"{what}: every word of all {names.Count} names is hidden" + (missed.Count > 0 ? $" (still readable: {string.Join(", ", missed)})" : ""));
            var exposed = others.Where(o => Covered(o.Value)).Select(o => o.Key).ToList();
            check(exposed.Count == 0, $"{what}: headers, countries, roles and buttons stay readable" + (exposed.Count > 0 ? $" (hidden: {string.Join(", ", exposed)})" : ""));

            using var shown = (Bitmap)table.Clone();
            using (var g = Graphics.FromImage(shown))
            {
                using var source = (Bitmap)table.Clone();
                foreach (var r in found) NameFinder.BlurFor(r).Draw(g, source);
            }
            shown.Save(Path.Combine(outDir, unfamiliar ? "33-admin-table-unfamiliar.png" : header ? "31-admin-table.png" : "32-admin-table-no-header.png"), ImageFormat.Png);
        }
    }

    /// <summary>
    /// A users table, 1280 × 760, with or without its header row. <paramref name="names"/> maps each person to the
    /// bounds of each word of their name; <paramref name="others"/> holds text that must stay readable. The last name is
    /// in none of LiteShot's lists, so only the layout can give it away; <paramref name="unfamiliar"/> makes them all so.
    /// </summary>
    static Bitmap AdminTable(bool header, bool unfamiliar, out Dictionary<string, List<RectangleF>> names, out Dictionary<string, RectangleF> others)
    {
        // All made up: a first name that's also a place, Irish, a "de la" surname, Arabic, Samoan-style with "Jr", South
        // Asian, a long family name, and one in no list at all. The strangers are in no list either.
        string[] strangers = { "Tavelu Moengaro", "Leleuma Fauvea", "Kaimelu Kahalopa", "Ruawehi Tamarowa", "Onelia Tsenako",
            "Bayarsul Dornovic", "Zhanira Abelkova", "Ihamu Pohareti", "Arovea Kerevin", "Selvio Marandu", "Manaia Torowhenua" };
        var rows = new (string Name, Color[] Flag, bool Verified, string Email, string Country, string Role, string Status)[]
        {
            ("Austin Pemberly", new[] { Color.FromArgb(213, 43, 30), Color.White, Color.FromArgb(213, 43, 30) }, true, "a.pemberly@maple.ca", "Canada", "Admin", "Active"),
            ("Ronan Kilbride", new[] { Color.FromArgb(1, 33, 105), Color.FromArgb(1, 33, 105), Color.White }, false, "ronan@kilbride.au", "Australia", "Editor", "Active"),
            ("Valentina Robles de la Fuente", new[] { Color.White, Color.FromArgb(213, 43, 30), Color.FromArgb(0, 57, 166) }, true, "vrobles@empresa.cl", "Chile", "Viewer", "Invited"),
            ("Ziad Hamdan", new[] { Color.FromArgb(237, 28, 36), Color.White, Color.FromArgb(0, 166, 81) }, false, "z.hamdan@cedar.lb", "Lebanon", "Editor", "Active"),
            ("Annika Brandauer", new[] { Color.White, Color.FromArgb(255, 0, 0), Color.White }, true, "annika@brandauer.ge", "Georgia", "Owner", "Active"),
            ("Tavita Lua Aumalo Jr", new[] { Color.FromArgb(0, 36, 125), Color.FromArgb(204, 20, 43), Color.FromArgb(0, 36, 125) }, false, "tavita.aumalo@pacific.nz", "New Zealand", "Viewer", "Suspended"),
            ("Farhan Siddiqui", new[] { Color.FromArgb(0, 106, 78), Color.FromArgb(244, 42, 65), Color.FromArgb(0, 106, 78) }, true, "farhan.s@delta.bd", "Bangladesh", "Admin", "Active"),
            ("Noura Almutabbaq", new[] { Color.FromArgb(0, 122, 61), Color.White, Color.FromArgb(206, 17, 38) }, false, "noura.m@gulf.kw", "Kuwait", "Editor", "Active"),
            ("Emma Clarke", new[] { Color.FromArgb(1, 33, 105), Color.FromArgb(200, 16, 46), Color.FromArgb(1, 33, 105) }, false, "emma.clarke@thames.co.uk", "United Kingdom", "Viewer", "Active"),
            ("Wei Zhang", new[] { Color.FromArgb(238, 28, 37), Color.White, Color.FromArgb(238, 28, 37) }, true, "wei.zhang@lotus.sg", "Singapore", "Editor", "Active"),
            ("Manaia Torowhenua", new[] { Color.FromArgb(0, 40, 104), Color.FromArgb(207, 20, 43), Color.FromArgb(104, 191, 229) }, false, "manaia@reef.fj", "Fiji", "Viewer", "Active"),
        };
        var bmp = new Bitmap(1280, 760, PixelFormat.Format32bppArgb);
        names = new();
        others = new();
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(246, 247, 249));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var title = new Font("Segoe UI Semibold", 24, GraphicsUnit.Pixel);
        using var font = new Font("Segoe UI", 14, GraphicsUnit.Pixel);
        using var head = new Font("Segoe UI Semibold", 12, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Color.FromArgb(28, 30, 36));
        using var dim = new SolidBrush(Color.FromArgb(110, 116, 128));
        var format = StringFormat.GenericTypographic;
        var others2 = others;
        void Text(string text, Font f, Brush b, float x, float y, string? key = null)
        {
            g.DrawString(text, f, b, x, y, format);
            if (key != null) others2[key] = new RectangleF(new PointF(x, y), g.MeasureString(text, f, PointF.Empty, format));
        }

        Text("Users", title, ink, 32, 20, "title Users");
        using (var button = new SolidBrush(Color.FromArgb(10, 132, 255))) g.FillRectangle(button, 1080, 22, 160, 36);
        Text("Add user", font, Brushes.White, 1126, 31, "button Add user");
        Text("Export", font, ink, 990, 31, "button Export");
        using (var card = new SolidBrush(Color.White)) g.FillRectangle(card, 24, 76, 1232, 642);
        float[] col = { 64, 400, 650, 830, 960, 1100 };
        string[] headers = { "NAME", "EMAIL", "COUNTRY", "ROLE", "STATUS", "ACTIONS" };
        if (header) for (int i = 0; i < headers.Length; i++) Text(headers[i], head, dim, col[i], 98, $"header {headers[i]}");
        using var line = new Pen(Color.FromArgb(232, 234, 238));
        for (int r = 0; r < rows.Length; r++)
        {
            var row = rows[r];
            if (unfamiliar) row.Name = strangers[r];
            float y = 130 + r * 52;
            g.DrawLine(line, 40, y - 8, 1240, y - 8);
            for (int s = 0; s < 3; s++)
                using (var stripe = new SolidBrush(row.Flag[s])) g.FillRectangle(stripe, col[0] + s * 8, y + 4, 8, 14);
            float x = col[0] + 34;
            var words = new List<RectangleF>();
            foreach (var word in row.Name.Split(' '))
            {
                var size = g.MeasureString(word, font, PointF.Empty, format);
                g.DrawString(word, font, ink, x, y, format);
                words.Add(new RectangleF(x, y, size.Width, size.Height));
                x += size.Width + g.MeasureString(" ", font, PointF.Empty, StringFormat.GenericDefault).Width * 0.6f;
            }
            names[row.Name] = words;
            if (row.Verified)
            {
                using var tick = new SolidBrush(Color.FromArgb(52, 199, 89));
                g.FillEllipse(tick, x + 4, y + 2, 16, 16);
                using var mark = new Pen(Color.White, 2f);
                g.DrawLines(mark, new[] { new PointF(x + 8, y + 10), new PointF(x + 11, y + 13), new PointF(x + 16, y + 7) });
            }
            Text(row.Email, font, dim, col[1], y);
            Text(row.Country, font, ink, col[2], y, $"country {row.Country} ({r + 1})");
            Text(row.Role, font, ink, col[3], y, $"role {row.Role} ({r + 1})");
            Text(row.Status, font, ink, col[4], y, $"status {row.Status} ({r + 1})");
            Text("Edit", font, ink, col[5], y, $"button Edit ({r + 1})");
            Text("Delete", font, ink, col[5] + 50, y, $"button Delete ({r + 1})");
        }
        Text("Showing 1–11 of 248 users", font, dim, 40, 728, "footer");
        return bmp;
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
            g.DrawString("Reach me at casey.miles@example.com or (555) 201-7788.", font, Brushes.Black, 20, 40);
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

    /// <summary>
    /// YuNet finds every face in a video call screenshot (a large speaker, tiles, small avatars) and none in text;
    /// Shield blur covers each one, changes nothing outside the ovals, and leaves no face for YuNet to find.
    /// </summary>
    internal static void Faces(string outDir, Action<bool, string> check)
    {
        check(FaceFinder.UsingYuNet, "the YuNet face detector loads (ONNX Runtime)");
        if (!FaceFinder.UsingYuNet) return;
        using var yunet = new YuNetDetector();
        List<DetectedFace> Detect(Bitmap b) => BitmapPixels.Read(b, (bgra, stride) => yunet.Detect(bgra, b.Width, b.Height, stride));

        using var none = new Bitmap(600, 400, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(none)) g.Clear(Color.FromArgb(240, 240, 240));
        check(Detect(none).Count == 0, "no faces in a blank image");
        using (var text = ChatImage(out _)) check(Detect(text).Count == 0, "no faces in a chat full of text");
        using (var desk = Program.SyntheticDesktop(new Size(2560, 1440))) check(Detect(desk).Count == 0, "no faces on a desktop of windows, code and charts");
        using (var print = SmallPrint())
        {
            var found = Detect(print);
            check(found.Count == 0, $"no faces in small print ({found.Count} found{string.Concat(found.Select(f => $": {Rectangle.Round(f.Box)} at {f.Score:F2}"))})");
        }

        // Each portrait has one face; its box decides how the portrait is cropped into the call below.
        string dir = Path.Combine(AppContext.BaseDirectory, "Faces");
        var people = new[] { "jemison", "glover", "ride", "chawla", "armstrong", "ochoa" }
            .Select(n => (Name: n, Photo: new Bitmap(Path.Combine(dir, n + ".jpg")))).ToList();
        try
        {
            var faceIn = new Dictionary<string, RectangleF>();
            foreach (var (name, photo) in people)
            {
                var found = Detect(photo);
                check(found.Count == 1, $"one face in the {name} portrait ({found.Count} found{string.Concat(found.Select(f => $": {Rectangle.Round(f.Box)} at {f.Score:F2}"))})");
                if (found.Count > 0) faceIn[name] = found[0].Box;
            }
            if (faceIn.Count == people.Count)
            {
                FacesInCall(outDir, check, Detect, people, faceIn);
                ProfileCards(outDir, check, Detect, people[0].Photo, faceIn[people[0].Name]);
                // Before tiling and the 2× pass these found 24/25, 21/30 and 5/12.
                using (var page = TeamPage(people, faceIn, out var truth))
                    Crowd(outDir, check, Detect, page, truth, 25, "a team page of 25 faces from 16 to 120 px", "28-team-page");
                // The faces still missed here are 12 to 14 px, grey and tilted.
                using (var gallery = Gallery(people, faceIn, out var truth))
                    Crowd(outDir, check, Detect, gallery, truth, 28, "a gallery of 30 faces from 12 to 28 px, some tilted, grey or dim", "30-gallery");
                using (var wide = WideDesktop(people, faceIn, out var truth))
                    Crowd(outDir, check, Detect, wide, truth, 12, "a 5120 px two-monitor desktop with 12 faces of 18 to 40 px", "29-wide-desktop");
            }
        }
        finally
        {
            foreach (var p in people) p.Photo.Dispose();
        }

        // A hand-placed oval blur hit-tests as an oval.
        var blur = new BlurShape { A = new PointF(560, 20), B = new PointF(700, 190), Oval = true, Size = ShieldBlur.DefaultStrength };
        using (var chat = ChatImage(out _))
        {
            using (var source = (Bitmap)chat.Clone())
            using (var g = Graphics.FromImage(chat)) blur.Draw(g, source);
            chat.Save(Path.Combine(outDir, "22-oval-blur.png"), ImageFormat.Png);
        }
        check(blur.Hit(new PointF(630, 105), 0) && !blur.Hit(new PointF(562, 22), 0), "oval blur hit-tests as an oval");
    }

    static void FacesInCall(string outDir, Action<bool, string> check, Func<Bitmap, List<DetectedFace>> detect,
        List<(string Name, Bitmap Photo)> people, Dictionary<string, RectangleF> faceIn)
    {
        using var call = VideoCall(people, faceIn, out var spots);
        var clock = Stopwatch.StartNew();
        var faces = detect(call);
        clock.Stop();
        bool OneEach(IEnumerable<RectangleF> found) => spots.All(s => found.Count(f => s.Contains(Center(f))) == 1);
        check(faces.Count == spots.Count && OneEach(faces.Select(f => f.Box)),
            $"YuNet finds all {spots.Count} faces in a video call, big and small ({faces.Count} found in {clock.ElapsedMilliseconds} ms)");

        var ovals = Task.Run(() => FaceFinder.FindAsync(call)).GetAwaiter().GetResult();
        check(ovals.Count == spots.Count && faces.All(f => ovals.Any(o => RectangleF.Inflate(o, 1, 1).Contains(f.Box))),
            "each oval takes in the face's whole box, plus a margin");
        var auto = Task.Run(() => LiteShot.PersonalInfo.FindAsync(call, Array.Empty<string>(), faces: true)).GetAwaiter().GetResult();
        check(auto.Count(r => r.Face) == spots.Count, $"Hide personal info finds the faces too ({auto.Count(r => r.Face)})");

        using (var marked = (Bitmap)call.Clone())
        {
            using var g = Graphics.FromImage(marked);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var boxPen = new Pen(Color.FromArgb(255, 220, 60), 1.5f);
            using var ovalPen = new Pen(Color.FromArgb(255, 70, 90), 2f);
            foreach (var f in faces) g.DrawRectangle(boxPen, Rectangle.Round(f.Box));
            foreach (var o in ovals) g.DrawEllipse(ovalPen, o);
            marked.Save(Path.Combine(outDir, "25-faces-found.png"), ImageFormat.Png);
        }

        // Shield blur over every face, drawn the way the editor and "Hide personal info" draw it.
        var blurs = ovals.Select(o => LiteShot.PersonalInfo.BlurFor(new Redaction(o, true))).ToList();
        using var blurred = (Bitmap)call.Clone();
        clock.Restart();
        using (var g = Graphics.FromImage(blurred)) foreach (var b in blurs) b.Draw(g, call);
        clock.Stop();
        blurred.Save(Path.Combine(outDir, "26-faces-shield.png"), ImageFormat.Png);

        var before = Luma(call);
        var after = Luma(blurred);
        int changedOutside = 0;
        for (int y = 0; y < call.Height; y++)
            for (int x = 0; x < call.Width; x++)
                if (before[y * call.Width + x] != after[y * call.Width + x] && !blurs.Any(b => b.Bounds.Contains(x, y))) changedOutside++;
        check(changedOutside == 0, $"Shield blur changes nothing outside the ovals ({changedOutside} pixels changed, {clock.ElapsedMilliseconds} ms for {blurs.Count} faces)");
        check(ovals.All(o => Difference(call, blurred, Inner(o)) > 6), "every face's pixels are replaced");

        var speaker = ovals.OrderByDescending(o => o.Width * o.Height).First();
        double sharp = Detail(before, call.Width, Inner(speaker)), soft = Detail(after, call.Width, Inner(speaker));
        // Shield blur keeps the head's outline and colour on purpose, so a detector may still see a head-shaped blob;
        // what must go is the detail inside it.
        check(soft < sharp * 0.15, $"the speaker's facial detail is gone (edge energy {soft:F1} vs {sharp:F1})");

        // Every blur is drawn from fresh randomness: the same oval twice gives two different results.
        using var again = (Bitmap)call.Clone();
        using (var g = Graphics.FromImage(again)) LiteShot.PersonalInfo.BlurFor(new Redaction(speaker, true)).Draw(g, call);
        check(Difference(blurred, again, Inner(speaker)) > 0.5, "each Shield blur uses new random noise");

        // Before and after, side by side.
        var area = Rectangle.Intersect(Rectangle.Inflate(speaker, 40, 40), new Rectangle(Point.Empty, call.Size));
        using (var pair = new Bitmap(area.Width * 2 + 12, area.Height, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(pair))
            {
                g.Clear(Color.White);
                g.DrawImage(call, new Rectangle(0, 0, area.Width, area.Height), area, GraphicsUnit.Pixel);
                g.DrawImage(blurred, new Rectangle(area.Width + 12, 0, area.Width, area.Height), area, GraphicsUnit.Pixel);
            }
            pair.Save(Path.Combine(outDir, "27-shield-closeup.png"), ImageFormat.Png);
        }
    }

    /// <summary>
    /// A 1600 × 900 video call: the speaker large (a face of about 280 px), four tiles beside (about 90 px), a chat, and
    /// six round avatars of 40 px with names below. <paramref name="spots"/> is where each face is drawn.
    /// </summary>
    static Bitmap VideoCall(List<(string Name, Bitmap Photo)> people, Dictionary<string, RectangleF> faceIn, out List<RectangleF> spots)
    {
        var bmp = new Bitmap(1600, 900, PixelFormat.Format32bppArgb);
        var placed = new List<RectangleF>();
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(30, 31, 36));
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        void Tile(int person, RectangleF tile, float share, bool round)
        {
            PlaceFace(g, people[person].Photo, faceIn[people[person].Name], tile, share, round);
            placed.Add(tile);
        }

        Tile(0, new RectangleF(16, 16, 1040, 700), 0.40f, round: false);
        for (int i = 0; i < 4; i++) Tile(1 + i, new RectangleF(1072 + i % 2 * 264, 16 + i / 2 * 216, 248, 200), 0.45f, round: false);

        using var font = new Font("Segoe UI", 15, GraphicsUnit.Pixel);
        using var bold = new Font("Segoe UI Semibold", 15, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Color.FromArgb(225, 228, 235));
        using var dim = new SolidBrush(Color.FromArgb(150, 155, 165));
        using (var panel = new SolidBrush(Color.FromArgb(40, 42, 48))) g.FillRectangle(panel, 1072, 448, 512, 268);
        string[] chat = { "Victor: Slides are in the shared folder.", "Sally: Agenda for Thursday is posted.",
            "Kalpana: I'll take the second review.", "Neil: Joining from the lab, audio only.", "Ellen: Thanks everyone, see you then." };
        g.DrawString("Chat", bold, ink, 1088, 460);
        for (int i = 0; i < chat.Length; i++) g.DrawString(chat[i], font, dim, 1088, 494 + i * 40);

        g.DrawString("In this call · 6", bold, ink, 24, 740);
        string[] names = { "Mae Jemison", "Victor Glover", "Sally Ride", "Kalpana Chawla", "Neil Armstrong", "Ellen Ochoa" };
        for (int i = 0; i < people.Count; i++)
        {
            var avatar = new RectangleF(24 + i * 262, 790, 40, 40);
            Tile(i, avatar, 0.62f, round: true);
            g.DrawString(names[i], font, ink, avatar.Right + 12, avatar.Y + 9);
        }
        spots = placed;
        return bmp;
    }

    /// <summary>
    /// Side profiles: a page of photo cards, nine heads turned to the side (public-domain photos, facing left and right,
    /// one with a full beard) and one facing the camera, cropped loosely to tightly, some flat or dim; the hardest, tight
    /// and flat, scores under YuNet's usual threshold. Every face must be found with nothing else, and each profile's oval
    /// must reach past the face toward the back of the head (the ear) and below it (a beard).
    /// </summary>
    static void ProfileCards(string outDir, Action<bool, string> check, Func<Bitmap, List<DetectedFace>> detect, Bitmap frontal, RectangleF frontalFace)
    {
        // Each card: fixture, how much of the card's height the face fills, contrast, light.
        var cards = new (string File, float Fill, float Contrast, float Light)[]
        {
            ("profile-1.jpg", 0.6f, 1, 1), ("profile-2.jpg", 0.9f, 0.5f, 1), ("profile-3.jpg", 0.7f, 1, 0.7f), ("profile-4.jpg", 0.6f, 0.6f, 1),
            ("profile-5.jpg", 0.9f, 1, 1), ("profile-6.jpg", 0.7f, 0.5f, 1), ("profile-7.jpg", 0.8f, 1, 0.7f), ("profile-8.jpg", 0.9f, 0.6f, 1),
            ("profile-9.jpg", 0.5f, 1, 1),
        };
        string dir = Path.Combine(AppContext.BaseDirectory, "Faces");
        var photos = cards.Select(c => new Bitmap(Path.Combine(dir, c.File))).ToList();
        try
        {
            // Where each face is, and which way it looks, from the photo at full size.
            var faces = photos.Select(p => detect(p).OrderByDescending(f => f.Box.Width * f.Box.Height).FirstOrDefault()).ToList();
            check(faces.All(f => f.Score >= 0.6f && MathF.Abs(f.Turn) >= 0.12f),
                $"each profile photo's face is found and reads as turned sideways (turns {string.Join(", ", faces.Select(f => f.Turn.ToString("0.00")))})");
            if (!faces.All(f => f.Score > 0)) return;

            using var page = new Bitmap(1600, 900, PixelFormat.Format32bppArgb);
            var truth = new List<(RectangleF Box, float Turn)>();
            using (var g = Graphics.FromImage(page))
            {
                g.Clear(Color.FromArgb(245, 246, 248));
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using var font = new Font("Segoe UI", 14, GraphicsUnit.Pixel);
                using var ink = new SolidBrush(Color.FromArgb(60, 64, 72));
                for (int i = 0; i <= cards.Length; i++)
                {
                    var tile = new RectangleF(60 + i % 5 * 300, 90 + i / 5 * 400, 220, 260);
                    if (i == cards.Length)
                    {
                        truth.Add((PlaceFace(g, frontal, frontalFace, tile, 0.6f, round: false), 0));
                        g.DrawString("Front", font, ink, tile.X, tile.Bottom + 10);
                        continue;
                    }
                    var c = cards[i];
                    truth.Add((PlaceFace(g, photos[i], faces[i].Box, tile, c.Fill, round: false, light: c.Light, contrast: c.Contrast), faces[i].Turn));
                    g.DrawString("Side profile", font, ink, tile.X, tile.Bottom + 10);
                }
            }

            var clock = Stopwatch.StartNew();
            var found = detect(page);
            clock.Stop();
            var (hits, missed, extra) = Score(truth.Select(t => t.Box).ToList(), found.Select(f => f.Box));
            check(missed.Count == 0 && extra.Count == 0, $"photo cards: all {truth.Count - 1} side profiles and the frontal face found in {clock.ElapsedMilliseconds} ms" +
                (missed.Count > 0 ? $"; missed {missed.Count}" : "") + (extra.Count > 0 ? $"; {extra.Count} false" : ""));

            var ovals = Task.Run(() => FaceFinder.FindAsync(page)).GetAwaiter().GetResult();
            int covered = 0;
            var short_ = new List<string>();
            for (int i = 0; i < cards.Length; i++)
            {
                var (box, turn) = truth[i];
                var oval = ovals.FirstOrDefault(o => o.Contains(Point.Round(Center(box))));
                float back = turn > 0 ? box.Left - oval.Left : oval.Right - box.Right, below = oval.Bottom - box.Bottom;
                // A frontal oval leaves 0.10 of the face's width behind it and 0.08 of its height below.
                if (!oval.IsEmpty && RectangleF.Inflate(oval, 1, 1).Contains(box) && back >= box.Width * 0.25f && below >= box.Height * 0.12f) covered++;
                else short_.Add($"card {i + 1}: back {back / box.Width:0.00}, below {below / box.Height:0.00}, above {(box.Top - oval.Top) / box.Height:0.00}, " +
                    $"front {(turn > 0 ? oval.Right - box.Right : box.Left - oval.Left) / box.Width:0.00} of the face");
            }
            check(covered == cards.Length, $"each profile's oval reaches the back of the head and below the chin ({covered}/{cards.Length})" +
                (short_.Count > 0 ? $"; {string.Join("; ", short_)}" : ""));

            using var shown = (Bitmap)page.Clone();
            using (var g = Graphics.FromImage(shown))
                foreach (var o in ovals) LiteShot.PersonalInfo.BlurFor(new Redaction(o, true)).Draw(g, page);
            shown.Save(Path.Combine(outDir, "34-profiles.png"), ImageFormat.Png);
        }
        finally
        {
            foreach (var p in photos) p.Dispose();
        }
    }

    /// <summary>
    /// At least <paramref name="need"/> faces in a crowded or very large screenshot are found, with nothing else mistaken
    /// for one, and "Blur faces" covers each found face's whole box. Saves the scene with true faces (green),
    /// detections (yellow) and misses (red).
    /// </summary>
    static void Crowd(string outDir, Action<bool, string> check, Func<Bitmap, List<DetectedFace>> detect, Bitmap scene,
        List<RectangleF> truth, int need, string what, string file)
    {
        var clock = Stopwatch.StartNew();
        var found = detect(scene);
        clock.Stop();
        var (hits, missed, extra) = Score(truth, found.Select(f => f.Box));
        check(hits >= need && extra.Count == 0, $"{what}: {hits}/{truth.Count} found in {clock.ElapsedMilliseconds} ms" +
            (missed.Count > 0 ? $"; missed faces of {Sizes(missed)} px" : "") + (extra.Count > 0 ? $"; {extra.Count} false ({Sizes(extra)} px)" : ""));
        var ovals = Task.Run(() => FaceFinder.FindAsync(scene)).GetAwaiter().GetResult();
        check(truth.Except(missed).All(t => ovals.Any(o => RectangleF.Inflate(o, 1, 1).Contains(t))), $"{what}: each oval covers its whole face");

        using var marked = (Bitmap)scene.Clone();
        using (var g = Graphics.FromImage(marked))
        {
            using var truePen = new Pen(Color.FromArgb(40, 200, 90), 1.5f);
            using var foundPen = new Pen(Color.FromArgb(255, 200, 0), 1.5f);
            using var missPen = new Pen(Color.FromArgb(255, 40, 60), 3f);
            foreach (var t in truth) g.DrawRectangle(truePen, Rectangle.Round(t));
            foreach (var f in found) g.DrawRectangle(foundPen, Rectangle.Round(f.Box));
            foreach (var m in missed) g.DrawRectangle(missPen, Rectangle.Round(RectangleF.Inflate(m, 6, 6)));
        }
        marked.Save(Path.Combine(outDir, file + ".png"), ImageFormat.Png);
    }

    /// <summary>
    /// Draws a portrait into <paramref name="tile"/>, cropped around its face so the face fills <paramref name="share"/>
    /// of the tile's height, optionally tilted by <paramref name="tilt"/> degrees, in grey, or dimmed to
    /// <paramref name="light"/>. Returns where the face's box lands (its centre is the tile's).
    /// </summary>
    static RectangleF PlaceFace(Graphics g, Bitmap photo, RectangleF face, RectangleF tile, float share, bool round,
        float tilt = 0, bool grey = false, float light = 1, float contrast = 1)
    {
        float h = face.Height / share, w = h * tile.Width / tile.Height;
        float k = tile.Height / h;
        // Draw a larger area than the tile, so a tilted picture still fills its corners.
        float grow = tilt == 0 ? 1 : 1.5f;
        var crop = new RectangleF(face.X + face.Width / 2 - w * grow / 2, face.Y + face.Height / 2 - h * grow / 2, w * grow, h * grow);
        var target = RectangleF.Inflate(tile, tile.Width * (grow - 1) / 2, tile.Height * (grow - 1) / 2);
        var state = g.Save();
        using (var clip = new GraphicsPath())
        {
            if (round) clip.AddEllipse(tile);
            else clip.AddRectangle(tile);
            g.SetClip(clip);
        }
        var centre = Center(tile);
        g.TranslateTransform(centre.X, centre.Y);
        g.RotateTransform(tilt);
        g.TranslateTransform(-centre.X, -centre.Y);
        using (var attr = new ImageAttributes())
        {
            attr.SetWrapMode(WrapMode.TileFlipXY);
            // Lower contrast pulls every channel toward mid-grey; light then scales the result.
            float s = light * contrast, o = (1 - contrast) * 0.5f * light;
            float r = grey ? 0.299f * s : s, gr = grey ? 0.587f * s : 0, b = grey ? 0.114f * s : 0;
            attr.SetColorMatrix(new ColorMatrix(new[]
            {
                new[] { r, grey ? r : 0, grey ? r : 0, 0, 0 },
                new[] { gr, grey ? gr : s, grey ? gr : 0, 0, 0 },
                new[] { b, grey ? b : 0, grey ? b : s, 0, 0 },
                new[] { 0f, 0, 0, 1, 0 },
                new[] { o, o, o, 0, 1 },
            }));
            g.DrawImage(photo, Rectangle.Round(target), crop.X, crop.Y, crop.Width, crop.Height, GraphicsUnit.Pixel, attr);
        }
        g.Restore(state);
        return new RectangleF(centre.X - face.Width * k / 2, centre.Y - face.Height * k / 2, face.Width * k, face.Height * k);
    }

    /// <summary>
    /// A photo gallery, 1920 × 1080: 30 thumbnails in a grid with captions, faces from 12 to 28 px, some tilted, grey or
    /// dim, as on a busy page captured from a large screen.
    /// </summary>
    static Bitmap Gallery(List<(string Name, Bitmap Photo)> people, Dictionary<string, RectangleF> faceIn, out List<RectangleF> faces)
    {
        var bmp = new Bitmap(1920, 1080, PixelFormat.Format32bppArgb);
        faces = new();
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var font = new Font("Segoe UI", 12, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Color.FromArgb(60, 64, 72));
        string[] captions = { "Launch day", "Crew photo", "Training", "Mission 41-G", "Press event", "Lab tour" };
        for (int i = 0; i < 30; i++)
        {
            int person = (i * 5 + 1) % people.Count;
            float face = 12 + (i * 7) % 17;
            float tilt = (i % 4) switch { 1 => 15, 3 => -20, _ => 0 };
            var cell = new PointF(60 + i % 6 * 300, 70 + i / 6 * 200);
            var tile = new RectangleF(cell.X, cell.Y, face * 2.4f, face * 2);
            faces.Add(PlaceFace(g, people[person].Photo, faceIn[people[person].Name], tile, 0.5f, round: i % 5 == 4, tilt,
                grey: i % 5 == 2, light: i % 7 == 3 ? 0.6f : 1));
            g.DrawString($"{captions[i % captions.Length]} · {2010 + i}", font, ink, cell.X, tile.Bottom + 6);
        }
        return bmp;
    }

    /// <summary>
    /// A team page, 1920 × 1080: 25 faces from 16 to 120 px tall, in square tiles and round avatars, the larger ones
    /// with a name and role under them. <paramref name="faces"/> is where each face's box lands.
    /// </summary>
    static Bitmap TeamPage(List<(string Name, Bitmap Photo)> people, Dictionary<string, RectangleF> faceIn, out List<RectangleF> faces)
    {
        int[] heights = { 120, 18, 64, 24, 96, 16, 40, 30, 112, 20, 54, 36, 80, 17, 28, 72, 22, 104, 44, 19, 60, 26, 88, 32, 48 };
        string[] names = { "Mae Jemison", "Victor Glover", "Sally Ride", "Kalpana Chawla", "Neil Armstrong", "Ellen Ochoa" };
        var bmp = new Bitmap(1920, 1080, PixelFormat.Format32bppArgb);
        faces = new();
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(248, 249, 251));
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var title = new Font("Segoe UI Semibold", 34, GraphicsUnit.Pixel);
        using var font = new Font("Segoe UI", 14, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Color.FromArgb(28, 30, 36));
        using var dim = new SolidBrush(Color.FromArgb(110, 115, 125));
        g.DrawString("Meet the team", title, ink, 40, 24);
        g.DrawString("The people behind the mission, from flight engineers to mission specialists.", font, dim, 42, 74);

        float x = 40, y = 120, row = 0;
        for (int i = 0; i < heights.Length; i++)
        {
            int person = i % people.Count;
            float tile = heights[i] * 2;
            bool labelled = tile >= 60;
            float advance = labelled ? Math.Max(tile, g.MeasureString(names[person], font).Width) : tile;
            if (x + advance > 1880) { x = 40; y += row + 24; row = 0; }
            faces.Add(PlaceFace(g, people[person].Photo, faceIn[people[person].Name], new RectangleF(x, y, tile, tile), 0.5f, round: i % 3 == 0,
                tilt: i % 4 == 1 ? 12 : 0, grey: i % 6 == 5));
            if (labelled)
            {
                g.DrawString(names[person], font, ink, x, y + tile + 4);
                g.DrawString("Mission specialist", font, dim, x, y + tile + 22);
            }
            row = Math.Max(row, tile + (labelled ? 42 : 0));
            x += advance + 20;
        }
        return bmp;
    }

    /// <summary>A 5120 × 1440 two-monitor desktop with 12 small faces (18 to 40 px) spread across it.</summary>
    static Bitmap WideDesktop(List<(string Name, Bitmap Photo)> people, Dictionary<string, RectangleF> faceIn, out List<RectangleF> faces)
    {
        int[] heights = { 18, 22, 26, 30, 34, 40, 20, 24, 28, 32, 36, 19 };
        var bmp = new Bitmap(5120, 1440, PixelFormat.Format32bppArgb);
        faces = new();
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(52, 58, 70));
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var window = new SolidBrush(Color.FromArgb(245, 246, 248)))
        {
            g.FillRectangle(window, 300, 200, 1900, 1000);
            g.FillRectangle(window, 2860, 160, 1960, 1100);
        }
        for (int i = 0; i < heights.Length; i++)
        {
            int person = i % people.Count;
            float tile = heights[i] * 2;
            var at = new PointF(380 + i % 6 * 300 + (i >= 6 ? 2560 : 0), 320 + i % 3 * 300);
            faces.Add(PlaceFace(g, people[person].Photo, faceIn[people[person].Name], new RectangleF(at.X, at.Y, tile, tile), 0.5f, round: i % 2 == 0));
        }
        return bmp;
    }

    /// <summary>Lines of 10 to 14 px text, light and dark, with bullets and symbols: what the 2× small-face pass sees most.</summary>
    static Bitmap SmallPrint()
    {
        var bmp = new Bitmap(1600, 900, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        using (var night = new SolidBrush(Color.FromArgb(28, 29, 34))) g.FillRectangle(night, 800, 0, 800, 900);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        string[] words = { "@mae", "re:", "Q3 (draft)", "o_O", ":-)", "100%", "•", "Ω", "#launch", "→", "[x]", "@@", "ok", "8:30", "∞", "oo", "(o)", "OO" };
        var rnd = new Random(11);
        for (int col = 0; col < 2; col++)
            for (int y = 10; y < 880; y += 18)
            {
                using var font = new Font(y % 54 == 10 ? "Segoe UI Semibold" : "Segoe UI", 10 + (y / 18) % 5, GraphicsUnit.Pixel);
                var line = string.Join(" ", Enumerable.Range(0, 14).Select(_ => words[rnd.Next(words.Length)]));
                using var ink = new SolidBrush(col == 0 ? Color.FromArgb(40, 40, 46) : Color.FromArgb(220, 222, 228));
                g.DrawString(line, font, ink, 12 + col * 800, y);
            }
        return bmp;
    }

    /// <summary>Matches detections to the true faces one to one: found, the faces missed, and detections that match nothing.</summary>
    static (int Found, List<RectangleF> Missed, List<RectangleF> Extra) Score(List<RectangleF> truth, IEnumerable<RectangleF> found)
    {
        var left = found.ToList();
        var missed = new List<RectangleF>();
        foreach (var t in truth.OrderBy(t => t.Height))
        {
            var zone = RectangleF.Inflate(t, t.Width * 0.3f, t.Height * 0.3f);
            int i = left.FindIndex(f => zone.Contains(Center(f)) && f.Height > t.Height * 0.4f && f.Height < t.Height * 2.5f);
            if (i < 0) missed.Add(t);
            else left.RemoveAt(i);
        }
        return (truth.Count - missed.Count, missed, left);
    }

    static string Sizes(IEnumerable<RectangleF> faces) => string.Join(", ", faces.Select(f => $"{f.Height:0}"));

    static PointF Center(RectangleF r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    // The middle of an oval, well inside the face.
    static Rectangle Inner(Rectangle oval) => Rectangle.Inflate(oval, -oval.Width / 4, -oval.Height / 4);

    static float[] Luma(Bitmap b) => BitmapPixels.Read(b, (bgra, stride) =>
    {
        var luma = new float[b.Width * b.Height];
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
            {
                int o = y * stride + x * 4;
                luma[y * b.Width + x] = 0.114f * bgra[o] + 0.587f * bgra[o + 1] + 0.299f * bgra[o + 2];
            }
        return luma;
    });

    // How much detail (eyes, brows, mouth) an area holds: the mean absolute Laplacian of its 4 × 4 block means, so
    // Shield blur's per-pixel grain doesn't count as detail.
    static double Detail(float[] luma, int width, Rectangle r)
    {
        int cols = r.Width / 4, rows = r.Height / 4;
        var means = new float[cols * rows];
        for (int j = 0; j < rows; j++)
            for (int i = 0; i < cols; i++)
            {
                float sum = 0;
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++) sum += luma[(r.Top + j * 4 + y) * width + r.Left + i * 4 + x];
                means[j * cols + i] = sum / 16;
            }
        double total = 0;
        int n = 0;
        for (int j = 1; j < rows - 1; j++)
            for (int i = 1; i < cols - 1; i++)
            {
                int k = j * cols + i;
                total += Math.Abs(4 * means[k] - means[k - 1] - means[k + 1] - means[k - cols] - means[k + cols]);
                n++;
            }
        return total / Math.Max(1, n);
    }

    // ───────────────────────────── updates ─────────────────────────────

    static void Updates(string server, Action<bool, string> check)
    {
        var settings = AppSettings.Current;
        string? savedServer = settings.UpdateServer;
        string savedChannel = settings.UpdateChannel;
        bool savedCount = settings.CountUsage;
        settings.UpdateServer = server;
        settings.CountUsage = false;   // test checks aren't copies in use
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
            settings.CountUsage = savedCount;
            try { if (Directory.Exists(updates)) Directory.Delete(updates, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Copies in use: the first update check of the day carries the install id, later ones don't, and none do with the
    /// setting off. Uses a stand-in server on localhost.
    /// </summary>
    internal static void UsageCount(Action<bool, string> check)
    {
        var settings = AppSettings.Current;
        var saved = (settings.UpdateServer, settings.CountUsage, settings.InstallId, settings.LastCountedDay);
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var seen = new List<string?>();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var ctx = await listener.GetContextAsync();
                    lock (seen) seen.Add(ctx.Request.Headers["X-LiteShot-Install"]);
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                }
                catch { return; }
            }
        });
        try
        {
            settings.UpdateServer = $"http://localhost:{port}";
            settings.CountUsage = true;
            settings.InstallId = null;
            settings.LastCountedDay = null;
            Run(() => Updater.LatestAsync());
            Run(() => Updater.LatestAsync());
            string? id = settings.InstallId;
            settings.LastCountedDay = null;
            settings.CountUsage = false;
            Run(() => Updater.LatestAsync());
            lock (seen)
            {
                check(seen.Count == 3 && seen[0] is { Length: 32 } first && first == id && first.All(Uri.IsHexDigit),
                    "usage count: the day's first update check carries the random install id");
                check(seen.Count == 3 && seen[1] == null, "usage count: later checks that day don't");
                check(seen.Count == 3 && seen[2] == null, "usage count: nothing is sent with the setting off");
            }
        }
        finally
        {
            (settings.UpdateServer, settings.CountUsage, settings.InstallId, settings.LastCountedDay) = saved;
            listener.Stop();
        }
    }

    /// <summary>LiteShot notices a newer version at startup, whether it installed itself or was installed by hand.</summary>
    static void VersionChanges(Action<bool, string> check)
    {
        string current = Updater.CurrentVersion.ToString();
        check(Updater.WasUpdated("0.9.0", justUpdated: false), "an older last-run version means LiteShot was updated");
        check(!Updater.WasUpdated(current, justUpdated: false), "the same version again isn't an update");
        check(!Updater.WasUpdated("99.0.0", justUpdated: false), "going back to an older copy isn't an update");
        check(!Updater.WasUpdated(null, justUpdated: false) && Updater.WasUpdated(null, justUpdated: true),
            "with no version on record, only --updated counts");
    }

    /// <summary>
    /// For people who update by hand: the newest release is fetched even when it isn't newer, each check is remembered,
    /// and Settings shows the answer. Needs channels harness-older (1.0.0) and harness-current (this version) on the
    /// local server: `dotnet run --project tools/LiteShot.Publish -- publish --server http://127.0.0.1:8788 --token
    /// &lt;.dev.vars token&gt; --channel harness-current --version x.y.z --dist &lt;folder with any LiteShot.exe&gt; --notes "…"`.
    /// </summary>
    static void ManualUpdates(string server, string outDir, Action<bool, string> check)
    {
        var settings = AppSettings.Current;
        // CheckNowAsync saves the settings, so the real settings file is put back afterwards.
        string file = Path.Combine(AppSettings.Directory, "settings.json");
        byte[]? savedFile = File.Exists(file) ? File.ReadAllBytes(file) : null;
        var saved = (settings.UpdateServer, settings.UpdateChannel, settings.LastUpdateCheck, settings.LatestVersion,
            settings.NotesVersion, settings.Notes, settings.UpdatedAt);
        var available = typeof(Updater).GetProperty(nameof(Updater.Available))!;
        settings.UpdateServer = server;
        try
        {
            settings.UpdateChannel = "harness-older";
            var older = Run(() => Updater.LatestAsync());
            check(older != null && older.Version < Updater.CurrentVersion, $"the latest release is fetched even when it's older ({older?.Version})");
            settings.LastUpdateCheck = null;
            var none = Run(() => Updater.CheckNowAsync());
            check(none == null && Updater.Available == null && Updater.Newer == null && settings.LastUpdateCheck != null
                && settings.LatestVersion == older?.Version.ToString(), "an older release means up to date, and the check is remembered");

            settings.UpdateChannel = "harness-current";
            var same = Run(() => Updater.LatestAsync());
            if (same?.Version == Updater.CurrentVersion)
            {
                settings.NotesVersion = settings.Notes = null;
                check(Run(() => Updater.CheckNowAsync()) == null && settings.NotesVersion == same.Version.ToString() && settings.Notes == same.Notes.Trim(),
                    $"this version's own notes are kept for Settings (\"{settings.Notes}\")");
            }
            else Console.WriteLine($"  (skipped notes check: channel harness-current holds {same?.Version.ToString() ?? "nothing"}, not {Updater.CurrentVersion})");

            settings.UpdateChannel = "stable";
            var found = Run(() => Updater.CheckNowAsync());
            check(found?.Version == new Version(9, 9, 9) && Updater.Available == found && Updater.Newer == found.Version && settings.LatestVersion == "9.9.9",
                "a newer release is offered and remembered");
            available.SetValue(null, null);   // as after a restart
            check(Updater.Newer == new Version(9, 9, 9), "a newer version an earlier run saw is still offered after a restart");
            settings.LatestVersion = Updater.CurrentVersion.ToString();
            check(Updater.Newer == null, "once this copy is that version, nothing is offered");

            // How Settings › General looks with an update on offer, and after one has been installed.
            void Shot(string name)
            {
                using var form = new SettingsForm(null!) { StartPosition = FormStartPosition.Manual, Location = new Point(-10000, -10000) };
                form.Show();
                form.ShowPageAt(SettingsForm.GeneralPage);
                Application.DoEvents();
                using var bmp = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
                bmp.Save(Path.Combine(outDir, name), ImageFormat.Png);
                form.Close();
            }
            available.SetValue(null, found);
            settings.LatestVersion = "9.9.9";
            Shot("08-settings-update-available.png");
            available.SetValue(null, null);
            settings.LatestVersion = Updater.CurrentVersion.ToString();
            settings.LastUpdateCheck = DateTime.Now.AddHours(-3);
            settings.UpdatedAt = DateTime.Now.AddDays(-1);
            settings.NotesVersion = Updater.CurrentVersion.ToString();
            settings.Notes = "Blur faces with Shield blur by FacePrivacy.ai, a highlighter cursor for the marker, and update checks in the tray menu.";
            Shot("08-settings-updated.png");
        }
        finally
        {
            available.SetValue(null, null);
            (settings.UpdateServer, settings.UpdateChannel, settings.LastUpdateCheck, settings.LatestVersion,
                settings.NotesVersion, settings.Notes, settings.UpdatedAt) = saved;
            try
            {
                if (savedFile != null) File.WriteAllBytes(file, savedFile);
                else File.Delete(file);
            }
            catch { /* best effort */ }
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
