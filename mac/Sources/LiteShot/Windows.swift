import AppKit
import SwiftUI

/// Keeps SwiftUI windows alive while they're open, and brings the app forward to show them (it lives in the menu bar).
@MainActor
enum Windows {
    private static var open: [NSWindow] = []

    @discardableResult
    static func show<Content: View>(_ title: String, size: CGSize, resizable: Bool = true, id: String? = nil, @ViewBuilder content: () -> Content) -> NSWindow {
        if let id, let existing = open.first(where: { $0.identifier?.rawValue == id }) {
            existing.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return existing
        }
        let window = NSWindow(contentRect: CGRect(origin: .zero, size: size),
                              styleMask: resizable ? [.titled, .closable, .miniaturizable, .resizable] : [.titled, .closable, .miniaturizable],
                              backing: .buffered, defer: false)
        window.title = title
        window.isReleasedWhenClosed = false
        if let id { window.identifier = NSUserInterfaceItemIdentifier(id) }
        window.contentViewController = NSHostingController(rootView: content())
        window.setContentSize(size)
        window.center()
        open.append(window)
        NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: window, queue: .main) { note in
            MainActor.assumeIsolated { open.removeAll { $0 === (note.object as? NSWindow) } }
        }
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        return window
    }
}
