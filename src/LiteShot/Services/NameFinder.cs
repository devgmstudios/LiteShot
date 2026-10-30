using LiteShot.Overlay;

namespace LiteShot;

/// <summary>
/// Finds text that looks like a person's name, so it can be blurred before a screenshot is shared.
/// Reads the screen with Windows' own OCR (offline), then looks for known first names, a first name followed by
/// a capitalized word or initial ("Sarah Johnson", "John S."), a title and a name ("Dr. Patel"), and common
/// surnames next to a capitalized word ("Xiaoming Zhang", "Zhang Wei"); names run on through particles and suffixes
/// ("Robles de la Fuente", "Aumalo Jr"). Words around a name give it away even when no list knows it ("emailed
/// Maelis Ondrako", "Hi Oriel", "Darvin Kesselring replied"). The page's layout finds the rest: the cells of a column headed "Name",
/// "Customer", "Owner"…, and the other rows of a list whose rows are names. It errs toward blurring.
/// </summary>
internal static class NameFinder
{
    /// <summary>Areas of <paramref name="image"/> that contain names, padded so the blur covers the whole word.</summary>
    public static async Task<List<Rectangle>> FindAsync(Bitmap image)
    {
        var lines = await OcrService.RecognizeWordsAsync(image).ConfigureAwait(false);
        var spans = new List<(int Line, int Start, int Count)>();
        for (int i = 0; i < lines.Count; i++)
            foreach (var (start, count) in Find(lines[i].Select(w => w.Text).ToList())) spans.Add((i, start, count));
        spans.AddRange(FromLayout(lines));
        var found = new List<Rectangle>();
        foreach (var (line, start, count) in spans)
        {
            var cover = Cover(lines[line], start, count, image.Size);
            if (!cover.IsEmpty) found.Add(cover);
        }
        return found;
    }

    /// <summary>The padded area over words <paramref name="start"/>…, clipped to the image; empty if too small.</summary>
    public static Rectangle Cover(IReadOnlyList<OcrWord> line, int start, int count, Size image)
    {
        var r = line[start].Bounds;
        for (int i = start + 1; i < start + count; i++) r = RectangleF.Union(r, line[i].Bounds);
        float pad = Math.Max(2, r.Height * 0.22f);
        var cover = Rectangle.Intersect(Rectangle.Round(RectangleF.Inflate(r, pad, pad)), new Rectangle(Point.Empty, image));
        return cover.Width > 2 && cover.Height > 2 ? cover : Rectangle.Empty;
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
        int done = 0;
        for (int i = 0; i < tokens.Length;)
        {
            int n = Match(tokens, i);
            // Whatever the lists say, capitalized words right after "emailed", "Hi"… or right before "replied",
            // "wrote"… are a person: "emailed Maelis Ondrako", "Darvin Kesselring replied". Not when they're all
            // everyday words: "emailed Customer Support", "Support replied".
            if (n == 0 && i > 0 && NameLists.Cues.Contains(tokens[i - 1].Lower) && !tokens[i - 1].BreaksAfter && IsPart(tokens[i]))
                n = Following(tokens, i, 3);
            if (n == 0) n = BeforeVerb(tokens, i);
            if (n > 0 && Match(tokens, i) == 0 && tokens.Skip(i).Take(n).All(Plain)) n = 0;
            if (n == 0) { i++; continue; }
            // A name found from its second part (a surname, or a first name the lists don't know) takes the
            // capitalized words just before it: "Tavelu" in "Tavelu Nguyen".
            int start = i;
            if (!NameLists.FirstNames.Contains(tokens[i].Lower))
                while (start > done && i - start < 2 && IsPart(tokens[start - 1]) && !tokens[start - 1].BreaksAfter) start--;
            spans.Add((start, i + n - start));
            i += n;
            done = i;
        }
        return spans;
    }

