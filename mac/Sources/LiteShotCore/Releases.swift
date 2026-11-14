import CryptoKit
import Foundation

/// A release from LiteShot's update server, after its signature checked out.
public struct Release: Equatable {
    public let version: String
    public let notes: String
    public let file: String
    public let size: Int
    public let sha256: String
}

/// The update server's signed manifests, the same contract as Windows: { payload, signature }, where the signature is
/// ECDSA P-256 with SHA-256 over the payload's UTF-8 bytes (64 bytes, r then s). The Mac app reads the "mac" channel.
public enum Releases {
    /// Devgm's release key; only manifests it signed are believed.
    public static let publicKeyPEM = """
    -----BEGIN PUBLIC KEY-----
    MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEIppu5/t8glXH4RNwtbIlKroAiRoQ
    itpPfqYTB05/fpujnv8KS6aG/ajVwhPskpH6KifNnwCs4p9yLLeDmKfkRA==
    -----END PUBLIC KEY-----
    """

    public enum Problem: Error, Equatable {
        case unreadable, badSignature, wrongProduct, noMacFile
    }

    /// The release a manifest describes, if its signature is good and it's a LiteShot Mac build on `channel`.
    public static func verify(_ data: Data, channel: String = "mac", publicKeyPEM: String = publicKeyPEM) throws -> Release {
        guard let envelope = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let payload = envelope["payload"] as? String, let signatureText = envelope["signature"] as? String,
              let signature = Data(base64Encoded: signatureText) else { throw Problem.unreadable }
        let key = try P256.Signing.PublicKey(pemRepresentation: publicKeyPEM)
        guard let ecdsa = try? P256.Signing.ECDSASignature(rawRepresentation: signature),
              key.isValidSignature(ecdsa, for: Data(payload.utf8)) else { throw Problem.badSignature }
        guard let info = (try? JSONSerialization.jsonObject(with: Data(payload.utf8))) as? [String: Any] else { throw Problem.unreadable }
        guard info["product"] as? String == "LiteShot", info["channel"] as? String == channel, let version = info["version"] as? String
        else { throw Problem.wrongProduct }
        guard let files = info["files"] as? [[String: Any]], let mac = files.first(where: { $0["kind"] as? String == "mac" }),
              let name = mac["name"] as? String, let sha = mac["sha256"] as? String else { throw Problem.noMacFile }
        return Release(version: version, notes: info["notes"] as? String ?? "", file: name, size: (mac["size"] as? NSNumber)?.intValue ?? 0, sha256: sha)
    }

    /// Whether `a` is a newer version than `b` (1.2.10 > 1.2.9).
    public static func isNewer(_ a: String, than b: String) -> Bool {
        let x = a.split(separator: ".").map { Int($0) ?? 0 }, y = b.split(separator: ".").map { Int($0) ?? 0 }
        for i in 0..<max(x.count, y.count) {
            let p = i < x.count ? x[i] : 0, q = i < y.count ? y[i] : 0
            if p != q { return p > q }
        }
        return false
    }

    public static func sha256Hex(_ data: Data) -> String { SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined() }
}
