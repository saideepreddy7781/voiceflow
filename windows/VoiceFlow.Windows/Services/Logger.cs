using System.Text;

namespace VoiceFlow.Windows;

/// <summary>
/// A small rolling log in %LOCALAPPDATA%\VoiceFlow\logs. Never pass API keys or dictated text at Info level:
/// transcripts are private, so the app logs lengths and outcomes, and only Debug (off by default) may say more.
/// </summary>
internal static class Logger
{
    static readonly object Gate = new();
    const long MaxBytes = 1_000_000;
    public static bool DebugEnabled { get; set; }
    public static string? FilePath { get; private set; }

    public static void Initialize(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            FilePath = Path.Combine(folder, "voiceflow.log");
        }
        catch (IOException) { FilePath = null; }
        catch (UnauthorizedAccessException) { FilePath = null; }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Debug(string message) { if (DebugEnabled) Write("DEBUG", message); }

    public static void Error(string message, Exception? e = null) =>
        Write("ERROR", e == null ? message : $"{message}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");

    static void Write(string level, string message)
    {
        var path = FilePath;
        if (path == null) return;
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}\n";
        lock (Gate)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    var old = path + ".1";
                    File.Move(path, old, overwrite: true);
                }
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
