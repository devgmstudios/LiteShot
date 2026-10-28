using LiteShot.Overlay;

namespace LiteShot;

/// <summary>
/// Finds text that looks like a person's name, so it can be blurred before a screenshot is shared.
/// Reads the screen with Windows' own OCR (offline), then looks for known first names, a first name followed by
/// a capitalized word or initial ("Sarah Johnson", "John S."), a title and a name ("Dr. Patel"), and common
/// surnames next to a capitalized word ("Xiaoming Zhang", "Zhang Wei"). It errs toward blurring.
/// </summary>
internal static class NameFinder
{
    /// <summary>Areas of <paramref name="image"/> that contain names, padded so the blur covers the whole word.</summary>
    public static async Task<List<Rectangle>> FindAsync(Bitmap image)
    {
        var lines = await OcrService.RecognizeWordsAsync(image).ConfigureAwait(false);
        var found = new List<Rectangle>();
        var bounds = new Rectangle(Point.Empty, image.Size);
        foreach (var line in lines)
            foreach (var (start, count) in Find(line.Select(w => w.Text).ToList()))
            {
                var r = line[start].Bounds;
                for (int i = start + 1; i < start + count; i++) r = RectangleF.Union(r, line[i].Bounds);
                float pad = Math.Max(2, r.Height * 0.22f);
                var cover = Rectangle.Intersect(Rectangle.Round(RectangleF.Inflate(r, pad, pad)), bounds);
                if (cover.Width > 2 && cover.Height > 2) found.Add(cover);
            }
        return found;
    }

    /// <summary>Finds names in <paramref name="image"/> and blurs them in place (for captures that skip the editor).</summary>
    public static async Task<int> BlurAsync(Bitmap image)
    {
        var areas = await FindAsync(image).ConfigureAwait(false);
        if (areas.Count == 0) return 0;
        using var source = image.Clone(new Rectangle(Point.Empty, image.Size), image.PixelFormat);
        using var g = Graphics.FromImage(image);
        foreach (var r in areas) BlurFor(r).Draw(g, source);
        return areas.Count;
    }

    /// <summary>A blur sized for text of this height: about one block per letter.</summary>
    public static BlurShape BlurFor(Rectangle r) =>
        new() { A = r.Location, B = new PointF(r.Right, r.Bottom), Size = Math.Max(3, r.Height * 0.32f) };

    /// <summary>The word spans (first index, word count) in one line of text that look like names.</summary>
    public static List<(int Start, int Count)> Find(IReadOnlyList<string> words)
    {
        var tokens = words.Select(Token.From).ToArray();
        var spans = new List<(int, int)>();
        for (int i = 0; i < tokens.Length;)
        {
            int n = Match(tokens, i);
            if (n > 0) { spans.Add((i, n)); i += n; }
            else i++;
        }
        return spans;
    }

    static int Match(Token[] t, int i)
    {
        var w = t[i];
        if (w.Word.Length == 0) return 0;

        // "Dr. Patel", "Mrs Smith": a title, then one or two capitalized words.
        if (NameLists.Titles.Contains(w.Lower) && i + 1 < t.Length && !w.EndsWithComma && t[i + 1].IsNameWord)
            return 1 + Following(t, i + 1, 2);

        if (!w.IsNameWord) return 0;
        bool first = NameLists.FirstNames.Contains(w.Lower);
        int next = w.BreaksAfter ? 0 : Following(t, i + 1, 2);

        // A first name with a surname or initial after it: "Sarah Johnson", "John S.", "Mary Ann Lee".
        // First names that are also words ("Grace Period", "Mark Read") need a known surname or an initial.
        if (first && next > 0 && (!NameLists.Ambiguous.Contains(w.Lower) || t[i + 1].IsInitial || NameLists.Surnames.Contains(t[i + 1].Lower)))
            return 1 + next;

        // A first name on its own, unless it's also an everyday word ("Will", "May", "Grace").
        if (first && !NameLists.Ambiguous.Contains(w.Lower) && !NameLists.Stop.Contains(w.Lower)) return 1;

        if (NameLists.Stop.Contains(w.Lower) || i + 1 >= t.Length) return 0;
        var n = t[i + 1];

        // "Smith, John": surname, comma, first name.
        if (w.Word.Length > 2 && NameLists.Surnames.Contains(w.Lower) && w.EndsWithComma && n.IsNameWord && NameLists.FirstNames.Contains(n.Lower))
            return 2;
        if (w.BreaksAfter || !n.IsNameWord || NameLists.Stop.Contains(n.Lower)) return 0;

        // An unfamiliar capitalized word next to a common surname ("Xiaoming Zhang"), or surname first ("Zhang Wei").
        if (n.Word.Length > 2 && NameLists.Surnames.Contains(n.Lower) && w.Word.Length > 2 && !w.AllCaps) return 2;
        if (w.Word.Length > 2 && NameLists.Surnames.Contains(w.Lower) && NameLists.FirstNames.Contains(n.Lower)) return 2;
        return 0;
    }

    /// <summary>How many of the next words (up to <paramref name="max"/>) continue a name: capitalized words or initials.</summary>
    static int Following(Token[] t, int from, int max)
    {
        int n = 0;
        while (n < max && from + n < t.Length)
        {
            var w = t[from + n];
            if (!(w.IsInitial || (w.IsNameWord && !NameLists.Stop.Contains(w.Lower)))) break;
            n++;
            if (w.BreaksAfter && !w.IsInitial) break;
        }
        return n;
    }

    readonly record struct Token(string Word, string Lower, bool IsNameWord, bool IsInitial, bool AllCaps, bool BreaksAfter, bool EndsWithComma)
    {
        const string Edge = "\"'“”‘’()[]{}<>«»,.;:!?*•·|/\\";

        public static Token From(string raw)
        {
            string word = raw.Trim().Trim(Edge.ToCharArray());
            if (word.EndsWith("'s", StringComparison.OrdinalIgnoreCase) || word.EndsWith("’s", StringComparison.OrdinalIgnoreCase))
                word = word[..^2];
            string end = raw.TrimEnd();
            bool comma = end.EndsWith(',');
            bool breaks = end.Length > 0 && ",;:!?)|•".Contains(end[^1]) || (end.EndsWith('.') && word.Length > 1);
            bool initial = word.Length == 1 && char.IsUpper(word[0]);
            bool letters = word.Length >= 2 && word.All(c => char.IsLetter(c) || c is '-' or '\'' or '’') && char.IsLetter(word[^1]);
            bool allCaps = letters && word.Where(char.IsLetter).All(char.IsUpper);
            string lower = word.ToLowerInvariant();
            // ALL-CAPS words are usually labels or acronyms; only treat them as names when they're known names.
            bool nameWord = letters && char.IsUpper(word[0])
                && (!allCaps || (word.Length >= 3 && (NameLists.FirstNames.Contains(lower) || NameLists.Surnames.Contains(lower))));
            return new Token(word, lower, nameWord, initial, allCaps, breaks, comma);
        }
    }
}
