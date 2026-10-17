import Foundation

public enum KeyCheckResult: Equatable, Sendable {
    case ok(String)
    case invalidKey
    case failed(String)

    public var summary: String {
        switch self {
        case .ok(let detail): "Works. \(detail)"
        case .invalidKey: "The provider rejected this key."
        case .failed(let reason): "Could not check: \(reason)"
        }
    }
}

/// Checks an API key with a cheap, read-only request (each provider's model list).
public struct KeyTester: Sendable {
    private let session: URLSession

    public init(session: URLSession = .shared) {
        self.session = session
    }

    public func check(_ key: SecretKey, value: String) async -> KeyCheckResult {
        let value = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !value.isEmpty else { return .failed("no key entered") }

        var request: URLRequest
        switch key {
        case .groq:
            request = URLRequest(url: Groq.baseURL.appending(path: "models"))
            request.setValue("Bearer \(value)", forHTTPHeaderField: "Authorization")
        case .gemini:
            request = URLRequest(url: Gemini.baseURL.appending(path: "models"))
            request.setValue(value, forHTTPHeaderField: "x-goog-api-key")
        }
        request.timeoutInterval = 10

        do {
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else { return .failed("no HTTP response") }
            switch http.statusCode {
            case 200:
                return .ok(Self.describeModels(data, for: key))
            case 400, 401, 403:
                // Gemini answers 400 API_KEY_INVALID for a bad key; Groq answers 401.
                return .invalidKey
            case 429:
                return .failed("rate limited, try again in a minute")
            default:
                return .failed("HTTP \(http.statusCode)")
            }
        } catch {
            return .failed(error.localizedDescription)
        }
    }

    static func describeModels(_ data: Data, for key: SecretKey) -> String {
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return "Model list could not be read."
        }
        switch key {
        case .groq:
            let ids = (json["data"] as? [[String: Any]] ?? []).compactMap { $0["id"] as? String }
            let missing = (Groq.transcriptionModels.prefix(1) + Groq.textModels.prefix(1)).filter { !ids.contains($0) }
            return missing.isEmpty
                ? "\(ids.count) models available, including the defaults."
                : "\(ids.count) models available. Missing: \(missing.joined(separator: ", "))."
        case .gemini:
            let names = (json["models"] as? [[String: Any]] ?? []).compactMap { $0["name"] as? String }
            return "\(names.count) models available."
        }
    }
}