    static int Match(Token[] t, int i)
    {
        var w = t[i];
        if (w.Word.Length == 0) return 0;

        // "Dr. Patel", "Mrs Smith": a title, then one or more capitalized words.
        if (NameLists.Titles.Contains(w.Lower) && i + 1 < t.Length && !w.EndsWithComma && t[i + 1].IsNameWord)
            return 1 + Following(t, i + 1, 3);

        if (!w.IsNameWord || (i > 0 && NameLists.PlaceWords.Contains(t[i - 1].Lower))) return 0;
        bool first = NameLists.FirstNames.Contains(w.Lower);
        int next = w.BreaksAfter ? 0 : Following(t, i + 1, 3);
        // In ALL CAPS only listed words read as names, so a known first name takes the all-caps word after it (and after
        // any initials) as its surname: "MARTA KVELLESTAD", "MARTA J KVELLESTAD".
        if (first && w.AllCaps && !w.BreaksAfter)
        {
            int j = i + 1;
            while (j < i + 1 + next && t[j].IsInitial && !t[j].BreaksAfter) j++;
            if (j == i + 1 + next && j < t.Length && t[j].AllCaps && !NameLists.Stop.Contains(t[j].Lower)) next = j - i;
        }

        // A first name with a surname or initial after it: "Sarah Johnson", "John S.", "Mary Ann Lee".
        // First names that are also words ("Grace Period", "Mark Read") need a known surname or an initial.
        if (first && next > 0 && (!NameLists.Ambiguous.Contains(w.Lower) || Initialed(t, i + 1) || NameLists.Surnames.Contains(t[i + 1].Lower)))
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
        // The unfamiliar word can't be an everyday one: "Install Norton".
        if (n.Word.Length > 2 && NameLists.Surnames.Contains(n.Lower) && w.Word.Length > 2 && !w.AllCaps && !Plain(w)) return 1 + Following(t, i + 1, 3);
        if (w.Word.Length > 2 && NameLists.Surnames.Contains(w.Lower) && NameLists.FirstNames.Contains(n.Lower)) return 1 + Following(t, i + 1, 3);
        return 0;
    }

    /// <summary>
    /// How many of the next words continue a name: up to <paramref name="max"/> capitalized words or initials, with
    /// particles between them ("de la", "van", "bin") and a closing suffix ("Jr", "III").
    /// </summary>
    static int Following(Token[] t, int from, int max)
    {
        int n = 0, words = 0;
        while (from + n < t.Length)
        {
            var w = t[from + n];
            if (words < max && (w.IsInitial || IsPart(w)))
            {
                n++;
                words++;
                if (w.BreaksAfter && !w.IsInitial) break;
                continue;
            }
            int p = Particles(t, from + n);
            if (p > 0 && words < max) { n += p; continue; }
            if (NameLists.Suffixes.Contains(w.Lower)) n++;
            break;
        }
        return n;
    }

    // One or two particles ("de", "de la") at `at`, followed by a capitalized word; 0 otherwise.
    static int Particles(Token[] t, int at)
    {
        int p = 0;
        while (p < 2 && at + p < t.Length && NameLists.Particles.Contains(t[at + p].Lower) && !t[at + p].BreaksAfter) p++;
        return p > 0 && at + p < t.Length && IsPart(t[at + p]) ? p : 0;
    }

    static bool IsPart(Token w) => w.IsNameWord && !NameLists.Stop.Contains(w.Lower);

    // An initial that really is one, "Mark S." or "Mark S Lee", not "Press H" or "Can I".
    static bool Initialed(Token[] t, int k) =>
        t[k].IsInitial && (t[k].Dotted || (t[k].Word != "I" && k + 1 < t.Length && IsPart(t[k + 1])));

    // An everyday English word that no list knows as a name: "Install", "Customer", "Support".
    static bool Plain(Token w) => NameLists.IsWord(w.Lower) && !NameLists.FirstNames.Contains(w.Lower) && !NameLists.Surnames.Contains(w.Lower);

    // One to three capitalized words just before a verb like "emailed" or "wrote": "Darvin Kesselring replied", "Oriel wrote".
    static int BeforeVerb(Token[] t, int i)
    {
        int k = 0;
        while (k < 3 && i + k < t.Length && IsPart(t[i + k]) && !t[i + k].BreaksAfter) k++;
        return k >= 1 && i + k < t.Length && NameLists.SaidVerbs.Contains(t[i + k].Lower) ? k : 0;
    }

    // ───────────────────────────── layout ─────────────────────────────

