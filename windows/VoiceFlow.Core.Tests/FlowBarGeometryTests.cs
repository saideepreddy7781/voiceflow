using VoiceFlow.Core.FlowBar;
using Xunit;

namespace VoiceFlow.Core.Tests;

public class FlowBarShapeTests
{
    // A 120 x 32 capsule at (10, 50): fully rounded ends.
    static readonly RoundedBox Capsule = new(10, 50, 120, 32, 16);

    [Fact]
    public void CapsuleContainsItsMiddleAndItsRoundedEnds()
    {
        Assert.True(Capsule.Contains(70, 66));
        Assert.True(Capsule.Contains(11, 66));          // the tip of the left end
        Assert.True(Capsule.Contains(129.5, 66));       // the tip of the right end
        Assert.True(Capsule.Contains(26, 50.5));        // top edge where the arc meets it
    }

    [Fact]
    public void CornersOfTheBoundingBoxAreOutsideTheCapsule()
    {
        Assert.False(Capsule.Contains(10.5, 50.5));
        Assert.False(Capsule.Contains(129.5, 81.5));
        Assert.False(Capsule.Contains(14, 54));          // inside the box, outside the arc
        Assert.False(Capsule.Contains(9.9, 66));
        Assert.False(Capsule.Contains(70, 82));          // the bottom edge is exclusive
    }

    [Fact]
    public void RadiusLargerThanHalfTheHeightIsAStadium()
    {
        var box = new RoundedBox(0, 0, 100, 20, 50);
        Assert.True(box.Contains(50, 10));
        Assert.True(box.Contains(1, 10));
        Assert.False(box.Contains(1, 1));
        Assert.False(new RoundedBox(0, 0, 0, 20, 4).Contains(0, 5));
    }

    [Fact]
    public void SquareCornersCoverTheWholeBox()
    {
        var box = new RoundedBox(0, 0, 10, 10, 0);
        Assert.True(box.Contains(0, 0));
        Assert.True(box.Contains(9.9, 9.9));
        Assert.False(box.Contains(10, 5));
    }

    [Fact]
    public void HitTestIsTheUnionOfTheVisibleParts()
    {
        var toast = new RoundedBox(0, 0, 200, 40, 12);
        var pill = new RoundedBox(40, 46, 120, 32, 16);
        RoundedBox[] parts = [toast, pill];
        Assert.True(BarShape.HitTest(parts, 100, 20));    // toast
        Assert.True(BarShape.HitTest(parts, 100, 60));    // pill
        Assert.False(BarShape.HitTest(parts, 100, 43));   // the gap between them
        Assert.False(BarShape.HitTest(parts, 10, 60));    // beside the pill, below the toast
        Assert.False(BarShape.HitTest(parts, 0, 0));      // the toast's rounded corner
        Assert.False(BarShape.HitTest([], 0, 0));
    }

    [Fact]
    public void PixelsAreTestedAtTheirCentre()
    {
        RoundedBox[] parts = [new RoundedBox(0, 0, 10, 10, 0)];
        Assert.True(BarShape.HitTest(parts, 9, 9));
        Assert.False(BarShape.HitTest(parts, 10, 9));
        Assert.False(BarShape.HitTest(parts, -1, 0));
    }

    [Fact]
    public void ScalingAndInflatingKeepTheShape()
    {
        var scaled = Capsule.Scaled(1.5);
        Assert.Equal(new RoundedBox(15, 75, 180, 48, 24), scaled);
        var grown = Capsule.Inflated(1);
        Assert.Equal(new RoundedBox(9, 49, 122, 34, 17), grown);
        Assert.True(grown.Contains(9.5, 66));
        Assert.Equal(0, new RoundedBox(0, 0, 4, 4, 0).Inflated(-1).Radius);
    }
}

public class FlowBarPlacementTests
{
    // 1920 x 1080 at 100% with a 48-pixel taskbar, and a 2560 x 1440 display at 150% to its right, 200 pixels higher.
    static readonly BarMonitor Laptop = new(new PxRect(0, 0, 1920, 1080), new PxRect(0, 0, 1920, 1032), 1.0);
    static readonly BarMonitor External = new(new PxRect(1920, -200, 2560, 1440), new PxRect(1920, -200, 2560, 1380), 1.5);
    static readonly BarMonitor[] Both = [Laptop, External];
    static readonly PxSize Pill = new(120, 32);

