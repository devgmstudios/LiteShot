# Generates src/LiteShot/app.ico (and a preview PNG) from vector shapes.
# Layers, Liquid Glass style: a lit orange tile with a bright rim, a frosted "glass pane" (the selection) on a soft
# shadow, and a white feather in front. Run with Windows PowerShell: powershell -File tools/MakeIcon.ps1
param(
    [string]$Out = (Join-Path $PSScriptRoot "..\src\LiteShot\app.ico"),
    [string]$Preview = ""
)

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class LiteShotIcon
{
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

    static void FillSoftShadow(Graphics g, GraphicsPath shape, float dx, float dy, int steps, Color color)
    {
        // A cheap blur: the same shape, grown a little each pass, at low opacity.
        for (int i = steps; i >= 1; i--)
        {
            using (var p = (GraphicsPath)shape.Clone())
            using (var m = new Matrix())
            using (var pen = new Pen(Color.FromArgb(color.A / steps, color), i * 3f) { LineJoin = LineJoin.Round })
            using (var b = new SolidBrush(Color.FromArgb(color.A / steps, color)))
            {
                m.Translate(dx, dy);
                p.Transform(m);
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
        }
    }

    static GraphicsPath Vane(float o)
    {
        var vane = new GraphicsPath();
        vane.AddBezier(80 - o, 186 + o, 80 - o, 120, 126, 70 - o, 196 + o, 60 - o);
        vane.AddBezier(196 + o, 60 - o, 190, 128, 146, 178 + o, 80 - o, 186 + o);
        vane.CloseFigure();
        return vane;
    }

    public static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float k = size / 256f;
            g.ScaleTransform(k, k);
            bool small = size <= 24;

            // Background layer: an orange tile, lit from the top left, with a bright rim like a lens edge.
            var tile = small ? new RectangleF(8, 8, 240, 240) : new RectangleF(16, 16, 224, 224);
            using (var path = Round(tile, small ? 60 : 54))
            {
                using (var bg = new LinearGradientBrush(tile, Color.FromArgb(255, 170, 76), Color.FromArgb(238, 86, 14), LinearGradientMode.Vertical))
                    g.FillPath(bg, path);
                var state = g.Save();
                g.SetClip(path);
                using (var glowPath = new GraphicsPath())
                {
                    glowPath.AddEllipse(tile.X - 70, tile.Y - 90, tile.Width + 40, tile.Height * 0.95f);
                    using (var glow = new PathGradientBrush(glowPath))
                    {
                        glow.CenterPoint = new PointF(tile.X + 60, tile.Y + 20);
                        glow.CenterColor = Color.FromArgb(small ? 40 : 70, 255, 255, 255);
                        glow.SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) };
                        g.FillPath(glow, glowPath);
                    }
                }
                using (var shade = new LinearGradientBrush(new RectangleF(tile.X, tile.Bottom - 80, tile.Width, 80), Color.FromArgb(0, 150, 40, 0), Color.FromArgb(40, 150, 40, 0), LinearGradientMode.Vertical))
                    g.FillRectangle(shade, tile.X, tile.Bottom - 79, tile.Width, 79);
                g.Restore(state);
                if (!small)
                {
                    using (var rimBrush = new LinearGradientBrush(tile, Color.FromArgb(150, 255, 255, 255), Color.FromArgb(20, 255, 255, 255), LinearGradientMode.Vertical))
                    using (var rim = new Pen(rimBrush, 3f))
                    using (var inner = Round(RectangleF.Inflate(tile, -1.5f, -1.5f), 52.5f))
                        g.DrawPath(rim, inner);
                }
            }

            // Middle layer: a frosted glass pane (the captured area) floating on a soft shadow.
            if (!small)
            {
                var pane = new RectangleF(50, 58, 156, 124);
                using (var path = Round(pane, 20))
                {
                    // Shadow only around the pane: under translucent glass it would muddy the fill.
                    var state = g.Save();
                    g.SetClip(path, CombineMode.Exclude);
                    FillSoftShadow(g, path, 0, 7, 5, Color.FromArgb(70, 140, 40, 0));
                    g.Restore(state);
                    using (var fill = new LinearGradientBrush(pane, Color.FromArgb(110, 255, 255, 255), Color.FromArgb(45, 255, 255, 255), LinearGradientMode.Vertical))
                        g.FillPath(fill, path);
                    using (var edgeBrush = new LinearGradientBrush(RectangleF.Inflate(pane, 0, 2), Color.FromArgb(240, 255, 255, 255), Color.FromArgb(90, 255, 255, 255), LinearGradientMode.Vertical))
                    using (var edge = new Pen(edgeBrush, 4.5f))
                        g.DrawPath(edge, path);
                }
            }

            // Foreground layer: a white feather with its own shadow.
            float o = small ? 6 : 0;
            using (var vane = Vane(o))
            {
                if (!small) FillSoftShadow(g, vane, 3, 7, 4, Color.FromArgb(80, 140, 40, 0));
                using (var white = new LinearGradientBrush(new RectangleF(70, 50, 140, 150), Color.White, Color.FromArgb(255, 246, 238), LinearGradientMode.Vertical))
                    g.FillPath(white, vane);
            }
            using (var quill = new Pen(Color.FromArgb(246, 122, 38), small ? 14f : 7f))
            {
                quill.StartCap = LineCap.Round;
                quill.EndCap = LineCap.Round;
                g.DrawLine(quill, 98, 168, 172, 86);
            }
            using (var stem = new Pen(Color.White, small ? 18f : 10f))
            {
                stem.StartCap = LineCap.Round;
                stem.EndCap = LineCap.Round;
                g.DrawLine(stem, small ? 52 : 60, small ? 210 : 204, 86, 178);
            }
        }
        return bmp;
    }

    public static void Write(string path, int[] sizes)
    {
        var images = new List<byte[]>();
        foreach (int s in sizes)
        {
            using (var bmp = Render(s))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                images.Add(ms.ToArray());
            }
        }
        using (var fs = File.Create(path))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) w.Write(img);
        }
    }
}
"@

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
[LiteShotIcon]::Write([IO.Path]::GetFullPath($Out), $sizes)
Write-Host "Wrote $Out"

if ($Preview) {
    $sheet = New-Object System.Drawing.Bitmap 520, 280
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.Clear([System.Drawing.Color]::FromArgb(240, 240, 240))
    $big = [LiteShotIcon]::Render(256); $g.DrawImage($big, 8, 12, 256, 256)
    $x = 280
    foreach ($s in 16, 24, 32, 48, 64) { $i = [LiteShotIcon]::Render($s); $g.DrawImage($i, $x, 20, $s, $s); $x += $s + 12 }
    $g.FillRectangle([System.Drawing.Brushes]::Black, 270, 150, 250, 130)
    $x = 280
    foreach ($s in 16, 24, 32, 48, 64) { $i = [LiteShotIcon]::Render($s); $g.DrawImage($i, $x, 170, $s, $s); $x += $s + 12 }
    $sheet.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "Preview $Preview"
}
