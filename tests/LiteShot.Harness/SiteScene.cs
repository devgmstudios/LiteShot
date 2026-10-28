using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace LiteShot.Harness;

/// <summary>
/// A staged desktop for the website's "see it work" shots: a chat app, a dashboard in a browser and a taskbar.
/// Everything is fictional; LiteShot's own UI is drawn over it by the real overlay.
/// </summary>
internal static class SiteScene
{
    public sealed record Layout(Rectangle Chat, Rectangle ChatMessages, Rectangle Dashboard, Rectangle Kpi, Rectangle Accounts, Rectangle Chart);

    static readonly (string Name, string Initials, Color Color)[] People =
    {
        ("Sarah Johnson", "SJ", Color.FromArgb(255, 149, 0)),
        ("Priya Patel", "PP", Color.FromArgb(175, 82, 222)),
        ("Marcus Lee", "ML", Color.FromArgb(52, 199, 89)),
        ("Wang Wei", "WW", Color.FromArgb(0, 122, 255)),
        ("Emma Davis", "ED", Color.FromArgb(255, 45, 85)),
    };

    public static Bitmap Draw(Size size, float s, out Layout layout)
    {
        var bmp = new Bitmap(size.Width, size.Height);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        float W = size.Width / s, H = size.Height / s;
        Rectangle Px(RectangleF r) => Rectangle.Round(new RectangleF(r.X * s, r.Y * s, r.Width * s, r.Height * s));

        Wallpaper(g, size);
        g.ScaleTransform(s, s);

        var chat = new RectangleF(56, 64, 800, Math.Min(620, H - 150));
        var messages = ChatWindow(g, chat);
        var dash = new RectangleF(Math.Min(W - 800, 900), 104, 760, Math.Min(660, H - 170));
        var (kpi, accounts, chart) = Dashboard(g, dash);
        Taskbar(g, W, H);

        layout = new Layout(Px(chat), Px(messages), Px(dash), Px(kpi), Px(accounts), Px(chart));
        return bmp;
    }

    static void Wallpaper(Graphics g, Size size)
    {
        var all = new Rectangle(Point.Empty, size);
        using (var bg = new LinearGradientBrush(all, Color.FromArgb(18, 32, 92), Color.FromArgb(236, 120, 80), 60f))
        {
            bg.InterpolationColors = new ColorBlend
            {
                Colors = new[] { Color.FromArgb(14, 26, 78), Color.FromArgb(88, 52, 150), Color.FromArgb(214, 96, 120), Color.FromArgb(246, 158, 92) },
                Positions = new[] { 0f, 0.42f, 0.74f, 1f },
            };
            g.FillRectangle(bg, all);
        }
        foreach (var (x, y, r, c) in new[] { (0.18f, 0.8f, 0.5f, Color.FromArgb(120, 255, 170, 90)), (0.85f, 0.15f, 0.45f, Color.FromArgb(110, 120, 140, 255)), (0.55f, 0.55f, 0.35f, Color.FromArgb(70, 255, 120, 170)) })
        {
            float rad = r * size.Width;
            using var path = new GraphicsPath();
            path.AddEllipse(x * size.Width - rad, y * size.Height - rad, rad * 2, rad * 2);
            using var glow = new PathGradientBrush(path) { CenterColor = c, SurroundColors = new[] { Color.FromArgb(0, c) } };
            g.FillPath(glow, path);
        }
    }

