using System.Text;
using System.Text.RegularExpressions;
using LiteShot.Overlay;

namespace LiteShot;

/// <summary>Something "Hide personal info" will blur: a stretch of text, or a face (blurred as an oval).</summary>
internal readonly record struct Redaction(Rectangle Area, bool Face);

/// <summary>
/// Finds personal details in a capture, all on this PC: names (<see cref="NameFinder"/>), email addresses, phone
/// numbers, card and social security numbers, the user's own keywords — read with the OCR built into Windows — and
/// faces, found with Windows' face detector.
/// </summary>
internal static class PersonalInfo
{
    static readonly Regex Email = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}", RegexOptions.Compiled);
    // Not inside a longer run of digit groups (a card number), so the boundaries also refuse "digit, space, digit".
    static readonly Regex Phone = new(@"(?<![\w-]|\d\s)(?:\+?\d{1,3}[\s.-]?)?(?:\(\d{2,4}\)|\d{2,4})[\s.-]\d{3,4}[\s.-]\d{3,4}(?![\w-]|\s\d)", RegexOptions.Compiled);
    static readonly Regex Ssn = new(@"(?<!\d)\d{3}-\d{2}-\d{4}(?!\d)", RegexOptions.Compiled);
    static readonly Regex CardLike = new(@"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)", RegexOptions.Compiled);

    public static async Task<List<Redaction>> FindAsync(Bitmap image, IReadOnlyList<string> keywords, bool faces)
    {
        var found = new List<Redaction>();
        var bounds = new Rectangle(Point.Empty, image.Size);
        foreach (var line in await OcrService.RecognizeWordsAsync(image).ConfigureAwait(false))
            foreach (var (start, count) in FindInLine(line.Select(w => w.Text).ToList(), keywords))
            {
                var r = line[start].Bounds;
                for (int i = start + 1; i < start + count; i++) r = RectangleF.Union(r, line[i].Bounds);
                float pad = Math.Max(2, r.Height * 0.22f);
                var cover = Rectangle.Intersect(Rectangle.Round(RectangleF.Inflate(r, pad, pad)), bounds);
                if (cover.Width > 2 && cover.Height > 2) found.Add(new Redaction(cover, false));
            }
        if (faces && FaceFinder.Supported)
        {
            try
            {
                foreach (var face in await FaceFinder.FindAsync(image).ConfigureAwait(false)) found.Add(new Redaction(face, true));
            }
            catch { /* text is still hidden; faces can be blurred with F */ }
        }
        return Distinct(found);
    }

    /// <summary>Word spans in one OCR line that hold personal details. Spans may overlap; callers merge the areas.</summary>
    public static List<(int Start, int Count)> FindInLine(IReadOnlyList<string> words, IReadOnlyList<string> keywords)
    {
        var spans = new List<(int Start, int Count)>(NameFinder.Find(words));
        if (words.Count == 0) return spans;

        // Join the words with single spaces, remembering where each starts, so a pattern can run across words.
        var text = new StringBuilder();
        var starts = new int[words.Count];
        for (int i = 0; i < words.Count; i++)
        {
            if (i > 0) text.Append(' ');
            starts[i] = text.Length;
            text.Append(words[i]);
        }
        string line = text.ToString();
        void Add(int index, int length)
        {
            int first = WordAt(starts, index), last = WordAt(starts, index + length - 1);
            spans.Add((first, last - first + 1));
        }

        foreach (Match m in Email.Matches(line)) Add(m.Index, m.Length);
        foreach (Match m in Phone.Matches(line)) if (Digits(m.Value) is >= 7 and <= 15) Add(m.Index, m.Length);
        foreach (Match m in Ssn.Matches(line)) Add(m.Index, m.Length);
        foreach (Match m in CardLike.Matches(line)) if (Luhn(m.Value)) Add(m.Index, m.Length);
        foreach (var raw in keywords)
        {
            string k = raw.Trim();
            if (k.Length < 2) continue;
            for (int at = line.IndexOf(k, StringComparison.OrdinalIgnoreCase); at >= 0; at = line.IndexOf(k, at + k.Length, StringComparison.OrdinalIgnoreCase))
                Add(at, k.Length);
        }
        return spans;
    }

    /// <summary>Finds personal details in <paramref name="image"/> and blurs them in place (for captures that skip the editor).</summary>
    public static async Task<int> BlurAsync(Bitmap image, IReadOnlyList<string> keywords)
    {
        var found = await FindAsync(image, keywords, faces: true).ConfigureAwait(false);
        if (found.Count == 0) return 0;
        using var source = image.Clone(new Rectangle(Point.Empty, image.Size), image.PixelFormat);
        using var g = Graphics.FromImage(image);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        foreach (var r in found) BlurFor(r).Draw(g, source);
        return found.Count;
    }

    /// <summary>The blur for one finding: about a block per letter for text, a soft oval for a face.</summary>
    public static BlurShape BlurFor(Redaction r) => r.Face
        ? new BlurShape { A = r.Area.Location, B = new PointF(r.Area.Right, r.Area.Bottom), Oval = true, Size = Math.Max(4, r.Area.Width / 7f), Auto = true }
        : new BlurShape { A = r.Area.Location, B = new PointF(r.Area.Right, r.Area.Bottom), Size = Math.Max(3, r.Area.Height * 0.32f), Auto = true };

    static int WordAt(int[] starts, int index)
    {
        int i = Array.BinarySearch(starts, index);
        return i >= 0 ? i : Math.Max(0, ~i - 1);
    }

    static int Digits(string s) => s.Count(char.IsDigit);

    static bool Luhn(string s)
    {
        var digits = s.Where(char.IsDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19) return false;
        int sum = 0;
        for (int i = 0; i < digits.Length; i++)
        {
            int d = digits[digits.Length - 1 - i];
            if (i % 2 == 1) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return sum % 10 == 0;
    }

    /// <summary>Drops findings that sit inside another one (a name inside a keyword match, say).</summary>
    static List<Redaction> Distinct(List<Redaction> all)
    {
        var kept = new List<Redaction>();
        foreach (var r in all.OrderByDescending(x => x.Area.Width * x.Area.Height))
        {
            bool inside = kept.Any(k => k.Face == r.Face && Rectangle.Intersect(k.Area, r.Area) is var i
                && i.Width * i.Height >= 0.85 * r.Area.Width * r.Area.Height);
            if (!inside) kept.Add(r);
        }
        return kept;
    }
}
