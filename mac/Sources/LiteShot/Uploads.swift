import AppKit
import LiteShotCore
import SwiftUI

/// Upload and copy link (⌘D), Share and Search similar images (⌘K), and the recent uploads in the menu bar, through
/// fortefile.com. Links are encrypted on the Mac and delete themselves; Recent Uploads › Delete Now removes one sooner.
@MainActor
enum Uploads {
    enum Kind { case link, share, search }

    private static let historyKey = "Uploads"

    /// The newest first, at most 20, without the ones that have expired.
    static var recent: [Upload] {
        get {
            guard let data = UserDefaults.standard.data(forKey: historyKey),
                  let list = try? JSONDecoder().decode([Upload].self, from: data) else { return [] }
            return list.filter { $0.expires > Date() }
        }
        set { UserDefaults.standard.set(try? JSONEncoder().encode(Array(newValue.prefix(20))), forKey: historyKey) }
    }

    static var lifetimeKey: String { UserDefaults.standard.string(forKey: "UploadLifetime") ?? "24h" }

    static func send(_ image: CGImage, kind: Kind) {
        guard let png = Renderer.png(image) else { return }
        // Image search needs Google to see the picture, so it goes up plain and lasts an hour.
        let search = kind == .search
        let hours = search ? 1 : Fortefile.hours(lifetimeKey)
        HUD.show(search ? "Uploading for the search…" : "Uploading…")
        Task {
            do {
                let upload = try await Fortefile().upload(png: png, lifetimeHours: hours, encrypt: !search, appVersion: appVersion)
                if !search { recent = [upload] + recent }
                switch kind {
                case .link:
                    copy(upload.url)
                    HUD.show("Link copied · deletes in \(hours == 1 ? "1 hour" : "\(hours) hours")")
                case .share:
                    copy(upload.url)
                    Windows.show("Share", size: CGSize(width: 420, height: 300), resizable: false) { ShareView(link: upload.url) }
                case .search:
                    if let lens = Fortefile.lensURL(for: upload.url) { NSWorkspace.shared.open(lens) }
                }
            } catch {
                copyImage(image)
                HUD.show("Upload failed: \(SyncNodeClient.friendly(error)) The screenshot was copied instead.")
            }
        }
    }

    static func delete(_ upload: Upload) {
        Task {
            do {
                try await Fortefile().delete(upload)
                recent = recent.filter { $0.id != upload.id }
                HUD.show("Deleted from fortefile.com")
            } catch {
                HUD.show(SyncNodeClient.friendly(error))
            }
        }
    }

    static func copy(_ text: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
    }

    private static func copyImage(_ image: CGImage) {
        ImageActions.copy(image, scale: NSScreen.main?.backingScaleFactor ?? 2)
    }

    static var appVersion: String { Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0" }

    /// The menu bar's Recent Uploads: each link with Copy, Open and Delete Now.
    static func menu() -> NSMenuItem? {
        let list = recent
        guard !list.isEmpty else { return nil }
        let parent = NSMenuItem(title: "Recent Uploads", action: nil, keyEquivalent: "")
        let sub = NSMenu()
        let time = DateFormatter()
        time.dateStyle = .none
        time.timeStyle = .short
        for upload in list {
            let item = NSMenuItem(title: "Link · deletes at \(time.string(from: upload.expires))", action: nil, keyEquivalent: "")
            let actions = NSMenu()
            actions.addItem(MenuAction("Copy Link") { copy(upload.url); HUD.show("Link copied") }.item)
            actions.addItem(MenuAction("Open") { if let u = URL(string: upload.url) { NSWorkspace.shared.open(u) } }.item)
            actions.addItem(MenuAction("Delete Now") { delete(upload) }.item)
            item.submenu = actions
            sub.addItem(item)
        }
        parent.submenu = sub
        return parent
    }
}

/// A menu item that runs a closure; kept alive by the item itself.
final class MenuAction: NSObject {
    let run: () -> Void
    let item: NSMenuItem

    init(_ title: String, run: @escaping () -> Void) {
        self.run = run
        item = NSMenuItem(title: title, action: #selector(fire), keyEquivalent: "")
        super.init()
        item.target = self
        item.representedObject = self
    }

    @objc private func fire() { run() }
}

/// After Share: the private link, copied already, and the places to post it.
struct ShareView: View {
    let link: String

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("The private link is copied.").font(.headline)
            Text(link).font(.caption.monospaced()).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle).textSelection(.enabled)
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 110), spacing: 8)], spacing: 8) {
                ForEach(Fortefile.shareTargets, id: \.name) { target in
                    Button(target.name) { if let u = Fortefile.shareURL(target.template, link: link) { NSWorkspace.shared.open(u) } }
                        .buttonStyle(.bordered).controlSize(.large)
                }
            }
            Text("Only people with the whole link can open it, and it deletes itself.").font(.caption).foregroundStyle(.secondary)
        }
        .padding(20)
        .frame(minWidth: 380)
    }
}
