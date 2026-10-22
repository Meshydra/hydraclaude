// State and actions behind the manager window and the menu bar menu - the non-drawing half of
// MainForm in HydraClaudeUi.cs.

import AppKit
import HydraClaudeCore
import Observation

enum Sheet: Identifiable {
    case new, edit(UUID), remove(UUID), settings

    var id: String {
        switch self {
        case .new: return "new"
        case .edit(let id): return "edit-\(id)"
        case .remove(let id): return "remove-\(id)"
        case .settings: return "settings"
        }
    }
}

struct Notice: Identifiable {
    let id = UUID()
    let title: String
    let message: String
}

@MainActor
@Observable
final class ManagerModel {
    private(set) var store: Store
    private(set) var app: AppInfo
    private(set) var running: [String: pid_t] = [:]
    var selection: UUID?
    var sheet: Sheet?
    var notice: Notice?

    @ObservationIgnored private let tracker = RunningTracker()
    @ObservationIgnored private var scanning = false
    @ObservationIgnored private var rescanQueued = false

    init(store: Store) {
        self.store = store
        self.app = ClaudeApp.resolve(store.config)
        selection = store.config.profiles.first?.id
    }

    var profiles: [ProfileEntry] { store.config.profiles }

    var selected: ProfileEntry? {
        guard let selection else { return nil }
        return profiles.first { $0.id == selection }
    }

    func profile(_ id: UUID) -> ProfileEntry? { profiles.first { $0.id == id } }

    func dataPath(of p: ProfileEntry) -> String { p.isStock ? Store.stockDir : p.dir }

    func isRunning(_ p: ProfileEntry) -> Bool {
        running[Paths.normalize(dataPath(of: p))] != nil
    }

    var status: (text: String, warning: Bool) {
        switch app.source {
        case .detected: return ("Claude \(app.version ?? "") detected", false)
        case .override: return ("Using the Claude app set in Settings", false)
        case .missing: return ("Claude not detected. Check Settings.", true)
        }
    }

    // ------------------------------------------------------------ state

    func reload(fromDisk: Bool) {
        if fromDisk {
            let keep = selected?.name
            store = Store.load(root: store.root)
            selection = keep.flatMap { store.find($0)?.id } ?? store.config.profiles.first?.id
        }
        app = ClaudeApp.resolve(store.config)
        rescan()
    }

    // The scan reads a few argv buffers through sysctl; cheap, but it still stays off the main
    // thread, and only touches observed state when the answer changed.
    func rescan() {
        if scanning { rescanQueued = true; return }
        scanning = true
        let tracker = self.tracker
        let bundleId = app.bundleIdentifier
        Task.detached(priority: .utility) {
            let changed = tracker.refresh(bundleIdentifier: bundleId)
            let dirs = tracker.dirs
            await MainActor.run {
                if changed || self.running != dirs { self.running = dirs }
                self.scanning = false
                if self.rescanQueued { self.rescanQueued = false; self.rescan() }
            }
        }
    }

    private func save() {
        do { try store.save() }
        catch {
            notice = Notice(title: "Could not save settings",
                            message: "HydraClaude could not write \(store.configURL.path)\n\n\(error.localizedDescription)")
        }
    }

    // ---------------------------------------------------------- actions

    func launch(_ id: UUID) {
        guard let i = store.index(of: id) else { return }
        var p = store.config.profiles[i]
        let pid = running[Paths.normalize(dataPath(of: p))]
        let app = self.app
        Task {
            do {
                try await Launcher.launch(&p, app: app, runningPid: pid)
                if let j = store.index(of: id) { store.config.profiles[j].lastLaunched = p.lastLaunched }
                save()
                rescan()
            } catch {
                notice = Notice(title: "HydraClaude", message: String(describing: error))
            }
        }
    }

    func add(_ p: ProfileEntry) {
        store.config.profiles.append(p)
        save()
        selection = p.id
    }

    func update(_ p: ProfileEntry) {
        guard let i = store.index(of: p.id) else { return }
        store.config.profiles[i] = p
        save()
    }

    func remove(_ id: UUID, moveFolderToBin: Bool) {
        guard let i = store.index(of: id) else { return }
        let p = store.config.profiles[i]
        if moveFolderToBin && !p.isStock && FileManager.default.fileExists(atPath: p.dir) {
            do {
                // The Bin, never a hard delete - a profile folder holds real history.
                try FileManager.default.trashItem(at: URL(fileURLWithPath: p.dir), resultingItemURL: nil)
            } catch {
                notice = Notice(title: "Folder kept",
                                message: "The profile was removed from the list, but its folder could not be moved to the Bin.\n\n\(error.localizedDescription)")
            }
        }
        store.config.profiles.remove(at: i)
        if selection == id { selection = store.config.profiles.first?.id }
        save()
    }

    func revealInFinder(_ id: UUID) {
        guard let p = profile(id) else { return }
        let dir = dataPath(of: p)
        let fm = FileManager.default
        if !fm.fileExists(atPath: dir) {
            if p.isStock {
                notice = Notice(title: "Nothing there yet",
                                message: "The stock profile keeps its data in\n\n\(dir)\n\nwhich Claude has not created yet.")
                return
            }
            do { try fm.createDirectory(atPath: dir, withIntermediateDirectories: true) }
            catch {
                notice = Notice(title: "Could not open the folder", message: error.localizedDescription)
                return
            }
        }
        NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: dir)])
    }

    func makeShortcut(_ id: UUID) {
        guard let p = profile(id), let exe = Bundle.main.executableURL else { return }
        let desktop = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Desktop")
        do {
            let made = try Shortcuts.create(profile: p.name, executable: exe.path,
                                            icon: Bundle.main.url(forResource: "hydra", withExtension: "icns"),
                                            in: desktop)
            NSWorkspace.shared.activateFileViewerSelecting([made])
        } catch {
            notice = Notice(title: "Could not create the shortcut", message: error.localizedDescription)
        }
    }

    func applySettings(bundleIdentifier: String, appPathOverride: String) {
        store.config.bundleIdentifier = bundleIdentifier
        store.config.appPathOverride = appPathOverride
        save()
        reload(fromDisk: false)
    }

    func markTrayHintShown() -> Bool {
        if store.config.trayHintShown { return false }
        store.config.trayHintShown = true
        save()
        return true
    }
}
