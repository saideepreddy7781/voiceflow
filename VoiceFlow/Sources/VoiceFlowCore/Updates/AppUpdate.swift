import Foundation

/// Compares app versions: 1.2.3, v1.2.3, 1.2 (same as 1.2.0), 1.2.3-beta.1. Build metadata after + is ignored.
public enum AppVersion {
    private struct Parsed {
        var numbers: [Int]
        var prerelease: [String]
    }

    private static func parse(_ text: String?) -> Parsed? {
        guard var s = text?.trimmingCharacters(in: .whitespacesAndNewlines), !s.isEmpty else { return nil }
        if s.first == "v" || s.first == "V" { s.removeFirst() }
        if let plus = s.firstIndex(of: "+") { s = String(s[..<plus]) }
        var prerelease: [String] = []
        if let dash = s.firstIndex(of: "-") {
            prerelease = s[s.index(after: dash)...].split(separator: ".", omittingEmptySubsequences: false).map(String.init)
            s = String(s[..<dash])
        }
        let parts = s.split(separator: ".", omittingEmptySubsequences: false)
        guard (1...4).contains(parts.count) else { return nil }
        var numbers = [0, 0, 0, 0]
        for (i, part) in parts.enumerated() {
            guard !part.isEmpty, part.allSatisfy(\.isASCII), part.allSatisfy(\.isNumber), let n = Int(part) else { return nil }
            numbers[i] = n
        }
        return Parsed(numbers: numbers, prerelease: prerelease)
    }

    public static func isValid(_ text: String?) -> Bool { parse(text) != nil }

    /// True when `candidate` is a later version than `current`. Anything unreadable is never newer.
    public static func isNewer(_ candidate: String?, than current: String?) -> Bool {
        guard let a = parse(candidate), let b = parse(current) else { return false }
        for (x, y) in zip(a.numbers, b.numbers) where x != y { return x > y }
        return comparePrerelease(a.prerelease, b.prerelease) > 0
    }

    /// SemVer order: a release sorts after its prereleases, and numeric identifiers sort before text.
    private static func comparePrerelease(_ a: [String], _ b: [String]) -> Int {
        if a.isEmpty || b.isEmpty { return a.count == b.count ? 0 : (a.isEmpty ? 1 : -1) }
        for (x, y) in zip(a, b) {
            let order: Int
            switch (Int(x), Int(y)) {
            case let (m?, n?): order = m == n ? 0 : (m < n ? -1 : 1)
            case (_?, nil): order = -1
            case (nil, _?): order = 1
            case (nil, nil): order = x == y ? 0 : (x < y ? -1 : 1)
            }
            if order != 0 { return order }
        }
        return a.count == b.count ? 0 : (a.count < b.count ? -1 : 1)
    }

    /// The version a user reads: no leading v and no +commit suffix.
    public static func display(_ version: String) -> String {
        var s = version.trimmingCharacters(in: .whitespacesAndNewlines)
        if s.first == "v" || s.first == "V" { s.removeFirst() }
        if let plus = s.firstIndex(of: "+") { s = String(s[..<plus]) }
        return s
    }
}

public struct ReleaseInfo: Equatable, Sendable {
    public var version: String
    public var url: URL

    public init(version: String, url: URL) {
        self.version = version
        self.url = url
    }
}

public enum UpdateCheckResult: Equatable, Sendable {
    case upToDate
    case available(ReleaseInfo)
    case failed
}

/// Asks GitHub for the latest published release. It sends nothing about the user and downloads nothing:
/// the app only shows a notice with a link to the release page.
public struct UpdateChecker: Sendable {
    public static let repository = "https://github.com/saideepreddy7781/voiceflow"
    public static let releasesPage = URL(string: repository + "/releases")!
    public static let latestReleaseAPI = URL(string: "https://api.github.com/repos/saideepreddy7781/voiceflow/releases/latest")!

    private let session: URLSession

    public init(session: URLSession = .shared) {
        self.session = session
    }

    public func check(currentVersion: String) async -> UpdateCheckResult {
        var request = URLRequest(url: Self.latestReleaseAPI, timeoutInterval: 15)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("2022-11-28", forHTTPHeaderField: "X-GitHub-Api-Version")
        // GitHub rejects API requests with no User-Agent.
        request.setValue("VoiceFlow", forHTTPHeaderField: "User-Agent")
        guard let (data, response) = try? await session.data(for: request),
              let status = (response as? HTTPURLResponse)?.statusCode else { return .failed }
        // A repository with no published release answers 404: nothing to offer, and nothing is wrong.
        if status == 404 { return .upToDate }
        guard (200..<300).contains(status) else { return .failed }
        return Self.evaluate(json: data, currentVersion: currentVersion)
    }

    static func evaluate(json: Data, currentVersion: String) -> UpdateCheckResult {
        guard let release = parse(json: json) else { return .failed }
        return AppVersion.isNewer(release.version, than: currentVersion) ? .available(release) : .upToDate
    }

    /// Reads tag_name and html_url. Nil when the body is not a published, readable release.
    static func parse(json: Data) -> ReleaseInfo? {
        struct Body: Decodable {
            var tag_name: String?
            var html_url: String?
            var draft: Bool?
            var prerelease: Bool?
        }
        guard let body = try? JSONDecoder().decode(Body.self, from: json),
              body.draft != true, body.prerelease != true,
              let tag = body.tag_name, AppVersion.isValid(tag) else { return nil }
        return ReleaseInfo(version: AppVersion.display(tag), url: safeURL(body.html_url))
    }

    /// The link opens in the browser, so only this repository's own pages are accepted.
    static func safeURL(_ text: String?) -> URL {
        // ".." would climb out of the repository once the browser resolves it.
        guard let text, text.hasPrefix(repository + "/"), let url = URL(string: text), url.scheme == "https",
              !url.pathComponents.contains("..") else { return releasesPage }
        return url
    }
}
