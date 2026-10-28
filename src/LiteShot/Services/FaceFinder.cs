using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;

namespace LiteShot;

/// <summary>Finds faces with the face detector built into Windows — offline, nothing is uploaded.</summary>
internal static class FaceFinder
{
    public static bool Supported
    {
        get
        {
            try { return FaceDetector.IsSupported; }
            catch { return false; }
        }
    }

    /// <summary>Ovals around each face in <paramref name="image"/>, grown to cover hair and chin.</summary>
    public static async Task<List<Rectangle>> FindAsync(Bitmap image)
    {
        if (!Supported) throw new NotSupportedException("Face detection isn't available on this PC.");
        var detector = await FaceDetector.CreateAsync();
        var format = FaceDetector.IsBitmapPixelFormatSupported(BitmapPixelFormat.Gray8) ? BitmapPixelFormat.Gray8 : BitmapPixelFormat.Nv12;

        // Screens are full of small faces (avatars, video tiles); look at an enlarged copy of small areas.
        float scale = Math.Max(image.Width, image.Height) switch { < 700 => 3f, < 1400 => 2f, _ => 1f };
        using var bitmap = await OcrService.ToSoftwareBitmapAsync(image, scale, format);
        try { detector.MinDetectableFaceSize = new BitmapSize { Width = 24, Height = 24 }; }
        catch { /* keep the detector's own minimum */ }
        var faces = await detector.DetectFacesAsync(bitmap);

        var bounds = new Rectangle(Point.Empty, image.Size);
        var found = new List<Rectangle>();
        foreach (var face in faces)
        {
            var b = face.FaceBox;
            float x = b.X / scale, y = b.Y / scale, w = b.Width / scale, h = b.Height / scale;
            // The detector's box runs from brow to chin; widen it and raise the top to take in the hair.
            var r = RectangleF.FromLTRB(x - w * 0.18f, y - h * 0.38f, x + w * 1.18f, y + h * 1.14f);
            var cover = Rectangle.Intersect(Rectangle.Round(r), bounds);
            if (cover.Width > 4 && cover.Height > 4) found.Add(cover);
        }
        return found;
    }
}
