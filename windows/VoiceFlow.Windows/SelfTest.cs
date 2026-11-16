using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VoiceFlow.Core;
using VoiceFlow.Core.AI;
using VoiceFlow.Core.Audio;
using VoiceFlow.Core.Diagnostics;
using VoiceFlow.Core.FlowBar;
using VoiceFlow.Core.Input;
using VoiceFlow.Core.Library;
using VoiceFlow.Core.Meetings;
using VoiceFlow.Core.Speech;
using VoiceFlow.Windows.Audio;
using VoiceFlow.Windows.Dictation;
using VoiceFlow.Windows.Input;
using VoiceFlow.Windows.Meetings;
using VoiceFlow.Windows.Native;
using VoiceFlow.Desktop.Services;
using VoiceFlow.Desktop.ViewModels;
using VoiceFlow.Desktop.Views;
using VoiceFlow.Windows.Shell;
using static VoiceFlow.Windows.Native.NativeMethods;
using Page = VoiceFlow.Desktop.ViewModels.Page;

namespace VoiceFlow.Windows;

/// <summary>
/// <c>VoiceFlow.exe --self-test [--self-test-out path]</c>: checks the Windows-specific pieces on a real Windows
/// machine (CI runners) without showing UI or touching the user's data or keys. One line per check:
/// <c>PASS name: detail</c>, <c>SKIP name: reason</c> or <c>FAIL name: reason</c>. Exit code 0 when nothing
/// failed, 1 otherwise. It runs the real Avalonia shell off-screen, so CI exercises the exe that ships, and leaves no
/// window or tray icon behind.
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
        var version = typeof(SelfTest).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        Emit($"VoiceFlow {version} self-test on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})");
        var temp = Path.Combine(Path.GetTempPath(), "VoiceFlow-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Avalonia first: the hotkey monitor and the Flow bar need its dispatcher, and this is the check that the
            // published single-file exe can start the UI at all.
            var uiReady = false;
            Check("avalonia-start", () =>
            {
                Program.BuildAvaloniaApp().SetupWithoutStarting();
                // Async helpers below are pumped by hand; a UI synchronization context would deadlock them.
                SynchronizationContext.SetSynchronizationContext(null);
                uiReady = true;
                return "Win32 and Skia initialized";
            });
            Check("struct-layout", StructLayout);
            Check("prompt", Prompt);
            Check("data-folder", () => DataFolder(temp));
            Check("credential-manager", CredentialManager);
            Check("multipart", Multipart);
            Check("sounds", SoundsCheck);
            using var owner = new MessageWindow();
            Check("clipboard", () => ClipboardRoundTrip(owner.Handle));
            Check("keyboard-hook", KeyboardHookCheck);
            Check("hotkey-thread", HotkeyThreadCheck);
            Check("ui-automation", UiAutomationCheck);
            Check("audio-devices", AudioDevicesCheck);
            Check("shared-capture", SharedCaptureCheck);
            Check("device-notifications", DeviceNotificationsCheck);
            Check("audio-sessions", AudioSessionsCheck);
            Check("call-end", CallEndCheck);
            Check("capture-format-change", CaptureFormatChangeCheck);
            Check("capture-errors", CaptureErrorsCheck);
            Check("real-microphone", RealMicrophoneCheck);
            Check("field-reading", FieldReadingCheck);
            Check("debug-report", () => DebugReportCheck(temp));
            if (uiReady)
            {
                Check("resources", ResourcesCheck);
                Check("tray-icon", TrayIconCheck);
                Check("flow-bar-window", () => FlowBarWindowCheck(temp));
                FlowBarScreenChecks(temp);
                Check("dictation-bar-reset", () => DictationBarResetCheck(temp));
                Check("ui", () => UiCheck(temp));
            }
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
            PumpMessages();
            Thread.Sleep(5);
        }
        return task.GetAwaiter().GetResult();
    }

    /// <summary>Delivers pending Win32 messages and Avalonia work on this thread.</summary>
    static void PumpMessages()
    {
        while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        Dispatcher.UIThread.RunJobs();
    }

    static void PumpFor(TimeSpan time)
    {
        var until = DateTime.UtcNow + time;
        while (DateTime.UtcNow < until)
        {
            PumpMessages();
            Thread.Sleep(5);
        }
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
        Assert(CleanupPrompt.System.Length == 5388, $"cleanup prompt is {CleanupPrompt.System.Length} characters, expected 5388");
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
            PumpMessages();
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
        int gestures = 0;
        var monitor = new HotkeyMonitor(Hotkey.CtrlWin, _ => gestures++, _ => gestures++);
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
            PumpMessages();
            Assert(gestures == 0, "an injected key produced a gesture");
            return $"hook ran on its own thread while the UI thread was blocked, saw {monitor.OwnInjectedSeen} own events, ignored them";
        }
        finally
        {
            monitor.Dispose();
            Assert(!monitor.IsRunning, "hook still installed after Dispose");
        }
    }

    /// <summary>A plain Win32 edit box, never shown. The caller destroys it.</summary>
    static IntPtr NativeEdit(string text, bool password)
    {
        var hwnd = CreateWindowExW(0, "EDIT", text, WS_POPUP | (password ? ES_PASSWORD : 0), 0, 0, 200, 30, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return hwnd;
    }

    /// <summary>Checks the hand-declared IUIAutomation vtables against real Win32 password and edit boxes.</summary>
    static string UiAutomationCheck()
    {
        var password = NativeEdit("hidden", password: true);
        var plain = NativeEdit("VoiceFlow UIA check", password: false);
        try
        {
            (bool, string?) secret, text;
            string before;
            try
            {
                // UI Automation must not be called from the thread that owns the windows; this thread keeps pumping.
                secret = Pump(() => Task.Run(() => UIAutomation.FromHandle(password, readValue: true)), TimeSpan.FromSeconds(10));
                text = Pump(() => Task.Run(() => UIAutomation.FromHandle(plain, readValue: true)), TimeSpan.FromSeconds(10));
                SendMessageW(plain, EM_SETSEL, new IntPtr(9), new IntPtr(9));   // caret after "VoiceFlow"
                before = Pump(() => Task.Run(() => UIAutomation.TextBeforeCursorOf(plain, 120)), TimeSpan.FromSeconds(10));
                var passwordBefore = Pump(() => Task.Run(() => UIAutomation.TextBeforeCursorOf(password, 120)), TimeSpan.FromSeconds(10));
                Assert(passwordBefore.Length == 0, "text before the caret was read from a password box");
            }
            catch (COMException e)
            {
                throw new SkipException($"UI Automation unavailable (0x{e.HResult:X8})");
            }
            Assert(secret.Item1, "a password box was not reported as a password field");
            Assert(secret.Item2 == null, "a password box value was read");
            Assert(!text.Item1, "a plain text box was reported as a password field");
            Assert(text.Item2 == "VoiceFlow UIA check", $"read \"{text.Item2}\" from a text box");
            // A never-shown edit box has no focus, and its caret isn't exposed. Wrong text is still a failure.
            if (before.Length == 0) throw new SkipException("password and value reads passed; caret text needs a focused field");
            Assert(before == "VoiceFlow", $"read \"{before}\" before the caret");
            return "password box detected, plain edit value and text before the caret read";
        }
        finally
        {
            DestroyWindow(password);
            DestroyWindow(plain);
        }
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

        // Exact zeros, as the Mac microphone sent while a call app held it: rebuilt at 3 s, reported at 6 s.
        var opened = source.Opened.Count;
        var reportedAt = -1.0;
        for (double t = 0.5; t <= 12; t += 0.5)
        {
            source.Latest!.Emit(frames);
            now += TimeSpan.FromSeconds(0.5);
            hub.Poll();
            if (reportedAt < 0 && hub.Health.Status == MicHealthStatus.DeadSignal) reportedAt = t;
        }
        Assert(hub.Diagnostics.DeadSignals == 1, "digital silence was not reported as a dead microphone");
        Assert(reportedAt is > 0 and <= 6.5, $"digital silence reported after {reportedAt} s");
        Assert(source.Opened.Count - opened >= 2, "a silent microphone was not rebuilt");

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

    /// <summary>
    /// The audio-session enumeration that decides whether a call app holds the microphone, on this machine's real
    /// endpoints. CI runners have few or none, so this proves the COM path runs and fails soft, not what a call looks like.
    /// </summary>
    static string AudioSessionsCheck()
    {
        var capture = AudioSessions.Read(NAudio.CoreAudioApi.DataFlow.Capture, out var captureEndpoints);
        var render = AudioSessions.Read(NAudio.CoreAudioApi.DataFlow.Render, out var renderEndpoints);
        var another = AudioSessions.AnotherAppIsRecording();
        if (capture == null && render == null) throw new SkipException("the audio service could not be asked for sessions");
        var self = (uint)Environment.ProcessId;
        Assert(capture == null || CaptureSessions.AnotherAppIsRecording(capture, self) is not null, "sessions were read but gave no answer");
        return $"{captureEndpoints} capture endpoints with {capture?.Count ?? 0} sessions ({capture?.Count(c => c.Active) ?? 0} active), "
               + $"{renderEndpoints} output endpoints with {render?.Count ?? 0} sessions; another app recording: {another?.ToString() ?? "unknown"}";
    }

    /// <summary>Auto-stop on simulated sessions: a call app records, lets go, and the call audio stops for a minute.</summary>
    static string CallEndCheck()
    {
        var self = (uint)Environment.ProcessId;
        var call = new CaptureSession[] { new(self, true), new(self + 1, true) };
        var ended = new CaptureSession[] { new(self, true), new(self + 1, false), new(0, true) };
        var detector = new CallEndDetector();
        var stops = new List<double>();
        for (double t = 0; t <= 300; t += 5)
        {
            var sessions = t < 120 ? call : ended;
            var silent = t < 120 ? 0 : (int)(t - 120);
            if (detector.Observe(t, CaptureSessions.AnotherAppIsRecording(sessions, self), silent)) stops.Add(t);
        }
        Assert(stops.SequenceEqual([180.0]), $"stopped at [{string.Join(", ", stops)}], expected [180]");
        var alone = new CallEndDetector();
        for (double t = 0; t <= 600; t += 5)
            Assert(!alone.Observe(t, CaptureSessions.AnotherAppIsRecording([new(self, true)], self), (int)t), "a meeting with no call app stopped by itself");
        return "stops 60 s after the call app lets go; VoiceFlow's own session and the system session never count as a call";
    }

    /// <summary>The per-buffer conversion, with the format changing under a running capture.</summary>
    static string CaptureFormatChangeCheck()
    {
        var floatStereo = WasapiMicrophone.FormatOf(NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2));
        var pcmQuad = WasapiMicrophone.FormatOf(new NAudio.Wave.WaveFormat(44_100, 16, 4));
        Assert(floatStereo == new CaptureFormat(48_000, 2, 32, true), $"float format read as {floatStereo}");
        Assert(pcmQuad == new CaptureFormat(44_100, 4, 16, false), $"PCM format read as {pcmQuad}");
        var converter = new CaptureConverter(oneChannel: true);
        var before = 0;
        var after = new List<float>();
        for (int i = 0; i < 5; i++)
        {
            var frames = Enumerable.Range(0, 4_800).SelectMany(f => { var v = 0.3f * MathF.Sin(f * 0.04f); return new[] { v, -v }; }).ToArray();
            before += converter.Convert(MemoryMarshal.AsBytes(frames.AsSpan()), floatStereo).Length;
        }
        for (int i = 0; i < 5; i++)
        {
            var pcm = Enumerable.Range(0, 4_410).SelectMany(f => Enumerable.Repeat((short)(9_000 * MathF.Sin(f * 0.04f)), 4)).SelectMany(BitConverter.GetBytes).ToArray();
            after.AddRange(converter.Convert(pcm, pcmQuad));
        }
        Assert(converter.FormatChanges == 1, $"{converter.FormatChanges} format changes seen");
        Assert(before is > 7_000 and <= 8_000 && after.Count is > 7_000 and <= 8_000, $"{before} and {after.Count} samples at 16 kHz");
        var level = AudioMath.Dbfs(AudioMath.Rms(after.Skip(2_000).ToArray()));
        Assert(level > -20, $"audio after the change at {level:F1} dBFS");
        return $"48 kHz stereo float, then 44.1 kHz 4-channel PCM: {before} and {after.Count} samples, {level:F1} dBFS after the change";
    }

    /// <summary>The messages a user sees when a call app locks the microphone or the device goes away.</summary>
    static string CaptureErrorsCheck()
    {
        var exclusive = AudioCapture.Translate(new COMException("in use", unchecked((int)AudioCapture.DeviceInUse)), loopback: false);
        Assert(exclusive.Message == AudioCapture.ExclusiveMicrophone, "AUDCLNT_E_DEVICE_IN_USE did not explain exclusive mode");
        var speakers = AudioCapture.Translate(new COMException("in use", unchecked((int)AudioCapture.DeviceInUse)), loopback: true);
        Assert(speakers.Message == AudioCapture.ExclusiveSpeakers, "loopback exclusive mode was not explained");
        var takeover = AudioCapture.Disconnected(NAudio.CoreAudioApi.Interfaces.AudioSessionDisconnectReason.DisconnectReasonExclusiveModeOverride, loopback: false);
        Assert(takeover.Message == AudioCapture.ExclusiveMicrophone, "an exclusive-mode takeover was not explained");
        var privacy = AudioCapture.Translate(new UnauthorizedAccessException(), loopback: false);
        Assert(privacy.Message.StartsWith("Microphone access is off", StringComparison.Ordinal), "the privacy switch was not explained");
        return "exclusive mode, exclusive takeover, privacy switch";
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

    /// <summary>Everything the published exe needs from inside itself: icon resources, compiled XAML, native Skia and fonts.</summary>
    static string ResourcesCheck()
    {
        using var embedded = typeof(SelfTest).Assembly.GetManifestResourceStream("VoiceFlow.ico");
        Assert(embedded is { Length: > 0 }, "the tray icon is not embedded in the exe");
        Assert(WindowsApp.LoadIcon() != null, "the tray icon did not load");
        using (var asset = AssetLoader.Open(new Uri("avares://VoiceFlow.Desktop/Assets/VoiceFlow.ico")))
            Assert(asset.Length > 0, "the window icon asset is empty");
        Assert(Application.Current?.Resources.ContainsKey("FontText") == true || Application.Current?.TryFindResource("FontText", out _) == true, "theme resources did not load");

        // Rendering proves libSkiaSharp and libHarfBuzzSharp were found, wherever the single-file host put them.
        var text = new TextBlock { Text = "నమస్తే नमस्ते VoiceFlow", FontSize = 18, FontFamily = new FontFamily("Segoe UI, Nirmala UI") };
        text.Measure(Size.Infinity);
        text.Arrange(new Rect(text.DesiredSize));
        using var bitmap = new RenderTargetBitmap(new PixelSize(320, 48));
        bitmap.Render(text);

        var nirmala = FontManager.Current.SystemFonts.Any(f => f.Name == "Nirmala UI");
        var bundled = File.Exists(Path.Combine(AppContext.BaseDirectory, "VoiceFlow.dll")) ? "loose files" : "single-file bundle";
        return $"{bundled}: icons and theme found, text rendered; Nirmala UI {(nirmala ? "present" : "not installed on this machine")}";
    }

    static (AppModel Model, SharedMicrophone Hub, DictationController Dictation, Notetaker Notetaker, FlowBarController Bar, AppServices Services, MessageWindow Owner) Compose(string folder)
    {
        Directory.CreateDirectory(folder);
        var model = new AppModel(folder, credentialPrefix: $"VoiceFlow-selftest-{Guid.NewGuid():N}/");
        var hub = new SharedMicrophone(new SimulatedMicrophoneSource(), new SharedMicrophoneOptions { PollInterval = null });
        var bar = new FlowBarController();
        var notetaker = new Notetaker(model, bar, hub);
        var owner = new MessageWindow();
        var inserter = new TextInserter(new ClipboardService(owner.Handle));
        var dictation = new DictationController(model, bar, inserter, new OutputMuter(folder), hub);
        return (model, hub, dictation, notetaker, bar, new AppServices(model, dictation, notetaker, bar, inserter, hub), owner);
    }

    /// <summary>
    /// The real Flow bar window: its native styles, its shape, and that it appears only when there is something to
    /// show. None of this needs an interactive desktop.
    /// </summary>
    static string FlowBarWindowCheck(string temp)
    {
        var (_, hub, dictation, notetaker, bar, services, owner) = Compose(Path.Combine(temp, "bar"));
        using (hub) using (dictation) using (notetaker) using (bar) using (owner)
        using (var host = new FlowBarHost(bar, new FlowBarViewModel(services, runClock: false), services.Preferences))
        {
            Assert(host.Handle != IntPtr.Zero, "the Flow bar has no native handle");
            var style = GetWindowLongPtrW(host.Handle, GWL_EXSTYLE).ToInt64();
            Assert((style & WS_EX_NOACTIVATE) != 0, "WS_EX_NOACTIVATE is not set");
            Assert((style & WS_EX_TOOLWINDOW) != 0, "WS_EX_TOOLWINDOW is not set");
            Assert((style & WS_EX_LAYERED) != 0, "WS_EX_LAYERED is not set");
            Assert(!IsWindowVisible(host.Handle), "the bar is on screen while idle");
            bar.Show("Self-test", seconds: 30);
            PumpFor(TimeSpan.FromMilliseconds(250));
            Assert(IsWindowVisible(host.Handle), "a toast did not show the bar");
            Assert((GetWindowLongPtrW(host.Handle, GWL_EXSTYLE).ToInt64() & WS_EX_NOACTIVATE) != 0, "showing the bar dropped WS_EX_NOACTIVATE");

            // Avalonia calls SetFocus on every click, and rebuilds the extended style on any window change.
            SetFocus(host.Handle);
            PumpMessages();
            Assert(GetFocus() != host.Handle, "the bar accepted the keyboard focus, so a click would steal it from the app being typed in");
            host.Window.CanResize = true;
            host.Window.CanResize = false;
            PumpFor(TimeSpan.FromMilliseconds(50));
            var rebuilt = GetWindowLongPtrW(host.Handle, GWL_EXSTYLE).ToInt64();
            Assert((rebuilt & WS_EX_NOACTIVATE) != 0 && (rebuilt & WS_EX_LAYERED) != 0, $"Avalonia's style update dropped the bar's flags (0x{rebuilt:X8})");

            // The window region and WM_NCHITTEST both follow the toast's rounded outline.
            Assert(host.Parts.Count == 1, $"expected the toast as the only visible part, found {host.Parts.Count}");
            var toast = host.Parts[0];
            Assert(GetWindowRect(host.Handle, out var frame), "no window rectangle");
            var region = CreateRectRgn(0, 0, 0, 0);
            try
            {
                var kind = GetWindowRgn(host.Handle, region);
                Assert(kind != ERROR_REGION && kind != NULLREGION, $"the bar has no window region (GetWindowRgn returned {kind})");
                Assert(!PtInRegion(region, 0, 0), "the window region covers the window's corner, outside the toast");
                Assert(PtInRegion(region, (int)toast.X + (int)(toast.Width / 2), (int)toast.Y + (int)(toast.Height / 2)), "the window region misses the toast");
            }
            finally { DeleteObject(region); }
            var origin = new POINT();
            ClientToScreen(host.Handle, ref origin);
            var corner = HitTestAt(host.Handle, frame.Left, frame.Top);
            var middle = HitTestAt(host.Handle, origin.X + (int)(toast.X + toast.Width / 2), origin.Y + (int)(toast.Y + toast.Height / 2));
            Assert(corner == HTTRANSPARENT, $"WM_NCHITTEST at the window's corner returned {corner}, expected HTTRANSPARENT");
            Assert(middle != HTTRANSPARENT, "WM_NCHITTEST on the toast returned HTTRANSPARENT");

            bar.DismissToast();
            PumpFor(TimeSpan.FromMilliseconds(100));
            Assert(!IsWindowVisible(host.Handle), "the bar stayed on screen after its toast");
            return $"extended styles 0x{style:X8}; hidden at rest, shown by a toast, refuses focus, keeps its flags, region and hit test follow the toast's outline, hidden again";
        }
    }

    static int HitTestAt(IntPtr hwnd, int x, int y) =>
        (int)SendMessageW(hwnd, WM_NCHITTEST, IntPtr.Zero, new IntPtr((y << 16) | (x & 0xFFFF))).ToInt64();

    /// <summary>
    /// The Flow bar over another window, with real mouse input: it must paint its shape and nothing else, pass
    /// clicks outside the shape to the window behind, take clicks on its buttons without taking the focus, and
    /// remember where it was dragged. Needs an interactive desktop; a runner without one skips.
    /// </summary>
    static void FlowBarScreenChecks(string temp)
    {
        var names = new[] { "flow-bar-paint", "flow-bar-click-through", "flow-bar-button", "flow-bar-drag" };
        if (!HasInteractiveDesktop())
        {
            foreach (var name in names) Emit($"SKIP {name}: no interactive desktop");
            return;
        }
        BarStage stage;
        try { stage = new BarStage(Path.Combine(temp, "stage")); }
        catch (Exception e)
        {
            foreach (var name in names)
            {
                _failures++;
                Emit($"FAIL {name}: setting up the bar over a test window failed: {e.GetType().Name}: {e.Message}");
            }
            return;
        }
        GetCursorPos(out var cursor);
        try
        {
            Check("flow-bar-paint", stage.Paint);
            Check("flow-bar-click-through", stage.ClickThrough);
            Check("flow-bar-button", stage.ButtonClick);
            Check("flow-bar-drag", stage.Drag);
        }
        finally
        {
            stage.Dispose();
            SetCursorPos(cursor.X, cursor.Y);
        }
    }

    /// <summary>The hands-free bar with a toast above it, in front of a plain window on another thread.</summary>
    sealed class BarStage : IDisposable
    {
        readonly SharedMicrophone _hub;
        readonly DictationController _dictation;
        readonly Notetaker _notetaker;
        readonly FlowBarController _bar;
        readonly MessageWindow _owner;
        readonly AppServices _services;
        readonly FlowBarHost _host;
        readonly BehindWindow _behind;
        readonly string _folder;
        int _cancels;

        public BarStage(string folder)
        {
            _folder = folder;
            (_, _hub, _dictation, _notetaker, _bar, _services, _owner) = Compose(folder);
            _bar.CancelRequested += () => _cancels++;
            _host = new FlowBarHost(_bar, new FlowBarViewModel(_services, runClock: false), _services.Preferences);
            _bar.Set(FlowPhase.HandsFree);
            _bar.Show("Self-test toast", "It sits above the bar.", seconds: 120);
            PumpFor(TimeSpan.FromMilliseconds(400));
            Assert(IsWindowVisible(_host.Handle), "the bar did not show");
            Assert(GetWindowRect(_host.Handle, out var frame), "no window rectangle");
            // The window behind covers the bar's whole rectangle with room to spare, on its own thread like any other app.
            _behind = new BehindWindow(new PxRect(frame.Left - 120, frame.Top - 120, frame.Right - frame.Left + 240, frame.Bottom - frame.Top + 240));
            SetWindowPos(_host.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            Park();
            PumpFor(TimeSpan.FromMilliseconds(300));
        }

        double Scale => _host.Window.RenderScaling;

        POINT ClientOrigin()
        {
            var origin = new POINT();
            ClientToScreen(_host.Handle, ref origin);
            return origin;
        }

        /// <summary>The capsule's rectangle on screen.</summary>
        (int X, int Y, int Width, int Height) Capsule()
        {
            var box = _host.Window.AnchorRect() ?? throw new InvalidOperationException("the capsule has not been laid out");
            var o = ClientOrigin();
            return (o.X + (int)Math.Round(box.X * Scale), o.Y + (int)Math.Round(box.Y * Scale), (int)Math.Round(box.Width * Scale), (int)Math.Round(box.Height * Scale));
        }

        bool InShape(int screenX, int screenY)
        {
            var o = ClientOrigin();
            return BarShape.HitTest(_host.Parts, screenX - o.X, screenY - o.Y);
        }

        /// <summary>Points inside the bar's window rectangle but outside its shape: corners, the gap above the capsule, its rounded ends.</summary>
        List<(string Name, int X, int Y)> OutsidePoints()
        {
            GetWindowRect(_host.Handle, out var frame);
            var c = Capsule();
            var inset = (int)Math.Ceiling(Scale);
            var candidates = new List<(string, int, int)>
            {
                ("window corner", frame.Left + inset, frame.Top + inset),
                ("window corner", frame.Right - 1 - inset, frame.Bottom - 1 - inset),
                ("beside the capsule", c.X - 2 * inset, c.Y + c.Height / 2),
                ("beside the capsule", c.X + c.Width + 2 * inset, c.Y + c.Height / 2),
                ("rounded end of the capsule", c.X + inset, c.Y + inset),
                ("gap between toast and capsule", c.X + c.Width / 2, c.Y - (int)Math.Round(3 * Scale)),
            };
            return candidates.Where(p => p.Item2 >= frame.Left && p.Item2 < frame.Right && p.Item3 >= frame.Top && p.Item3 < frame.Bottom && !InShape(p.Item2, p.Item3)).ToList();
        }

        /// <summary>Moves the pointer onto the window behind, away from the bar, with the bar in front.</summary>
        void Park()
        {
            SetWindowPos(_host.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            SetCursorPos(_behind.Bounds.X + 20, _behind.Bounds.Y + 20);
            PumpFor(TimeSpan.FromMilliseconds(60));
        }

        public string Paint()
        {
            Park();
            var c = Capsule();
            // Five device-independent pixels in from the capsule's left end is plain fill (#161616).
            int fx = c.X + (int)Math.Round(5 * Scale), fy = c.Y + c.Height / 2;
            var fill = ReadPixel(fx, fy);
            if (!Near(fill, 0x16, 0x16, 0x16, 24))
            {
                // Without the region: if the fill shows now, the region is what hid it.
                SetWindowRgn(_host.Handle, IntPtr.Zero, true);
                PumpFor(TimeSpan.FromMilliseconds(300));
                var unshaped = ReadPixel(fx, fy);
                _host.ReapplyShape();
                PumpFor(TimeSpan.FromMilliseconds(100));
                Assert(!Near(unshaped, 0x16, 0x16, 0x16, 24), "the bar paints without its window region but not with it");
                throw new SkipException($"the capsule's fill was not on screen (read #{fill.R:X2}{fill.G:X2}{fill.B:X2}); headless desktop, or the bar did not paint");
            }
            var outside = OutsidePoints();
            Assert(outside.Count >= 3, $"only {outside.Count} test points outside the shape");
            foreach (var (name, x, y) in outside)
            {
                var color = ReadPixel(x, y);
                Assert(Near(color, BehindWindow.Red, BehindWindow.Green, BehindWindow.Blue, 12),
                       $"{name} at ({x}, {y}) shows #{color.R:X2}{color.G:X2}{color.B:X2}, not the window behind");
            }
            return $"capsule fill on screen; {outside.Count} points around it show the window behind unchanged";
        }

        /// <summary>
        /// Another program's window at a test point (a runner's console, a notification) makes a click test
        /// meaningless, so the check is skipped and the window named.
        /// </summary>
        string? ForeignWindowAt(int x, int y, bool expectBar)
        {
            var hit = WindowFromPoint(new POINT { X = x, Y = y });
            var root = hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, GA_ROOT);
            if (root == _host.Handle || (!expectBar && root == _behind.Handle)) return null;
            var name = new StringBuilder(128);
            if (root != IntPtr.Zero) GetClassNameW(root, name, name.Capacity);
            GetWindowThreadProcessId(root, out var pid);
            string process;
            try { process = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { process = $"pid {pid}"; }
            return $"\"{name}\" of {process}";
        }

        public string ClickThrough()
        {
            var outside = OutsidePoints();
            Assert(outside.Count >= 3, $"only {outside.Count} test points outside the shape");
            foreach (var (name, x, y) in outside)
            {
                if (ForeignWindowAt(x, y, expectBar: false) is { } other)
                    throw new SkipException($"another program's window ({other}) covers the {name} at ({x}, {y})");
                var before = _behind.Clicks;
                if (!Click(x, y)) throw new SkipException($"SendInput was refused (error {Marshal.GetLastWin32Error()})");
                var until = DateTime.UtcNow.AddSeconds(2);
                while (_behind.Clicks == before && DateTime.UtcNow < until) PumpFor(TimeSpan.FromMilliseconds(10));
                Assert(_behind.Clicks > before, $"a click on the {name} at ({x}, {y}) did not reach the window behind");
                var at = _behind.LastClick;
                Assert(Math.Abs(at.X - x) <= 1 && Math.Abs(at.Y - y) <= 1, $"the window behind got the click at ({at.X}, {at.Y}), expected ({x}, {y})");
            }
            var mode = _host.RegionHonoured switch { true => "window region", false => "cursor-driven WS_EX_TRANSPARENT fallback", null => "region not probed yet" };
            return $"{outside.Count} clicks outside the shape reached a window on another thread ({mode})";
        }

        public string ButtonClick()
        {
            var button = _host.Window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => AutomationProperties.GetName(b) == "Cancel dictation" && b.IsEffectivelyVisible)
                ?? throw new InvalidOperationException("the hands-free bar has no Cancel button");
            var centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), _host.Window) ?? throw new InvalidOperationException("the Cancel button is not in the window");
            var o = ClientOrigin();
            int x = o.X + (int)Math.Round(centre.X * Scale), y = o.Y + (int)Math.Round(centre.Y * Scale);
            if (ForeignWindowAt(x, y, expectBar: true) is { } other)
                throw new SkipException($"another program's window ({other}) covers the Cancel button at ({x}, {y})");
            var foreground = GetForegroundWindow();
            var behindClicks = _behind.Clicks;
            var cancels = _cancels;
            SetCursorPos(x, y);
            PumpFor(TimeSpan.FromMilliseconds(120));   // the fallback, if it is in use, notices the pointer on its next tick
            if (!Click(x, y)) throw new SkipException($"SendInput was refused (error {Marshal.GetLastWin32Error()})");
            var until = DateTime.UtcNow.AddSeconds(2);
            while (_cancels == cancels && DateTime.UtcNow < until) PumpFor(TimeSpan.FromMilliseconds(10));
            Park();
            Assert(_cancels > cancels, $"a click on the Cancel button at ({x}, {y}) did not reach the bar");
            Assert(_behind.Clicks == behindClicks, "the click on the Cancel button also reached the window behind");
            Assert(GetForegroundWindow() == foreground, "clicking the bar changed the foreground window");
            Assert(GetForegroundWindow() != _host.Handle && GetFocus() != _host.Handle, "the bar took the focus");
            return "the Cancel button got the click; the foreground window and the focus stayed where they were";
        }

        public string Drag()
        {
            var prefs = _services.Preferences;
            Assert(prefs.Current.FlowBarPositions.Count == 0, "a position was saved before the drag");
            var home = _host.Anchor ?? throw new InvalidOperationException("the bar was never placed");
            Assert(GetWindowRect(_host.Handle, out var before), "no window rectangle");
            var c = Capsule();
            // The middle of the hands-free capsule is the level meter, not a button.
            int startX = c.X + c.Width / 2, startY = c.Y + c.Height / 2;
            var monitor = MonitorFromPoint(new POINT { X = startX, Y = startY }, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfoW(monitor, ref info);
            var work = info.rcWork;
            int dx = (int)Math.Round(160 * Scale), dy = -(int)Math.Round(180 * Scale);
            if (startX - dx < work.Left + 2 * c.Width) dx = -dx;
            if (startY + dy < work.Top + 4 * c.Height) throw new SkipException("the screen is too short to drag the bar upwards");
            if (ForeignWindowAt(startX, startY, expectBar: true) is { } other)
                throw new SkipException($"another program's window ({other}) covers the bar at ({startX}, {startY})");
            var foreground = GetForegroundWindow();
            var cancels = _cancels;

            SetCursorPos(startX, startY);
            PumpFor(TimeSpan.FromMilliseconds(60));
            if (!SendMouse(MOUSEEVENTF_LEFTDOWN)) throw new SkipException($"SendInput was refused (error {Marshal.GetLastWin32Error()})");
            PumpFor(TimeSpan.FromMilliseconds(40));
            const int steps = 12;
            for (int i = 1; i <= steps; i++)
            {
                MoveTo(startX - dx * i / steps, startY + dy * i / steps);
                PumpFor(TimeSpan.FromMilliseconds(16));
            }
            PumpFor(TimeSpan.FromMilliseconds(60));
            GetCursorPos(out var end);
            SendMouse(MOUSEEVENTF_LEFTUP);
            PumpFor(TimeSpan.FromMilliseconds(150));
            Park();

            var saved = prefs.Current.FlowBarPositions;
            Assert(saved.Count == 1, $"expected one saved position after the drag, found {saved.Count}");
            int movedX = end.X - startX, movedY = end.Y - startY;
            Assert(Math.Abs(saved[0].X - (home.X + movedX)) <= 2 && Math.Abs(saved[0].Y - (home.Y + movedY)) <= 2,
                   $"saved ({saved[0].X}, {saved[0].Y}); the pointer moved the centre from ({home.X}, {home.Y}) by ({movedX}, {movedY})");
            Assert(GetWindowRect(_host.Handle, out var after), "no window rectangle");
            Assert(Math.Abs(after.Left - before.Left - movedX) <= 2 && Math.Abs(after.Top - before.Top - movedY) <= 2,
                   $"the window moved by ({after.Left - before.Left}, {after.Top - before.Top}), the pointer by ({movedX}, {movedY})");
            Assert(_cancels == cancels, "dragging clicked a button");
            Assert(GetForegroundWindow() == foreground && GetFocus() != _host.Handle, "dragging the bar changed the focus");
            var layout = saved[0].Layout;

            // Saved to disk, and used again the next time the bar shows.
            Assert(new PreferenceStore(_folder).Load().FlowBarPositions.SequenceEqual(saved), "the position was not saved to settings.json");
            _bar.DismissToast();
            _bar.Set(FlowPhase.Idle);
            PumpFor(TimeSpan.FromMilliseconds(80));
            Assert(!IsWindowVisible(_host.Handle), "the bar did not hide");
            _bar.Set(FlowPhase.HandsFree);
            PumpFor(TimeSpan.FromMilliseconds(200));
            Assert(_host.Anchor is { } shown && Math.Abs(shown.X - saved[0].X) <= 1 && Math.Abs(shown.Y - saved[0].Y) <= 1,
                   $"shown again at {_host.Anchor}, not where it was left");
            _host.ViewModel.ResetPositionCommand.Execute(null);
            PumpFor(TimeSpan.FromMilliseconds(150));
            Assert(prefs.Current.FlowBarPositions.Count == 0, "Reset position left a saved position");
            Assert(_host.Anchor is { } back && Math.Abs(back.X - home.X) <= 1 && Math.Abs(back.Y - home.Y) <= 1, "Reset position did not put the bar back at the bottom centre");
            return $"dragged by ({movedX}, {movedY}) px; saved for displays \"{layout}\" at ({saved[0].X}, {saved[0].Y}); no button clicked, focus unchanged; reset returned it home";
        }

        public void Dispose()
        {
            _behind.Dispose();
            _bar.DismissToast();
            _bar.Set(FlowPhase.Idle);
            PumpFor(TimeSpan.FromMilliseconds(50));
            _host.Dispose();
            _dictation.Dispose();
            _notetaker.Dispose();
            _bar.Dispose();
            _hub.Dispose();
            _owner.Dispose();
        }
    }

    static INPUT MouseInput(uint flags, int dx = 0, int dy = 0) =>
        new() { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags } } };

    static bool SendMouse(uint flags) => SendInput(1, [MouseInput(flags)], Marshal.SizeOf<INPUT>()) == 1;

    static bool Click(int x, int y)
    {
        SetCursorPos(x, y);
        PumpFor(TimeSpan.FromMilliseconds(80));   // long enough for the fallback's 33 ms timer, if it is in use
        return SendInput(2, [MouseInput(MOUSEEVENTF_LEFTDOWN), MouseInput(MOUSEEVENTF_LEFTUP)], Marshal.SizeOf<INPUT>()) == 2;
    }

    /// <summary>A real mouse move, in absolute coordinates over the whole virtual desktop.</summary>
    static bool MoveTo(int x, int y)
    {
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN), top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = Math.Max(2, GetSystemMetrics(SM_CXVIRTUALSCREEN)), height = Math.Max(2, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        var nx = (int)Math.Round((x - left) * 65535.0 / (width - 1));
        var ny = (int)Math.Round((y - top) * 65535.0 / (height - 1));
        return SendInput(1, [MouseInput(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny)], Marshal.SizeOf<INPUT>()) == 1;
    }

    static (int R, int G, int B) ReadPixel(int x, int y)
    {
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, 1, 1);
        var previous = SelectObject(memory, bitmap);
        try
        {
            if (!BitBlt(memory, 0, 0, 1, 1, screen, x, y, SRCCOPY | CAPTUREBLT)) throw new SkipException("the screen could not be read");
            var color = GetPixel(memory, 0, 0);
            return ((int)(color & 0xFF), (int)((color >> 8) & 0xFF), (int)((color >> 16) & 0xFF));
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    static bool Near((int R, int G, int B) c, int r, int g, int b, int tolerance) =>
        Math.Abs(c.R - r) <= tolerance && Math.Abs(c.G - g) <= tolerance && Math.Abs(c.B - b) <= tolerance;

    /// <summary>
    /// A plain, solid-coloured Win32 window on its own thread with its own message loop, standing in for another
    /// app under the bar. It counts left clicks and where they landed.
    /// </summary>
    sealed class BehindWindow : IDisposable
    {
        public const int Red = 0x20, Green = 0xC0, Blue = 0x60;
        readonly Thread _thread;
        readonly ManualResetEventSlim _ready = new();
        readonly string _class = "VoiceFlowSelfTest" + Guid.NewGuid().ToString("N");
        WndProc? _proc;   // kept alive while the window exists
        uint _threadId;
        int _clicks;
        long _lastClick;
        Exception? _error;

        public BehindWindow(PxRect bounds)
        {
            Bounds = bounds;
            _thread = new Thread(Run) { IsBackground = true, Name = "Self-test window" };
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("the test window did not start");
            if (_error != null) throw _error;
        }

        public PxRect Bounds { get; }
        public IntPtr Handle { get; private set; }
        public int Clicks => Volatile.Read(ref _clicks);

        /// <summary>Where the last click landed, on screen.</summary>
        public POINT LastClick
        {
            get
            {
                var packed = Interlocked.Read(ref _lastClick);
                return new POINT { X = Bounds.X + (short)(packed & 0xFFFF), Y = Bounds.Y + (short)((packed >> 16) & 0xFFFF) };
            }
        }

        void Run()
        {
            var instance = GetModuleHandleW(null);
            var brush = CreateSolidBrush((uint)(Red | (Green << 8) | (Blue << 16)));
            try
            {
                _threadId = GetCurrentThreadId();
                _proc = Procedure;
                var wc = new WNDCLASSEXW
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                    hInstance = instance,
                    hbrBackground = brush,
                    lpszClassName = _class,
                };
                if (RegisterClassExW(ref wc) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                Handle = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, _class, "VoiceFlow self-test", WS_POPUP,
                                         Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                if (Handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
            catch (Exception e)
            {
                _error = e;
                _ready.Set();
                return;
            }
            _ready.Set();
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            DestroyWindow(Handle);
            UnregisterClassW(_class, instance);
            DeleteObject(brush);
        }

        IntPtr Procedure(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            // Like a window in the background: a click does not raise it above the bar.
            if (msg == WM_MOUSEACTIVATE) return new IntPtr(MA_NOACTIVATE);
            if (msg == WM_LBUTTONDOWN)
            {
                Interlocked.Exchange(ref _lastClick, lParam.ToInt64() & 0xFFFFFFFF);
                Interlocked.Increment(ref _clicks);
            }
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            if (_threadId != 0) PostThreadMessageW(_threadId, WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>A dictation that heard nothing must take the listening pill off the screen when its toast ends.</summary>
    static string DictationBarResetCheck(string temp)
    {
        var folder = Path.Combine(temp, "reset");
        Directory.CreateDirectory(folder);
        var model = new AppModel(folder, credentialPrefix: $"VoiceFlow-selftest-{Guid.NewGuid():N}/");
        model.UpdatePreferences(p => { p.PlaySounds = false; p.MuteAudioWhileDictating = false; });
        var source = new SimulatedMicrophoneSource();
        using var hub = new SharedMicrophone(source, new SharedMicrophoneOptions { PollInterval = null });
        using var bar = new FlowBarController();
        using var owner = new MessageWindow();
        using var dictation = new DictationController(model, bar, new TextInserter(new ClipboardService(owner.Handle)), new OutputMuter(folder), hub);
        model.SaveKey(SecretKey.Groq, "selftest-not-a-real-key");
        try
        {
            dictation.Begin(SessionKind.Dictation);
            if (!dictation.IsRecording) throw new SkipException("this window can't be dictated into");
            source.Latest!.Emit(new float[(int)AudioMath.SampleRate]);   // a second of digital silence
            dictation.Finish();
            PumpFor(TimeSpan.FromMilliseconds(50));
            Assert(bar.Phase == FlowPhase.Idle, $"the bar was left in {bar.Phase} after a dictation with no speech");
            Assert(bar.ShouldShow, "the toast did not show");
            bar.DismissToast();
            Assert(!bar.ShouldShow, "the bar stayed on screen after its toast");
            return "no-audio dictation returned the bar to rest";
        }
        finally
        {
            model.DeleteKey(SecretKey.Groq);
        }
    }

    /// <summary>The tray icon and menu, built the way the real app builds them, then removed.</summary>
    static string TrayIconCheck()
    {
        var menu = new NativeMenu { new NativeMenuItem("Self-test") };
        var tray = new TrayIcon { Icon = WindowsApp.LoadIcon(), Menu = menu, ToolTipText = "VoiceFlow self-test", IsVisible = true };
        try
        {
            TrayIcon.SetIcons(Application.Current!, new TrayIcons { tray });
            PumpFor(TimeSpan.FromMilliseconds(100));
            Assert(TrayIcon.GetIcons(Application.Current!) is { Count: 1 }, "the tray icon was not registered");
            return "built with its menu and icon, then removed";
        }
        finally
        {
            TrayIcon.SetIcons(Application.Current!, null);
            tray.Dispose();
        }
    }

    /// <summary>Builds every page and window of the real shell off-screen, then closes them.</summary>
    static string UiCheck(string temp)
    {
        var (_, hub, dictation, notetaker, bar, services, owner) = Compose(Path.Combine(temp, "ui"));
        using (hub) using (dictation) using (notetaker) using (bar) using (owner)
        {
            var shell = new ShellViewModel(services);
            var window = new MainWindow { DataContext = shell, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Position = new PixelPoint(-32000, -32000);
            try
            {
                window.Show();
                PumpFor(TimeSpan.FromMilliseconds(150));
                foreach (var page in Enum.GetValues<Page>())
                {
                    shell.Navigate(page);
                    PumpFor(TimeSpan.FromMilliseconds(80));
                    Assert(shell.Current != null, $"page {page} has no view model");
                }
                shell.Navigate(Page.Settings);
                foreach (var section in shell.Settings.Sections)
                {
                    section.IsSelected = true;
                    PumpFor(TimeSpan.FromMilliseconds(40));
                }
            }
            finally
            {
                window.HideOnClose = false;
                window.Close();
                PumpFor(TimeSpan.FromMilliseconds(50));
            }

            var onboardingVm = new OnboardingViewModel(services, () => { });
            var onboarding = new OnboardingWindow { DataContext = onboardingVm, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
            onboarding.Position = new PixelPoint(-32000, -32000);
            try
            {
                onboarding.Show();
                for (int step = 0; step < onboardingVm.Steps.Count; step++)
                {
                    onboardingVm.Step = step;
                    PumpFor(TimeSpan.FromMilliseconds(40));
                }
            }
            finally
            {
                onboarding.Close();
                PumpFor(TimeSpan.FromMilliseconds(50));
            }
            return "main window with every page and settings section, and every onboarding step, built off-screen and closed";
        }
    }
}
