import AppKit
import LiteShotCore

/// Keeps LiteShot up to date from liteshot.app's "mac" channel, like the Windows updater: it checks at launch and every
/// few hours, believes only releases signed by Devgm whose file matches its SHA-256, and then installs on its own or
/// says one is ready (Settings › Updates). The day's first check carries a random install id, so copies in use can be
/// counted; "Count this copy" turns that off.
@MainActor
final class Updater {
    static let shared = Updater()
    static let server = URL(string: "https://liteshot.app")!

    enum Mode: String, CaseIterable { case automatic, notify, off }

    private(set) var available: Release?
    private(set) var status = "Not checked yet."
    private var timer: Timer?
    private let defaults = UserDefaults.standard

    var mode: Mode { Mode(rawValue: defaults.string(forKey: "UpdateMode") ?? "") ?? .notify }
    var countUsage: Bool { defaults.object(forKey: "CountUsage") as? Bool ?? true }
    static var version: String { Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0" }

    func start() {
        DispatchQueue.main.asyncAfter(deadline: .now() + 20) { Task { await self.check(manual: false) } }
        timer = Timer.scheduledTimer(withTimeInterval: 6 * 3600, repeats: true) { _ in Task { @MainActor in await Updater.shared.check(manual: false) } }
    }

    func check(manual: Bool) async {
        guard manual || mode != .off else { return }
        var parts = URLComponents(url: Updater.server.appendingPathComponent("v1/latest"), resolvingAgainstBaseURL: false)!
        parts.queryItems = [URLQueryItem(name: "channel", value: "mac")]
        var request = URLRequest(url: parts.url!)
        request.timeoutInterval = 30
        // The server reads the version from "LiteShot/x.y.z".
        request.setValue("LiteShot/\(Updater.version)", forHTTPHeaderField: "User-Agent")
        let counted = countMe(&request)
        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            if let counted { defaults.set(counted, forKey: "LastCountedDay") }
            guard (response as? HTTPURLResponse)?.statusCode == 200 else { status = "No Mac release yet."; return }
            let release = try Releases.verify(data)
            guard Releases.isNewer(release.version, than: Updater.version) else {
                available = nil
                status = "You're up to date (\(Updater.version))."
                if manual { HUD.show("LiteShot \(Updater.version) is the latest") }
                return
            }
            available = release
            status = "LiteShot \(release.version) is available."
            if mode == .automatic { await install(release) } else { HUD.show("LiteShot \(release.version) is ready · Update from the menu bar") }
        } catch {
            status = "Couldn't check for updates."
            if manual { HUD.show(status) }
        }
    }

    /// The day's first check says "this copy is in use" with a random id; nothing about the Mac or the person.
    private func countMe(_ request: inout URLRequest) -> String? {
        guard countUsage else { return nil }
        let day = ISO8601DateFormatter.string(from: Date(), timeZone: TimeZone(identifier: "UTC")!, formatOptions: [.withFullDate])
        guard defaults.string(forKey: "LastCountedDay") != day else { return nil }
        let id = defaults.string(forKey: "InstallId") ?? UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        defaults.set(id, forKey: "InstallId")
        request.setValue(id, forHTTPHeaderField: "X-LiteShot-Install")
        return day
    }

    /// Downloads the release, checks its SHA-256, swaps it in for this copy of LiteShot.app and starts it again.
    func install(_ release: Release) async {
        let current = Bundle.main.bundleURL
        guard current.pathExtension == "app" else { status = "Only an installed LiteShot.app can update itself."; return }
        HUD.show("Downloading LiteShot \(release.version)…")
        do {
            let url = Updater.server.appendingPathComponent("v1/download/\(release.version)/\(release.file)")
            var request = URLRequest(url: url)
            request.setValue("LiteShot/\(Updater.version)", forHTTPHeaderField: "User-Agent")
            let (zip, _) = try await URLSession.shared.data(for: request)
            guard Releases.sha256Hex(zip) == release.sha256.lowercased() else { throw SyncNodeError("The download didn't match its checksum.") }
            let folder = FileManager.default.temporaryDirectory.appendingPathComponent("LiteShot-update-\(UUID().uuidString)", isDirectory: true)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let file = folder.appendingPathComponent(release.file)
            try zip.write(to: file)
            let unzip = Process()
            unzip.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
            unzip.arguments = ["-x", "-k", file.path, folder.path]
            try unzip.run()
            unzip.waitUntilExit()
            let fresh = folder.appendingPathComponent("LiteShot.app")
            guard unzip.terminationStatus == 0, FileManager.default.fileExists(atPath: fresh.path) else { throw SyncNodeError("The update couldn't be unpacked.") }
            _ = try FileManager.default.replaceItemAt(current, withItemAt: fresh)
            // Start the new copy once this one has quit.
            let relaunch = Process()
            relaunch.executableURL = URL(fileURLWithPath: "/bin/sh")
            relaunch.arguments = ["-c", "sleep 1; /usr/bin/open \"$0\"", current.path]
            try relaunch.run()
            NSApp.terminate(nil)
        } catch {
            status = "The update failed: \(SyncNodeClient.friendly(error))"
            HUD.show(status)
        }
    }
}
