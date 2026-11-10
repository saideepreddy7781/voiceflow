using VoiceFlow.Core.Diagnostics;
using VoiceFlow.Core.Output;
using VoiceFlow.Windows.Native;
using static VoiceFlow.Windows.Native.NativeMethods;

namespace VoiceFlow.Windows.Dictation;

internal enum InsertResult
{
    /// <summary>Clipboard + Ctrl+V (or Shift+Insert in terminals), and UI Automation found the text in the field.</summary>
    Pasted,
    /// <summary>Sent, but the field can't be read back. Normal for many apps, so the user is not told.</summary>
    PastedUnverified,
    /// <summary>Sent, and the field reads exactly as it did before: the text did not go in.</summary>
    PasteNotLanded,
    /// <summary>The user switched apps or windows meanwhile; text kept for "Paste last" and on the clipboard.</summary>
    FocusChanged,
    /// <summary>Password field, password manager or credential prompt: nothing typed, nothing copied.</summary>
    Blocked,
    /// <summary>The app runs as administrator, is not responding, or nothing is in front: text left on the clipboard.</summary>
    CopiedOnly,
    /// <summary>The clipboard was locked by another app the whole time, or the paste keys were refused.</summary>
    Failed,
}

/// <summary>How an insertion ended, with the reason code the debug report and history record.</summary>
internal readonly record struct InsertOutcome(InsertResult Result, DeliveryReason Reason, string? Detail);

/// <summary>
/// Puts text where the user is typing: the clipboard is saved, the text goes on it marked to stay out of
/// clipboard history and cloud clipboard, Ctrl+V is sent, and the old clipboard comes back after 500 ms.
///
/// The target is re-read at insertion time. Safety rules (as on macOS): never type into password fields or excluded
/// apps; never type into a different app or window than the one the dictation started in; strip control characters; never
/// insert a newline into a terminal (it would run a command).
/// </summary>
internal sealed class TextInserter(ClipboardService clipboard)
{
    public ClipboardService Clipboard => clipboard;

    /// <summary>Reads the window and field now and applies the rules.</summary>
    async Task<(InsertionDecision Decision, string? Before)> CheckTargetAsync(TypingContext context, bool textIsEmpty)
    {
        var now = FocusInfo.Capture();
        var sameWindow = now.Window != IntPtr.Zero && now.Window == context.Window;
        // Field contents are read only to compare before and after, and never stored or logged. Terminal
        // scrollback is left unread altogether, and so is any field outside the window being typed into.
        var field = await FocusInfo.ReadFieldAsync(now.Window, readValue: sameWindow && !context.IsTerminal);
        var decision = InsertionPolicy.Decide(new InsertionFacts
        {
            Excluded = context.IsExcluded || now.IsExcluded,
            PasswordField = field.State == FieldState.Password,
            ContextProcessId = context.Window == IntPtr.Zero ? 0 : context.ProcessId,
            FrontProcessId = now.Window == IntPtr.Zero ? null : now.ProcessId,
            WindowChanged = context.Window != IntPtr.Zero && now.Window != IntPtr.Zero && now.Window != context.Window,
            Elevated = now.Elevated,
            Hung = field.State == FieldState.Unresponsive,
            TextIsEmpty = textIsEmpty,
        });
        return (decision, field.Value);
    }

    public async Task<InsertResult> InsertAsync(string text, TypingContext context) => (await DeliverAsync(text, context)).Result;

    /// <summary>Inserts text and says exactly how it ended.</summary>
    public async Task<InsertOutcome> DeliverAsync(string text, TypingContext context, bool replaceAll = false)
    {
        var clean = OutputSafety.Sanitize(text, context.IsTerminal);
        var (decision, before) = await CheckTargetAsync(context, clean.Length == 0);
        if (decision.Plan == InsertionPlan.Block) return new(InsertResult.Blocked, decision.Reason, decision.Detail);
        if (decision.Plan != InsertionPlan.Paste)
        {
            if (clean.Length > 0 && !await CopyAsync(clean)) return new(InsertResult.Failed, DeliveryReason.Blocked, DeliveryDetail.ClipboardLocked);
            return new(decision.Plan == InsertionPlan.CopyFocusChanged ? InsertResult.FocusChanged : InsertResult.CopiedOnly, decision.Reason, decision.Detail);
        }
        if (replaceAll)
        {
            await InputSender.PrepareForChordAsync();
            InputSender.CtrlA();
            await Task.Delay(60);
        }
        return await PasteCheckedAsync(clean, context.IsTerminal, before);
    }

