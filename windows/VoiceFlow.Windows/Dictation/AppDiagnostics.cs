using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using VoiceFlow.Core.Audio;
using VoiceFlow.Core.Diagnostics;
using VoiceFlow.Windows.Audio;

namespace VoiceFlow.Windows.Dictation;

/// <summary>The delivery log and the debug report for one data folder.</summary>
internal static class AppDiagnostics
{
    static readonly ConcurrentDictionary<string, DeliveryLog> Logs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The log for this app's data folder. "Never store my data" keeps it in memory only.</summary>
    public static DeliveryLog LogFor(AppModel model) => Logs.GetOrAdd(model.DataFolder, folder =>
    {
        var log = new DeliveryLog(folder, persists: model.Preferences.HistoryRetentionDays != 0);
        model.PreferencesChanged += (_, now) => log.Persists = now.HistoryRetentionDays != 0;
        return log;
    });

    /// <summary>Text for a bug report. Contains no transcripts, window titles, keys or meeting content.</summary>
    public static string BuildDebugReport(AppModel model, SharedMicrophone? microphone = null) =>
        DebugReport.Build(new DebugReportInput(
            typeof(AppDiagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            AudioDevices.Inputs().Select(d => d.Name).ToList(),
            AudioDevices.Outputs().Select(d => d.Name).ToList(),
            (microphone ?? MicrophoneHub.Shared).Diagnostics,
            LogFor(model).Recent(),
            DateTimeOffset.UtcNow));
}
