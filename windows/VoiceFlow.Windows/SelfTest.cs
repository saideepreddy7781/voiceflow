using System.Runtime.InteropServices;
using System.Text;
using VoiceFlow.Core;
using VoiceFlow.Core.AI;
using VoiceFlow.Core.Audio;
using VoiceFlow.Core.Diagnostics;
using VoiceFlow.Core.Input;
using VoiceFlow.Core.Library;
using VoiceFlow.Core.Speech;
using VoiceFlow.Windows.Audio;
using VoiceFlow.Windows.Dictation;
using VoiceFlow.Windows.Input;
using VoiceFlow.Windows.Meetings;
using VoiceFlow.Windows.Native;
using VoiceFlow.Windows.UI;
using static VoiceFlow.Windows.Native.NativeMethods;

namespace VoiceFlow.Windows;

/// <summary>
/// <c>VoiceFlow.exe --self-test [--self-test-out path]</c>: checks the Windows-specific pieces on a real Windows
/// machine (CI runners) without showing UI or touching the user's data or keys. One line per check:
/// <c>PASS name: detail</c>, <c>SKIP name: reason</c> or <c>FAIL name: reason</c>. Exit code 0 when nothing
/// failed, 1 otherwise.
///
/// Output goes to the file given with --self-test-out (the reliable way for a GUI-subsystem exe) and to standard
/// output when a parent process redirected it or has a console to attach to.
/// </summary>
internal static class SelfTest
{
    static readonly List<string> Lines = new();
    static StreamWriter? _file;
    static TextWriter? _console;
    static int _failures;

