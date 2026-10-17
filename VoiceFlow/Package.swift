// swift-tools-version: 6.2
import PackageDescription

let package = Package(
    name: "VoiceFlow",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "VoiceFlow", targets: ["VoiceFlow"]),
    ],
    targets: [
        // Everything that can be tested without a UI: secrets, preferences, provider clients.
        .target(name: "VoiceFlowCore"),
        // The menu-bar app. Bundled into VoiceFlow.app by scripts/build-app.sh.
        .executableTarget(name: "VoiceFlow", dependencies: ["VoiceFlowCore"]),
        .testTarget(name: "VoiceFlowCoreTests", dependencies: ["VoiceFlowCore"]),
    ]
)
