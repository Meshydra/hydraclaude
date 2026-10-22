// Desktop shortcuts - the macOS counterpart of Shortcuts in HydraClaude.cs.
//
// A Finder alias cannot carry arguments, so each shortcut is a tiny app bundle whose executable
// is a shell script that runs `HydraClaude <name>`. LSUIElement keeps it out of the Dock, and a
// script executable needs no code signature. The bundle is built locally, so it never carries a
// quarantine flag and Gatekeeper does not prompt.

import Foundation

public enum Shortcuts {
    // Finder shows ":" as "/" and the reverse, so neither is safe in a file name.
    public static func safeFileName(_ name: String) -> String {
        name.replacingOccurrences(of: "/", with: "-").replacingOccurrences(of: ":", with: "-")
    }

    // Single-quoted for /bin/sh: the only character that needs care is the quote itself.
    public static func shellQuote(_ s: String) -> String {
        "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }

    public static func script(executable: String, profile: String) -> String {
        """
        #!/bin/sh
        # Created by HydraClaude. Launches the \(shellQuote(profile)) Claude profile.
        exec \(shellQuote(executable)) \(shellQuote(profile))

        """
    }

    @discardableResult
    public static func create(profile: String, executable: String, icon: URL?,
                              in folder: URL) throws -> URL {
        let fm = FileManager.default
        let app = folder.appendingPathComponent("Claude - \(safeFileName(profile)).app", isDirectory: true)
        let contents = app.appendingPathComponent("Contents", isDirectory: true)
        let macOS = contents.appendingPathComponent("MacOS", isDirectory: true)
        let resources = contents.appendingPathComponent("Resources", isDirectory: true)

        if fm.fileExists(atPath: app.path) { try fm.removeItem(at: app) }
        try fm.createDirectory(at: macOS, withIntermediateDirectories: true)
        try fm.createDirectory(at: resources, withIntermediateDirectories: true)

        let launcher = macOS.appendingPathComponent("launch")
        try script(executable: executable, profile: profile).write(to: launcher, atomically: true, encoding: .utf8)
        try fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: launcher.path)

        var plist: [String: Any] = [
            "CFBundleExecutable": "launch",
            "CFBundleIdentifier": "io.github.meshydra.hydraclaude.shortcut.\(bundleSafe(profile))",
            "CFBundleName": "Claude - \(profile)",
            "CFBundlePackageType": "APPL",
            "CFBundleShortVersionString": "1.0",
            "LSUIElement": true,
        ]
        if let icon, fm.fileExists(atPath: icon.path) {
            try fm.copyItem(at: icon, to: resources.appendingPathComponent("hydra.icns"))
            plist["CFBundleIconFile"] = "hydra"
        }
        let data = try PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0)
        try data.write(to: contents.appendingPathComponent("Info.plist"))
        return app
    }

    static func bundleSafe(_ s: String) -> String {
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-."))
        let mapped = s.unicodeScalars.map { allowed.contains($0) && $0.isASCII ? Character($0) : "-" }
        return String(mapped)
    }
}
