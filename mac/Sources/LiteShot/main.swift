import AppKit

// `LiteShot --render-shots <folder>` draws the overlay on a made-up screen and saves PNGs, so the UI can be
// checked on a build machine with no one at the screen. Otherwise LiteShot runs as a menu bar app.
let args = CommandLine.arguments
if let i = args.firstIndex(of: "--render-shots") {
    let folder = i + 1 < args.count ? args[i + 1] : "shots"
    exit(SelfTest.renderShots(to: URL(fileURLWithPath: folder)) ? 0 : 1)
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
