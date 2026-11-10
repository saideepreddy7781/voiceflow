using System.Globalization;
using System.Net;

namespace VoiceFlow.Core;

/// <summary>One shared HttpClient for every provider call, plus Groq's status handling.</summary>
public static class GroqHttp
{
    /// <summary>
    /// Shared client. Timeouts are per request (a linked CancellationTokenSource), so the client itself
    /// has none. Connections are recycled every few minutes so DNS changes are picked up.
    /// </summary>
    public static HttpClient Shared { get; } = CreateClient();

    static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VoiceFlow-Windows/0.1.0");
        return client;
    }

    /// <summary>Sends a request with a timeout and maps failures to <see cref="ProviderException"/>.</summary>
    public static async Task<byte[]> SendAsync(HttpClient client, Func<HttpRequestMessage> build, TimeSpan timeout, CancellationToken cancel) =>
        (await SendDetailedAsync(client, build, timeout, cancel).ConfigureAwait(false)).Data;

    /// <summary>The same, with the response headers, which carry Groq's rate limits.</summary>
    public static async Task<GroqResponse> SendDetailedAsync(HttpClient client, Func<HttpRequestMessage> build, TimeSpan timeout,
                                                             CancellationToken cancel)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(timeout);
        HttpResponseMessage response;
        using var request = build();
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw ProviderException.Network("the request timed out");
        }
        catch (HttpRequestException e)
        {
            throw ProviderException.Network(e.Message);
        }
        using (response)
        {
            byte[] data;
            try
            {
                data = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw ProviderException.Network("the response timed out");
            }
            catch (HttpRequestException e)
            {
                throw ProviderException.Network(e.Message);
            }
            switch ((int)response.StatusCode)
            {
                case 200:
                    return new GroqResponse(data, Headers(response));
                case 429:
                    if (GroqLimitMessage.DailyResource(System.Text.Encoding.UTF8.GetString(data)) is { } resource)
                        throw ProviderException.DailyLimit(resource, RetryAfter(response));
                    throw ProviderException.RateLimited(RetryAfter(response));
                default:
                    throw ProviderException.Http((int)response.StatusCode, System.Text.Encoding.UTF8.GetString(data));
            }
        }
    }

    static Dictionary<string, string> Headers(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers) headers[name] = string.Join(",", values);
        return headers;
    }

    static double? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("retry-after", out var values))
        {
            var raw = values.FirstOrDefault();
            if (raw != null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return seconds;
        }
        return response.Headers.RetryAfter?.Delta?.TotalSeconds;
    }
}

/// <summary>A successful Groq response.</summary>
public sealed record GroqResponse(byte[] Data, IReadOnlyDictionary<string, string> Headers);

/// <summary>Reads Groq's rate-limit error text, e.g. "...on tokens per day (TPD): Limit 200000, Used 199000...".</summary>
internal static class GroqLimitMessage
{
    internal static string? DailyResource(string body)
    {
        var b = body.ToLowerInvariant();
        if (!b.Contains("per day") && !b.Contains("(tpd)") && !b.Contains("(rpd)") && !b.Contains("(aspd)")) return null;
        if (b.Contains("seconds of audio") || b.Contains("aspd")) return "transcription";
        if (b.Contains("requests per day") || b.Contains("rpd")) return "requests";
        return "tokens";
    }
}
