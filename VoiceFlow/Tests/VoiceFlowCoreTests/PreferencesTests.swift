import CoreGraphics
import Foundation
import Testing
@testable import VoiceFlowCore

struct PreferencesTests {
    private func freshDefaults() -> UserDefaults {
        let suite = "com.saideep.voiceflow.tests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defaults.removePersistentDomain(forName: suite)
        return defaults
    }

    @Test func defaultsMatchTheApp() {
        let prefs = Preferences()
        #expect(prefs.hotkey == .fn)
        #expect(prefs.transcriptionModel == "whisper-large-v3-turbo")
        #expect(prefs.cleanupModel == "qwen/qwen3.8-27b")
        #expect(prefs.commandModel == "openai/gpt-oss-120b")
        #expect(prefs.editorProvider == .groq)
        #expect(prefs.cleanupLevel == .light)
        #expect(prefs.languages == ["en"])
        #expect(!prefs.debugLogging)
        #expect(!prefs.showBarWhenIdle)   // the Flow bar appears only while dictating
        #expect(prefs.flowBarDraggable)
        #expect(prefs.flowBarSpots.isEmpty)  // bottom centre until dragged
    }

    @Test func everyFieldRoundTrips() {
        let store = PreferenceStore(defaults: freshDefaults())
        var prefs = Preferences()
        prefs.hotkey = .rightOption
        prefs.editorProvider = .gemini
        prefs.transcriptionModel = "whisper-large-v3"
        prefs.cleanupModel = "openai/gpt-oss-20b"
        prefs.commandModel = "qwen/qwen3.8-27b"
        prefs.geminiModel = "some-flash-model"
        prefs.languages = ["te", "en"]
        prefs.cleanupLevel = .medium
        prefs.styles[.email] = .excited
        prefs.showInDock = false
        prefs.historyRetentionDays = -1
        prefs.launchAtLogin = true
        prefs.debugLogging = true
        prefs.flowBarDraggable = false
        prefs.flowBarSpots.set(CGPoint(x: 420, y: 300), for: "0,0,1512x982")

        store.save(prefs)
        #expect(store.load() == prefs)
    }

    /// Preferences saved before update checks existed still decode, with checks on and nothing dismissed.
    @Test func olderStoredPreferencesGetUpdateDefaults() throws {
        let old = #"{"hotkey":"rightOption","languages":["te","en"],"historyRetentionDays":-1,"onboarded":true,"showBarWhenIdle":false}"#
        let prefs = try JSONDecoder().decode(Preferences.self, from: Data(old.utf8))
        #expect(prefs.hotkey == .rightOption)
        #expect(prefs.languages == ["te", "en"])
        #expect(prefs.historyRetentionDays == -1)
        #expect(prefs.onboarded)
        #expect(prefs.checkForUpdates)
        #expect(prefs.dismissedUpdateVersion == "")
    }

    /// Preferences saved before the Flow bar could move still decode: draggable, at the bottom centre.
    @Test func olderStoredPreferencesGetFlowBarDefaults() throws {
        let old = #"{"hotkey":"fn","showBarWhenIdle":true,"onboarded":true,"checkForUpdates":false}"#
        let prefs = try JSONDecoder().decode(Preferences.self, from: Data(old.utf8))
        #expect(prefs.showBarWhenIdle)
        #expect(!prefs.checkForUpdates)
        #expect(prefs.flowBarDraggable)
        #expect(prefs.flowBarSpots.isEmpty)
    }

    /// A damaged spot list resets only the bar's position, never the other settings.
    @Test func damagedFlowBarSpotsKeepOtherSettings() throws {
        let stored = #"{"hotkey":"rightOption","flowBarSpots":{"x":1},"flowBarDraggable":false}"#
        let prefs = try JSONDecoder().decode(Preferences.self, from: Data(stored.utf8))
        #expect(prefs.hotkey == .rightOption)
        #expect(!prefs.flowBarDraggable)
        #expect(prefs.flowBarSpots.isEmpty)
    }

    @Test func missingStoreGivesDefaults() {
        #expect(PreferenceStore(defaults: freshDefaults()).load() == Preferences())
    }

    @Test func missingFieldsKeepOtherSettings() throws {
        let defaults = freshDefaults()
        let partial = #"{"hotkey":"rightCommand","languages":["te"]}"#
        defaults.set(Data(partial.utf8), forKey: PreferenceStore.defaultsKey)

        let prefs = PreferenceStore(defaults: defaults).load()
        #expect(prefs.hotkey == .rightCommand)
        #expect(prefs.languages == ["te"])
        #expect(prefs.cleanupModel == Preferences().cleanupModel)
    }

    @Test func unknownEnumValueFallsBackToDefault() {
        let defaults = freshDefaults()
        defaults.set(Data(#"{"hotkey":"capsLock","languages":["hi"]}"#.utf8), forKey: PreferenceStore.defaultsKey)
        let prefs = PreferenceStore(defaults: defaults).load()
        #expect(prefs.hotkey == .fn)
        #expect(prefs.languages == ["hi"])
    }

    @Test func settingsFromOlderVersionsMigrate() {
        let defaults = freshDefaults()
        defaults.set(Data(#"{"language":"","cleanupEnabled":false}"#.utf8), forKey: PreferenceStore.defaultsKey)
        let prefs = PreferenceStore(defaults: defaults).load()
        #expect(prefs.languages.isEmpty)           // "" meant auto-detect
        #expect(prefs.whisperLanguage == nil)
        #expect(prefs.cleanupLevel == .none)       // cleanup was switched off
    }

    @Test func whisperLanguageOnlyWhenExactlyOne() {
        var prefs = Preferences()
        prefs.languages = ["te"]
        #expect(prefs.whisperLanguage == "te")
        prefs.languages = ["te", "en"]
        #expect(prefs.whisperLanguage == nil)
    }

    @Test func stylesStayWithinTheirCategory() {
        var prefs = Preferences()
        #expect(prefs.style(for: .personal) == .casual)
        #expect(prefs.style(for: .email) == .formal)
        prefs.styles[.email] = .veryCasual            // not offered for email
        #expect(prefs.style(for: .email) == .formal)
        prefs.styles[.personal] = .excited            // not offered for personal
        #expect(prefs.style(for: .personal) == .casual)
        #expect(StyleCategory.personal.options == [.formal, .casual, .veryCasual])
        #expect(StyleCategory.work.options == [.formal, .casual, .excited])
    }

    @Test func reasoningEffortPerModel() {
        #expect(Groq.reasoningEffort(for: "openai/gpt-oss-120b") == "low")
        #expect(Groq.reasoningEffort(for: "qwen/qwen3.8-27b") == "none")
    }
}
