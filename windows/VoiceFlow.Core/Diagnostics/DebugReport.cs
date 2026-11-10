using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VoiceFlow.Core.Audio;

namespace VoiceFlow.Core.Diagnostics;

/// <summary>Everything a debug report may contain. Nothing here can hold transcript text, window titles or meeting content.</summary>
public sealed record DebugReportInput(
    string AppVersion,
    string OsVersion,
    string Architecture,
    string RuntimeVersion,
    IReadOnlyList<string> InputDevices,
    IReadOnlyList<string> OutputDevices,
    MicDiagnostics Microphone,
    IReadOnlyList<DeliveryLogEntry> Deliveries,
    DateTimeOffset Generated);

/// <summary>Plain text for a bug report. Safe to paste into a public GitHub issue.</summary>
public static class DebugReport
{
    /// <summary>Device names and versions come from the OS; these strip anything key-shaped that ended up in one.</summary>
    static readonly Regex KeyShaped = new(@"gsk_[A-Za-z0-9]{8,}|sk-[A-Za-z0-9-]{8,}|AIza[0-9A-Za-z_-]{8,}", RegexOptions.CultureInvariant);

    public static string Build(DebugReportInput input)
    {
        var sb = new StringBuilder();
        void Line(string text) => sb.Append(text).Append('\n');

        Line("VoiceFlow debug report (no transcripts, window titles or keys)");
        Line($"Generated: {input.Generated.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC");
        Line($"App: {Clean(input.AppVersion)}");
        Line($"OS: {Clean(input.OsVersion)} ({Clean(input.Architecture)})");
        Line($"Runtime: {Clean(input.RuntimeVersion)}");
        Line("");
        Line("Microphones:");
        foreach (var name in input.InputDevices) Line($"- {DeviceName(name)}");
        if (input.InputDevices.Count == 0) Line("- none found");
        Line("Output devices:");
        foreach (var name in input.OutputDevices) Line($"- {DeviceName(name)}");
        if (input.OutputDevices.Count == 0) Line("- none found");
        Line("");
        var mic = input.Microphone;
        Line($"Microphone in use: {(mic.CurrentDevice.Length == 0 ? "none" : DeviceName(mic.CurrentDevice))}");
        Line($"Restarts: {mic.Restarts}, device switches: {mic.DeviceSwitches}, stalls: {mic.Stalls}, dead signals: {mic.DeadSignals}, failed starts: {mic.FailedStarts}");
        Line($"Last failure: {(mic.LastFailure == null ? "none" : Clean(mic.LastFailure))}");
        Line("");

        var entries = input.Deliveries;
        Line($"Recent deliveries ({entries.Count}):");
        foreach (var group in entries.GroupBy(e => e.Reason).OrderByDescending(g => g.Count()))
            Line($"- {group.Key.WireName()}: {group.Count()}");
        if (entries.Count > 0)
        {
            var latencies = entries.Where(e => e.LatencyMs > 0).Select(e => e.LatencyMs).OrderBy(v => v).ToArray();
            if (latencies.Length > 0)
                Line($"Latency: median {Percentile(latencies, 0.5)} ms, 90th percentile {Percentile(latencies, 0.9)} ms");
        }
        Line("");
        Line("Newest first (time UTC, session, reason, detail, audio ms, latency ms):");
        foreach (var e in entries)
        {
            var when = e.Date.ToUniversalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            // Allow() again: entries can reach here from a file on disk.
            var detail = DeliveryDetail.Allow(e.Detail) ?? "-";
            Line(FormattableString.Invariant($"{when}  {e.Session,-9}  {e.Reason.WireName(),-20}  {detail,-18}  {e.AudioMs,8}  {e.LatencyMs,7}"));
        }
        return sb.ToString();
    }

    /// <summary>"Priya's AirPods Pro" becomes "AirPods Pro": the report gets pasted into public issues.</summary>
    internal static string DeviceName(string name)
    {
        var clean = name;
        foreach (var marker in new[] { "\u2019s ", "'s " })
        {
            var at = clean.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0) clean = clean[(at + marker.Length)..];
        }
        clean = Clean(clean);
        return clean.Length > 80 ? clean[..80] : clean;
    }

    static string Clean(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value) sb.Append(char.IsControl(ch) ? ' ' : ch);
        var trimmed = sb.ToString().Trim();
        if (trimmed.Length > 200) trimmed = trimmed[..200];
        return KeyShaped.Replace(trimmed, "[removed]");
    }

    static int Percentile(int[] sorted, double p) => sorted[Math.Min(sorted.Length - 1, (int)Math.Floor(sorted.Length * p))];
}