    public static int Run(string[] args)
    {
        var outIndex = Array.FindIndex(args, a => a.Equals("--self-test-out", StringComparison.OrdinalIgnoreCase));
        if (outIndex >= 0 && outIndex + 1 < args.Length)
        {
            var path = Path.GetFullPath(args[outIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _file = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        }
        _console = OpenConsole();
        // Async helpers below are pumped by hand; a UI synchronization context would deadlock them.
        WindowsFormsSynchronizationContext.AutoInstall = false;
        SynchronizationContext.SetSynchronizationContext(null);

        Emit($"VoiceFlow {Application.ProductVersion} self-test on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})");
        var temp = Path.Combine(Path.GetTempPath(), "VoiceFlow-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Check("struct-layout", StructLayout);
            Check("prompt", Prompt);
            Check("data-folder", () => DataFolder(temp));
            Check("credential-manager", CredentialManager);
            Check("multipart", Multipart);
            Check("sounds", SoundsCheck);
            using var owner = new HiddenOwner();
            Check("clipboard", () => ClipboardRoundTrip(owner.Handle));
            Check("keyboard-hook", KeyboardHookCheck);
            Check("hotkey-thread", HotkeyThreadCheck);
            Check("ui-automation", UiAutomationCheck);
            Check("audio-devices", AudioDevicesCheck);
            Check("shared-capture", SharedCaptureCheck);
            Check("device-notifications", DeviceNotificationsCheck);
            Check("real-microphone", RealMicrophoneCheck);
            Check("field-reading", FieldReadingCheck);
            Check("debug-report", () => DebugReportCheck(temp));
            Check("ui", () => UiCheck(temp));
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Emit(_failures == 0 ? "RESULT PASS" : $"RESULT FAIL ({_failures} failed)");
        _file?.Dispose();
        _console?.Flush();
        return _failures == 0 ? 0 : 1;
    }

    sealed class SkipException(string reason) : Exception(reason);

    static void Check(string name, Func<string> check)
    {
        try
        {
            Emit($"PASS {name}: {check()}");
        }
        catch (SkipException e)
        {
            Emit($"SKIP {name}: {e.Message}");
        }
        catch (Exception e)
        {
            _failures++;
            Emit($"FAIL {name}: {e.GetType().Name}: {e.Message}");
        }
    }

    static void Emit(string line)
    {
        Lines.Add(line);
        _file?.WriteLine(line);
        _console?.WriteLine(line);
    }

    static TextWriter? OpenConsole()
    {
        try
        {
            var handle = GetStdHandle(STD_OUTPUT_HANDLE);
            // A GUI-subsystem exe gets a stdout handle only when the parent redirected it; otherwise borrow the
            // parent's console window if there is one.
            if ((handle == IntPtr.Zero || handle == new IntPtr(-1)) && !AttachConsole(ATTACH_PARENT_PROCESS)) return null;
            var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine();
            return writer;
        }
        catch (IOException) { return null; }
    }

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>Runs an async operation on this thread, pumping messages so hooks and timers keep working.</summary>
    static T Pump<T>(Func<Task<T>> work, TimeSpan timeout)
    {
        var task = work();
        var until = DateTime.UtcNow + timeout;
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("timed out");
            Application.DoEvents();
            Thread.Sleep(5);
        }
        return task.GetAwaiter().GetResult();
    }

    static bool HasInteractiveDesktop()
    {
        var desktop = OpenInputDesktop(0, false, 0x0001);
        if (desktop == IntPtr.Zero) return false;
        CloseDesktop(desktop);
        return true;
    }

    // MARK: Checks

    static string StructLayout()
    {
        var input = Marshal.SizeOf<INPUT>();
        var expected = IntPtr.Size == 8 ? 40 : 28;
        Assert(input == expected, $"sizeof(INPUT) is {input}, expected {expected}");
        var hook = Marshal.SizeOf<KBDLLHOOKSTRUCT>();
        Assert(hook == (IntPtr.Size == 8 ? 24 : 20), $"sizeof(KBDLLHOOKSTRUCT) is {hook}");
        var offset = Marshal.OffsetOf<INPUT>(nameof(INPUT.u)).ToInt32();
        Assert(offset == (IntPtr.Size == 8 ? 8 : 4), $"INPUT union offset is {offset}");
        return $"INPUT {input} bytes, union at {offset}, KBDLLHOOKSTRUCT {hook} bytes";
    }

    static string Prompt()
    {
        Assert(!CleanupPrompt.System.Contains('\r'), "the cleanup prompt has CR characters (CRLF checkout not normalized)");
        Assert(CleanupPrompt.System.Length == 4370, $"cleanup prompt is {CleanupPrompt.System.Length} characters, expected 4370");
        return "cleanup prompt matches the macOS length with LF line endings";
    }

    static string DataFolder(string temp)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert(appData.Length > 0, "%APPDATA% is not available");
        Directory.CreateDirectory(temp);
        var prefs = new PreferenceStore(temp);
        var p = new Preferences { Languages = ["en", "te"], Hotkey = Hotkey.RightCtrl, HistoryRetentionDays = 7 };
        prefs.Save(p);
        prefs.Save(p);   // the second write replaces an existing file
        Assert(prefs.Load().Equals(p), "settings did not round-trip");
        var history = new JsonFile<List<HistoryEntry>>("history.json", temp);
        history.Save([new HistoryEntry(HistoryKind.Dictation, "Notepad", "raw", "నమస్తే", 1, 1, "pasted")]);
        Assert(history.Load()?.Single().Final == "నమస్తే", "history did not round-trip");
        Assert(Directory.GetFiles(temp, "*.tmp", SearchOption.AllDirectories).Length == 0, "temporary files were left behind");
        return $"atomic JSON write/read in a temp folder; data folder would be {Path.Combine(appData, "VoiceFlow")}";
    }

    static string CredentialManager()
    {
        var target = $"VoiceFlow-selftest/{Guid.NewGuid():N}";
        const string secret = "selftest-not-a-real-key-✓";
        try
        {
            CredentialStore.Write(target, secret);
            Assert(CredentialStore.Read(target) == secret, "read back a different value");
            CredentialStore.Delete(target);
            Assert(CredentialStore.Read(target) == null, "still present after delete");
            return "write, read, delete of a dummy secret";
        }
        finally
        {
            try { CredentialStore.Delete(target); } catch (System.ComponentModel.Win32Exception) { }
        }
    }

    static string Multipart()
    {
        var wav = AudioMath.Wav(new float[1600]);
        using var content = GroqTranscriber.BuildContent(wav, "whisper-large-v3-turbo", "te", "Saideep", "verbose_json");
        var bytes = content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        var text = Encoding.UTF8.GetString(bytes);
        foreach (var field in new[] { "name=model", "name=response_format", "name=temperature", "name=language", "name=prompt", "name=file", "filename=audio.wav", "audio/wav", "RIFF" })
            Assert(text.Contains(field, StringComparison.Ordinal), $"missing {field}");
        Assert(bytes.Length > wav.Length, "body is smaller than the audio");
        return $"{bytes.Length} bytes for a {wav.Length}-byte WAV (not sent)";
    }

    static string SoundsCheck()
    {
        var wav = Sounds.Tone([(880, 0.05)], 0.1f);
        Assert(Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && wav.Length > 44, "tone is not a WAV");
        return $"generated cue tone ({wav.Length} bytes, not played)";
    }

    static string ClipboardRoundTrip(IntPtr owner)
    {
        if (!HasInteractiveDesktop()) throw new SkipException("no interactive desktop");
        var clipboard = new ClipboardService(owner);
        var saved = Pump(() => clipboard.SaveAsync(), TimeSpan.FromSeconds(5));
        if (saved == null) throw new SkipException("the clipboard stayed locked by another process");
        var text = "VoiceFlow self-test ✓ నమస్తే\r\nline 2";
        var sequence = Pump(() => clipboard.SetTextAsync(text, transient: true), TimeSpan.FromSeconds(5));
        Assert(sequence != null, "could not set clipboard text");
        Assert(ClipboardService.HasFormat(ClipboardService.CanIncludeInHistory), "history-exclusion format missing");
        Assert(ClipboardService.HasFormat(ClipboardService.CanUploadToCloud), "cloud-exclusion format missing");
        Assert(ClipboardService.HasFormat(ClipboardService.ExcludeFromMonitoring), "monitor-exclusion format missing");
        var back = Pump(() => clipboard.GetTextAsync(), TimeSpan.FromSeconds(5));
        Assert(back == text, "read back different text");
        Assert(Pump(() => clipboard.RestoreAsync(saved), TimeSpan.FromSeconds(5)), "could not restore the clipboard");
        return $"text with exclusion formats set, read back, previous clipboard ({saved.Count} formats) restored";
    }

    static string KeyboardHookCheck()
    {
        var processor = new HotkeyProcessor(Hotkey.CtrlWin, new GestureRecognizer());
        int ownSeen = 0, actions = 0;
        using var hook = new KeyboardHook((vk, scan, down, flags, extra) =>
        {
            var own = (flags & LLKHF_INJECTED) != 0 && extra == InputSender.Marker;
            if (own && vk == HotkeyProcessor.MaskVk) ownSeen++;
            var result = processor.Handle(new KeyInput(vk, down, scan, own), 0);
            actions += result.Actions.Count;
            return false;
        });
        hook.Install();
        Assert(hook.IsInstalled, "SetWindowsHookEx returned no handle");
        if (!HasInteractiveDesktop())
        {
            hook.Uninstall();
            throw new SkipException("hook installed and removed; no interactive desktop to inject input");
        }
        if (!InputSender.TapMaskKey()) throw new SkipException($"SendInput was refused (error {Marshal.GetLastWin32Error()})");
        var until = DateTime.UtcNow.AddSeconds(2);
        while (ownSeen < 2 && DateTime.UtcNow < until)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
        hook.Uninstall();
        Assert(!hook.IsInstalled, "hook still installed");
        if (ownSeen == 0) throw new SkipException("the injected key never reached the hook (input may be blocked on this desktop)");
        Assert(actions == 0, "an injected key produced a gesture");
        return $"installed, saw {ownSeen} of VoiceFlow's own injected events and ignored them, uninstalled";
    }

    /// <summary>The real hotkey monitor: hook on its own thread, our injected key seen there and ignored, clean shutdown.</summary>
    static string HotkeyThreadCheck()
    {
        using var poster = new Control();
        poster.CreateControl();
        int gestures = 0;
        var monitor = new HotkeyMonitor(poster, Hotkey.CtrlWin, _ => gestures++, _ => gestures++);
        try
        {
            monitor.Start();
            Assert(monitor.IsRunning, "hook thread started but the hook is not installed");
            if (!HasInteractiveDesktop()) throw new SkipException("hook thread started and stopped; no interactive desktop to inject input");
            // Block the UI thread briefly: keys must still reach the hook, which no longer depends on this thread.
            if (!InputSender.TapMaskKey()) throw new SkipException($"SendInput was refused (error {Marshal.GetLastWin32Error()})");
            var until = DateTime.UtcNow.AddSeconds(2);
            while (monitor.OwnInjectedSeen < 2 && DateTime.UtcNow < until) Thread.Sleep(5);
            if (monitor.OwnInjectedSeen == 0) throw new SkipException("the injected key never reached the hook (input may be blocked on this desktop)");
            Application.DoEvents();
            Assert(gestures == 0, "an injected key produced a gesture");
            return $"hook ran on its own thread while the UI thread was blocked, saw {monitor.OwnInjectedSeen} own events, ignored them";
        }
        finally
        {
            monitor.Dispose();
            Assert(!monitor.IsRunning, "hook still installed after Dispose");
        }
    }

    /// <summary>Checks the hand-declared IUIAutomation vtable against VoiceFlow's own password and text boxes.</summary>
    static string UiAutomationCheck()
    {
        using var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) };
        var password = new TextBox { UseSystemPasswordChar = true, Text = "hidden" };
        var plain = new TextBox { Text = "VoiceFlow UIA check", Top = 30 };
        form.Controls.Add(password);
        form.Controls.Add(plain);
        _ = form.Handle;
        var passwordHandle = password.Handle;
        var plainHandle = plain.Handle;
        (bool, string?) secret, text;
        try
        {
            // UI Automation must not be called from the thread that owns the windows; this thread keeps pumping.
            secret = Pump(() => Task.Run(() => UIAutomation.FromHandle(passwordHandle, readValue: true)), TimeSpan.FromSeconds(10));
            text = Pump(() => Task.Run(() => UIAutomation.FromHandle(plainHandle, readValue: true)), TimeSpan.FromSeconds(10));
        }
        catch (COMException e)
        {
            throw new SkipException($"UI Automation unavailable (0x{e.HResult:X8})");
        }
        Assert(secret.Item1, "a password box was not reported as a password field");
        Assert(secret.Item2 == null, "a password box value was read");
        Assert(!text.Item1, "a plain text box was reported as a password field");
        Assert(text.Item2 == "VoiceFlow UIA check", $"read \"{text.Item2}\" from a text box");
        return "password box detected, plain text box value read";
    }

    static string AudioDevicesCheck()
    {
        var inputs = AudioDevices.Inputs();
        var outputs = AudioDevices.Outputs();
        if (inputs.Count == 0) throw new SkipException($"no active microphones ({outputs.Count} output devices)");
        return $"{inputs.Count} microphones, {outputs.Count} output devices";
    }

    /// <summary>The whole shared-capture design on a simulated device: runs with no microphone at all.</summary>
    static string SharedCaptureCheck()
    {
        var now = TimeSpan.FromSeconds(100);
        var source = new SimulatedMicrophoneSource();
        using var hub = new SharedMicrophone(source, new SharedMicrophoneOptions { PollInterval = null }, () => now);
        var frames = new float[800];
        var voice = Enumerable.Range(0, 800).Select(i => 0.3f * MathF.Sin(i * 0.2f)).ToArray();

        var meeting = hub.Subscribe("meeting");
        var dictation = hub.Subscribe("dictation");
        Assert(source.Opened.Count == 1, "a second subscriber opened another device");
        source.Latest!.Emit(voice);
        Assert(meeting.SampleCount == voice.Length && dictation.SampleCount == voice.Length, "subscribers did not both get the audio");
        dictation.Stop();
        Assert(source.Latest!.Running, "stopping one subscriber stopped the device");
        source.Latest!.Emit(voice);
        Assert(meeting.SampleCount == 2 * voice.Length, "the meeting lost audio when dictation stopped");

        source.Latest!.SimulateLoss(new TimeoutException());
        now += TimeSpan.FromSeconds(1);
        hub.Poll();
        Assert(source.Opened.Count == 2, "a lost device was not reopened");
        source.Latest!.Emit(voice);
        now += TimeSpan.FromSeconds(1);
        hub.Poll();
        Assert(hub.Health.Status == MicHealthStatus.Healthy && hub.Diagnostics.Restarts == 1, $"status {hub.Health.Status}, {hub.Diagnostics.Restarts} restarts");

        source.CurrentId = "mic-2";
        source.RaiseDevicesChanged();
        hub.Poll();
        Assert(source.Opened.Count == 3 && hub.Diagnostics.DeviceSwitches == 1, "a default-device change did not switch");

        for (int i = 0; i < 12; i++)
        {
            source.Latest!.Emit(frames);
            now += TimeSpan.FromSeconds(1);
            hub.Poll();
        }
        Assert(hub.Diagnostics.DeadSignals == 1, "digital silence was not reported as a dead microphone");

        meeting.Dispose();
        Assert(!source.Latest!.Running && hub.Health.Status == MicHealthStatus.Idle, "the device kept running after the last subscriber left");
        return "two subscribers, independent stop, loss restart, device switch, dead-signal detection on a simulated device";
    }

    /// <summary>Windows must accept VoiceFlow's device-change callback, or default-device changes go unnoticed.</summary>
    static string DeviceNotificationsCheck()
    {
        WasapiMicrophoneSource.CheckNotificationInterface();
        using var source = new WasapiMicrophoneSource();
        source.EnsureNotifications();
        if (!source.NotificationsRegistered) throw new SkipException("the audio service refused registration (COM callback interface itself is fine)");
        return "COM callback built and registered with the audio service";
    }

    /// <summary>The real WASAPI path, when this machine has a microphone. CI runners don't, so this usually skips.</summary>
    static string RealMicrophoneCheck()
    {
        if (AudioDevices.Inputs().Count == 0) throw new SkipException("no microphone on this machine");
        using var hub = new SharedMicrophone(new WasapiMicrophoneSource(), new SharedMicrophoneOptions { PollInterval = null });
        var first = hub.Subscribe("self-test-1");
        var second = hub.Subscribe("self-test-2");
        var until = DateTime.UtcNow.AddSeconds(2);
        while (first.SampleCount == 0 && DateTime.UtcNow < until) Thread.Sleep(20);
        var name = hub.Health.DeviceName;
        var samples = first.SampleCount;
        if (samples == 0) throw new SkipException($"\"{name}\" delivered no audio in 2 s (privacy switch, or nothing plugged in)");
        first.Dispose();
        Thread.Sleep(50);
        var stillRunning = second.SampleCount;
        Thread.Sleep(300);
        Assert(second.SampleCount > stillRunning, "the second subscriber stopped receiving when the first left");
        second.Dispose();
        return $"\"{name}\": {samples} samples at 16 kHz, one subscriber left and the other kept receiving";
    }

    /// <summary>Reading the focused element at insertion time, as the paste path does it.</summary>
    static string FieldReadingCheck()
    {
        var reading = Pump(() => FocusInfo.ReadFieldAsync(IntPtr.Zero, readValue: false, timeoutMs: 2000), TimeSpan.FromSeconds(5));
        return $"focused element read as {reading.State}";
    }

    /// <summary>The report the bug-report button will use, from the real sources, with leaks attempted.</summary>
    static string DebugReportCheck(string temp)
    {
        const string spoken = "the secret quarterly numbers";
        var folder = Path.Combine(temp, "report");
        Directory.CreateDirectory(folder);
        var model = new AppModel(folder, credentialPrefix: $"VoiceFlow-selftest-{Guid.NewGuid():N}/");
        var log = AppDiagnostics.LogFor(model);
        log.Record(new DeliveryLogEntry(DeliverySession.Dictation, DeliveryReason.PastedUnverified, DeliveryDetail.FieldUnreadable, 2000, 800));
        log.Record(new DeliveryLogEntry(DeliverySession.Dictation, DeliveryReason.Blocked, spoken));
        var history = new HistoryEntry(HistoryKind.Dictation, "App", spoken, spoken, 1, 1, "pasted");
        model.Record(history);
        using var hub = new SharedMicrophone(new SimulatedMicrophoneSource(), new SharedMicrophoneOptions { PollInterval = null });
        var report = AppDiagnostics.BuildDebugReport(model, hub);
        foreach (var expected in new[] { "VoiceFlow debug report", "pasted-unverified", "field-unreadable", "Restarts: 0" })
            Assert(report.Contains(expected, StringComparison.Ordinal), $"missing \"{expected}\"");
        Assert(!report.Contains(spoken, StringComparison.Ordinal), "the report contained dictated text");
        Assert(!report.Contains("gsk_", StringComparison.Ordinal), "the report contained a key");
        return $"{report.Length}-character report with reason codes and no dictated text";
    }

    static string UiCheck(string temp)
    {
        var folder = Path.Combine(temp, "ui");
        Directory.CreateDirectory(folder);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        try
        {
            var model = new AppModel(folder, credentialPrefix: $"VoiceFlow-selftest-{Guid.NewGuid():N}/");
            using var bar = new FlowBarController();
            bar.RenderOffscreen();
            using var notetaker = new Notetaker(model, bar);
            using var owner = new HiddenOwner();
            var inserter = new Dictation.TextInserter(new ClipboardService(owner.Handle));
            using var dictation = new Dictation.DictationController(model, bar, inserter, new OutputMuter(folder));
            using var window = new MainWindow(model, notetaker, inserter, null)
            {
                StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false,
            };
            _ = window.Handle;
            foreach (var page in Enum.GetValues<PageId>()) window.ShowPage(page);
            using var onboarding = new OnboardingForm(model, null);
            _ = onboarding.Handle;
            window.HideOnClose = false;
            return "main window (all pages), onboarding, Flow bar and toast built off-screen and disposed";
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(null);
        }
    }

    /// <summary>A hidden top-level window used as clipboard owner.</summary>
    sealed class HiddenOwner : NativeWindow, IDisposable
    {
        public HiddenOwner() => CreateHandle(new CreateParams { Caption = "VoiceFlow self-test" });
        public void Dispose() => DestroyHandle();
    }
}
