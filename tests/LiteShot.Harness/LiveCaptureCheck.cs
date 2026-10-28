using System.Drawing.Imaging;
using System.Reflection;
using LiteShot.Overlay;

namespace LiteShot.Harness;

/// <summary>Renders the overlay from a real screen capture (not shown on screen) and reports pixel values.</summary>
internal static class LiveCaptureCheck
{
    public static void Run(string outDir)
    {
        var virt = SystemInformation.VirtualScreen;
        var shot = ScreenCapture.Capture(virt, false);
        var before = shot.GetPixel(60, 60);
        var overlay = new CaptureOverlay(null!, shot, virt, new List<Rectangle>(), new Rectangle(virt.X + 500, virt.Y + 320, 1000, 640));
        var dimmed = (Bitmap)typeof(CaptureOverlay).GetField("dimmed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(overlay)!;
        Console.WriteLine($"shot(60,60)={before}  dimmed(60,60)={dimmed.GetPixel(60, 60)}  shot fmt={shot.PixelFormat} dimmed fmt={dimmed.PixelFormat}");

        using var frame = new Bitmap(virt.Width, virt.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(frame))
        {
            var paint = typeof(CaptureOverlay).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!;
            paint.Invoke(overlay, new object[] { new PaintEventArgs(g, new Rectangle(Point.Empty, virt.Size)) });
        }
        Console.WriteLine($"rendered(60,60)={frame.GetPixel(60, 60)}");
        using var small = new Bitmap(frame, virt.Width / 2, virt.Height / 2);
        small.Save(Path.Combine(outDir, "10-live-capture-offscreen.png"), ImageFormat.Png);
        overlay.Dispose();
    }
}
