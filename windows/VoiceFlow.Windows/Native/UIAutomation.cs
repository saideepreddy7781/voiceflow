using System.Runtime.InteropServices;

namespace VoiceFlow.Windows.Native;

/// <summary>
/// The UI Automation calls VoiceFlow needs (is the focused field a password field, what is its value, and what sits before the caret),
/// through the UIAutomationCore COM API directly instead of the WPF client assemblies.
///
/// Only the vtable slots up to the methods used are declared; the placeholder methods keep the slot order of
/// UIAutomationClient.h and are never called. --self-test checks the layout against a real password box.
/// </summary>
internal static class UIAutomation
{
    public const int UIA_IsPasswordPropertyId = 30019;
    public const int UIA_ValueValuePropertyId = 30045;
    public const int UIA_IsValuePatternAvailablePropertyId = 30043;

    static readonly Guid CLSID_CUIAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomation
    {
        void CompareElements();             // slot 3
        void CompareRuntimeIds();           // slot 4
        void GetRootElement();              // slot 5
        IUIAutomationElement ElementFromHandle(IntPtr hwnd);   // slot 6
        void ElementFromPoint();            // slot 7
        IUIAutomationElement GetFocusedElement();              // slot 8
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationElement
    {
        void SetFocus();                    // slot 3
        void GetRuntimeId();                // slot 4
        void FindFirst();                   // slot 5
        void FindAll();                     // slot 6
        void FindFirstBuildCache();         // slot 7
        void FindAllBuildCache();           // slot 8
        void BuildUpdatedCache();           // slot 9
        [return: MarshalAs(UnmanagedType.Struct)]
        object? GetCurrentPropertyValue(int propertyId);       // slot 10
        void GetCurrentPropertyValueEx();   // slot 11
        void GetCachedPropertyValue();      // slot 12
        void GetCachedPropertyValueEx();    // slot 13
        void GetCurrentPatternAs();         // slot 14
        void GetCachedPatternAs();          // slot 15
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object? GetCurrentPattern(int patternId);              // slot 16
    }

    [ComImport, Guid("32eba289-3583-42c9-9c59-3b6d9a1e9b6a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationTextPattern
    {
        void RangeFromPoint();              // slot 3
        void RangeFromChild();              // slot 4
        IUIAutomationTextRangeArray GetSelection();            // slot 5
    }

    [ComImport, Guid("ce4ae76a-e717-4c98-81ea-47371d028eb6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationTextRangeArray
    {
        int Length { get; }                 // slot 3
        IUIAutomationTextRange GetElement(int index);          // slot 4
    }

    [ComImport, Guid("a543cc6a-f4ae-494b-8239-c814481187a8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationTextRange
    {
        IUIAutomationTextRange Clone();     // slot 3
        void Compare();                     // slot 4
        void CompareEndpoints();            // slot 5
        void ExpandToEnclosingUnit();       // slot 6
        void FindAttribute();               // slot 7
        void FindText();                    // slot 8
        void GetAttributeValue();           // slot 9
        void GetBoundingRectangles();       // slot 10
        void GetEnclosingElement();         // slot 11
        [return: MarshalAs(UnmanagedType.BStr)]
        string GetText(int maxLength);      // slot 12
        void Move();                        // slot 13
        int MoveEndpointByUnit(int endpoint, int unit, int count);                     // slot 14
        void MoveEndpointByRange(int endpoint, IUIAutomationTextRange range, int targetEndpoint);   // slot 15
    }

    const int UIA_TextPatternId = 10014, TextUnit_Character = 0, Endpoint_Start = 0, Endpoint_End = 1;

    /// <summary>A field reference held for a later read (auto-learn looks at the same field 15 seconds later).</summary>
    internal sealed class FieldHandle : IDisposable
    {
        readonly IUIAutomationElement _element;
        internal FieldHandle(object element) => _element = (IUIAutomationElement)element;

        /// <summary>The field's value, or null for a password field or a field that cannot be read.</summary>
        public string? ReadValue()
        {
            if (_element.GetCurrentPropertyValue(UIA_IsPasswordPropertyId) is true) return null;
            return _element.GetCurrentPropertyValue(UIA_ValueValuePropertyId) as string;
        }

        public void Dispose() => Marshal.ReleaseComObject(_element);
    }

    /// <summary>The focused element, kept for a later read. Null when nothing is focused or it is a password field.</summary>
    public static FieldHandle? CaptureFocused()
    {
        var element = Create().GetFocusedElement();
        if (element == null) return null;
        if (element.GetCurrentPropertyValue(UIA_IsPasswordPropertyId) is true)
        {
            Marshal.ReleaseComObject(element);
            return null;
        }
        return new FieldHandle(element);
    }

    /// <summary>Up to <paramref name="count"/> characters before the caret in the focused field. "" when it cannot be read.</summary>
    public static string TextBeforeCursor(int count) => BeforeCursor(Create().GetFocusedElement(), count);

    /// <summary>Same for a window handle (the self-test uses its own edit box).</summary>
    public static string TextBeforeCursorOf(IntPtr hwnd, int count) => BeforeCursor(Create().ElementFromHandle(hwnd), count);

    static string BeforeCursor(IUIAutomationElement? element, int count)
    {
        if (element == null) return "";
        object? pattern = null;
        IUIAutomationTextRangeArray? selection = null;
        IUIAutomationTextRange? caret = null, range = null;
        try
        {
            // Never read a password field, even to look at the text pattern.
            if (element.GetCurrentPropertyValue(UIA_IsPasswordPropertyId) is true) return "";
            pattern = element.GetCurrentPattern(UIA_TextPatternId);
            if (pattern is not IUIAutomationTextPattern text) return "";
            selection = text.GetSelection();
            if (selection.Length == 0) return "";
            caret = selection.GetElement(0);
            range = caret.Clone();
            range.MoveEndpointByRange(Endpoint_End, caret, Endpoint_Start);
            range.MoveEndpointByUnit(Endpoint_Start, TextUnit_Character, -count);
            return range.GetText(count * 4) ?? "";
        }
        finally
        {
            if (range != null) Marshal.ReleaseComObject(range);
            if (caret != null) Marshal.ReleaseComObject(caret);
            if (selection != null) Marshal.ReleaseComObject(selection);
            if (pattern != null) Marshal.ReleaseComObject(pattern);
            Marshal.ReleaseComObject(element);
        }
    }

    static IUIAutomation Create() =>
        (IUIAutomation)(Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_CUIAutomation, throwOnError: true)!)
                        ?? throw new COMException("CUIAutomation could not be created"));

    /// <summary>Reads properties of the focused element. Call on a worker (MTA) thread, never the UI thread.</summary>
    public static (bool IsPassword, string? Value) Focused(bool readValue) => Describe(Create().GetFocusedElement(), readValue);

    /// <summary>Same for a window handle (the self-test uses its own password box).</summary>
    public static (bool IsPassword, string? Value) FromHandle(IntPtr hwnd, bool readValue) => Describe(Create().ElementFromHandle(hwnd), readValue);

    static (bool, string?) Describe(IUIAutomationElement? element, bool readValue)
    {
        if (element == null) return (false, null);
        try
        {
            var isPassword = element.GetCurrentPropertyValue(UIA_IsPasswordPropertyId) is true;
            string? value = null;
            if (readValue && !isPassword && element.GetCurrentPropertyValue(UIA_IsValuePatternAvailablePropertyId) is true)
                value = element.GetCurrentPropertyValue(UIA_ValueValuePropertyId) as string;
            return (isPassword, value);
        }
        finally
        {
            Marshal.ReleaseComObject(element);
        }
    }
}
