import AppKit
import ServiceManagement

/// The menu bar item, the global shortcut and the settings menu.
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private var status: NSStatusItem!
    private var hotKey: HotKey?
    private var session: CaptureSession?

    func applicationDidFinishLaunching(_ notification: Notification) {
        status = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let button = status.button {
            button.image = NSImage(systemSymbolName: "camera.viewfinder", accessibilityDescription: "LiteShot")
            button.image?.isTemplate = true
        }
        let menu = NSMenu()
        menu.delegate = self
        status.menu = menu
        hotKey = HotKey { [weak self] in self?.capture() }
        // Ask for Screen Recording up front, so the first ⌘⇧9 doesn't land on a permission prompt.
        if !CGPreflightScreenCaptureAccess() { CGRequestScreenCaptureAccess() }
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        let capture = NSMenuItem(title: "Capture Area", action: #selector(captureFromMenu), keyEquivalent: "9")
        capture.keyEquivalentModifierMask = [.command, .shift]
        menu.addItem(capture)
        menu.addItem(.separator())
        menu.addItem(toggle("Bright Mode", Settings.brightMode, #selector(toggleBright)))
        menu.addItem(toggle("Button Names", Settings.buttonNames, #selector(toggleNames)))
        menu.addItem(toggle("Hide Personal Info", Settings.hidePersonalInfo, #selector(toggleHide)))
        menu.addItem(toggle("Open at Login", SMAppService.mainApp.status == .enabled, #selector(toggleLogin)))
        menu.addItem(NSMenuItem(title: "Settings…", action: #selector(openSettings), keyEquivalent: ","))
        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "LiteShot Website", action: #selector(openSite), keyEquivalent: ""))
        menu.addItem(NSMenuItem(title: "Quit LiteShot", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
        for item in menu.items where item.action != #selector(NSApplication.terminate(_:)) { item.target = self }
    }

    private func toggle(_ title: String, _ on: Bool, _ action: Selector) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: "")
        item.state = on ? .on : .off
        return item
    }

    @objc private func captureFromMenu() {
        // Let the menu close first, so it isn't in the frozen screen.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { [weak self] in self?.capture() }
    }

    @objc private func toggleBright() {
        Settings.brightMode.toggle()
        // Bright mode comes with the names; they can still be switched off on their own.
        if Settings.brightMode { Settings.buttonNames = true }
    }

    @objc private func toggleNames() { Settings.buttonNames.toggle() }
    @objc private func toggleHide() { Settings.hidePersonalInfo.toggle() }

    @objc private func toggleLogin() {
        let service = SMAppService.mainApp
        do {
            if service.status == .enabled { try service.unregister() } else { try service.register() }
        } catch {
            HUD.show("Couldn't change Open at Login: \(error.localizedDescription)")
        }
    }

    @objc private func openSettings() {
        Task { @MainActor in SettingsView.show() }
    }

    @objc private func openSite() {
        NSWorkspace.shared.open(URL(string: "https://liteshot.app")!)
    }

    func capture() {
        guard session == nil else { return }
        Task { @MainActor in
            do {
                let s = try await CaptureSession.start { [weak self] in self?.session = nil }
                session = s
            } catch {
                session = nil
                CaptureSession.explain(error)
            }
        }
    }
}
