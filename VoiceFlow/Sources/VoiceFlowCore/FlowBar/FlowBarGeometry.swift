import CoreGraphics
import Foundation

/// A rounded rectangle in screen points. The Flow bar and its toast are hit-tested against these, so
/// clicks on the transparent corners and margins of their windows reach the app behind.
public struct RoundedBox: Equatable, Sendable {
    public var rect: CGRect
    public var radius: CGFloat

    public init(rect: CGRect, radius: CGFloat) {
        self.rect = rect
        self.radius = max(0, min(radius, rect.width / 2, rect.height / 2))
    }

    /// A pill whose ends are half circles.
    public static func capsule(_ rect: CGRect) -> RoundedBox {
        RoundedBox(rect: rect, radius: min(rect.width, rect.height) / 2)
    }

    /// True when `point` is on or inside the rounded outline.
    public func contains(_ point: CGPoint) -> Bool {
        guard rect.width > 0, rect.height > 0,
              point.x >= rect.minX, point.x <= rect.maxX, point.y >= rect.minY, point.y <= rect.maxY else { return false }
        guard radius > 0 else { return true }
        // Distance from the point to the rectangle shrunk by the radius: inside when it is at most the radius.
        let inner = rect.insetBy(dx: radius, dy: radius)
        let dx = max(inner.minX - point.x, 0, point.x - inner.maxX)
        let dy = max(inner.minY - point.y, 0, point.y - inner.maxY)
        return dx * dx + dy * dy <= radius * radius
    }

    public func offsetBy(dx: CGFloat, dy: CGFloat) -> RoundedBox {
        RoundedBox(rect: rect.offsetBy(dx: dx, dy: dy), radius: radius)
    }
}

/// One display: its whole frame and the part clear of the menu bar and Dock, in global screen points
/// (origin at the bottom left of the main display, as AppKit reports them).
public struct DisplayArea: Equatable, Sendable {
    public var frame: CGRect
    public var visible: CGRect

    public init(frame: CGRect, visible: CGRect) {
        self.frame = frame
        self.visible = visible
    }
}

/// Where the Flow bar sits on screen. Everything here is a pure function of rectangles, so it is unit-tested.
public enum FlowBarGeometry {
    /// Gap between the bar and a screen edge at the default spot and when it snaps to an edge.
    public static let edgeInset: CGFloat = 8
    /// How close to a snap line the bar must be dropped before it settles there.
    public static let snapDistance: CGFloat = 16
    /// Gap between the bar and its toast.
    public static let toastGap: CGFloat = 6

    /// Names the display arrangement, so a spot chosen on one set of displays is remembered for that set.
    public static func arrangementKey(_ frames: [CGRect]) -> String {
        frames
            .map { "\(Int($0.minX.rounded())),\(Int($0.minY.rounded())),\(Int($0.width.rounded()))x\(Int($0.height.rounded()))" }
            .sorted()
            .joined(separator: ";")
    }

    /// Bottom centre of `visible`, just above the Dock.
    public static func defaultCentre(size: CGSize, in visible: CGRect) -> CGPoint {
        CGPoint(x: visible.midX, y: visible.minY + edgeInset + size.height / 2)
    }

    /// The display that contains `point`, or the nearest one.
    public static func display(for point: CGPoint, among displays: [DisplayArea]) -> DisplayArea? {
        if let containing = displays.first(where: { $0.frame.contains(point) }) { return containing }
        return displays.min { distance(point, $0.frame) < distance(point, $1.frame) }
    }

    /// Moves `centre` so a bar of `size` lies wholly inside `visible`. A bar wider than the area is centred on it.
    public static func clamp(centre: CGPoint, size: CGSize, in visible: CGRect) -> CGPoint {
        func axis(_ value: CGFloat, _ half: CGFloat, _ lower: CGFloat, _ upper: CGFloat) -> CGFloat {
            let low = lower + half, high = upper - half
            return low > high ? (lower + upper) / 2 : min(max(value, low), high)
        }
        return CGPoint(x: axis(centre.x, size.width / 2, visible.minX, visible.maxX),
                       y: axis(centre.y, size.height / 2, visible.minY, visible.maxY))
    }

    /// Pulls `centre` onto the bottom centre or an edge when it is within `distance` of one.
    /// Each axis snaps on its own, so the bar can sit on the bottom edge anywhere along it.
    public static func snap(centre: CGPoint, size: CGSize, in visible: CGRect, distance: CGFloat = snapDistance) -> CGPoint {
        let xLines = [visible.midX, visible.minX + edgeInset + size.width / 2, visible.maxX - edgeInset - size.width / 2]
        let yLines = [visible.minY + edgeInset + size.height / 2, visible.maxY - edgeInset - size.height / 2]
        func nearest(_ value: CGFloat, _ lines: [CGFloat]) -> CGFloat {
            guard let line = lines.min(by: { abs($0 - value) < abs($1 - value) }), abs(line - value) <= distance else { return value }
            return line
        }
        return CGPoint(x: nearest(centre.x, xLines), y: nearest(centre.y, yLines))
    }

