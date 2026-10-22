// Menu bar presence, the manager window and the app menu - the macOS counterpart of the tray
// and window plumbing in MainForm.
//
// HydraClaude is a menu bar app (LSUIElement). While the manager window is open it also takes a
// Dock icon and a place in Cmd-Tab, the same way the Windows build only shows in the taskbar
// while its window is up. Closing the window parks it in the menu bar; Quit is the real exit.

import AppKit
import HydraClaudeCore
import SwiftUI

enum Signal {
    // Posted by a second `HydraClaude --gui` to wake the one already running.
    static let show = Notification.Name("io.github.meshydra.hydraclaude.show")
    // Posted by `HydraClaude <name>` after it saved the config, so the manager re-reads it.
    static let configChanged = Notification.Name("io.github.meshydra.hydraclaude.config-changed")
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, NSMenuDelegate {
    private let model: ManagerModel
    private var statusItem: NSStatusItem?
    private var window: NSWindow?
    private var hint: NSPopover?

    init(model: ManagerModel) {
        self.model = model
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        Theme.applyForcedAppearance()
        buildMainMenu()
        buildStatusItem()
        watchClaude()

        let dnc = DistributedNotificationCenter.default()
        dnc.addObserver(forName: Signal.show, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.showManager() }
        }
        dnc.addObserver(forName: Signal.configChanged, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.model.reload(fromDisk: true) }
        }

