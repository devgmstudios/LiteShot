using System.IO.Compression;

namespace LiteShot;

/// <summary>
/// Which words name a kind of something: "salmon" and "trout" are kinds of "fish". Find on screen falls back on this
/// when the typed word isn't on screen. Built from Open English WordNet (CC BY 4.0), itself derived from Princeton
/// WordNet; tools/related-words/build.py makes the table. Nouns only, all offline.
/// </summary>
internal static class RelatedWords
{
    sealed record Table(int[][] Parents, Dictionary<string, int[]> Words);

    static readonly Lazy<Table?> table = new(Load);

    static Table? Load()
    {
        using var stream = typeof(RelatedWords).Assembly.GetManifestResourceStream("LiteShot.related-words.gz");
        if (stream == null) return null;
        using var reader = new StreamReader(new GZipStream(stream, CompressionMode.Decompress));
        reader.ReadLine(); // header: source and license
        int count = int.Parse(reader.ReadLine()!.AsSpan(2));
        var parents = new int[count][];
        for (int i = 0; i < count; i++) parents[i] = Numbers(reader.ReadLine()!);
        reader.ReadLine(); // "W"
        var words = new Dictionary<string, int[]>(StringComparer.Ordinal);
        for (string? line = reader.ReadLine(); !string.IsNullOrEmpty(line); line = reader.ReadLine())
        {
            int tab = line.IndexOf('\t');
            words[line[..tab]] = Numbers(line[(tab + 1)..]);
        }
        return new Table(parents, words);
    }

    /// <summary>Words so general that everything is a kind of them, in one sense or another.</summary>
    static readonly HashSet<string> CatchAll = new(StringComparer.Ordinal)
    {
        "thing", "things", "stuff", "object", "objects", "item", "items", "entity", "something", "anything", "everything",
        "matter", "part", "parts", "kind", "sort", "type", "way", "piece", "unit", "whole", "group", "set",
    };

    static int[] Numbers(string s) => s.Length == 0 ? Array.Empty<int>() : s.Split(',').Select(int.Parse).ToArray();

    /// <summary>
    /// A test for words on screen that are the same thing as <paramref name="query"/>, a kind of it ("salmon" for
    /// "fish"), or one or two steps broader ("fish" for "salmon"); null when the word is unknown or too general to help
    /// ("thing", "object").
    /// </summary>
    public static Func<string, bool>? For(string query)
    {
        if (table.Value is not { } t) return null;
        if (CatchAll.Contains(Clean(query))) return null;
        // Everything is a kind of "entity": senses that close to the top would light up every noun on screen.
        var q = Senses(t, query).Where(s => Ancestors(t, s, 64).Count >= 4).ToArray();
        if (q.Length == 0) return null;
        var asked = q.ToHashSet();
        var broader = q.SelectMany(s => Ancestors(t, s, 2).Keys).ToHashSet();
        var seen = new Dictionary<string, bool>(StringComparer.Ordinal);
        return word =>
        {
            string key = Clean(word);
            if (key.Length < 2) return false;
            if (seen.TryGetValue(key, out bool known)) return known;
            bool related = false;
            foreach (int s in Senses(t, key))
                if (asked.Contains(s) || broader.Contains(s) || Ancestors(t, s, 14).Keys.Any(asked.Contains)) { related = true; break; }
            return seen[key] = related;
        };
    }

    /// <summary>A word's noun senses, trying it as read and then with common plural endings taken off.</summary>
    static int[] Senses(Table t, string word)
    {
        string w = Clean(word);
        foreach (var form in Forms(w))
            if (t.Words.TryGetValue(form, out var senses)) return senses;
        return Array.Empty<int>();
    }

    static IEnumerable<string> Forms(string w)
    {
        yield return w;
        if (w.EndsWith("ies") && w.Length > 4) yield return w[..^3] + "y";
        if (w.EndsWith("ves") && w.Length > 4) { yield return w[..^3] + "f"; yield return w[..^3] + "fe"; }
        if (w.EndsWith("es") && w.Length > 3) yield return w[..^2];
        if (w.EndsWith('s') && w.Length > 3) yield return w[..^1];
    }

    /// <summary>Lower case, without the punctuation OCR leaves around a word ("Salmon," → "salmon").</summary>
    public static string Clean(string word)
    {
        int a = 0, b = word.Length;
        while (a < b && !char.IsLetter(word[a])) a++;
        while (b > a && !char.IsLetter(word[b - 1])) b--;
        return word[a..b].ToLowerInvariant();
    }

    /// <summary>The senses above <paramref name="sense"/>, up to <paramref name="depth"/> steps, with how many steps.</summary>
    static Dictionary<int, int> Ancestors(Table t, int sense, int depth)
    {
        var seen = new Dictionary<int, int>();
        var frontier = new List<int> { sense };
        for (int d = 1; d <= depth && frontier.Count > 0; d++)
        {
            var next = new List<int>();
            foreach (int s in frontier)
                foreach (int p in t.Parents[s])
                    if (seen.TryAdd(p, d)) next.Add(p);
            frontier = next;
        }
        return seen;
    }
}
