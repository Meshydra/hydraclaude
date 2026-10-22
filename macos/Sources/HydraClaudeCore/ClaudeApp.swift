// Finds Claude.app - the macOS counterpart of Packaging in HydraClaude.cs.
//
// There is no MSIX on the Mac, but the same rule holds: never pin a path. Claude Desktop
// updates itself through Squirrel, which swaps the whole bundle in place, and the user may keep
// it in /Applications, ~/Applications or anywhere else. So the bundle is looked up through
// Launch Services by its identifier every time, and its version read from that bundle.

import AppKit

public struct AppInfo: Equatable, Sendable {
    public enum Source: String, Sendable { case detected, override, missing }

    public var url: URL?
    public var version: String?
    public var bundleIdentifier: String
    public var source: Source
    public var error: String?

    public var resolved: Bool { url != nil }
}

public enum ClaudeApp {
    public static let defaultBundleIdentifier = "com.anthropic.claudefordesktop"

    public typealias Lookup = @Sendable (String) -> URL?

    public static let launchServices: Lookup = { id in
        NSWorkspace.shared.urlForApplication(withBundleIdentifier: id)
    }

    public static func resolve(_ cfg: HydraConfig, lookup: Lookup = launchServices) -> AppInfo {
        let trimmed = cfg.bundleIdentifier.trimmingCharacters(in: .whitespaces)
        let id = trimmed.isEmpty ? defaultBundleIdentifier : trimmed
        var info = AppInfo(url: nil, version: nil, bundleIdentifier: id, source: .missing, error: nil)

        // An explicit override always wins - it is the escape hatch when detection is wrong.
        let override = cfg.appPathOverride.trimmingCharacters(in: .whitespaces)
        if !override.isEmpty {
            let url = URL(fileURLWithPath: (override as NSString).expandingTildeInPath)
            if let bundle = Bundle(url: url), bundle.executableURL != nil {
                info.url = url
                info.version = version(of: bundle)
                info.source = .override
                return info
            }
            info.error = "The app set in Settings (\(url.path)) is not an application bundle."
        }

        if let url = lookup(id) {
            info.url = url
            info.version = Bundle(url: url).flatMap(version(of:))
            info.source = .detected
            info.error = nil
        } else if info.error == nil {
            info.error = "No installed app has the bundle identifier \"\(id)\"."
        }
        return info
    }

    static func version(of bundle: Bundle) -> String? {
        bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
    }
}
