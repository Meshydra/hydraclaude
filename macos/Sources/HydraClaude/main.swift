// Entry point - the macOS counterpart of Program in HydraClaudeUi.cs.
//
//   HydraClaude            manager window and menu bar icon
//   HydraClaude Work       launch the "Work" profile directly - what desktop shortcuts call
//   HydraClaude --gui      force the manager even with other args

import AppKit
import HydraClaudeCore

@MainActor
func parseArguments(_ argv: [String]) -> (profile: String?, forceGui: Bool) {
    var profile: String?
    var forceGui = false
    var skipNext = false
    for a in argv.dropFirst() {
        if skipNext { skipNext = false; continue }
        if a.isEmpty { continue }
        if a == "--gui" { forceGui = true; continue }
        if a.hasPrefix("--") { continue }
        // Cocoa's own single-dash options (-NSDocumentRevisionsDebugMode YES, -psn_0_1234) take
        // a value or are the value; neither is a profile name.
        if a.hasPrefix("-") { skipNext = !a.hasPrefix("-psn_"); continue }
        if profile == nil { profile = a.trimmingCharacters(in: .whitespaces) }
    }
    return (profile, forceGui)
}

// `HydraClaude <name>`. An unknown name is created on the spot so a hand-made shortcut still
// works. The path stays thin: no window, no menu bar icon.
@MainActor
func launchDirect(_ store: Store, name: String) async -> Int32 {
    if store.find(name) == nil {
        store.config.profiles.append(ProfileEntry(name: name, dir: store.defaultDir(for: name)))
    }
    guard let i = store.config.profiles.firstIndex(where: { $0.name.caseInsensitiveCompare(name) == .orderedSame }) else { return 1 }

    let app = ClaudeApp.resolve(store.config)
    let tracker = RunningTracker()
    tracker.refresh(bundleIdentifier: app.bundleIdentifier)

    var p = store.config.profiles[i]
    do {
        try await Launcher.launch(&p, app: app, runningPid: tracker.pid(for: p))
    } catch {
        let message = String(describing: error)
        FileHandle.standardError.write(Data((message + "\n").utf8))
        NSApplication.shared.setActivationPolicy(.accessory)
        NSApp.activate()
        let alert = NSAlert()
        alert.alertStyle = .critical
        alert.messageText = "Claude - \(name)"
        alert.informativeText = message
        alert.runModal()
        return 1
    }

    store.config.profiles[i] = p
    try? store.save()
    DistributedNotificationCenter.default().postNotificationName(Signal.configChanged, object: nil,
                                                                 userInfo: nil, deliverImmediately: true)
    return 0
}

let (profileArg, forceGui) = parseArguments(CommandLine.arguments)
let store = Store.load()

if !forceGui, let name = profileArg {
    exit(await launchDirect(store, name: name))
}

// Only one manager at a time: wake the parked one instead of starting a second.
if let myId = Bundle.main.bundleIdentifier {
    let me = ProcessInfo.processInfo.processIdentifier
    let others = NSRunningApplication.runningApplications(withBundleIdentifier: myId)
        .filter { $0.processIdentifier != me && !$0.isTerminated }
    if !others.isEmpty {
        DistributedNotificationCenter.default().postNotificationName(Signal.show, object: nil,
                                                                     userInfo: nil, deliverImmediately: true)
        exit(0)
    }
}

let app = NSApplication.shared
let delegate = AppDelegate(model: ManagerModel(store: store))
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
