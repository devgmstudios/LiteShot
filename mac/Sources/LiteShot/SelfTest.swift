import AppKit
import SwiftUI
import LiteShotCore

/// `LiteShot --render-shots <folder>`: the real overlay on a made-up screen, drawn to PNGs in each look, with
/// checks that the toolbars fit. Lets a build machine show what the UI looks like. Returns false on a failed check.
enum SelfTest {
    static func renderShots(to folder: URL) -> Bool {
        _ = NSApplication.shared
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let saved = (LiteShot.Settings.brightMode, LiteShot.Settings.buttonNames)
        defer { LiteShot.Settings.brightMode = saved.0; LiteShot.Settings.buttonNames = saved.1 }

        let size = CGSize(width: 1440, height: 900), scale: CGFloat = 2
        let window = CGRect(x: 180, y: 120, width: 820, height: 560)
        guard let screen = desktop(size: size, scale: scale, window: window) else { print("FAIL could not draw the desktop"); return false }
        var ok = true
        func check(_ pass: Bool, _ what: String) {
            print(pass ? "PASS  \(what)" : "FAIL  \(what)")
            if !pass { ok = false }
        }

        // Hovering a window outlines it.
        let hovering = OverlayView(frame: CGRect(origin: .zero, size: size), image: screen, windows: [window])
        hovering.hoverWindow = window
        save(hovering, folder.appendingPathComponent("mac-1-hover.png"))

        let looks: [(name: String, bright: Bool, names: Bool)] = [("glass", false, false), ("names", false, true), ("bright", true, true)]
        for look in looks {
            LiteShot.Settings.brightMode = look.bright
            LiteShot.Settings.buttonNames = look.names
            let view = OverlayView(frame: CGRect(origin: .zero, size: size), image: screen, windows: [window])
            let sel = CGRect(x: window.minX + 30, y: window.minY + 50, width: 640, height: 420)
            view.select(sel)
            view.color = Palette.colors[1]
            view.add(Annotation(.marker([CGPoint(x: sel.minX + 24, y: sel.minY + 112), CGPoint(x: sel.minX + 330, y: sel.minY + 112)]),
                                color: Palette.colors[2], width: 3))
            view.add(Annotation(.arrow(CGPoint(x: sel.minX + 560, y: sel.minY + 300), CGPoint(x: sel.minX + 380, y: sel.minY + 170)),
                                color: Palette.colors[1], width: 4))
            view.add(Annotation(.counter(1, at: CGPoint(x: sel.minX + 560, y: sel.minY + 300)), color: Palette.colors[1], width: 3))
            view.add(Annotation(.pixelate(CGRect(x: sel.minX + 150, y: sel.minY + 196, width: 230, height: 26), oval: false), color: .black, width: 3))
            view.add(Annotation(.text("Ship it Friday", at: CGPoint(x: sel.minX + 40, y: sel.minY + 330), size: 28), color: Palette.colors[0], width: 3))
            view.tool = .arrow
            view.layoutBars()
            if !look.names { view.hoverButton(view.tools, group: 0, index: 2) }
            save(view, folder.appendingPathComponent("mac-2-\(look.name).png"))

            let bounds = CGRect(origin: .zero, size: size)
            check(bounds.contains(view.tools.frame) && bounds.contains(view.actions.frame), "\(look.name): toolbars on screen")
            check(!view.tools.frame.intersects(view.actions.frame), "\(look.name): toolbars don't overlap")
            let frames = view.tools.buttonFrames.flatMap { $0 } + view.actions.buttonFrames.flatMap { $0 }
            let overlap = frames.enumerated().contains { i, a in frames.enumerated().contains { j, b in i != j && a.intersection(b).width > 0.5 && a.intersection(b).height > 0.5 } }
            check(!overlap, "\(look.name): no two buttons overlap")
            if let font = view.tools.labelFont {
                let cramped = (view.tools.groups.flatMap { $0 } + view.actions.groups.flatMap { $0 }).enumerated()
                    .filter { i, b in (b.label as NSString).size(withAttributes: [.font: font]).width > frames[i].width - 4 }
                    .map { $0.element.label }
                check(cramped.isEmpty, "\(look.name): every name fits its button \(cramped)")
            }
            if let out = view.renderResult() {
                check(out.width == Int(sel.width * scale) && out.height == Int(sel.height * scale), "\(look.name): the result is full resolution (\(out.width)×\(out.height))")
                if look.name == "glass", let png = Renderer.png(out) { try? png.write(to: folder.appendingPathComponent("mac-3-result.png")) }
            } else {
                check(false, "\(look.name): the result renders")
            }
        }
        // Find on screen, for real: Vision reads the report, "year" lights up twice, Highlight marks both.
        LiteShot.Settings.brightMode = false
        LiteShot.Settings.buttonNames = false
        let finding = OverlayView(frame: CGRect(origin: .zero, size: size), image: screen, windows: [window])
        let area = CGRect(x: window.minX + 20, y: window.minY + 50, width: 760, height: 420)
        finding.select(area)
        finding.layoutBars()
        finding.findBar.scanNow("year")
        let years = finding.findBar.hits
        check(years.count == 2, "find: \"year\" lights up twice (\(years.count))")
        check(years.allSatisfy { area.contains($0) }, "find: matches are inside the selection")
        check(years.count == 2 && years[0].minX < years[1].minX, "find: matches are in reading order")
        check(finding.findBar.status == (years.isEmpty ? "No matches" : "1 of \(years.count)"), "find: the bar counts them (\(finding.findBar.status))")
        finding.findBar.step(1)
        check(years.count < 2 || finding.findBar.index == 1, "find: Return goes to the next match")
        save(finding, folder.appendingPathComponent("mac-4-find.png"))
        finding.findBar.scanNow("GLASS interface")
        check(finding.findBar.hits.count == 1, "find: a phrase in any case (\(finding.findBar.hits.count))")
        finding.findBar.scanNow("zebra crossing")
        check(finding.findBar.hits.isEmpty && finding.findBar.status == "No matches", "find: says when nothing matches")
        finding.findBar.scanNow("year")
        let before = finding.annotations.count
        finding.findBar.highlight()
        check(finding.annotations.count == before + 2 && !finding.findBar.isOpen, "find: Highlight marks both and closes the bar")
        if let out = finding.renderResult(), let png = Renderer.png(out) { try? png.write(to: folder.appendingPathComponent("mac-5-find-highlighted.png")) }

        // Related words: no "fish" on the menu, so its salmon, trout and tuna light up in blue.
        check(RelatedWords.shared != nil, "find: the related-words table is in the app")
        if let menu = desktop(size: size, scale: scale, window: window, lines: ["Tonight: grilled salmon and trout tacos.", "Lunch: tuna salad with lemon."]) {
            let menuView = OverlayView(frame: CGRect(origin: .zero, size: size), image: menu, windows: [window])
            menuView.select(area)
            menuView.layoutBars()
            menuView.findBar.scanNow("fish")
            check(menuView.findBar.hits.count == 3 && menuView.findBar.related && menuView.findBar.status == "1 of 3 related",
                  "find: \"fish\" lights up salmon, trout and tuna (\(menuView.findBar.hits.count), \(menuView.findBar.status))")
            save(menuView, folder.appendingPathComponent("mac-6-find-related.png"))
            menuView.findBar.scanNow("salmon")
            check(menuView.findBar.hits.count == 1 && !menuView.findBar.related, "find: an exact match wins over related words")
        }
        // Editing drawings: pick one by clicking its stroke, delete it, undo brings it back.
        do {
            let editing = OverlayView(frame: CGRect(origin: .zero, size: size), image: screen, windows: [window])
            editing.select(CGRect(x: 100, y: 100, width: 800, height: 600))
            editing.add(Annotation(.rect(CGRect(x: 200, y: 200, width: 300, height: 150)), color: Palette.colors[0], width: 4))
            editing.add(Annotation(.arrow(CGPoint(x: 600, y: 500), CGPoint(x: 700, y: 300)), color: Palette.colors[1], width: 4))
            editing.pick(at: CGPoint(x: 201, y: 260))
            check(editing.selectedShape == 0, "edit: clicking a box's edge picks it")
            editing.pick(at: CGPoint(x: 350, y: 270))
            check(editing.selectedShape == nil, "edit: inside the box is free for other clicks")
            editing.pick(at: CGPoint(x: 650, y: 400))
            check(editing.selectedShape == 1, "edit: clicking the arrow picks it")
            save(editing, folder.appendingPathComponent("mac-10-picked.png"))
            editing.deleteSelectedShape()
            check(editing.annotations.count == 1, "edit: Delete removes the picked drawing")
            editing.undo()
            check(editing.annotations.count == 2, "edit: undo brings it back")
        }

        // The magnifier, and the color under the pointer: the document window is white.
        do {
            LiteShot.Settings.showMagnifier = true
            defer { LiteShot.Settings.showMagnifier = false }
            let loupe = OverlayView(frame: CGRect(origin: .zero, size: size), image: screen, windows: [window])
            loupe.hover(at: CGPoint(x: window.minX + 400, y: window.minY + 420))
            check(loupe.pixelColor(at: CGPoint(x: window.minX + 400, y: window.minY + 420)).map(OverlayView.hex) == "#FFFFFF", "loupe: reads the white page as #FFFFFF")
            save(loupe, folder.appendingPathComponent("mac-11-magnifier.png"))
        }

        // Pins turn: a quarter turn swaps width and height, a flip keeps them.
        if let wide = desktop(size: CGSize(width: 40, height: 20), scale: 1, window: CGRect(x: 2, y: 2, width: 30, height: 10)) {
            let r = PinWindow.turned(wide, .right), f = PinWindow.turned(wide, .horizontal)
            check(r?.width == 20 && r?.height == 40 && f?.width == 40 && f?.height == 20, "pin: rotating swaps the sides, flipping doesn't")
        }

        // The new windows, drawn for a look: settings, the Ask AI chat and Edit with AI.
        MainActor.assumeIsolated {
            snapshot(SettingsView(), size: CGSize(width: 560, height: 900), to: folder.appendingPathComponent("mac-7-settings.png"))
            if let report = finding.renderResult() {
                let chat = AskAIChat(image: report, key: { nil })
                chat.show("What's in this screenshot?", answer: "It's a **quarterly report**:\n- Revenue grew 24% year over year\n- Customer satisfaction reached 92%")
                check(chat.messages.count == 2 && chat.messages[1].text.hasPrefix("It's a quarterly report"), "ask: the chat shows the question and a plain answer")
                snapshot(AskAIView(chat: chat), size: CGSize(width: 640, height: 720), to: folder.appendingPathComponent("mac-8-ask-ai.png"))
                chat.ask("Hello?")
                check(chat.messages.count == 2 && chat.status.contains("SyncNode API key"), "ask: without a key nothing is sent")
                let run = AIRun(kind: .edit, image: report, scale: scale)
                run.prompt = "Remove the background"
                snapshot(AIRunView(run: run), size: CGSize(width: 620, height: 640), to: folder.appendingPathComponent("mac-9-edit-with-ai.png"))
            }
        }
        return ok
    }

