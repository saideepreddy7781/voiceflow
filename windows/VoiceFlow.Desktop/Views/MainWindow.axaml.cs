using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace VoiceFlow.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        // Windows 11 draws Mica behind the sidebar. Other systems keep the opaque brush from the theme.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.None];
            Opened += (_, _) => ApplyMica();
            ActualThemeVariantChanged += (_, _) => ApplyMica();
        }
    }

    /// <summary>Sidebar collapses to an icon rail below this width, which covers a 1080p screen at 200%.</summary>
    const double CompactBelow = 1040;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty) Classes.Set("compact", ClientSize.Width < CompactBelow);
    }

    void ApplyMica()
    {
        if (ActualTransparencyLevel != WindowTransparencyLevel.Mica) return;
        // Mica shows through the sidebar and only there; the page keeps its solid surface.
        if (this.FindControl<Border>("Sidebar") is { } sidebar) sidebar.Background = Brushes.Transparent;
    }
}
