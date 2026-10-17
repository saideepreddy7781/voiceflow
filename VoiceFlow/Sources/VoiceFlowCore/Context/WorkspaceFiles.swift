import Foundation

/// Finds the project open in a code editor and matches spoken file names against it
/// (file tagging in Cursor and Windsurf).
public enum WorkspaceFiles {
    static let storageFiles: [String: String] = [
        "com.todesktop.230313mzl4w4u92": "Cursor",
        "com.exafunction.windsurf": "Windsurf",
        "com.microsoft.VSCode": "Code",
    ]
    static let skipped: Set<String> = [".git", "node_modules", ".build", "build", "dist", ".next", "Pods", "DerivedData",
                                       "venv", ".venv", "__pycache__", "target", ".swiftpm", "out", "coverage", ".idea"]

    nonisolated(unsafe) private static var cache: [String: (date: Date, files: [String])] = [:]
    private static let cacheLock = NSLock()

    /// Project folder for the editor window, found by matching the window title's project name against
    /// folders the editor recently opened (its storage.json).
    public static func projectFolder(bundleID: String, windowTitle: String, documentPath: String?) -> URL? {
        if let documentPath, let root = gitRoot(from: URL(fileURLWithPath: documentPath)) { return root }
        guard let appFolder = storageFiles[bundleID] else { return nil }
        let storage = FileManager.default.homeDirectoryForCurrentUser
            .appending(path: "Library/Application Support/\(appFolder)/User/globalStorage/storage.json")
        guard let text = try? String(contentsOf: storage, encoding: .utf8) else { return nil }
        let uris = Set(text.matches(of: /file:\/\/[^"\\]+/).map { String($0.output) })
        let folders = uris.compactMap(URL.init(string:)).filter { $0.hasDirectoryPath || isDirectory($0) }
        // Titles look like "file.swift — project" or "project — Cursor".
        let pieces = windowTitle.components(separatedBy: " — ").flatMap { $0.components(separatedBy: " - ") }
            .map { $0.trimmingCharacters(in: .whitespaces) }
        return folders.first { folder in pieces.contains(folder.lastPathComponent.removingPercentEncoding ?? folder.lastPathComponent) }
    }

    /// Relative paths of files in the folder (cached for a minute, capped at 5,000).
    public static func files(in folder: URL) -> [String] {
        let key = folder.path
        cacheLock.lock()
        if let hit = cache[key], Date().timeIntervalSince(hit.date) < 60 { cacheLock.unlock(); return hit.files }
        cacheLock.unlock()

        var result: [String] = []
        let fm = FileManager.default
        if let walker = fm.enumerator(at: folder, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles]) {
            for case let url as URL in walker {
                if skipped.contains(url.lastPathComponent) { walker.skipDescendants(); continue }
                if walker.level > 8 { walker.skipDescendants(); continue }
                if (try? url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true { continue }
                result.append(String(url.path.dropFirst(folder.path.count + 1)))
                if result.count >= 5_000 { break }
            }
        }
        cacheLock.lock(); cache[key] = (Date(), result); cacheLock.unlock()
        return result
    }

    /// Files whose name the transcript mentions, e.g. "auth controller" → `AuthController.swift`.
    public static func mentioned(in transcript: String, files: [String], limit: Int = 8) -> [String] {
        let spoken = " " + words(transcript).joined(separator: " ") + " "
        var hits: [(String, Int)] = []
        for path in files {
            let name = (path as NSString).lastPathComponent
            let stem = (name as NSString).deletingPathExtension
            let stemWords = words(splitIdentifier(stem))
            guard !stemWords.isEmpty, stemWords.joined().count >= 4 else { continue }
            let phrase = " " + stemWords.joined(separator: " ") + " "
            if spoken.contains(phrase) || spoken.contains(" " + stemWords.joined() + " ") {
                hits.append((name, stemWords.count))
            }
        }
        // Prefer longer, more specific names; drop duplicates.
        var seen = Set<String>()
        return hits.sorted { $0.1 > $1.1 }.map(\.0).filter { seen.insert($0).inserted }.prefix(limit).map { $0 }
    }

    static func splitIdentifier(_ s: String) -> String {
        s.replacing(/([a-z0-9])([A-Z])/) { "\($0.output.1) \($0.output.2)" }
            .replacing(/[_\-.]/, with: " ")
    }

    static func words(_ s: String) -> [String] {
        s.lowercased().split { !$0.isLetter && !$0.isNumber }.map(String.init)
    }

    static func gitRoot(from file: URL) -> URL? {
        var dir = file.deletingLastPathComponent()
        for _ in 0..<12 {
            if FileManager.default.fileExists(atPath: dir.appending(path: ".git").path) { return dir }
            let parent = dir.deletingLastPathComponent()
            if parent.path == dir.path { break }
            dir = parent
        }
        return nil
    }

    static func isDirectory(_ url: URL) -> Bool {
        var isDir: ObjCBool = false
        return FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir) && isDir.boolValue
    }
}
