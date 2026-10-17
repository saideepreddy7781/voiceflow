using VoiceFlow.Core.Output;
using VoiceFlow.Windows.Native;
using static VoiceFlow.Windows.Native.NativeMethods;

namespace VoiceFlow.Windows.Dictation;

internal enum InsertResult
{
    /// <summary>Clipboard + Ctrl+V (or Shift+Insert in terminals).</summary>
    Pasted,
    /// <summary>The user switched windows meanwhile; text kept for "Paste last" and on the clipboard.</summary>
    FocusChanged,
    /// <summary>Password field, password manager or credential prompt: nothing typed, nothing copied.</summary>
    Blocked,
    /// <summary>The app runs as administrator or is not responding: text left on the clipboard instead.</summary>
    CopiedOnly,
    /// <summary>The clipboard was locked by another app the whole time.</summary>
    Failed,
}

/// <summary>
/// Puts text where the user is typing: the clipboard is saved, the text goes on it marked to stay out of
/// clipboard history and cloud clipboard, Ctrl+V is sent, and the old clipboard comes back after 500 ms.
///
/// Safety rules (as on macOS): never type into password fields or excluded apps; never type into a different
/// window than the one the dictation started in; strip control characters; never insert a newline into a
/// terminal (it would run a command).
/// </summary>
internal sealed class TextInserter(ClipboardService clipboard)
{
    public ClipboardService Clipboard => clipboard;

    /// <summary>Checks that the focused field may receive text now. Returns null when it may, or the result to report.</summary>
    public async Task<InsertResult?> CheckTargetAsync(TypingContext context)
    {
        var now = FocusInfo.Capture();
        if (context.IsExcluded || now.IsExcluded) return InsertResult.Blocked;
        if (context.Window != IntPtr.Zero && now.Window != context.Window) return InsertResult.FocusChanged;
        var state = await FocusInfo.FocusedFieldStateAsync(now.Window);
        if (state == FieldState.Password) return InsertResult.Blocked;
        // A hung app would receive the paste only after the old clipboard is back: paste the wrong thing later.
        if (state == FieldState.Unresponsive || now.Elevated) return InsertResult.CopiedOnly;
        return null;
    }

    public async Task<InsertResult> InsertAsync(string text, TypingContext context)
    {
        var clean = OutputSafety.Sanitize(text, context.IsTerminal);
        if (clean.Length == 0) return InsertResult.Failed;
        if (await CheckTargetAsync(context) is { } problem)
        {
            if (problem is InsertResult.FocusChanged or InsertResult.CopiedOnly) await CopyAsync(clean);
            return problem;
        }
        return await PasteAsync(clean, context.IsTerminal) ? InsertResult.Pasted : InsertResult.Failed;
    }

    /// <summary>Leaves text on the clipboard for the user to paste (a deliberate copy: kept in local history, never uploaded).</summary>
    public async Task<bool> CopyAsync(string text) =>
        await clipboard.SetTextAsync(OutputSafety.ToClipboardText(text), transient: false, noCloud: true) != null;

    /// <summary>Pastes <paramref name="text"/> with Ctrl+V (Shift+Insert in terminals), then restores the clipboard.</summary>
    public async Task<bool> PasteAsync(string text, bool terminal)
    {
        var saved = await clipboard.SaveAsync();
        var sequence = await clipboard.SetTextAsync(OutputSafety.ToClipboardText(text), transient: true);
        if (sequence == null) return false;
        await InputSender.PrepareForChordAsync();
        var sent = terminal ? InputSender.ShiftInsert() : InputSender.CtrlV();
        if (!sent) Logger.Warn("SendInput was blocked (UIPI or secure desktop)");
        // Slow apps read the clipboard late; restoring too early would paste the old contents.
        await Task.Delay(500);
        // Restore only if nothing else used the clipboard meanwhile.
        if (saved != null && ClipboardService.SequenceNumber == sequence) await clipboard.RestoreAsync(saved);
        return sent;
    }

    /// <summary>
    /// Copies the selection with Ctrl+C (Command Mode), then restores the clipboard. Null when nothing was
    /// selected or the app did not copy.
    /// </summary>
    public async Task<string?> CopySelectionAsync()
    {
        var saved = await clipboard.SaveAsync();
        var before = ClipboardService.SequenceNumber;
        await InputSender.PrepareForChordAsync();
        if (!InputSender.CtrlC()) return null;
        for (int i = 0; i < 12 && ClipboardService.SequenceNumber == before; i++) await Task.Delay(25);
        var after = ClipboardService.SequenceNumber;
        if (after == before) return null;
        var copied = await clipboard.GetTextAsync();
        if (saved != null && ClipboardService.SequenceNumber == after) await clipboard.RestoreAsync(saved);
        return string.IsNullOrEmpty(copied) ? null : copied;
    }

    /// <summary>Selects the whole field and pastes over it (Command Mode with nothing selected).</summary>
    public async Task<InsertResult> ReplaceAllAsync(string text, TypingContext context)
    {
        var clean = OutputSafety.Sanitize(text, context.IsTerminal);
        if (await CheckTargetAsync(context) is { } problem)
        {
            if (problem is InsertResult.FocusChanged or InsertResult.CopiedOnly) await CopyAsync(clean);
            return problem;
        }
        await InputSender.PrepareForChordAsync();
        InputSender.CtrlA();
        await Task.Delay(60);
        return await PasteAsync(clean, context.IsTerminal) ? InsertResult.Pasted : InsertResult.Failed;
    }

    /// <summary>Brings a window back to the front (Paste last from the tray menu).</summary>
    public static async Task<bool> RefocusAsync(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window)) return false;
        SetForegroundWindow(window);
        for (int i = 0; i < 10 && GetForegroundWindow() != window; i++) await Task.Delay(20);
        return GetForegroundWindow() == window;
    }
}
