import AppKit
import CoreText

/// Plain text, text with a contrasting outline, text on a filled label, or text with a drop shadow, as on Windows.
public enum TextLook: Int, CaseIterable {
    case plain, outline, label, shadow
    public var name: String { ["Plain", "Outline", "Label", "Shadow"][rawValue] }
}

public enum TextAlign: Int, CaseIterable {
    case left, center, right
    var paragraph: NSTextAlignment { [.left, .center, .right][rawValue] }
}

/// How a text annotation looks: its font, weight, slant, alignment and look.
public struct TextStyle: Equatable {
    /// A PostScript font name, or nil for the system font.
    public var font: String?
    public var bold = false
    public var italic = false
    public var align = TextAlign.left
    public var look = TextLook.plain

    public init(font: String? = nil, bold: Bool = false, italic: Bool = false, align: TextAlign = .left, look: TextLook = .plain) {
        self.font = font
        self.bold = bold
        self.italic = italic
        self.align = align
        self.look = look
    }

    /// The fonts the text bar offers: the system font, then the ones bundled with LiteShot (the same as Windows).
    public static let fonts: [(name: String, postScript: String?)] = [
        ("System", nil), ("Poppins SemiBold", "Poppins-SemiBold"), ("Bebas Neue", "BebasNeue-Regular"), ("Pacifico", "Pacifico-Regular"),
        ("Permanent Marker", "PermanentMarker-Regular"), ("Patrick Hand", "PatrickHand-Regular"), ("Anton", "Anton-Regular"),
        ("Archivo Black", "ArchivoBlack-Regular"),
    ]

    public static let sizes: [CGFloat] = [12, 14, 16, 18, 22, 28, 34, 42, 54, 72, 96]

    public var fontName: String { TextStyle.fonts.first { $0.postScript == font }?.name ?? "System" }

    /// The font at `size`, with real bold and italic faces where the font has them (see `fauxBold` / `fauxItalic`).
    public func nsFont(size: CGFloat) -> NSFont {
        var base = font.flatMap { NSFont(name: $0, size: size) } ?? .systemFont(ofSize: size, weight: bold ? .bold : .semibold)
        if bold, font != nil { base = NSFontManager.shared.convert(base, toHaveTrait: .boldFontMask) }
        if italic { base = NSFontManager.shared.convert(base, toHaveTrait: .italicFontMask) }
        return base
    }

    /// Fonts with no bold or italic face get a thicker stroke or a slant instead, like on Windows.
    func fauxBold(_ f: NSFont) -> Bool { bold && !NSFontManager.shared.traits(of: f).contains(.boldFontMask) }
    func fauxItalic(_ f: NSFont) -> Bool { italic && !NSFontManager.shared.traits(of: f).contains(.italicFontMask) }

    func attributes(size: CGFloat, color: NSColor) -> [NSAttributedString.Key: Any] {
        let f = nsFont(size: size)
        let paragraph = NSMutableParagraphStyle()
        paragraph.alignment = align.paragraph
        var a: [NSAttributedString.Key: Any] = [.font: f, .foregroundColor: color, .paragraphStyle: paragraph]
        if fauxBold(f) { a[.strokeWidth] = -4.5; a[.strokeColor] = color }
        if fauxItalic(f) { a[.obliqueness] = 0.2 }
        return a
    }

    /// Registers the fonts bundled in the app's Resources/Fonts, so `NSFont(name:)` finds them. Safe to call twice.
    public static func registerBundledFonts(in folder: URL? = Bundle.main.resourceURL?.appendingPathComponent("Fonts")) {
        guard let folder, let files = try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil) else { return }
        for file in files where ["ttf", "otf"].contains(file.pathExtension.lowercased()) {
            CTFontManagerRegisterFontsForURL(file as CFURL, .process, nil)
        }
    }
}

extension Annotation {
    /// The text block itself, without the padding a label or outline adds.
    public func textRect(_ s: String, at: CGPoint, size: CGFloat) -> CGRect {
        let shown = s.isEmpty ? " " : s
        let box = (shown as NSString).boundingRect(with: CGSize(width: 10_000, height: 10_000), options: [.usesLineFragmentOrigin],
                                                   attributes: style.attributes(size: size, color: color))
        return CGRect(origin: at, size: CGSize(width: ceil(box.width) + (style.italic ? size * 0.2 : 0), height: ceil(box.height)))
    }

    func textBounds(_ s: String, at: CGPoint, size: CGFloat) -> CGRect {
        let r = textRect(s, at: at, size: size)
        switch style.look {
        case .label: return r.insetBy(dx: -size * 0.32, dy: -size * 0.12)
        case .outline: return r.insetBy(dx: -size * 0.12, dy: -size * 0.12)
        case .shadow: return r.insetBy(dx: -size * 0.1, dy: -size * 0.1)
        case .plain: return r
        }
    }

    /// Dark ink on light colors, white on dark ones: the label's text and the outline's halo.
    static func contrast(_ c: NSColor) -> NSColor {
        let rgb = c.usingColorSpace(.sRGB) ?? c
        let lum = 0.2126 * rgb.redComponent + 0.7152 * rgb.greenComponent + 0.0722 * rgb.blueComponent
        return lum > 0.6 ? NSColor(srgbRed: 0.11, green: 0.11, blue: 0.12, alpha: 1) : .white
    }

    func drawText(_ s: String, at: CGPoint, size: CGFloat) {
        guard !s.isEmpty else { return }
        let rect = textRect(s, at: at, size: size)
        var ink = color
        switch style.look {
        case .label:
            color.setFill()
            let label = rect.insetBy(dx: -size * 0.32, dy: -size * 0.12)
            NSBezierPath(roundedRect: label, xRadius: size * 0.3, yRadius: size * 0.3).fill()
            ink = Annotation.contrast(color)
        case .outline:
            var halo = style.attributes(size: size, color: Annotation.contrast(color))
            halo[.strokeColor] = Annotation.contrast(color)
            halo[.strokeWidth] = 16
            (s as NSString).draw(with: rect, options: [.usesLineFragmentOrigin], attributes: halo)
        case .shadow:
            let shade = style.attributes(size: size, color: NSColor(white: 0, alpha: 0.55))
            (s as NSString).draw(with: rect.offsetBy(dx: size * 0.05, dy: size * 0.07), options: [.usesLineFragmentOrigin], attributes: shade)
        case .plain:
            break
        }
        (s as NSString).draw(with: rect, options: [.usesLineFragmentOrigin], attributes: style.attributes(size: size, color: ink))
    }
}
