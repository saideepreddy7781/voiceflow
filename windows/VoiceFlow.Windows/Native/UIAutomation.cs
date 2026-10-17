using System.Runtime.InteropServices;

namespace VoiceFlow.Windows.Native;

/// <summary>
/// The two UI Automation calls VoiceFlow needs (is the focused field a password field, and what is its value),
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
