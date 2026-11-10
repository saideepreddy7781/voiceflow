using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using VoiceFlow.Core;
using VoiceFlow.Core.Output;
using static VoiceFlow.Windows.Native.NativeMethods;

namespace VoiceFlow.Windows.Native;

/// <summary>The window the user is typing into, captured when a dictation starts.</summary>
internal sealed record TypingContext(IntPtr Window, uint ProcessId, string ProcessName, string AppName, string WindowTitle, bool Elevated)
{
    public static readonly TypingContext None = new(IntPtr.Zero, 0, "", "", "", false);

    public bool IsTerminal => OutputSafety.IsTerminal(ProcessName);
    public bool IsExcluded => OutputSafety.IsExcluded(ProcessName);
}

internal enum FieldState { Normal, Password, Unresponsive, Unknown }

/// <summary>The focused element at one moment. <paramref name="Value"/> is null when the field cannot be read.</summary>
internal readonly record struct FieldReading(FieldState State, string? Value);

internal static class FocusInfo
{
    static readonly uint OwnPid = (uint)Environment.ProcessId;

    public static TypingContext Capture() => Describe(GetForegroundWindow());

    public static TypingContext Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return TypingContext.None;
        GetWindowThreadProcessId(hwnd, out var pid);
        var path = ProcessPath(pid);
        var name = Path.GetFileName(path ?? "");
        // UWP apps are hosted by ApplicationFrameHost; the real app owns a child window.
        if (name.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) && HostedApp(hwnd, pid) is { } child)
        {
            pid = child;
            path = ProcessPath(pid);
            name = Path.GetFileName(path ?? "");
        }
        return new TypingContext(hwnd, pid, name, FriendlyName(path, name), Title(hwnd), IsHigherIntegrity(pid));
    }

    public static bool IsOwnWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == OwnPid;
    }

    static uint? HostedApp(IntPtr frame, uint framePid)
    {
        uint found = 0;
        EnumChildWindows(frame, (child, _) =>
        {
            GetWindowThreadProcessId(child, out var p);
            if (p != framePid) { found = p; return false; }
            return true;
        }, IntPtr.Zero);
        return found == 0 ? null : found;
    }

    public static string Title(IntPtr hwnd)
    {
        var length = GetWindowTextLengthW(hwnd);
        if (length <= 0) return "";
        var sb = new StringBuilder(Math.Min(length, 1024) + 1);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>The process's file name (for example zoom.exe), or "" when it cannot be read.</summary>
    public static string ProcessFileName(uint pid) => Path.GetFileName(ProcessPath(pid) ?? "");

    static string? ProcessPath(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageNameW(handle, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    static string FriendlyName(string? path, string exe)
    {
        if (path != null)
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description) && description.Length <= 40) return description.Trim();
            }
            catch (FileNotFoundException) { }
            catch (ArgumentException) { }
        }
        return Path.GetFileNameWithoutExtension(exe);
    }

    /// <summary>
    /// True when the target runs at a higher integrity level than VoiceFlow (an app run as administrator).
    /// Windows silently drops synthetic keys sent to such windows (UIPI), so VoiceFlow copies instead.
    /// A token that cannot be read is treated as higher.
    /// </summary>
    static bool IsHigherIntegrity(uint pid)
    {
        if (pid == 0) return false;
        var own = IntegrityLevel(GetCurrentProcess());
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return true;
        try
        {
            var level = IntegrityLevel(handle);
            return level == null || (own != null && level > own);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    static int? IntegrityLevel(IntPtr process)
    {
        if (!OpenProcessToken(process, TOKEN_QUERY, out var token)) return null;
        var buffer = IntPtr.Zero;
        try
        {
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var needed);
            if (needed == 0) return null;
            buffer = Marshal.AllocHGlobal((int)needed);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _)) return null;
            var sid = Marshal.ReadIntPtr(buffer);   // TOKEN_MANDATORY_LABEL.Label.Sid
            var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            CloseHandle(token);
        }
    }

    /// <summary>
    /// Asks UI Automation about the focused element, on a worker thread with a timeout so a hung app cannot
    /// block VoiceFlow. Unresponsive means the app did not answer in time (it may be hung; don't paste).
    /// </summary>
    public static async Task<FieldState> FocusedFieldStateAsync(IntPtr expectedWindow, int timeoutMs = 400)
    {
        if (expectedWindow != IntPtr.Zero && IsHungAppWindow(expectedWindow)) return FieldState.Unresponsive;
        var work = Task.Run(() =>
        {
            try
            {
                return UIAutomation.Focused(readValue: false).IsPassword ? FieldState.Password : FieldState.Normal;
            }
            // UI Automation HRESULTs surface as many exception types (UIA_E_TIMEOUT is TimeoutException,
            // E_ACCESSDENIED is UnauthorizedAccessException…). None of them may fail the dictation.
            catch (Exception e) when (e is not OutOfMemoryException) { return FieldState.Unknown; }
        });
        var finished = await Task.WhenAny(work, Task.Delay(timeoutMs));
        return finished == work ? work.Result : FieldState.Unresponsive;
    }

    /// <summary>
    /// The focused element now, for the moment of insertion. Unresponsive means the window is hung. A slow UI
    /// Automation answer from a window that is still running reads as Unknown, so it doesn't block a paste.
    /// </summary>
    /// <param name="expectedWindow">Checked for a hung message loop; zero to skip the check.</param>
    /// <param name="readValue">False leaves the field's text unread.</param>
    public static async Task<FieldReading> ReadFieldAsync(IntPtr expectedWindow, bool readValue, int timeoutMs = 400)
    {
        if (expectedWindow != IntPtr.Zero && IsHungAppWindow(expectedWindow)) return new FieldReading(FieldState.Unresponsive, null);
        var work = Task.Run(() =>
        {
            try
            {
                var (isPassword, value) = UIAutomation.Focused(readValue);
                return new FieldReading(isPassword ? FieldState.Password : FieldState.Normal, value);
            }
            catch (Exception e) when (e is not OutOfMemoryException) { return new FieldReading(FieldState.Unknown, null); }
        });
        var finished = await Task.WhenAny(work, Task.Delay(timeoutMs));
        if (finished == work) return work.Result;
        // The worker may still be waiting on the app; a late result has nothing to report to.
        _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return new FieldReading(expectedWindow != IntPtr.Zero && IsHungAppWindow(expectedWindow) ? FieldState.Unresponsive : FieldState.Unknown, null);
    }

    /// <summary>
    /// Text just before the caret in the focused field, for cleanup and spacing. "" when the field has no text
    /// pattern, is a password field, or does not answer in time.
    /// </summary>
    public static async Task<string> BeforeCursorAsync(int timeoutMs = 250)
    {
        var work = Task.Run(() =>
        {
            try { return UIAutomation.TextBeforeCursor(120); }
            catch (Exception e) when (e is not OutOfMemoryException) { return ""; }
        });
        var finished = await Task.WhenAny(work, Task.Delay(timeoutMs));
        if (finished == work) return work.Result;
        _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return "";
    }

    /// <summary>Holds on to the focused field so auto-learn can read that same field again later.</summary>
    public static async Task<UIAutomation.FieldHandle?> CaptureFieldAsync(int timeoutMs = 400)
    {
        var work = Task.Run(() =>
        {
            try { return UIAutomation.CaptureFocused(); }
            catch (Exception e) when (e is not OutOfMemoryException) { return null; }
        });
        var finished = await Task.WhenAny(work, Task.Delay(timeoutMs));
        if (finished == work) return work.Result;
        _ = work.ContinueWith(t => t.Result?.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
        return null;
    }

    /// <summary>Reads a held field. Null when it is gone or does not answer in time; never throws.</summary>
    public static async Task<string?> ReadHeldFieldAsync(UIAutomation.FieldHandle field, int timeoutMs = 800)
    {
        var work = Task.Run(() =>
        {
            try { return field.ReadValue(); }
            catch (Exception e) when (e is not OutOfMemoryException) { return null; }
        });
        var finished = await Task.WhenAny(work, Task.Delay(timeoutMs));
        return finished == work ? work.Result : null;
    }

    /// <summary>The focused field's whole value (Command Mode context), or "" when it cannot be read in time.</summary>
    public static async Task<string> FocusedFieldValueAsync(int timeoutMs = 500)
    {
        var work = Task.Run(() =>
        {
            try
            {
                var (isPassword, value) = UIAutomation.Focused(readValue: true);
                return isPassword ? "" : value ?? "";
            }
            catch (Exception e) when (e is not OutOfMemoryException) { return ""; }
        });
        var finished = await Task.WhenAny(work, Task.Delay(timeoutMs));
        return finished == work ? work.Result : "";
    }
}
