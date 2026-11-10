using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using VoiceFlow.Desktop.Services;
using VoiceFlow.Desktop.Services.Demo;
using VoiceFlow.Desktop.ViewModels;
using VoiceFlow.Desktop.Views;

namespace VoiceFlow.Desktop;

public partial class App : Application
{
    /// <summary>Set by the real app before startup. Null runs the app on demo data.</summary>
    public static IAppServices? Services { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = Services ?? new DemoServices();
            var shell = new ShellViewModel(services);
            var window = new MainWindow { DataContext = shell };
            desktop.MainWindow = window;
            shell.OnRunSetup = () => ShowOnboarding(services, window);
        }
        base.OnFrameworkInitializationCompleted();
    }

    static void ShowOnboarding(IAppServices services, MainWindow owner)
    {
        var win = new OnboardingWindow();
        var vm = new OnboardingViewModel(services, () => Dispatcher.UIThread.Post(win.Close));
        win.DataContext = vm;
        win.ShowDialog(owner);
    }
}