    [Fact]
    public void HomeIsBottomCentreOfTheWorkArea()
    {
        Assert.Equal(new PxPoint(960, 1032 - 14 - 16), BarPlacement.Home(Laptop.WorkArea, Pill, 1.0));
        var big = BarPlacement.ToPixels(120, 32, 1.5);
        Assert.Equal(new PxSize(180, 48), big);
        Assert.Equal(new PxPoint(1920 + 1280, -200 + 1380 - 21 - 24), BarPlacement.Home(External.WorkArea, big, 1.5));
    }

    [Fact]
    public void ClampKeepsTheWholeCapsuleInsideTheWorkArea()
    {
        Assert.Equal(new PxPoint(4 + 60, 4 + 16), BarPlacement.Clamp(new PxPoint(-500, -500), Pill, Laptop.WorkArea, 1.0));
        Assert.Equal(new PxPoint(1920 - 4 - 60, 1032 - 4 - 16), BarPlacement.Clamp(new PxPoint(5000, 5000), Pill, Laptop.WorkArea, 1.0));
        Assert.Equal(new PxPoint(700, 400), BarPlacement.Clamp(new PxPoint(700, 400), Pill, Laptop.WorkArea, 1.0));
        // Wider than the work area: centred on it.
        var tiny = new PxRect(0, 0, 100, 600);
        Assert.Equal(50, BarPlacement.Clamp(new PxPoint(900, 300), Pill, tiny, 1.0).X);
    }

    [Fact]
    public void DroppedNearTheBottomCentreItGoesHome()
    {
        var (centre, home) = BarPlacement.Snap(new PxPoint(972, 1000), Pill, Laptop.WorkArea, 1.0);
        Assert.True(home);
        Assert.Equal(BarPlacement.Home(Laptop.WorkArea, Pill, 1.0), centre);
    }

    [Fact]
    public void DroppedNearAnEdgeItDocks()
    {
        var (left, home) = BarPlacement.Snap(new PxPoint(75, 500), Pill, Laptop.WorkArea, 1.0);
        Assert.False(home);
        Assert.Equal(new PxPoint(14 + 60, 500), left);
        var (topRight, _) = BarPlacement.Snap(new PxPoint(1840, 30), Pill, Laptop.WorkArea, 1.0);
        Assert.Equal(new PxPoint(1920 - 14 - 60, 14 + 16), topRight);
        var (bottom, notHome) = BarPlacement.Snap(new PxPoint(400, 1000), Pill, Laptop.WorkArea, 1.0);
        Assert.False(notHome);
        Assert.Equal(new PxPoint(400, 1032 - 14 - 16), bottom);
    }

    [Fact]
    public void DroppedInTheOpenItStaysPut()
    {
        var (centre, home) = BarPlacement.Snap(new PxPoint(700, 400), Pill, Laptop.WorkArea, 1.0);
        Assert.False(home);
        Assert.Equal(new PxPoint(700, 400), centre);
        var (centred, _) = BarPlacement.Snap(new PxPoint(970, 400), Pill, Laptop.WorkArea, 1.0);
        Assert.Equal(new PxPoint(960, 400), centred);
    }

    [Fact]
    public void SnapDistancesScaleWithTheDisplay()
    {
        var item = BarPlacement.ToPixels(120, 32, 1.5);
        // 25 physical pixels from the left edge is under 20 DIP at 150%, so it docks with a 21-pixel gap.
        var (docked, _) = BarPlacement.Snap(new PxPoint(External.WorkArea.X + 25 + 90, 300), item, External.WorkArea, 1.5);
        Assert.Equal(External.WorkArea.X + 21 + 90, docked.X);
    }

    [Fact]
    public void LayoutKeyIgnoresOrderAndScale()
    {
        var key = BarPlacement.LayoutKey(Both);
        Assert.Equal("0,0,1920,1080;1920,-200,2560,1440", key);
        Assert.Equal(key, BarPlacement.LayoutKey([External, Laptop]));
        Assert.Equal(key, BarPlacement.LayoutKey([Laptop with { Scale = 1.25 }, External with { WorkArea = External.Bounds }]));
        Assert.NotEqual(key, BarPlacement.LayoutKey([Laptop]));
    }

    [Fact]
    public void MonitorForFindsTheDisplayUnderAPoint()
    {
        Assert.Equal(External, BarPlacement.MonitorFor(new PxPoint(2500, 0), Both));
        Assert.Equal(Laptop, BarPlacement.MonitorFor(new PxPoint(500, 1060), Both));   // on the taskbar
        Assert.Equal(Laptop, BarPlacement.MonitorFor(new PxPoint(-300, 500), Both));   // left of everything
        Assert.Equal(External, BarPlacement.MonitorFor(new PxPoint(3000, 2000), Both));
    }

