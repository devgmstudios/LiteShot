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
/// a footer as the last frame shows it. Columns that stay put (a fixed side menu, a details panel, an inner
/// scrollbar) are left out of the matching, and below the first frame the side panels are filled with their background.
/// </summary>
internal sealed class ScrollStitcher
{
    public const int DefaultMaxHeight = 30_000;
    // Below this many moving rows, a change is a caret or a spinner, not a scroll.
    const int MinMovingRows = 8;
    // A shift counts when this share of the overlapping rows, and at least MinMatchedRows of them, match exactly.
    const float MinMatch = 0.85f;
    const int MinMatchedRows = 6;
    // A column scrolls when at least this share of the moving rows changed in it; fewer is a badge or a spinner.
    const int ChangedShare = 40;
    // Columns with fewer colour changes from top to bottom (margins, a scrollbar's track and thumb) say nothing about the shift.
    const int MinDetail = 6;

    readonly int maxHeight;

    /// <summary>The tallest image it will make; frames past it are cut off.</summary>
    public int MaxHeight => maxHeight;
    int width, height, ignoreRight;
    int[]? first, previous;
    // The columns lined up between frames: all but the scrollbar strip until the second frame shows which ones scroll.
    int[] cols = Array.Empty<int>();
    bool colsKnown;
    // The columns that scroll; outside them is a fixed side panel.
    int bandLo, bandHi;
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
            cols = Enumerable.Range(0, width - ignoreRight).ToArray();
            bandLo = 0;
            bandHi = width - ignoreRight;
            first = previous = ReadRows(frame, 0, height);
            return height >= frame.Height ? StitchStep.Added : StitchStep.Full;
        }
        if (frame.Width != width || frame.Height < height) throw new ArgumentException("Every frame must be the size of the first.");
        if (Height >= maxHeight) return StitchStep.Full;

        var pixels = ReadRows(frame, 0, height);
        var use = cols;
        var a = Print(previous!, use);
        var next = Print(pixels, use);
        var (top, bottom) = Unchanged(a, next);
        if (height - top - bottom < MinMovingRows) return StitchStep.Same;
        int lo = bandLo, hi = bandHi;
        if (!colsKnown)
        {
            // The second frame shows which columns scroll; line up those alone.
            (use, lo, hi) = Scrolling(previous!, pixels, top, height - bottom);
            if (hi <= lo) return StitchStep.Same;
            if (use.Length == 0) use = Enumerable.Range(lo, hi - lo).ToArray();
            a = Print(previous!, use);
            next = Print(pixels, use);
            (top, bottom) = Unchanged(a, next);
            if (height - top - bottom < MinMovingRows) return StitchStep.Same;
        }

        int shift = FindShift(a, next, top, height - bottom, expectedShift);
        if (shift <= 0) return StitchStep.Lost;
        cols = use;
        colsKnown = true;
        (bandLo, bandHi) = (lo, hi);

        // The footer can only shrink as frames come in, so keeping this frame from (footer + shift) rows above its
        // bottom covers whatever the final footer turns out to be.
        Header = strips.Count == 0 ? top : Math.Min(Header, top);
        footer = Math.Min(footer, bottom);
        int start = Math.Max(0, height - footer - shift);
        int take = Math.Min(shift, maxHeight - Height);
        var kept = new int[(height - start) * width];
        Array.Copy(pixels, start * width, kept, 0, kept.Length);
        strips.Add((kept, start, shift, take));
        previous = pixels;
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
            // Under the first frame, a fixed side panel would show its own rows again: fill it with its background.
            // Plain margins are left as the frames show them, so a full-width section further down keeps its edges.
            int n = width - ignoreRight;
            bool left = Busy(0, bandLo, height - f), right = Busy(bandHi, n, height - f);
            var row = new int[width];
            for (int c = 0; c < n; c++)
                if ((left && c < bandLo) || (right && c >= bandHi)) row[c] = Background(c, height - f);
            for (int yy = height - f; yy < total - f; yy++)
            {
                if (left) Marshal.Copy(row, 0, data.Scan0 + yy * data.Stride, bandLo);
                if (right) Marshal.Copy(row, bandHi, data.Scan0 + yy * data.Stride + bandHi * 4, n - bandHi);
            }
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

    RowPrint Print(int[] pixels, int[] columns)
    {
        var hashes = new ulong[height];
        var flat = new bool[height];
        for (int r = 0; r < height; r++)
        {
            int o = r * width;
            ulong h = 14695981039346656037UL;
            bool same = true;
            int firstPixel = pixels[o + columns[0]];
            foreach (int x in columns)
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

    /// <summary>How many rows at the top and at the bottom are the same in both frames.</summary>
    (int Top, int Bottom) Unchanged(RowPrint a, RowPrint b)
    {
        int top = 0;
        while (top < height && a.Hashes[top] == b.Hashes[top]) top++;
        int bottom = 0;
        while (bottom < height - top && a.Hashes[height - 1 - bottom] == b.Hashes[height - 1 - bottom]) bottom++;
        return (top, bottom);
    }

    /// <summary>
    /// Which columns scrolled between frames <paramref name="a"/> and <paramref name="b"/> across rows [lo, hi): the band
    /// from the first to the last that changed, and the ones in it with enough detail to line up.
    /// </summary>
    (int[] Detailed, int Lo, int Hi) Scrolling(int[] a, int[] b, int lo, int hi)
    {
        int n = width - ignoreRight;
        var changed = new int[n];
        var detail = new int[n];
        for (int r = lo; r < hi; r++)
        {
            int o = r * width;
            for (int x = 0; x < n; x++)
            {
                if (a[o + x] != b[o + x]) changed[x]++;
                if (r > lo && (a[o + x] != a[o - width + x] || b[o + x] != b[o - width + x])) detail[x]++;
            }
        }
        int need = Math.Max(MinMovingRows, (hi - lo) / ChangedShare);
        int first = Array.FindIndex(changed, c => c >= need), last = Array.FindLastIndex(changed, c => c >= need);
        if (first < 0) return (Array.Empty<int>(), 0, 0);
        // Out to the edge of what moved at all, but not across a still gap into a side panel.
        while (first > 0 && changed[first - 1] > 0) first--;
        while (last < n - 1 && changed[last + 1] > 0) last++;
        var detailed = Enumerable.Range(first, last - first + 1).Where(x => changed[x] >= need && detail[x] > MinDetail).ToArray();
        return (detailed, first, last + 1);
    }

    /// <summary>Whether columns [from, to) of the first frame show something (a menu, a panel), not just a background.</summary>
    bool Busy(int from, int to, int bottom)
    {
        for (int c = from; c < to; c++)
        {
            int changes = 0;
            for (int r = Header + 1; r < bottom; r++)
                if (first![r * width + c] != first[(r - 1) * width + c] && ++changes > MinDetail) return true;
        }
        return false;
    }

    /// <summary>The colour column <paramref name="c"/> of the first frame shows most, above row <paramref name="bottom"/>.</summary>
    int Background(int c, int bottom)
    {
        var counts = new Dictionary<int, int>();
        for (int r = Header; r < bottom; r++) counts[first![r * width + c]] = counts.GetValueOrDefault(first[r * width + c]) + 1;
        return counts.Count == 0 ? first![c] : counts.MaxBy(kv => kv.Value).Key;
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
