// Draws the VoiceFlow app icon and writes VoiceFlow/Support/AppIcon.icns (macOS),
// windows/VoiceFlow.Windows/Assets/VoiceFlow.ico (Windows) and docs/assets/icon.png (README).
//   swift scripts/make_icon.swift
// The mark: five sound-wave bars that flow into a text cursor (voice becoming text), white on a
// black tile. The same geometry is drawn in the app by LogoMark (Sources/VoiceFlow/Logo.swift).
import AppKit

let root = URL(fileURLWithPath: CommandLine.arguments[0]).deletingLastPathComponent().deletingLastPathComponent()
let iconset = FileManager.default.temporaryDirectory.appending(path: "AppIcon.iconset")
try? FileManager.default.removeItem(at: iconset)
try FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)

/// `fullBleed` draws the tile edge to edge with no shadow margin, as Windows icons expect.
func drawIcon(size: CGFloat, fullBleed: Bool = false) -> NSImage {
    NSImage(size: NSSize(width: size, height: size), flipped: false) { _ in
        guard let ctx = NSGraphicsContext.current?.cgContext else { return false }
        let s = size / 1024
        // Apple's icon grid: an 824×824 tile centred in 1024, continuous-corner radius ~185.
        let inset: CGFloat = fullBleed ? 16 : 100
        let tile = CGRect(x: inset * s, y: inset * s, width: (1024 - 2 * inset) * s, height: (1024 - 2 * inset) * s)
        let radius = tile.width * 185 / 824
        let path = CGPath(roundedRect: tile, cornerWidth: radius, cornerHeight: radius, transform: nil)

        if !fullBleed {
            // Soft drop shadow under the tile.
            ctx.saveGState()
            ctx.setShadow(offset: CGSize(width: 0, height: -10 * s), blur: 28 * s, color: NSColor.black.withAlphaComponent(0.28).cgColor)
            ctx.addPath(path)
            ctx.setFillColor(NSColor.black.cgColor)
            ctx.fillPath()
            ctx.restoreGState()
        }

        // Black tile with a faint top light, and a hairline edge so it stays visible on dark backgrounds.
        ctx.saveGState()
        ctx.addPath(path)
        ctx.clip()
        ctx.setFillColor(NSColor.black.cgColor)
        ctx.fill(tile)
        let gloss = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB),
                               colors: [NSColor.white.withAlphaComponent(0.10).cgColor, NSColor.white.withAlphaComponent(0).cgColor] as CFArray,
                               locations: [0, 1])!
        ctx.drawLinearGradient(gloss, start: CGPoint(x: tile.midX, y: tile.maxY), end: CGPoint(x: tile.midX, y: tile.midY), options: [])
        ctx.restoreGState()
        ctx.addPath(CGPath(roundedRect: tile.insetBy(dx: 2 * s, dy: 2 * s), cornerWidth: radius - 2 * s, cornerHeight: radius - 2 * s, transform: nil))
        ctx.setStrokeColor(NSColor.white.withAlphaComponent(0.16).cgColor)
        ctx.setLineWidth(max(1, 4 * s))
        ctx.strokePath()

        // The mark, in a 1024 design space, scaled with the tile.
        let k = tile.width / 824
        drawMark(ctx: ctx, in: CGRect(x: tile.midX - 280 * s * k, y: tile.midY - 200 * s * k, width: 560 * s * k, height: 400 * s * k), color: .white)
        return true
    }
}

