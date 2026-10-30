using System.Buffers.Binary;
using System.Drawing;
using System.Security.Cryptography;
using System.Text;

namespace LiteShot.Core;

public enum ShieldShape { Ellipse, Rect }

/// <summary>
/// Shield blur's result for one region: <see cref="Area"/> in image pixels, and <see cref="Pixels"/> holding it as BGRA
/// rows (Area.Width · 4 bytes each) with straight alpha. The alpha is the shape's soft-edged mask, so drawing the patch
/// over the image applies the blur and leaves everything outside the shape alone.
/// </summary>
public sealed record ShieldPatch(Rectangle Area, byte[] Pixels);

/// <summary>
/// Shield blur 4.2.1 by FacePrivacy.ai: an outline-preserving blur that can't be reversed. A port of the reference
/// module (faceprivacy.ai/lib/shield-blur-4.2.1.js, the same algorithm as 4.2.0), following technical note FP-TN-01 v1.1,
/// https://faceprivacy.ai/research/shield-blur/ — section numbers below are the note's.
/// The region is reduced to about eight blocks across on a randomly shifted grid, pulled toward its mean colour,
/// dithered and quantised, then smoothed and grained: the outline and colours survive, the face does not.
/// Shield Blur License 1.0 (MIT with attribution): show <see cref="Credit"/>, linked to <see cref="CreditUrl"/>,
/// where users can see it.
/// </summary>
public static class ShieldBlur
{
    public const string Version = "4.2.1";
    public const string Credit = "Shield blur by FacePrivacy.ai";
    public const string CreditUrl = "https://faceprivacy.ai";
    public const float DefaultStrength = 20;

    const string Label = "faceprivacy-shield-v4.2"; // the algorithm's label: unchanged in 4.2.1
    const double Pull = 0.45;

    /// <summary>
    /// Blurs <paramref name="region"/> (the box around a face, with some margin) of an image given as BGRA rows.
    /// <paramref name="strength"/> is the reference tool's Blur slider (5–50); <paramref name="brush"/> the brush
    /// diameter when the area was painted; <paramref name="entropy"/> optional extra random bytes to mix in.
    /// Returns null when the region is too small to blur.
    /// </summary>
    public static ShieldPatch? Render(ReadOnlySpan<byte> bgra, int width, int height, int stride, RectangleF region,
        ShieldShape shape = ShieldShape.Ellipse, float strength = DefaultStrength, float brush = 0, ReadOnlySpan<byte> entropy = default)
    {
        float s = Math.Clamp(strength, 5, 50);
        int pad = (int)MathF.Ceiling(s) + 4;
        int x0 = Math.Max(0, (int)MathF.Floor(region.X - pad)), y0 = Math.Max(0, (int)MathF.Floor(region.Y - pad));
        int x1 = Math.Min(width, (int)MathF.Ceiling(region.Right + pad)), y1 = Math.Min(height, (int)MathF.Ceiling(region.Bottom + pad));
        int w = x1 - x0, h = y1 - y0;
        if (w < 2 || h < 2 || region.Width <= 0 || region.Height <= 0) return null;

        // §3.1 Block size, and the grid's random phase.
        float c = MathF.Max(12, MathF.Max(brush / 6, Math.Min(w, h) / 8f));
        int nx = Math.Max(2, (int)MathF.Ceiling(w / c) + 1), ny = Math.Max(2, (int)MathF.Ceiling(h / c) + 1);
        byte[] S = RandomStream(8 + nx * ny * 3 + w * h, entropy);
        float phiX = S[0] / 256f * c, phiY = S[1] / 256f * c;

        // §3.2 Each block's mean colour, kept at 8 bits per channel like the reference's tiny canvas. q is R, G, B.
        var means = Pixels.CellMeans(bgra, width, height, stride, x0 - phiX, y0 - phiY, c, c, nx, ny);
        var q = new double[nx * ny * 3];
        for (int k = 0; k < nx * ny; k++)
            for (int ch = 0; ch < 3; ch++) q[k * 3 + ch] = ToByte(means[k * 3 + 2 - ch]);

        // §3.3 Pull every block 45% toward the mean of the blocks whose centre is inside the shape, then add noise that
        // is mostly shared by the three channels, and quantise each channel to a step of 10.
        float rx = region.X - x0, ry = region.Y - y0, rw = region.Width, rh = region.Height;
        var mean = new double[3];
        int inside = 0;
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                if (!Inside(shape, i * c - phiX + c / 2, j * c - phiY + c / 2, rx, ry, rw, rh)) continue;
                for (int ch = 0; ch < 3; ch++) mean[ch] += q[(j * nx + i) * 3 + ch];
                inside++;
            }
        if (inside > 0) for (int ch = 0; ch < 3; ch++) mean[ch] /= inside;
        for (int k = 0; k < nx * ny; k++)
        {
            int lum = S[8 + k * 3] % 13 - 6;
            for (int ch = 0; ch < 3; ch++)
            {
                double v = q[k * 3 + ch];
                if (inside > 0) v = ToByte(v + Pull * (mean[ch] - v));
                int nu = lum + (ch == 0 ? 0 : S[8 + k * 3 + ch] % 3 - 1);
                q[k * 3 + ch] = Math.Clamp(Math.Floor((v + nu) / 10 + 0.5) * 10, 0, 255);
            }
        }