        showManager()
    }

    // Opening HydraClaude.app again while it is parked wakes the manager.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        showManager()
        return false
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    // Launch Services posts a notification whenever any app starts or quits, so the running
    // badges are event driven: no timer, nothing to pay for while idle.
    private func watchClaude() {
        let nc = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.didLaunchApplicationNotification, NSWorkspace.didTerminateApplicationNotification] {
            nc.addObserver(forName: name, object: nil, queue: .main) { [weak self] note in
                let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication
                let id = app?.bundleIdentifier
                MainActor.assumeIsolated {
                    guard let self, id == self.model.app.bundleIdentifier else { return }
                    self.model.rescan()
                }
            }
        }
        model.rescan()
    }

    // ------------------------------------------------------------ window

    func showManager() {
        if window == nil {
            let view = ManagerView(model: model) { [weak self] in self?.hideManager() }
            let w = NSWindow(contentViewController: NSHostingController(rootView: view))
            w.title = "HydraClaude"
            w.styleMask = [.titled, .closable, .miniaturizable, .resizable]
            w.setContentSize(NSSize(width: 640, height: CGFloat(min(max(model.profiles.count, 3), 8) * 52 + 140)))
            w.isReleasedWhenClosed = false
            w.delegate = self
            w.setFrameAutosaveName("HydraClaudeManager")
            if !w.setFrameUsingName("HydraClaudeManager") { w.center() }
            window = w
        }
        NSApp.setActivationPolicy(.regular)
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate()
        model.reload(fromDisk: false)
    }

    func hideManager() {
        window?.orderOut(nil)
        NSApp.setActivationPolicy(.accessory)
        if model.markTrayHintShown() { showTrayHint() }
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        hideManager()
        return false
    }

    // The Windows build shows a one-time balloon; a popover off the menu bar icon is the
    // closest thing here, and it makes the "closing doesn't quit" contract visible.
    private func showTrayHint() {
        guard let button = statusItem?.button else { return }
        let text = Text("HydraClaude is still running.\nOpen a profile from this menu bar icon.")
            .padding(14)
            .fixedSize()
        let pop = NSPopover()
        pop.behavior = .transient
        pop.contentViewController = NSHostingController(rootView: text)
        pop.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
        hint = pop
        Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(6))
            self?.hint?.performClose(nil)
            self?.hint = nil
        }
    }

    // --------------------------------------------------------- menu bar

    private func buildStatusItem() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let image = Bundle.main.image(forResource: "hydra-menubar") {
            image.isTemplate = true     // inverts with the menu bar, like the two tray icons do
            image.size = NSSize(width: 18, height: 18)
            item.button?.image = image
        } else {
            item.button?.image = NSImage(systemSymbolName: "square.stack.3d.up", accessibilityDescription: "HydraClaude")
        }
        item.button?.toolTip = "HydraClaude"
        let menu = NSMenu()
        menu.delegate = self
        item.menu = menu
        statusItem = item
    }

    // Rebuilt every time it opens, so the running marks are always current.
    func menuNeedsUpdate(_ menu: NSMenu) {
        guard menu == statusItem?.menu else { return }
        menu.removeAllItems()
        for p in model.profiles {
            let item = NSMenuItem(title: p.name, action: #selector(launchFromMenu(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = p.id
            if model.isRunning(p) {
                item.state = .on
                item.toolTip = "Already running. Brings its window forward."
            }
            menu.addItem(item)
        }
        if !model.profiles.isEmpty { menu.addItem(.separator()) }
        menu.addItem(withTitle: "Open Manager…", action: #selector(openManager), keyEquivalent: "").target = self
        menu.addItem(withTitle: "Quit HydraClaude", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        model.rescan()
    }

    @objc private func launchFromMenu(_ sender: NSMenuItem) {
        if let id = sender.representedObject as? UUID { model.launch(id) }
    }

    @objc private func openManager() { showManager() }

    // --------------------------------------------------------- app menu

    // Without a main menu a SwiftUI text field gets no Cut/Copy/Paste, so this is required,
    // not decoration. It also carries the keyboard shortcuts the Windows build binds to keys.
    private func buildMainMenu() {
        let main = NSMenu()

        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "About HydraClaude", action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)), keyEquivalent: "")
        appMenu.addItem(.separator())
        appMenu.addItem(item("Settings…", ",") { $0.model.sheet = .settings })
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Hide HydraClaude", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        appMenu.addItem(withTitle: "Quit HydraClaude", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        main.addItem(submenu(appMenu, "HydraClaude"))

        let file = NSMenu(title: "Profile")
        file.addItem(item("New Profile…", "n") { $0.model.sheet = .new })
        file.addItem(item("Open Profile", "o") { d in d.withSelection { d.model.launch($0) } })
        file.addItem(item("Edit Profile…", "e") { d in d.withSelection { d.model.sheet = .edit($0) } })
        file.addItem(item("Show in Finder", "r", [.command, .shift]) { d in d.withSelection { d.model.revealInFinder($0) } })
        file.addItem(item("Create Desktop Shortcut", "") { d in d.withSelection { d.model.makeShortcut($0) } })
        file.addItem(.separator())
        file.addItem(item("Remove Profile…", "\u{8}") { d in d.withSelection { d.model.sheet = .remove($0) } })
        file.addItem(.separator())
        file.addItem(item("Refresh", "r") { $0.model.reload(fromDisk: true) })
        file.addItem(item("Close Window", "w") { $0.hideManager() })
        main.addItem(submenu(file, "Profile"))

        let edit = NSMenu(title: "Edit")
        edit.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
        edit.addItem(withTitle: "Redo", action: Selector(("redo:")), keyEquivalent: "Z")
        edit.addItem(.separator())
        edit.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        main.addItem(submenu(edit, "Edit"))

        let win = NSMenu(title: "Window")
        win.addItem(withTitle: "Minimise", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        main.addItem(submenu(win, "Window"))
        NSApp.windowsMenu = win

        NSApp.mainMenu = main
    }

    private func submenu(_ menu: NSMenu, _ title: String) -> NSMenuItem {
        let holder = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        menu.title = title
        holder.submenu = menu
        return holder
    }

    private func item(_ title: String, _ key: String, _ mods: NSEvent.ModifierFlags = .command,
                      _ run: @escaping @MainActor (AppDelegate) -> Void) -> NSMenuItem {
        let item = ClosureMenuItem(title: title, keyEquivalent: key, run: { [weak self] in
            if let self { run(self) }
        })
        item.keyEquivalentModifierMask = mods
        return item
    }

    // Menu shortcuts act on the manager only while it is on screen and no sheet is up.
    private func withSelection(_ act: (UUID) -> Void) {
        guard window?.isVisible == true, model.sheet == nil, let id = model.selection else { return }
        act(id)
    }
}

@MainActor
final class ClosureMenuItem: NSMenuItem {
    private let run: @MainActor () -> Void

    init(title: String, keyEquivalent: String, run: @escaping @MainActor () -> Void) {
        self.run = run
        super.init(title: title, action: #selector(fire), keyEquivalent: keyEquivalent)
        target = self
    }

    required init(coder: NSCoder) { fatalError("not used") }

    @objc private func fire() { run() }
}
