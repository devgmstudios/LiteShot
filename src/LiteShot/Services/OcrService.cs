using System.Drawing.Drawing2D;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace LiteShot;

/// <summary>A recognized word and where it is in the source image.</summary>
internal readonly record struct OcrWord(string Text, RectangleF Bounds);

/// <summary>Text recognition with the OCR engine built into Windows — offline, no extra downloads.</summary>
internal static class OcrService
{
    static OcrEngine CreateEngine() => OcrEngine.TryCreateFromUserProfileLanguages()
        ?? throw new InvalidOperationException("No OCR language is installed. Add one under Settings › Time & language › Language & region.");

    public static async Task<string> RecognizeAsync(Bitmap image)
    {
        var engine = CreateEngine();
        using var bitmap = await ToSoftwareBitmapAsync(image, UpscaleFor(image));
        var result = await engine.RecognizeAsync(bitmap);
        return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
    }

    /// <summary>Every line as its words, with positions in <paramref name="image"/>'s pixels.</summary>
    public static async Task<List<List<OcrWord>>> RecognizeWordsAsync(Bitmap image)
    {
        var engine = CreateEngine();
        // Upscaling helps with small screen text; cap the work for big multi-monitor captures.
        float scale = Math.Min(2f, MathF.Sqrt(16_000_000f / Math.Max(1, image.Width * image.Height)));
        scale = Math.Max(1f, Math.Min(scale, Math.Min(OcrEngine.MaxImageDimension / (float)image.Width, OcrEngine.MaxImageDimension / (float)image.Height)));
        using var bitmap = await ToSoftwareBitmapAsync(image, scale);
        var result = await engine.RecognizeAsync(bitmap);
        return result.Lines.Select(l => l.Words.Select(w => new OcrWord(w.Text, new RectangleF(
            (float)w.BoundingRect.X / scale, (float)w.BoundingRect.Y / scale,
            (float)w.BoundingRect.Width / scale, (float)w.BoundingRect.Height / scale))).ToList()).ToList();
    }

    /// <summary>The engine is most accurate on larger glyphs, and screen text at 100% scale is small, so upscale.</summary>
    static float UpscaleFor(Bitmap src)
    {
        float max = OcrEngine.MaxImageDimension;
        float scale = src.Width < 1800 && src.Height < 1800 ? 2f : 1f;
        return Math.Min(scale, Math.Min(max / src.Width, max / src.Height));
    }

    /// <summary>Converts to a WinRT bitmap (BGRA, or <paramref name="format"/>) for the Windows imaging APIs.</summary>
    public static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap src, float scale, BitmapPixelFormat format = BitmapPixelFormat.Bgra8)
    {
        byte[] png;
        using (var prepared = Resize(src, scale)) png = ImageOutput.ToPng(prepared);
        using var stream = new MemoryStream(png);
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        if (format == BitmapPixelFormat.Bgra8) return bitmap;
        using (bitmap) return SoftwareBitmap.Convert(bitmap, format);
    }

    static Bitmap Resize(Bitmap src, float scale)
    {
        int w = Math.Max(1, (int)(src.Width * scale)), h = Math.Max(1, (int)(src.Height * scale));
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = scale == 1f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
        return bmp;
    }
}
