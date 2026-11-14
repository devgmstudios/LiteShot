import AppKit

/// A screenshot floating above every window. Drag it anywhere, drag an edge to resize, scroll to zoom, ⌥-scroll to fade,
/// R and L to rotate, H and V to flip, T to let clicks pass through, arrows to nudge, double-click or Esc to close,
/// right-click for everything else. The same as a Windows pin.
final class PinWindow: NSWindow {
    private static var pins: [PinWindow] = []
    private(set) var image: CGImage
    let scale: CGFloat

    enum Turn { case right, left, horizontal, vertical }

    static func open(_ image: CGImage, frame: CGRect) {
        let pin = PinWindow(image: image, frame: frame)
        pins.append(pin)
        pin.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    /// Pins that let clicks through can't be clicked, so the menu bar offers to make them clickable again.
    static var anyClickThrough: Bool { pins.contains { $0.clickThrough } }

    static func makeAllClickable() {
        for pin in pins where pin.clickThrough { pin.clickThrough = false }
    }

    private init(image: CGImage, frame: CGRect) {
        self.image = image
        scale = CGFloat(image.width) / max(1, frame.width)
        super.init(contentRect: frame, styleMask: [.borderless, .resizable], backing: .buffered, defer: false)
        level = .floating
        hasShadow = true
        isOpaque = false
        backgroundColor = .clear
        isReleasedWhenClosed = false
        contentAspectRatio = frame.size
        minSize = CGSize(width: 48, height: 48 * frame.height / max(1, frame.width))
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        contentView = PinView(pin: self)
    }

    override var canBecomeKey: Bool { true }

    override func close() {
        super.close()
        PinWindow.pins.removeAll { $0 === self }
    }

    /// Clicks go to whatever is underneath; the pin fades a little so it reads as "not in the way".
    var clickThrough = false {
        didSet {
            ignoresMouseEvents = clickThrough
            alphaValue = clickThrough ? min(alphaValue, 0.7) : max(alphaValue, 0.9)
            if clickThrough { HUD.show("Clicks pass through · make it clickable from the menu bar") }
        }
    }

    /// Zooms around the top-left corner, keeping the shape.
    func zoom(by factor: CGFloat) {
        let ratio = frame.height / frame.width
        let w = min(max(frame.width * factor, minSize.width), CGFloat(image.width) / scale * 6)
        let h = w * ratio
        setFrame(CGRect(x: frame.minX, y: frame.maxY - h, width: w, height: h), display: true)
    }

    func actualSize() { zoom(by: CGFloat(image.width) / scale / frame.width) }

    func nudge(dx: CGFloat, dy: CGFloat) { setFrameOrigin(CGPoint(x: frame.minX + dx, y: frame.minY + dy)) }

    /// Rotates or flips the picture; a quarter turn swaps the pin's width and height around its top-left corner.
    func turn(_ how: Turn) {
        guard let turned = PinWindow.turned(image, how) else { return }
        image = turned
        let rotates = how == .right || how == .left
        let size = rotates ? CGSize(width: frame.height, height: frame.width) : frame.size
        contentAspectRatio = size
        minSize = CGSize(width: 48, height: 48 * size.height / max(1, size.width))
        setFrame(CGRect(x: frame.minX, y: frame.maxY - size.height, width: size.width, height: size.height), display: true)
        contentView?.needsDisplay = true
    }

    static func turned(_ image: CGImage, _ how: Turn) -> CGImage? {
        let rotates = how == .right || how == .left
        let w = image.width, h = image.height
        let (nw, nh) = rotates ? (h, w) : (w, h)
        guard let ctx = CGContext(data: nil, width: nw, height: nh, bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue) else { return nil }
        switch how {
        case .right: ctx.translateBy(x: 0, y: CGFloat(nh)); ctx.rotate(by: -.pi / 2)
        case .left: ctx.translateBy(x: CGFloat(nw), y: 0); ctx.rotate(by: .pi / 2)
        case .horizontal: ctx.translateBy(x: CGFloat(w), y: 0); ctx.scaleBy(x: -1, y: 1)
        case .vertical: ctx.translateBy(x: 0, y: CGFloat(h)); ctx.scaleBy(x: 1, y: -1)
        }
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        return ctx.makeImage()
    }

    func copyText() {
        let picture = image
        Task {
            let text = (try? await VisionTools.text(in: picture)) ?? ""
            await MainActor.run {
                if text.isEmpty { HUD.show("No text found"); return }
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(text, forType: .string)
                HUD.show("Text copied")
            }
        }
    }
}

private final class PinView: NSView {
    unowned let pin: PinWindow

