import AppKit
import LiteShotCore

/// One screen's frozen image: hover a window or drag an area, then mark it up with the toolbars beside it.
/// Coordinates are view points with the origin at the top left; the image fills the view.
final class OverlayView: NSView {
    weak var session: CaptureSession?
    weak var host: NSWindow?

    let image: CGImage
    private let screenImage: NSImage
    private let windows: [CGRect]

    private(set) var selection: CGRect?
    var hoverWindow: CGRect?
    private var mouse: CGPoint = .zero

    private enum Drag {
        case none
        case select(from: CGPoint)
        case move(from: CGPoint, start: CGRect)
        case resize(handle: Int)
        case draw(from: CGPoint)
        case button(Toolbar, group: Int, index: Int)
        case palette
        case moveShape(from: CGPoint, index: Int, start: Annotation)
    }
    private var drag: Drag = .none
    private var dragMoved = false

    var tool: Tool?
    var color = Palette.colors[0]
    var lineWidth: CGFloat = 3
    private(set) var annotations: [Annotation] = []
    /// What Hide personal info and Blur faces found; drawn under the annotations.
    private(set) var redactions: [Annotation] = []
    private var undoStack: [[Annotation]] = []
    private var redoStack: [[Annotation]] = []
    private var drawing: Annotation?
    /// The drawing picked with no tool active: drag to move it, Delete to remove it, the palette recolors it.
    private(set) var selectedShape: Int?
    private var editing: Int?
    private var counter = 1
    private var hidingPersonal = false
    private var busy: String?
    private var flash: String?
    private var flashWork: DispatchWorkItem?
    private var paletteOpen = false

    private(set) lazy var tools = buildTools()
    private(set) lazy var actions = buildActions()
    private(set) lazy var findBar = FindBar(view: self)
    private var hover: (bar: Toolbar, group: Int, index: Int)?

