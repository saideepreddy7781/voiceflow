import Foundation
import Testing
@testable import VoiceFlowCore

struct AppVersionTests {
    @Test func comparesNumbers() {
        #expect(AppVersion.isNewer("1.2.4", than: "1.2.3"))
        #expect(AppVersion.isNewer("1.10.0", than: "1.9.9"))
        #expect(AppVersion.isNewer("2.0", than: "1.99.99"))
        #expect(!AppVersion.isNewer("1.2.3", than: "1.2.3"))
        #expect(!AppVersion.isNewer("1.2.2", than: "1.2.3"))
    }

    @Test func treatsShortAndPrefixedVersionsAsEqual() {
        #expect(!AppVersion.isNewer("v1.2", than: "1.2.0"))
        #expect(!AppVersion.isNewer("V1.2.0", than: "v1.2"))
        #expect(AppVersion.isNewer("v1.2.1", than: "1.2"))
    }

    @Test func ignoresBuildMetadata() {
        #expect(!AppVersion.isNewer("1.2.3+abc", than: "1.2.3+def"))
    }

    @Test func aReleaseIsNewerThanItsPrerelease() {
        #expect(AppVersion.isNewer("1.2.3", than: "1.2.3-beta.1"))
        #expect(!AppVersion.isNewer("1.2.3-beta.1", than: "1.2.3"))
    }

    @Test func comparesPrereleases() {
        #expect(AppVersion.isNewer("1.2.3-beta.2", than: "1.2.3-beta.1"))
        #expect(AppVersion.isNewer("1.2.3-beta.10", than: "1.2.3-beta.9"))
        #expect(AppVersion.isNewer("1.2.3-rc", than: "1.2.3-beta"))
        #expect(AppVersion.isNewer("1.2.3-beta.1", than: "1.2.3-beta"))
        #expect(AppVersion.isNewer("1.2.3-beta", than: "1.2.3-1"))   // numeric identifiers sort first
    }

    @Test func unreadableIsNeverNewer() {
        #expect(!AppVersion.isNewer("dev", than: "1.0.0"))
        #expect(!AppVersion.isNewer("1.0.0", than: "dev"))
        #expect(!AppVersion.isNewer("", than: "1.0.0"))
        #expect(!AppVersion.isNewer(nil, than: "1.0.0"))
        #expect(!AppVersion.isNewer("1.2.3.4.5", than: "1.0.0"))
        #expect(!AppVersion.isNewer("1..2", than: "1.0.0"))
        #expect(!AppVersion.isNewer("١.٢", than: "1.0.0"))   // non-ASCII digits
    }

    @Test func displayDropsPrefixAndMetadata() {
        #expect(AppVersion.display(" v1.2.3+abc ") == "1.2.3")
        #expect(AppVersion.display("1.2.3-beta.1") == "1.2.3-beta.1")
    }
}

struct UpdateCheckerTests {
    private func json(_ tag: String = "v1.4.0", url: String = "https://github.com/saideepreddy7781/voiceflow/releases/tag/v1.4.0",
                      draft: Bool = false, prerelease: Bool = false) -> Data {
        Data(#"{"tag_name":"\#(tag)","html_url":"\#(url)","draft":\#(draft),"prerelease":\#(prerelease),"body":"notes"}"#.utf8)
    }

    @Test func offersANewerRelease() {
        let result = UpdateChecker.evaluate(json: json(), currentVersion: "0.1.0")
        #expect(result == .available(ReleaseInfo(version: "1.4.0",
                                                 url: URL(string: "https://github.com/saideepreddy7781/voiceflow/releases/tag/v1.4.0")!)))
    }

    @Test func sameVersionIsUpToDate() {
        #expect(UpdateChecker.evaluate(json: json(), currentVersion: "1.4.0") == .upToDate)
        #expect(UpdateChecker.evaluate(json: json(), currentVersion: "v1.4.0") == .upToDate)
        #expect(UpdateChecker.evaluate(json: json(), currentVersion: "2.0.0") == .upToDate)
    }

    @Test func aDevBuildGetsNoNotice() {
        #expect(UpdateChecker.evaluate(json: json(), currentVersion: "dev") == .upToDate)
    }

    @Test func draftsPrereleasesAndGarbageAreIgnored() {
        #expect(UpdateChecker.evaluate(json: json(draft: true), currentVersion: "0.1.0") == .failed)
        #expect(UpdateChecker.evaluate(json: json(prerelease: true), currentVersion: "0.1.0") == .failed)
        #expect(UpdateChecker.evaluate(json: Data("not json".utf8), currentVersion: "0.1.0") == .failed)
        #expect(UpdateChecker.evaluate(json: json("latest"), currentVersion: "0.1.0") == .failed)
        #expect(UpdateChecker.evaluate(json: Data("{}".utf8), currentVersion: "0.1.0") == .failed)
    }

    @Test func linksStayInsideThisRepository() {
        #expect(UpdateChecker.safeURL("https://evil.example/voiceflow") == UpdateChecker.releasesPage)
        #expect(UpdateChecker.safeURL("https://github.com/someone-else/voiceflow/releases/tag/v1") == UpdateChecker.releasesPage)
        #expect(UpdateChecker.safeURL("https://github.com/saideepreddy7781/voiceflow.evil/x") == UpdateChecker.releasesPage)
        #expect(UpdateChecker.safeURL("http://github.com/saideepreddy7781/voiceflow/releases") == UpdateChecker.releasesPage)
        #expect(UpdateChecker.safeURL(nil) == UpdateChecker.releasesPage)
        #expect(UpdateChecker.safeURL("https://github.com/saideepreddy7781/voiceflow/../../someone-else/x") == UpdateChecker.releasesPage)
        #expect(UpdateChecker.safeURL("https://github.com/saideepreddy7781/voiceflow/%2e%2e/%2e%2e/someone-else/x") == UpdateChecker.releasesPage)
    }
}

struct DayUsageTests {
    private func entry(_ words: Int, seconds: Double, date: Date, outcome: String = "inserted") -> HistoryEntry {
        var e = HistoryEntry(kind: .dictation, appName: "", raw: "", final: Array(repeating: "word", count: words).joined(separator: " "),
                             audioSeconds: seconds, latencyMs: 0, outcome: outcome)
        e.date = date
        return e
    }

    @Test func countsOnlyTodaysDeliveredDictations() {
        let now = Date()
        let usage = DayUsage.compute([entry(100, seconds: 30, date: now), entry(5, seconds: 2, date: now, outcome: "failed"),
                                      entry(50, seconds: 10, date: now.addingTimeInterval(-3 * 86_400))], on: now)
        #expect(usage.words == 100)
        #expect(usage.dictations == 1)
        #expect(usage.minutesSaved == 2)
        #expect(usage.timeSavedLabel == "2 min")
    }

    @Test func labelsSmallAndZeroSavings() {
        #expect(DayUsage().timeSavedLabel == "0 min")
        #expect(DayUsage(words: 10, dictations: 1, secondsSpoken: 10).timeSavedLabel == "<1 min")
    }
}

struct JSONFileTests {
    @Test func discardingStoreNeverReadsOrWrites() {
        let file = JSONFile<[String]>.discarding("anything.json")
        file.save(["secret"])
        #expect(file.load() == nil)
        #expect(!FileManager.default.fileExists(atPath: file.url.path))
    }
}