    static void Window(Graphics g, RectangleF r, string title, Color bar)
    {
        for (int i = 6; i >= 1; i--)
        {
            using var shade = new SolidBrush(Color.FromArgb(10, 0, 0, 0));
            using var sp = Round(RectangleF.Inflate(new RectangleF(r.X, r.Y + 8, r.Width, r.Height), i * 3, i * 3), 12 + i * 3);
            g.FillPath(shade, sp);
        }
        using var path = Round(r, 9);
        using (var body = new SolidBrush(Color.White)) g.FillPath(body, path);
        var state = g.Save();
        g.SetClip(path);
        using (var top = new SolidBrush(bar)) g.FillRectangle(top, r.X, r.Y, r.Width, 38);
        using var f = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Color.FromArgb(60, 60, 67));
        g.DrawString(title, f, ink, r.X + 16, r.Y + 11);
        using var glyph = new Pen(Color.FromArgb(90, 90, 96), 1.1f);
        float x = r.Right - 128;
        g.DrawLine(glyph, x + 18, r.Y + 19, x + 28, r.Y + 19);
        g.DrawRectangle(glyph, x + 60, r.Y + 14, 10, 10);
        g.DrawLine(glyph, x + 104, r.Y + 14, x + 114, r.Y + 24);
        g.DrawLine(glyph, x + 114, r.Y + 14, x + 104, r.Y + 24);
        g.Restore(state);
        using var edge = new Pen(Color.FromArgb(40, 0, 0, 0), 1f);
        g.DrawPath(edge, path);
    }

    static RectangleF ChatWindow(Graphics g, RectangleF r)
    {
        Window(g, r, "Messages — Design team", Color.FromArgb(243, 243, 246));
        using var name = new Font("Segoe UI Semibold", 14f, GraphicsUnit.Pixel);
        using var text = new Font("Segoe UI", 14f, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Color.FromArgb(29, 29, 31));
        using var gray = new SolidBrush(Color.FromArgb(110, 110, 118));

        // Conversation list
        var side = new RectangleF(r.X, r.Y + 38, 236, r.Height - 38);
        using (var sb = new SolidBrush(Color.FromArgb(248, 248, 250))) g.FillRectangle(sb, side);
        using (var sel = Round(new RectangleF(side.X + 8, side.Y + 10, side.Width - 16, 56), 10))
        using (var selFill = new SolidBrush(Color.FromArgb(232, 236, 250))) g.FillPath(selFill, sel);
        string[] previews = { "Can you send the Q3…", "On it. Draft is up.", "Looks good to me!", "Reviewing this afternoon", "Thanks!" };
        for (int i = 0; i < People.Length; i++)
        {
            float y = side.Y + 18 + i * 62;
            Avatar(g, People[i], side.X + 20, y, 40);
            g.DrawString(People[i].Name, name, ink, side.X + 70, y);
            g.DrawString(previews[i], small, gray, side.X + 70, y + 20);
        }

        // Thread
        var main = new RectangleF(side.Right, side.Y, r.Right - side.Right, side.Height);
        using (var line = new Pen(Color.FromArgb(232, 232, 236))) g.DrawLine(line, main.X, main.Y, main.X, main.Bottom);
        using (var head = new Font("Segoe UI Semibold", 17f, GraphicsUnit.Pixel)) g.DrawString("Design team", head, ink, main.X + 24, main.Y + 16);
        g.DrawString("5 members", small, gray, main.X + 26, main.Y + 40);

        var thread = new (int Who, string Time, string Text)[]
        {
            (0, "10:42 AM", "The new glass toolbar looks great. Can you send the\nQ3 numbers to Dr. Patel before Friday?"),
            (1, "10:44 AM", "On it. Marcus Lee already shared the draft."),
            (3, "10:51 AM", "I'll review it with Emma Davis this afternoon."),
            (2, "11:02 AM", "Looks good to me! Ship it."),
        };
        float ty = main.Y + 84;
        foreach (var (who, time, body) in thread)
        {
            Avatar(g, People[who], main.X + 24, ty, 38);
            g.DrawString(People[who].Name, name, ink, main.X + 74, ty);
            float nw = g.MeasureString(People[who].Name, name).Width;
            g.DrawString(time, small, gray, main.X + 74 + nw + 4, ty + 2);
            g.DrawString(body, text, ink, main.X + 74, ty + 22);
            ty += body.Contains('\n') ? 92 : 70;
        }
        var input = new RectangleF(main.X + 20, main.Bottom - 62, main.Width - 40, 42);
        using (var box = Round(input, 21))
        using (var bf = new SolidBrush(Color.FromArgb(244, 244, 247)))
        {
            g.FillPath(bf, box);
            g.DrawString("Message Design team", text, gray, input.X + 18, input.Y + 11);
        }
        return new RectangleF(main.X + 12, main.Y + 72, main.Width - 24, ty - main.Y - 72);
    }

    static (RectangleF Kpi, RectangleF Accounts, RectangleF Chart) Dashboard(Graphics g, RectangleF r)
    {
        Window(g, r, "Q3 Dashboard", Color.FromArgb(236, 238, 243));
        using var small = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);
        using var text = new Font("Segoe UI", 14f, GraphicsUnit.Pixel);
        using var semi = new Font("Segoe UI Semibold", 14f, GraphicsUnit.Pixel);
        using var big = new Font("Segoe UI Semibold", 26f, GraphicsUnit.Pixel);
        using var head = new Font("Segoe UI Semibold", 24f, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Color.FromArgb(29, 29, 31));
        using var gray = new SolidBrush(Color.FromArgb(110, 110, 118));
        using var green = new SolidBrush(Color.FromArgb(36, 160, 80));

        // Address bar
        var url = new RectangleF(r.X + 16, r.Y + 46, r.Width - 32, 30);
        using (var pill = Round(url, 15))
        using (var pf = new SolidBrush(Color.FromArgb(241, 242, 246))) g.FillPath(pf, pill);
        g.DrawString("app.example.com/reports/q3", small, gray, url.X + 16, url.Y + 7);

        float x = r.X + 28, y = r.Y + 96;
        g.DrawString("Q3 Performance", head, ink, x, y);
        g.DrawString("July – September · All regions", small, gray, x + 2, y + 34);

        // KPI cards
        var kpis = new[] { ("Revenue", "$4.2M", "▲ 24%"), ("Customers", "18,420", "▲ 9%"), ("NPS", "72", "▲ 6") };
        float cw = (r.Width - 56 - 24) / 3f, cy = y + 68;
        for (int i = 0; i < kpis.Length; i++)
        {
            var card = new RectangleF(x + i * (cw + 12), cy, cw, 92);
            using (var cp = Round(card, 12))
            using (var cf = new SolidBrush(Color.FromArgb(247, 248, 251))) g.FillPath(cf, cp);
            g.DrawString(kpis[i].Item1, small, gray, card.X + 16, card.Y + 14);
            g.DrawString(kpis[i].Item2, big, ink, card.X + 14, card.Y + 32);
            g.DrawString(kpis[i].Item3, semi, green, card.X + 16, card.Y + 66);
        }
        var kpiArea = new RectangleF(x, cy, r.Width - 56, 92);

        // Chart
        var chart = new RectangleF(x, cy + 116, r.Width - 56, 170);
        int[] values = { 52, 61, 58, 70, 66, 78, 84, 96 };
        float bw = (chart.Width - 20) / values.Length;
        using (var axis = new Pen(Color.FromArgb(230, 230, 236))) g.DrawLine(axis, chart.X, chart.Bottom, chart.Right, chart.Bottom);
        for (int i = 0; i < values.Length; i++)
        {
            float h = values[i] / 100f * (chart.Height - 20);
            var bar = new RectangleF(chart.X + 10 + i * bw + bw * 0.18f, chart.Bottom - h, bw * 0.64f, h);
            using var bp = Round(bar, 5);
            using var bf = new LinearGradientBrush(bar, Color.FromArgb(90, 170, 255), Color.FromArgb(10, 114, 232), LinearGradientMode.Vertical);
            g.FillPath(bf, bp);
        }

        // Accounts table
        float ty = chart.Bottom + 26;
        g.DrawString("Top accounts", semi, ink, x, ty);
        ty += 30;
        var rows = new[] { ("4417-1234-5678", "Sarah Johnson", "Active"), ("9921-7765-1102", "Marcus Lee", "Renewing"), ("3108-4420-9915", "Priya Patel", "Active") };
        float c1 = x, c2 = x + 200, c3 = x + 420;
        g.DrawString("Account", small, gray, c1, ty); g.DrawString("Owner", small, gray, c2, ty); g.DrawString("Status", small, gray, c3, ty);
        var accounts = RectangleF.Empty;
        foreach (var (acct, owner, status) in rows)
        {
            ty += 30;
            g.DrawString(acct, text, ink, c1, ty);
            g.DrawString(owner, text, ink, c2, ty);
            g.DrawString(status, text, gray, c3, ty);
            var cell = new RectangleF(c1 - 4, ty - 4, 150, 26);
            accounts = accounts.IsEmpty ? cell : RectangleF.Union(accounts, cell);
        }
        return (kpiArea, accounts, chart);
    }

    static void Taskbar(Graphics g, float W, float H)
    {
        var bar = new RectangleF(0, H - 48, W, 48);
        using (var fill = new SolidBrush(Color.FromArgb(225, 28, 26, 36))) g.FillRectangle(fill, bar);
        using (var hi = new Pen(Color.FromArgb(40, 255, 255, 255))) g.DrawLine(hi, 0, bar.Y, W, bar.Y);
        var colors = new[] { (Color.FromArgb(80, 160, 255), Color.FromArgb(0, 100, 220)), (Color.FromArgb(255, 200, 80), Color.FromArgb(240, 140, 20)), (Color.FromArgb(120, 220, 140), Color.FromArgb(30, 160, 80)), (Color.FromArgb(200, 140, 255), Color.FromArgb(120, 60, 220)), (Color.FromArgb(255, 170, 80), Color.FromArgb(240, 92, 18)) };
        float x = W / 2 - colors.Length * 22;
        foreach (var (a, b) in colors)
        {
            var r = new RectangleF(x, bar.Y + 9, 30, 30);
            using var p = Round(r, 8);
            using var br = new LinearGradientBrush(r, a, b, LinearGradientMode.Vertical);
            g.FillPath(br, p);
            x += 44;
        }
        using var f = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);
        using var white = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
        var fmt = new StringFormat { Alignment = StringAlignment.Far };
        g.DrawString("3:42 PM\n10/28/2026", f, white, new RectangleF(W - 140, bar.Y + 6, 124, 40), fmt);
    }

    static void Avatar(Graphics g, (string Name, string Initials, Color Color) p, float x, float y, float d)
    {
        var r = new RectangleF(x, y, d, d);
        using (var b = new LinearGradientBrush(r, ControlPaint.Light(p.Color, 0.3f), p.Color, LinearGradientMode.Vertical)) g.FillEllipse(b, r);
        using var f = new Font("Segoe UI Semibold", d * 0.36f, GraphicsUnit.Pixel);
        using var white = new SolidBrush(Color.White);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(p.Initials, f, white, r, fmt);
    }

    static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
