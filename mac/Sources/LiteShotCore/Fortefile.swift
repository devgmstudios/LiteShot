import CryptoKit
import Foundation

/// One upload: its link (with the key after the #), and what's needed to delete it early.
public struct Upload: Codable, Equatable {
    public let url: String
    public let id: String
    public let deleteToken: String
    public let expires: Date
}

/// Uploads to fortefile.com, Devgm's own host. Screenshots are encrypted here with AES-256-GCM before they leave the Mac
/// and the key travels only in the link's #fragment, which browsers never send to a server, so fortefile stores data it
/// can't read. Files delete themselves after 1, 12 or 24 hours; the delete token removes one sooner. Reverse image search
/// is the one exception: Google has to see the picture, so those uploads aren't encrypted and last an hour.
/// The same protocol as the Windows app.
public final class Fortefile {
    public static var server = URL(string: "https://fortefile.com")!
    public static let lifetimes: [(key: String, label: String, hours: Int)] = [("1h", "1 hour", 1), ("12h", "12 hours", 12), ("24h", "24 hours", 24)]
    let session: URLSession

    public init(session: URLSession = .shared) { self.session = session }

    public static func hours(_ key: String) -> Int { lifetimes.first { $0.key == key }?.hours ?? 24 }

    /// Uploads a PNG and returns its link: encrypted unless it's for an image search.
    public func upload(png: Data, lifetimeHours: Int, encrypt: Bool, appVersion: String = "1.0") async throws -> Upload {
        let key = encrypt ? SymmetricKey(size: .bits256) : nil
        var request = URLRequest(url: Fortefile.server.appendingPathComponent("api/files"))
        request.httpMethod = "POST"
        request.timeoutInterval = 90
        request.setValue(key != nil ? "application/octet-stream" : "image/png", forHTTPHeaderField: "Content-Type")
        request.setValue(String(lifetimeHours * 3600), forHTTPHeaderField: "X-Lifetime")
        request.setValue(key != nil ? "1" : "0", forHTTPHeaderField: "X-Encrypted")
        request.setValue("LiteShot-Mac/\(appVersion)", forHTTPHeaderField: "User-Agent")
        let body = try key.map { try Fortefile.encrypt(png, key: $0) } ?? png
        let (data, response) = try await session.upload(for: request, from: body)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard (200..<300).contains(status) else { throw Fortefile.error(data, status: status) }
        guard let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let url = json["url"] as? String, let link = URL(string: url),
              link.scheme == "https" || link.host == "localhost" || link.host == "127.0.0.1",
              let id = json["id"] as? String, let token = json["deleteToken"] as? String,
              let expiresText = json["expires"] as? String, let expires = Fortefile.date(expiresText)
        else { throw SyncNodeError("fortefile.com sent an answer LiteShot doesn't understand.") }
        let keyText = key.map { "#" + Fortefile.base64URL($0.withUnsafeBytes { Data($0) }) } ?? ""
        return Upload(url: url + keyText, id: id, deleteToken: token, expires: expires)
    }

    /// Deletes an upload now; one that's already gone counts as deleted.
    public func delete(_ upload: Upload) async throws {
        guard let link = URL(string: upload.url), let scheme = link.scheme, let host = link.host else { return }
        var origin = URLComponents()
        origin.scheme = scheme
        origin.host = host
        origin.port = link.port
        guard let base = origin.url else { return }
        var request = URLRequest(url: base.appendingPathComponent("api/files").appendingPathComponent(upload.id))
        request.httpMethod = "DELETE"
        request.setValue("Bearer \(upload.deleteToken)", forHTTPHeaderField: "Authorization")
        let (data, response) = try await session.data(for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        if (200..<300).contains(status) || status == 404 { return }
        throw Fortefile.error(data, status: status)
    }

    /// AES-256-GCM: nonce (12 bytes), then ciphertext, then tag (16 bytes), which is what the fortefile viewer expects.
    public static func encrypt(_ plain: Data, key: SymmetricKey) throws -> Data {
        guard let combined = try AES.GCM.seal(plain, using: key).combined else { throw SyncNodeError("Couldn't encrypt the screenshot.") }
        return combined
    }

    public static func decrypt(_ data: Data, key: SymmetricKey) throws -> Data {
        try AES.GCM.open(AES.GCM.SealedBox(combined: data), using: key)
    }

    public static func base64URL(_ data: Data) -> String {
        data.base64EncodedString().replacingOccurrences(of: "=", with: "").replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
    }

    public static func fromBase64URL(_ text: String) -> Data? {
        var s = text.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        s += String(repeating: "=", count: (4 - s.count % 4) % 4)
        return Data(base64Encoded: s)
    }

    static func date(_ text: String) -> Date? {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let d = f.date(from: text) { return d }
        f.formatOptions = [.withInternetDateTime]
        return f.date(from: text)
    }

    static func error(_ data: Data, status: Int) -> Error {
        if status == 429 { return SyncNodeError("Too many uploads in a row. Try again in a minute.", status: status) }
        if let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any], let message = json["error"] as? String, !message.isEmpty {
            return SyncNodeError("fortefile.com: \(message).", status: status)
        }
        return SyncNodeError("fortefile.com answered \(status).", status: status)
    }

    /// The address that searches the web for a picture with Google Lens.
    public static func lensURL(for imageURL: String) -> URL? {
        var parts = URLComponents(string: "https://lens.google.com/uploadbyurl")
        parts?.queryItems = [URLQueryItem(name: "url", value: imageURL)]
        return parts?.url
    }

    /// Where a shared link goes, as on Windows: X, Facebook, Reddit, Pinterest, VK and email.
    public static let shareTargets: [(name: String, template: String)] = [
        ("X (Twitter)", "https://twitter.com/intent/tweet?url={0}"),
        ("Facebook", "https://www.facebook.com/sharer/sharer.php?u={0}"),
        ("Reddit", "https://www.reddit.com/submit?url={0}"),
        ("Pinterest", "https://pinterest.com/pin/create/button/?url={0}&media={0}"),
        ("VK", "https://vk.com/share.php?url={0}"),
        ("Email", "mailto:?subject=Screenshot&body={0}"),
    ]

    public static func shareURL(_ template: String, link: String) -> URL? {
        let escaped = link.addingPercentEncoding(withAllowedCharacters: .alphanumerics.union(CharacterSet(charactersIn: "-._~"))) ?? link
        return URL(string: template.replacingOccurrences(of: "{0}", with: escaped))
    }
}