        // §3.4 Upsample the blocks bilinearly back onto the shifted grid, smooth with a Gaussian, then add grain.
        var planes = new[] { new float[w * h], new float[w * h], new float[w * h] };
        for (int py = 0; py < h; py++)
        {
            float v = (py + 0.5f + phiY) / c - 0.5f;
            int j0 = (int)MathF.Floor(v);
            float fy = v - j0;
            int ja = Math.Clamp(j0, 0, ny - 1), jb = Math.Clamp(j0 + 1, 0, ny - 1);
            for (int px = 0; px < w; px++)
            {
                float u = (px + 0.5f + phiX) / c - 0.5f;
                int i0 = (int)MathF.Floor(u);
                float fx = u - i0;
                int ia = Math.Clamp(i0, 0, nx - 1), ib = Math.Clamp(i0 + 1, 0, nx - 1);
                for (int ch = 0; ch < 3; ch++)
                {
                    double top = q[(ja * nx + ia) * 3 + ch] * (1 - fx) + q[(ja * nx + ib) * 3 + ch] * fx;
                    double bottom = q[(jb * nx + ia) * 3 + ch] * (1 - fx) + q[(jb * nx + ib) * 3 + ch] * fx;
                    planes[ch][py * w + px] = (float)(top * (1 - fy) + bottom * fy);
                }
            }
        }
        foreach (var plane in planes) Pixels.GaussianBlur(plane, w, h, MathF.Max(3, 0.6f * s));

        // Keep it to the region's shape, with a soft 1 px edge.
        var mask = Mask(shape, w, h, rx, ry, rw, rh);
        Pixels.GaussianBlur(mask, w, h, 1);

