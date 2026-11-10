import Foundation

/// Groq chat completions (OpenAI-compatible).
public struct GroqChat: Sendable {
    let apiKey: String
    let session: URLSession
    let gate: GroqGate

    public init(apiKey: String, session: URLSession = .shared, gate: GroqGate = .shared) {
        self.apiKey = apiKey
        self.session = session
        self.gate = gate
    }

    public func complete(model: String, system: String, user: String, maxTokens: Int = 800,
                         json: Bool = false, timeout: TimeInterval = 6,
                         priority: GroqPriority = .interactive) async throws -> String {
        guard !apiKey.isEmpty else { throw ProviderError.missingKey }
        var body: [String: Any] = [
            "model": model,
            "temperature": 0,
            "max_completion_tokens": maxTokens,
            "reasoning_effort": Groq.reasoningEffort(for: model),
            "messages": [["role": "system", "content": system], ["role": "user", "content": user]],
        ]
        if json { body["response_format"] = ["type": "json_object"] }

        var request = URLRequest(url: Groq.baseURL.appending(path: "chat/completions"))
        request.httpMethod = "POST"
        request.timeoutInterval = timeout
        request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: body)

        let estimate = TokenEstimate.count(system) + TokenEstimate.count(user) + maxTokens / 2
        let data = try await gate.perform(.chat(model: model, estimatedTokens: estimate), priority: priority) { [session, request] in
            try await GroqHTTP.send(request, session: session)
        }
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let choice = (root["choices"] as? [[String: Any]])?.first,
              let message = choice["message"] as? [String: Any] else { throw ProviderError.badResponse }
        let finish = choice["finish_reason"] as? String ?? "stop"
        guard finish == "stop" else { throw ProviderError.http(200, "finish_reason \(finish)") }
        return (message["content"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

/// A rough token count for chat requests: about 4 characters per token for English, about 1 token per character
/// for Telugu and other non-Latin scripts.
public enum TokenEstimate {
    public static func count(_ text: String) -> Int {
        var ascii = 0, other = 0
        for scalar in text.unicodeScalars { if scalar.isASCII { ascii += 1 } else { other += 1 } }
        return ascii / 4 + other
    }
}
