import AppKit
import ScreenCaptureKit

/// One press of the shortcut: every screen frozen under its own overlay window until the capture is done.
@MainActor
final class CaptureSession {
    private var windows: [OverlayWindow] = []
    private let onClose: () -> Void

    private init(onClose: @escaping () -> Void) { self.onClose = onClose }

    static func start(onClose: @escaping () -> Void) async throws -> CaptureSession {
        let shots = try await freezeScreens()
        let session = CaptureSession(onClose: onClose)
        let windowRects = visibleWindows()
        for (screen, image) in shots {
            let w = OverlayWindow(screen: screen, image: image, windows: windowRects, session: session)
            session.windows.append(w)
        }
        NSApp.activate(ignoringOtherApps: true)
        for w in session.windows { w.orderFrontRegardless() }
        // The screen under the pointer gets the keyboard.
        let mouse = NSEvent.mouseLocation
        (session.windows.first { $0.frame.contains(mouse) } ?? session.windows.first)?.makeKeyAndOrderFront(nil)
        return session
    }

    /// One image per screen at its full pixel resolution, taken before any overlay is shown.
    private static func freezeScreens() async throws -> [(NSScreen, CGImage)] {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        var shots: [(NSScreen, CGImage)] = []
        for screen in NSScreen.screens {
            guard let id = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID,
                  let display = content.displays.first(where: { $0.displayID == id }) else { continue }
            let config = SCStreamConfiguration()
            config.width = Int(screen.frame.width * screen.backingScaleFactor)
            config.height = Int(screen.frame.height * screen.backingScaleFactor)
            config.showsCursor = false
            config.captureResolution = .best
            let filter = SCContentFilter(display: display, excludingWindows: [])
            let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
            shots.append((screen, image))
        }
        if shots.isEmpty { throw CaptureError.noScreens }
        return shots
    }

    /// Other apps' windows, front to back, in global top-left coordinates, for click-to-capture-a-window.
    private static func visibleWindows() -> [CGRect] {
        guard let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]]
        else { return [] }
        let me = ProcessInfo.processInfo.processIdentifier
        return list.compactMap { info in
            guard (info[kCGWindowLayer as String] as? Int) == 0,
                  (info[kCGWindowOwnerPID as String] as? Int32) != me,
                  let bounds = info[kCGWindowBounds as String] as? NSDictionary,
                  let rect = CGRect(dictionaryRepresentation: bounds),
                  rect.width > 40, rect.height > 40 else { return nil }
            return rect
        }
    }

    /// A selection started on one screen clears any on the others.
    func began(on window: OverlayWindow) {
        for w in windows where w !== window { w.overlay.clearSelection() }
    }

    func finish() {
        for w in windows { w.orderOut(nil) }
        windows.removeAll()
        onClose()
    }

    static func explain(_ error: Error) {
        let alert = NSAlert()
        alert.messageText = "LiteShot can't see the screen yet"
        alert.informativeText = "Allow LiteShot under System Settings › Privacy & Security › Screen & System Audio Recording, then try again.\n\n(\(error.localizedDescription))"
        alert.addButton(withTitle: "Open Settings")
        alert.addButton(withTitle: "Cancel")
        NSApp.activate(ignoringOtherApps: true)
        if alert.runModal() == .alertFirstButtonReturn,
           let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") {
            NSWorkspace.shared.open(url)
        }
    }

    enum CaptureError: LocalizedError {
        case noScreens
        var errorDescription: String? { "No screen could be captured." }
    }
}

/// A borderless window over one screen, above everything, holding that screen's frozen image.
final class OverlayWindow: NSWindow {
    let overlay: OverlayView

    @MainActor
    init(screen: NSScreen, image: CGImage, windows: [CGRect], session: CaptureSession) {
        // Global window rects are top-left based on the primary screen; make them local to this screen's view.
        let primaryHeight = NSScreen.screens.first?.frame.maxY ?? screen.frame.maxY
        let top = primaryHeight - screen.frame.maxY
        let local = windows.map { $0.offsetBy(dx: -screen.frame.minX, dy: -top) }
            .filter { $0.intersects(CGRect(origin: .zero, size: screen.frame.size)) }
        overlay = OverlayView(frame: CGRect(origin: .zero, size: screen.frame.size), image: image, windows: local)
        super.init(contentRect: screen.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        overlay.session = session
        overlay.host = self
        setFrame(screen.frame, display: false)
        level = .screenSaver
        isOpaque = true
        backgroundColor = .black
        hasShadow = false
        isReleasedWhenClosed = false
        acceptsMouseMovedEvents = true
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        contentView = overlay
        makeFirstResponder(overlay)
    }

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}