        var pixels = new byte[w * h * 4];
        int g0 = 8 + nx * ny * 3;
        for (int i = 0; i < w * h; i++)
        {
            int grain = S[g0 + i] % 9 - 4;
            pixels[i * 4] = (byte)Math.Clamp(ToByte(planes[2][i]) + grain, 0, 255);
            pixels[i * 4 + 1] = (byte)Math.Clamp(ToByte(planes[1][i]) + grain, 0, 255);
            pixels[i * 4 + 2] = (byte)Math.Clamp(ToByte(planes[0][i]) + grain, 0, 255);
            pixels[i * 4 + 3] = (byte)ToByte(mask[i] * 255);
        }
        CryptographicOperations.ZeroMemory(S);
        return new ShieldPatch(new Rectangle(x0, y0, w, h), pixels);
    }

    /// <summary>Draws a patch over an image given as BGRA rows (source-over, straight alpha).</summary>
    public static void Blend(ShieldPatch patch, Span<byte> bgra, int stride)
    {
        var a = patch.Area;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                int p = (y * a.Width + x) * 4, o = (a.Y + y) * stride + (a.X + x) * 4;
                int alpha = patch.Pixels[p + 3];
                if (alpha == 0) continue;
                for (int ch = 0; ch < 3; ch++) bgra[o + ch] = (byte)((patch.Pixels[p + ch] * alpha + bgra[o + ch] * (255 - alpha) + 127) / 255);
            }
    }

    static bool Inside(ShieldShape shape, float px, float py, float rx, float ry, float rw, float rh) => shape == ShieldShape.Rect
        ? px >= rx && py >= ry && px < rx + rw && py < ry + rh
        : Sq((px - rx - rw / 2) / (rw / 2)) + Sq((py - ry - rh / 2) / (rh / 2)) <= 1;

    // How much of each pixel the shape covers: exact for a rectangle, 4 × 4 samples near an ellipse's edge.
    static float[] Mask(ShieldShape shape, int w, int h, float rx, float ry, float rw, float rh)
    {
        var mask = new float[w * h];
        float band = 1 / MathF.Min(rw / 2, rh / 2);
        for (int py = 0; py < h; py++)
            for (int px = 0; px < w; px++)
            {
                float cover;
                if (shape == ShieldShape.Rect)
                    cover = Math.Clamp(MathF.Min(px + 1, rx + rw) - MathF.Max(px, rx), 0, 1) * Math.Clamp(MathF.Min(py + 1, ry + rh) - MathF.Max(py, ry), 0, 1);
                else
                {
                    float d = MathF.Sqrt(Sq((px + 0.5f - rx - rw / 2) / (rw / 2)) + Sq((py + 0.5f - ry - rh / 2) / (rh / 2)));
                    if (d <= 1 - band) cover = 1;
                    else if (d >= 1 + band) cover = 0;
                    else
                    {
                        int n = 0;
                        for (int sy = 0; sy < 4; sy++)
                            for (int sx = 0; sx < 4; sx++)
                                if (Inside(shape, px + (sx + 0.5f) / 4, py + (sy + 0.5f) / 4, rx, ry, rw, rh)) n++;
                        cover = n / 16f;
                    }
                }
                mask[py * w + px] = cover;
            }
        return mask;
    }

    /// <summary>
    /// §4: the random stream. SHA-256 over the version label, 32 bytes from the system CSPRNG and any extra entropy is
    /// the key for AES-256-CTR over zeros, from a random counter block whose last 64 bits count (as Web Crypto's
    /// AES-CTR with length 64 does).
    /// </summary>
    static byte[] RandomStream(int length, ReadOnlySpan<byte> entropy)
    {
        byte[] label = Encoding.ASCII.GetBytes(Label);
        var seed = new byte[label.Length + 32 + entropy.Length];
        label.CopyTo(seed, 0);
        RandomNumberGenerator.Fill(seed.AsSpan(label.Length, 32));
        entropy.CopyTo(seed.AsSpan(label.Length + 32));
        byte[] key = SHA256.HashData(seed);
        CryptographicOperations.ZeroMemory(seed);

        byte[] counter = RandomNumberGenerator.GetBytes(16);
        ulong low = BinaryPrimitives.ReadUInt64BigEndian(counter.AsSpan(8));
        int blocks = (length + 15) / 16;
        var counters = new byte[blocks * 16];
        for (int b = 0; b < blocks; b++)
        {
            counter.AsSpan(0, 8).CopyTo(counters.AsSpan(b * 16));
            BinaryPrimitives.WriteUInt64BigEndian(counters.AsSpan(b * 16 + 8), unchecked(low + (ulong)b));
        }
        using var aes = Aes.Create();
        aes.Key = key;
        CryptographicOperations.ZeroMemory(key);
        byte[] stream = aes.EncryptEcb(counters, PaddingMode.None);
        return stream.Length == length ? stream : stream[..length];
    }

    // How Uint8ClampedArray stores a number: rounded half to even, clamped to 0–255.
    static double ToByte(double v) => Math.Clamp(Math.Round(v), 0, 255);

    static float Sq(float v) => v * v;
}
