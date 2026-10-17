using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceFlow.Core;

/// <summary>Writes files so a crash or power cut leaves either the old or the new file, never half of one.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text) => WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));

    public static void WriteAllBytes(string path, byte[] data)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(data);
                fs.Flush(true);
            }
            // File.Move with overwrite is MoveFileEx(MOVEFILE_REPLACE_EXISTING) on Windows and rename(2) elsewhere.
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Small JSON-file store. Writes are atomic; unreadable files load as null.</summary>
public sealed class JsonFile<T> where T : class
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string Path { get; }

    public JsonFile(string name, string folder)
    {
        Path = System.IO.Path.Combine(folder, name);
    }

    public T? Load()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(Path), Options);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    public void Save(T value) => AtomicFile.WriteAllText(Path, JsonSerializer.Serialize(value, Options));
}
