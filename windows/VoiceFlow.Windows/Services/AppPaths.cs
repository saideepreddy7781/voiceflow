using Microsoft.Win32;

namespace VoiceFlow.Windows;

/// <summary>Where VoiceFlow keeps its data: settings and library in %APPDATA%\VoiceFlow, logs in %LOCALAPPDATA%\VoiceFlow\logs.</summary>
internal static class AppPaths
{
    public static string DataFolder
    {
        get
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceFlow");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string LogFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceFlow", "logs");

    public static string ExePath => Environment.ProcessPath ?? "";
}

/// <summary>"Start with Windows": a value under HKCU\Software\Microsoft\Windows\CurrentVersion\Run. Off by default.</summary>
internal static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    /// <summary>
    /// Task Manager and Settings → Apps → Startup do not delete the Run value: they write a binary value here whose
    /// first byte has the low bit set when the entry is disabled.
    /// </summary>
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string ValueName = "VoiceFlow";

    public static string Command => $"\"{AppPaths.ExePath}\" --background";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is not string { Length: > 0 }) return false;
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } flags || (flags[0] & 1) == 0;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName, Command, RegistryValueKind.String);
            // Turning it on in VoiceFlow also clears a "disabled" mark left by Task Manager.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        else if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