    /// A SwiftUI view drawn to a PNG, in an offscreen window so its AppKit controls have somewhere to draw.
    @MainActor
    private static func snapshot<V: View>(_ view: V, size: CGSize, to url: URL) {
        let host = NSHostingView(rootView: view.frame(width: size.width, height: size.height))
        host.frame = CGRect(origin: .zero, size: size)
        let window = NSWindow(contentRect: host.frame, styleMask: [.titled], backing: .buffered, defer: false)
        window.contentView = host
        host.layoutSubtreeIfNeeded()
        save(host, url)
    }

    private static func save(_ view: NSView, _ url: URL) {
        guard let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return }
        view.cacheDisplay(in: view.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: url)
    }

    /// A wallpaper-ish gradient with a document window on it, at `scale` pixels per point.
    private static func desktop(size: CGSize, scale: CGFloat, window: CGRect, lines custom: [String]? = nil) -> CGImage? {
        let w = Int(size.width * scale), h = Int(size.height * scale)
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)
        else { return nil }
        ctx.translateBy(x: 0, y: CGFloat(h))
        ctx.scaleBy(x: scale, y: -scale)
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: true)
        NSGradient(colors: [NSColor(srgbRed: 0.1, green: 0.1, blue: 0.36, alpha: 1), NSColor(srgbRed: 0.48, green: 0.24, blue: 0.62, alpha: 1),
                            NSColor(srgbRed: 0.94, green: 0.54, blue: 0.36, alpha: 1)])?.draw(in: CGRect(origin: .zero, size: size), angle: -35)
        NSColor(white: 0, alpha: 0.25).setFill()
        NSBezierPath(roundedRect: window.offsetBy(dx: 0, dy: 10), xRadius: 12, yRadius: 12).fill()
        NSColor.white.setFill()
        NSBezierPath(roundedRect: window, xRadius: 12, yRadius: 12).fill()
        NSColor(white: 0.95, alpha: 1).setFill()
        NSBezierPath(rect: CGRect(x: window.minX, y: window.minY + 12, width: window.width, height: 28)).fill()
        for (i, c) in [NSColor.systemRed, .systemYellow, .systemGreen].enumerated() {
            c.setFill()
            NSBezierPath(ovalIn: CGRect(x: window.minX + 14 + CGFloat(i) * 20, y: window.minY + 14, width: 12, height: 12)).fill()
        }
        let title: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 30, weight: .bold), .foregroundColor: NSColor(white: 0.1, alpha: 1)]
        ("Quarterly report" as NSString).draw(at: CGPoint(x: window.minX + 48, y: window.minY + 80), withAttributes: title)
        let body: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 19), .foregroundColor: NSColor(white: 0.3, alpha: 1)]
        let lines = custom ?? ["Revenue grew 24% year over year, led by the new product line.",
                     "Customer satisfaction reached an all-time high of 92%.",
                     "Account numbers 4417-1234-5678 and 9921-7765-1102 are redacted.",
                     "Next quarter we focus on onboarding and performance.",
                     "The design team shipped the glass interface refresh."]
        for (i, line) in lines.enumerated() {
            (line as NSString).draw(at: CGPoint(x: window.minX + 48, y: window.minY + 150 + CGFloat(i) * 42), withAttributes: body)
        }
        NSGraphicsContext.restoreGraphicsState()
        return ctx.makeImage()
    }
}