    /// Where the bar goes: the saved spot if there is one, otherwise the bottom centre of `fallback`.
    /// Either way the result is clamped onto the display it lands on.
    public static func place(saved: CGPoint?, size: CGSize, displays: [DisplayArea], fallback: DisplayArea?) -> CGPoint? {
        if let saved, let display = display(for: saved, among: displays) {
            return clamp(centre: saved, size: size, in: display.visible)
        }
        guard let area = fallback ?? displays.first else { return nil }
        return defaultCentre(size: size, in: area.visible)
    }

    /// The spot for a bar being dragged: follows the pointer, stays on the display under it, and snaps near lines.
    public static func dragged(start: CGPoint, pointerStart: CGPoint, pointer: CGPoint, size: CGSize,
                               displays: [DisplayArea]) -> CGPoint {
        let raw = CGPoint(x: start.x + pointer.x - pointerStart.x, y: start.y + pointer.y - pointerStart.y)
        guard let display = display(for: pointer, among: displays) ?? display(for: raw, among: displays) else { return raw }
        return clamp(centre: snap(centre: raw, size: size, in: display.visible), size: size, in: display.visible)
    }

    /// The toast's frame: centred above the bar, or below it when there is no room above, and kept inside `visible`.
    public static func toastFrame(size: CGSize, bar: CGRect, in visible: CGRect) -> CGRect {
        let above = bar.maxY + toastGap
        let below = bar.minY - toastGap - size.height
        var y = above
        if above + size.height > visible.maxY {
            y = below >= visible.minY ? below : visible.maxY - size.height
        }
        var x = bar.midX - size.width / 2
        x = min(max(x, visible.minX + edgeInset), visible.maxX - edgeInset - size.width)
        if size.width + 2 * edgeInset > visible.width { x = visible.midX - size.width / 2 }
        y = max(y, visible.minY)
        return CGRect(x: x, y: y, width: size.width, height: size.height)
    }

    /// A rectangle of `size` centred on `centre`.
    public static func rect(centre: CGPoint, size: CGSize) -> CGRect {
        CGRect(x: centre.x - size.width / 2, y: centre.y - size.height / 2, width: size.width, height: size.height)
    }

    private static func distance(_ p: CGPoint, _ r: CGRect) -> CGFloat {
        let dx = max(r.minX - p.x, 0, p.x - r.maxX)
        let dy = max(r.minY - p.y, 0, p.y - r.maxY)
        return (dx * dx + dy * dy).squareRoot()
    }
}

/// A spot chosen by dragging the Flow bar, for one display arrangement. `x` and `y` are the bar's centre.
public struct FlowBarSpot: Codable, Equatable, Sendable {
    public var arrangement: String
    public var x: Double
    public var y: Double

    public init(arrangement: String, x: Double, y: Double) {
        self.arrangement = arrangement
        self.x = x
        self.y = y
    }

    public var centre: CGPoint { CGPoint(x: x, y: y) }
}

/// The Flow bar's saved spots, newest last, one per display arrangement. Stored as a plain JSON array.
public struct FlowBarSpots: Codable, Equatable, Sendable {
    /// Arrangements remembered at most; the oldest is forgotten first.
    public static let limit = 8
    public private(set) var items: [FlowBarSpot]

    public init(_ items: [FlowBarSpot] = []) {
        self.items = Array(items.suffix(Self.limit))
    }

    public var isEmpty: Bool { items.isEmpty }

    public func centre(for arrangement: String) -> CGPoint? {
        items.last { $0.arrangement == arrangement }?.centre
    }

    public mutating func set(_ centre: CGPoint, for arrangement: String) {
        items.removeAll { $0.arrangement == arrangement }
        items.append(FlowBarSpot(arrangement: arrangement, x: Double(centre.x), y: Double(centre.y)))
        if items.count > Self.limit { items.removeFirst(items.count - Self.limit) }
    }

    public mutating func remove(_ arrangement: String) {
        items.removeAll { $0.arrangement == arrangement }
    }

    public init(from decoder: Decoder) throws {
        let all = try decoder.singleValueContainer().decode([FlowBarSpot].self)
        // Drop anything that isn't a real point, so a damaged entry can't put the bar off screen.
        self.init(all.filter { $0.x.isFinite && $0.y.isFinite && !$0.arrangement.isEmpty })
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        try c.encode(items)
    }
}
