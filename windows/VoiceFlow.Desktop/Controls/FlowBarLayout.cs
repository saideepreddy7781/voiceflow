using Avalonia;
using Avalonia.Controls;
using VoiceFlow.Core.FlowBar;

namespace VoiceFlow.Desktop.Controls;

/// <summary>
/// Lays out the Flow bar: the first child is the capsule, the second the toast and menu. The second child opens
/// above the capsule, or below it with <see cref="ExtrasBelow"/>, shifted sideways by <see cref="ExtrasOffset"/>
/// so it stays on screen. The panel is exactly as big as the two together.
/// </summary>
public sealed class FlowBarLayout : Panel
{
    public static readonly StyledProperty<bool> ExtrasBelowProperty =
        AvaloniaProperty.Register<FlowBarLayout, bool>(nameof(ExtrasBelow));
    public static readonly StyledProperty<double> ExtrasOffsetProperty =
        AvaloniaProperty.Register<FlowBarLayout, double>(nameof(ExtrasOffset));
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<FlowBarLayout, double>(nameof(Spacing), 6);

    static FlowBarLayout()
    {
        AffectsMeasure<FlowBarLayout>(ExtrasBelowProperty, ExtrasOffsetProperty, SpacingProperty);
    }

    public bool ExtrasBelow { get => GetValue(ExtrasBelowProperty); set => SetValue(ExtrasBelowProperty, value); }
    /// <summary>Horizontal shift of the toast and menu from the capsule's centre, in device-independent pixels.</summary>
    public double ExtrasOffset { get => GetValue(ExtrasOffsetProperty); set => SetValue(ExtrasOffsetProperty, value); }
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    Control? Capsule => Children.Count > 0 ? Children[0] : null;
    Control? Extras => Children.Count > 1 ? Children[1] : null;

    /// <summary>The capsule's box from the last layout pass, or the toast's when no capsule shows.</summary>
    public Rect AnchorBox { get; private set; }

    public Size CapsuleSize => Size(Capsule);
    public Size ExtrasSize => Size(Extras);

    static Size Size(Control? c) => c is { IsVisible: true } ? c.DesiredSize : default;

    (double Width, double Height, LayoutBox Capsule, LayoutBox Extras) Plan()
    {
        var c = CapsuleSize;
        var e = ExtrasSize;
        return BarLayout.Arrange(c.Width, c.Height, e.Width, e.Height, ExtrasOffset, ExtrasBelow, Spacing);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        foreach (var child in Children) child.Measure(infinite);
        var plan = Plan();
        return new Size(plan.Width, plan.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var plan = Plan();
        // Centre the plan in whatever space the panel was given (the window may be a pixel larger after rounding).
        var dx = Math.Max(0, (finalSize.Width - plan.Width) / 2);
        var dy = Math.Max(0, finalSize.Height - plan.Height);
        static Rect R(LayoutBox b, double dx, double dy) => new(b.X + dx, b.Y + dy, b.Width, b.Height);
        Capsule?.Arrange(R(plan.Capsule, dx, dy));
        Extras?.Arrange(R(plan.Extras, dx, dy));
        AnchorBox = plan.Capsule.Width > 0 ? R(plan.Capsule, dx, dy) : R(plan.Extras, dx, dy);
        return finalSize;
    }
}
