import CoreGraphics
import Foundation
import Testing
@testable import VoiceFlowCore

struct FlowBarGeometryTests {
    /// A 1512 by 982 laptop display with a 25-point menu bar and a 60-point Dock.
    let laptop = DisplayArea(frame: CGRect(x: 0, y: 0, width: 1512, height: 982),
                             visible: CGRect(x: 0, y: 60, width: 1512, height: 897))
    /// A 1920 by 1080 monitor to the right of it, with no Dock.
    let monitor = DisplayArea(frame: CGRect(x: 1512, y: 0, width: 1920, height: 1080),
                              visible: CGRect(x: 1512, y: 0, width: 1920, height: 1055))
    let pill = CGSize(width: 120, height: 30)

    // MARK: Hit testing

    @Test func capsuleContainsItsMiddleAndEdges() {
        let bar = RoundedBox.capsule(CGRect(x: 100, y: 100, width: 120, height: 30))
        #expect(bar.radius == 15)
        #expect(bar.contains(CGPoint(x: 160, y: 115)))
        #expect(bar.contains(CGPoint(x: 100, y: 115)))     // tip of the left end
        #expect(bar.contains(CGPoint(x: 160, y: 100)))     // bottom edge, straight part
        #expect(bar.contains(CGPoint(x: 115, y: 101)))     // on the curve, just inside
    }

    @Test func transparentCornersAreOutside() {
        let bar = RoundedBox.capsule(CGRect(x: 100, y: 100, width: 120, height: 30))
        // Inside the bounding box, outside the rounded ends.
        #expect(!bar.contains(CGPoint(x: 101.5, y: 101.5)))
        #expect(!bar.contains(CGPoint(x: 218.5, y: 101.5)))
        #expect(!bar.contains(CGPoint(x: 101.5, y: 128.5)))
        #expect(!bar.contains(CGPoint(x: 218.5, y: 128.5)))
        #expect(!bar.contains(CGPoint(x: 104, y: 104)))
    }

    @Test func pointsJustOutsideAreOutside() {
        let bar = RoundedBox.capsule(CGRect(x: 100, y: 100, width: 120, height: 30))
        #expect(!bar.contains(CGPoint(x: 97, y: 115)))
        #expect(!bar.contains(CGPoint(x: 223, y: 115)))
        #expect(!bar.contains(CGPoint(x: 160, y: 133)))
        #expect(!bar.contains(CGPoint(x: 160, y: 97)))
    }

    @Test func toastCornersFollowItsRadius() {
        let toast = RoundedBox(rect: CGRect(x: 0, y: 0, width: 300, height: 50), radius: 12)
        #expect(toast.contains(CGPoint(x: 150, y: 25)))
        #expect(toast.contains(CGPoint(x: 12, y: 0.5)))
        #expect(!toast.contains(CGPoint(x: 1.5, y: 1.5)))
        #expect(!toast.contains(CGPoint(x: 298.5, y: 48.5)))
        #expect(!toast.contains(CGPoint(x: 3, y: 3)))
        #expect(toast.contains(CGPoint(x: 6, y: 6)))
    }

    @Test func radiusIsCappedAndEmptyBoxesHoldNothing() {
        #expect(RoundedBox(rect: CGRect(x: 0, y: 0, width: 20, height: 10), radius: 40).radius == 5)
        #expect(!RoundedBox(rect: .zero, radius: 0).contains(.zero))
        let square = RoundedBox(rect: CGRect(x: 0, y: 0, width: 10, height: 10), radius: 0)
        #expect(square.contains(CGPoint(x: 0, y: 0)))
        #expect(square.contains(CGPoint(x: 10, y: 10)))
    }

    // MARK: Default spot and clamping

    @Test func defaultSpotIsBottomCentreAboveTheDock() {
        let c = FlowBarGeometry.defaultCentre(size: pill, in: laptop.visible)
        #expect(c == CGPoint(x: 756, y: 60 + 8 + 15))
        let placed = FlowBarGeometry.place(saved: nil, size: pill, displays: [laptop, monitor], fallback: monitor)
        #expect(placed == CGPoint(x: 1512 + 960, y: 0 + 8 + 15))
    }

    @Test func clampKeepsTheBarWhollyOnScreen() {
        let v = laptop.visible
        #expect(FlowBarGeometry.clamp(centre: CGPoint(x: -500, y: 2000), size: pill, in: v) == CGPoint(x: 60, y: v.maxY - 15))
        #expect(FlowBarGeometry.clamp(centre: CGPoint(x: 5000, y: 0), size: pill, in: v) == CGPoint(x: v.maxX - 60, y: 75))
        let inside = CGPoint(x: 400, y: 400)
        #expect(FlowBarGeometry.clamp(centre: inside, size: pill, in: v) == inside)
    }

    @Test func barWiderThanTheAreaIsCentred() {
        let tiny = CGRect(x: 10, y: 10, width: 100, height: 20)
        #expect(FlowBarGeometry.clamp(centre: CGPoint(x: 0, y: 0), size: pill, in: tiny) == CGPoint(x: 60, y: 20))
    }

