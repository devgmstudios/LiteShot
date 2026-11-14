import Foundation
import Security

/// The SyncNode key lives in the login Keychain, the Mac's place for secrets (like DPAPI on Windows), never in plain settings.
enum Keychain {
    private static let service = "app.liteshot.mac.syncnode"
    private static let account = "SyncNode API key"

    static var syncNodeKey: String? {
        get {
            let query: [String: Any] = [
                kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account,
                kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne,
            ]
            var result: CFTypeRef?
            guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return nil }
            let key = String(data: data, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines)
            return key?.isEmpty == false ? key : nil
        }
        set {
            let match: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account]
            SecItemDelete(match as CFDictionary)
            guard let value = newValue?.trimmingCharacters(in: .whitespacesAndNewlines), !value.isEmpty else { return }
            var add = match
            add[kSecValueData as String] = Data(value.utf8)
            add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
            SecItemAdd(add as CFDictionary, nil)
        }
    }

    static var hasKey: Bool { syncNodeKey != nil }
}
