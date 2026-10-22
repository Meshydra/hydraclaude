// Config store - the macOS counterpart of Store in HydraClaude.cs.
//
// Everything HydraClaude owns lives in ~/Library/Application Support/ClaudeProfiles, outside
// Claude.app, so a Claude auto-update (which replaces the whole bundle) never touches it.

import Foundation

public struct ProfileEntry: Codable, Equatable, Identifiable, Sendable {
    // Identity for the UI only; never written to disk, so the file stays hand-editable.
    public let id: UUID
    public var name: String
    // Empty dir means "launch with no --user-data-dir", i.e. the stock Claude instance.
    public var dir: String
    public var extraArgs: String
    public var lastLaunched: String

    public var isStock: Bool { dir.isEmpty }

    public init(name: String, dir: String = "", extraArgs: String = "", lastLaunched: String = "") {
        self.id = UUID()
        self.name = name
        self.dir = dir
        self.extraArgs = extraArgs
        self.lastLaunched = lastLaunched
    }

    enum CodingKeys: String, CodingKey { case name, dir, extraArgs, lastLaunched }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = UUID()
        name = try c.decode(String.self, forKey: .name)
        dir = try c.decodeIfPresent(String.self, forKey: .dir) ?? ""
        extraArgs = try c.decodeIfPresent(String.self, forKey: .extraArgs) ?? ""
        lastLaunched = try c.decodeIfPresent(String.self, forKey: .lastLaunched) ?? ""
    }
}

public struct HydraConfig: Codable, Equatable, Sendable {
    public var schema: Int
    // Overridable so a future bundle id change can be fixed from the UI, not a rebuild.
    public var bundleIdentifier: String
    // A specific Claude.app to launch instead of whatever Launch Services finds.
    public var appPathOverride: String
    // The "I'm still in the menu bar" hint is shown once, then never again.
    public var trayHintShown: Bool
    public var profiles: [ProfileEntry]

    public init(schema: Int = 1, bundleIdentifier: String = ClaudeApp.defaultBundleIdentifier,
                appPathOverride: String = "", trayHintShown: Bool = false,
                profiles: [ProfileEntry] = []) {
        self.schema = schema
        self.bundleIdentifier = bundleIdentifier
        self.appPathOverride = appPathOverride
        self.trayHintShown = trayHintShown
        self.profiles = profiles
    }

    enum CodingKeys: String, CodingKey {
        case schema, bundleIdentifier, appPathOverride, trayHintShown, profiles
    }

    // Every key is optional, so a hand-trimmed file still loads.
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        schema = try c.decodeIfPresent(Int.self, forKey: .schema) ?? 1
        bundleIdentifier = try c.decodeIfPresent(String.self, forKey: .bundleIdentifier) ?? ""
        appPathOverride = try c.decodeIfPresent(String.self, forKey: .appPathOverride) ?? ""
        trayHintShown = try c.decodeIfPresent(Bool.self, forKey: .trayHintShown) ?? false
        profiles = try c.decodeIfPresent([ProfileEntry].self, forKey: .profiles) ?? []
    }
}

public final class Store {
    public var config: HydraConfig
    public let root: URL

    public var configURL: URL { root.appendingPathComponent("hydraclaude.json") }

    public static var defaultRoot: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/ClaudeProfiles", isDirectory: true)
    }

    // Where the stock (no --user-data-dir) instance keeps its data. Confirmed from the command
    // line of a running Claude: its helpers carry --user-data-dir=~/Library/Application Support/Claude.
    public static var stockDir: String {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/Claude", isDirectory: true).path
    }

    public init(root: URL, config: HydraConfig) {
        self.root = root
        self.config = config
    }

    public func defaultDir(for name: String) -> String {
        root.appendingPathComponent(name, isDirectory: true).path
    }

    public static func load(root: URL = Store.defaultRoot) -> Store {
        let url = root.appendingPathComponent("hydraclaude.json")
        var config: HydraConfig?
        if FileManager.default.fileExists(atPath: url.path) {
            do {
                config = try JSONDecoder().decode(HydraConfig.self, from: Data(contentsOf: url))
            } catch {
                // A corrupt config must never block a launch: keep the bad file for inspection
                // and carry on with a freshly seeded one.
                let bad = url.appendingPathExtension("bad")
                try? FileManager.default.removeItem(at: bad)
                try? FileManager.default.copyItem(at: url, to: bad)
            }
        }

        var cfg = config ?? seed(root: root)
        if cfg.bundleIdentifier.trimmingCharacters(in: .whitespaces).isEmpty {
            cfg.bundleIdentifier = ClaudeApp.defaultBundleIdentifier
        }
        cfg.schema = 1
        return Store(root: root, config: cfg)
    }

    // First run: adopt whatever profile folders already exist, plus the stock instance.
    static func seed(root: URL) -> HydraConfig {
        var cfg = HydraConfig()
        cfg.profiles.append(ProfileEntry(name: "Default"))

        let fm = FileManager.default
        let children = (try? fm.contentsOfDirectory(at: root, includingPropertiesForKeys: [.isDirectoryKey])) ?? []
        for child in children.sorted(by: { $0.lastPathComponent < $1.lastPathComponent }) {
            let leaf = child.lastPathComponent
            if leaf.hasPrefix(".") { continue }
            guard (try? child.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true else { continue }
            // Built from root rather than child.path, which comes back as /private/var/... for
            // a root under /var: the stored path should read the way the user knows the folder.
            cfg.profiles.append(ProfileEntry(name: leaf, dir: root.appendingPathComponent(leaf).path))
        }
        return cfg
    }

    public func find(_ name: String) -> ProfileEntry? {
        config.profiles.first { $0.name.caseInsensitiveCompare(name) == .orderedSame }
    }

    public func index(of id: UUID) -> Int? {
        config.profiles.firstIndex { $0.id == id }
    }

    public func save() throws {
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let enc = JSONEncoder()
        // Sorted keys: JSONEncoder otherwise emits them in a different order on every save,
        // which makes a hand-edited file churn.
        enc.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        // .atomic writes a temp file and renames it over, so an interrupted save cannot
        // truncate the config.
        try enc.encode(config).write(to: configURL, options: .atomic)
    }
}
