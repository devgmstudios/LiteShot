using LiteShot.Core;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;

namespace LiteShot;

/// <summary>
/// Finds faces on this PC, nothing uploaded: with YuNet (LiteShot.Core, on ONNX Runtime), or with the face detector
/// built into Windows if ONNX Runtime can't load (it needs the Visual C++ runtime).
/// </summary>
internal static class FaceFinder
{
    static readonly Lazy<YuNetDetector?> YuNet = new(() =>
    {
        try { return new YuNetDetector(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"YuNet unavailable, using Windows face detection: {ex.Message}");
            return null;
        }
    });

    /// <summary>True when faces are found with YuNet rather than Windows' detector. Loads YuNet on first use.</summary>
    public static bool UsingYuNet => YuNet.Value != null;

    static bool WindowsSupported
    {
        get
        {
            try { return FaceDetector.IsSupported; }
            catch { return false; }
        }
    }

    /// <summary>Ovals around each face in <paramref name="image"/>, a little bigger than the face.</summary>
    public static async Task<List<Rectangle>> FindAsync(Bitmap image)
    {
        if (YuNet.Value is { } yunet)
        {
            var faces = BitmapPixels.Read(image, (bgra, stride) => yunet.Detect(bgra, image.Width, image.Height, stride));
            return faces.Select(f => OvalFor(f, image.Size)).Where(r => !r.IsEmpty).ToList();
        }
        if (!WindowsSupported) throw new NotSupportedException("Face detection isn't available on this PC.");
        return await WindowsFindAsync(image).ConfigureAwait(false);
    }

    /// <summary>
    /// The oval for one face. YuNet's box runs from the brow to the chin and cheek to cheek: take in a few percent more on
    /// each side, and a little extra above for the forehead. A head turned to the side also needs its ear and the back of
    /// the head (away from the nose), the tip of the nose, and more below for a beard, in step with how far it's turned.
    /// </summary>
    internal static Rectangle OvalFor(Core.DetectedFace face, Size image)
    {
        // Faces looking at the camera turn less than 0.05; profiles 0.13 and more.
        float k = Math.Clamp((MathF.Abs(face.Turn) - 0.05f) / 0.08f, 0, 1);
        float Mix(float frontal, float profile) => frontal + (profile - frontal) * k;
        float back = Mix(0.10f, 0.45f), front = Mix(0.10f, 0.15f);
        bool facingRight = face.Turn > 0;
        return Oval(face.Box, image, left: facingRight ? back : front, top: Mix(0.16f, 0.25f), right: facingRight ? front : back, bottom: Mix(0.08f, 0.28f));
    }

    // The margins are fractions of the face's size, and at least 3 px: boxes around tiny faces are less exact.
    static Rectangle Oval(RectangleF b, Size size, float left, float top, float right, float bottom)
    {
        float Margin(float share, float side) => MathF.Max(3, side * share);
        var r = RectangleF.FromLTRB(b.Left - Margin(left, b.Width), b.Top - Margin(top, b.Height), b.Right + Margin(right, b.Width), b.Bottom + Margin(bottom, b.Height));
        var cover = Rectangle.Intersect(Rectangle.Round(r), new Rectangle(Point.Empty, size));
        return cover.Width > 4 && cover.Height > 4 ? cover : Rectangle.Empty;
    }

    static async Task<List<Rectangle>> WindowsFindAsync(Bitmap image)
    {
        var detector = await FaceDetector.CreateAsync();
        var format = FaceDetector.IsBitmapPixelFormatSupported(BitmapPixelFormat.Gray8) ? BitmapPixelFormat.Gray8 : BitmapPixelFormat.Nv12;

        // Screens are full of small faces (avatars, video tiles); look at an enlarged copy of small areas.
        float scale = Math.Max(image.Width, image.Height) switch { < 700 => 3f, < 1400 => 2f, _ => 1f };
        using var bitmap = await OcrService.ToSoftwareBitmapAsync(image, scale, format);
        try { detector.MinDetectableFaceSize = new BitmapSize { Width = 24, Height = 24 }; }
        catch { /* keep the detector's own minimum */ }
        var faces = await detector.DetectFacesAsync(bitmap);

        // Windows' box runs from brow to chin; widen it and raise the top to take in the hair.
        var boxes = faces.Select(f => new RectangleF(f.FaceBox.X / scale, f.FaceBox.Y / scale, f.FaceBox.Width / scale, f.FaceBox.Height / scale));
        return boxes.Select(b => Oval(b, image.Size, left: 0.18f, top: 0.38f, right: 0.18f, bottom: 0.14f)).Where(r => !r.IsEmpty).ToList();
    }
}