    @Test func savedSpotIsClampedOntoItsDisplay() {
        // Saved on the monitor, partly off its right edge.
        let placed = FlowBarGeometry.place(saved: CGPoint(x: 3420, y: 500), size: pill, displays: [laptop, monitor], fallback: laptop)
        #expect(placed == CGPoint(x: 3432 - 60, y: 500))
        // Saved below the laptop's Dock line: pulled up into the visible area.
        let low = FlowBarGeometry.place(saved: CGPoint(x: 300, y: 20), size: pill, displays: [laptop], fallback: laptop)
        #expect(low == CGPoint(x: 300, y: 75))
    }

    @Test func spotOnAMissingDisplayMovesToTheNearestOne() {
        // The monitor was unplugged; the spot that was on it lands on the laptop's right edge.
        let placed = FlowBarGeometry.place(saved: CGPoint(x: 2500, y: 500), size: pill, displays: [laptop], fallback: laptop)
        #expect(placed == CGPoint(x: 1512 - 60, y: 500))
        #expect(FlowBarGeometry.display(for: CGPoint(x: 2500, y: 500), among: [laptop]) == laptop)
        #expect(FlowBarGeometry.display(for: CGPoint(x: 2000, y: 500), among: [laptop, monitor]) == monitor)
        #expect(FlowBarGeometry.place(saved: nil, size: pill, displays: [], fallback: nil) == nil)
    }

    // MARK: Snapping and dragging

    @Test func snapsToBottomCentreWhenNear() {
        let v = laptop.visible
        let snapped = FlowBarGeometry.snap(centre: CGPoint(x: v.midX + 11, y: v.minY + 23 + 9), size: pill, in: v)
        #expect(snapped == CGPoint(x: v.midX, y: v.minY + 23))
    }

    @Test func snapsToEachEdgeOnItsOwnAxis() {
        let v = laptop.visible
        let left = FlowBarGeometry.snap(centre: CGPoint(x: 80, y: 500), size: pill, in: v)
        #expect(left == CGPoint(x: 8 + 60, y: 500))
        let right = FlowBarGeometry.snap(centre: CGPoint(x: v.maxX - 75, y: 500), size: pill, in: v)
        #expect(right == CGPoint(x: v.maxX - 68, y: 500))
        let top = FlowBarGeometry.snap(centre: CGPoint(x: 500, y: v.maxY - 30), size: pill, in: v)
        #expect(top == CGPoint(x: 500, y: v.maxY - 23))
        // On the bottom line, anywhere along it.
        let bottom = FlowBarGeometry.snap(centre: CGPoint(x: 500, y: v.minY + 30), size: pill, in: v)
        #expect(bottom == CGPoint(x: 500, y: v.minY + 23))
    }

    @Test func farFromEveryLineDoesNotSnap() {
        let v = laptop.visible
        let free = CGPoint(x: v.midX + 200, y: v.midY)
        #expect(FlowBarGeometry.snap(centre: free, size: pill, in: v) == free)
        // Just past the snap distance.
        let edge = CGPoint(x: v.midX + FlowBarGeometry.snapDistance + 1, y: v.midY)
        #expect(FlowBarGeometry.snap(centre: edge, size: pill, in: v) == edge)
    }

    @Test func dragFollowsThePointerThenSnapsAndClamps() {
        let start = CGPoint(x: 756, y: 83)
        let moved = FlowBarGeometry.dragged(start: start, pointerStart: CGPoint(x: 760, y: 85), pointer: CGPoint(x: 960, y: 385),
                                            size: pill, displays: [laptop, monitor])
        #expect(moved == CGPoint(x: 956, y: 383))
        // Dragged towards the left edge, 5 points short of the snap line.
        let snapped = FlowBarGeometry.dragged(start: start, pointerStart: start, pointer: CGPoint(x: 73, y: 400),
                                              size: pill, displays: [laptop, monitor])
        #expect(snapped == CGPoint(x: 68, y: 400))
        // Flung far below the screen: stays on it.
        let flung = FlowBarGeometry.dragged(start: start, pointerStart: start, pointer: CGPoint(x: 400, y: -900),
                                            size: pill, displays: [laptop])
        #expect(laptop.visible.contains(FlowBarGeometry.rect(centre: flung, size: pill)))
    }

    @Test func dragCrossesOntoTheDisplayUnderThePointer() {
        let start = CGPoint(x: 756, y: 83)
        let moved = FlowBarGeometry.dragged(start: start, pointerStart: start, pointer: CGPoint(x: 2300, y: 600),
                                            size: pill, displays: [laptop, monitor])
        #expect(moved == CGPoint(x: 2300, y: 600))
        #expect(monitor.visible.contains(FlowBarGeometry.rect(centre: moved, size: pill)))
    }

    // MARK: Toast

