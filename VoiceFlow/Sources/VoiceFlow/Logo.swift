import AppKit
import SwiftUI

/// The VoiceFlow mark: five sound-wave bars flowing into a text cursor. Same geometry as the app icon
/// (scripts/make_icon.swift).
enum LogoGeometry {
    static let heights: [CGFloat] = [0.34, 0.62, 1.0, 0.70, 0.42]

    /// Rounded rectangles for the mark inside `rect` (y grows upward, as in Core Graphics).
    static func shapes(in rect: CGRect) -> [CGRect] {
        let barWidth = rect.width * 0.105
        let gap = rect.width * 0.062
        var out: [CGRect] = []
        var x = rect.minX
        for h in heights {
            let height = rect.height * h
            out.append(CGRect(x: x, y: rect.midY - height / 2, width: barWidth, height: height))
            x += barWidth + gap
        }
        let cursorX = x + gap * 0.6
        let stem = rect.width * 0.078
        let serif = rect.width * 0.16
        out.append(CGRect(x: cursorX + (serif - stem) / 2, y: rect.minY, width: stem, height: rect.height))
        out.append(CGRect(x: cursorX, y: rect.maxY - stem, width: serif, height: stem))
        out.append(CGRect(x: cursorX, y: rect.minY, width: serif, height: stem))
        return out
    }
}

/// The logo for windows: black tile with the white mark.
struct LogoMark: View {
    var size: CGFloat = 40

    var body: some View {
        Canvas { context, canvasSize in
            let s = canvasSize.width
            let tile = Path(roundedRect: CGRect(x: 0, y: 0, width: s, height: s), cornerRadius: s * 0.225, style: .continuous)
            context.fill(tile, with: .color(.black))
            // Hairline edge so the tile stays visible on dark backgrounds.
            context.stroke(tile, with: .color(.white.opacity(0.16)), lineWidth: max(0.5, s / 160))
            // Mark area: 560×400 of an 824 tile, centred.
            let w = s * 560 / 824, h = s * 400 / 824
            let rect = CGRect(x: (s - w) / 2, y: (s - h) / 2, width: w, height: h)
            for r in LogoGeometry.shapes(in: rect) {
                // Flip vertically (Canvas y grows downward); the mark is symmetric except the cursor serifs.
                let flipped = CGRect(x: r.minX, y: s - r.maxY, width: r.width, height: r.height)
                let radius = min(r.width, r.height) / 2
                context.fill(Path(roundedRect: flipped, cornerRadius: radius), with: .color(.white))
            }
        }
        .frame(width: size, height: size)
        .accessibilityLabel("VoiceFlow")
    }
}

/// Menu-bar icon: the mark alone as a template image, so macOS tints it for light and dark menu bars.
enum MenuBarIcon {
    enum Badge { case none, dot }

    static func image(badge: Badge = .none) -> NSImage {
        let size = NSSize(width: 20, height: 16)
        let image = NSImage(size: size, flipped: false) { bounds in
            NSColor.black.setFill()
            let rect = CGRect(x: 1, y: 2, width: badge == .dot ? 12.5 : 17, height: 12)
            for r in LogoGeometry.shapes(in: rect) {
                let radius = min(r.width, r.height) / 2
                NSBezierPath(roundedRect: r, xRadius: radius, yRadius: radius).fill()
            }
            if badge == .dot {
                NSBezierPath(ovalIn: CGRect(x: bounds.maxX - 5, y: bounds.maxY - 6, width: 5, height: 5)).fill()
            }
            return true
        }
        image.isTemplate = true
        image.accessibilityDescription = "VoiceFlow"
        return image
    }
}
