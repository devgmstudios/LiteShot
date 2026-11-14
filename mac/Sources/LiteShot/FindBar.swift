import AppKit
import LiteShotCore
import Vision

/// Find on screen (⌘F): Apple's on-device text recognition reads the selection once, then every place the typed text
/// appears lights up as you type, with the rest of the selection stepping back; if it isn't there, related words light
/// up in blue instead ("salmon" for "fish"). Return steps through the matches;
/// Highlight marks them all with the highlighter so they stay in the screenshot.
final class FindBar: NSObject, NSTextFieldDelegate {
    static let yellow = NSColor(srgbRed: 1, green: 0.84, blue: 0.04, alpha: 1)
    static let orange = NSColor(srgbRed: 1, green: 0.58, blue: 0, alpha: 1)

    unowned let view: OverlayView
    let field = NSTextField()
    private(set) var isOpen = false
    private(set) var frame: CGRect = .zero
    private var fieldFrame: CGRect = .zero
    private var buttons: [CGRect] = [.zero, .zero, .zero]   // previous, next, highlight
    var hoverButton: Int?

    private var observations: [VNRecognizedTextObservation] = []
    private var scanArea: CGRect = .null
    private var scanning = false
    private var failed = false
    private(set) var hits: [CGRect] = []
    /// No exact match, so `hits` are related words ("salmon" for "fish"), drawn in blue.
    private(set) var related = false
    private(set) var index = 0
    static let blue = NSColor(srgbRed: 0.04, green: 0.52, blue: 1, alpha: 1)

    init(view: OverlayView) {
        self.view = view
        super.init()
        field.isBordered = false
        field.drawsBackground = false
        field.focusRingType = .none
        field.isHidden = true
        field.delegate = self
        field.cell?.isScrollable = true
        field.cell?.wraps = false
    }

    var isEditing: Bool { isOpen && field.currentEditor() != nil }

    func open() {
        guard let sel = view.selection else { return }
        if !isOpen {
            isOpen = true
            index = 0
            if field.superview == nil { view.addSubview(field) }
            field.isHidden = false
            field.placeholderAttributedString = NSAttributedString(string: "Find on screen", attributes: [
                .foregroundColor: NSColor(white: 1, alpha: 0.45), .font: font,
            ])
            field.font = font
            field.textColor = .white
            if !scanArea.contains(sel) { scan() }
            layout()
        }
        view.window?.makeFirstResponder(field)
        field.currentEditor()?.selectAll(nil)
        view.needsDisplay = true
    }

    func close() {
        guard isOpen else { return }
        isOpen = false
        field.isHidden = true
        hits = []
        hoverButton = nil
        view.window?.makeFirstResponder(view)
        view.needsDisplay = true
    }

    private var font: NSFont { .systemFont(ofSize: Settings.brightMode ? 15 : 14) }

    /// Reads the selection in the background; a newer read replaces an older one.
    private func scan() {
        guard let sel = view.selection, let image = view.renderResult(annotated: false) else { return }
        scanArea = sel
        scanning = true
        failed = false
        observations = []
        Task {
            let found = try? await VisionTools.observations(in: image)
            await MainActor.run {
                guard self.scanArea == sel else { return }
                self.scanning = false
                self.failed = found == nil
                self.observations = found ?? []
                self.update()
            }
        }
        update()
    }

    /// For the self-test: read and match on the calling thread.
    func scanNow(_ query: String) {
        guard let sel = view.selection, let image = view.renderResult(annotated: false) else { return }
        open()
        scanArea = sel
        scanning = false
        observations = (try? VisionTools.observationsNow(in: image)) ?? []
        field.stringValue = query
        index = 0
        update()
    }

    /// Matches the typed text against what was read, keeping the matches inside the selection.
    func update() {
        guard isOpen, let sel = view.selection else { return }
        if !scanArea.contains(sel), !scanning { scan(); return }
        let query = field.stringValue
        func boxes(_ ranges: (String) -> [Range<String.Index>]) -> [CGRect] {
            var found: [CGRect] = []
            for line in observations {
                guard let candidate = line.topCandidates(1).first else { continue }
                for range in ranges(candidate.string) {
                    guard let box = try? candidate.boundingBox(for: range) else { continue }
                    let r = Geometry.fromNormalized(box.boundingBox, in: scanArea)
                    if sel.contains(CGPoint(x: r.midX, y: r.midY)) { found.append(r) }
                }
            }
            return found
        }
        var found = boxes { TextFinder.ranges(of: query, in: $0) }
        related = false
        // Not on screen as typed: light up related words instead, "salmon" and "trout" for "fish".
        if found.isEmpty, let words = RelatedWords.shared {
            found = Array(boxes { TextFinder.relatedRanges(of: query, in: $0, words: words) }.prefix(150))
            related = !found.isEmpty
        }
        hits = TextFinder.readingOrder(found)
        index = hits.isEmpty ? 0 : min(index, hits.count - 1)
        view.needsDisplay = true
    }

