import AppKit

/// A screenshot, big: it opens fitted to the window; a mouse wheel (or ⌘-scroll, or a pinch) zooms where the pointer is,
/// dragging moves it, double-click switches between fitted and actual size. + − 0 1 do the same from the keyboard,
/// ⌘C copies, Esc closes.
@MainActor
final class ImageViewer: NSObject, NSWindowDelegate {
    private static var open: [ImageViewer] = []

    let window: NSWindow
    private let scroll: ZoomScrollView

    @discardableResult
    static func show(_ image: CGImage, title: String = "Screenshot") -> ImageViewer {
        if let existing = open.first(where: { $0.scroll.image === image }) {
            existing.window.makeKeyAndOrderFront(nil)
            return existing
        }
        let viewer = ImageViewer(image, title: title)
        open.append(viewer)
        viewer.window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        viewer.scroll.fit()
        return viewer
    }

    /// Closes the viewer showing `image`, if there is one (when its chat closes).
    static func close(_ image: CGImage) { open.first { $0.scroll.image === image }?.window.close() }

    private init(_ image: CGImage, title: String) {
        let screen = NSScreen.main?.visibleFrame ?? CGRect(x: 0, y: 0, width: 1280, height: 800)
        let frame = CGRect(x: 0, y: 0, width: screen.width * 0.84, height: screen.height * 0.86)
        window = NSWindow(contentRect: frame, styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        window.title = title
        window.isReleasedWhenClosed = false
        window.minSize = CGSize(width: 360, height: 280)
        let scale = NSScreen.main?.backingScaleFactor ?? 2
        scroll = ZoomScrollView(image: image, scale: scale)
        super.init()

        let content = NSView(frame: frame)
        let hint = NSTextField(labelWithString: "Scroll or pinch to zoom · drag to move · double-click for actual size or fit · ⌘C copies · \(image.width) × \(image.height)")
        hint.font = .systemFont(ofSize: 12)
        hint.textColor = .secondaryLabelColor
        hint.alignment = .center
        hint.lineBreakMode = .byTruncatingTail
        hint.frame = CGRect(x: 0, y: 6, width: frame.width, height: 18)
        hint.autoresizingMask = [.width]
        scroll.frame = CGRect(x: 0, y: 30, width: frame.width, height: frame.height - 30)
        scroll.autoresizingMask = [.width, .height]
        content.addSubview(scroll)
        content.addSubview(hint)
        window.contentView = content
        window.delegate = self
        window.center()
        window.makeFirstResponder(scroll.documentView)
    }

    func windowWillClose(_ notification: Notification) {
        ImageViewer.open.removeAll { $0 === self }
    }

    func windowDidResize(_ notification: Notification) {
        if scroll.fitted { scroll.fit() }
    }
}

/// The scroll view: magnifies instead of scrolling for a mouse wheel or ⌘-scroll, centres a picture smaller than itself.
final class ZoomScrollView: NSScrollView {
    let image: CGImage
    private(set) var fitted = true

