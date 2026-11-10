import AppKit
import SwiftUI

// The shared design language. Values match windows/VoiceFlow.Desktop/Theme/Tokens.axaml:
// one monochrome palette per appearance, and one accent (Signal) for recording and attention only.

/// Colours as 0xAARRGGBB, the same notation the Windows tokens use.
enum Palette {
    static let window = adaptive(light: 0xFFFAFAFA, dark: 0xFF151515)
    static let sidebar = adaptive(light: 0xFFF1F1F1, dark: 0xFF0F0F0F)
    static let surface = adaptive(light: 0xFFFFFFFF, dark: 0xFF1C1C1C)
    static let surfaceRaised = adaptive(light: 0xFFFFFFFF, dark: 0xFF232323)
    static let surfaceInset = adaptive(light: 0xFFF5F5F5, dark: 0xFF151515)
    static let hairline = adaptive(light: 0x1A000000, dark: 0x1FFFFFFF)
    static let hairlineStrong = adaptive(light: 0x33000000, dark: 0x38FFFFFF)
    static let foreground = adaptive(light: 0xFF161616, dark: 0xFFF2F2F2)
    static let muted = adaptive(light: 0xFF5C5C5C, dark: 0xFFA3A3A3)
    static let subtle = adaptive(light: 0xFF737373, dark: 0xFF8A8A8A)
    static let hover = adaptive(light: 0x0D000000, dark: 0x14FFFFFF)
    static let pressed = adaptive(light: 0x14000000, dark: 0x0CFFFFFF)
    static let selected = adaptive(light: 0x12000000, dark: 0x1CFFFFFF)
    static let focusRing = adaptive(light: 0xCC000000, dark: 0xD9FFFFFF)
    static let primary = adaptive(light: 0xFF161616, dark: 0xFFF2F2F2)
    static let primaryHover = adaptive(light: 0xFF2E2E2E, dark: 0xFFFFFFFF)
    static let primaryPressed = adaptive(light: 0xFF000000, dark: 0xFFD1D1D1)
    static let onPrimary = adaptive(light: 0xFFFFFFFF, dark: 0xFF0F0F0F)
    /// Recording and attention. Nothing else may use this colour.
    static let signal = adaptive(light: 0xFFCF3A1A, dark: 0xFFF2613F)
    static let signalSoft = adaptive(light: 0x1FCF3A1A, dark: 0x26F2613F)
    static let track = adaptive(light: 0xFFE8E8E8, dark: 0xFF2B2B2B)
    static let meterFill = adaptive(light: 0xFF161616, dark: 0xFFF2F2F2)
    static let heat: [Color] = [
        adaptive(light: 0xFFEBEBEB, dark: 0xFF2A2A2A),
        adaptive(light: 0xFFC4C4C4, dark: 0xFF4D4D4D),
        adaptive(light: 0xFF8F8F8F, dark: 0xFF808080),
        adaptive(light: 0xFF555555, dark: 0xFFB8B8B8),
        adaptive(light: 0xFF161616, dark: 0xFFF2F2F2),
    ]

    private static func adaptive(light: UInt32, dark: UInt32) -> Color {
        Color(nsColor: NSColor(name: nil) { appearance in
            appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua ? color(dark) : color(light)
        })
    }

    private static func color(_ argb: UInt32) -> NSColor {
        NSColor(srgbRed: CGFloat((argb >> 16) & 0xFF) / 255,
                green: CGFloat((argb >> 8) & 0xFF) / 255,
                blue: CGFloat(argb & 0xFF) / 255,
                alpha: CGFloat((argb >> 24) & 0xFF) / 255)
    }
}

/// 8-point rhythm. 12 is the one half step, used inside controls.
enum Spacing {
    static let xxs: CGFloat = 4
    static let xs: CGFloat = 8
    static let sm: CGFloat = 12
    static let md: CGFloat = 16
    static let lg: CGFloat = 24
    static let xl: CGFloat = 32
    static let xxl: CGFloat = 40
}

enum Radius {
    static let control: CGFloat = 6
    static let overlay: CGFloat = 8
    static let card: CGFloat = 12
}

enum TypeScale {
    static let pageTitle = Font.system(size: 28, weight: .bold)
    static let sectionTitle = Font.system(size: 18, weight: .bold)
    static let cardTitle = Font.system(size: 15, weight: .semibold)
    static let statValue = Font.system(size: 28, weight: .bold)
    static let statLarge = Font.system(size: 34, weight: .bold)
    static let body = Font.system(size: 14)
    static let bodyMedium = Font.system(size: 14, weight: .medium)
    static let callout = Font.system(size: 13)
    static let calloutMedium = Font.system(size: 13, weight: .medium)
    static let caption = Font.system(size: 12)
    static let label = Font.system(size: 12, weight: .semibold)
    static let micro = Font.system(size: 11)
}

/// Layout that repeats across pages.
enum Metrics {
    static let pagePadding: CGFloat = 40
    static let contentWidth: CGFloat = 840
    static let wideContentWidth: CGFloat = 920
    static let listColumnWidth: CGFloat = 264
    static let sidebarWidth: CGFloat = 208
}
