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
    private var editing: Int?
    private var counter = 1
    private var hidingPersonal = false
    private var busy: String?
    private var flash: String?
    private var flashWork: DispatchWorkItem?
    private var paletteOpen = false

    private(set) lazy var tools = buildTools()
    private(set) lazy var actions = buildActions()
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
            [t(.pen, "Pen", "Pen", "P", ["pencil.tip", "pencil"]),
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
            [b("Pin to screen", "Pin", "⌘T", ["pin"]) { [unowned self] in self.pin() },
             b("Copy text", "Copy text", "⇧⌘C", ["text.viewfinder"]) { [unowned self] in self.copyText() }],
            [b("Copy", "Copy", "⌘C", ["doc.on.doc"]) { [unowned self] in self.copy() },
             b("Save", "Save", "⌘S", ["square.and.arrow.down"]) { [unowned self] in self.save() }],
            [b("Close", "Close", "Esc", ["xmark"]) { [unowned self] in self.close() }],
        ])
    }

    private var bars: [Toolbar] { selection == nil ? [] : [tools, actions] }

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
                if let h = hover, tools.labelFont == nil { drawTip(h.bar.groups[h.group][h.index], h.bar, h.bar.buttonFrames[h.group][h.index], look) }
            }
            if let text = busy ?? flash { drawPill(text, near: sel) }
        } else if let w = hoverWindow {
            let p = NSBezierPath(rect: w.insetBy(dx: 1.5, dy: 1.5))
            p.lineWidth = 3
            NSColor.controlAccentColor.setStroke()
            p.stroke()
        }
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
            let font = Annotation.font(size: size)
            let lines = s.components(separatedBy: "\n")
            let last = lines.last ?? ""
            let w = (last as NSString).size(withAttributes: [.font: font]).width
            let lineHeight = ceil(font.ascender - font.descender + font.leading)
            a.color.setFill()
            NSBezierPath(rect: CGRect(x: at.x + w + 1, y: at.y + CGFloat(lines.count - 1) * lineHeight, width: 2, height: lineHeight)).fill()
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

    // MARK: - mouse

    private func point(_ e: NSEvent) -> CGPoint { convert(e.locationInWindow, from: nil) }

    override func mouseMoved(with e: NSEvent) {
        mouse = point(e)
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
        else if let sel = selection, sel.contains(mouse), tool == nil { NSCursor.openHand.set() }
        else { NSCursor.crosshair.set() }
    }

    override func mouseDown(with e: NSEvent) {
        let p = point(e)
        mouse = p
        dragMoved = false
        if paletteOpen {
            if paletteRect.contains(p) {
                if let cell = paletteCells().first(where: { $0.rect.insetBy(dx: -4, dy: -4).contains(p) }) {
                    if let c = cell.color { color = c }
                    if let w = cell.width { lineWidth = w }
                    restyleEditedText()
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
            if sel.contains(p) {
                drag = .move(from: p, start: sel)
                NSCursor.closedHand.set()
                return
            }
        }
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

    func clearSelection() {
        finishText()
        selection = nil
        resetAnnotationsForNewSelection()
        needsDisplay = true
    }

    private func resetAnnotationsForNewSelection() {
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
            let size = max(14, lineWidth * 6)
            record { annotations.append(Annotation(.text("", at: CGPoint(x: p.x, y: p.y - size * 0.6), size: size), color: color, width: lineWidth)) }
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
        guard let last = undoStack.popLast() else { return }
        redoStack.append(annotations)
        annotations = last
        needsDisplay = true
    }

    func redo() {
        guard let next = redoStack.popLast() else { return }
        undoStack.append(annotations)
        annotations = next
        needsDisplay = true
    }

    func setTool(_ t: Tool) {
        finishText()
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
        guard let i = editing, i < annotations.count, case let .text(s, at, _) = annotations[i].kind else { return }
        annotations[i].color = color
        annotations[i].kind = .text(s, at: at, size: max(14, lineWidth * 6))
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
        e.modifierFlags.contains(.command) ? handleKey(e) : false
    }

    @discardableResult
    func handleKey(_ e: NSEvent) -> Bool {
        let cmd = e.modifierFlags.contains(.command), shift = e.modifierFlags.contains(.shift)
        let key = e.charactersIgnoringModifiers?.lowercased() ?? ""
        if editing != nil, !cmd {
            if e.keyCode == 53 { finishText() } else { type(e) }   // Esc finishes the text
            return true
        }
        if e.keyCode == 53 {
            if paletteOpen { paletteOpen = false; needsDisplay = true } else { close() }
            return true
        }
        if cmd {
            switch key {
            case "c": shift ? copyText() : copy()
            case "s": save()
            case "t": pin()
            case "z": shift ? redo() : undo()
            case "y": redo()
            case "a": finishText(); selection = bounds; hoverWindow = nil; needsDisplay = true
            default: return false
            }
            return true
        }
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
        case "h": toggleHidePersonal()
        default:
            if e.keyCode == 36 || e.keyCode == 76 { copy(); return true }   // Return copies, like double-clicking
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

    func copy() {
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

    private func showFlash(_ text: String) {
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