    var status: String {
        if scanning { return "Reading…" }
        if failed { return "Couldn't read" }
        if TextFinder.normalized(field.stringValue) == nil { return "" }
        if hits.isEmpty { return "No matches" }
        return related ? "\(index + 1) of \(hits.count) related" : "\(index + 1) of \(hits.count)"
    }

    func step(_ delta: Int) {
        guard !hits.isEmpty else { return }
        index = ((index + delta) % hits.count + hits.count) % hits.count
        view.needsDisplay = true
    }

    func select(_ i: Int) {
        index = i
        view.needsDisplay = true
    }

    func hitIndex(at p: CGPoint) -> Int? {
        hits.firstIndex { $0.insetBy(dx: -3, dy: -2).contains(p) }
    }

    func button(at p: CGPoint) -> Int? {
        hits.isEmpty ? nil : buttons.firstIndex { $0.contains(p) }
    }

    func click(_ p: CGPoint) {
        switch button(at: p) {
        case 0: step(-1)
        case 1: step(1)
        case 2: highlight()
        default: break
        }
        if isOpen { view.window?.makeFirstResponder(field) }
    }

    /// Highlight: a highlighter stroke over every match, as one undo step, and the bar closes.
    func highlight() {
        guard !hits.isEmpty else { return }
        let count = hits.count
        view.addMarks(hits)
        close()
        view.showFlash(count == 1 ? "Highlighted 1 match" : "Highlighted \(count) matches")
    }

    // MARK: - text field

    func controlTextDidChange(_ obj: Notification) {
        index = 0
        update()
    }

    func control(_ control: NSControl, textView: NSTextView, doCommandBy selector: Selector) -> Bool {
        let flags = NSApp.currentEvent?.modifierFlags ?? []
        switch selector {
        case #selector(NSResponder.insertNewline(_:)):
            if flags.contains(.command) { highlight() } else { step(flags.contains(.shift) ? -1 : 1) }
        case #selector(NSResponder.moveDown(_:)): step(1)
        case #selector(NSResponder.moveUp(_:)): step(-1)
        case #selector(NSResponder.cancelOperation(_:)): close()
        default: return false
        }
        return true
    }

    // MARK: - layout and drawing