    /// <summary>
    /// Names found from where text sits rather than from the words alone: the cells of a column headed "Name",
    /// "Customer", "Owner"…, and the other rows of a list most of whose rows are names. Returns spans
    /// (line, first word, word count) into <paramref name="lines"/> that <see cref="Find"/> doesn't already cover.
    /// </summary>
    public static List<(int Line, int Start, int Count)> FromLayout(IReadOnlyList<IReadOnlyList<OcrWord>> lines)
    {
        var tokens = lines.Select(l => l.Select(w => Token.From(w.Text)).ToArray()).ToArray();
        var known = new List<(int Line, int Start, int Count)>();
        for (int i = 0; i < lines.Count; i++)
            foreach (var (s, c) in Find(lines[i].Select(w => w.Text).ToList())) known.Add((i, s, c));

        var added = new List<(int Line, int Start, int Count)>();
        void Add((int Line, int Start, int Count) span)
        {
            if (!known.Concat(added).Any(k => k.Line == span.Line && k.Start <= span.Start && span.Start + span.Count <= k.Start + k.Count)) added.Add(span);
        }
        for (int i = 0; i < lines.Count; i++)
            for (int k = 0; k < tokens[i].Length; k++)
                if (NameLists.NameHeaders.Contains(tokens[i][k].Lower) && !tokens[i][k].BreaksAfter)
                    foreach (var span in Column(lines, tokens, i, k)) Add(span);
        foreach (var span in ListRows(lines, tokens, known.Concat(added).ToList())) Add(span);
        return added;
    }

