using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LiteShot;

/// <summary>What became of a frame handed to <see cref="ScrollStitcher.Add"/>.</summary>
internal enum StitchStep
{
    /// <summary>The page moved; the rows that scrolled into view were kept.</summary>
    Added,
    /// <summary>Nothing moved: the end of the page, or the window didn't scroll.</summary>
    Same,
    /// <summary>The frame couldn't be lined up with the one before (it scrolled too far, or the page changed).</summary>
    Lost,
    /// <summary>The image reached its height limit; what fit was kept.</summary>
    Full,
}

/// <summary>
/// Joins the frames of a scrolling capture into one tall image. Each frame is lined up with the one before by
/// matching rows of pixels, and only the rows that scrolled into view are kept. Rows that stay put while the page
/// moves (a sticky header, a cookie bar, a chat's message box) appear once: a header as the first frame shows it,
/// a footer as the last frame shows it.
/// </summary>
internal sealed class ScrollStitcher
{
    public const int DefaultMaxHeight = 30_000;
    // Below this many moving rows, a change is a caret or a spinner, not a scroll.
    const int MinMovingRows = 8;
    // A shift counts when this share of the overlapping rows, and at least MinMatchedRows of them, match exactly.
    const float MinMatch = 0.85f;
    const int MinMatchedRows = 6;

    readonly int maxHeight;

    /// <summary>The tallest image it will make; frames past it are cut off.</summary>
    public int MaxHeight => maxHeight;
    int width, height, ignoreRight;
    int[]? first;
    RowPrint? previous;
    // Each later frame: its pixels from row Start down, and how many rows scrolled into view (Shift; Take of them kept).
    readonly List<(int[] Pixels, int Start, int Shift, int Take)> strips = new();
    int footer = int.MaxValue;

    public ScrollStitcher(int maxHeight = DefaultMaxHeight) => this.maxHeight = maxHeight;

    /// <summary>Frames that added rows, the first one included.</summary>
    public int Frames => first == null ? 0 : strips.Count + 1;

    /// <summary>How tall the stitched image is so far.</summary>
    public int Height => first == null ? 0 : Math.Min(maxHeight, height + strips.Sum(s => s.Take));

    /// <summary>How far the last added frame had scrolled, in pixels.</summary>
    public int LastShift { get; private set; }

    /// <summary>The rows of the frames that stay put while the page scrolls: a sticky header and footer.</summary>
    public int Header { get; private set; }
    public int Footer => footer == int.MaxValue ? 0 : footer;

    /// <summary>
    /// Adds the next frame, which must be the size of the first. <paramref name="expectedShift"/>, when known, picks
    /// between equally good matches on repetitive pages. A frame that comes back <see cref="StitchStep.Same"/> or
    /// <see cref="StitchStep.Lost"/> changes nothing, so a fresh frame can be tried again.
    /// </summary>
    public StitchStep Add(Bitmap frame, int expectedShift = 0)
    {
        if (first == null)
        {
            width = frame.Width;
            height = Math.Min(frame.Height, maxHeight);
            // The page's scrollbar moves while the content scrolls; leave its strip out of the matching.
            ignoreRight = width >= 200 ? Math.Min(width / 10, 32) : 0;
            first = ReadRows(frame, 0, height);
            previous = Print(first, 0, height);
            return height >= frame.Height ? StitchStep.Added : StitchStep.Full;
        }
        if (frame.Width != width || frame.Height < height) throw new ArgumentException("Every frame must be the size of the first.");
        if (Height >= maxHeight) return StitchStep.Full;

        var pixels = ReadRows(frame, 0, height);
        var next = Print(pixels, 0, height);
        var a = previous!.Value;
        int top = 0;
        while (top < height && a.Hashes[top] == next.Hashes[top]) top++;
        if (top == height) return StitchStep.Same;
        int bottom = 0;
        while (bottom < height - top && a.Hashes[height - 1 - bottom] == next.Hashes[height - 1 - bottom]) bottom++;
        if (height - top - bottom < MinMovingRows) return StitchStep.Same;

        int shift = FindShift(a, next, top, height - bottom, expectedShift);
        if (shift <= 0) return StitchStep.Lost;

        // The footer can only shrink as frames come in, so keeping this frame from (footer + shift) rows above its
        // bottom covers whatever the final footer turns out to be.
        Header = strips.Count == 0 ? top : Math.Min(Header, top);
        footer = Math.Min(footer, bottom);
        int start = Math.Max(0, height - footer - shift);
        int take = Math.Min(shift, maxHeight - Height);
        var kept = new int[(height - start) * width];
        Array.Copy(pixels, start * width, kept, 0, kept.Length);
        strips.Add((kept, start, shift, take));
        previous = next;
        LastShift = shift;
        return take < shift || Height >= maxHeight ? StitchStep.Full : StitchStep.Added;
    }

