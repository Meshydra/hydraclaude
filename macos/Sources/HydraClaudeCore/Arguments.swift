// Turning a profile into an argv, and reading one back out of a running process.
//
// On Windows the activation API takes the arguments as one string. NSWorkspace takes an array,
// so the free-text "extra args" field is split here with shell-style quoting rules.

import Foundation

public enum Arguments {
    public static let dataDirFlag = "--user-data-dir"

    public static func build(for p: ProfileEntry) -> [String] {
        var args: [String] = []
        if !p.isStock { args.append("\(dataDirFlag)=\(Paths.trimTrailingSlash(p.dir))") }
        args.append(contentsOf: split(p.extraArgs))
        return args
    }

    // Whitespace separates words; single quotes are literal, double quotes allow \" and \\,
    // and a backslash outside quotes escapes the next character. Enough for Electron flags.
    public static func split(_ s: String) -> [String] {
        var out: [String] = []
        var cur = ""
        var inWord = false
        var quote: Character?
        var escaping = false

        for ch in s {
            if escaping {
                cur.append(ch); escaping = false; inWord = true
                continue
            }
            if let q = quote {
                if ch == q { quote = nil }
                else if ch == "\\" && q == "\"" { escaping = true }
                else { cur.append(ch) }
                continue
            }
            switch ch {
            case "'", "\"": quote = ch; inWord = true
            case "\\": escaping = true
            case _ where ch.isWhitespace:
                if inWord { out.append(cur); cur = ""; inWord = false }
            default: cur.append(ch); inWord = true
            }
        }
        if inWord { out.append(cur) }
        return out
    }

    // Returns nil when the argv says nothing about the data directory. Chromium accepts both
    // `--user-data-dir=/path` and `--user-data-dir /path`.
    public static func dataDir(in argv: [String]) -> String? {
        let prefix = dataDirFlag + "="
        for (i, a) in argv.enumerated() {
            if a.hasPrefix(prefix) { return String(a.dropFirst(prefix.count)) }
            if a == dataDirFlag, i + 1 < argv.count { return argv[i + 1] }
        }
        return nil
    }
}

public enum Paths {
    public static func trimTrailingSlash(_ p: String) -> String {
        var s = p
        while s.count > 1 && s.hasSuffix("/") { s.removeLast() }
        return s
    }

    // One spelling per folder: tilde expanded, symlinks resolved (/tmp is /private/tmp), no
    // trailing slash, and lowercased because the default APFS volume is case-insensitive.
    //
    // realpath(3) rather than URL.resolvingSymlinksInPath: the latter strips a leading /private
    // and gives up on paths that don't exist yet, so /tmp/x and /private/tmp/x would differ.
    // Resolving the nearest existing ancestor covers a profile whose folder isn't created yet.
    public static func normalize(_ p: String) -> String {
        if p.isEmpty { return "" }
        let expanded = (p as NSString).expandingTildeInPath
        var head = URL(fileURLWithPath: expanded).standardizedFileURL.path
        var tail: [String] = []
        while head != "/" {
            if let real = realpath(head, nil) {
                defer { free(real) }
                head = String(cString: real)
                break
            }
            tail.insert((head as NSString).lastPathComponent, at: 0)
            head = (head as NSString).deletingLastPathComponent
        }
        let joined = tail.reduce(head) { ($0 as NSString).appendingPathComponent($1) }
        return trimTrailingSlash(joined).lowercased()
    }
}
