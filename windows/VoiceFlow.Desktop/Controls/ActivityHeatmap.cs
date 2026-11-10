using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace VoiceFlow.Desktop.Controls;

/// <summary>Sunday-to-Saturday activity grid. It fills the available width, newest week on the right.</summary>
public sealed class ActivityHeatmap : Control
{
    const double Cell = 13, Gap = 3, GutterLeft = 30, GutterTop = 18;

    public static readonly StyledProperty<IReadOnlyDictionary<DateOnly, int>?> DailyWordsProperty =
        AvaloniaProperty.Register<ActivityHeatmap, IReadOnlyDictionary<DateOnly, int>?>(nameof(DailyWords));
    public static readonly StyledProperty<int> WeekOffsetProperty =
        AvaloniaProperty.Register<ActivityHeatmap, int>(nameof(WeekOffset));
    public static readonly StyledProperty<DateOnly> TodayProperty =
        AvaloniaProperty.Register<ActivityHeatmap, DateOnly>(nameof(Today), DateOnly.FromDateTime(DateTime.Today));
    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<ActivityHeatmap, IBrush?>(nameof(LabelBrush));

    static ActivityHeatmap() =>
        AffectsRender<ActivityHeatmap>(DailyWordsProperty, WeekOffsetProperty, TodayProperty, LabelBrushProperty);

    public IReadOnlyDictionary<DateOnly, int>? DailyWords { get => GetValue(DailyWordsProperty); set => SetValue(DailyWordsProperty, value); }
    public int WeekOffset { get => GetValue(WeekOffsetProperty); set => SetValue(WeekOffsetProperty, value); }
    public DateOnly Today { get => GetValue(TodayProperty); set => SetValue(TodayProperty, value); }
    public IBrush? LabelBrush { get => GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    /// <summary>The five word bands: 0, 1-249, 250-499, 500-749, 750 and up.</summary>
    public static int Band(int words) => words switch { <= 0 => 0, < 250 => 1, < 500 => 2, < 750 => 3, _ => 4 };

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = GutterTop + 7 * Cell + 6 * Gap;
        var width = double.IsInfinity(availableSize.Width) ? WeeksToWidth(20) : availableSize.Width;
        return new Size(width, height);
    }

    static double WeeksToWidth(int weeks) => GutterLeft + weeks * Cell + (weeks - 1) * Gap;

    int VisibleWeeks => Math.Clamp((int)((Bounds.Width - GutterLeft + Gap) / (Cell + Gap)), 4, 52);

    DateOnly LastSunday => Today.AddDays(-(int)Today.DayOfWeek).AddDays(-7 * WeekOffset);

    public override void Render(DrawingContext context)
    {
        var weeks = VisibleWeeks;
        var last = LastSunday;
        var daily = DailyWords;
        var label = LabelBrush ?? Brushes.Gray;
        var typeface = new Typeface((FontFamily)(Application.Current!.FindResource("FontText") ?? FontFamily.Default));
        var brushes = new IBrush[5];
        for (int b = 0; b < 5; b++)
            brushes[b] = new SolidColorBrush(this.TryFindResource("Heat" + b, ActualThemeVariant, out var v) && v is Color c ? c : Colors.Gray);

        // Weekday labels on two rows only, so the grid stays quiet.
        foreach (var (name, row) in new[] { ("Mon", 1), ("Wed", 3), ("Fri", 5) })
            DrawText(context, name, typeface, label, new Point(0, GutterTop + row * (Cell + Gap) - 1));

        int? lastMonth = null;
        for (int w = 0; w < weeks; w++)
        {
            var weekStart = last.AddDays(7 * (w - (weeks - 1)));
            var x = GutterLeft + w * (Cell + Gap);
            if (weekStart.Month != lastMonth && weekStart.Day <= 7 && w > 0)
            {
                DrawText(context, weekStart.ToString("MMM", CultureInfo.CurrentCulture), typeface, label, new Point(x, 0));
                lastMonth = weekStart.Month;
            }
            for (int d = 0; d < 7; d++)
            {
                var day = weekStart.AddDays(d);
                if (day > Today) continue;
                var words = daily != null && daily.TryGetValue(day, out var n) ? n : 0;
                context.DrawRectangle(brushes[Band(words)], null, new Rect(x, GutterTop + d * (Cell + Gap), Cell, Cell), 2.5, 2.5);
            }
        }
    }

    static void DrawText(DrawingContext context, string text, Typeface typeface, IBrush brush, Point origin) =>
        context.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, brush), origin);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        var column = (int)((p.X - GutterLeft) / (Cell + Gap));
        var row = (int)((p.Y - GutterTop) / (Cell + Gap));
        string? tip = null;
        if (p.X >= GutterLeft && p.Y >= GutterTop && column >= 0 && column < VisibleWeeks && row is >= 0 and < 7)
        {
            var day = LastSunday.AddDays(7 * (column - (VisibleWeeks - 1)) + row);
            if (day <= Today)
            {
                var words = DailyWords != null && DailyWords.TryGetValue(day, out var n) ? n : 0;
                tip = $"{day.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}: {words:N0} words";
            }
        }
        ToolTip.SetTip(this, tip);
        ToolTip.SetIsOpen(this, tip != null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ToolTip.SetIsOpen(this, false);
    }
}
