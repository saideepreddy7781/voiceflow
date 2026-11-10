using System.Net;
using System.Text.Json;

namespace VoiceFlow.Core.Updates;

/// <summary>Compares app versions: 1.2.3, v1.2.3, 1.2 (same as 1.2.0), 1.2.3-beta.1. Build metadata after + is ignored.</summary>
public static class AppVersion
{
    sealed record Parsed(int[] Numbers, string[] Prerelease);

    static Parsed? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        if (s[0] is 'v' or 'V') s = s[1..];
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        string[] prerelease = [];
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = s[(dash + 1)..].Split('.');
            s = s[..dash];
        }
        var parts = s.Split('.');
        if (parts.Length is 0 or > 4) return null;
        var numbers = new int[4];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out numbers[i])) return null;
        }
        return new Parsed(numbers, prerelease);
    }

    public static bool IsValid(string? text) => Parse(text) != null;

    /// <summary>True when <paramref name="candidate"/> is a later version. Anything unreadable is never newer.</summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        var a = Parse(candidate);
        var b = Parse(current);
        if (a == null || b == null) return false;
        for (int i = 0; i < a.Numbers.Length; i++)
        {
            if (a.Numbers[i] != b.Numbers[i]) return a.Numbers[i] > b.Numbers[i];
        }
        return ComparePrerelease(a.Prerelease, b.Prerelease) > 0;
    }

    /// <summary>The SemVer order: a release sorts after its prereleases; numeric identifiers sort before text.</summary>
    static int ComparePrerelease(string[] a, string[] b)
    {
        if (a.Length == 0 || b.Length == 0) return a.Length == b.Length ? 0 : a.Length == 0 ? 1 : -1;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNumeric = int.TryParse(a[i], out var x);
            var bNumeric = int.TryParse(b[i], out var y);
            int order = aNumeric && bNumeric ? x.CompareTo(y)
                : aNumeric ? -1
                : bNumeric ? 1
                : string.CompareOrdinal(a[i], b[i]);
            if (order != 0) return order;
        }
        return a.Length.CompareTo(b.Length);
    }

    /// <summary>The version a user reads: no leading v and no +commit suffix.</summary>
    public static string Display(string version)
    {
        var s = version.Trim();
        if (s.Length > 0 && s[0] is 'v' or 'V') s = s[1..];
        var plus = s.IndexOf('+');
        return plus >= 0 ? s[..plus] : s;
    }
}

public sealed record ReleaseInfo(string Version, string Url);

public enum UpdateCheckStatus { UpToDate, Available, Failed }

public sealed record UpdateCheckResult(UpdateCheckStatus Status, ReleaseInfo? Release = null);

/// <summary>
/// Asks GitHub for the latest published release. It sends nothing about the user and downloads nothing:
/// the app only shows a notice with a link to the release page.
/// </summary>
public sealed class UpdateChecker(HttpClient? client = null)
{
    public const string RepositoryUrl = "https://github.com/saideepreddy7781/voiceflow";
    public const string ReleasesUrl = RepositoryUrl + "/releases";
    public const string LatestReleaseApi = "https://api.github.com/repos/saideepreddy7781/voiceflow/releases/latest";

    readonly HttpClient _client = client ?? GroqHttp.Shared;

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancel = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            using var response = await _client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            // A repository with no published release answers 404: nothing to offer, and nothing is wrong.
            if (response.StatusCode == HttpStatusCode.NotFound) return new UpdateCheckResult(UpdateCheckStatus.UpToDate);
            if (!response.IsSuccessStatusCode) return new UpdateCheckResult(UpdateCheckStatus.Failed);
            var body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            return Evaluate(body, currentVersion);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed);
        }
    }

    internal static UpdateCheckResult Evaluate(byte[] json, string currentVersion)
    {
        if (Parse(json) is not { } release) return new UpdateCheckResult(UpdateCheckStatus.Failed);
        return AppVersion.IsNewer(release.Version, currentVersion)
            ? new UpdateCheckResult(UpdateCheckStatus.Available, release)
            : new UpdateCheckResult(UpdateCheckStatus.UpToDate);
    }

    /// <summary>Reads tag_name and html_url. Null when the body is not a published, readable release.</summary>
    internal static ReleaseInfo? Parse(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
            if (!root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String) return null;
            var version = tag.GetString();
            if (!AppVersion.IsValid(version)) return null;
            var url = root.TryGetProperty("html_url", out var html) && html.ValueKind == JsonValueKind.String ? html.GetString() : null;
            return new ReleaseInfo(AppVersion.Display(version!), SafeUrl(url));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The link opens in the browser, so only this repository's own pages are accepted.</summary>
    static string SafeUrl(string? url) =>
        url != null && url.StartsWith(RepositoryUrl + "/", StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out _) ? url : ReleasesUrl;
}
