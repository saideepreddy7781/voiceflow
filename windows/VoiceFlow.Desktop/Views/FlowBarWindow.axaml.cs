using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace VoiceFlow.Desktop.Views;

/// <summary>The borderless always-on-top bar. Sits bottom-centre of the display that holds the mouse.</summary>
public partial class FlowBarWindow : Window
{
    public FlowBarWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Reposition for the current size. Call when the screen layout or phase changes.</summary>
    public void PositionAtBottom(Screen? screen)
    {
        var area = (screen ?? Screens.Primary)?.WorkingArea;
        if (area is not { } a) return;
        var scale = RenderScaling;
        var width = Bounds.Width * scale;
        var height = Bounds.Height * scale;
        Position = new PixelPoint(a.X + (int)((a.Width - width) / 2), a.Y + a.Height - (int)height - (int)(6 * scale));
    }
}
