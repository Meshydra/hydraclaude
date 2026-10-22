// Which profiles currently have a Claude instance up - the macOS counterpart of RunningScan
// and RunningTracker in HydraClaude.cs.
//
// Two differences from Windows, both confirmed against a live Claude 3.x:
//
//   * The main process *does* carry --user-data-dir when it was launched with one, so there is
//     no need to read every helper. A main process with no flag is the stock instance, whose
//     helpers show ~/Library/Application Support/Claude.
//   * Launch Services already tracks every running instance (NSRunningApplication) and posts a
//     notification when one starts or quits, so there is no process-count poll. Reading the
//     argv of the two or three Claude main processes through sysctl(KERN_PROCARGS2) is the only
//     per-scan cost.
//
// Each data folder maps to its main process id, which is what lets a second click on a running
// profile bring that instance forward instead of starting a duplicate. Claude does not refuse a
// second instance on the same folder - it starts a full second copy that shares the profile.

import AppKit
import Darwin

public enum RunningScan {
    // Arguments of another process of the same user, or nil if it has gone or is not ours.
    public static func argv(of pid: pid_t) -> [String]? {
        var mib: [Int32] = [CTL_KERN, KERN_PROCARGS2, pid]
        var size = 0
        guard sysctl(&mib, 3, nil, &size, nil, 0) == 0, size > 0 else { return nil }
        var buf = [UInt8](repeating: 0, count: size)
        guard sysctl(&mib, 3, &buf, &size, nil, 0) == 0 else { return nil }
        return parseProcArgs(Array(buf.prefix(size)))
    }

    // KERN_PROCARGS2 layout: Int32 argc, the exec path, NUL padding, then argc NUL-terminated
    // strings, then the environment (ignored).
    public static func parseProcArgs(_ buf: [UInt8]) -> [String]? {
        let intSize = MemoryLayout<Int32>.size
        guard buf.count >= intSize else { return nil }
        let argc = buf.withUnsafeBytes { Int($0.loadUnaligned(as: Int32.self)) }
        guard argc >= 0 else { return nil }

        var i = intSize
        while i < buf.count && buf[i] != 0 { i += 1 }      // exec path
        while i < buf.count && buf[i] == 0 { i += 1 }      // padding

        var args: [String] = []
        while args.count < argc && i < buf.count {
            let start = i
            while i < buf.count && buf[i] != 0 { i += 1 }
            args.append(String(decoding: buf[start..<i], as: UTF8.self))
            i += 1
        }
        return args.count == argc ? args : nil
    }

    // Normalised data folder -> pid of the instance that owns it.
    public static func scan(pids: [pid_t], stockDir: String = Store.stockDir) -> [String: pid_t] {
        var found: [String: pid_t] = [:]
        let stock = Paths.normalize(stockDir)
        for pid in pids {
            guard let args = argv(of: pid) else { continue }
            let dir = Arguments.dataDir(in: args).map(Paths.normalize) ?? stock
            if found[dir] == nil { found[dir] = pid }
        }
        return found
    }

    public static func claudePids(bundleIdentifier: String) -> [pid_t] {
        NSRunningApplication.runningApplications(withBundleIdentifier: bundleIdentifier)
            .filter { !$0.isTerminated }
            .map(\.processIdentifier)
    }
}

// Holds the last known answer; refresh() reports whether it moved.
public final class RunningTracker: @unchecked Sendable {
    private let lock = NSLock()
    private var byDir: [String: pid_t] = [:]

    public init() {}

    public var dirs: [String: pid_t] {
        lock.lock(); defer { lock.unlock() }
        return byDir
    }

    // Returns true when the set of running folders changed. Safe off the main thread.
    @discardableResult
    public func refresh(bundleIdentifier: String) -> Bool {
        let found = RunningScan.scan(pids: RunningScan.claudePids(bundleIdentifier: bundleIdentifier))
        lock.lock(); defer { lock.unlock() }
        if found == byDir { return false }
        byDir = found
        return true
    }

    public func pid(for p: ProfileEntry) -> pid_t? {
        dirs[Paths.normalize(p.isStock ? Store.stockDir : p.dir)]
    }

    public func isRunning(_ p: ProfileEntry) -> Bool { pid(for: p) != nil }
}
