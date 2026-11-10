using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace VoiceFlow.Desktop;

public partial class App : Application
{
    /// <summary>Set by the real app before startup. It builds the windows and tray once the framework is ready.</summary>
    public static Action<IClassicDesktopStyleApplicationLifetime>? Startup { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) Startup?.Invoke(desktop);
        base.OnFrameworkInitializationCompleted();
    }
}