    /// Below the actions bar (or above it, if there's no room), centred on the Find button.
    func layout() {
        guard isOpen else { return }
        let bounds = view.bounds, bar = view.actions.frame, anchor = view.findButtonFrame
        let look = ToolbarLook.current
        let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: look.bright ? 13 : 12, weight: .medium)]
        let statusW = ("88 of 88 related" as NSString).size(withAttributes: attrs).width + 16
        let markW = ("Highlight" as NSString).size(withAttributes: attrs).width + 48
        let h: CGFloat = 46, pad: CGFloat = 7, btn: CGFloat = 32, iconArea: CGFloat = 34
        let fieldW: CGFloat = 190 + statusW
        let w = iconArea + fieldW + 8 + btn * 2 + 4 + markW + pad + 4
        let below = bar.minY >= (view.selection?.maxY ?? 0) - 1
        var x = anchor.midX - w / 2
        var y = below ? bar.maxY + 10 : bar.minY - 10 - h
        if y + h > bounds.maxY - 4 { y = bar.minY - 10 - h }
        if y < 4 { y = bar.maxY + 10 }
        x = min(max(x, 4), max(4, bounds.maxX - w - 4))
        frame = CGRect(x: x.rounded(), y: y.rounded(), width: ceil(w), height: h)
        fieldFrame = CGRect(x: frame.minX + iconArea, y: frame.minY + pad, width: fieldW, height: h - pad * 2)
        var cx = fieldFrame.maxX + 8
        let by = frame.minY + (h - btn) / 2
        buttons[0] = CGRect(x: cx, y: by, width: btn, height: btn); cx += btn + 2
        buttons[1] = CGRect(x: cx, y: by, width: btn, height: btn); cx += btn + 2
        buttons[2] = CGRect(x: cx, y: by, width: markW, height: btn)
        let lineHeight = ceil(font.ascender - font.descender + font.leading) + 2
        let fieldRect = CGRect(x: fieldFrame.minX + 14, y: fieldFrame.midY - lineHeight / 2, width: fieldFrame.width - 14 - statusW, height: lineHeight)
        if field.frame != fieldRect { field.frame = fieldRect }
        field.font = font
    }

    /// The matches, over the capture: the rest of the selection steps back, the current match is ringed in orange.
    func drawHits(in sel: CGRect) {
        guard isOpen, !hits.isEmpty else { return }
        let bright = Settings.brightMode
        let dim = NSBezierPath(rect: sel)
        for h in hits { dim.append(NSBezierPath(roundedRect: h.insetBy(dx: -3, dy: -2), xRadius: 4, yRadius: 4)) }
        dim.windingRule = .evenOdd
        NSColor(white: 0, alpha: bright ? 0.5 : 0.33).setFill()
        dim.fill()
        // Related words are blue, so they don't pass for the word that was typed.
        let tint = related ? FindBar.blue : FindBar.yellow
        for (i, h) in hits.enumerated() {
            let current = i == index
            let p = NSBezierPath(roundedRect: h.insetBy(dx: -3, dy: -2), xRadius: 4, yRadius: 4)
            tint.withAlphaComponent(current ? 0.38 : 0.22).setFill()
            p.fill()
            p.lineWidth = (current ? 3 : 1.6) * (bright ? 1.4 : 1)
            (current ? FindBar.orange : tint).setStroke()
            p.stroke()
        }
    }

    func draw(look: ToolbarLook) {
        guard isOpen else { return }
        let r = frame.height / 2
        let shadow = NSShadow()
        shadow.shadowColor = NSColor(white: 0, alpha: 0.35)
        shadow.shadowBlurRadius = 14
        shadow.shadowOffset = CGSize(width: 0, height: -4)
        NSGraphicsContext.saveGraphicsState()
        shadow.set()
        look.panel.setFill()
        NSBezierPath(roundedRect: frame, xRadius: r, yRadius: r).fill()
        NSGraphicsContext.restoreGraphicsState()
        let edge = NSBezierPath(roundedRect: frame.insetBy(dx: look.borderWidth / 2, dy: look.borderWidth / 2), xRadius: r, yRadius: r)
        edge.lineWidth = look.borderWidth
        look.border.setStroke()
        edge.stroke()

        if let icon = Toolbar.symbol(["text.magnifyingglass", "magnifyingglass"], size: look.iconSize, weight: look.bright ? .semibold : .regular, color: look.glyph) {
            icon.draw(in: CGRect(x: frame.minX + 17 - icon.size.width / 2, y: frame.midY - icon.size.height / 2, width: icon.size.width, height: icon.size.height))
        }
        let fr = fieldFrame.height / 2
        NSColor(white: 1, alpha: look.bright ? 0.14 : 0.1).setFill()
        let fieldPath = NSBezierPath(roundedRect: fieldFrame, xRadius: fr, yRadius: fr)
        fieldPath.fill()
        if isEditing {
            fieldPath.lineWidth = 2
            (look.bright ? look.on : NSColor.controlAccentColor).setStroke()
            fieldPath.stroke()
        }
        let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: look.bright ? 13 : 12, weight: .medium)]
        let text = status
        if !text.isEmpty {
            let bad = text == "No matches" || text == "Couldn't read"
            var a = attrs
            a[.foregroundColor] = bad ? NSColor(srgbRed: 1, green: 0.47, blue: 0.43, alpha: 1)
                : related ? NSColor(srgbRed: 0.4, green: 0.71, blue: 1, alpha: 1)
                : NSColor(white: 1, alpha: look.bright ? 0.85 : 0.6)
            let s = (text as NSString).size(withAttributes: a)
            (text as NSString).draw(at: CGPoint(x: fieldFrame.maxX - 14 - s.width, y: fieldFrame.midY - s.height / 2), withAttributes: a)
        }

        let any = !hits.isEmpty
        let ink = any ? look.glyph : look.disabled
        for (i, b) in buttons.enumerated() {
            if any, hoverButton == i {
                look.hover.setFill()
                NSBezierPath(roundedRect: b, xRadius: b.height / 2, yRadius: b.height / 2).fill()
            }
            if i < 2 {
                if let chevron = Toolbar.symbol([i == 0 ? "chevron.up" : "chevron.down"], size: look.iconSize - 1, weight: .semibold, color: ink) {
                    chevron.draw(in: CGRect(x: b.midX - chevron.size.width / 2, y: b.midY - chevron.size.height / 2, width: chevron.size.width, height: chevron.size.height))
                }
                continue
            }
            let pill = NSBezierPath(roundedRect: b.insetBy(dx: 0.5, dy: 0.5), xRadius: b.height / 2, yRadius: b.height / 2)
            pill.lineWidth = look.bright ? 2 : 1
            ink.withAlphaComponent(look.bright ? 1 : 0.4).setStroke()
            pill.stroke()
            var a = attrs
            a[.foregroundColor] = ink
            let label = "Highlight" as NSString
            let s = label.size(withAttributes: a)
            let marker = Toolbar.symbol(["highlighter"], size: look.iconSize - 2, weight: .regular, color: ink)
            let mw = marker?.size.width ?? 0
            let left = b.midX - (mw + 6 + s.width) / 2
            if let marker {
                marker.draw(in: CGRect(x: left, y: b.midY - marker.size.height / 2, width: marker.size.width, height: marker.size.height))
            }
            label.draw(at: CGPoint(x: left + mw + 6, y: b.midY - s.height / 2), withAttributes: a)
        }
    }
}