/// Five bars rising to the centre, then a text cursor. Drawn inside `rect`.
func drawMark(ctx: CGContext, in rect: CGRect, color: NSColor) {
    let heights: [CGFloat] = [0.34, 0.62, 1.0, 0.70, 0.42]
    let barWidth = rect.width * 0.105
    let gap = rect.width * 0.062
    ctx.setFillColor(color.cgColor)
    var x = rect.minX
    for h in heights {
        let height = rect.height * h
        let bar = CGRect(x: x, y: rect.midY - height / 2, width: barWidth, height: height)
        ctx.addPath(CGPath(roundedRect: bar, cornerWidth: barWidth / 2, cornerHeight: barWidth / 2, transform: nil))
        ctx.fillPath()
        x += barWidth + gap
    }
    // Text cursor: a tall I-beam after the bars.
    let cursorX = x + gap * 0.6
    let stem = rect.width * 0.078
    let serif = rect.width * 0.16
    let top = rect.maxY, bottom = rect.minY
    ctx.addPath(CGPath(roundedRect: CGRect(x: cursorX + (serif - stem) / 2, y: bottom, width: stem, height: top - bottom),
                       cornerWidth: stem / 2, cornerHeight: stem / 2, transform: nil))
    ctx.addPath(CGPath(roundedRect: CGRect(x: cursorX, y: top - stem, width: serif, height: stem), cornerWidth: stem / 2, cornerHeight: stem / 2, transform: nil))
    ctx.addPath(CGPath(roundedRect: CGRect(x: cursorX, y: bottom, width: serif, height: stem), cornerWidth: stem / 2, cornerHeight: stem / 2, transform: nil))
    ctx.fillPath()
}

func writePNG(_ image: NSImage, pixels: Int, to url: URL) throws {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels, bitsPerSample: 8, samplesPerPixel: 4,
                               hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    rep.size = NSSize(width: pixels, height: pixels)
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    image.draw(in: NSRect(x: 0, y: 0, width: pixels, height: pixels))
    NSGraphicsContext.restoreGraphicsState()
    try rep.representation(using: .png, properties: [:])!.write(to: url)
}

let sizes: [(String, Int)] = [
    ("icon_16x16", 16), ("icon_16x16@2x", 32), ("icon_32x32", 32), ("icon_32x32@2x", 64),
    ("icon_128x128", 128), ("icon_128x128@2x", 256), ("icon_256x256", 256), ("icon_256x256@2x", 512),
    ("icon_512x512", 512), ("icon_512x512@2x", 1024),
]
for (name, px) in sizes {
    try writePNG(drawIcon(size: CGFloat(px)), pixels: px, to: iconset.appending(path: "\(name).png"))
}
let preview = root.appending(path: "build/AppIcon-1024.png")
try? FileManager.default.createDirectory(at: preview.deletingLastPathComponent(), withIntermediateDirectories: true)
try writePNG(drawIcon(size: 1024), pixels: 1024, to: preview)
let readmeIcon = root.appending(path: "docs/assets/icon.png")
try writePNG(drawIcon(size: 512), pixels: 512, to: readmeIcon)

// Windows .ico: PNG-compressed entries (supported since Windows Vista), largest last is not required.
func writeICO(sizes: [Int], to url: URL) throws {
    let pngs = try sizes.map { px -> Data in
        let tmp = FileManager.default.temporaryDirectory.appending(path: "ico-\(px).png")
        try writePNG(drawIcon(size: CGFloat(px), fullBleed: true), pixels: px, to: tmp)
        return try Data(contentsOf: tmp)
    }
    var data = Data()
    func u16(_ v: Int) { data.append(contentsOf: [UInt8(v & 0xFF), UInt8((v >> 8) & 0xFF)]) }
    func u32(_ v: Int) { u16(v & 0xFFFF); u16(v >> 16) }
    u16(0); u16(1); u16(sizes.count)
    var offset = 6 + 16 * sizes.count
    for (px, png) in zip(sizes, pngs) {
        data.append(UInt8(px >= 256 ? 0 : px)); data.append(UInt8(px >= 256 ? 0 : px))
        data.append(0); data.append(0)
        u16(1); u16(32); u32(png.count); u32(offset)
        offset += png.count
    }
    pngs.forEach { data.append($0) }
    try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
    try data.write(to: url)
}
let ico = root.appending(path: "windows/VoiceFlow.Windows/Assets/VoiceFlow.ico")
try writeICO(sizes: [16, 20, 24, 32, 40, 48, 64, 128, 256], to: ico)

let output = root.appending(path: "VoiceFlow/Support/AppIcon.icns")
let p = Process()
p.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
p.arguments = ["-c", "icns", iconset.path, "-o", output.path]
try p.run()
p.waitUntilExit()
print(p.terminationStatus == 0 ? "Wrote \(output.path)\nWrote \(ico.path)\nPreview: \(preview.path)" : "iconutil failed")
