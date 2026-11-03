import AppKit

struct ToolButton {
    let tip: String
    /// The short name shown when button names are on.
    let label: String
    let key: String?
    /// SF Symbol names, first one available wins.
    let symbols: [String]
    var isActive: () -> Bool = { false }
    var isEnabled: () -> Bool = { true }
    /// Draws the button's face instead of a symbol (the color button).
    var paint: ((CGRect) -> Void)?
    let action: () -> Void
}

/// The colors of one toolbar look. Glass is a dark smoky panel; Bright mode is solid black with white
/// glyphs and yellow for whatever is turned on, for anyone who finds the glass hard to read.
struct ToolbarLook {
    let bright: Bool
    var panel: NSColor { bright ? NSColor(srgbRed: 0.055, green: 0.055, blue: 0.063, alpha: 1) : NSColor(white: 0.12, alpha: 0.86) }
    var border: NSColor { bright ? .white : NSColor(white: 1, alpha: 0.16) }
    var borderWidth: CGFloat { bright ? 2 : 1 }
    var glyph: NSColor { .white }
    var disabled: NSColor { bright ? NSColor(white: 0.5, alpha: 1) : NSColor(white: 1, alpha: 0.32) }
    var hover: NSColor { NSColor(white: 1, alpha: bright ? 0.3 : 0.14) }
    var on: NSColor { bright ? NSColor(srgbRed: 1, green: 0.84, blue: 0.04, alpha: 1) : .controlAccentColor }
    var onGlyph: NSColor { bright ? .black : .white }
    var iconSize: CGFloat { bright ? 17 : 15 }

    static var current: ToolbarLook { ToolbarLook(bright: Settings.brightMode) }
}

/// A row or column of capsule-shaped groups of buttons, laid out beside the selection.
final class Toolbar {
    static let button: CGFloat = 32, padding: CGFloat = 4, spacing: CGFloat = 2, gap: CGFloat = 8

    let vertical: Bool
    var groups: [[ToolButton]]
    /// When set, every button shows its name: beside the icon in a column, under it in a row.
    var labelFont: NSFont?
    private(set) var frame: CGRect = .zero
    private(set) var groupFrames: [CGRect] = []
    private(set) var buttonFrames: [[CGRect]] = []

    init(vertical: Bool, groups: [[ToolButton]]) {
        self.vertical = vertical
        self.groups = groups
    }

    private func labelWidth(_ b: ToolButton) -> CGFloat {
        guard let font = labelFont else { return 0 }
        return ceil((b.label as NSString).size(withAttributes: [.font: font]).width)
    }

    /// Each button's size. Names make a column wider and a row taller.
    private func cells() -> [[CGSize]] {
        let b = Toolbar.button
        guard let font = labelFont else { return groups.map { $0.map { _ in CGSize(width: b, height: b) } } }
        if vertical {
            let widest = groups.flatMap { $0 }.map(labelWidth).max() ?? 0
            return groups.map { $0.map { _ in CGSize(width: b + widest + 12, height: b) } }
        }
        let tall = ceil(b + font.ascender - font.descender + 2)
        return groups.map { $0.map { CGSize(width: max(b, labelWidth($0) + 16), height: tall) } }
    }

    var size: CGSize {
        let c = cells()
        var length = CGFloat(max(0, groups.count - 1)) * Toolbar.gap, thickness: CGFloat = 0
        for g in c {
            length += Toolbar.padding * 2 + CGFloat(max(0, g.count - 1)) * Toolbar.spacing
            for s in g {
                length += vertical ? s.height : s.width
                thickness = max(thickness, vertical ? s.width : s.height)
            }
        }
        thickness += Toolbar.padding * 2
        return vertical ? CGSize(width: thickness, height: length) : CGSize(width: length, height: thickness)
    }

    func layout(at origin: CGPoint) {
        let c = cells()
        frame = CGRect(origin: origin, size: size)
        let thickness = vertical ? frame.width : frame.height
        var pos: CGFloat = 0
        groupFrames = []
        buttonFrames = []
        for g in c {
            var along = Toolbar.padding
            var frames: [CGRect] = []
            for s in g {
                frames.append(vertical
                    ? CGRect(x: origin.x + Toolbar.padding, y: origin.y + pos + along, width: s.width, height: s.height)
                    : CGRect(x: origin.x + pos + along, y: origin.y + Toolbar.padding, width: s.width, height: s.height))
                along += (vertical ? s.height : s.width) + Toolbar.spacing
            }
            let length = along - Toolbar.spacing + Toolbar.padding
            groupFrames.append(vertical
                ? CGRect(x: origin.x, y: origin.y + pos, width: thickness, height: length)
                : CGRect(x: origin.x + pos, y: origin.y, width: length, height: thickness))
            buttonFrames.append(frames)
            pos += length + Toolbar.gap
        }
    }

