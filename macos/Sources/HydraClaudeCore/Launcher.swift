// Launching a profile - the macOS counterpart of Launcher in HydraClaude.cs.
//
// Launch Services treats every Claude window as the same app, so a plain "open Claude" just
// brings forward whichever instance is already running. Each launch therefore asks for a new
// instance, and passes --user-data-dir as a real argv entry. A profile that is already up is
// activated instead: Claude would otherwise start a second full copy on the same folder.

import AppKit

public enum LaunchError: Error, CustomStringConvertible {
    case notInstalled(AppInfo)
    case failed(profile: String, app: AppInfo, args: [String], underlying: String)

    public var description: String {
        switch self {
        case .notInstalled(let app):
            return """
            Claude Desktop could not be found.

            \(app.error ?? "")

            Install Claude from claude.ai/download, or point Settings at the Claude.app you use.
            """
        case let .failed(profile, app, args, underlying):
            return """
            Could not start the "\(profile)" profile.

            App:   \(app.url?.path ?? "-")  (\(app.source.rawValue))
            Args:  \(args.joined(separator: " "))

            \(underlying)
            """
        }
    }
}

public enum Launcher {
    public static let timestampStyle = Date.ISO8601FormatStyle(includingFractionalSeconds: true)

    public static func parseTimestamp(_ s: String) -> Date? {
        if s.isEmpty { return nil }
        return (try? timestampStyle.parse(s)) ?? (try? Date.ISO8601FormatStyle().parse(s))
    }

    // Starts the profile, or brings its running instance forward. Stamps lastLaunched on success.
    @MainActor
    public static func launch(_ p: inout ProfileEntry, app: AppInfo, runningPid: pid_t?) async throws {
        if let pid = runningPid, let running = NSRunningApplication(processIdentifier: pid) {
            running.unhide()
            running.activate()
            p.lastLaunched = Date().formatted(timestampStyle)
            return
        }

        guard let url = app.url else { throw LaunchError.notInstalled(app) }
        let args = Arguments.build(for: p)
        do {
            if !p.isStock {
                try FileManager.default.createDirectory(atPath: p.dir, withIntermediateDirectories: true)
            }
            let cfg = NSWorkspace.OpenConfiguration()
            cfg.createsNewApplicationInstance = true
            cfg.arguments = args
            cfg.activates = true
            _ = try await NSWorkspace.shared.openApplication(at: url, configuration: cfg)
        } catch {
            throw LaunchError.failed(profile: p.name, app: app, args: args,
                                     underlying: error.localizedDescription)
        }
        p.lastLaunched = Date().formatted(timestampStyle)
    }
}
