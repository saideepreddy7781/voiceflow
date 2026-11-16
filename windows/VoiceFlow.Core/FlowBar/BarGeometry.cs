using System.Globalization;

namespace VoiceFlow.Core.FlowBar;

/// <summary>A rectangle in physical screen pixels.</summary>
public readonly record struct PxRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public int CentreX => X + Width / 2;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

public readonly record struct PxPoint(int X, int Y);

public readonly record struct PxSize(int Width, int Height);

/// <summary>One display: its full bounds, the part not covered by the taskbar, and its scale (1.5 at 150%).</summary>
public readonly record struct BarMonitor(PxRect Bounds, PxRect WorkArea, double Scale);

/// <summary>A saved bar position: the centre of the capsule in physical pixels, for one arrangement of displays.</summary>
public sealed record BarPosition(string Layout, int X, int Y);

/// <summary>
/// A rounded rectangle: the outline of one visible part of the bar (the capsule, a toast, the menu). Points inside
/// it belong to the bar; every other point of the bar's window belongs to the app underneath.
/// </summary>
public readonly record struct RoundedBox(double X, double Y, double Width, double Height, double Radius)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool Contains(double px, double py)
    {
        if (Width <= 0 || Height <= 0 || px < X || py < Y || px >= Right || py >= Bottom) return false;
        var r = Math.Clamp(Radius, 0, Math.Min(Width, Height) / 2);
        if (r <= 0) return true;
        // Outside the four corner squares the rectangle is solid; inside one, the point must be within the arc.
        var cx = px < X + r ? X + r : px > Right - r ? Right - r : px;
        var cy = py < Y + r ? Y + r : py > Bottom - r ? Bottom - r : py;
        var dx = px - cx;
        var dy = py - cy;
        return dx * dx + dy * dy <= r * r;
    }

    public RoundedBox Scaled(double scale) => new(X * scale, Y * scale, Width * scale, Height * scale, Radius * scale);

    /// <summary>Grows every side by <paramref name="amount"/>, keeping the corners concentric.</summary>
    public RoundedBox Inflated(double amount) =>
        new(X - amount, Y - amount, Width + 2 * amount, Height + 2 * amount, Math.Max(0, Radius + amount));
}

/// <summary>The bar's clickable shape: the union of its visible parts.</summary>
public static class BarShape
{
    /// <summary>True when the pixel at (x, y) is part of the bar. Pixels are tested at their centre.</summary>
    public static bool HitTest(IReadOnlyList<RoundedBox> parts, int x, int y)
    {
        foreach (var part in parts)
            if (part.Contains(x + 0.5, y + 0.5)) return true;
        return false;
    }

    /// <summary>The parts in physical pixels of a window at <paramref name="scale"/>.</summary>
    public static RoundedBox[] ToPixels(IReadOnlyList<RoundedBox> dipParts, double scale) =>
        dipParts.Select(p => p.Scaled(scale)).ToArray();
}

/// <summary>Where the capsule sits, and where its toasts open. Pure geometry, in physical pixels.</summary>
public static class BarPlacement
{
    /// <summary>Gap between a docked capsule and the edge of the work area, in device-independent pixels.</summary>
    public const double EdgeGap = 14;
    /// <summary>The capsule always stays at least this far inside the work area.</summary>
    public const double MinGap = 4;
    /// <summary>Dropped closer than this to an edge, the capsule docks to it.</summary>
    public const double EdgeSnap = 20;
    /// <summary>Dropped this close to the work area's vertical centre line, the capsule centres on it.</summary>
    public const double CentreSnap = 16;
    /// <summary>Layouts remembered at most; the oldest goes first.</summary>
    public const int MaxLayouts = 8;

    static int Px(double dip, double scale) => (int)Math.Round(dip * scale);

    /// <summary>The physical size of something measured in device-independent pixels on a display at <paramref name="scale"/>.</summary>
    public static PxSize ToPixels(double width, double height, double scale) =>
        new((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale));

    /// <summary>Identifies an arrangement of displays. The order of the list does not matter; display scale is left out on purpose.</summary>
    public static string LayoutKey(IEnumerable<PxRect> monitorBounds) =>
        string.Join(";", monitorBounds.OrderBy(b => b.X).ThenBy(b => b.Y)
            .Select(b => string.Create(CultureInfo.InvariantCulture, $"{b.X},{b.Y},{b.Width},{b.Height}")));