    /// <summary>The stitched image: the first frame down to the footer, each frame's new rows, then the last footer.</summary>
    public Bitmap Compose()
    {
        if (first == null) throw new InvalidOperationException("No frames were added.");
        int f = Footer, total = Height;
        var image = new Bitmap(width, total, PixelFormat.Format32bppArgb);
        image.SetResolution(96, 96);
        var data = image.LockBits(new Rectangle(0, 0, width, total), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int y = 0;
            void Copy(int[] source, int fromRow, int rows)
            {
                for (int r = 0; r < rows && y < total; r++, y++)
                    Marshal.Copy(source, (fromRow + r) * width, data.Scan0 + y * data.Stride, width);
            }
            Copy(first, 0, height - f);
            foreach (var (pixels, start, shift, take) in strips) Copy(pixels, height - f - shift - start, take);
            if (strips.Count == 0) Copy(first, height - f, f);
            else Copy(strips[^1].Pixels, height - f - strips[^1].Start, f);
        }
        finally { image.UnlockBits(data); }
        return image;
    }

    /// <summary>
    /// How far frame <paramref name="b"/> scrolled past <paramref name="a"/>: the d for which b's row i shows a's row
    /// i + d across rows [lo, hi), the part of both frames that moved. 0 when nothing lines up.
    /// </summary>
    static int FindShift(RowPrint a, RowPrint b, int lo, int hi, int expected)
    {
        int best = 0, bestMatched = 0;
        float bestShare = 0;
        for (int d = 1; d < hi - lo; d++)
        {
            int matched = 0, counted = 0;
            for (int i = lo; i < hi - d; i++)
            {
                // Rows of one flat colour match anywhere, so they say nothing about the shift.
                if (b.Flat[i] && a.Flat[i + d]) continue;
                counted++;
                if (b.Hashes[i] == a.Hashes[i + d]) matched++;
            }
            if (matched < MinMatchedRows) continue;
            float share = (float)matched / counted;
            if (share < MinMatch) continue;
            bool better = best == 0
                || (expected > 0 && Math.Abs(share - bestShare) < 0.02f
                    ? Math.Abs(d - expected) < Math.Abs(best - expected)
                    : share > bestShare + 0.0001f || (Math.Abs(share - bestShare) <= 0.0001f && matched > bestMatched));
            if (better) { best = d; bestShare = share; bestMatched = matched; }
        }
        return best;
    }

    readonly record struct RowPrint(ulong[] Hashes, bool[] Flat);

    RowPrint Print(int[] pixels, int fromRow, int rows)
    {
        var hashes = new ulong[rows];
        var flat = new bool[rows];
        int cols = width - ignoreRight;
        for (int r = 0; r < rows; r++)
        {
            int o = (fromRow + r) * width;
            ulong h = 14695981039346656037UL;
            bool same = true;
            int firstPixel = pixels[o];
            for (int x = 0; x < cols; x++)
            {
                int p = pixels[o + x];
                h = (h ^ (uint)p) * 1099511628211UL;
                same &= p == firstPixel;
            }
            hashes[r] = h;
            flat[r] = same;
        }
        return new RowPrint(hashes, flat);
    }

    static int[] ReadRows(Bitmap bitmap, int fromRow, int rows)
    {
        int w = bitmap.Width;
        var pixels = new int[w * rows];
        var data = bitmap.LockBits(new Rectangle(0, fromRow, w, rows), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int r = 0; r < rows; r++) Marshal.Copy(data.Scan0 + r * data.Stride, pixels, r * w, w);
        }
        finally { bitmap.UnlockBits(data); }
        return pixels;
    }

    /// <summary>True when two captures of the same area show the same pixels (the page has stopped moving).</summary>
    public static bool SamePixels(Bitmap a, Bitmap b)
    {
        if (a.Size != b.Size) return false;
        var pa = ReadRows(a, 0, a.Height);
        var pb = ReadRows(b, 0, b.Height);
        return pa.AsSpan().SequenceEqual(pb);
    }
}
