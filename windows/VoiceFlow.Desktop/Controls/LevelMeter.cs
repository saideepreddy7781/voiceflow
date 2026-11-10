using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;

namespace VoiceFlow.Desktop.Controls;

/// <summary>Segmented horizontal input meter. Level is eased so the bars move smoothly at low update rates.</summary>
public sealed class LevelMeter : Control
{
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<LevelMeter, double>(nameof(Level));
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<LevelMeter, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<LevelMeter, IBrush?>(nameof(Track));
    public static readonly StyledProperty<int> SegmentsProperty =
        AvaloniaProperty.Register<LevelMeter, int>(nameof(Segments), 28);

    static LevelMeter() => AffectsRender<LevelMeter>(LevelProperty, FillProperty, TrackProperty, SegmentsProperty);

    public LevelMeter()
    {
        Transitions = [new DoubleTransition { Property = LevelProperty, Duration = TimeSpan.FromMilliseconds(70) }];
        Height = 10;
    }

    public double Level { get => GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public int Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }

    public override void Render(DrawingContext context)
    {
        var n = Segments;
        var gap = 3.0;
        var w = (Bounds.Width - gap * (n - 1)) / n;
        if (w <= 0) return;
        var h = Bounds.Height;
        var lit = (int)Math.Round(Math.Clamp(Level, 0, 1) * n);
        for (int i = 0; i < n; i++)
        {
            var brush = i < lit ? Fill : Track;
            if (brush != null) context.DrawRectangle(brush, null, new Rect(i * (w + gap), 0, w, h), 1.5, 1.5);
        }
    }
}
