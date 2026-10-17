namespace VoiceFlow.Windows;

internal static class Program
{
    // Session-local names: one VoiceFlow per signed-in user session.
    const string MutexName = @"Local\VoiceFlow.SingleInstance.5f3c2a7e";
    const string ShowEventName = @"Local\VoiceFlow.Show.5f3c2a7e";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase)) return SelfTest.Run(args);

        Logger.Initialize(AppPaths.LogFolder);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => OnUiException(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // A background thread crashed; .NET will end the process. Log it and un-mute the speakers first.
            Logger.Error("Unhandled exception (process ending)", e.ExceptionObject as Exception);
            TrayApp.Current?.EmergencyCleanup();
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
            MessageBox.Show("VoiceFlow is already running as administrator. Quit that copy first.", "VoiceFlow",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
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

        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);   // also declared in app.manifest
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

            using var app = new TrayApp(background: args.Contains("--background", StringComparer.OrdinalIgnoreCase));
            var registration = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => app.OnSecondInstance(), null, Timeout.Infinite, executeOnlyOnce: false);
            Application.ApplicationExit += (_, _) => app.EmergencyCleanup();
            Application.Run(app);
            registration.Unregister(null);
            return 0;
        }
        catch (Exception e)
        {
            Logger.Error("VoiceFlow could not start", e);
            MessageBox.Show($"VoiceFlow could not start.\n\n{e.Message}\n\nDetails are in {Logger.FilePath}", "VoiceFlow",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            if (owner) mutex.ReleaseMutex();
        }
    }

    static int _reporting;

    /// <summary>An exception on the UI thread: log it and tell the user once, without crashing the app.</summary>
    static void OnUiException(Exception e)
    {
        Logger.Error("Unhandled UI exception", e);
        if (Interlocked.Exchange(ref _reporting, 1) == 1) return;
        try
        {
            TrayApp.Current?.EmergencyCleanup();
            MessageBox.Show($"Something went wrong, but VoiceFlow is still running.\n\n{e.Message}\n\nDetails are in {Logger.FilePath}",
                            "VoiceFlow", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            Interlocked.Exchange(ref _reporting, 0);
        }
    }
}
