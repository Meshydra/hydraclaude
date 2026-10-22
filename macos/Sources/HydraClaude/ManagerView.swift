// The manager window - one card: a header naming the section with the two app-level actions,
// the profile list, and a footer with system status on the left and exactly one primary action
// on the right. Everything per-row that isn't "open it" lives in the row's context menu.

import HydraClaudeCore
import SwiftUI

struct ManagerView: View {
    @Bindable var model: ManagerModel
    var onHide: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            list
            Divider()
            footer
        }
        .background(Theme.windowBg)
        .tint(Theme.accent)
        .frame(minWidth: 520, minHeight: 320)
        .onExitCommand(perform: onHide)
        .sheet(item: $model.sheet) { sheet in
            switch sheet {
            case .new: ProfileSheet(model: model, editing: nil)
            case .edit(let id): ProfileSheet(model: model, editing: model.profile(id))
            case .remove(let id):
                if let p = model.profile(id) { RemoveSheet(model: model, profile: p) }
            case .settings: SettingsSheet(model: model)
            }
        }
        .alert(item: $model.notice) { n in
            Alert(title: Text(n.title), message: Text(n.message))
        }
    }

    private var header: some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Text("Profiles").font(.title2.weight(.semibold))
            Text("\(model.profiles.count)").foregroundStyle(.secondary)
            Spacer()
            Button("New profile") { model.sheet = .new }
            Button("Settings") { model.sheet = .settings }
        }
        .padding(.horizontal, 20)
        .frame(height: 56)
        .background(Theme.surfaceBg)
    }

    private var list: some View {
        List(selection: $model.selection) {
            ForEach(model.profiles) { p in
                ProfileRow(profile: p, path: model.dataPath(of: p), running: model.isRunning(p))
                    .tag(p.id)
            }
        }
        .listStyle(.inset)
        .scrollContentBackground(.hidden)
        .contextMenu(forSelectionType: UUID.self) { ids in
            if let id = ids.first {
                Button("Open profile") { model.launch(id) }
                Button("Edit…") { model.sheet = .edit(id) }
                Button("Show in Finder") { model.revealInFinder(id) }
                Button("Create desktop shortcut") { model.makeShortcut(id) }
                Divider()
                Button("Remove…") { model.sheet = .remove(id) }
            }
        } primaryAction: { ids in
            // Double-click or Return.
            if let id = ids.first { model.launch(id) }
        }
        .overlay {
            if model.profiles.isEmpty {
                Text("No profiles yet. Create one with New profile.")
                    .foregroundStyle(.secondary)
            }
        }
    }

    private var footer: some View {
        let status = model.status
        let id = model.selection
        return HStack(spacing: 8) {
            Text(status.text)
                .foregroundStyle(status.warning ? Theme.accent : .secondary)
                .lineLimit(1)
                .truncationMode(.tail)
                .help(status.text)
            Spacer(minLength: 12)
            Button("Show in Finder") { if let id { model.revealInFinder(id) } }
                .disabled(id == nil)
            Button("Edit") { if let id { model.sheet = .edit(id) } }
                .disabled(id == nil)
            Button("Open profile") { if let id { model.launch(id) } }
                .buttonStyle(.borderedProminent)
                .disabled(id == nil)
        }
        .controlSize(.large)
        .padding(.horizontal, 20)
        .frame(height: 60)
    }
}

struct ProfileRow: View {
    let profile: ProfileEntry
    let path: String
    let running: Bool

    var body: some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 8) {
                    Text(profile.name).font(.body.weight(.semibold))
                    if profile.isStock {
                        Text("Stock install").font(.caption).foregroundStyle(.secondary)
                    }
                }
                Text(path)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
            Spacer(minLength: 12)
            if running {
                Text("Running")
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(Theme.liveText)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 2)
                    .background(Theme.liveBg, in: Capsule())
            }
            Text(Self.when(profile.lastLaunched))
                .font(.callout)
                .foregroundStyle(.secondary)
                .monospacedDigit()
                .frame(minWidth: 90, alignment: .trailing)
        }
        .padding(.vertical, 6)
        .contentShape(Rectangle())
    }

    static func when(_ iso: String) -> String {
        guard let date = Launcher.parseTimestamp(iso) else { return "" }
        if Calendar.current.isDateInToday(date) {
            return date.formatted(date: .omitted, time: .shortened)
        }
        return date.formatted(.dateTime.day().month(.abbreviated).hour().minute())
    }
}
