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
            PropertyChanged += (_, e) => { if (e.Property == ActualTransparencyLevelProperty) ApplyMica(); };
        }
    }

    /// <summary>Set by the real app: closing keeps VoiceFlow running in the notification area.</summary>
    public bool HideOnClose { get; set; }

    /// <summary>Sidebar collapses to an icon rail below this width, which covers a 1080p screen at 200%.</summary>
    const double CompactBelow = 1040;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty) Classes.Set("compact", ClientSize.Width < CompactBelow);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!HideOnClose || e.Cancel || e.CloseReason == WindowCloseReason.OSShutdown) return;
        e.Cancel = true;
        Hide();
    }

    void ApplyMica()
    {
        var mica = ActualTransparencyLevel == WindowTransparencyLevel.Mica;
        // Mica shows through the sidebar and only there: the window turns transparent and the page keeps its solid surface.
        if (mica) Background = Brushes.Transparent;
        else if (this.TryFindResource("WindowBg", ActualThemeVariant, out var brush) && brush is IBrush b) Background = b;
        if (this.FindControl<Border>("Sidebar") is not { } sidebar) return;
        if (mica) sidebar.Background = Brushes.Transparent;
        else if (this.TryFindResource("SidebarBg", ActualThemeVariant, out var side) && side is IBrush s) sidebar.Background = s;
    }
}
