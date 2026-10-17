import Testing
@testable import VoiceFlowCore

@Suite(.serialized)
struct SecretStoreTests {
    // A separate service so tests never touch the real app's keys.
    let store = SecretStore(service: "com.saideep.voiceflow.tests")

    @Test func saveReadUpdateDelete() throws {
        try store.delete(.groq)
        #expect(try store.read(.groq) == nil)

        try store.save("  first-value \n", for: .groq)
        #expect(try store.read(.groq) == "first-value")

        try store.save("second-value", for: .groq)
        #expect(try store.read(.groq) == "second-value")

        try store.delete(.groq)
        #expect(try store.read(.groq) == nil)
    }

    @Test func keysAreIndependent() throws {
        try store.save("groq-value", for: .groq)
        try store.save("gemini-value", for: .gemini)
        #expect(try store.read(.groq) == "groq-value")
        #expect(try store.read(.gemini) == "gemini-value")
        try store.delete(.groq)
        try store.delete(.gemini)
    }

    @Test func deletingAMissingKeyIsNotAnError() throws {
        try store.delete(.gemini)
        try store.delete(.gemini)
    }
}

extension SecretStoreTests {
    @Test func containsDoesNotNeedTheSecret() throws {
        try store.delete(.gemini)
        #expect(!store.contains(.gemini))
        try store.save("value", for: .gemini)
        #expect(store.contains(.gemini))
        try store.delete(.gemini)
        #expect(!store.contains(.gemini))
    }
}