    init(frame: CGRect, image: CGImage, windows: [CGRect]) {
        self.image = image
        self.windows = windows
        screenImage = NSImage(cgImage: image, size: frame.size)
        super.init(frame: frame)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseMoved, .activeAlways, .inVisibleRect], owner: self))
    }

    // MARK: - toolbars

    private func buildTools() -> Toolbar {
        func t(_ tool: Tool, _ tip: String, _ label: String, _ key: String, _ symbols: [String]) -> ToolButton {
            ToolButton(tip: tip, label: label, key: key, symbols: symbols,
                       isActive: { [unowned self] in self.tool == tool },
                       action: { [unowned self] in self.setTool(tool) })
        }
        return Toolbar(vertical: true, groups: [
            [t(.pen, "Pen", "Pen", "P", ["pencil"]),
             t(.line, "Line", "Line", "L", ["line.diagonal"]),
             t(.arrow, "Arrow", "Arrow", "A", ["arrow.up.right"]),
             t(.rect, "Rectangle", "Rectangle", "R", ["rectangle"]),
             t(.ellipse, "Ellipse", "Ellipse", "E", ["circle"]),
             t(.marker, "Marker", "Marker", "M", ["highlighter"]),
             t(.text, "Text", "Text", "T", ["textformat"]),
             t(.counter, "Step number", "Number", "N", ["1.circle"])],
            [ToolButton(tip: "Hide personal info", label: "Hide info", key: "H", symbols: ["eye.slash"],
                        isActive: { [unowned self] in self.hidingPersonal },
                        action: { [unowned self] in self.toggleHidePersonal() }),
             t(.pixelate, "Pixelate", "Pixelate", "B", ["checkerboard.rectangle"]),
             ToolButton(tip: "Blur faces", label: "Blur faces", key: "F", symbols: ["face.dashed", "person.crop.circle.badge.xmark"],
                        action: { [unowned self] in self.blurFaces() })],
            [ToolButton(tip: "Color and size", label: "Color", key: nil, symbols: [],
                        isActive: { [unowned self] in self.paletteOpen },
                        paint: { [unowned self] r in self.paintColorButton(r) },
                        action: { [unowned self] in self.paletteOpen.toggle(); self.needsDisplay = true })],
            [ToolButton(tip: "Undo", label: "Undo", key: "⌘Z", symbols: ["arrow.uturn.backward"],
                        isEnabled: { [unowned self] in !self.undoStack.isEmpty },
                        action: { [unowned self] in self.undo() }),
             ToolButton(tip: "Redo", label: "Redo", key: "⇧⌘Z", symbols: ["arrow.uturn.forward"],
                        isEnabled: { [unowned self] in !self.redoStack.isEmpty },
                        action: { [unowned self] in self.redo() })],
        ])
    }

    private func buildActions() -> Toolbar {
        func b(_ tip: String, _ label: String, _ key: String?, _ symbols: [String], _ action: @escaping () -> Void) -> ToolButton {
            ToolButton(tip: tip, label: label, key: key, symbols: symbols, action: action)
        }
        return Toolbar(vertical: false, groups: [
            [b("Scrolling capture", "Scroll", "S", ["arrow.down.doc", "scroll"]) { [unowned self] in self.startScrollCapture() }],
            [b("Pin to screen", "Pin", "⌘T", ["pin"]) { [unowned self] in self.pin() },
             b("Copy text", "Copy text", "⇧⌘C", ["text.viewfinder"]) { [unowned self] in self.copyText() },
             ToolButton(tip: "Find on screen", label: "Find", key: "⌘F", symbols: ["text.magnifyingglass", "magnifyingglass"],
                        isActive: { [unowned self] in self.findBar.isOpen },
                        action: { [unowned self] in self.toggleFind() })],
            [b("Upload and copy link", "Upload", "⌘D", ["icloud.and.arrow.up"]) { [unowned self] in self.upload(.link) },
             b("Share", "Share", nil, ["square.and.arrow.up"]) { [unowned self] in self.upload(.share) },
             b("Search similar images", "Search", "⌘K", ["magnifyingglass"]) { [unowned self] in self.upload(.search) },
             b("Print", "Print", "⌘P", ["printer"]) { [unowned self] in self.printImage() }],
            [b("Ask AI about it", "Ask AI", "⇧⌘A", ["bubble.left.and.text.bubble.right", "bubble.left"]) { [unowned self] in self.askAI() },
             b("Edit with AI", "AI edit", nil, ["sparkles"]) { [unowned self] in self.runAI(.edit) },
             b("Make a video", "Video", nil, ["video"]) { [unowned self] in self.runAI(.video) }],
            [b("Copy", "Copy", "⌘C", ["doc.on.doc"]) { [unowned self] in self.copyToClipboard() },
             b("Save", "Save", "⌘S", ["square.and.arrow.down"]) { [unowned self] in self.save() }],
            [b("Close", "Close", "Esc", ["xmark"]) { [unowned self] in self.close() }],
        ])
    }

    private var bars: [Toolbar] { selection == nil ? [] : textBarVisible ? [tools, actions, textBar] : [tools, actions] }

    /// Where the Find button is, for anchoring the find bar under it.
    var findButtonFrame: CGRect { actions.buttonFrames.count > 1 && actions.buttonFrames[1].count > 2 ? actions.buttonFrames[1][2] : actions.frame }

    /// Lightshot's layout: tools to the right of the selection (bottom-aligned), actions below it (right-aligned).
    func layoutBars() {
        guard let sel = selection else { return }
        let names = Settings.buttonNames
        let font = NSFont.systemFont(ofSize: Settings.brightMode ? 12 : 11, weight: .semibold)
        tools.labelFont = names ? font : nil
        actions.labelFont = names ? font : nil
        let gap: CGFloat = 10, area = bounds.insetBy(dx: 4, dy: 4)
        let vs = tools.size, hs = actions.size

        var vx = sel.maxX + gap, vy = sel.maxY - vs.height
        if vx + vs.width > area.maxX { vx = sel.minX - gap - vs.width }
        if vx < area.minX { vx = sel.maxX - gap - vs.width }
        vy = min(max(vy, area.minY), max(area.minY, area.maxY - vs.height))

        var hx = sel.maxX - hs.width, hy = sel.maxY + gap
        if hy + hs.height > area.maxY { hy = sel.minY - gap - hs.height }
        if hy < area.minY { hy = sel.maxY - gap - hs.height }
        hx = min(max(hx, area.minX), max(area.minX, area.maxX - hs.width))

        let vr = CGRect(x: vx, y: vy, width: vs.width, height: vs.height)
        let hr = CGRect(x: hx, y: hy, width: hs.width, height: hs.height)
        if vr.intersects(hr) { vy = max(area.minY, hr.minY - gap - vs.height) }
        tools.layout(at: CGPoint(x: vx.rounded(), y: vy.rounded()))
        actions.layout(at: CGPoint(x: hx.rounded(), y: hy.rounded()))
        if textBarVisible { layoutTextBar() }
    }

    private func hitBar(_ p: CGPoint) -> (bar: Toolbar, group: Int, index: Int)? {
        for bar in bars { if let h = bar.hit(p) { return (bar, h.group, h.index) } }
        return nil
    }

    // MARK: - drawing

    override func draw(_ dirtyRect: NSRect) {
        guard let gc = NSGraphicsContext.current else { return }
        gc.imageInterpolation = .none
        screenImage.draw(in: bounds, from: .zero, operation: .copy, fraction: 1, respectFlipped: true, hints: nil)
        gc.imageInterpolation = .high

        let dim = NSBezierPath(rect: bounds)
        if let sel = selection {
            dim.append(NSBezierPath(rect: sel))
            dim.windingRule = .evenOdd
        }
        NSColor(white: 0, alpha: 0.45).setFill()
        dim.fill()

        if let sel = selection {
            gc.saveGraphicsState()
            NSBezierPath(rect: sel).addClip()
            let size = bounds.size
            for a in redactions + annotations { a.draw(screen: image, screenSize: size) }
            drawing?.draw(screen: image, screenSize: size)
            if let i = editing, i < annotations.count { drawEditor(annotations[i]) }
            if let i = selectedShape, i < annotations.count, editing == nil {
                let box = NSBezierPath(roundedRect: annotations[i].bounds.insetBy(dx: -5, dy: -5), xRadius: 4, yRadius: 4)
                NSColor(white: 0, alpha: 0.45).setStroke()
                box.lineWidth = 2
                box.stroke()
                box.setLineDash([4, 3], count: 2, phase: 0)
                box.lineWidth = 1
                NSColor.white.setStroke()
                box.stroke()
            }
            findBar.drawHits(in: sel)
            gc.restoreGraphicsState()
            drawFrame(sel)
            drawSizeLabel(sel)
            if !isDraggingSelection {
                layoutBars()
                let look = ToolbarLook.current
                for bar in bars {
                    var h: (group: Int, index: Int)?, p: (group: Int, index: Int)?
                    if let hv = hover, hv.bar === bar { h = (hv.group, hv.index) }
                    if case let .button(b, g, i) = drag, b === bar { p = (g, i) }
                    bar.draw(look: look, hover: h, pressed: p)
                }
                if paletteOpen { drawPalette(look) }
                if fontListOpen, textBarVisible { drawFontList(look) }
                if findBar.isOpen { findBar.layout(); findBar.draw(look: look) }
                if let h = hover, tools.labelFont == nil { drawTip(h.bar.groups[h.group][h.index], h.bar, h.bar.buttonFrames[h.group][h.index], look) }
            }
            if let text = busy ?? flash { drawPill(text, near: sel) }
        } else if let w = hoverWindow {
            let p = NSBezierPath(rect: w.insetBy(dx: 1.5, dy: 1.5))
            p.lineWidth = 3
            NSColor.controlAccentColor.setStroke()
            p.stroke()
        }
        if magnifierVisible { drawMagnifier() }
    }

    private var isDraggingSelection: Bool {
        switch drag {
        case .select, .move, .resize: return dragMoved
        default: return false
        }
    }

    private func drawFrame(_ r: CGRect) {
        NSColor(white: 0, alpha: 0.35).setStroke()
        NSBezierPath(rect: r.insetBy(dx: -1.5, dy: -1.5)).stroke()
        NSColor.white.setStroke()
        NSBezierPath(rect: r.insetBy(dx: -0.5, dy: -0.5)).stroke()
        guard !isDraggingSelection else { return }
        for c in Geometry.handles(of: r) {
            let k = CGRect(x: c.x - 4.5, y: c.y - 4.5, width: 9, height: 9)
            NSColor.white.setFill()
            NSBezierPath(ovalIn: k).fill()
            NSColor(white: 0, alpha: 0.3).setStroke()
            NSBezierPath(ovalIn: k).stroke()
        }
    }

    private func drawSizeLabel(_ r: CGRect) {
        let s = CGFloat(image.width) / bounds.width
        let text = "\(Int((r.width * s).rounded())) × \(Int((r.height * s).rounded()))"
        var at = CGPoint(x: r.minX, y: r.minY - 30)
        if at.y < 4 { at = CGPoint(x: r.minX + 8, y: r.minY + 8) }
        drawPillText(text, at: at)
    }

    private func pillAttributes(_ look: ToolbarLook) -> [NSAttributedString.Key: Any] {
        [.font: NSFont.systemFont(ofSize: look.bright ? 14 : 12, weight: .semibold), .foregroundColor: NSColor.white]
    }

    @discardableResult
    private func drawPillText(_ text: String, at: CGPoint) -> CGRect {
        let look = ToolbarLook.current
        let attrs = pillAttributes(look)
        let size = (text as NSString).size(withAttributes: attrs)
        let r = CGRect(x: at.x, y: at.y, width: ceil(size.width) + 22, height: ceil(size.height) + 10)
        look.panel.setFill()
        NSBezierPath(roundedRect: r, xRadius: r.height / 2, yRadius: r.height / 2).fill()
        look.border.setStroke()
        let edge = NSBezierPath(roundedRect: r.insetBy(dx: 0.5, dy: 0.5), xRadius: r.height / 2, yRadius: r.height / 2)
        edge.lineWidth = look.borderWidth
        edge.stroke()
        (text as NSString).draw(at: CGPoint(x: r.minX + 11, y: r.minY + 5), withAttributes: attrs)
        return r
    }

    private func drawPill(_ text: String, near sel: CGRect) {
        let size = (text as NSString).size(withAttributes: pillAttributes(.current))
        let x = sel.midX - (size.width + 22) / 2
        let y = min(sel.maxY - size.height - 26, bounds.maxY - size.height - 30)
        drawPillText(text, at: CGPoint(x: x, y: max(sel.minY + 8, y)))
    }

    private func drawTip(_ b: ToolButton, _ bar: Toolbar, _ f: CGRect, _ look: ToolbarLook) {
        let text = b.key.map { "\(b.tip)   \($0)" } ?? b.tip
        let size = (text as NSString).size(withAttributes: pillAttributes(look))
        let w = size.width + 22, h = size.height + 10
        var at: CGPoint
        if bar.vertical {
            at = CGPoint(x: bar.frame.maxX + 10, y: f.midY - h / 2)
            if at.x + w > bounds.maxX - 4 { at.x = bar.frame.minX - 10 - w }
        } else {
            at = CGPoint(x: f.midX - w / 2, y: bar.frame.maxY + 10)
            if at.y + h > bounds.maxY - 4 { at.y = bar.frame.minY - 10 - h }
        }
        at.x = min(max(at.x, 4), bounds.maxX - w - 4)
        drawPillText(text, at: at)
    }

    private func drawEditor(_ a: Annotation) {
        let box = a.bounds.insetBy(dx: -6, dy: -4)
        let p = NSBezierPath(rect: box)
        p.setLineDash([4, 3], count: 2, phase: 0)
        NSColor.white.setStroke()
        p.stroke()
        if case let .text(s, at, size) = a.kind {
            let font = a.style.nsFont(size: size)
            let lines = s.components(separatedBy: "\n")
            let last = lines.last ?? ""
            let w = (last as NSString).size(withAttributes: [.font: font]).width
            let lineHeight = ceil(font.ascender - font.descender + font.leading)
            // The caret follows the last line, wherever the alignment puts it.
            let block = a.textRect(s, at: at, size: size).width
            let x = at.x + (a.style.align == .left ? w : a.style.align == .center ? (block + w) / 2 : block)
            a.color.setFill()
            NSBezierPath(rect: CGRect(x: x + 1, y: at.y + CGFloat(lines.count - 1) * lineHeight, width: 2, height: lineHeight)).fill()
        }
    }

    // MARK: - color and size

    private func paintColorButton(_ r: CGRect) {
        color.setFill()
        NSBezierPath(ovalIn: r).fill()
        NSColor.white.setStroke()
        let ring = NSBezierPath(ovalIn: r.insetBy(dx: 1, dy: 1))
        ring.lineWidth = 2
        ring.stroke()
    }

    private var paletteRect: CGRect {
        let cell: CGFloat = 26, pad: CGFloat = 12, cols = 5
        let rows = Int(ceil(Double(Palette.colors.count) / Double(cols))) + 1
        let w = pad * 2 + CGFloat(cols) * cell + CGFloat(cols - 1) * 8
        let h = pad * 2 + CGFloat(rows) * cell + CGFloat(rows - 1) * 8
        let anchor = tools.buttonFrames.count > 2 ? tools.buttonFrames[2][0] : tools.frame
        var x = tools.frame.minX - 10 - w
        if x < 4 { x = tools.frame.maxX + 10 }
        let y = min(max(anchor.midY - h / 2, 4), bounds.maxY - h - 4)
        return CGRect(x: x, y: y, width: w, height: h)
    }

    private func paletteCells() -> [(rect: CGRect, color: NSColor?, width: CGFloat?)] {
        let r = paletteRect, cell: CGFloat = 26, pad: CGFloat = 12, cols = 5
        var out: [(CGRect, NSColor?, CGFloat?)] = []
        for (i, c) in Palette.colors.enumerated() {
            let x = r.minX + pad + CGFloat(i % cols) * (cell + 8), y = r.minY + pad + CGFloat(i / cols) * (cell + 8)
            out.append((CGRect(x: x, y: y, width: cell, height: cell), c, nil))
        }
        let row = CGFloat(Int(ceil(Double(Palette.colors.count) / Double(cols))))
        for (i, w) in Palette.widths.enumerated() {
            let x = r.minX + pad + CGFloat(i) * (cell + 8), y = r.minY + pad + row * (cell + 8)
            out.append((CGRect(x: x, y: y, width: cell, height: cell), nil, w))
        }
        return out
    }

    private func drawPalette(_ look: ToolbarLook) {
        let r = paletteRect
        look.panel.setFill()
        NSBezierPath(roundedRect: r, xRadius: 18, yRadius: 18).fill()
        look.border.setStroke()
        let edge = NSBezierPath(roundedRect: r.insetBy(dx: 0.5, dy: 0.5), xRadius: 18, yRadius: 18)
        edge.lineWidth = look.borderWidth
        edge.stroke()
        for cell in paletteCells() {
            if let c = cell.color {
                c.setFill()
                NSBezierPath(ovalIn: cell.rect.insetBy(dx: 3, dy: 3)).fill()
                if c == color {
                    (look.bright ? look.on : NSColor.white).setStroke()
                    let ring = NSBezierPath(ovalIn: cell.rect.insetBy(dx: 0.5, dy: 0.5))
                    ring.lineWidth = 2
                    ring.stroke()
                }
            } else if let w = cell.width {
                if w == lineWidth {
                    (look.bright ? look.on : look.hover).setFill()
                    NSBezierPath(ovalIn: cell.rect).fill()
                }
                let d = 4 + w * 1.6
                (w == lineWidth && look.bright ? NSColor.black : NSColor.white).setFill()
                NSBezierPath(ovalIn: CGRect(x: cell.rect.midX - d / 2, y: cell.rect.midY - d / 2, width: d, height: d)).fill()
            }
        }
    }

    // MARK: - text bar

    /// The style new text gets, and the size; the text bar changes them and the text being typed or picked.
    var textStyle = TextStyle()
    var textSize: CGFloat = 22
    private var fontListOpen = false

    private(set) lazy var textBar = buildTextBar()

    /// Shown with the Text tool, while typing, or with a text picked.
    var textBarVisible: Bool { selection != nil && (tool == .text || editing != nil || textTarget != nil) }

    /// The text the bar works on: the one being typed, or the one picked.
    private var textTarget: Int? {
        if let i = editing, i < annotations.count { return i }
        if let i = selectedShape, i < annotations.count, case .text = annotations[i].kind { return i }
        return nil
    }

    private var targetStyle: TextStyle { textTarget.map { annotations[$0].style } ?? textStyle }

    private func buildTextBar() -> Toolbar {
        func b(_ tip: String, _ label: String, _ key: String?, _ symbols: [String], active: @escaping () -> Bool = { false },
               paint: ((CGRect) -> Void)? = nil, _ action: @escaping () -> Void) -> ToolButton {
            ToolButton(tip: tip, label: label, key: key, symbols: symbols, isActive: active, paint: paint, action: action)
        }
        return Toolbar(vertical: false, groups: [
            [b("Font", "Font", nil, ["textformat"], active: { [unowned self] in self.fontListOpen }) { [unowned self] in
                self.fontListOpen.toggle(); self.paletteOpen = false; self.needsDisplay = true }],
            [b("Smaller", "Smaller", "⌘[", ["textformat.size.smaller"]) { [unowned self] in self.stepTextSize(-1) },
             b("Larger", "Larger", "⌘]", ["textformat.size.larger"]) { [unowned self] in self.stepTextSize(1) }],
            [b("Bold", "Bold", "⌘B", ["bold"], active: { [unowned self] in self.targetStyle.bold }) { [unowned self] in self.restyle { $0.bold.toggle() } },
             b("Italic", "Italic", "⌘I", ["italic"], active: { [unowned self] in self.targetStyle.italic }) { [unowned self] in self.restyle { $0.italic.toggle() } }],
            [b("Align left", "Left", nil, ["text.alignleft"], active: { [unowned self] in self.targetStyle.align == .left }) { [unowned self] in self.restyle { $0.align = .left } },
             b("Center", "Center", nil, ["text.aligncenter"], active: { [unowned self] in self.targetStyle.align == .center }) { [unowned self] in self.restyle { $0.align = .center } },
             b("Align right", "Right", nil, ["text.alignright"], active: { [unowned self] in self.targetStyle.align == .right }) { [unowned self] in self.restyle { $0.align = .right } }],
            [b("Text look: plain, outline, label, shadow", "Look", nil, [], paint: { [unowned self] r in self.paintLook(r) }) { [unowned self] in
                self.restyle { $0.look = TextLook(rawValue: ($0.look.rawValue + 1) % TextLook.allCases.count) ?? .plain } }],
        ])
    }

    /// Changes the style of new text and of the text being typed or picked (undoable when it's an existing one).
    func restyle(_ change: (inout TextStyle) -> Void) {
        change(&textStyle)
        if let i = textTarget {
            var style = annotations[i].style
            change(&style)
            if editing == i { annotations[i].style = style } else { record { annotations[i].style = style } }
        }
        needsDisplay = true
    }

    func stepTextSize(_ step: Int) {
        let sizes = TextStyle.sizes
        let current: CGFloat = {
            if let i = textTarget, case let .text(_, _, size) = annotations[i].kind { return size }
            return textSize
        }()
        let index = sizes.firstIndex { $0 >= current - 0.5 } ?? sizes.count - 1
        let next = sizes[max(0, min(sizes.count - 1, index + step))]
        textSize = next
        if let i = textTarget, case let .text(s, at, _) = annotations[i].kind {
            if editing == i { annotations[i].kind = .text(s, at: at, size: next) } else { record { annotations[i].kind = .text(s, at: at, size: next) } }
        }
        needsDisplay = true
    }

    func setFont(_ postScript: String?) {
        restyle { $0.font = postScript }
        fontListOpen = false
    }

    /// An "A" in the current look: the Look button shows what it will do.
    private func paintLook(_ r: CGRect) {
        let size = r.height * 1.05
        var a = Annotation(.text("A", at: .zero, size: size), color: ToolbarLook.current.glyph, width: 3,
                           style: TextStyle(bold: true, look: targetStyle.look))
        let box = a.textRect("A", at: .zero, size: size)
        a.kind = .text("A", at: CGPoint(x: r.midX - box.width / 2, y: r.midY - box.height / 2), size: size)
        a.draw(screen: image, screenSize: bounds.size)
    }

    /// Above the text being typed, or above the selection's top right; clear of the tools bar.
    private func layoutTextBar() {
        guard let sel = selection else { return }
        textBar.labelFont = tools.labelFont
        let size = textBar.size, gap: CGFloat = 10, area = bounds.insetBy(dx: 4, dy: 4)
        let anchor = textTarget.map { annotations[$0].bounds.insetBy(dx: -6, dy: -4) } ?? sel
        var x = textTarget == nil ? sel.maxX - size.width : anchor.minX
        var y = anchor.minY - gap - size.height
        if y < area.minY { y = (textTarget == nil ? sel.minY : anchor.maxY) + gap }
        let frame = CGRect(x: x, y: y, width: size.width, height: size.height)
        if frame.intersects(tools.frame.insetBy(dx: -gap, dy: -gap)) { x = tools.frame.minX > anchor.minX ? tools.frame.minX - gap - size.width : tools.frame.maxX + gap }
        x = min(max(x, area.minX), max(area.minX, area.maxX - size.width))
        y = min(max(y, area.minY), max(area.minY, area.maxY - size.height))
        textBar.layout(at: CGPoint(x: x.rounded(), y: y.rounded()))
    }

    private var fontListRect: CGRect {
        let rowH: CGFloat = 34, w: CGFloat = 230, h = CGFloat(TextStyle.fonts.count) * rowH + 16
        let anchor = textBar.buttonFrames.first?.first ?? textBar.frame
        var y = textBar.frame.maxY + 8
        if y + h > bounds.maxY - 4 { y = textBar.frame.minY - 8 - h }
        let x = min(max(anchor.minX, 4), bounds.maxX - w - 4)
        return CGRect(x: x, y: max(4, y), width: w, height: h)
    }

    private func fontRow(at p: CGPoint) -> Int? {
        let r = fontListRect
        guard r.contains(p) else { return nil }
        let i = Int((p.y - r.minY - 8) / 34)
        return (0..<TextStyle.fonts.count).contains(i) ? i : nil
    }

    private func drawFontList(_ look: ToolbarLook) {
        let r = fontListRect
        look.panel.setFill()
        NSBezierPath(roundedRect: r, xRadius: 14, yRadius: 14).fill()
        look.border.setStroke()
        let edge = NSBezierPath(roundedRect: r.insetBy(dx: 0.5, dy: 0.5), xRadius: 14, yRadius: 14)
        edge.lineWidth = look.borderWidth
        edge.stroke()
        let current = targetStyle.font
        for (i, f) in TextStyle.fonts.enumerated() {
            let row = CGRect(x: r.minX + 6, y: r.minY + 8 + CGFloat(i) * 34, width: r.width - 12, height: 34)
            let on = f.postScript == current
            if on {
                look.on.setFill()
                NSBezierPath(roundedRect: row.insetBy(dx: 0, dy: 2), xRadius: 9, yRadius: 9).fill()
            }
            let font = f.postScript.flatMap { NSFont(name: $0, size: 17) } ?? .systemFont(ofSize: 15, weight: .semibold)
            let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: on ? look.onGlyph : look.glyph]
            let s = (f.name as NSString).size(withAttributes: attrs)
            (f.name as NSString).draw(at: CGPoint(x: row.minX + 12, y: row.midY - s.height / 2), withAttributes: attrs)
        }
    }

    // MARK: - mouse

    private func point(_ e: NSEvent) -> CGPoint { convert(e.locationInWindow, from: nil) }

    override func mouseMoved(with e: NSEvent) {
        mouse = point(e)
        if Settings.showMagnifier { needsDisplay = true }
        if findBar.isOpen {
            let b = findBar.frame.contains(mouse) ? findBar.button(at: mouse) : nil
            if b != findBar.hoverButton { findBar.hoverButton = b; needsDisplay = true }
            if findBar.frame.contains(mouse) { NSCursor.arrow.set(); return }
            if findBar.hitIndex(at: mouse) != nil { NSCursor.pointingHand.set(); return }
        }
        if selection == nil {
            let w = windows.first { $0.contains(mouse) }
            if w != hoverWindow { hoverWindow = w; needsDisplay = true }
        }
        let h = hitBar(mouse)
        if h?.bar !== hover?.bar || h?.group != hover?.group || h?.index != hover?.index {
            hover = h
            needsDisplay = true
        }
        if h != nil || (paletteOpen && paletteRect.contains(mouse)) { NSCursor.arrow.set() }
        else if let sel = selection, sel.contains(mouse), tool == nil, shape(at: mouse) != nil { NSCursor.pointingHand.set() }
        else if let sel = selection, sel.contains(mouse), tool == nil { NSCursor.openHand.set() }
        else { NSCursor.crosshair.set() }
    }

    override func mouseDown(with e: NSEvent) {
        let p = point(e)
        mouse = p
        dragMoved = false
        if findBar.isOpen {
            if findBar.frame.contains(p) { findBar.click(p); return }
            if let i = findBar.hitIndex(at: p) { findBar.select(i); return }
            let onFindButton = findButtonFrame.contains(p)
            findBar.close()
            if onFindButton { return }
        }
        if fontListOpen, textBarVisible {
            if let row = fontRow(at: p) { setFont(TextStyle.fonts[row].postScript); needsDisplay = true; return }
            if !fontListRect.contains(p), hitBar(p) == nil { fontListOpen = false; needsDisplay = true }
        }
        if paletteOpen {
            if paletteRect.contains(p) {
                if let cell = paletteCells().first(where: { $0.rect.insetBy(dx: -4, dy: -4).contains(p) }) {
                    if let c = cell.color { color = c }
                    if let w = cell.width { lineWidth = w }
                    restyleEditedText()
                    restyleSelectedShape(color: cell.color, width: cell.width)
                }
                drag = .palette
                needsDisplay = true
                return
            }
            if hitBar(p) == nil { paletteOpen = false; needsDisplay = true }
        }
        if let h = hitBar(p) {
            drag = .button(h.bar, group: h.group, index: h.index)
            needsDisplay = true
            return
        }
        if let i = editing {
            if i < annotations.count, annotations[i].bounds.insetBy(dx: -6, dy: -4).contains(p) { return }
            finishText()
        }
        if let sel = selection {
            if let handle = Geometry.handle(at: p, in: sel, radius: 8) {
                drag = .resize(handle: handle)
                return
            }
            if sel.contains(p), let tool {
                begin(tool, at: p)
                return
            }
            if sel.contains(p), let i = shape(at: p) {
                selectedShape = i
                drag = .moveShape(from: p, index: i, start: annotations[i])
                needsDisplay = true
                return
            }
            selectedShape = nil
            if sel.contains(p) {
                drag = .move(from: p, start: sel)
                NSCursor.closedHand.set()
                return
            }
        }
        selectedShape = nil
        drag = .select(from: p)
        if let host = host as? OverlayWindow { session?.began(on: host) }
    }

    override func mouseDragged(with e: NSEvent) {
        let p = point(e)
        mouse = p
        switch drag {
        case .select(let from):
            if !dragMoved, hypot(p.x - from.x, p.y - from.y) < 3 { return }
            dragMoved = true
            hoverWindow = nil
            selection = Geometry.rect(from, clamp(p)).intersection(bounds)
            resetAnnotationsForNewSelection()
        case .move(let from, let start):
            dragMoved = true
            var r = start.offsetBy(dx: p.x - from.x, dy: p.y - from.y)
            r.origin.x = min(max(r.minX, 0), bounds.width - r.width)
            r.origin.y = min(max(r.minY, 0), bounds.height - r.height)
            selection = r
        case .resize(let handle):
            guard let sel = selection else { return }
            dragMoved = true
            selection = Geometry.resize(sel, handle: handle, to: clamp(p))
        case .draw(let from):
            extendDrawing(from: from, to: p, shift: e.modifierFlags.contains(.shift))
        case let .moveShape(from, index, start):
            guard index < annotations.count else { return }
            if !dragMoved {
                guard hypot(p.x - from.x, p.y - from.y) >= 2 else { return }
                dragMoved = true
                undoStack.append(annotations)
                redoStack.removeAll()
            }
            annotations[index] = start.moved(by: CGSize(width: p.x - from.x, height: p.y - from.y))
        default:
            return
        }
        needsDisplay = true
    }

    override func mouseUp(with e: NSEvent) {
        let p = point(e)
        switch drag {
        case .select:
            if !dragMoved {
                selection = hoverWindow?.intersection(bounds)
                hoverWindow = nil
            }
            if let s = selection, s.width < 4 || s.height < 4 { selection = nil }
            if selection != nil, Settings.hidePersonalInfo { findPersonalInfo() }
        case .move, .resize:
            NSCursor.openHand.set()
            findBar.update()
        case .draw:
            if let d = drawing, isMeaningful(d) { record { annotations.append(d) } }
            drawing = nil
        case let .button(bar, g, i):
            drag = .none
            if let h = bar.hit(p), h.group == g, h.index == i { bar.groups[g][i].action() }
        default:
            break
        }
        drag = .none
        dragMoved = false
        needsDisplay = true
    }

    override func rightMouseDown(with e: NSEvent) {
        if selection != nil, editing == nil { clearSelection() } else { close() }
    }

    private func clamp(_ p: CGPoint) -> CGPoint {
        CGPoint(x: min(max(p.x, 0), bounds.width), y: min(max(p.y, 0), bounds.height))
    }

    /// The topmost drawing under `p`, if any.
    func shape(at p: CGPoint) -> Int? {
        annotations.indices.reversed().first { annotations[$0].hit(p, tolerance: 5) }
    }

    /// The palette changed: the picked drawing takes the new color or size.
    private func restyleSelectedShape(color: NSColor?, width: CGFloat?) {
        guard editing == nil, let i = selectedShape, i < annotations.count else { return }
        record {
            if let color { annotations[i].color = color }
            if let width { annotations[i].width = width }
        }
    }

    func deleteSelectedShape() {
        guard let i = selectedShape, i < annotations.count else { return }
        record { annotations.remove(at: i) }
        selectedShape = nil
    }

    func clearSelection() {
        findBar.close()
        finishText()
        selection = nil
        resetAnnotationsForNewSelection()
        needsDisplay = true
    }

    private func resetAnnotationsForNewSelection() {
        selectedShape = nil
        guard !annotations.isEmpty || !redactions.isEmpty else { return }
        annotations.removeAll()
        redactions.removeAll()
        undoStack.removeAll()
        redoStack.removeAll()
        hidingPersonal = false
        counter = 1
    }

    // MARK: - annotations

    private func begin(_ tool: Tool, at p: CGPoint) {
        switch tool {
        case .text:
            let size = textSize
            record { annotations.append(Annotation(.text("", at: CGPoint(x: p.x, y: p.y - size * 0.6), size: size), color: color, width: lineWidth, style: textStyle)) }
            editing = annotations.count - 1
        case .counter:
            record { annotations.append(Annotation(.counter(counter, at: p), color: color, width: lineWidth)) }
            counter += 1
        default:
            drawing = Annotation(initialKind(tool, p), color: color, width: lineWidth)
            drag = .draw(from: p)
        }
        needsDisplay = true
    }

    private func initialKind(_ tool: Tool, _ p: CGPoint) -> Annotation.Kind {
        switch tool {
        case .pen: return .pen([p])
        case .marker: return .marker([p])
        case .line: return .line(p, p)
        case .arrow: return .arrow(p, p)
        case .rect: return .rect(CGRect(origin: p, size: .zero))
        case .ellipse: return .ellipse(CGRect(origin: p, size: .zero))
        case .pixelate: return .pixelate(CGRect(origin: p, size: .zero), oval: false)
        case .text, .counter: return .pen([p])
        }
    }

    private func extendDrawing(from a: CGPoint, to b: CGPoint, shift: Bool) {
        guard var d = drawing else { return }
        var end = b
        if shift {
            // Shift: straight lines snap to 45°, boxes and circles stay square.
            let dx = b.x - a.x, dy = b.y - a.y
            switch d.kind {
            case .line, .arrow:
                let angle = (atan2(dy, dx) / (.pi / 4)).rounded() * (.pi / 4), len = hypot(dx, dy)
                end = CGPoint(x: a.x + cos(angle) * len, y: a.y + sin(angle) * len)
            default:
                let side = max(abs(dx), abs(dy))
                end = CGPoint(x: a.x + (dx < 0 ? -side : side), y: a.y + (dy < 0 ? -side : side))
            }
        }
        switch d.kind {
        case .pen(var ps): ps.append(b); d.kind = .pen(ps)
        case .marker(var ps): ps.append(b); d.kind = .marker(ps)
        case .line: d.kind = .line(a, end)
        case .arrow: d.kind = .arrow(a, end)
        case .rect: d.kind = .rect(Geometry.rect(a, end))
        case .ellipse: d.kind = .ellipse(Geometry.rect(a, end))
        case .pixelate(_, let oval): d.kind = .pixelate(Geometry.rect(a, end), oval: oval)
        default: break
        }
        drawing = d
    }

    private func isMeaningful(_ a: Annotation) -> Bool {
        switch a.kind {
        case .pen, .marker: return true
        default: let b = a.bounds; return b.width >= 3 || b.height >= 3
        }
    }

    /// Changes to the annotations go through here so they can be undone.
    private func record(_ change: () -> Void) {
        undoStack.append(annotations)
        redoStack.removeAll()
        change()
        needsDisplay = true
    }

    func undo() {
        finishText()
        selectedShape = nil
        guard let last = undoStack.popLast() else { return }
        redoStack.append(annotations)
        annotations = last
        needsDisplay = true
    }

    func redo() {
        selectedShape = nil
        guard let next = redoStack.popLast() else { return }
        undoStack.append(annotations)
        annotations = next
        needsDisplay = true
    }

    func setTool(_ t: Tool) {
        finishText()
        selectedShape = nil
        tool = tool == t ? nil : t
        needsDisplay = true
    }

    private func type(_ e: NSEvent) {
        guard let i = editing, i < annotations.count, case let .text(s, at, size) = annotations[i].kind else { return }
        var text = s
        switch e.keyCode {
        case 51: if !text.isEmpty { text.removeLast() }   // delete
        case 36, 76: text.append("\n")                     // return
        default:
            let typed = (e.characters ?? "").filter { !$0.isNewline && ($0.isLetter || $0.isNumber || $0.isPunctuation || $0.isSymbol || $0 == " ") }
            text.append(contentsOf: typed)
        }
        annotations[i].kind = .text(text, at: at, size: size)
        needsDisplay = true
    }

    private func restyleEditedText() {
        guard let i = editing, i < annotations.count, case .text = annotations[i].kind else { return }
        annotations[i].color = color
    }

    /// Stops typing; empty text is dropped along with its undo step.
    private func finishText() {
        guard let i = editing else { return }
        editing = nil
        if i < annotations.count, case let .text(s, _, _) = annotations[i].kind,
           s.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            annotations.remove(at: i)
            _ = undoStack.popLast()
        }
        needsDisplay = true
    }

    // MARK: - keyboard

    override func keyDown(with e: NSEvent) {
        if !handleKey(e) { super.keyDown(with: e) }
    }

    override func performKeyEquivalent(with e: NSEvent) -> Bool {
        guard e.modifierFlags.contains(.command) else { return false }
        // Typing in the find box: its own copy, paste and undo come first.
        if findBar.isEditing, let key = e.charactersIgnoringModifiers?.lowercased(), ["c", "v", "x", "a", "z"].contains(key) { return false }
        return handleKey(e)
    }

    func toggleFind() {
        if findBar.isOpen { findBar.close(); return }
        finishText()
        paletteOpen = false
        // With nothing selected yet, look through the whole screen.
        if selection == nil { selection = bounds; hoverWindow = nil; layoutBars() }
        findBar.open()
    }

    /// Highlight from the find bar: a highlighter stroke across each match, as one undo step.
    func addMarks(_ boxes: [CGRect]) {
        let marks = boxes.map { r in
            // The marker draws max(14, width × 5) wide with square ends, so this covers the match's height exactly.
            Annotation(.marker([CGPoint(x: r.minX, y: r.midY), CGPoint(x: r.maxX, y: r.midY)]), color: FindBar.yellow, width: r.height * 1.15 / 5)
        }
        record { annotations.append(contentsOf: marks) }
    }

    @discardableResult
    func handleKey(_ e: NSEvent) -> Bool {
        let cmd = e.modifierFlags.contains(.command), shift = e.modifierFlags.contains(.shift)
        let key = e.charactersIgnoringModifiers?.lowercased() ?? ""
        if editing != nil, !cmd {
            if e.keyCode == 53 { finishText() } else { type(e) }   // Esc finishes the text
            return true
        }
        if (e.keyCode == 51 || e.keyCode == 117), !cmd, selectedShape != nil {   // delete, forward delete
            deleteSelectedShape()
            return true
        }
        if e.keyCode == 53 {
            if findBar.isOpen { findBar.close() }
            else if paletteOpen { paletteOpen = false; needsDisplay = true }
            else { close() }
            return true
        }
        if cmd {
            switch key {
            case "c": shift ? copyText() : copyToClipboard()
            case "s": save()
            case "t": pin()
            case "p": printImage()
            case "b" where textBarVisible: restyle { $0.bold.toggle() }
            case "i" where textBarVisible: restyle { $0.italic.toggle() }
            case "[" where textBarVisible: stepTextSize(-1)
            case "]" where textBarVisible: stepTextSize(1)
            case "d": upload(.link)
            case "k": upload(.search)
            case "z": shift ? redo() : undo()
            case "y": redo()
            case "a" where shift: askAI()
            case "a": finishText(); selection = bounds; hoverWindow = nil; needsDisplay = true
            case "f": toggleFind()
            default: return false
            }
            return true
        }
        if key == "c", selection == nil { copyColor(); return true }
        guard selection != nil else { return false }
        switch key {
        case "p": setTool(.pen)
        case "l": setTool(.line)
        case "a": setTool(.arrow)
        case "r": setTool(.rect)
        case "e": setTool(.ellipse)
        case "m": setTool(.marker)
        case "t": setTool(.text)
        case "n": setTool(.counter)
        case "b": setTool(.pixelate)
        case "f": blurFaces()
        case "s": startScrollCapture()
        case "h": toggleHidePersonal()
        default:
            if e.keyCode == 36 || e.keyCode == 76 { copyToClipboard(); return true }   // Return copies, like double-clicking
            return false
        }
        return true
    }

    // MARK: - results

    /// The finished image: the selection at full resolution with everything drawn on it.
    func renderResult(annotated: Bool = true) -> CGImage? {
        finishText()
        guard let sel = selection else { return nil }
        return Renderer.render(screen: image, screenSize: bounds.size, selection: sel,
                               annotations: annotated ? redactions + annotations : [])
    }

    private var scale: CGFloat { CGFloat(image.width) / bounds.width }

    func close() { session?.finish() }

    func copyToClipboard() {
        guard let img = renderResult() else { return }
        ImageActions.copy(img, scale: scale)
        close()
        HUD.show("Copied to clipboard")
    }

    func save() {
        guard let img = renderResult() else { return }
        let s = scale
        close()
        DispatchQueue.main.async { ImageActions.save(img, scale: s) }
    }

    func pin() {
        guard let img = renderResult(), let sel = selection, let window = host else { return }
        // Where the selection is on screen, in AppKit's bottom-left global coordinates.
        let frame = CGRect(x: window.frame.minX + sel.minX, y: window.frame.maxY - sel.maxY, width: sel.width, height: sel.height)
        close()
        PinWindow.open(img, frame: frame)
    }

    /// Ask AI: the finished screenshot goes to a chat window, and the capture closes.
    func askAI() {
        guard let img = renderResult() else { return }
        close()
        AskAIChat.open(img)
    }

    /// Edit with AI or Make a video, in their own window.
    func runAI(_ kind: AIRun.Kind) {
        guard let img = renderResult() else { return }
        let s = scale
        close()
        AIRun.open(kind, image: img, scale: s)
    }

    /// Scrolling capture over the selection: the overlay goes away and the page under it is scrolled and stitched.
    func startScrollCapture() {
        guard let sel = selection, let window = host, let screen = window.screen else { return }
        let area = CGRect(x: window.frame.minX + sel.minX, y: window.frame.maxY - sel.maxY, width: sel.width, height: sel.height)
        close()
        Task { @MainActor in await ScrollCapture.run(area: area, screen: screen) }
    }

    /// Upload and copy link, Share or Search similar images, through fortefile.com.
    func upload(_ kind: Uploads.Kind) {
        guard let img = renderResult() else { return }
        close()
        Uploads.send(img, kind: kind)
    }

    func printImage() {
        guard let img = renderResult() else { return }
        let s = scale
        close()
        DispatchQueue.main.async { ImageActions.print(img, scale: s) }
    }

    /// C with nothing selected: the hex color under the pointer, to the clipboard.
    func copyColor() {
        guard let c = pixelColor(at: mouse) else { return }
        let text = Self.hex(c)
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
        HUD.show("Copied \(text)")
    }

    // MARK: - magnifier

    private lazy var pixels = NSBitmapImageRep(cgImage: image)

    /// The color of the screen pixel under `p` (view points).
    func pixelColor(at p: CGPoint) -> NSColor? {
        let x = Int(p.x * scale), y = Int(p.y * scale)
        guard x >= 0, y >= 0, x < pixels.pixelsWide, y < pixels.pixelsHigh else { return nil }
        return pixels.colorAt(x: x, y: y)?.usingColorSpace(.sRGB)
    }

    static func hex(_ c: NSColor) -> String {
        String(format: "#%02X%02X%02X", Int((c.redComponent * 255).rounded()), Int((c.greenComponent * 255).rounded()), Int((c.blueComponent * 255).rounded()))
    }

    private var magnifierVisible: Bool {
        guard Settings.showMagnifier, bounds.contains(mouse), !paletteOpen else { return false }
        switch drag {
        case .select, .resize: return true
        default: return selection == nil
        }
    }

    /// Fifteen screen pixels around the pointer, eight points each, in a round lens; coordinates and color underneath.
    private func drawMagnifier() {
        let cells = 15, cell: CGFloat = 8, d = CGFloat(cells) * cell, infoH: CGFloat = 26
        var origin = CGPoint(x: mouse.x + 24, y: mouse.y + 24)
        if origin.x + d > bounds.maxX - 4 { origin.x = mouse.x - 24 - d }
        if origin.y + d + infoH + 8 > bounds.maxY - 4 { origin.y = mouse.y - 24 - d - infoH - 8 }
        let lens = CGRect(origin: origin, size: CGSize(width: d, height: d))
        let center = CGPoint(x: (mouse.x * scale).rounded(.down), y: (mouse.y * scale).rounded(.down))
        let wanted = CGRect(x: center.x - CGFloat(cells / 2), y: center.y - CGFloat(cells / 2), width: CGFloat(cells), height: CGFloat(cells))
        let have = wanted.intersection(CGRect(x: 0, y: 0, width: image.width, height: image.height))
        guard let gc = NSGraphicsContext.current else { return }
        gc.saveGraphicsState()
        NSBezierPath(ovalIn: lens).addClip()
        NSColor.black.setFill()
        lens.fill()
        if !have.isEmpty, let crop = image.cropping(to: have) {
            let dest = CGRect(x: lens.minX + (have.minX - wanted.minX) * cell, y: lens.minY + (have.minY - wanted.minY) * cell,
                              width: have.width * cell, height: have.height * cell)
            gc.imageInterpolation = .none
            NSImage(cgImage: crop, size: dest.size).draw(in: dest, from: .zero, operation: .copy, fraction: 1, respectFlipped: true, hints: nil)
        }
        let mid = CGRect(x: lens.minX + CGFloat(cells / 2) * cell, y: lens.minY + CGFloat(cells / 2) * cell, width: cell, height: cell)
        NSColor.black.setStroke()
        NSBezierPath(rect: mid.insetBy(dx: -0.5, dy: -0.5)).stroke()
        NSColor.white.setStroke()
        NSBezierPath(rect: mid.insetBy(dx: 0.5, dy: 0.5)).stroke()
        gc.restoreGraphicsState()
        let ring = NSBezierPath(ovalIn: lens.insetBy(dx: 1.5, dy: 1.5))
        ring.lineWidth = 3
        NSColor.white.setStroke()
        ring.stroke()

        let color = pixelColor(at: mouse) ?? .black
        let text = "\(Int(mouse.x * scale)), \(Int(mouse.y * scale))   \(Self.hex(color))"
        let look = ToolbarLook.current
        let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.monospacedDigitSystemFont(ofSize: look.bright ? 13 : 12, weight: .medium), .foregroundColor: NSColor.white]
        let size = (text as NSString).size(withAttributes: attrs)
        let pill = CGRect(x: lens.midX - (size.width + 44) / 2, y: lens.maxY + 8, width: size.width + 44, height: infoH)
        look.panel.setFill()
        NSBezierPath(roundedRect: pill, xRadius: infoH / 2, yRadius: infoH / 2).fill()
        let swatch = CGRect(x: pill.minX + 10, y: pill.midY - 7, width: 14, height: 14)
        color.setFill()
        NSBezierPath(ovalIn: swatch).fill()
        NSColor(white: 1, alpha: 0.6).setStroke()
        NSBezierPath(ovalIn: swatch).stroke()
        (text as NSString).draw(at: CGPoint(x: swatch.maxX + 8, y: pill.midY - size.height / 2), withAttributes: attrs)
    }

    /// For the self-test: put the pointer somewhere, and pick what's under it.
    func hover(at p: CGPoint) { mouse = p }
    func pick(at p: CGPoint) { selectedShape = shape(at: p); needsDisplay = true }

    func copyText() {
        guard let img = renderResult(annotated: false) else { return }
        close()
        Task {
            let text = (try? await VisionTools.text(in: img)) ?? ""
            await MainActor.run {
                if text.isEmpty { HUD.show("No text found"); return }
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(text, forType: .string)
                HUD.show("Text copied")
            }
        }
    }

    func blurFaces() {
        guard let sel = selection, busy == nil, let img = renderResult(annotated: false) else { return }
        busy = "Finding faces…"
        needsDisplay = true
        Task {
            let faces = (try? await VisionTools.faces(in: img)) ?? []
            await MainActor.run {
                busy = nil
                let blurs = faces.map { Annotation(.pixelate(Geometry.fromNormalized($0, in: sel).insetBy(dx: -$0.width * sel.width * 0.12, dy: -$0.height * sel.height * 0.12), oval: true), color: color, width: lineWidth) }
                if !blurs.isEmpty { record { annotations.append(contentsOf: blurs) } }
                showFlash(blurs.isEmpty ? "No faces found" : blurs.count == 1 ? "1 face blurred" : "\(blurs.count) faces blurred")
            }
        }
    }

    func toggleHidePersonal() {
        if hidingPersonal {
            hidingPersonal = false
            redactions.removeAll()
            Settings.hidePersonalInfo = false
            needsDisplay = true
            return
        }
        Settings.hidePersonalInfo = true
        findPersonalInfo()
    }

    private func findPersonalInfo() {
        guard let sel = selection, busy == nil, let img = renderResult(annotated: false) else { return }
        hidingPersonal = true
        busy = "Hiding personal info…"
        needsDisplay = true
        let keywords = Settings.keywords
        Task {
            let text = (try? await VisionTools.personalInfo(in: img, keywords: keywords)) ?? []
            let faces = (try? await VisionTools.faces(in: img)) ?? []
            await MainActor.run {
                busy = nil
                guard hidingPersonal, selection == sel else { needsDisplay = true; return }
                redactions = text.map { Annotation(.pixelate(Geometry.fromNormalized($0, in: sel).insetBy(dx: -2, dy: -2), oval: false), color: .black, width: 3) }
                    + faces.map { Annotation(.pixelate(Geometry.fromNormalized($0, in: sel).insetBy(dx: -$0.width * sel.width * 0.12, dy: -$0.height * sel.height * 0.12), oval: true), color: .black, width: 3) }
                showFlash(redactions.isEmpty ? "Nothing personal found" : "Personal info hidden")
            }
        }
    }

    func showFlash(_ text: String) {
        flash = text
        flashWork?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.flash = nil; self?.needsDisplay = true }
        flashWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.6, execute: work)
        needsDisplay = true
    }

    // MARK: - for the self-test

    func select(_ r: CGRect) {
        selection = r
        hoverWindow = nil
        needsDisplay = true
    }

    func add(_ a: Annotation) { record { annotations.append(a) } }

    func hoverButton(_ bar: Toolbar, group: Int, index: Int) { hover = (bar, group, index) }
}

enum Palette {
    static let colors: [NSColor] = [
        NSColor(srgbRed: 1, green: 0.23, blue: 0.19, alpha: 1), NSColor(srgbRed: 1, green: 0.58, blue: 0, alpha: 1),
        NSColor(srgbRed: 1, green: 0.8, blue: 0, alpha: 1), NSColor(srgbRed: 0.2, green: 0.78, blue: 0.35, alpha: 1),
        NSColor(srgbRed: 0, green: 0.78, blue: 0.75, alpha: 1), NSColor(srgbRed: 0.04, green: 0.52, blue: 1, alpha: 1),
        NSColor(srgbRed: 0.69, green: 0.32, blue: 0.87, alpha: 1), NSColor(srgbRed: 1, green: 0.18, blue: 0.57, alpha: 1),
        .white, .black,
    ]
    static let widths: [CGFloat] = [2, 3, 5, 8]
}
