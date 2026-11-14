import Foundation

/// Which words name a kind of something: "salmon" and "trout" are kinds of "fish". Find on screen falls back on this
/// when the typed word isn't on screen. Built from Open English WordNet (CC BY 4.0), itself derived from Princeton
/// WordNet, by tools/related-words/build.py — the same table, and the same rules, as the Windows app.
public final class RelatedWords {
    let parents: [[Int]]
    let words: [String: [Int]]

    /// The table shipped in the app bundle's Resources.
    public static let shared: RelatedWords? = Bundle.main.url(forResource: "related-words", withExtension: "gz")
        .flatMap { try? Data(contentsOf: $0) }.flatMap { RelatedWords(gzip: $0) }

    /// Words so general that everything is a kind of them, in one sense or another.
    static let catchAll: Set<String> = [
        "thing", "things", "stuff", "object", "objects", "item", "items", "entity", "something", "anything", "everything",
        "matter", "part", "parts", "kind", "sort", "type", "way", "piece", "unit", "whole", "group", "set",
    ]

    public init?(gzip data: Data) {
        guard let text = RelatedWords.gunzip(data).flatMap({ String(data: $0, encoding: .utf8) }) else { return nil }
        var lines = text.split(separator: "\n", omittingEmptySubsequences: false)[...]
        guard lines.count > 2 else { return nil }
        lines = lines.dropFirst() // header: source and license
        guard let countLine = lines.popFirst(), countLine.hasPrefix("S "), let count = Int(countLine.dropFirst(2)) else { return nil }
        var parents: [[Int]] = []
        parents.reserveCapacity(count)
        for _ in 0..<count {
            guard let line = lines.popFirst() else { return nil }
            parents.append(line.isEmpty ? [] : line.split(separator: ",").compactMap { Int($0) })
        }
        _ = lines.popFirst() // "W"
        var words: [String: [Int]] = [:]
        for line in lines where !line.isEmpty {
            guard let tab = line.firstIndex(of: "\t") else { continue }
            words[String(line[..<tab])] = line[line.index(after: tab)...].split(separator: ",").compactMap { Int($0) }
        }
        self.parents = parents
        self.words = words
    }

    /// A test for words on screen that are the same thing as `query`, a kind of it ("salmon" for "fish"), or one or
    /// two steps broader ("fish" for "salmon"); nil when the word is unknown or too general to help ("thing").
    public func matcher(for query: String) -> ((String) -> Bool)? {
        let q = RelatedWords.clean(query)
        guard !RelatedWords.catchAll.contains(q) else { return nil }
        // Everything is a kind of "entity": senses that close to the top would light up every noun on screen.
        let asked = Set(senses(q).filter { ancestors($0, depth: 64).count >= 4 })
        guard !asked.isEmpty else { return nil }
        let broader = Set(asked.flatMap { ancestors($0, depth: 2).keys })
        var seen: [String: Bool] = [:]
        return { [self] word in
            let key = RelatedWords.clean(word)
            guard key.count >= 2 else { return false }
            if let known = seen[key] { return known }
            let related = senses(key).contains { s in
                asked.contains(s) || broader.contains(s) || ancestors(s, depth: 14).keys.contains(where: asked.contains)
            }
            seen[key] = related
            return related
        }
    }

    /// A word's noun senses, trying it as read and then with common plural endings taken off.
    func senses(_ word: String) -> [Int] {
        var forms = [word]
        if word.count > 4, word.hasSuffix("ies") { forms.append(String(word.dropLast(3)) + "y") }
        if word.count > 4, word.hasSuffix("ves") { forms += [String(word.dropLast(3)) + "f", String(word.dropLast(3)) + "fe"] }
        if word.count > 3, word.hasSuffix("es") { forms.append(String(word.dropLast(2))) }
        if word.count > 3, word.hasSuffix("s") { forms.append(String(word.dropLast())) }
        for f in forms { if let s = words[f] { return s } }
        return []
    }

    /// The senses above `sense`, up to `depth` steps, with how many steps.
    func ancestors(_ sense: Int, depth: Int) -> [Int: Int] {
        var seen: [Int: Int] = [:]
        var frontier = [sense]
        var d = 0
        while d < depth, !frontier.isEmpty {
            d += 1
            var next: [Int] = []
            for s in frontier where s < parents.count {
                for p in parents[s] where seen[p] == nil {
                    seen[p] = d
                    next.append(p)
                }
            }
            frontier = next
        }
        return seen
    }

    /// Lower case, without the punctuation OCR leaves around a word ("Salmon," → "salmon").
    public static func clean(_ word: String) -> String {
        word.trimmingCharacters(in: CharacterSet.letters.inverted).lowercased()
    }

    /// The deflate stream inside a gzip file (Foundation's .zlib is raw deflate), skipping the optional header fields.
    static func gunzip(_ data: Data) -> Data? {
        let b = [UInt8](data)
        guard b.count > 18, b[0] == 0x1f, b[1] == 0x8b, b[2] == 8 else { return nil }
        let flags = b[3]
        var i = 10
        if flags & 4 != 0 { guard i + 2 <= b.count else { return nil }; i += 2 + (Int(b[i]) | (Int(b[i + 1]) << 8)) }
        if flags & 8 != 0 { while i < b.count, b[i] != 0 { i += 1 }; i += 1 }
        if flags & 16 != 0 { while i < b.count, b[i] != 0 { i += 1 }; i += 1 }
        if flags & 2 != 0 { i += 2 }
        guard i < b.count - 8 else { return nil }
        return try? (Data(b[i..<(b.count - 8)]) as NSData).decompressed(using: .zlib) as Data
    }
}

extension TextFinder {
    /// When a single word isn't on screen: the words in `line` that are the same thing, a kind of it, or just
    /// broader ("salmon" and "trout" for "fish"), as ranges of `line`.
    public static func relatedRanges(of query: String, in line: String, words: RelatedWords) -> [Range<String.Index>] {
        guard let q = normalized(query), !q.contains(" "), let related = words.matcher(for: q) else { return [] }
        let asked = RelatedWords.clean(q)
        var found: [Range<String.Index>] = []
        line.enumerateSubstrings(in: line.startIndex..<line.endIndex, options: .byWords) { word, range, _, _ in
            if let word, RelatedWords.clean(word) != asked, related(word) { found.append(range) }
        }
        return found
    }
}
