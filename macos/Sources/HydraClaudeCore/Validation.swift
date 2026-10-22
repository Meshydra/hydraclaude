// The rules the profile sheet enforces - the macOS counterpart of ProfileDialog.Commit.

import Foundation

public enum ProfileValidation {
    public enum Problem: Equatable, CustomStringConvertible {
        case emptyName, badName, nameTaken(String), noFolder, folderTaken(String)

        public var description: String {
            switch self {
            case .emptyName: return "Give the profile a name."
            case .badName: return "A profile name cannot contain / or : or start with a full stop."
            case .nameTaken(let n): return "There is already a profile called \"\(n)\"."
            case .noFolder: return "Choose a data folder, or tick the stock data folder box."
            case .folderTaken(let n): return "That folder already belongs to the \"\(n)\" profile. Two profiles cannot share one folder."
            }
        }
    }

    public static func check(name rawName: String, dir rawDir: String, useStock: Bool,
                             editing id: UUID?, in profiles: [ProfileEntry]) -> Problem? {
        let name = rawName.trimmingCharacters(in: .whitespaces)
        if name.isEmpty { return .emptyName }
        if name.contains("/") || name.contains(":") || name.hasPrefix(".") { return .badName }
        if let clash = profiles.first(where: { $0.id != id && $0.name.caseInsensitiveCompare(name) == .orderedSame }) {
            return .nameTaken(clash.name)
        }
        if useStock { return nil }
        let dir = rawDir.trimmingCharacters(in: .whitespaces)
        if dir.isEmpty { return .noFolder }
        let norm = Paths.normalize(dir)
        if let taken = profiles.first(where: { $0.id != id && !$0.isStock && Paths.normalize($0.dir) == norm }) {
            return .folderTaken(taken.name)
        }
        return nil
    }
}
