// The three sheets: new/edit profile, remove profile, settings - the macOS counterparts of
// ProfileDialog, RemoveDialog and SettingsDialog in HydraClaudeUi.cs.

import AppKit
import HydraClaudeCore
import SwiftUI
import UniformTypeIdentifiers

struct ProfileSheet: View {
    let model: ManagerModel
    let editing: ProfileEntry?

    @Environment(\.dismiss) private var dismiss
    @State private var name = ""
    @State private var dir = ""
    @State private var extraArgs = ""
    @State private var useStock = false
    @State private var problem: String?
    @FocusState private var nameFocused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text(editing == nil ? "New profile" : "Edit profile").font(.headline)

            Form {
                TextField("Name", text: $name)
                    .focused($nameFocused)
                    .onChange(of: name) { old, new in syncDefaultDir(oldName: old, newName: new) }

                // The stock entry is the one Claude installed; a new profile is always its own
                // folder, so that choice is simply absent when creating one.
                if editing != nil {
                    Toggle("Use the stock data folder (the normal Claude install)", isOn: $useStock)
                }

                LabeledContent("Data folder") {
                    HStack {
                        TextField("Data folder", text: $dir).labelsHidden()
                        Button("Browse…", action: browse)
                    }
                }
                .disabled(useStock)

                TextField("Extra args", text: $extraArgs)
            }
            .formStyle(.columns)

            Text("Each profile is its own Claude login, history and settings. Nothing here lives inside Claude.app, so an app update never touches it.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            if let problem {
                Text(problem).font(.callout).foregroundStyle(Theme.accent)
                    .fixedSize(horizontal: false, vertical: true)
            }

            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button(editing == nil ? "Create" : "Save", action: commit)
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(.borderedProminent)
            }
        }
        .padding(20)
        .frame(width: 520)
        .tint(Theme.accent)
        .onAppear {
            if let p = editing {
                name = p.name
                useStock = p.isStock
                dir = p.isStock ? model.store.defaultDir(for: p.name) : p.dir
                extraArgs = p.extraArgs
            }
            nameFocused = true
        }
    }

    // While the folder is still the default for the old name, follow the name as it's typed -
    // one less thing to keep in your head.
    private func syncDefaultDir(oldName: String, newName: String) {
        if useStock { return }
        let store = model.store
        let oldDefault = store.defaultDir(for: oldName.trimmingCharacters(in: .whitespaces))
        let underRoot = dir.hasPrefix(store.root.path) && !FileManager.default.fileExists(atPath: dir)
        if dir.isEmpty || Paths.normalize(dir) == Paths.normalize(oldDefault) || underRoot {
            dir = store.defaultDir(for: newName.trimmingCharacters(in: .whitespaces))
        }
    }

    private func browse() {
        let panel = NSOpenPanel()
        panel.message = "Choose the data folder for this profile"
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.directoryURL = URL(fileURLWithPath: FileManager.default.fileExists(atPath: dir) ? dir : model.store.root.path)
        if panel.runModal() == .OK, let url = panel.url { dir = url.path }
    }

    private func commit() {
        if let issue = ProfileValidation.check(name: name, dir: dir, useStock: useStock,
                                               editing: editing?.id, in: model.profiles) {
            problem = issue.description
            return
        }

        var folder = ""
        if !useStock {
            folder = Paths.trimTrailingSlash((dir.trimmingCharacters(in: .whitespaces) as NSString).expandingTildeInPath)
            folder = URL(fileURLWithPath: folder).standardizedFileURL.path
            do { try FileManager.default.createDirectory(atPath: folder, withIntermediateDirectories: true) }
            catch {
                problem = "Could not create \(folder)\n\n\(error.localizedDescription)"
                return
            }
        }

        let trimmedName = name.trimmingCharacters(in: .whitespaces)
        let args = extraArgs.trimmingCharacters(in: .whitespaces)
        if var p = editing {
            p.name = trimmedName
            p.dir = folder
            p.extraArgs = args
            model.update(p)
        } else {
            model.add(ProfileEntry(name: trimmedName, dir: folder, extraArgs: args))
        }
        dismiss()
    }
}