    [Fact]
    public void WithNothingSavedItSitsHomeOnTheActiveDisplay()
    {
        var (centre, monitor, saved) = BarPlacement.Resolve([], Both, External, 120, 32);
        Assert.False(saved);
        Assert.Equal(External, monitor);
        Assert.Equal(BarPlacement.Home(External.WorkArea, new PxSize(180, 48), 1.5), centre);
    }

    [Fact]
    public void ASavedPositionIsUsedOnlyForItsOwnLayout()
    {
        var key = BarPlacement.LayoutKey(Both);
        List<BarPosition> positions = [new(key, 2500, 100)];
        var (centre, monitor, saved) = BarPlacement.Resolve(positions, Both, Laptop, 120, 32);
        Assert.True(saved);
        Assert.Equal(External, monitor);
        Assert.Equal(new PxPoint(2500, 100), centre);

        // Unplug the external display: the laptop alone has its own (empty) memory.
        var (alone, _, savedAlone) = BarPlacement.Resolve(positions, [Laptop], Laptop, 120, 32);
        Assert.False(savedAlone);
        Assert.Equal(BarPlacement.Home(Laptop.WorkArea, Pill, 1.0), alone);
    }

    [Fact]
    public void ASavedPositionIsClampedWhenTheTaskbarOrScaleChanges()
    {
        var key = BarPlacement.LayoutKey([Laptop]);
        List<BarPosition> positions = [new(key, 960, 1060)];   // was on the bottom edge before the taskbar appeared
        var (centre, _, saved) = BarPlacement.Resolve(positions, [Laptop], Laptop, 120, 32);
        Assert.True(saved);
        Assert.Equal(1032 - 4 - 16, centre.Y);
        // Same display at 200%: the capsule is twice as tall, and still fully on screen.
        var (scaled, _, _) = BarPlacement.Resolve(positions, [Laptop with { Scale = 2 }], Laptop, 120, 32);
        Assert.Equal(1032 - 8 - 32, scaled.Y);
    }

    [Fact]
    public void RememberKeepsOneEntryPerLayoutNewestLast()
    {
        var list = BarPlacement.Remember([], "a", new PxPoint(1, 1));
        list = BarPlacement.Remember(list, "b", new PxPoint(2, 2));
        list = BarPlacement.Remember(list, "a", new PxPoint(3, 3));
        Assert.Equal([new BarPosition("b", 2, 2), new BarPosition("a", 3, 3)], list);
        for (int i = 0; i < 20; i++) list = BarPlacement.Remember(list, $"layout{i}", new PxPoint(i, i));
        Assert.Equal(BarPlacement.MaxLayouts, list.Count);
        Assert.Equal("layout19", list[^1].Layout);
        Assert.DoesNotContain(list, p => p.Layout == "a");
        Assert.Equal(BarPlacement.MaxLayouts - 1, BarPlacement.Forget(list, "layout19").Count);
        Assert.Equal(BarPlacement.MaxLayouts, BarPlacement.Forget(list, "nothing").Count);
    }

    [Fact]
    public void ToastsOpenAboveUnlessThereIsNoRoom()
    {
        var toast = new PxSize(300, 50);
        var (below, offset) = BarPlacement.Extras(new PxPoint(960, 1002), Pill, toast, 6, Laptop.WorkArea, 1.0);
        Assert.False(below);
        Assert.Equal(0, offset);
        var (nearTop, _) = BarPlacement.Extras(new PxPoint(960, 30), Pill, toast, 6, Laptop.WorkArea, 1.0);
        Assert.True(nearTop);
    }

    [Fact]
    public void ToastsShiftSidewaysToStayOnScreen()
    {
        var toast = new PxSize(400, 50);
        // Capsule docked at the left edge (x = 14): centred on it, the toast would start at x = -126.
        var (_, offset) = BarPlacement.Extras(new PxPoint(74, 500), Pill, toast, 6, Laptop.WorkArea, 1.0);
        Assert.Equal(14 + 200 - 74, offset);   // its left edge lines up with the docked capsule's
        var (_, right) = BarPlacement.Extras(new PxPoint(1846, 500), Pill, toast, 6, Laptop.WorkArea, 1.0);
        Assert.Equal(1920 - 14 - 200 - 1846, right);
    }

    [Fact]
    public void LayoutPutsTheToastAboveTheCapsuleCentred()
    {
        var (width, height, capsule, extras) = BarLayout.Arrange(120, 32, 300, 50, 0, below: false, spacing: 6);
        Assert.Equal(300, width);
        Assert.Equal(88, height);
        Assert.Equal(new LayoutBox(90, 56, 120, 32), capsule);
        Assert.Equal(new LayoutBox(0, 0, 300, 50), extras);
        Assert.Equal(extras.CentreX, capsule.CentreX);
    }

