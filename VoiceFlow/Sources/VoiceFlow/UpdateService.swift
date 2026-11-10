import AppKit
import Foundation
import Observation
import VoiceFlowCore

/// Looks up the latest GitHub release at launch and once a day, for the notice in Settings and on Home.
/// It never downloads anything: the notice links to the release page.
@MainActor
@Observable
final class UpdateService {
    enum State: Equatable {
        case idle
        case checking
        case upToDate
        case available(ReleaseInfo)
        case failed
    }

    private(set) var state: State
    private let isDemo: Bool
    private var loop: Task<Void, Never>?

    init() {
        state = .idle
        isDemo = false
    }

    /// Demo mode: a fixed result and no network.
    init(demoState: State) {
        state = demoState
        isDemo = true
    }

    var currentVersion: String {
        isDemo ? "0.1.0" : Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "dev"
    }

    var available: ReleaseInfo? {
        if case .available(let release) = state { return release }
        return nil
    }

    /// Turns the launch and daily checks on or off. Turning them off also clears any notice.
    func setAutomatic(_ enabled: Bool) {
        loop?.cancel()
        loop = nil
        guard enabled, !isDemo else {
            if !enabled { state = .idle }
            return
        }
        loop = Task { [weak self] in
            while !Task.isCancelled {
                await self?.check()
                try? await Task.sleep(for: .seconds(24 * 60 * 60))
            }
        }
    }

    func check() async {
        guard !isDemo, state != .checking else { return }
        state = .checking
        switch await UpdateChecker().check(currentVersion: currentVersion) {
        case .upToDate: state = .upToDate
        case .available(let release): state = .available(release)
        case .failed: state = .failed
        }
    }

    func openRelease() {
        NSWorkspace.shared.open(available?.url ?? UpdateChecker.releasesPage)
    }
}
