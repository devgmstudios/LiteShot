using System.Globalization;
using System.Text;

namespace LiteShot;

/// <summary>
/// "Find on screen": where a phrase appears in recognized text. Matching ignores case and accents, and any run of
/// spaces in the phrase matches the gap between two words, so a phrase can span words but not lines.
/// </summary>
internal static class TextFinder
{
    /// <summary>A box around every match, in the words' pixels, in reading order.</summary>
    public static List<RectangleF> Find(IReadOnlyList<IReadOnlyList<OcrWord>> lines, string query)
    {
        var hits = new List<RectangleF>();
        string q = Fold(string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        if (q.Length == 0) return hits;

        foreach (var words in lines)
        {
            if (words.Count == 0) continue;
            // The line as one string, one folded character per original character, remembering each word's start.
            var text = new StringBuilder();
            var starts = new int[words.Count];
            for (int i = 0; i < words.Count; i++)
            {
                if (i > 0) text.Append(' ');
                starts[i] = text.Length;
                text.Append(Fold(words[i].Text));
            }
            string line = text.ToString();
            for (int at = line.IndexOf(q, StringComparison.Ordinal); at >= 0; at = line.IndexOf(q, at + q.Length, StringComparison.Ordinal))
                hits.Add(Box(words, starts, at, at + q.Length));
        }
        // Lines come back roughly in reading order; sort so stepping through goes top to bottom, left to right.
        return hits.OrderBy(h => MathF.Round(h.Top / Math.Max(1, h.Height))).ThenBy(h => h.Left).ToList();
    }

    /// <summary>
    /// When a single word isn't on screen: the words that are the same thing, a kind of it or just broader, by
    /// <see cref="RelatedWords"/> ("fish" finds salmon and trout). Whole words, in reading order, at most 150.
    /// </summary>
    public static List<RectangleF> FindRelated(IReadOnlyList<IReadOnlyList<OcrWord>> lines, string query)
    {
        var hits = new List<RectangleF>();
        string q = query.Trim();
        if (q.Length < 2 || q.Any(char.IsWhiteSpace) || RelatedWords.For(q) is not { } related) return hits;
        string asked = RelatedWords.Clean(q);
        foreach (var words in lines)
            foreach (var w in words)
                if (RelatedWords.Clean(w.Text) != asked && related(w.Text)) hits.Add(w.Bounds);
        return hits.OrderBy(h => MathF.Round(h.Top / Math.Max(1, h.Height))).ThenBy(h => h.Left).Take(150).ToList();
    }

    /// <summary>The part of the line's words from character <paramref name="from"/> up to <paramref name="to"/>.</summary>
    static RectangleF Box(IReadOnlyList<OcrWord> words, int[] starts, int from, int to)
    {
        int first = WordAt(starts, from), last = WordAt(starts, to - 1);
        RectangleF box = RectangleF.Empty;
        for (int i = first; i <= last; i++)
        {
            var b = words[i].Bounds;
            string word = words[i].Text;
            float l = i == first ? b.Left + b.Width * Share(word, from - starts[i]) : b.Left;
            float r = i == last ? b.Left + b.Width * Share(word, to - starts[i]) : b.Right;
            var part = RectangleF.FromLTRB(l, b.Top, Math.Max(r, l + 1), b.Bottom);
            box = box.IsEmpty ? part : RectangleF.Union(box, part);
        }
        return box;
    }

    static readonly Font MeasureFont = new("Segoe UI", 20, GraphicsUnit.Pixel);

    /// <summary>How far into <paramref name="word"/> its first <paramref name="chars"/> characters reach, 0 to 1, by glyph widths:
    /// a comma is narrower than a "w", so letter counts would land the edge in the wrong place.</summary>
    static float Share(string word, int chars)
    {
        chars = Math.Clamp(chars, 0, word.Length);
        if (chars == 0 || chars == word.Length || word.Length == 0) return chars == 0 ? 0 : 1;
        float whole = Ui.Measure(word, MeasureFont).Width;
        return whole <= 0 ? (float)chars / word.Length : Math.Clamp(Ui.Measure(word[..chars], MeasureFont).Width / whole, 0, 1);
    }

    static int WordAt(int[] starts, int index)
    {
        int i = Array.BinarySearch(starts, index);
        return i >= 0 ? i : Math.Max(0, ~i - 1);
    }

    /// <summary>Lower case without accents, one character for one, so positions line up with the original.</summary>
    static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            string d = c.ToString().Normalize(NormalizationForm.FormD);
            char basic = d.Length > 0 && CharUnicodeInfo.GetUnicodeCategory(d[0]) != UnicodeCategory.NonSpacingMark ? d[0] : c;
            sb.Append(char.ToLowerInvariant(basic));
        }
        return sb.ToString();
    }
}
