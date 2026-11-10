using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VoiceFlow.Desktop.Controls;

/// <summary>A horizontal bar showing a share from 0 to 1, with a small minimum so tiny shares stay visible.</summary>
public sealed class ShareBar : Control
{
    public static readonly StyledProperty<double> ShareProperty =
        AvaloniaProperty.Register<ShareBar, double>(nameof(Share));
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<ShareBar, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<ShareBar, IBrush?>(nameof(Track));

    static ShareBar() => AffectsRender<ShareBar>(ShareProperty, FillProperty, TrackProperty);

    public double Share { get => GetValue(ShareProperty); set => SetValue(ShareProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    public override void Render(DrawingContext context)
    {
        var h = Bounds.Height;
        var r = h / 2;
        if (Track != null) context.DrawRectangle(Track, null, new Rect(0, 0, Bounds.Width, h), r, r);
        if (Fill != null) context.DrawRectangle(Fill, null, new Rect(0, 0, Math.Max(h, Bounds.Width * Math.Clamp(Share, 0, 1)), h), r, r);
    }
}