    func hit(_ p: CGPoint) -> (group: Int, index: Int)? {
        guard frame.contains(p) else { return nil }
        for (g, frames) in buttonFrames.enumerated() {
            for (i, f) in frames.enumerated() where f.insetBy(dx: -1, dy: -1).contains(p) { return (g, i) }
        }
        return nil
    }

    /// Corners concentric with the button pills; a named column is a rounded rectangle, not a long capsule.
    private static func corner(_ r: CGRect) -> CGFloat { min(min(r.width, r.height), button) / 2 }

    func draw(look: ToolbarLook, hover: (group: Int, index: Int)?, pressed: (group: Int, index: Int)?) {
        for (g, group) in groups.enumerated() {
            let gf = groupFrames[g]
            let radius = min(min(gf.width, gf.height) / 2, Toolbar.button / 2 + Toolbar.padding)
            let shadow = NSShadow()
            shadow.shadowColor = NSColor(white: 0, alpha: 0.35)
            shadow.shadowBlurRadius = 14
            shadow.shadowOffset = CGSize(width: 0, height: -4)
            NSGraphicsContext.saveGraphicsState()
            shadow.set()
            look.panel.setFill()
            NSBezierPath(roundedRect: gf, xRadius: radius, yRadius: radius).fill()
            NSGraphicsContext.restoreGraphicsState()
            let edge = NSBezierPath(roundedRect: gf.insetBy(dx: look.borderWidth / 2, dy: look.borderWidth / 2),
                                    xRadius: radius - look.borderWidth / 2, yRadius: radius - look.borderWidth / 2)
            edge.lineWidth = look.borderWidth
            look.border.setStroke()
            edge.stroke()
            for (i, b) in group.enumerated() {
                let isHover = hover.map { $0.group == g && $0.index == i } ?? false
                let isPressed = pressed.map { $0.group == g && $0.index == i } ?? false
                drawButton(b, in: buttonFrames[g][i], look: look, hover: isHover, pressed: isPressed)
            }
        }
    }

    private func drawButton(_ b: ToolButton, in r: CGRect, look: ToolbarLook, hover: Bool, pressed: Bool) {
        let enabled = b.isEnabled(), active = b.isActive()
        if active || (enabled && (hover || pressed)) {
            let pill = r.insetBy(dx: pressed ? 1.5 : 0, dy: pressed ? 1.5 : 0)
            (active ? look.on : look.hover).setFill()
            NSBezierPath(roundedRect: pill, xRadius: Toolbar.corner(pill), yRadius: Toolbar.corner(pill)).fill()
        }
        let color = !enabled ? look.disabled : active ? look.onGlyph : look.glyph
        let side = Toolbar.button
        // With names the icon keeps its square: at the left of a column's row, at the top of a row's button.
        let square = labelFont == nil ? r
            : vertical ? CGRect(x: r.minX, y: r.minY, width: side, height: r.height)
            : CGRect(x: r.minX, y: r.minY + 1, width: r.width, height: side)
        if let paint = b.paint {
            paint(CGRect(x: square.midX - 9, y: square.midY - 9, width: 18, height: 18))
        } else if let image = Toolbar.symbol(b.symbols, size: look.iconSize, weight: look.bright ? .semibold : .regular, color: color) {
            let s = image.size
            image.draw(in: CGRect(x: square.midX - s.width / 2, y: square.midY - s.height / 2, width: s.width, height: s.height))
        }
        guard let font = labelFont else { return }
        let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color]
        let size = (b.label as NSString).size(withAttributes: attrs)
        let at = vertical
            ? CGPoint(x: r.minX + side, y: r.midY - size.height / 2)
            : CGPoint(x: r.midX - size.width / 2, y: r.maxY - size.height - 3)
        (b.label as NSString).draw(at: at, withAttributes: attrs)
    }

    static func symbol(_ names: [String], size: CGFloat, weight: NSFont.Weight, color: NSColor) -> NSImage? {
        for name in names {
            guard let base = NSImage(systemSymbolName: name, accessibilityDescription: nil) else { continue }
            let config = NSImage.SymbolConfiguration(pointSize: size, weight: weight)
                .applying(NSImage.SymbolConfiguration(paletteColors: [color]))
            return base.withSymbolConfiguration(config)
        }
        return nil
    }
}
