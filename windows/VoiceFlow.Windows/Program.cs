using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using VoiceFlow.Desktop;
using VoiceFlow.Windows.Native;
using VoiceFlow.Windows.Shell;

namespace VoiceFlow.Windows;

internal static class Program
{
    // Session-local names: one VoiceFlow per signed-in user session.
    const string MutexName = @"Local\VoiceFlow.SingleInstance.5f3c2a7e";
    const string ShowEventName = @"Local\VoiceFlow.Show.5f3c2a7e";

    /// <summary>Nirmala UI has Telugu and Devanagari, which Segoe UI does not.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseWin32()
            .UseSkia()
            .UseHarfBuzz()
            .With(new FontManagerOptions { FontFallbacks = [new FontFallback { FontFamily = new FontFamily("Nirmala UI") }] })
            .LogToTrace();

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase)) return SelfTest.Run(args);

        Logger.Initialize(AppPaths.LogFolder);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // A background thread crashed; .NET will end the process. Log it and un-mute the speakers first.
            Logger.Error("Unhandled exception (process ending)", e.ExceptionObject as Exception);
            WindowsApp.Current?.EmergencyCleanup();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        Mutex mutex;
        EventWaitHandle showEvent;
        try
        {
            mutex = new Mutex(initiallyOwned: false, MutexName);
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        }
        catch (UnauthorizedAccessException e)
        {
            // A copy running as administrator created these objects; this unelevated copy may not open them.
            Logger.Error("Another VoiceFlow is running as administrator", e);
            NativeMethods.MessageBoxW(IntPtr.Zero, "VoiceFlow is already running as administrator. Quit that copy first.", "VoiceFlow", NativeMethods.MB_ICONINFORMATION);
            return 0;
        }
        using var mutexScope = mutex;
        using var showEventScope = showEvent;
        bool owner;
        try { owner = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owner = true; }   // the previous copy crashed
        if (!owner)
        {
            // Another copy is running: ask it to show its window, then leave.
            showEvent.Set();
            return 0;
        }

        RegisteredWaitHandle? registration = null;
        try
        {
            var background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
            App.Startup = desktop =>
            {
                Dispatcher.UIThread.UnhandledException += (_, e) =>
                {
                    e.Handled = true;
                    OnUiException(e.Exception);
                };
                var app = new WindowsApp(desktop, background);
                registration = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => Dispatcher.UIThread.Post(app.OnSecondInstance), null, Timeout.Infinite, executeOnlyOnce: false);
            };
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception e)
        {
            Logger.Error("VoiceFlow could not start", e);
            NativeMethods.MessageBoxW(IntPtr.Zero, $"VoiceFlow could not start.\n\n{e.Message}\n\nDetails are in {Logger.FilePath}", "VoiceFlow", NativeMethods.MB_ICONERROR);
            return 1;
        }
        finally
        {
            registration?.Unregister(null);
            if (owner) mutex.ReleaseMutex();
        }
    }

    static int _reporting;
    static long _lastReported;

    /// <summary>An exception on the UI thread: log it and tell the user once, without crashing the app.</summary>
    static void OnUiException(Exception e)
    {
        Logger.Error("Unhandled UI exception", e);
        if (Interlocked.Exchange(ref _reporting, 1) == 1) return;
        try
        {
            WindowsApp.Current?.EmergencyCleanup();
            // A timer that keeps failing must not put up a message box every time it ticks.
            var now = Environment.TickCount64;
            if (_lastReported != 0 && now - _lastReported < 60_000) return;
            _lastReported = now;
            NativeMethods.MessageBoxW(IntPtr.Zero, $"Something went wrong, but VoiceFlow is still running.\n\n{e.Message}\n\nDetails are in {Logger.FilePath}", "VoiceFlow", NativeMethods.MB_ICONWARNING);
        }
        finally
        {
            Interlocked.Exchange(ref _reporting, 0);
        }
    }
}
