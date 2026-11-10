using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace VoiceFlow.Desktop.Views;

/// <summary>The borderless always-on-top bar. It sits at the bottom centre of the active monitor and never takes focus.</summary>
public partial class FlowBarWindow : Window
{
    public FlowBarWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Where the window goes: horizontally centred, just above the bottom of the work area.</summary>
    public static PixelPoint BottomCentre(PixelRect workArea, PixelSize size, double scale)
    {
        var margin = (int)Math.Round(6 * scale);
        return new PixelPoint(workArea.X + (workArea.Width - size.Width) / 2, workArea.Y + workArea.Height - size.Height - margin);
    }

    /// <summary>
    /// True when the screen point is over a button or a toast. Everywhere else clicks fall through to the
    /// app underneath, so the bar never gets in the way.
    /// </summary>
    public bool IsInteractiveAt(PixelPoint screenPoint)
    {
        var scale = RenderScaling;
        var local = new Point((screenPoint.X - Position.X) / scale, (screenPoint.Y - Position.Y) / scale);
        foreach (var control in this.GetVisualDescendants().OfType<Control>())
        {
            if (control is not Button && !control.Classes.Contains("barHit")) continue;
            if (!control.IsEffectivelyVisible) continue;
            if (control.TranslatePoint(default, this) is not { } origin) continue;
            if (new Rect(origin, control.Bounds.Size).Contains(local)) return true;
        }
        return false;
    }
}
