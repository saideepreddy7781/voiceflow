using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VoiceFlow.Desktop.Controls;

/// <summary>Centre-aligned capsule bars for the Flow bar and the microphone test.</summary>
public sealed class WaveBars : Control
{
    public static readonly StyledProperty<IReadOnlyList<float>?> LevelsProperty =
        AvaloniaProperty.Register<WaveBars, IReadOnlyList<float>?>(nameof(Levels));
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<WaveBars, IBrush?>(nameof(Fill), Brushes.White);
    public static readonly StyledProperty<int> BarCountProperty =
        AvaloniaProperty.Register<WaveBars, int>(nameof(BarCount), 11);
    public static readonly StyledProperty<double> BarWidthProperty =
        AvaloniaProperty.Register<WaveBars, double>(nameof(BarWidth), 2.5);
    public static readonly StyledProperty<double> BarGapProperty =
        AvaloniaProperty.Register<WaveBars, double>(nameof(BarGap), 2.5);
    public static readonly StyledProperty<bool> FrozenProperty =
        AvaloniaProperty.Register<WaveBars, bool>(nameof(Frozen));

    static WaveBars() => AffectsRender<WaveBars>(LevelsProperty, FillProperty, FrozenProperty, BarCountProperty);

    public IReadOnlyList<float>? Levels { get => GetValue(LevelsProperty); set => SetValue(LevelsProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public int BarCount { get => GetValue(BarCountProperty); set => SetValue(BarCountProperty, value); }
    public double BarWidth { get => GetValue(BarWidthProperty); set => SetValue(BarWidthProperty, value); }
    public double BarGap { get => GetValue(BarGapProperty); set => SetValue(BarGapProperty, value); }
    public bool Frozen { get => GetValue(FrozenProperty); set => SetValue(FrozenProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        var n = BarCount;
        var width = n * BarWidth + (n - 1) * BarGap;
        var height = double.IsInfinity(availableSize.Height) ? 22 : availableSize.Height;
        return new Size(width, height);
    }

    public override void Render(DrawingContext context)
    {
        var levels = Levels;
        var n = BarCount;
        var w = BarWidth;
        var h = Bounds.Height;
        var r = w / 2;
        var fill = Fill ?? Brushes.White;
        using (context.PushOpacity(Frozen ? 0.45 : 0.95))
        {
            var x = (Bounds.Width - (n * w + (n - 1) * BarGap)) / 2;
            for (int i = 0; i < n; i++)
            {
                var v = levels is { Count: > 0 } ? levels[Math.Min(i * levels.Count / n, levels.Count - 1)] : 0f;
                var height = Math.Max(3, v * h);
                context.DrawRectangle(fill, null, new Rect(x, (h - height) / 2, w, height), r, r);
                x += w + BarGap;
            }
        }
    }
}
