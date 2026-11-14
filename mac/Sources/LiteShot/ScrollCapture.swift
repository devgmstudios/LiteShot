import AppKit
import ApplicationServices
import LiteShotCore
import ScreenCaptureKit
import SwiftUI

/// Scrolling capture (S): turns the wheel over an area of the screen, grabs a frame each time the page comes to rest
/// and stitches the frames into one tall image (`ScrollStitcher`), as on Windows. It stops at the end of the page, at
/// the height limit, or on Esc or a click, which are held back from the page so the click can't follow a link.
/// macOS lets an app turn the wheel and hold back clicks only once it's allowed under Accessibility.
@MainActor
enum ScrollCapture {
    private static var stopped = false
    private static var tap: CFMachPort?

    /// `area` is in AppKit's global coordinates (origin at the bottom left of the main screen).
    static func run(area: CGRect, screen: NSScreen) async {
        guard AXIsProcessTrustedWithOptions([kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary) else {
            let alert = NSAlert()
            alert.messageText = "Allow LiteShot to scroll for you"
            alert.informativeText = "Scrolling capture turns the mouse wheel for you. Allow LiteShot under System Settings › Privacy & Security › Accessibility, then try again."
            alert.addButton(withTitle: "Open Settings")
            alert.addButton(withTitle: "Cancel")
            NSApp.activate(ignoringOtherApps: true)
            if alert.runModal() == .alertFirstButtonReturn,
               let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
                NSWorkspace.shared.open(url)
            }
            return
        }
        guard let id = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID,
              let content = try? await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true),
              let display = content.displays.first(where: { $0.displayID == id }) else {
            HUD.show("Couldn't capture this screen")
            return
        }
        let scale = screen.backingScaleFactor
        // The area within the display, top-left origin, in points; and its middle in global top-left coordinates for the wheel.
        let local = CGRect(x: area.minX - screen.frame.minX, y: screen.frame.maxY - area.maxY, width: area.width, height: area.height)
        let primaryTop = NSScreen.screens.first?.frame.maxY ?? screen.frame.maxY
        let center = CGPoint(x: area.midX, y: primaryTop - area.midY)
        let home = CGEvent(source: nil)?.location
        let stitcher = ScrollStitcher()
        let pill = ScrollPill(near: area, on: screen)

        func grab() async -> CGImage? {
            let config = SCStreamConfiguration()
            config.sourceRect = local
            config.width = Int(local.width * scale)
            config.height = Int(local.height * scale)
            config.showsCursor = false
            config.captureResolution = .best
            return try? await SCScreenshotManager.captureImage(contentFilter: SCContentFilter(display: display, excludingWindows: []), configuration: config)
        }

        stopped = false
        startHoldingInput()
        defer {
            stopHoldingInput()
            pill.close()
            if let home { CGWarpMouseCursorPosition(home) }
        }
        try? await Task.sleep(nanoseconds: 250_000_000)   // the overlay is off the screen
        CGWarpMouseCursorPosition(center)
        pill.orderFrontRegardless()
        try? await Task.sleep(nanoseconds: 150_000_000)
        guard var frame = await grab() else { HUD.show("Couldn't capture this screen"); return }
        _ = stitcher.add(frame)

