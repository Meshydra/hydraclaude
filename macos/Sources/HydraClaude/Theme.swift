// Palette - the same warm neutrals and rose accent as Theme.cs, as dynamic colours that follow
// the macOS appearance live. AppKit supplies the window chrome, controls and type, so only the
// colours that carry HydraClaude's identity are defined here.

import AppKit
import SwiftUI

enum Theme {
    static let accent = dynamic(light: 0xB23A4C, dark: 0xE2687C)
    static let liveText = dynamic(light: 0x2C6E49, dark: 0x7BD3A0)
    static let liveBg = dynamic(light: 0xE6F1E9, dark: 0x1C2F25)
    static let windowBg = dynamic(light: 0xF7F3F3, dark: 0x171516)
    static let surfaceBg = dynamic(light: 0xFDFCFC, dark: 0x1E1B1D)

    private static func dynamic(light: UInt32, dark: UInt32) -> Color {
        Color(nsColor: NSColor(name: nil) { appearance in
            let isDark = appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
            return rgb(isDark ? dark : light)
        })
    }

    private static func rgb(_ v: UInt32) -> NSColor {
        NSColor(srgbRed: CGFloat((v >> 16) & 0xFF) / 255,
                green: CGFloat((v >> 8) & 0xFF) / 255,
                blue: CGFloat(v & 0xFF) / 255, alpha: 1)
    }

    // Escape hatch for checking both palettes without touching the system setting.
    @MainActor
    static func applyForcedAppearance() {
        switch ProcessInfo.processInfo.environment["HYDRACLAUDE_THEME"]?.lowercased() {
        case "dark": NSApp.appearance = NSAppearance(named: .darkAqua)
        case "light": NSApp.appearance = NSAppearance(named: .aqua)
        default: break
        }
    }
}
