import Foundation
import Security

/// The API keys VoiceFlow can hold.
public enum SecretKey: String, CaseIterable, Sendable, Identifiable {
    case groq
    case gemini

    public var id: String { rawValue }

    public var displayName: String {
        switch self {
        case .groq: "Groq"
        case .gemini: "Gemini"
        }
    }

    public var whereToGetIt: String {
        switch self {
        case .groq: "console.groq.com → API Keys"
        case .gemini: "aistudio.google.com → Get API key (use a billing-enabled project)"
        }
    }
}

public struct KeychainError: Error, Equatable, CustomStringConvertible {
    public let status: OSStatus

    public var description: String {
        let message = SecCopyErrorMessageString(status, nil) as String? ?? "unknown error"
        return "Keychain error \(status): \(message)"
    }
}

/// Stores API keys as generic passwords in the login keychain.
/// Keys never go to UserDefaults, logs or crash reports.
public struct SecretStore: Sendable {
    public let service: String

    public init(service: String = "com.saideep.voiceflow") {
        self.service = service
    }

    public func save(_ value: String, for key: SecretKey) throws {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        let data = Data(trimmed.utf8)
        let query = baseQuery(for: key)

        let updateStatus = SecItemUpdate(query as CFDictionary, [kSecValueData: data] as CFDictionary)
        if updateStatus == errSecSuccess { return }
        guard updateStatus == errSecItemNotFound else { throw KeychainError(status: updateStatus) }

        var addQuery = query
        addQuery[kSecValueData] = data
        addQuery[kSecAttrLabel] = "VoiceFlow \(key.displayName) API key"
        let addStatus = SecItemAdd(addQuery as CFDictionary, nil)
        guard addStatus == errSecSuccess else { throw KeychainError(status: addStatus) }
    }

    public func read(_ key: SecretKey) throws -> String? {
        var query = baseQuery(for: key)
        query[kSecReturnData] = true
        query[kSecMatchLimit] = kSecMatchLimitOne

        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess else { throw KeychainError(status: status) }
        guard let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    /// Whether a key is stored, without reading the secret itself. Reading only the item's attributes
    /// never triggers macOS's "wants to use your confidential information" prompt, so this is safe to
    /// call at launch.
    public func contains(_ key: SecretKey) -> Bool {
        var query = baseQuery(for: key)
        query[kSecReturnAttributes] = true
        query[kSecMatchLimit] = kSecMatchLimitOne
        var result: AnyObject?
        return SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess
    }

    public func delete(_ key: SecretKey) throws {
        let status = SecItemDelete(baseQuery(for: key) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw KeychainError(status: status)
        }
    }

    private func baseQuery(for key: SecretKey) -> [CFString: Any] {
        [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: key.rawValue,
        ]
    }
}
