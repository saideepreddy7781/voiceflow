/// How this process was started.
enum AppRuntime {
    /// True in `--screenshots` mode: views show in-memory demo data and may not touch the Mac's mic, Keychain or files.
    @MainActor static var isDemo = false
}