struct RemoveSheet: View {
    let model: ManagerModel
    let profile: ProfileEntry

    @Environment(\.dismiss) private var dismiss
    @State private var moveToBin = false

    private var hasFolder: Bool {
        !profile.isStock && FileManager.default.fileExists(atPath: profile.dir)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Remove \"\(profile.name)\" from HydraClaude?").font(.headline)
            Text(profile.isStock
                 ? "This removes the entry only. The Claude installation is untouched."
                 : "This removes the entry only. The folder and everything in it stays where it is.")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            Toggle(profile.isStock ? "No folder to delete" : "Also move the folder to the Bin", isOn: $moveToBin)
                .disabled(!hasFolder)
            if !profile.isStock {
                Text(profile.dir).font(.callout).foregroundStyle(.secondary)
                    .lineLimit(1).truncationMode(.middle)
                    .padding(.leading, 20)
            }

            HStack {
                Spacer()
                // The destructive action never takes Return.
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("Remove", role: .destructive) {
                    model.remove(profile.id, moveFolderToBin: moveToBin && hasFolder)
                    dismiss()
                }
            }
            .padding(.top, 8)
        }
        .padding(20)
        .frame(width: 440)
        .tint(Theme.accent)
    }
}

struct SettingsSheet: View {
    let model: ManagerModel

    @Environment(\.dismiss) private var dismiss
    @State private var bundleId = ""
    @State private var appPath = ""
    @State private var detected: AppInfo?

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Settings").font(.headline)
            Text("HydraClaude looks Claude up by its bundle identifier every time it runs, so app updates and moving Claude.app cannot break it. Change these only if detection fails.")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            Form {
                TextField("Bundle identifier", text: $bundleId)
                LabeledContent("Claude app override") {
                    HStack {
                        TextField("Claude app override", text: $appPath, prompt: Text("Detected automatically"))
                            .labelsHidden()
                        Button("Choose…", action: choose)
                    }
                }
                LabeledContent("Detected") {
                    VStack(alignment: .leading, spacing: 8) {
                        Button("Detect now", action: detect)
                        if let d = detected {
                            Text(readout(d))
                                .font(.system(.callout, design: .monospaced))
                                .foregroundStyle(d.resolved ? .secondary : Theme.accent)
                                .textSelection(.enabled)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                    }
                }
            }
            .formStyle(.columns)

            Text(model.store.configURL.path)
                .font(.callout).foregroundStyle(.secondary)
                .textSelection(.enabled)

            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("Save") {
                    model.applySettings(bundleIdentifier: bundleId.trimmingCharacters(in: .whitespaces),
                                        appPathOverride: appPath.trimmingCharacters(in: .whitespaces))
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
                .buttonStyle(.borderedProminent)
            }
        }
        .padding(20)
        .frame(width: 580)
        .tint(Theme.accent)
        .onAppear {
            bundleId = model.store.config.bundleIdentifier
            appPath = model.store.config.appPathOverride
            detect()
        }
    }

    // Detection ignores the override, so the readout shows what Launch Services would pick.
    private func detect() {
        detected = ClaudeApp.resolve(HydraConfig(bundleIdentifier: bundleId.trimmingCharacters(in: .whitespaces)))
    }

    private func readout(_ d: AppInfo) -> String {
        var lines = [
            "App       \(d.url?.path ?? "-")",
            "Bundle    \(d.bundleIdentifier)",
            "Version   \(d.version ?? "-")",
        ]
        if let e = d.error { lines.append("Note      \(e)") }
        return lines.joined(separator: "\n")
    }

    private func choose() {
        let panel = NSOpenPanel()
        panel.message = "Choose the Claude app to launch"
        panel.allowedContentTypes = [.application]
        panel.directoryURL = URL(fileURLWithPath: "/Applications")
        if panel.runModal() == .OK, let url = panel.url { appPath = url.path }
    }
}