        var notches = 1
        var perNotch: CGFloat = 0
        var note: String?
        while !stopped {
            let expected = perNotch > 0 ? Int(perNotch * CGFloat(notches)) : 0
            wheel(at: center, notches: -notches)
            var next = await settle(after: frame, grab: grab)
            var step = next.map { stitcher.add($0, expectedShift: expected) } ?? .lost
            if step == .same, !stopped {
                // Pages that load more as you scroll need a moment, and another turn of the wheel.
                try? await Task.sleep(nanoseconds: 400_000_000)
                wheel(at: center, notches: -notches)
                next = await settle(after: frame, grab: grab)
                step = next.map { stitcher.add($0, expectedShift: expected) } ?? .lost
            }
            if step == .lost, notches > 1, !stopped {
                // Scrolled past the last frame's bottom: go back and take smaller steps.
                wheel(at: center, notches: notches)
                _ = await settle(after: frame, grab: grab)
                notches = max(1, notches / 2)
                continue
            }
            if let next { frame = next }
            if step == .added || step == .full {
                perNotch = CGFloat(stitcher.lastShift) / CGFloat(notches)
                let moving = CGFloat(frame.height - stitcher.header - stitcher.footer)
                notches = max(1, min(12, Int(moving * 0.6 / max(1, perNotch))))
                pill.set(height: stitcher.stitchedHeight)
            }
            if step == .full { note = "Stopped at the \(stitcher.maxHeight.formatted()) px limit"; break }
            if step == .same { note = stitcher.frames > 1 ? nil : "This area didn't scroll"; break }
            if step == .lost { note = "Stopped where the page changed as it scrolled"; break }
        }
        if note == nil, stopped { note = "Stopped" }
        guard let page = stitcher.compose() else { return }
        ScrollResult.open(page, scale: scale, note: note)
    }

    /// Grabs frames until two in a row match, after the page has moved away from `before`.
    private static func settle(after before: CGImage, grab: () async -> CGImage?) async -> CGImage? {
        let started = Date()
        guard var last = await grab() else { return nil }
        var moved = !ScrollStitcher.samePixels(last, before)
        while Date().timeIntervalSince(started) < 1.5, !stopped {
            try? await Task.sleep(nanoseconds: 40_000_000)
            guard let next = await grab() else { break }
            let still = ScrollStitcher.samePixels(next, last)
            moved = moved || !ScrollStitcher.samePixels(next, before)
            last = next
            if still, moved || Date().timeIntervalSince(started) > 0.35 { break }
        }
        return last
    }

    /// Turns the wheel over `point` (global top-left coordinates): negative notches scroll down.
    private static func wheel(at point: CGPoint, notches: Int) {
        CGWarpMouseCursorPosition(point)
        for _ in 0..<abs(notches) {
            guard let e = CGEvent(scrollWheelEvent2Source: nil, units: .line, wheelCount: 1, wheel1: Int32(notches > 0 ? 3 : -3), wheel2: 0, wheel3: 0) else { continue }
            e.location = point
            e.post(tap: .cghidEventTap)
        }
    }

    /// Clicks and keys go nowhere while it scrolls; any of them (Esc included) stops it.
    private static func startHoldingInput() {
        let kinds: [CGEventType] = [.leftMouseDown, .rightMouseDown, .otherMouseDown, .keyDown]
        let mask = kinds.reduce(CGEventMask(0)) { $0 | (CGEventMask(1) << $1.rawValue) }
        tap = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap, options: .defaultTap, eventsOfInterest: mask,
                                callback: { _, type, event, _ in
                                    if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput { return Unmanaged.passUnretained(event) }
                                    DispatchQueue.main.async { ScrollCapture.stopped = true }
                                    return nil
                                }, userInfo: nil)
        if let tap, let source = CFMachPortCreateRunLoopSource(nil, tap, 0) {
            CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
            CGEvent.tapEnable(tap: tap, enable: true)
        }
    }

    private static func stopHoldingInput() {
        if let tap { CGEvent.tapEnable(tap: tap, enable: false); CFMachPortInvalidate(tap) }
        tap = nil
    }
}

/// "Scrolling… Esc or click to stop": a small pill beside the area being captured, outside it so it's never in a frame.
final class ScrollPill: NSPanel {
    private let label = NSTextField(labelWithString: "Scrolling…  Esc or click to stop")

    init(near area: CGRect, on screen: NSScreen) {
        super.init(contentRect: CGRect(x: 0, y: 0, width: 330, height: 34), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        level = .statusBar
        isOpaque = false
        backgroundColor = .clear
        hasShadow = true
        ignoresMouseEvents = true
        let bg = NSView(frame: CGRect(x: 0, y: 0, width: 330, height: 34))
        bg.wantsLayer = true
        bg.layer?.backgroundColor = NSColor(white: 0.12, alpha: 0.92).cgColor
        bg.layer?.cornerRadius = 17
        label.font = .systemFont(ofSize: 13, weight: .semibold)
        label.textColor = .white
        label.frame = CGRect(x: 16, y: 8, width: 300, height: 18)
        bg.addSubview(label)
        contentView = bg
        // Above the area if there's room on its screen, else below it.
        let f = screen.visibleFrame
        let x = min(max(area.midX - 165, f.minX + 8), f.maxX - 338)
        let y = area.maxY + 10 + 34 <= f.maxY ? area.maxY + 10 : max(f.minY + 8, area.minY - 44)
        setFrameOrigin(CGPoint(x: x, y: y))
    }

    func set(height: Int) { label.stringValue = "Scrolling…  \(height.formatted()) px  ·  Esc or click to stop" }
}

/// The stitched page: scroll through it, then copy, save, upload or ask AI about it.
enum ScrollResult {
    @MainActor
    static func open(_ page: CGImage, scale: CGFloat, note: String?) {
        let size = CGSize(width: min(900, CGFloat(page.width) / scale + 40), height: 760)
        Windows.show("Scrolling capture", size: size) { ScrollResultView(page: page, scale: scale, note: note) }
    }
}

struct ScrollResultView: View {
    let page: CGImage
    let scale: CGFloat
    let note: String?

    var body: some View {
        VStack(spacing: 0) {
            ScrollView {
                Image(nsImage: NSImage(cgImage: page, size: CGSize(width: CGFloat(page.width) / scale, height: CGFloat(page.height) / scale)))
                    .resizable().scaledToFit()
                    .padding(16)
            }
            Divider()
            HStack {
                Text("\(page.width) × \(page.height) px" + (note.map { " · \($0)" } ?? "")).font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("Copy") { ImageActions.copy(page, scale: scale); HUD.show("Copied to clipboard") }
                Button("Save…") { ImageActions.save(page, scale: scale) }
                Button("Upload") { Uploads.send(page, kind: .link) }
                Button("Ask AI") { AskAIChat.open(page) }
            }
            .padding(12)
        }
        .frame(minWidth: 480, minHeight: 400)
    }
}