    init(pin: PinWindow) {
        self.pin = pin
        super.init(frame: CGRect(origin: .zero, size: pin.frame.size))
        autoresizingMask = [.width, .height]
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func draw(_ dirtyRect: NSRect) {
        NSGraphicsContext.current?.imageInterpolation = .high
        NSImage(cgImage: pin.image, size: bounds.size).draw(in: bounds)
        NSColor(white: 1, alpha: pin.isKeyWindow ? 0.55 : 0.25).setStroke()
        NSBezierPath(rect: bounds.insetBy(dx: 0.5, dy: 0.5)).stroke()
    }

    override func mouseDown(with e: NSEvent) {
        if e.clickCount == 2 { pin.close(); return }
        pin.performDrag(with: e)
    }

    override func scrollWheel(with e: NSEvent) {
        let delta = e.hasPreciseScrollingDeltas ? e.scrollingDeltaY / 100 : e.scrollingDeltaY / 10
        if e.modifierFlags.contains(.option) {
            pin.alphaValue = min(1, max(0.2, pin.alphaValue + delta))
        } else {
            pin.zoom(by: 1 + delta)
        }
    }

    override func keyDown(with e: NSEvent) {
        let step: CGFloat = e.modifierFlags.contains(.shift) ? 10 : 1
        switch e.keyCode {
        case 53: pin.close()                       // Esc
        case 123: pin.nudge(dx: -step, dy: 0)      // arrows
        case 124: pin.nudge(dx: step, dy: 0)
        case 125: pin.nudge(dx: 0, dy: -step)
        case 126: pin.nudge(dx: 0, dy: step)
        default:
            switch e.charactersIgnoringModifiers?.lowercased() {
            case "r": pin.turn(.right)
            case "l": pin.turn(.left)
            case "h": pin.turn(.horizontal)
            case "v": pin.turn(.vertical)
            case "t": pin.clickThrough = true
            case "0": pin.actualSize()
            default: super.keyDown(with: e)
            }
        }
    }

    override func performKeyEquivalent(with e: NSEvent) -> Bool {
        guard e.modifierFlags.contains(.command) else { return false }
        switch e.charactersIgnoringModifiers?.lowercased() {
        case "c" where e.modifierFlags.contains(.shift): pin.copyText(); return true
        case "c": copyImage(); return true
        case "s": saveImage(); return true
        case "w": pin.close(); return true
        default: return false
        }
    }

    override func rightMouseDown(with e: NSEvent) {
        let menu = NSMenu()
        func item(_ title: String, _ key: String, _ action: Selector, _ mods: NSEvent.ModifierFlags = []) {
            let i = menu.addItem(withTitle: title, action: action, keyEquivalent: key)
            i.keyEquivalentModifierMask = mods
            i.target = self
        }
        item("Copy", "c", #selector(copyImage), .command)
        item("Copy Text", "c", #selector(copyText), [.command, .shift])
        item("Save…", "s", #selector(saveImage), .command)
        menu.addItem(.separator())
        item("Rotate Right", "r", #selector(rotateRight))
        item("Rotate Left", "l", #selector(rotateLeft))
        item("Flip Horizontally", "h", #selector(flipH))
        item("Flip Vertically", "v", #selector(flipV))
        item("Actual Size", "0", #selector(actualSize))
        item("Let Clicks Through", "t", #selector(passThrough))
        menu.addItem(.separator())
        item("Close", "w", #selector(closePin), .command)
        NSMenu.popUpContextMenu(menu, with: e, for: self)
    }

    @objc private func copyImage() {
        ImageActions.copy(pin.image, scale: pin.scale)
        HUD.show("Copied to clipboard")
    }

    @objc private func copyText() { pin.copyText() }
    @objc private func saveImage() { ImageActions.save(pin.image, scale: pin.scale) }
    @objc private func rotateRight() { pin.turn(.right) }
    @objc private func rotateLeft() { pin.turn(.left) }
    @objc private func flipH() { pin.turn(.horizontal) }
    @objc private func flipV() { pin.turn(.vertical) }
    @objc private func actualSize() { pin.actualSize() }
    @objc private func passThrough() { pin.clickThrough = true }
    @objc private func closePin() { pin.close() }
}