    public static string LayoutKey(IEnumerable<BarMonitor> monitors) => LayoutKey(monitors.Select(m => m.Bounds));

    /// <summary>The default spot: horizontally centred, just above the bottom of the work area.</summary>
    public static PxPoint Home(PxRect work, PxSize item, double scale)
    {
        var top = work.Bottom - Px(EdgeGap, scale) - item.Height;
        return new PxPoint(work.CentreX, top + item.Height / 2);
    }

    /// <summary>Moves the centre so the whole item stays inside the work area.</summary>
    public static PxPoint Clamp(PxPoint centre, PxSize item, PxRect work, double scale)
    {
        var gap = Px(MinGap, scale);
        var left = ClampSpan(centre.X - item.Width / 2, item.Width, work.X + gap, work.Right - gap);
        var top = ClampSpan(centre.Y - item.Height / 2, item.Height, work.Y + gap, work.Bottom - gap);
        return new PxPoint(left + item.Width / 2, top + item.Height / 2);
    }

    /// <summary>Keeps [start, start + length) inside [min, max), or centres it when it is longer than the space.</summary>
    static int ClampSpan(int start, int length, int min, int max)
    {
        if (length >= max - min) return min + (max - min - length) / 2;
        return Math.Clamp(start, min, max - length);
    }

    /// <summary>
    /// Where a dragged capsule comes to rest: docked to an edge it is close to, centred when it is near the
    /// centre line, and always inside the work area. <c>Home</c> is true when that is the default spot.
    /// </summary>
    public static (PxPoint Centre, bool Home) Snap(PxPoint centre, PxSize item, PxRect work, double scale)
    {
        var snap = Px(EdgeSnap, scale);
        var gap = Px(EdgeGap, scale);
        var left = centre.X - item.Width / 2;
        var top = centre.Y - item.Height / 2;
        if (left - work.X <= snap) left = work.X + gap;
        else if (work.Right - (left + item.Width) <= snap) left = work.Right - gap - item.Width;
        else if (Math.Abs(centre.X - work.CentreX) <= Px(CentreSnap, scale)) left = work.CentreX - item.Width / 2;
        if (top - work.Y <= snap) top = work.Y + gap;
        else if (work.Bottom - (top + item.Height) <= snap) top = work.Bottom - gap - item.Height;
        var snapped = Clamp(new PxPoint(left + item.Width / 2, top + item.Height / 2), item, work, scale);
        return (snapped, snapped == Home(work, item, scale));
    }

    /// <summary>The display a point belongs to: the one whose work area or bounds contain it, else the nearest.</summary>
    public static BarMonitor MonitorFor(PxPoint point, IReadOnlyList<BarMonitor> monitors)
    {
        if (monitors.Count == 0) throw new ArgumentException("No displays", nameof(monitors));
        foreach (var m in monitors) if (m.WorkArea.Contains(point.X, point.Y)) return m;
        foreach (var m in monitors) if (m.Bounds.Contains(point.X, point.Y)) return m;
        return monitors.MinBy(m => Distance(point, m.Bounds));
    }

    static long Distance(PxPoint p, PxRect r)
    {
        long dx = p.X < r.X ? r.X - p.X : p.X >= r.Right ? p.X - r.Right + 1 : 0;
        long dy = p.Y < r.Y ? r.Y - p.Y : p.Y >= r.Bottom ? p.Y - r.Bottom + 1 : 0;
        return dx * dx + dy * dy;
    }

    /// <summary>
    /// Where the capsule goes now. A position saved for this arrangement of displays wins, clamped to the display
    /// it is on (the taskbar or the scale may have changed). Otherwise it sits at the default spot on
    /// <paramref name="active"/>, the display the user is working on. <paramref name="itemWidth"/> and
    /// <paramref name="itemHeight"/> are device-independent.
    /// </summary>
    public static (PxPoint Centre, BarMonitor Monitor, bool Saved) Resolve(IReadOnlyList<BarPosition> saved, IReadOnlyList<BarMonitor> monitors,
                                                                           BarMonitor active, double itemWidth, double itemHeight)
    {
        var key = LayoutKey(monitors);
        var entry = saved.LastOrDefault(s => s.Layout == key);
        if (entry == null) return (Home(active.WorkArea, ToPixels(itemWidth, itemHeight, active.Scale), active.Scale), active, false);
        var point = new PxPoint(entry.X, entry.Y);
        var monitor = MonitorFor(point, monitors);
        return (Clamp(point, ToPixels(itemWidth, itemHeight, monitor.Scale), monitor.WorkArea, monitor.Scale), monitor, true);
    }

