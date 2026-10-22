import Foundation
import Testing
@testable import HydraClaudeCore

private func tempRoot() throws -> URL {
    let url = FileManager.default.temporaryDirectory
        .appendingPathComponent("hydraclaude-tests-\(UUID().uuidString)", isDirectory: true)
    try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

@Suite struct ArgumentsTests {
    @Test func stockProfileHasNoDataDir() {
        #expect(Arguments.build(for: ProfileEntry(name: "Default")) == [])
    }

    @Test func dataDirIsOneArgvEntryEvenWithSpaces() {
        let p = ProfileEntry(name: "Work", dir: "/Users/me/Library/Application Support/ClaudeProfiles/Work/")
        #expect(Arguments.build(for: p) == ["--user-data-dir=/Users/me/Library/Application Support/ClaudeProfiles/Work"])
    }

    @Test func extraArgsFollowTheDataDir() {
        let p = ProfileEntry(name: "W", dir: "/tmp/w", extraArgs: "--lang=en-AU --flag")
        #expect(Arguments.build(for: p) == ["--user-data-dir=/tmp/w", "--lang=en-AU", "--flag"])
    }

    @Test(arguments: [
        ("", [String]()),
        ("  a   b ", ["a", "b"]),
        ("--x=\"one two\" 'it''s'", ["--x=one two", "its"]),
        ("a\\ b", ["a b"]),
        ("\"say \\\"hi\\\"\"", ["say \"hi\""]),
        ("''", [""]),
    ])
    func split(input: String, expected: [String]) {
        #expect(Arguments.split(input) == expected)
    }

    @Test func dataDirInBothChromiumForms() {
        #expect(Arguments.dataDir(in: ["/x/Claude", "--user-data-dir=/a b"]) == "/a b")
        #expect(Arguments.dataDir(in: ["/x/Claude", "--user-data-dir", "/c"]) == "/c")
        #expect(Arguments.dataDir(in: ["/x/Claude", "--type=gpu-process"]) == nil)
        #expect(Arguments.dataDir(in: ["/x/Claude", "--user-data-dir"]) == nil)
    }
}

@Suite struct PathsTests {
    @Test func normalizeFoldsSpellings() {
        #expect(Paths.normalize("/tmp/Foo/") == Paths.normalize("/private/tmp/foo"))
        #expect(Paths.normalize("/a/b/../c") == "/a/c")
        #expect(Paths.normalize("") == "")
        #expect(Paths.normalize("~") == FileManager.default.homeDirectoryForCurrentUser.path.lowercased())
    }

    @Test func trimKeepsRoot() {
        #expect(Paths.trimTrailingSlash("/") == "/")
        #expect(Paths.trimTrailingSlash("/a//") == "/a")
    }
}

@Suite struct ProcArgsTests {
    private func buffer(exec: String, args: [String], env: [String]) -> [UInt8] {
        var b = withUnsafeBytes(of: Int32(args.count)) { Array($0) }
        b += Array(exec.utf8) + [0, 0, 0, 0]
        for a in args + env { b += Array(a.utf8) + [0] }
        return b
    }

    @Test func parsesArgvAndStopsBeforeEnvironment() {
        let buf = buffer(exec: "/Applications/Claude.app/Contents/MacOS/Claude",
                         args: ["/Applications/Claude.app/Contents/MacOS/Claude", "--user-data-dir=/x y"],
                         env: ["HOME=/Users/me"])
        #expect(RunningScan.parseProcArgs(buf) == ["/Applications/Claude.app/Contents/MacOS/Claude", "--user-data-dir=/x y"])
    }

    @Test func rejectsTruncatedBuffers() {
        #expect(RunningScan.parseProcArgs([1, 0]) == nil)
        let buf = buffer(exec: "/bin/x", args: ["/bin/x"], env: [])
        var claimsMore = buf
        claimsMore.replaceSubrange(0..<4, with: withUnsafeBytes(of: Int32(3)) { Array($0) })
        #expect(RunningScan.parseProcArgs(claimsMore) == nil)
    }

    @Test func readsOwnProcess() throws {
        let mine = try #require(RunningScan.argv(of: ProcessInfo.processInfo.processIdentifier))
        #expect(mine == CommandLine.arguments)
    }

    @Test func goneProcessIsNil() {
        #expect(RunningScan.argv(of: 999_999) == nil)
    }

    @Test func scanMapsOwnProcessToStockWhenNoFlag() {
        let me = ProcessInfo.processInfo.processIdentifier
        let found = RunningScan.scan(pids: [me], stockDir: "/tmp/stock")
        #expect(found == [Paths.normalize("/tmp/stock"): me])
    }
}

@Suite struct StoreTests {
    @Test func firstRunSeedsDefaultAndAdoptsFolders() throws {
        let root = try tempRoot()
        for d in ["Work", "Client", ".hidden"] {
            try FileManager.default.createDirectory(at: root.appendingPathComponent(d), withIntermediateDirectories: true)
        }
        try Data("x".utf8).write(to: root.appendingPathComponent("notes.txt"))

        let store = Store.load(root: root)
        #expect(store.config.profiles.map(\.name) == ["Default", "Client", "Work"])
        #expect(store.config.profiles[0].isStock)
        #expect(store.config.profiles[2].dir == root.appendingPathComponent("Work").path)
        #expect(store.config.bundleIdentifier == ClaudeApp.defaultBundleIdentifier)
    }

    @Test func saveThenLoadRoundTrips() throws {
        let root = try tempRoot()
        let store = Store.load(root: root)
        store.config.profiles.append(ProfileEntry(name: "Work", dir: "/x/Work", extraArgs: "--a", lastLaunched: "2026-10-22T01:02:03.456Z"))
        store.config.trayHintShown = true
        try store.save()

        let again = Store.load(root: root)
        #expect(again.config.trayHintShown)
        #expect(again.find("work")?.extraArgs == "--a")
        #expect(again.find("WORK")?.lastLaunched == "2026-10-22T01:02:03.456Z")
    }

    @Test func savedFileIsHandEditable() throws {
        let root = try tempRoot()
        let store = Store.load(root: root)
        try store.save()
        let text = try String(contentsOf: store.configURL, encoding: .utf8)
        #expect(text.contains("\n  \"profiles\""))
        #expect(!text.contains("\"id\""))
        #expect(!text.contains("\\/"))
        // Stable order, so saving twice never churns the file.
        try store.save()
        #expect(try String(contentsOf: store.configURL, encoding: .utf8) == text)
        #expect(text.range(of: "\"appPathOverride\"")!.lowerBound < text.range(of: "\"trayHintShown\"")!.lowerBound)
    }

    @Test func corruptConfigIsMovedAsideAndReseeded() throws {
        let root = try tempRoot()
        let url = root.appendingPathComponent("hydraclaude.json")
        try Data("{ not json".utf8).write(to: url)

        let store = Store.load(root: root)
        #expect(store.config.profiles.map(\.name) == ["Default"])
        let bad = try String(contentsOf: url.appendingPathExtension("bad"), encoding: .utf8)
        #expect(bad == "{ not json")
    }

    @Test func missingKeysTakeDefaults() throws {
        let root = try tempRoot()
        try Data(#"{"profiles":[{"name":"Solo"}]}"#.utf8).write(to: root.appendingPathComponent("hydraclaude.json"))
        let store = Store.load(root: root)
        #expect(store.config.profiles.count == 1)
        #expect(store.config.profiles[0].isStock)
        #expect(store.config.bundleIdentifier == ClaudeApp.defaultBundleIdentifier)
        #expect(store.config.schema == 1)
    }
}

@Suite struct ClaudeAppTests {
    @Test func detectedThroughLookup() {
        let info = ClaudeApp.resolve(HydraConfig(), lookup: { _ in URL(fileURLWithPath: "/Applications/Claude.app") })
        #expect(info.source == .detected)
        #expect(info.bundleIdentifier == "com.anthropic.claudefordesktop")
    }

    @Test func missingWhenLookupFails() {
        let info = ClaudeApp.resolve(HydraConfig(bundleIdentifier: "  x.y  "), lookup: { _ in nil })
        #expect(info.source == .missing)
        #expect(info.bundleIdentifier == "x.y")
        #expect(info.error?.contains("x.y") == true)
    }

    @Test func overrideWinsWhenItIsAnApp() {
        let finder = "/System/Library/CoreServices/Finder.app"
        let info = ClaudeApp.resolve(HydraConfig(appPathOverride: finder), lookup: { _ in nil })
        #expect(info.source == .override)
        #expect(info.url?.path == finder)
        #expect(info.version != nil)
    }

    @Test func badOverrideFallsBackToDetection() {
        let info = ClaudeApp.resolve(HydraConfig(appPathOverride: "/nope.app"),
                                     lookup: { _ in URL(fileURLWithPath: "/Applications/Claude.app") })
        #expect(info.source == .detected)
    }
}

@Suite struct ValidationTests {
    let existing = [ProfileEntry(name: "Default"), ProfileEntry(name: "Work", dir: "/tmp/hc/Work")]

    @Test func rules() {
        #expect(ProfileValidation.check(name: " ", dir: "/a", useStock: false, editing: nil, in: existing) == .emptyName)
        #expect(ProfileValidation.check(name: "a/b", dir: "/a", useStock: false, editing: nil, in: existing) == .badName)
        #expect(ProfileValidation.check(name: ".x", dir: "/a", useStock: false, editing: nil, in: existing) == .badName)
        #expect(ProfileValidation.check(name: "work", dir: "/a", useStock: false, editing: nil, in: existing) == .nameTaken("Work"))
        #expect(ProfileValidation.check(name: "New", dir: "", useStock: false, editing: nil, in: existing) == .noFolder)
        #expect(ProfileValidation.check(name: "New", dir: "/TMP/hc/work/", useStock: false, editing: nil, in: existing) == .folderTaken("Work"))
        #expect(ProfileValidation.check(name: "New", dir: "/tmp/hc/New", useStock: false, editing: nil, in: existing) == nil)
        #expect(ProfileValidation.check(name: "New", dir: "", useStock: true, editing: nil, in: existing) == nil)
    }

    @Test func editingDoesNotClashWithItself() {
        let work = existing[1]
        #expect(ProfileValidation.check(name: "Work", dir: work.dir, useStock: false, editing: work.id, in: existing) == nil)
    }
}

@Suite struct ShortcutTests {
    @Test func scriptQuotesHostileNames() {
        let s = Shortcuts.script(executable: "/Users/me/Applications/HydraClaude.app/Contents/MacOS/HydraClaude",
                                 profile: "Bob's $(rm -rf ~)")
        #expect(s.contains(#"exec '/Users/me/Applications/HydraClaude.app/Contents/MacOS/HydraClaude' 'Bob'\''s $(rm -rf ~)'"#))
    }

    @Test func createsARunnableBundle() throws {
        let folder = try tempRoot()
        let app = try Shortcuts.create(profile: "A/B: C", executable: "/bin/echo", icon: nil, in: folder)
        #expect(app.lastPathComponent == "Claude - A-B- C.app")

        let launcher = app.appendingPathComponent("Contents/MacOS/launch")
        #expect(FileManager.default.isExecutableFile(atPath: launcher.path))

        let plist = try #require(NSDictionary(contentsOf: app.appendingPathComponent("Contents/Info.plist")))
        #expect(plist["CFBundleExecutable"] as? String == "launch")
        #expect(plist["LSUIElement"] as? Bool == true)

        let run = Process()
        run.executableURL = launcher
        let pipe = Pipe()
        run.standardOutput = pipe
        try run.run()
        run.waitUntilExit()
        let out = String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        #expect(out == "A/B: C\n")
    }
}
