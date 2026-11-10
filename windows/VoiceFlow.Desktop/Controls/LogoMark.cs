using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace VoiceFlow.Desktop.Controls;

/// <summary>The VoiceFlow mark: white wave bars and a text cursor on a black tile. Geometry matches Logo.swift.</summary>
public sealed class LogoMark : Control
{
    static readonly double[] Heights = [0.34, 0.62, 1.0, 0.70, 0.42];

    public LogoMark()
    {
        AutomationProperties.SetName(this, "VoiceFlow");
        Focusable = false;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        double.IsNaN(Width) ? new Size(28, 28) : new Size(Width, Height);

    public override void Render(DrawingContext context)
    {
        var s = Bounds.Width;
        var tile = new Rect(0, 0, s, s);
        var radius = s * 0.225;
        context.DrawRectangle(Brushes.Black, new Pen(new SolidColorBrush(Colors.White, 0.16), Math.Max(0.5, s / 160)), tile, radius, radius);

        var w = s * 560 / 824;
        var h = s * 400 / 824;
        var left = (s - w) / 2;
        var top = (s - h) / 2;
        var barWidth = w * 0.105;
        var gap = w * 0.062;
        var x = left;
        foreach (var f in Heights)
        {
            var barHeight = h * f;
            var y = top + (h - barHeight) / 2;
            var r = barWidth / 2;
            context.DrawRectangle(Brushes.White, null, new Rect(x, y, barWidth, barHeight), r, r);
            x += barWidth + gap;
        }
        var cursorX = x + gap * 0.6;
        var stem = w * 0.078;
        var serif = w * 0.16;
        var stemRadius = stem / 2;
        context.DrawRectangle(Brushes.White, null, new Rect(cursorX + (serif - stem) / 2, top, stem, h), stemRadius, stemRadius);
        context.DrawRectangle(Brushes.White, null, new Rect(cursorX, top, serif, stem), stemRadius, stemRadius);
        context.DrawRectangle(Brushes.White, null, new Rect(cursorX, top + h - stem, serif, stem), stemRadius, stemRadius);
    }
}
