namespace LiteShot.Core;

/// <summary>Pixel work shared by Shield blur and the face detector. Images are 32-bit BGRA rows.</summary>
internal static class Pixels
{
    /// <summary>
    /// The exact mean colour of each cell of a grid laid over the image. Cell (i, j) covers
    /// [left + i·cellW, left + (i+1)·cellW) × [top + j·cellH, top + (j+1)·cellH); pixels it only partly covers count by
    /// their overlap, and outside the image the edge pixels repeat. Returns cols·rows·3 values in B, G, R order.
    /// </summary>
    public static float[] CellMeans(ReadOnlySpan<byte> bgra, int width, int height, int stride,
        float left, float top, float cellW, float cellH, int cols, int rows)
    {
        var xs = Coverage(left, cellW, cols, width);
        var ys = Coverage(top, cellH, rows, height);
        var means = new float[cols * rows * 3];
        for (int j = 0; j < rows; j++)
            for (int i = 0; i < cols; i++)
            {
                double b = 0, g = 0, r = 0, total = 0;
                foreach (var (y, wy) in ys[j])
                {
                    int row = y * stride;
                    foreach (var (x, wx) in xs[i])
                    {
                        double w = wx * wy;
                        int o = row + x * 4;
                        b += w * bgra[o];
                        g += w * bgra[o + 1];
                        r += w * bgra[o + 2];
                        total += w;
                    }
                }
                int k = (j * cols + i) * 3;
                means[k] = (float)(b / total);
                means[k + 1] = (float)(g / total);
                means[k + 2] = (float)(r / total);
            }
        return means;
    }

    /// <summary>
    /// Writes <paramref name="window"/> of the image, resized to <paramref name="sw"/> × <paramref name="sh"/>, into three
    /// float planes (B, G, R, values 0–255) of <paramref name="planeWidth"/> × <paramref name="planeSize"/>/planeWidth.
    /// Shrinking averages the pixels each output pixel covers; enlarging interpolates bilinearly.
    /// </summary>
    public static void ToPlanes(ReadOnlySpan<byte> bgra, int width, int height, int stride, System.Drawing.Rectangle window,
        int sw, int sh, float[] planes, int planeWidth, int planeSize)
    {
        if (sw == window.Width && sh == window.Height)
        {
            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                {
                    int o = (window.Y + y) * stride + (window.X + x) * 4, t = y * planeWidth + x;
                    planes[t] = bgra[o];
                    planes[planeSize + t] = bgra[o + 1];
                    planes[2 * planeSize + t] = bgra[o + 2];
                }
        }
        else if (sw < window.Width)
        {
            var means = CellMeans(bgra, width, height, stride, window.X, window.Y, (float)window.Width / sw, (float)window.Height / sh, sw, sh);
            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                {
                    int k = (y * sw + x) * 3, t = y * planeWidth + x;
                    planes[t] = means[k];
                    planes[planeSize + t] = means[k + 1];
                    planes[2 * planeSize + t] = means[k + 2];
                }
        }
        else
        {
            float fx = (float)window.Width / sw, fy = (float)window.Height / sh;
            for (int y = 0; y < sh; y++)
            {
                float v = window.Y + (y + 0.5f) * fy - 0.5f;
                int y0 = (int)MathF.Floor(v);
                float wy = v - y0;
                int ra = Math.Clamp(y0, 0, height - 1) * stride, rb = Math.Clamp(y0 + 1, 0, height - 1) * stride;
                for (int x = 0; x < sw; x++)
                {
                    float u = window.X + (x + 0.5f) * fx - 0.5f;
                    int x0 = (int)MathF.Floor(u);
                    float wx = u - x0;
                    int ca = Math.Clamp(x0, 0, width - 1) * 4, cb = Math.Clamp(x0 + 1, 0, width - 1) * 4;
                    int t = y * planeWidth + x;
                    for (int ch = 0; ch < 3; ch++)
                    {
                        float top = bgra[ra + ca + ch] * (1 - wx) + bgra[ra + cb + ch] * wx;
                        float bottom = bgra[rb + ca + ch] * (1 - wx) + bgra[rb + cb + ch] * wx;
                        planes[ch * planeSize + t] = top * (1 - wy) + bottom * wy;
                    }
                }
            }
        }
    }

    /// <summary>True when a window of the image is nearly one flat colour (a blank margin, an empty desktop).</summary>
    public static bool IsFlat(ReadOnlySpan<byte> bgra, int stride, System.Drawing.Rectangle window)
    {
        int low = 255, high = 0;
        for (int y = window.Top; y < window.Bottom; y += 3)
            for (int x = window.Left; x < window.Right; x += 3)
            {
                int o = y * stride + x * 4, luma = (bgra[o] + 2 * bgra[o + 1] + bgra[o + 2]) / 4;
                low = Math.Min(low, luma);
                high = Math.Max(high, luma);
                if (high - low > 16) return false;
            }
        return true;
    }

    // For each of `count` cells starting at `start`, the pixels it touches (clamped to the image) and how much of each.
    static (int Pixel, float Weight)[][] Coverage(float start, float cell, int count, int size)
    {
        var cells = new (int, float)[count][];
        var touched = new List<(int, float)>();
        for (int i = 0; i < count; i++)
        {
            float a = start + i * cell, b = a + cell;
            touched.Clear();
            for (int p = (int)MathF.Floor(a); p < b; p++)
            {
                float w = MathF.Min(b, p + 1) - MathF.Max(a, p);
                if (w > 0) touched.Add((Math.Clamp(p, 0, size - 1), w));
            }
            cells[i] = touched.ToArray();
        }
        return cells;
    }

    /// <summary>
    /// Gaussian blur with standard deviation <paramref name="sigma"/>, in place on one plane, done as the W3C filter
    /// spec describes for feGaussianBlur (and browsers implement CSS blur()): three box blurs in each direction.
    /// Edge pixels repeat.
    /// </summary>
    public static void GaussianBlur(float[] plane, int width, int height, float sigma)
    {
        int d = (int)MathF.Floor(sigma * 3 * MathF.Sqrt(2 * MathF.PI) / 4 + 0.5f);
        if (d < 1) return;
        // Odd d: three centred boxes of d. Even d: boxes of d leaning left, then right, then one of d + 1 centred.
        (int Left, int Right)[] passes = d % 2 == 1
            ? [((d - 1) / 2, (d - 1) / 2), ((d - 1) / 2, (d - 1) / 2), ((d - 1) / 2, (d - 1) / 2)]
            : [(d / 2, d / 2 - 1), (d / 2 - 1, d / 2), (d / 2, d / 2)];
        var line = new float[Math.Max(width, height)];
        foreach (var (l, r) in passes)
            for (int y = 0; y < height; y++) BoxLine(plane, y * width, 1, width, l, r, line);
        foreach (var (l, r) in passes)
            for (int x = 0; x < width; x++) BoxLine(plane, x, width, height, l, r, line);
    }

    // Replaces each value of one row or column with the mean of the values from `left` before it to `right` after it.
    static void BoxLine(float[] data, int start, int step, int count, int left, int right, float[] line)
    {
        for (int i = 0; i < count; i++) line[i] = data[start + i * step];
        double sum = 0;
        for (int k = -left; k <= right; k++) sum += line[Math.Clamp(k, 0, count - 1)];
        float n = left + right + 1;
        for (int i = 0; i < count; i++)
        {
            data[start + i * step] = (float)(sum / n);
            sum += line[Math.Min(i + right + 1, count - 1)] - line[Math.Max(i - left, 0)];
        }
    }
}
