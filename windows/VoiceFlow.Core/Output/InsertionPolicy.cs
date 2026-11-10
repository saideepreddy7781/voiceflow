using VoiceFlow.Core.Diagnostics;

namespace VoiceFlow.Core.Output;

/// <summary>What is true at the moment text is about to be typed. Read fresh, never carried over from dictation start.</summary>
public sealed record InsertionFacts
{
    /// <summary>A password manager or sign-in prompt was in front at the start, or is now.</summary>
    public bool Excluded { get; init; }
    /// <summary>The field focused right now is a password field.</summary>
    public bool PasswordField { get; init; }
    /// <summary>The app the dictation started in. 0 when it was never known.</summary>
    public uint ContextProcessId { get; init; }
    /// <summary>The app in front now, if any.</summary>
    public uint? FrontProcessId { get; init; }
    /// <summary>True only when both the old and new foreground windows are known and they differ.</summary>
    public bool WindowChanged { get; init; }
    /// <summary>The target runs as administrator, so synthetic keys are dropped.</summary>
    public bool Elevated { get; init; }
    /// <summary>The target window is not processing messages.</summary>
    public bool Hung { get; init; }
    public bool TextIsEmpty { get; init; }
}

public enum InsertionPlan { Block, CopyFocusChanged, CopyNoField, Paste }

public sealed record InsertionDecision(InsertionPlan Plan, DeliveryReason Reason, string? Detail);

/// <summary>What reading the focused field back says about a paste.</summary>
public enum PasteCheck
{
    /// <summary>The text is in the field.</summary>
    Verified,
    /// <summary>The field is readable and did not change.</summary>
    NotLanded,
    /// <summary>The field can't be read, or changed in a way that says nothing either way.</summary>
    Unknown,
}

/// <summary>The rules for typing into whatever is focused. The same order as macOS, testable off Windows.</summary>
public static class InsertionPolicy
{
    public static InsertionDecision Decide(InsertionFacts f)
    {
        // Safety rules come first and nothing below can override them.
        if (f.Excluded) return new(InsertionPlan.Block, DeliveryReason.Blocked, DeliveryDetail.ExcludedApp);
        if (f.PasswordField) return new(InsertionPlan.Block, DeliveryReason.Blocked, DeliveryDetail.PasswordField);
        if (f.TextIsEmpty) return new(InsertionPlan.CopyNoField, DeliveryReason.CopiedNoField, DeliveryDetail.EmptyText);
        if (f.ContextProcessId != 0 && f.FrontProcessId != f.ContextProcessId)
            return new(InsertionPlan.CopyFocusChanged, DeliveryReason.CopiedFocusChanged, null);
        if (f.FrontProcessId == null) return new(InsertionPlan.CopyNoField, DeliveryReason.CopiedNoField, DeliveryDetail.NoWindow);
        if (f.WindowChanged) return new(InsertionPlan.CopyFocusChanged, DeliveryReason.CopiedFocusChanged, null);
        if (f.Elevated) return new(InsertionPlan.CopyNoField, DeliveryReason.CopiedNoField, DeliveryDetail.Elevated);
        // A hung app would process the paste after the old clipboard is back, and paste the wrong thing later.
        if (f.Hung) return new(InsertionPlan.CopyNoField, DeliveryReason.CopiedNoField, DeliveryDetail.AppNotResponding);
        // Same app, nothing protected: paste even when UI Automation can't name the field. Electron and web apps
        // often answer with nothing for a field that takes a paste perfectly well.
        return new(InsertionPlan.Paste, DeliveryReason.PastedUnverified, DeliveryDetail.FieldUnreadable);
    }

    /// <summary>Whether a paste is visible in the field. Whitespace is ignored because editors reflow what they receive.</summary>
    /// <param name="before">The field's text before the paste, or null when it could not be read.</param>
    /// <param name="after">The field's text after the paste, or null when it could not be read.</param>
    public static PasteCheck Verify(string? before, string? after, string inserted)
    {
        // Monaco and many web editors report an empty value whatever they hold: that is not evidence.
        if (string.IsNullOrEmpty(after)) return PasteCheck.Unknown;
        if (before != null && before == after) return PasteCheck.NotLanded;
        var text = Squeezed(inserted);
        return Squeezed(after).Contains(text.Length > 60 ? text[..60] : text, StringComparison.Ordinal) ? PasteCheck.Verified : PasteCheck.Unknown;
    }

    /// <summary>
    /// Whether the text just before the caret ends with what was pasted, for editors that expose text but no value
    /// (Word and web editors). Reads a short tail of the document, so it can confirm a paste and never reject one.
    /// </summary>
    /// <param name="beforeCaret">Text before the caret after the paste, or empty when it could not be read.</param>
    public static PasteCheck VerifyCaret(string beforeCaret, string inserted)
    {
        var text = Squeezed(inserted);
        if (text.Length == 0 || beforeCaret.Length == 0) return PasteCheck.Unknown;
        var tail = text.Length > 40 ? text[^40..] : text;
        return Squeezed(beforeCaret).EndsWith(tail, StringComparison.Ordinal) ? PasteCheck.Verified : PasteCheck.Unknown;
    }

    public static (DeliveryReason Reason, string? Detail) ReasonFor(PasteCheck check) => check switch
    {
        PasteCheck.Verified => (DeliveryReason.PastedVerified, null),
        PasteCheck.NotLanded => (DeliveryReason.PasteNotLanded, DeliveryDetail.ValueUnchanged),
        _ => (DeliveryReason.PastedUnverified, DeliveryDetail.FieldUnreadable),
    };

    static string Squeezed(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
}