    /// <summary>
    /// The name cells below the header word (line <paramref name="hl"/>, word <paramref name="hw"/>): in each row, the
    /// first word that starts within the column (a flag or avatar may push it to the right), walking down while the
    /// rows keep a steady rhythm. Needs two names, so a stray "Name" label doesn't claim what follows it.
    /// </summary>
    static List<(int Line, int Start, int Count)> Column(IReadOnlyList<IReadOnlyList<OcrWord>> lines, Token[][] tokens, int hl, int hw)
    {
        var header = lines[hl][hw].Bounds;
        // "Full name", "Customer name": the column starts at the first word of the header.
        float left = hw > 0 && lines[hl][hw].Bounds.Left - lines[hl][hw - 1].Bounds.Right < header.Height * 1.2f ? lines[hl][hw - 1].Bounds.Left : header.Left;
        // A header heads its column: text lined up just above it means this is a cell ("Owner" in a Role column).
        float near = Math.Max(0.8f * header.Height, 8);
        if (lines.Where((l, i) => i != hl).SelectMany(l => l).Any(w => Math.Abs(w.Bounds.Left - left) <= near
                && w.Bounds.Bottom <= header.Top && header.Top - w.Bounds.Bottom < 4 * header.Height))
            return new();
        var cells = new List<(int Line, int Word, RectangleF Box)>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (i == hl) continue;
            for (int k = 0; k < lines[i].Count; k++)
            {
                // OCR reads flags, ticks and avatars as stray "I", "u" or "|": they don't start a cell.
                if (lines[i][k].Text.Count(char.IsLetterOrDigit) < 2) continue;
                var b = lines[i][k].Bounds;
                float h = Math.Max(header.Height, b.Height);
                if (b.Top < header.Bottom || b.Left < left - 2 * h) continue;
                if (b.Left <= left + Math.Max(5 * h, 48)) cells.Add((i, k, b));
                break;
            }
        }
        // The column ends at a big gap, or at text that doesn't line up with the cells above it (a footer, say).
        var found = new List<(int, int, int)>();
        float last = header.Top, pitch = 0, start = float.NaN;
        foreach (var c in cells.OrderBy(c => c.Box.Top))
        {
            float gap = c.Box.Top - last;
            if (gap < c.Box.Height * 0.5f) continue;
            float h = Math.Max(header.Height, c.Box.Height);
            if (gap > (pitch == 0 ? 6 * h : Math.Max(2.5f * pitch, 3 * h))) break;
            if (!float.IsNaN(start) && Math.Abs(c.Box.Left - start) > Math.Max(0.8f * h, 8)) break;
            start = float.IsNaN(start) ? c.Box.Left : start;
            pitch = Math.Max(pitch, gap);
            last = c.Box.Top;
            int n = Run(lines[c.Line], tokens[c.Line], c.Word, inColumn: true);
            if (n > 0) found.Add((c.Line, c.Word, n));
        }
        return found.Count >= 2 ? found : new();
    }

    /// <summary>
    /// The other rows of a list of names: lines whose text starts at the same x as a name already found, at the same
    /// text height, in an evenly spaced block. When at least a third of the block (and two rows) are known names, the
    /// rest are taken as names too: "Annika Brandauer" between "Emma Clarke" and "Wei Zhang".
    /// </summary>
    static List<(int Line, int Start, int Count)> ListRows(IReadOnlyList<IReadOnlyList<OcrWord>> lines, Token[][] tokens, List<(int Line, int Start, int Count)> known)
    {
        var found = new List<(int, int, int)>();
        foreach (var anchor in known)
        {
            var a = lines[anchor.Line][anchor.Start].Bounds;
            var rows = new List<(int Line, int Word, int Count, RectangleF Box)>();
            for (int i = 0; i < lines.Count; i++)
                for (int k = 0; k < lines[i].Count; k++)
                {
                    var b = lines[i][k].Bounds;
                    if (Math.Abs(b.Left - a.Left) > a.Height * 0.6f) continue;
                    if (Math.Abs(b.Height - a.Height) <= Math.Max(a.Height, b.Height) * 0.4f && Run(lines[i], tokens[i], k, inColumn: false) is var n and > 0)
                        rows.Add((i, k, n, b));
                    break;
                }
            rows.Sort((x, y) => x.Box.Top.CompareTo(y.Box.Top));
            var gaps = rows.Zip(rows.Skip(1), (x, y) => y.Box.Top - x.Box.Top).OrderBy(g => g).ToList();
            if (gaps.Count < 2) continue;
            float typical = gaps[gaps.Count / 2];
            // The evenly spaced block of rows around the anchor.
            int at = rows.FindIndex(r => r.Line == anchor.Line), from = at, to = at;
            if (at < 0) continue;
            while (from > 0 && rows[from].Box.Top - rows[from - 1].Box.Top <= typical * 1.6f) from--;
            while (to < rows.Count - 1 && rows[to + 1].Box.Top - rows[to].Box.Top <= typical * 1.6f) to++;
            var block = rows.GetRange(from, to - from + 1);
            bool Known((int Line, int Word, int Count, RectangleF Box) r) => known.Any(s => s.Line == r.Line && s.Start <= r.Word && r.Word < s.Start + s.Count);
            int named = block.Count(Known);
            // A row taken on the list's word alone has to end where its name does (or at the next column): a feed's
            // "Absolutely! I've attached…" lines up with the senders above it, but it's a sentence.
            if (block.Count >= 3 && named >= 2 && named * 3 >= block.Count)
                found.AddRange(block.Where(r => Known(r) || EndsAfter(lines[r.Line], r.Word + r.Count)).Select(r => (r.Line, r.Word, r.Count)));
        }
        return found;
    }

    // True when nothing follows word `at` on its line but stray marks (a tick, an avatar read as "I"), or a gap as wide as a column.
    static bool EndsAfter(IReadOnlyList<OcrWord> words, int at)
    {
        int next = at;
        while (next < words.Count && words[next].Text.Count(char.IsLetterOrDigit) < 2) next++;
        if (next >= words.Count) return true;
        var prev = words[at - 1].Bounds;
        return words[next].Bounds.Left - prev.Right > prev.Height * 1.5f;
    }

    /// <summary>
    /// How many words from <paramref name="k"/> read as one name: capitalized words, initials, particles and a suffix,
    /// with no wide gap between them (a gap that wide is the next column). In a name column, first names that are
    /// also everyday words ("Will", "May") count too.
    /// </summary>
    static int Run(IReadOnlyList<OcrWord> words, Token[] t, int k, bool inColumn)
    {
        bool Part(Token w) => IsPart(w) || (inColumn && w.IsNameWord && NameLists.FirstNames.Contains(w.Lower));
        if (!Part(t[k]) || t[k].Word.Length < 2) return 0;
        int n = 1;
        while (k + n < t.Length && n < 7 && !t[k + n - 1].BreaksAfter)
        {
            var prev = words[k + n - 1].Bounds;
            if (words[k + n].Bounds.Left - prev.Right > prev.Height * 1.5f) break;
            var w = t[k + n];
            if (Part(w) || w.IsInitial) { n++; continue; }
            int p = Particles(t, k + n);
            if (p > 0) { n += p; continue; }
            if (NameLists.Suffixes.Contains(w.Lower)) n++;
            break;
        }
        return n;
    }

    readonly record struct Token(string Word, string Lower, bool IsNameWord, bool IsInitial, bool AllCaps, bool BreaksAfter, bool EndsWithComma, bool Dotted)
    {
        const string Edge = "\"'“”‘’()[]{}<>«»,.;:!?*•·|/\\";

        public static Token From(string raw)
        {
            // OCR runs a word into the next across an ellipsis ("Kevin...sounds"): read the part before it.
            int dots = raw.IndexOf("...", StringComparison.Ordinal) is var d and > 0 ? d : raw.IndexOf('…');
            if (dots > 0) raw = raw[..dots] + ".";
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
            return new Token(word, lower, nameWord, initial, allCaps, breaks, comma, end.EndsWith('.'));
        }
    }
}