    /// <summary>Leaves text on the clipboard for the user to paste (a deliberate copy: kept in local history, never uploaded).</summary>
    public async Task<bool> CopyAsync(string text) =>
        await clipboard.SetTextAsync(OutputSafety.ToClipboardText(text), transient: false, noCloud: true) != null;

    /// <summary>Pastes <paramref name="text"/> with Ctrl+V (Shift+Insert in terminals), then restores the clipboard.</summary>
    public async Task<bool> PasteAsync(string text, bool terminal) => (await PasteCheckedAsync(text, terminal, null, verify: false)).Result != InsertResult.Failed;

    /// <param name="before">The field's text just before the paste, when it could be read.</param>
    /// <param name="verify">False to skip reading the field back.</param>
    async Task<InsertOutcome> PasteCheckedAsync(string text, bool terminal, string? before, bool verify = true)
    {
        var saved = await clipboard.SaveAsync();
        var sequence = await clipboard.SetTextAsync(OutputSafety.ToClipboardText(text), transient: true);
        if (sequence == null) return new(InsertResult.Failed, DeliveryReason.Blocked, DeliveryDetail.ClipboardLocked);
        await InputSender.PrepareForChordAsync();
        var sent = terminal ? InputSender.ShiftInsert() : InputSender.CtrlV();
        if (!sent) Logger.Warn("SendInput was blocked (UIPI or secure desktop)");
        // Verification runs inside the wait the clipboard needs anyway, so it adds no delay.
        var check = sent && verify && !terminal ? VerifyAsync(text, before) : Task.FromResult(PasteCheck.Unknown);
        // Slow apps read the clipboard late; restoring too early would paste the old contents.
        await Task.WhenAll(check, Task.Delay(500));
        // Restore only if nothing else used the clipboard meanwhile.
        if (saved != null && ClipboardService.SequenceNumber == sequence) await clipboard.RestoreAsync(saved);
        if (!sent) return new(InsertResult.Failed, DeliveryReason.Blocked, DeliveryDetail.InputRefused);
        var (reason, detail) = InsertionPolicy.ReasonFor(await check);
        var result = reason switch
        {
            DeliveryReason.PastedVerified => InsertResult.Pasted,
            DeliveryReason.PasteNotLanded => InsertResult.PasteNotLanded,
            _ => InsertResult.PastedUnverified,
        };
        return new(result, reason, detail);
    }

    /// <summary>Reads the field back until the text shows up or the time runs out. Apps apply a paste asynchronously.</summary>
    static async Task<PasteCheck> VerifyAsync(string text, string? before)
    {
        var last = PasteCheck.Unknown;
        var until = DateTime.UtcNow.AddMilliseconds(450);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(70);
            var after = await FocusInfo.ReadFieldAsync(IntPtr.Zero, readValue: true, timeoutMs: 200);
            last = InsertionPolicy.Verify(before, after.Value, text);
            if (last == PasteCheck.Verified) break;
        }
        return last;
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
    public async Task<InsertResult> ReplaceAllAsync(string text, TypingContext context) => (await DeliverAsync(text, context, replaceAll: true)).Result;

    /// <summary>Brings a window back to the front (Paste last from the tray menu).</summary>
    public static async Task<bool> RefocusAsync(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window)) return false;
        SetForegroundWindow(window);
        for (int i = 0; i < 10 && GetForegroundWindow() != window; i++) await Task.Delay(20);
        return GetForegroundWindow() == window;
    }
}
