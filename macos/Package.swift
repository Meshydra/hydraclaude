// swift-tools-version: 6.0
//
// HydraClaude for macOS. Built with the Swift toolchain that ships with Xcode or the Command
// Line Tools - no Xcode project, no dependencies. `./build.sh` wraps the binary in an .app.

import PackageDescription

let package = Package(
    name: "HydraClaude",
    platforms: [.macOS(.v14)],
    targets: [
        // Everything that isn't UI: config, Claude discovery, launching, the running scan.
        // Kept separate so the tests can reach it without starting an app.
        .target(name: "HydraClaudeCore"),
        .executableTarget(name: "HydraClaude", dependencies: ["HydraClaudeCore"]),
        .testTarget(name: "HydraClaudeCoreTests", dependencies: ["HydraClaudeCore"]),
    ]
)
