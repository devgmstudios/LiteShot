import Foundation

/// The text rules behind "Hide personal info", the same as the Windows app's: email addresses, phone numbers,
/// social security numbers, card numbers that pass the Luhn check, and the user's own keywords.
public enum PersonalInfo {
    static let email = try! NSRegularExpression(pattern: #"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}"#)
    // Not inside a longer run of digit groups (a card number), so the boundaries also refuse "digit, space, digit".
    static let phone = try! NSRegularExpression(pattern: #"(?<![\w-]|\d\s)(?:\+?\d{1,3}[\s.-]?)?(?:\(\d{2,4}\)|\d{2,4})[\s.-]\d{3,4}[\s.-]\d{3,4}(?![\w-]|\s\d)"#)
    static let ssn = try! NSRegularExpression(pattern: #"(?<!\d)\d{3}-\d{2}-\d{4}(?!\d)"#)
    static let cardLike = try! NSRegularExpression(pattern: #"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)"#)

    /// Every stretch of `text` to hide. Ranges may overlap; the caller covers each one.
    public static func ranges(in text: String, keywords: [String] = []) -> [Range<String.Index>] {
        var found: [Range<String.Index>] = []
        let all = NSRange(text.startIndex..., in: text)
        func each(_ re: NSRegularExpression, _ keep: (String) -> Bool = { _ in true }) {
            for m in re.matches(in: text, range: all) {
                guard let r = Range(m.range, in: text), keep(String(text[r])) else { continue }
                found.append(r)
            }
        }
        each(email)
        each(phone) { (7...15).contains(digits($0)) }
        each(ssn)
        each(cardLike) { luhn($0) }
        for raw in keywords {
            let k = raw.trimmingCharacters(in: .whitespaces)
            guard k.count >= 2 else { continue }
            var from = text.startIndex
            while let r = text.range(of: k, options: .caseInsensitive, range: from..<text.endIndex) {
                found.append(r)
                from = r.upperBound
            }
        }
        return found
    }

    static func digits(_ s: String) -> Int { s.filter(\.isNumber).count }

    /// The checksum every payment card number passes, so long numbers that aren't cards stay readable.
    static func luhn(_ s: String) -> Bool {
        let d = s.compactMap(\.wholeNumberValue)
        guard (13...19).contains(d.count) else { return false }
        var sum = 0
        for (i, n) in d.reversed().enumerated() {
            if i % 2 == 1 { let x = n * 2; sum += x > 9 ? x - 9 : x } else { sum += n }
        }
        return sum % 10 == 0
    }
}