    [Fact]
    public void LayoutCanPutTheToastBelowAndShiftIt()
    {
        var (width, height, capsule, extras) = BarLayout.Arrange(120, 32, 300, 50, 130, below: true, spacing: 6);
        Assert.Equal(60 + 130 + 150, width);   // from the capsule's left end to the toast's right end
        Assert.Equal(88, height);
        Assert.Equal(new LayoutBox(0, 0, 120, 32), capsule);
        Assert.Equal(new LayoutBox(40, 38, 300, 50), extras);
        Assert.Equal(capsule.CentreX + 130, extras.CentreX);
    }

    [Fact]
    public void LayoutWithOnePartIsJustThatPart()
    {
        var (w1, h1, capsule, _) = BarLayout.Arrange(120, 32, 0, 0, 50, below: false, spacing: 6);
        Assert.Equal((120.0, 32.0), (w1, h1));
        Assert.Equal(new LayoutBox(0, 0, 120, 32), capsule);
        var (w2, h2, _, toast) = BarLayout.Arrange(0, 0, 300, 50, 80, below: true, spacing: 6);
        Assert.Equal((300.0, 50.0), (w2, h2));
        Assert.Equal(new LayoutBox(0, 0, 300, 50), toast);   // the offset only applies next to a capsule
        Assert.Equal((0.0, 0.0), (BarLayout.Arrange(0, 0, 0, 0, 0, false, 6).Width, BarLayout.Arrange(0, 0, 0, 0, 0, false, 6).Height));
    }
}

public class FlowBarPreferencesTests
{
    [Fact]
    public void DefaultsAreBottomCentreAndUnlocked()
    {
        var p = new Preferences();
        Assert.Empty(p.FlowBarPositions);
        Assert.False(p.FlowBarLocked);
    }

    [Fact]
    public void PositionsAndLockRoundTrip()
    {
        using var dir = new TempFolder();
        var store = new PreferenceStore(dir.Path);
        var p = new Preferences { FlowBarLocked = true, FlowBarPositions = [new("0,0,1920,1080", 300, 900), new("0,0,1920,1080;1920,-200,2560,1440", -40, 1200)] };
        store.Save(p);
        var back = store.Load();
        Assert.Equal(p, back);
        Assert.Equal(p.FlowBarPositions, back.FlowBarPositions);
        Assert.True(back.FlowBarLocked);
        var clone = p.Clone();
        clone.FlowBarPositions.Clear();
        Assert.Equal(2, p.FlowBarPositions.Count);   // a clone shares no list with the original
    }

    [Fact]
    public void SettingsFromBeforeThisVersionKeepEverythingElse()
    {
        var p = Preferences.FromJson("""{"hotkey":"rightAlt","showBarWhenIdle":true,"languages":["te","en"]}""");
        Assert.Equal(Hotkey.RightAlt, p.Hotkey);
        Assert.True(p.ShowBarWhenIdle);
        Assert.Empty(p.FlowBarPositions);
        Assert.False(p.FlowBarLocked);
    }

    [Fact]
    public void DamagedPositionsAreSkipped()
    {
        var p = Preferences.FromJson("""
            {"flowBarLocked":"yes","flowBarPositions":[
              {"layout":"a","x":1,"y":2}, {"layout":"","x":1,"y":2}, {"layout":"b","x":"far","y":2}, 7, null,
              {"layout":"c","x":5}, {"layout":"a","x":9,"y":9}, {"layout":"d","x":-3,"y":4}]}
            """);
        Assert.False(p.FlowBarLocked);
        Assert.Equal([new BarPosition("a", 9, 9), new BarPosition("d", -3, 4)], p.FlowBarPositions);
        Assert.Empty(Preferences.FromJson("""{"flowBarPositions":{"a":1}}""").FlowBarPositions);
    }

    [Fact]
    public void AtMostEightLayoutsAreKept()
    {
        var items = string.Join(",", Enumerable.Range(0, 12).Select(i => $$"""{"layout":"l{{i}}","x":{{i}},"y":0}"""));
        var p = Preferences.FromJson($$"""{"flowBarPositions":[{{items}}]}""");
        Assert.Equal(BarPlacement.MaxLayouts, p.FlowBarPositions.Count);
        Assert.Equal("l11", p.FlowBarPositions[^1].Layout);
    }
}