    init(image: CGImage, scale: CGFloat) {
        self.image = image
        super.init(frame: .zero)
        contentView = CenteringClipView()
        let size = CGSize(width: CGFloat(image.width) / scale, height: CGFloat(image.height) / scale)
        let picture = PictureView(frame: CGRect(origin: .zero, size: size))
        picture.image = NSImage(cgImage: image, size: size)
        picture.imageScaling = .scaleAxesIndependently
        documentView = picture
        hasVerticalScroller = true
        hasHorizontalScroller = true
        autohidesScrollers = true
        allowsMagnification = true
        minMagnification = 0.05
        maxMagnification = 16
        backgroundColor = NSColor.windowBackgroundColor.blended(withFraction: 0.04, of: .black) ?? .windowBackgroundColor
        drawsBackground = true
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    func fit() {
        guard let doc = documentView else { return }
        let size = contentSize
        let m = min((size.width - 24) / doc.frame.width, (size.height - 24) / doc.frame.height)
        magnification = max(minMagnification, min(m, maxMagnification))
        fitted = true
    }

    func zoom(by factor: CGFloat, at point: CGPoint? = nil) {
        let p = point ?? CGPoint(x: contentView.bounds.midX, y: contentView.bounds.midY)
        setMagnification(max(minMagnification, min(magnification * factor, maxMagnification)), centeredAt: p)
        fitted = false
    }

    func actual(at point: CGPoint? = nil) {
        let p = point ?? CGPoint(x: contentView.bounds.midX, y: contentView.bounds.midY)
        setMagnification(1, centeredAt: p)
        fitted = false
    }

    override func scrollWheel(with event: NSEvent) {
        // A mouse wheel zooms, like the Windows viewer; a trackpad's two-finger scroll moves the picture.
        if event.modifierFlags.contains(.command) || !event.hasPreciseScrollingDeltas {
            guard event.scrollingDeltaY != 0 else { return }
            let p = contentView.convert(event.locationInWindow, from: nil)
            zoom(by: event.scrollingDeltaY > 0 ? 1.2 : 1 / 1.2, at: p)
        } else {
            super.scrollWheel(with: event)
        }
    }

    override func magnify(with event: NSEvent) {
        super.magnify(with: event)
        fitted = false
    }
}

/// Keeps a picture smaller than the window in the middle, instead of the bottom-left corner.
final class CenteringClipView: NSClipView {
    override func constrainBoundsRect(_ proposed: NSRect) -> NSRect {
        var r = super.constrainBoundsRect(proposed)
        guard let doc = documentView else { return r }
        if r.width > doc.frame.width { r.origin.x = (doc.frame.width - r.width) / 2 }
        if r.height > doc.frame.height { r.origin.y = (doc.frame.height - r.height) / 2 }
        return r
    }
}

/// The picture itself: drag to move, double-click to switch between fitted and actual size, and the keys.
final class PictureView: NSImageView {
    private var dragFrom: CGPoint?
    private var scroll: ZoomScrollView? { enclosingScrollView as? ZoomScrollView }

    override var acceptsFirstResponder: Bool { true }
    override var mouseDownCanMoveWindow: Bool { false }

    override func resetCursorRects() { addCursorRect(visibleRect, cursor: .openHand) }

    override func mouseDown(with event: NSEvent) {
        if event.clickCount == 2, let scroll {
            let p = convert(event.locationInWindow, from: nil)
            if scroll.fitted { scroll.actual(at: p) } else { scroll.fit() }
            return
        }
        dragFrom = event.locationInWindow
        NSCursor.closedHand.push()
    }

    override func mouseDragged(with event: NSEvent) {
        guard let from = dragFrom, let clip = enclosingScrollView?.contentView else { return }
        let m = enclosingScrollView?.magnification ?? 1
        var origin = clip.bounds.origin
        origin.x -= (event.locationInWindow.x - from.x) / m
        origin.y -= (event.locationInWindow.y - from.y) / m
        clip.scroll(to: clip.constrainBoundsRect(CGRect(origin: origin, size: clip.bounds.size)).origin)
        enclosingScrollView?.reflectScrolledClipView(clip)
        dragFrom = event.locationInWindow
    }

    override func mouseUp(with event: NSEvent) {
        if dragFrom != nil { NSCursor.pop() }
        dragFrom = nil
    }

    override func keyDown(with event: NSEvent) {
        guard let scroll else { return super.keyDown(with: event) }
        if event.modifierFlags.contains(.command), event.charactersIgnoringModifiers == "c" {
            if let image { NSPasteboard.general.clearContents(); NSPasteboard.general.writeObjects([image]) }
            return
        }
        switch event.charactersIgnoringModifiers {
        case "+", "=": scroll.zoom(by: 1.25)
        case "-", "_": scroll.zoom(by: 0.8)
        case "0": scroll.fit()
        case "1": scroll.actual()
        default:
            if event.keyCode == 53 { window?.close() } else { super.keyDown(with: event) }
        }
    }
}
