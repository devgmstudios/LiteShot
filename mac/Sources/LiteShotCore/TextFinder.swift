import CoreGraphics
import Foundation

/// "Find on screen": where a phrase appears in a line of recognized text. Matching ignores case and accents, and any
/// run of spaces in the phrase matches one space, so a phrase can span words but not lines (the same rules as Windows).
public enum TextFinder {
    /// The phrase with its spaces tidied, or nil when there's nothing to look for.
    public static func normalized(_ query: String) -> String? {
        let words = query.split(whereSeparator: \.isWhitespace)
        return words.isEmpty ? nil : words.joined(separator: " ")
    }

    /// Every place `query` appears in `line`, as ranges of `line` itself.
    public static func ranges(of query: String, in line: String) -> [Range<String.Index>] {
        guard let q = normalized(query) else { return [] }
        // Recognized lines can carry doubled spaces; match those as one too.
        var found: [Range<String.Index>] = []
        var from = line.startIndex
        while from < line.endIndex,
              let r = line.range(of: q, options: [.caseInsensitive, .diacriticInsensitive], range: from..<line.endIndex) {
            found.append(r)
            from = r.upperBound
        }
        if found.isEmpty, q.contains(" ") {
            let pattern = q.split(separator: " ").map { NSRegularExpression.escapedPattern(for: String($0)) }.joined(separator: "\\s+")
            if let re = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive]) {
                found = re.matches(in: line, range: NSRange(line.startIndex..., in: line)).compactMap { Range($0.range, in: line) }
            }
        }
        return found
    }

    /// Top to bottom, then left to right, for boxes in top-left coordinates. Boxes whose middles share a row count as one line.
    public static func readingOrder(_ boxes: [CGRect]) -> [CGRect] {
        boxes.sorted { a, b in
            let sameRow = abs(a.midY - b.midY) < min(a.height, b.height) / 2
            return sameRow ? a.minX < b.minX : a.midY < b.midY
        }
    }
}