    /// <summary>The list with <paramref name="layout"/> set to <paramref name="centre"/>, newest last, at most <see cref="MaxLayouts"/> long.</summary>
    public static List<BarPosition> Remember(IEnumerable<BarPosition> saved, string layout, PxPoint centre)
    {
        var list = saved.Where(s => s.Layout != layout).ToList();
        list.Add(new BarPosition(layout, centre.X, centre.Y));
        if (list.Count > MaxLayouts) list.RemoveRange(0, list.Count - MaxLayouts);
        return list;
    }

    public static List<BarPosition> Forget(IEnumerable<BarPosition> saved, string layout) =>
        saved.Where(s => s.Layout != layout).ToList();

    /// <summary>
    /// Where a toast or the menu opens relative to the capsule: above it unless there is no room, and shifted
    /// sideways just enough to stay inside the work area. The offset is from the capsule's centre, in pixels.
    /// </summary>
    public static (bool Below, int Offset) Extras(PxPoint capsuleCentre, PxSize capsule, PxSize extras, int spacing, PxRect work, double scale)
    {
        var gap = Px(MinGap, scale);
        var capsuleTop = capsuleCentre.Y - capsule.Height / 2;
        var capsuleBottom = capsuleTop + capsule.Height;
        var roomAbove = capsuleTop - spacing - gap - work.Y;
        var roomBelow = work.Bottom - gap - spacing - capsuleBottom;
        var below = roomAbove < extras.Height && roomBelow > roomAbove;
        var left = ClampSpan(capsuleCentre.X - extras.Width / 2, extras.Width, work.X + gap, work.Right - gap);
        return (below, left + extras.Width / 2 - capsuleCentre.X);
    }
}

/// <summary>A box in the bar's own layout, in device-independent pixels.</summary>
public readonly record struct LayoutBox(double X, double Y, double Width, double Height)
{
    public double CentreX => X + Width / 2;
    public double CentreY => Y + Height / 2;
}

/// <summary>How the bar window lays out the capsule and the toast or menu next to it.</summary>
public static class BarLayout
{
    /// <summary>
    /// The extras are centred on the capsule plus <paramref name="offset"/>, above it or below it. The window is
    /// the bounding box of both. Either part may be empty (zero size).
    /// </summary>
    public static (double Width, double Height, LayoutBox Capsule, LayoutBox Extras) Arrange(
        double capsuleWidth, double capsuleHeight, double extrasWidth, double extrasHeight, double offset, bool below, double spacing)
    {
        var hasCapsule = capsuleWidth > 0 && capsuleHeight > 0;
        var hasExtras = extrasWidth > 0 && extrasHeight > 0;
        if (!hasCapsule) offset = 0;   // a toast on its own takes the capsule's place
        var minX = Math.Min(hasCapsule ? -capsuleWidth / 2 : double.MaxValue, hasExtras ? offset - extrasWidth / 2 : double.MaxValue);
        var maxX = Math.Max(hasCapsule ? capsuleWidth / 2 : double.MinValue, hasExtras ? offset + extrasWidth / 2 : double.MinValue);
        if (!hasCapsule && !hasExtras) return (0, 0, default, default);
        var gap = hasCapsule && hasExtras ? spacing : 0;
        var height = (hasCapsule ? capsuleHeight : 0) + gap + (hasExtras ? extrasHeight : 0);
        double capsuleY, extrasY;
        if (below) { capsuleY = 0; extrasY = (hasCapsule ? capsuleHeight : 0) + gap; }
        else { extrasY = 0; capsuleY = (hasExtras ? extrasHeight : 0) + gap; }
        var capsule = hasCapsule ? new LayoutBox(-capsuleWidth / 2 - minX, capsuleY, capsuleWidth, capsuleHeight) : default;
        var extras = hasExtras ? new LayoutBox(offset - extrasWidth / 2 - minX, extrasY, extrasWidth, extrasHeight) : default;
        return (maxX - minX, height, capsule, extras);
    }
}
