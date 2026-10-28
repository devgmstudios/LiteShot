using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using LiteShot;

namespace LiteShot.Harness;

/// <summary>Renders every glyph in <see cref="Icons"/> at toolbar and menu sizes, on light and dark, for review.</summary>
internal static class IconSheet
{
    public static void Render(string outDir)
    {
        var glyphs = typeof(Icons).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(Graphics), typeof(Pen), typeof(Brush) }))
            .Select(m => (m.Name, Glyph: (Icons.Glyph)Delegate.CreateDelegate(typeof(Icons.Glyph), m)))
            .ToList();
        int[] sizes = { 64, 38, 19, 16 };
        int cell = 96, rowH = 300, cols = glyphs.Count;
        using var sheet = new Bitmap(cell * cols, rowH * 2, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(sheet);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe UI", 8f);
        foreach (var (row, bg, fg) in new[] { (0, Color.FromArgb(242, 242, 247), Color.FromArgb(28, 28, 30)), (1, Color.FromArgb(28, 28, 30), Color.FromArgb(242, 242, 247)) })
        {
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, 0, row * rowH, sheet.Width, rowH);
            for (int i = 0; i < cols; i++)
            {
                int x = i * cell, y = row * rowH + 8;
                foreach (int s in sizes)
                {
                    Icons.Draw(g, new RectangleF(x + (cell - s) / 2f, y, s, s), fg, glyphs[i].Glyph, s <= 20 ? 2f : 1.8f);
                    y += s + 10;
                }
                using var tb = new SolidBrush(Color.FromArgb(150, fg));
                g.DrawString(glyphs[i].Name, font, tb, new RectangleF(x, row * rowH + rowH - 22, cell, 20), new StringFormat { Alignment = StringAlignment.Center });
            }
        }
        string path = Path.Combine(outDir, "icons.png");
        sheet.Save(path, ImageFormat.Png);
        Console.WriteLine($"Wrote {path} ({glyphs.Count} icons)");
    }
}
