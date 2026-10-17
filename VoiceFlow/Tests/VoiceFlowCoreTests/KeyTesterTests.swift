import Foundation
import Testing
@testable import VoiceFlowCore

/// Answers every request with a canned status and body, and records the request.
final class StubProtocol: URLProtocol, @unchecked Sendable {
    nonisolated(unsafe) static var status = 200
    nonisolated(unsafe) static var body = Data()
    nonisolated(unsafe) static var lastRequest: URLRequest?

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        Self.lastRequest = request
        let response = HTTPURLResponse(url: request.url!, statusCode: Self.status, httpVersion: nil, headerFields: nil)!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Self.body)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}

@Suite(.serialized)
struct KeyTesterTests {
    private func tester() -> KeyTester {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [StubProtocol.self]
        return KeyTester(session: URLSession(configuration: config))
    }

    @Test func groqOKSendsBearerAndReportsModels() async {
        StubProtocol.status = 200
        StubProtocol.body = Data(#"{"data":[{"id":"whisper-large-v3-turbo"},{"id":"qwen/qwen3.8-27b"}]}"#.utf8)

        let result = await tester().check(.groq, value: " gsk_test ")

        #expect(result == .ok("2 models available, including the defaults."))
        #expect(StubProtocol.lastRequest?.value(forHTTPHeaderField: "Authorization") == "Bearer gsk_test")
        #expect(StubProtocol.lastRequest?.url?.absoluteString == "https://api.groq.com/openai/v1/models")
    }

    @Test func groqReportsMissingDefaultModels() async {
        StubProtocol.status = 200
        StubProtocol.body = Data(#"{"data":[{"id":"whisper-large-v3"}]}"#.utf8)
        let result = await tester().check(.groq, value: "gsk_test")
        #expect(result == .ok("1 models available. Missing: whisper-large-v3-turbo, qwen/qwen3.8-27b."))
    }

    @Test func groqRejectedKey() async {
        StubProtocol.status = 401
        StubProtocol.body = Data()
        #expect(await tester().check(.groq, value: "bad") == .invalidKey)
    }

    @Test func geminiUsesHeaderAndTreats400AsInvalid() async {
        StubProtocol.status = 400
        StubProtocol.body = Data(#"{"error":{"status":"INVALID_ARGUMENT"}}"#.utf8)

        let result = await tester().check(.gemini, value: "AIza-test")

        #expect(result == .invalidKey)
        #expect(StubProtocol.lastRequest?.value(forHTTPHeaderField: "x-goog-api-key") == "AIza-test")
    }

    @Test func rateLimitIsNotReportedAsBadKey() async {
        StubProtocol.status = 429
        StubProtocol.body = Data()
        #expect(await tester().check(.groq, value: "gsk_test") == .failed("rate limited, try again in a minute"))
    }

    @Test func emptyKeyIsNotSent() async {
        StubProtocol.lastRequest = nil
        #expect(await tester().check(.groq, value: "   ") == .failed("no key entered"))
        #expect(StubProtocol.lastRequest == nil)
    }

    /// Runs only when GROQ_API_KEY is set, so the normal suite stays offline.
    @Test(.enabled(if: ProcessInfo.processInfo.environment["GROQ_API_KEY"] != nil))
    func liveGroqKey() async {
        let key = ProcessInfo.processInfo.environment["GROQ_API_KEY"]!
        let result = await KeyTester().check(.groq, value: key)
        guard case .ok(let detail) = result else {
            Issue.record("Live Groq check failed: \(result.summary)")
            return
        }
        #expect(detail.hasSuffix("including the defaults."))
    }
}
