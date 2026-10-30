using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using LiteShot.Core;

namespace LiteShot;

/// <summary>Hands bitmaps to LiteShot.Core as 32-bit BGRA rows, and turns its results back into bitmaps.</summary>
internal static class BitmapPixels
{
    public delegate T Reader<T>(ReadOnlySpan<byte> bgra, int stride);

    /// <summary>Calls <paramref name="read"/> with the whole bitmap as BGRA rows.</summary>
    public static unsafe T Read<T>(Bitmap bitmap, Reader<T> read)
    {
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { return read(new ReadOnlySpan<byte>((void*)data.Scan0, data.Stride * data.Height), data.Stride); }
        finally { bitmap.UnlockBits(data); }
    }

    /// <summary>A Shield blur patch as a bitmap with straight alpha, to draw at <see cref="ShieldPatch.Area"/>.</summary>
    public static Bitmap ToBitmap(ShieldPatch patch)
    {
        var area = patch.Area;
        var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(Point.Empty, area.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < area.Height; y++) Marshal.Copy(patch.Pixels, y * area.Width * 4, data.Scan0 + y * data.Stride, area.Width * 4);
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }
}
