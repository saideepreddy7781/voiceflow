import Foundation

/// Groq chat completions (OpenAI-compatible).
public struct GroqChat: Sendable {
    let apiKey: String
    let session: URLSession

    public init(apiKey: String, session: URLSession = .shared) {
        self.apiKey = apiKey
        self.session = session
    }

    public func complete(model: String, system: String, user: String, maxTokens: Int = 800,
                         json: Bool = false, timeout: TimeInterval = 6) async throws -> String {
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

        let (data, _) = try await GroqHTTP.send(request, session: session)
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let choice = (root["choices"] as? [[String: Any]])?.first,
              let message = choice["message"] as? [String: Any] else { throw ProviderError.badResponse }
        let finish = choice["finish_reason"] as? String ?? "stop"
        guard finish == "stop" else { throw ProviderError.http(200, "finish_reason \(finish)") }
        return (message["content"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
    }
}
