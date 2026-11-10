using System.Text.RegularExpressions;

namespace VoiceFlow.Core;

/// <summary>Finds the project open in a code editor and matches spoken file names against it (for @file tags).</summary>
public static partial class WorkspaceFiles
{
    static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", ".build", "build", "dist", ".next", "Pods", "DerivedData", "venv", ".venv", "__pycache__",
        "target", ".swiftpm", "out", "coverage", ".idea", "bin", "obj",
    };

    static readonly object Gate = new();
    static readonly Dictionary<string, (DateTime At, List<string> Files)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The per-user folder name of an editor under %APPDATA%, or null when it is not VS Code based.</summary>
    public static string? StorageFolder(string processName) => AppClassifier.Normalize(processName) switch
    {
        "cursor.exe" => "Cursor",
        "windsurf.exe" => "Windsurf",
        "code.exe" => "Code",
        "code - insiders.exe" => "Code - Insiders",
        _ => null,
    };

    /// <summary>
    /// Project folder for an editor window: the folder the editor recently opened (its storage.json) whose name
    /// appears in the title. Titles look like "file.cs - project - Cursor".
    /// </summary>
    public static string? ProjectFolder(string storageJson, string windowTitle, Func<string, bool> directoryExists)
    {
        var pieces = windowTitle.Split([" — ", " - "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in FileUri().Matches(storageJson))
        {
            if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) || !uri.IsFile) continue;
            var path = uri.LocalPath.TrimEnd('/', '\\');
            if (path.Length > 0 && pieces.Contains(Path.GetFileName(path)) && directoryExists(path)) return path;
        }
        return null;
    }

    /// <summary>Relative paths of files in the folder. Cached for a minute and capped at 5,000.</summary>
    public static List<string> Files(string folder)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(folder, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(1)) return hit.Files;
        }
        var result = new List<string>();
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System, ReturnSpecialDirectories = false };
        void Walk(string dir, int depth)
        {
            if (depth > 8 || result.Count >= 5_000) return;
            foreach (var file in Directory.EnumerateFiles(dir, "*", options))
            {
                if (result.Count >= 5_000) return;
                result.Add(Path.GetRelativePath(folder, file));
            }
            foreach (var sub in Directory.EnumerateDirectories(dir, "*", options))
                if (!Skipped.Contains(Path.GetFileName(sub))) Walk(sub, depth + 1);
        }
        try { Walk(folder, 0); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
        lock (Gate) Cache[folder] = (DateTime.UtcNow, result);
        return result;
    }

    /// <summary>File names the transcript mentions: "auth controller" finds AuthController.cs.</summary>
    public static List<string> Mentioned(string transcript, IEnumerable<string> files, int limit = 8)
    {
        var spoken = " " + string.Join(' ', Words(transcript)) + " ";
        var hits = new List<(string Name, int Words)>();
        foreach (var path in files)
        {
            var name = Path.GetFileName(path.Replace('\\', '/'));
            var stemWords = Words(SplitIdentifier(Path.GetFileNameWithoutExtension(name)));
            if (stemWords.Count == 0 || string.Concat(stemWords).Length < 4) continue;
            if (spoken.Contains(" " + string.Join(' ', stemWords) + " ", StringComparison.Ordinal)
                || spoken.Contains(" " + string.Concat(stemWords) + " ", StringComparison.Ordinal))
                hits.Add((name, stemWords.Count));
        }
        return hits.OrderByDescending(h => h.Words).Select(h => h.Name).Distinct(StringComparer.Ordinal).Take(limit).ToList();
    }

    static string SplitIdentifier(string s) => IdentifierSeparators().Replace(CamelBoundary().Replace(s, "$1 $2"), " ");

    static List<string> Words(string s) =>
        WordCharacters().Matches(s.ToLowerInvariant()).Select(m => m.Value).ToList();

    [GeneratedRegex(@"file:///?[^""\\\s]+")]
    private static partial Regex FileUri();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex CamelBoundary();

    [GeneratedRegex(@"[_\-.]")]
    private static partial Regex IdentifierSeparators();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordCharacters();
}
