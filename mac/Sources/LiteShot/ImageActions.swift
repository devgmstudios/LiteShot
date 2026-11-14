import AppKit
import LiteShotCore
import UniformTypeIdentifiers

/// Copy and save, shared by the editor and pinned screenshots.
enum ImageActions {
    static func copy(_ image: CGImage, scale: CGFloat) {
        let pb = NSPasteboard.general
        pb.clearContents()
        let size = CGSize(width: CGFloat(image.width) / scale, height: CGFloat(image.height) / scale)
        pb.writeObjects([NSImage(cgImage: image, size: size)])
        if let png = Renderer.png(image) { pb.setData(png, forType: .png) }
    }

    /// Prints the screenshot, fitted to the page and centred, landscape when it's wider than tall.
    static func print(_ image: CGImage, scale: CGFloat) {
        let size = CGSize(width: CGFloat(image.width) / scale, height: CGFloat(image.height) / scale)
        let view = NSImageView(frame: CGRect(origin: .zero, size: size))
        view.image = NSImage(cgImage: image, size: size)
        view.imageScaling = .scaleProportionallyUpOrDown
        let info = (NSPrintInfo.shared.copy() as? NSPrintInfo) ?? NSPrintInfo()
        info.horizontalPagination = .fit
        info.verticalPagination = .fit
        info.isHorizontallyCentered = true
        info.isVerticallyCentered = true
        info.orientation = size.width > size.height ? .landscape : .portrait
        NSApp.activate(ignoringOtherApps: true)
        NSPrintOperation(view: view, printInfo: info).run()
    }

    static func save(_ image: CGImage, scale: CGFloat) {
        let panel = NSSavePanel()
        let stamp = DateFormatter()
        stamp.dateFormat = "yyyy-MM-dd 'at' HH.mm.ss"
        panel.nameFieldStringValue = "LiteShot \(stamp.string(from: Date())).png"
        panel.directoryURL = FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask).first
        panel.allowedContentTypes = [.png, .jpeg]
        panel.isExtensionHidden = false
        NSApp.activate(ignoringOtherApps: true)
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let rep = NSBitmapImageRep(cgImage: image)
        rep.size = CGSize(width: CGFloat(image.width) / scale, height: CGFloat(image.height) / scale)
        let jpeg = ["jpg", "jpeg"].contains(url.pathExtension.lowercased())
        guard let data = rep.representation(using: jpeg ? .jpeg : .png, properties: jpeg ? [.compressionFactor: 0.92] : [:]) else { return }
        do { try data.write(to: url) } catch { HUD.show("Couldn't save: \(error.localizedDescription)") }
    }
}

/// A short message near the bottom of the screen: "Copied to clipboard".
enum HUD {
    private static var panel: NSPanel?

    static func show(_ text: String) {
        panel?.orderOut(nil)
        let label = NSTextField(labelWithString: text)
        label.font = .systemFont(ofSize: Settings.brightMode ? 16 : 14, weight: .semibold)
        label.textColor = .white
        let size = label.fittingSize
        let frame = CGRect(x: 0, y: 0, width: size.width + 36, height: size.height + 20)
        let p = NSPanel(contentRect: frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        p.level = .statusBar
        p.isOpaque = false
        p.backgroundColor = .clear
        p.hasShadow = true
        p.ignoresMouseEvents = true
        let bg = NSView(frame: frame)
        bg.wantsLayer = true
        bg.layer?.backgroundColor = (Settings.brightMode ? NSColor.black : NSColor(white: 0.1, alpha: 0.9)).cgColor
        bg.layer?.cornerRadius = frame.height / 2
        if Settings.brightMode {
            bg.layer?.borderColor = NSColor.white.cgColor
            bg.layer?.borderWidth = 2
        }
        label.frame = CGRect(x: 18, y: 10, width: size.width, height: size.height)
        bg.addSubview(label)
        p.contentView = bg
        if let screen = NSScreen.main {
            let f = screen.visibleFrame
            p.setFrameOrigin(CGPoint(x: f.midX - frame.width / 2, y: f.minY + 90))
        }
        p.orderFrontRegardless()
        panel = p
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.6) {
            if panel === p { p.orderOut(nil); panel = nil }
        }
    }
}
