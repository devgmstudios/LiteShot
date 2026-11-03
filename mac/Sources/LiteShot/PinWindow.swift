import AppKit

/// A screenshot floating above every window. Drag it anywhere, drag an edge to resize, scroll to zoom,
/// ⌥-scroll to fade, double-click or Esc to close, right-click for Copy and Save.
final class PinWindow: NSWindow {
    private static var pins: [PinWindow] = []
    let image: CGImage
    let scale: CGFloat

    static func open(_ image: CGImage, frame: CGRect) {
        let pin = PinWindow(image: image, frame: frame)
        pins.append(pin)
        pin.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
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

    /// Zooms around the top-left corner, keeping the shape.
    func zoom(by factor: CGFloat) {
        let ratio = frame.height / frame.width
        let w = min(max(frame.width * factor, minSize.width), CGFloat(image.width) / scale * 6)
        let h = w * ratio
        setFrame(CGRect(x: frame.minX, y: frame.maxY - h, width: w, height: h), display: true)
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
        if e.keyCode == 53 { pin.close() } else { super.keyDown(with: e) }
    }

    override func performKeyEquivalent(with e: NSEvent) -> Bool {
        guard e.modifierFlags.contains(.command) else { return false }
        switch e.charactersIgnoringModifiers?.lowercased() {
        case "c": copyImage(); return true
        case "s": saveImage(); return true
        case "w": pin.close(); return true
        default: return false
        }
    }

    override func rightMouseDown(with e: NSEvent) {
        let menu = NSMenu()
        menu.addItem(withTitle: "Copy", action: #selector(copyImage), keyEquivalent: "c").target = self
        menu.addItem(withTitle: "Save…", action: #selector(saveImage), keyEquivalent: "s").target = self
        menu.addItem(.separator())
        menu.addItem(withTitle: "Actual Size", action: #selector(actualSize), keyEquivalent: "0").target = self
        menu.addItem(withTitle: "Close", action: #selector(closePin), keyEquivalent: "w").target = self
        NSMenu.popUpContextMenu(menu, with: e, for: self)
    }

    @objc private func copyImage() {
        ImageActions.copy(pin.image, scale: pin.scale)
        HUD.show("Copied to clipboard")
    }

    @objc private func saveImage() { ImageActions.save(pin.image, scale: pin.scale) }
    @objc private func closePin() { pin.close() }
    @objc private func actualSize() { pin.zoom(by: CGFloat(pin.image.width) / pin.scale / pin.frame.width) }
}