    @Test func toastOpensCentredAboveTheBar() {
        let bar = FlowBarGeometry.rect(centre: CGPoint(x: 756, y: 83), size: pill)
        let toast = FlowBarGeometry.toastFrame(size: CGSize(width: 300, height: 50), bar: bar, in: laptop.visible)
        #expect(toast == CGRect(x: 606, y: bar.maxY + 6, width: 300, height: 50))
    }

    @Test func toastOpensBelowABarAtTheTop() {
        let v = laptop.visible
        let bar = FlowBarGeometry.rect(centre: CGPoint(x: 756, y: v.maxY - 23), size: pill)
        let toast = FlowBarGeometry.toastFrame(size: CGSize(width: 300, height: 50), bar: bar, in: v)
        #expect(toast.maxY == bar.minY - 6)
        #expect(v.contains(toast))
    }

    @Test func toastStaysOnScreenNextToABarInACorner() {
        let v = laptop.visible
        let bar = FlowBarGeometry.rect(centre: CGPoint(x: v.maxX - 68, y: v.maxY - 23), size: pill)
        let toast = FlowBarGeometry.toastFrame(size: CGSize(width: 420, height: 64), bar: bar, in: v)
        #expect(v.contains(toast))
        #expect(toast.maxX == v.maxX - 8)
        #expect(toast.maxY <= bar.minY)
        let leftBar = FlowBarGeometry.rect(centre: CGPoint(x: 68, y: 83), size: pill)
        let leftToast = FlowBarGeometry.toastFrame(size: CGSize(width: 420, height: 64), bar: leftBar, in: v)
        #expect(leftToast.minX == v.minX + 8)
        #expect(leftToast.minY == leftBar.maxY + 6)
    }

    // MARK: Per-display memory

    @Test func arrangementKeyIgnoresOrderAndNamesEachSetup() {
        let a = FlowBarGeometry.arrangementKey([laptop.frame, monitor.frame])
        let b = FlowBarGeometry.arrangementKey([monitor.frame, laptop.frame])
        #expect(a == b)
        #expect(a != FlowBarGeometry.arrangementKey([laptop.frame]))
        // The same monitor on the other side is a different arrangement.
        let leftMonitor = CGRect(x: -1920, y: 0, width: 1920, height: 1080)
        #expect(a != FlowBarGeometry.arrangementKey([laptop.frame, leftMonitor]))
        #expect(FlowBarGeometry.arrangementKey([laptop.frame]) == "0,0,1512x982")
    }

    @Test func spotsAreRememberedPerArrangement() {
        let alone = FlowBarGeometry.arrangementKey([laptop.frame])
        let docked = FlowBarGeometry.arrangementKey([laptop.frame, monitor.frame])
        var spots = FlowBarSpots()
        spots.set(CGPoint(x: 100, y: 200), for: alone)
        spots.set(CGPoint(x: 2000, y: 900), for: docked)
        #expect(spots.centre(for: alone) == CGPoint(x: 100, y: 200))
        #expect(spots.centre(for: docked) == CGPoint(x: 2000, y: 900))
        #expect(spots.centre(for: "1,1,1x1") == nil)
        spots.set(CGPoint(x: 300, y: 400), for: alone)
        #expect(spots.items.count == 2)
        #expect(spots.centre(for: alone) == CGPoint(x: 300, y: 400))
        spots.remove(alone)
        #expect(spots.centre(for: alone) == nil)
    }

    @Test func oldestArrangementIsForgottenPastTheLimit() {
        var spots = FlowBarSpots()
        for i in 0...FlowBarSpots.limit {
            spots.set(CGPoint(x: i, y: i), for: "setup \(i)")
        }
        #expect(spots.items.count == FlowBarSpots.limit)
        #expect(spots.centre(for: "setup 0") == nil)
        #expect(spots.centre(for: "setup \(FlowBarSpots.limit)") != nil)
        // Using an old setup again makes it the newest.
        spots.set(CGPoint(x: 5, y: 5), for: "setup 1")
        spots.set(CGPoint(x: 9, y: 9), for: "another")
        #expect(spots.centre(for: "setup 1") != nil)
        #expect(spots.centre(for: "setup 2") == nil)
    }

    @Test func spotsRoundTripAsAPlainArrayAndDropBadEntries() throws {
        var spots = FlowBarSpots()
        spots.set(CGPoint(x: 10.5, y: 20.25), for: "0,0,1512x982")
        let data = try JSONEncoder().encode(spots)
        #expect(String(decoding: data, as: UTF8.self).hasPrefix("["))
        #expect(try JSONDecoder().decode(FlowBarSpots.self, from: data) == spots)
        let damaged = #"[{"arrangement":"","x":1,"y":2},{"arrangement":"a","x":3,"y":4}]"#
        let cleaned = try JSONDecoder().decode(FlowBarSpots.self, from: Data(damaged.utf8))
        #expect(cleaned.items == [FlowBarSpot(arrangement: "a", x: 3, y: 4)])
    }
}
